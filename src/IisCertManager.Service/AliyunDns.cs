using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DnsClient;
using IisCertManager.Contracts;
namespace IisCertManager.Service;
public sealed record DnsLease(string RecordId, string Name, string Value);
public interface IDnsProbe
{
    Task<bool> HasCname(string name, CancellationToken ct);
    Task<bool> HasTxt(string name, string value, CancellationToken ct);
}
public sealed class PublicDnsProbe : IDnsProbe
{
    readonly LookupClient[] resolvers = new[] { "1.1.1.1", "8.8.8.8" }.Select(ip => new LookupClient(
        new LookupClientOptions(IPAddress.Parse(ip)) { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 })).ToArray();
    public async Task<bool> HasCname(string name, CancellationToken ct)
    {
        foreach (var resolver in resolvers)
            if ((await resolver.QueryAsync(name, QueryType.CNAME, cancellationToken: ct)).Answers.CnameRecords().Any()) return true;
        return false;
    }
    public async Task<bool> HasTxt(string name, string value, CancellationToken ct)
    {
        foreach (var resolver in resolvers)
        {
            try
            {
                var answer = await resolver.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
                if (!answer.Answers.TxtRecords().Any(x => string.Concat(x.Text) == value)) return false;
            }
            catch (DnsResponseException) { return false; }
        }
        return true;
    }
}
public sealed class AliyunDns(StateStore store, Settings settings, HttpClient? httpClient = null, IDnsProbe? dnsProbe = null) : IDisposable
{
    readonly HttpClient http = httpClient ?? new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly IDnsProbe probe = dnsProbe ?? new PublicDnsProbe();
    string Journal => Path.Combine(store.Root, "dns-cleanup.json");
    List<DnsLease> Leases() => File.Exists(Journal)
        ? JsonSerializer.Deserialize<List<DnsLease>>(File.ReadAllText(Journal), Wire.Json) ?? [] : [];
    void SaveLeases(List<DnsLease> leases)
    {
        File.WriteAllText(Journal + ".tmp", JsonSerializer.Serialize(leases, Wire.Json));
        File.Move(Journal + ".tmp", Journal, true);
    }
    async Task<JsonElement> Call(string action, Dictionary<string,string> args, CancellationToken ct)
    {
        args["Action"] = action; args["Format"] = "JSON"; args["Version"] = "2015-01-09";
        args["AccessKeyId"] = settings.AccessKeyId; args["SignatureMethod"] = "HMAC-SHA1";
        args["SignatureVersion"] = "1.0"; args["SignatureNonce"] = Guid.NewGuid().ToString("N");
        args["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        args["Signature"] = Policy.Sign(args, settings.AccessKeySecret);
        using var response = await http.PostAsync("https://alidns.aliyuncs.com/", new FormUrlEncodedContent(args), ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!response.IsSuccessStatusCode || root.TryGetProperty("Code", out _))
        {
            var code = root.TryGetProperty("Code", out var c) ? c.GetString() : response.StatusCode.ToString();
            throw new InvalidOperationException("阿里云 DNS 请求失败，错误码：" + code);
        }
        return root.Clone();
    }
    public async Task<DnsLease> Create(string name, string value, CancellationToken ct)
    {
        var rr = Policy.RelativeRecord(name, settings.AliyunZone);
        // 不自动跟随 CNAME，避免修改错误 DNS 区域。
        if (await probe.HasCname(name, ct))
            throw new InvalidOperationException("_acme-challenge 存在 CNAME 委派，首版请移除委派或使用直接 TXT 记录。");
        var result = await Call("AddDomainRecord", new() { ["DomainName"] = settings.AliyunZone,
            ["RR"] = rr, ["Type"] = "TXT", ["Value"] = value, ["TTL"] = "600" }, ct);
        var lease = new DnsLease(result.GetProperty("RecordId").GetString()!, name, value);
        try
        {
            var leases = Leases(); leases.Add(lease); SaveLeases(leases);
        }
        catch
        {
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await Call("DeleteDomainRecord", new() { ["RecordId"] = lease.RecordId }, rollback.Token); }
            catch { store.Log($"严重：DNS 记录需人工清理，{lease.Name}，RecordId={lease.RecordId}。"); }
            throw;
        }
        return lease;
    }
    public async Task Cleanup(CancellationToken ct)
    {
        foreach (var lease in Leases())
        {
            try
            {
                // ID 精确删除本程序创建的 TXT，保留相同 RR 下的其他值。
                await Call("DeleteDomainRecord", new() { ["RecordId"] = lease.RecordId }, ct);
                var remaining = Leases(); remaining.RemoveAll(x => x.RecordId == lease.RecordId); SaveLeases(remaining);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (e.Message.EndsWith("InvalidRecordId.NotFound", StringComparison.Ordinal))
                { var remaining = Leases(); remaining.RemoveAll(x => x.RecordId == lease.RecordId); SaveLeases(remaining); }
                else store.Log($"DNS 清理待重试：{lease.Name}，RecordId={lease.RecordId}；{e.Message}");
            }
        }
    }
    public async Task WaitPropagation(DnsLease lease, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await probe.HasTxt(lease.Name, lease.Value, ct)) return;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        throw new TimeoutException("DNS TXT 尚未传播至两个公共解析器；保留现有 HTTPS 证书，请检查 DNS/防火墙后重试。");
    }
    public void Dispose() { if (httpClient == null) http.Dispose(); }
}
