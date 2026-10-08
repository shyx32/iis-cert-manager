using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using IisCertManager.Contracts;
using Microsoft.Web.Administration;
namespace IisCertManager.Service;
public sealed class IisManager(StateStore store)
{
    public List<SiteBinding> Read()
    {
        using var manager = new ServerManager();
        var rows = new List<SiteBinding>();
        foreach (var site in manager.Sites)
        foreach (var binding in site.Bindings.Where(x => x.Protocol is "http" or "https"))
        {
            var hash = binding.Protocol == "https" ? binding.CertificateHash : null;
            var thumb = hash is { Length: > 0 } ? Convert.ToHexString(hash) : null;
            DateTimeOffset? expires = null;
            string? subject = null, issuer = null;
            if (thumb != null)
            {
                try
                {
                    using var certStore = new X509Store(binding.CertificateStoreName ?? "My", StoreLocation.LocalMachine);
                    certStore.Open(OpenFlags.ReadOnly);
                    using var cert = certStore.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).FirstOrDefault();
                    expires = cert?.NotAfter;
                    subject = cert?.GetNameInfo(X509NameType.SimpleName, false);
                    issuer = cert?.GetNameInfo(X509NameType.SimpleName, true);
                }
                catch (System.Security.Cryptography.CryptographicException) { }
            }
            rows.Add(new(site.Id, site.Name, site.State.ToString(),
                Environment.ExpandEnvironmentVariables(site.Applications["/"].VirtualDirectories["/"].PhysicalPath),
                binding.Protocol, binding.EndPoint.Address.ToString() is "0.0.0.0" ? "*" : binding.EndPoint.Address.ToString(),
                binding.EndPoint.Port, binding.Host, binding.BindingInformation, thumb, expires,
                binding.Protocol == "https" ? (int)binding.SslFlags : 0, subject, issuer,
                binding.Protocol == "https" ? binding.CertificateStoreName : null, site.Applications["/"].ApplicationPoolName));
        }
        return rows;
    }
    public void Validate(Profile p)
    {
        p.Host = Policy.Domain(p.Host);
        p.Domains = p.Domains.Select(x => Policy.Domain(x, true)).Distinct(StringComparer.Ordinal).ToArray();
        if (p.Domains.Length is < 1 or > 100 || !p.Domains.Any(x => Policy.Covers(x, p.Host)))
            throw new ArgumentException("证书域名须包含所选 IIS 主机名（或覆盖它的泛域名），数量为 1–100。");
        if (p.Port is < 1 or > 65535) throw new ArgumentException("HTTPS 端口无效。");
        var rows = Read();
        var source = rows.FirstOrDefault(x => x.SiteId == p.SiteId && x.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == p.Ip)
            ?? throw new ArgumentException("IIS 站点/主机名/IP 已变更，请重新选择现有绑定。");
        p.SiteName = source.SiteName;
        foreach (var row in rows.Where(x => x.Protocol == "https" && x.Port == p.Port))
        {
            if (row.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase))
            {
                if (row.SiteId != p.SiteId || row.Ip != p.Ip)
                    throw new InvalidOperationException("相同 SNI 主机名/端口被其他绑定使用，拒绝覆盖共享证书。");
                if ((row.SslFlags & 1) == 0 || (row.SslFlags & 2) != 0)
                    throw new InvalidOperationException("首版只更新普通 SNI 绑定；请先在 IIS 中处理非 SNI 或集中证书存储绑定。");
            }
        }
    }
    static string BindingInfo(Profile p) => $"{(p.Ip.Contains(':') ? "[" + p.Ip + "]" : p.Ip)}:{p.Port}:{p.Host}";
    public void Install(Profile p, byte[] pfx, string password)
    {
        Validate(p); // 再次读取，防止签发期间绑定被修改。
        X509Certificate2Collection? bundle = null;
        var stage = "导入 PFX";
        try
        {
        store.Log($"{p.Host}：开始部署，站点 {p.SiteId}，HTTPS 端口 {p.Port}。");
        bundle = X509CertificateLoader.LoadPkcs12Collection(pfx, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        var cert = bundle.Cast<X509Certificate2>().Single(x => x.HasPrivateKey);
        stage = "校验证书";
        CertificatePolicy.Validate(cert, p.Domains, DateTimeOffset.UtcNow);
        store.Log($"{p.Host}：证书 {cert.Thumbprint}；{DiagnosticReport.KeyInfo(cert)}。");
        using var manager = new ServerManager();
        var site = manager.Sites.First(x => x.Id == p.SiteId);
        var information = BindingInfo(p);
        var existing = site.Bindings.FirstOrDefault(x => x.Protocol == "https" && x.BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase));
        var previousHash = existing?.CertificateHash;
        var previousStore = existing?.CertificateStoreName;
        var previousFlags = existing?.SslFlags;
        var backup = Path.Combine(store.Root, "bindings"); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{p.Id}.json"),
            JsonSerializer.Serialize(new { p.SiteId, p.SiteName, BindingInformation = information,
                InstalledThumbprint = cert.Thumbprint, Existed = existing != null, Thumbprint = previousHash == null ? null : Convert.ToHexString(previousHash),
                CertificateStore = previousStore, SslFlags = previousFlags }, Wire.Json));
        using var certStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        certStore.Open(OpenFlags.ReadWrite);
        // 将中间证书装入 CA 库，避免首个 TLS 握手依赖在线 AIA 下载。绝不新增根信任。
        using (var intermediates = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine))
        {
            intermediates.Open(OpenFlags.ReadWrite);
            foreach (var issuer in bundle.Cast<X509Certificate2>().Where(x => !x.HasPrivateKey &&
                !x.SubjectName.RawData.SequenceEqual(x.IssuerName.RawData))) intermediates.Add(issuer);
        }
        stage = "写入机器证书存储";
        certStore.Add(cert);
        using (var persisted = certStore.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Single())
        {
            stage = "回读机器私钥";
            store.Log($"{p.Host}：机器证书存储回读；{DiagnosticReport.KeyInfo(persisted)}。");
            using var privateKey = persisted.GetRSAPrivateKey();
            using var publicKey = persisted.GetRSAPublicKey();
            if (privateKey == null || publicKey == null)
                throw new InvalidOperationException("机器证书存储中无法读取 RSA 私钥，未修改 IIS 绑定。请导出诊断日志。");
            var probe = RandomNumberGenerator.GetBytes(32);
            var signature = privateKey.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!publicKey.VerifyData(probe, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("机器证书私钥核验失败，未修改 IIS 绑定。");
        }
        try
        {
            stage = "提交 IIS HTTPS 绑定";
            // An existing certificate-less SNI binding does not persist a hash-only update
            // through MWA on IIS 2016. Recreate that exact binding with the SSL-aware Add
            // overload; supplying SNI at creation also avoids transient IP-based SSL entries.
            var emptyBinding = existing != null && previousHash is not { Length: > 0 };
            if (emptyBinding) site.Bindings.Remove(existing!);
            var binding = existing != null && !emptyBinding ? existing :
                site.Bindings.Add(information, cert.GetCertHash(), "My", previousFlags ?? SslFlags.Sni);
            binding.SslFlags = previousFlags ?? SslFlags.Sni;
            binding.CertificateHash = cert.GetCertHash();
            binding.CertificateStoreName = "My";
            manager.CommitChanges();
            stage = "回读 IIS HTTPS 绑定";
            var readback = Read().Single(x => x.SiteId == p.SiteId && x.Protocol == "https" &&
                x.BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase));
            if (readback.Thumbprint != cert.Thumbprint)
                throw new InvalidOperationException($"IIS 证书绑定回读不一致：{information}，预期 {cert.Thumbprint}，实际 {readback.Thumbprint ?? "无证书"}，证书库 {readback.CertificateStore ?? "未设置"}，SslFlags={readback.SslFlags}。");
        }
        catch (Exception error)
        {
            try
            {
                using var rollback = new ServerManager();
                var rollbackSite = rollback.Sites.First(x => x.Id == p.SiteId);
                var binding = rollbackSite.Bindings.FirstOrDefault(x => x.Protocol == "https" && x.BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase));
                // 不覆盖管理员在此期间写入的另一张证书。
                if (binding != null && Convert.ToHexString(binding.CertificateHash ?? []) == cert.Thumbprint)
                {
                    if (existing == null) rollbackSite.Bindings.Remove(binding);
                    else if (previousHash is not { Length: > 0 })
                    {
                        rollbackSite.Bindings.Remove(binding);
                        var restored = rollbackSite.Bindings.Add(information, "https");
                        restored.SslFlags = previousFlags!.Value;
                    }
                    else { binding.CertificateHash = previousHash; binding.CertificateStoreName = previousStore; binding.SslFlags = previousFlags!.Value; }
                    rollback.CommitChanges();
                }
            }
            catch (Exception rollbackError) { store.LogError("绑定自动回滚", rollbackError); store.Log("严重：绑定自动回滚失败，请用 bindings 目录的快照恢复。"); }
            throw new InvalidOperationException($"部署失败，已尝试恢复原绑定；旧证书保留。错误码 0x{error.HResult:X8}：{error.Message} 请在“操作日志”导出诊断日志。", error);
        }
        p.Thumbprint = cert.Thumbprint;
        p.Expires = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        store.Log($"{p.Host}：IIS 部署及指纹回读通过，证书 {cert.Thumbprint}。");
        }
        catch (Exception error) { store.LogError($"{p.Host}：{stage}", error); throw; }
        finally { if (bundle != null) foreach (var certificate in bundle) certificate.Dispose(); }
    }
}
