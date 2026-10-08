using IisCertManager.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
void Reject(Action action, string name)
{ try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception("FAIL " + name); }
Check(Policy.Resolve(ChallengeMode.Auto, false, ["a.example.com"]) == ChallengeMode.Http01, "auto falls back to HTTP");
Check(Policy.Resolve(ChallengeMode.Auto, true, ["a.example.com"]) == ChallengeMode.Dns01, "configured auto uses DNS");
Check(Policy.Resolve(ChallengeMode.Http01, true, ["a.example.com"]) == ChallengeMode.Http01, "explicit HTTP respected");
Reject(() => Policy.Resolve(ChallengeMode.Dns01, false, ["a.example.com"]), "missing DNS credentials rejected");
Reject(() => Policy.Resolve(ChallengeMode.Auto, false, ["*.example.com"]), "wildcard never silently falls back");
Reject(() => Policy.Resolve((ChallengeMode)999, false, ["a.example.com"]), "unknown mode rejected");
Check(Policy.Domain("WWW.Example.COM.") == "www.example.com", "domain canonicalized");
Check(Policy.Domain("*.example.com", true) == "*.example.com", "wildcard accepted as SAN");
Reject(() => Policy.Domain("*.example.com"), "wildcard IIS hostname rejected");
foreach (var invalid in new[] { "localhost", "127.0.0.1", "a..example.com", "https://a.example.com", "-a.example.com", "a/b.example.com", "a_.example.com" })
    Reject(() => Policy.Domain(invalid), "invalid name " + invalid);
Check(Policy.Covers("*.example.com", "a.example.com"), "wildcard covers single level");
Check(!Policy.Covers("*.example.com", "a.b.example.com"), "wildcard does not cover nested host");
Check(!Policy.Covers("*.example.com", "example.com"), "wildcard does not cover apex");
Check(Policy.RelativeRecord("_acme-challenge.a.example.com", "example.com") == "_acme-challenge.a", "TXT relative RR");
Reject(() => Policy.RelativeRecord("_acme-challenge.notexample.com", "example.com"), "zone suffix boundary");
Check(Policy.Encode("a +/*~") == "a%20%2B%2F%2A~", "Aliyun RFC3986 encoding");
var parameters = new Dictionary<string,string> { ["Z"] = "a b", ["A"] = "+" };
var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("secret&"), Encoding.UTF8.GetBytes("POST&%2F&A%3D%252B%26Z%3Da%2520b")));
Check(Policy.Sign(parameters, "secret") == signature, "Aliyun POST signature canonical string");
var now = DateTimeOffset.UtcNow;
var s = new Settings { Staging = false, RenewBeforeDays = 30 };
var p = new Profile { Enabled = true, Expires = now.AddDays(20) };
Check(Policy.Due(p, s, now), "expiring certificate due");
Check(!Policy.Due(p with { Enabled = false }, s, now), "disabled rule never renewed");
Check(!Policy.Due(p, s with { Staging = true }, now), "staging never automatically deployed");
Check(!Policy.Due(p with { NextAttempt = now.AddHours(1) }, s, now), "retry backoff honored");
Check(!Policy.Due(p with { Expires = now.AddDays(60) }, s, now), "fresh certificate not reissued");
Check(Policy.Due(p with { Expires = null }, s, now), "unissued managed rule due");
var req = new Request("profile", Profile: p);
Check(JsonSerializer.Deserialize<Request>(JsonSerializer.Serialize(req, Wire.Json), Wire.Json)?.Profile?.Id == p.Id, "IPC JSON roundtrip");
Check(Policy.RetryDelay(1) == TimeSpan.FromMinutes(15), "first failure retries after 15 minutes");
Check(Policy.RetryDelay(2) == TimeSpan.FromMinutes(30), "second failure retries after 30 minutes");
Check(Policy.RetryDelay(8) == TimeSpan.FromHours(24), "retry delay capped at 24 hours");
Check(Policy.RetryDelay(int.MaxValue) == TimeSpan.FromHours(24), "large failure count cannot overflow backoff");
Check(Policy.Due(p with { Expires = now.AddDays(30), NextAttempt = now }, s, now), "renewal starts exactly at configured boundary");
Check(!Policy.Due(p with { Expires = now.AddDays(30).AddTicks(1) }, s, now), "certificate just outside window is not due");
Check(Policy.Domain("bücher.example") == "xn--bcher-kva.example", "international domain normalized");
Reject(() => Policy.Domain(" "), "empty host rejected");
using (var key = RSA.Create(2048))
{
    var csr = new CertificateRequest("CN=www.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("www.example.com"); san.AddDnsName("*.example.com");
    csr.CertificateExtensions.Add(san.Build());
    csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
    using var cert = csr.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));
    CertificatePolicy.Validate(cert, ["www.example.com", "*.example.com"], now);
    Check(true, "valid server certificate and all SAN accepted");
    void RejectCert(X509Certificate2 candidate, string[] domains, DateTimeOffset at, string label)
    {
        try { CertificatePolicy.Validate(candidate, domains, at); }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("FAIL " + label);
    }
    RejectCert(cert, ["other.example.com"], now, "missing SAN blocks deployment");
    RejectCert(cert, ["www.example.com"], now.AddDays(31), "expired certificate blocks deployment");
    RejectCert(cert, ["www.example.com"], now.AddMinutes(-2), "future certificate blocks deployment");
    var client = new CertificateRequest("CN=www.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    client.CertificateExtensions.Add(san.Build());
    client.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));
    using var clientCert = client.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));
    RejectCert(clientCert, ["www.example.com"], now, "client-only EKU blocks server deployment");
    var noSan = new CertificateRequest("CN=www.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var noSanCert = noSan.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));
    RejectCert(noSanCert, ["www.example.com"], now, "CN alone cannot replace a SAN");
}
Console.WriteLine($"{count} checks passed.");
