using IisCertManager.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
Console.WriteLine($"{count} checks passed.");
