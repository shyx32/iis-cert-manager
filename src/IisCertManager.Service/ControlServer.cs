using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IisCertManager.Contracts;
namespace IisCertManager.Service;
public sealed class ControlServer(StateStore store, IisManager iis, CertificateManager certificates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Log("Windows 服务已启动。");
        while (!stoppingToken.IsCancellationRequested)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            // 拒绝网络登录，只允许本机提升权限的管理员及 SYSTEM。
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(Wire.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                inputTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                // 有界帧：一个 UTF-8 JSON 请求，以换行终止。
                var bytes = new List<byte>(); var buffer = new byte[1];
                while (true)
                {
                    if (await pipe.ReadAsync(buffer, inputTimeout.Token) == 0) throw new IOException("客户端已断开。");
                    if (buffer[0] == 10) break;
                    if (bytes.Count >= 65536) throw new InvalidDataException("请求过大。");
                    bytes.Add(buffer[0]);
                }
                var request = JsonSerializer.Deserialize<Request>(Encoding.UTF8.GetString(bytes.ToArray()), Wire.Json)
                    ?? throw new InvalidDataException("请求无效。");
                Response response;
                if (!await store.Gate.WaitAsync(0, stoppingToken)) response = new(false, "后台正在签发或续期，请稍后刷新。");
                else
                {
                    try { response = await Handle(request, stoppingToken); }
                    catch (Exception e) { response = new(false, e.Message); }
                    finally { store.Gate.Release(); }
                }
                using var outputTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, Wire.Json) + "\n"), outputTimeout.Token);
                await pipe.FlushAsync(outputTimeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException)
            { store.Log("控制连接已关闭：" + e.GetType().Name); }
        }
    }
    Snapshot Snapshot() => new(store.PublicSettings(), iis.Read(), store.Data.Profiles, store.Logs());
    async Task<Response> Handle(Request request, CancellationToken ct)
    {
        switch (request.Action)
        {
            case "snapshot": break;
            case "settings": store.UpdateSettings(request.Settings ?? throw new ArgumentException("缺少配置。")); break;
            case "profile":
            {
                var input = request.Profile ?? throw new ArgumentException("缺少站点配置。");
                certificates.Validate(input);
                var old = store.Data.Profiles.FirstOrDefault(x => x.Id == input.Id);
                if (store.Data.Profiles.Any(x => x.Id != input.Id && x.Host == input.Host && x.Port == input.Port))
                    throw new ArgumentException("相同主机名/端口已有托管规则，请编辑该规则。");
                var changed = old == null || !old.Domains.SequenceEqual(input.Domains) || old.Mode != input.Mode ||
                    old.SiteId != input.SiteId || old.Ip != input.Ip || old.Port != input.Port || old.Host != input.Host;
                var p = input with { Thumbprint = changed ? null : old?.Thumbprint, Expires = changed ? null : old?.Expires,
                    Failures = changed ? 0 : old!.Failures, LastAttempt = old?.LastAttempt,
                    NextAttempt = changed ? null : old?.NextAttempt, Status = changed ? "等待签发" : old!.Status };
                if (old != null) store.Data.Profiles.Remove(old);
                store.Data.Profiles.Add(p);
                try { store.Save(); } catch { store.Data.Profiles.Remove(p); if (old != null) store.Data.Profiles.Add(old); throw; }
                store.Log($"{p.Host}：托管规则已保存，自动续期 {(p.Enabled ? "开启" : "关闭")}。");
                break;
            }
            case "enabled":
            {
                var p = store.Data.Profiles.Single(x => x.Id == request.Id);
                var old = p.Enabled;
                var enabled = request.Enabled ?? throw new ArgumentException("缺少启停状态。");
                if (enabled) certificates.Validate(p);
                p.Enabled = enabled;
                try { store.Save(); } catch { p.Enabled = old; throw; }
                store.Log($"{p.Host}：自动续期已{(enabled ? "启用" : "暂停")}。");
                break;
            }
            case "delete":
            {
                var p = store.Data.Profiles.Single(x => x.Id == request.Id);
                store.Data.Profiles.Remove(p);
                try { store.Save(); } catch { store.Data.Profiles.Add(p); throw; }
                store.Log($"{p.Host}：已移除托管规则，IIS 绑定和证书保留。"); break;
            }
            case "issue": await certificates.Issue(store.Data.Profiles.Single(x => x.Id == request.Id), ct); break;
            default: throw new ArgumentException("未知请求。");
        }
        return new(true, "操作完成。", Snapshot());
    }
}
public sealed class RenewalWorker(StateStore store, CertificateManager certificates, IisManager iis) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await store.Gate.WaitAsync(0, stoppingToken))
            {
                try
                {
                    using var cleanup = new AliyunDns(store, store.Data.Settings);
                    if (StateStore.DnsConfigured(store.Data.Settings)) await cleanup.Cleanup(stoppingToken);
                    if (!store.Data.Settings.Staging)
                    {
                        var bindings = iis.Read();
                        var changed = false;
                        foreach (var managed in store.Data.Profiles.Where(x => x.Enabled && x.Thumbprint != null))
                        {
                            var current = bindings.FirstOrDefault(x => x.SiteId == managed.SiteId && x.Protocol == "https" &&
                                x.Host.Equals(managed.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == managed.Ip && x.Port == managed.Port);
                            if (current?.Thumbprint != managed.Thumbprint)
                            {
                                managed.Expires = null; managed.Thumbprint = null;
                                managed.Status = "受管绑定已变更，等待重签发"; changed = true;
                            }
                            else if (current!.Expires != managed.Expires)
                            { managed.Expires = current.Expires; changed = true; }
                        }
                        if (changed) store.Save();
                    }
                    foreach (var p in store.Data.Profiles.Where(x => Policy.Due(x, store.Data.Settings, DateTimeOffset.UtcNow)).ToArray())
                    {
                        try { await certificates.Issue(p, stoppingToken); }
                        catch (Exception e) { store.Log($"自动续期未完成：{p.Host}，{e.Message}"); }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { store.Log("续期扫描失败：" + e.Message); }
                finally { store.Gate.Release(); }
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
