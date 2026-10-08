using System.Security.Cryptography.X509Certificates;
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
            if (thumb != null)
            {
                try
                {
                    using var certStore = new X509Store(binding.CertificateStoreName ?? "My", StoreLocation.LocalMachine);
                    certStore.Open(OpenFlags.ReadOnly);
                    using var cert = certStore.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).FirstOrDefault();
                    expires = cert?.NotAfter;
                }
                catch (System.Security.Cryptography.CryptographicException) { }
            }
            rows.Add(new(site.Id, site.Name, site.State.ToString(),
                Environment.ExpandEnvironmentVariables(site.Applications["/"].VirtualDirectories["/"].PhysicalPath),
                binding.Protocol, binding.EndPoint.Address.ToString() is "0.0.0.0" ? "*" : binding.EndPoint.Address.ToString(),
                binding.EndPoint.Port, binding.Host, binding.BindingInformation, thumb, expires,
                binding.Protocol == "https" ? (int)binding.SslFlags : 0));
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
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("证书没有私钥或已到期。");
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
                Existed = existing != null, Thumbprint = previousHash == null ? null : Convert.ToHexString(previousHash),
                CertificateStore = previousStore, SslFlags = previousFlags }, Wire.Json));
        using var certStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        certStore.Open(OpenFlags.ReadWrite);
        certStore.Add(cert);
        try
        {
            var binding = existing ?? site.Bindings.Add(information, cert.GetCertHash(), "My");
            binding.SslFlags = previousFlags ?? SslFlags.Sni;
            binding.CertificateHash = cert.GetCertHash();
            binding.CertificateStoreName = "My";
            manager.CommitChanges();
            var readback = Read().Single(x => x.SiteId == p.SiteId && x.Protocol == "https" &&
                x.BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase));
            if (readback.Thumbprint != cert.Thumbprint) throw new InvalidOperationException("IIS 证书绑定回读不一致。");
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
                    else { binding.CertificateHash = previousHash; binding.CertificateStoreName = previousStore; binding.SslFlags = previousFlags!.Value; }
                    rollback.CommitChanges();
                }
            }
            catch { store.Log("严重：绑定自动回滚失败，请用 bindings 目录的快照恢复。"); }
            throw new InvalidOperationException("部署失败，已尝试恢复原绑定；旧证书保留。" + error.Message, error);
        }
        p.Thumbprint = cert.Thumbprint;
        p.Expires = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
    }
}
