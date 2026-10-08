using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace IisCertManager.Contracts;
public static class CertificatePolicy
{
    public static void Validate(X509Certificate2 cert, IEnumerable<string> domains, DateTimeOffset now)
    {
        if (cert.NotAfter.ToUniversalTime() <= now.UtcDateTime || cert.NotBefore.ToUniversalTime() > now.UtcDateTime)
            throw new InvalidOperationException("证书尚未生效或已到期。");
        var san = cert.Extensions["2.5.29.17"];
        var names = san == null ? new HashSet<string>(StringComparer.Ordinal) :
            new X509SubjectAlternativeNameExtension(san.RawData, san.Critical).EnumerateDnsNames()
                .Select(x => Policy.Domain(x, true)).ToHashSet(StringComparer.Ordinal);
        if (domains.Any(x => !names.Contains(x)))
            throw new InvalidOperationException("返回证书的 SAN 未包含全部申请域名，拒绝部署。");
        var usage = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usage != null && !usage.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"))
            throw new InvalidOperationException("证书不允许用于 TLS 服务端。");
    }
}
