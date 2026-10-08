using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Windows;
using System.Text.Json;
using System.Text;
using IisCertManager.Contracts;
using IisCertManager.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.Administration;

internal static class Program
{
    static int checks;
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); checks++; }
    static void Reject(Action action, string label)
    { try { action(); } catch (ArgumentException) { Check(true, label); return; } throw new Exception("FAIL " + label); }
    [STAThread]
    static void Main()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "IisCertManager.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            StateChecks(scratch);
            DnsChecks(scratch).GetAwaiter().GetResult();
            IisChecks(scratch).GetAwaiter().GetResult();
            var application = new IisCertManager.Client.App();
            application.InitializeComponent();
            var window = new IisCertManager.Client.MainWindow();
            window.Measure(new Size(1160, 820)); window.Arrange(new Rect(0, 0, 1160, 820)); window.UpdateLayout();
            Check(window.Title == "IIS 证书管家", "WPF application resources and window instantiate");
            window.Close();
            Console.WriteLine($"{checks} Windows checks passed.");
        }
        finally { Directory.Delete(scratch, true); }
    }
    static StateStore Open(string path) => new(NullLogger<StateStore>.Instance, path);
    static void StateChecks(string scratch)
    {
        var path = Path.Combine(scratch, "state");
        var store = Open(path);
        store.UpdateSettings(new Settings { Email = "test@example.com", AcceptTerms = true,
            AliyunZone = "example.com", AccessKeyId = "fixture-id", AccessKeySecret = "fixture-secret" });
        var publicSettings = store.PublicSettings();
        Check(publicSettings.HasDnsCredentials && publicSettings.AccessKeyId == "" && publicSettings.AccessKeySecret == "", "DNS keys redacted");
        var bytes = File.ReadAllBytes(Path.Combine(path, "state.dpapi"));
        Check(!System.Text.Encoding.UTF8.GetString(bytes).Contains("fixture-secret"), "state encrypted on disk");
        Check(Open(path).Data.Settings.AccessKeySecret == "fixture-secret", "DPAPI state survives reopen");
        var acl = new DirectoryInfo(path).GetAccessControl();
        Check(acl.AreAccessRulesProtected, "state directory inheritance disabled");
        Check(acl.GetOwner(typeof(SecurityIdentifier))?.Value == "S-1-5-32-544", "state owner is Administrators, not an unprivileged creator");
        Check(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(x => x.AccessControlType == AccessControlType.Allow).All(x =>
                x.IdentityReference.Value is "S-1-5-18" or "S-1-5-32-544"), "state ACL restricted to SYSTEM and Administrators");
        Reject(() => store.UpdateSettings(store.Data.Settings with { RenewBeforeDays = 6 }), "invalid renewal window rejected");
        Reject(() => store.UpdateSettings(store.Data.Settings with { AcceptTerms = false }), "unaccepted terms rejected");
        var profile = new Profile { Host = "test.example.com", Expires = DateTimeOffset.UtcNow.AddDays(5) };
        store.Data.Profiles.Add(profile); store.Save();
        Check(Open(path).Data.Profiles.Single().Id == profile.Id, "managed rule persists");
        store.Data.Settings.RenewBeforeDays = 21; store.Save();
        File.WriteAllText(Path.Combine(path, "state.dpapi"), "corrupt-fixture");
        var recovered = Open(path);
        Check(recovered.Data.Settings.RenewBeforeDays == 30 && recovered.Data.Profiles.Single().Id == profile.Id, "corrupt state restores last valid encrypted backup");
        Check(Directory.GetFiles(path, "state.dpapi.corrupt-*").Length == 1, "corrupt original preserved for diagnosis");
    }
    sealed class FakeProbe : IDnsProbe
    {
        public bool Cname { get; set; }
        public Task<bool> HasCname(string name, CancellationToken ct) => Task.FromResult(Cname);
        public Task<bool> HasTxt(string name, string value, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    }
    sealed class FakeAliyun : HttpMessageHandler
    {
        public List<Dictionary<string,string>> Calls { get; } = [];
        public string? DeleteError { get; set; }
        public string? AddError { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://alidns.aliyuncs.com/", "Aliyun uses HTTPS POST");
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0].Replace("+", " ")), x => Uri.UnescapeDataString(x[1].Replace("+", " ")));
            var unsigned = new Dictionary<string,string>(form); unsigned.Remove("Signature");
            Check(form["Signature"] == Policy.Sign(unsigned, "fixture-secret"), "Aliyun signed request matches transmitted parameters");
            Calls.Add(form);
            var error = form["Action"] == "DeleteDomainRecord" ? DeleteError : AddError;
            var body = error == null ? "{\"RecordId\":\"fixture-record-1\"}" : JsonSerializer.Serialize(new { Code = error });
            return new HttpResponseMessage(error == null ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    static async Task DnsChecks(string scratch)
    {
        var state = Open(Path.Combine(scratch, "dns-state"));
        var settings = new Settings { Email = "test@example.com", AcceptTerms = true, AliyunZone = "example.com",
            AccessKeyId = "fixture-id", AccessKeySecret = "fixture-secret" };
        state.UpdateSettings(settings);
        using var transport = new FakeAliyun(); using var http = new HttpClient(transport); var probe = new FakeProbe();
        using var dns = new AliyunDns(state, settings, http, probe);
        var lease = await dns.Create("_acme-challenge.www.example.com", "fixture-txt", CancellationToken.None);
        Check(transport.Calls.Last()["RR"] == "_acme-challenge.www" && transport.Calls.Last()["DomainName"] == "example.com", "Aliyun zone and relative record transmitted correctly");
        Check(File.ReadAllText(Path.Combine(state.Root, "dns-cleanup.json")).Contains(lease.RecordId), "created TXT ID durably journaled");
        await dns.WaitPropagation(lease, CancellationToken.None);
        Check(true, "propagation uses DNS probe before validation");
        var refused = false;
        try { state.UpdateSettings(settings with { ClearDnsCredentials = true }); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "credentials cannot be cleared with pending TXT cleanup");
        state.UpdateSettings(settings with { AccessKeySecret = "rotated-fixture-secret" });
        Check(state.Data.Settings.AccessKeySecret == "rotated-fixture-secret", "same-zone key rotation allowed to recover cleanup permission");
        transport.DeleteError = "AccessDenied"; await dns.Cleanup(CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(state.Root, "dns-cleanup.json")).Contains(lease.RecordId), "failed TXT cleanup retained for retry");
        transport.DeleteError = "InvalidRecordId.NotFound"; await dns.Cleanup(CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(state.Root, "dns-cleanup.json")) == "[]", "already deleted TXT treated as successful cleanup");
        Check(transport.Calls.Where(x => x["Action"] == "DeleteDomainRecord").All(x => x["RecordId"] == lease.RecordId && !x.ContainsKey("RR")), "cleanup deletes exact owned RecordId only");
        var calls = transport.Calls.Count; probe.Cname = true; refused = false;
        try { await dns.Create("_acme-challenge.www.example.com", "fixture-txt", CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && transport.Calls.Count == calls, "CNAME delegation rejected before DNS mutation");
        probe.Cname = false; transport.AddError = "InvalidAccessKeyId.NotFound";
        string message = "";
        try { await dns.Create("_acme-challenge.www.example.com", "fixture-txt", CancellationToken.None); }
        catch (InvalidOperationException e) { message = e.Message; }
        Check(message.Contains("InvalidAccessKeyId.NotFound") && !message.Contains("fixture-secret"), "Aliyun API error reported without secret");
    }
    static async Task IisChecks(string scratch)
    {
        var host = "fixture-" + Guid.NewGuid().ToString("N") + ".example.invalid";
        var siteName = "IisCertManager.Tests-" + Guid.NewGuid().ToString("N");
        var webRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", siteName);
        Directory.CreateDirectory(webRoot);
        var acl = new DirectoryInfo(webRoot).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(webRoot).SetAccessControl(acl);
        File.WriteAllText(Path.Combine(webRoot, "probe.txt"), "iis-business-path");
        using var storeMy = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        using var storeCa = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
        storeMy.Open(OpenFlags.ReadWrite); storeCa.Open(OpenFlags.ReadWrite);
        using var caKey = RSA.Create(2048); using var issuerKey = RSA.Create(2048); using var leafKey = RSA.Create(2048);
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        var rootRequest = CaRequest("CN=" + siteName + " Root", caKey);
        using var root = rootRequest.CreateSelfSigned(from, from.AddDays(180));
        var issuerRequest = CaRequest("CN=" + siteName + " Intermediate", issuerKey);
        using var issuerPublic = issuerRequest.Create(root, from, from.AddDays(90), RandomNumberGenerator.GetBytes(16));
        using var issuer = issuerPublic.CopyWithPrivateKey(issuerKey);
        var leafRequest = new CertificateRequest("CN=" + host, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var leafPublic = leafRequest.Create(issuer, from, from.AddDays(30), RandomNumberGenerator.GetBytes(16));
        using var leaf = leafPublic.CopyWithPrivateKey(leafKey);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var bundle = new X509Certificate2Collection(new[] { leaf, issuerPublic, rootPublic });
        const string password = "local-fixture-password";
        var pfx = bundle.Export(X509ContentType.Pfx, password)!;
        var state = Open(Path.Combine(scratch, "iis-state")); var iis = new IisManager(state);
        long siteId = 0;
        // Use a free port for HTTPS. HTTP-01 intentionally exercises the mandatory port 80 alongside IIS.
        var temporary = new TcpListener(IPAddress.Loopback, 0); temporary.Start();
        var httpsPort = ((IPEndPoint)temporary.LocalEndpoint).Port; temporary.Stop();
        try
        {
            using (var manager = new ServerManager())
            {
                var site = manager.Sites.Add(siteName, "http", "*:80:" + host, webRoot);
                siteId = site.Id; manager.CommitChanges(); site.Start();
            }
            var profile = new Profile { SiteId = siteId, Host = host, Ip = "*", Port = httpsPort, Domains = [host] };
            Check(iis.Read().Any(x => x.SiteId == siteId && x.Host == host && x.Protocol == "http"), "real IIS enumeration");
            iis.Install(profile, pfx, password);
            Check(iis.Read().Any(x => x.SiteId == siteId && x.Protocol == "https" && x.Thumbprint == leaf.Thumbprint), "real IIS SNI certificate binding");
            Check(storeMy.Certificates.Find(X509FindType.FindByThumbprint, leaf.Thumbprint, false).Count > 0, "leaf installed in LocalMachine My");
            Check(storeCa.Certificates.Find(X509FindType.FindByThumbprint, issuer.Thumbprint, false).Count > 0, "intermediate installed in LocalMachine CA");
            using (var trust = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                trust.Open(OpenFlags.ReadOnly);
                Check(trust.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false).Count == 0, "PFX root never added to Windows trust");
            }
            var mismatch = profile with { Domains = [host, "missing.example.invalid"] };
            var rejected = false;
            try { iis.Install(mismatch, pfx, password); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && iis.Read().Single(x => x.SiteId == siteId && x.Protocol == "https").Thumbprint == leaf.Thumbprint, "missing SAN rejected without changing binding");
            Check(Directory.GetFiles(Path.Combine(state.Root, "bindings"), "*.json").Length == 1, "original binding snapshot saved");
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false,
                ConnectCallback = async (context, ct) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
                    catch { socket.Dispose(); throw; }
                },
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == leaf.Thumbprint }
            }) { Timeout = TimeSpan.FromSeconds(30) };
            Check(await client.GetStringAsync($"https://{host}:{httpsPort}/probe.txt") == "iis-business-path", "real IIS TLS handshake presents installed certificate");
            await using (var challenge = new HttpChallenge())
            {
                challenge.Add(host, "fixture-token", "fixture-key-auth");
                Check(await client.GetStringAsync($"http://{host}/.well-known/acme-challenge/fixture-token") == "fixture-key-auth", "HTTP.sys challenge shares port 80 with IIS");
                Check(await client.GetStringAsync($"http://{host}/probe.txt") == "iis-business-path", "business path unaffected by challenge listener");
                using var unknown = await client.GetAsync($"http://{host}/.well-known/acme-challenge/unknown");
                Check(unknown.StatusCode == HttpStatusCode.NotFound, "unknown ACME token denied");
                using var wrongCase = await client.GetAsync($"http://{host}/.well-known/acme-challenge/FIXTURE-token");
                Check(wrongCase.StatusCode == HttpStatusCode.NotFound, "challenge token is case sensitive");
                using var nested = await client.GetAsync($"http://{host}/.well-known/acme-challenge/nested/fixture-token");
                Check(nested.StatusCode == HttpStatusCode.NotFound, "nested challenge path rejected");
            }
            Check(await client.GetStringAsync($"http://{host}/probe.txt") == "iis-business-path", "IIS business path works after challenge cleanup");
        }
        finally
        {
            using var manager = new ServerManager();
            var site = manager.Sites.FirstOrDefault(x => x.Id == siteId);
            if (site != null) { manager.Sites.Remove(site); manager.CommitChanges(); }
            foreach (var cert in storeMy.Certificates.Find(X509FindType.FindByThumbprint, leaf.Thumbprint, false)) { storeMy.Remove(cert); cert.Dispose(); }
            foreach (var cert in storeCa.Certificates.Find(X509FindType.FindByThumbprint, issuer.Thumbprint, false)) { storeCa.Remove(cert); cert.Dispose(); }
            Directory.Delete(webRoot, true);
        }
    }
    static CertificateRequest CaRequest(string name, RSA key)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }
}
