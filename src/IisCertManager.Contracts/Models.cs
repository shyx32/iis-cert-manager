using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace IisCertManager.Contracts;
public static class Wire
{
    public const string PipeName = "IisCertManager.v1";
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
}
public enum ChallengeMode { Auto, Http01, Dns01 }
public sealed record Settings
{
    public string Email { get; set; } = "";
    public bool AcceptTerms { get; set; }
    public bool Staging { get; set; } = true;
    public int RenewBeforeDays { get; set; } = 30;
    public string AliyunZone { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";
    public bool HasDnsCredentials { get; set; }
    public bool ClearDnsCredentials { get; set; }
}
public sealed record SiteBinding(long SiteId, string SiteName, string State, string Root, string Protocol,
    string Ip, int Port, string Host, string BindingInformation, string? Thumbprint, DateTimeOffset? Expires, int SslFlags);
public sealed record Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SiteId { get; set; }
    public string SiteName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Ip { get; set; } = "*";
    public int Port { get; set; } = 443;
    public string[] Domains { get; set; } = [];
    public ChallengeMode Mode { get; set; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "尚未签发";
    public string? Thumbprint { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    public int Failures { get; set; }
}
public sealed record Snapshot(Settings Settings, List<SiteBinding> Bindings, List<Profile> Profiles, string[] Logs);
public sealed record Request(string Action, Settings? Settings = null, Profile? Profile = null, Guid? Id = null, bool? Enabled = null);
public sealed record Response(bool Ok, string Message, Snapshot? Snapshot = null);
public static class Policy
{
    public static string Domain(string value, bool wildcard = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim().TrimEnd('.').ToLowerInvariant();
        var star = wildcard && value.StartsWith("*.");
        if (star) value = value[2..];
        value = new IdnMapping().GetAscii(value);
        if (IPAddress.TryParse(value, out _) || !value.Contains('.') || value.Length > 253 ||
            value.Split('.').Any(x => x.Length is < 1 or > 63 || x[0] == '-' || x[^1] == '-' ||
                x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new ArgumentException("请输入有效公网域名，不支持 IP、localhost 或空主机名。");
        return star ? "*." + value : value;
    }
    public static bool Covers(string identifier, string host) => identifier == host ||
        (identifier.StartsWith("*.") && host.EndsWith(identifier[1..], StringComparison.Ordinal) &&
         host.Split('.').Length == identifier.Split('.').Length);
    public static ChallengeMode Resolve(ChallengeMode mode, bool configured, IEnumerable<string> domains)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("未知验证方式。");
        var chosen = mode == ChallengeMode.Auto ? (configured ? ChallengeMode.Dns01 : ChallengeMode.Http01) : mode;
        if (chosen == ChallengeMode.Dns01 && !configured) throw new ArgumentException("DNS-01 需要完整的阿里云 DNS 配置。");
        if (chosen == ChallengeMode.Http01 && domains.Any(x => x.StartsWith("*.")))
            throw new ArgumentException("泛域名证书必须配置 DNS-01，不能回退 HTTP-01。");
        return chosen;
    }
    public static string RelativeRecord(string fqdn, string zone)
    {
        if (!fqdn.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("证书域名不属于配置的阿里云 DNS 主域。");
        return fqdn[..^(zone.Length + 1)];
    }
    public static string Encode(string value) => Uri.EscapeDataString(value);
    public static string Sign(IDictionary<string,string> parameters, string secret)
    {
        var canonical = string.Join("&", parameters.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Encode(x.Key) + "=" + Encode(x.Value)));
        var input = "POST&%2F&" + Encode(canonical);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret + "&"), Encoding.UTF8.GetBytes(input)));
    }
    public static TimeSpan RetryDelay(int failures) => TimeSpan.FromMinutes(Math.Min(1440, 15 * Math.Pow(2, Math.Clamp(failures - 1, 0, 7))));
    public static bool Due(Profile p, Settings s, DateTimeOffset now) => p.Enabled && !s.Staging &&
        (p.NextAttempt == null || p.NextAttempt <= now) &&
        (p.Expires == null || p.Expires <= now.AddDays(s.RenewBeforeDays));
}
