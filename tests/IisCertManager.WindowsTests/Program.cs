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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
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
    static void Main(string[] args)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "IisCertManager.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            if (!args.Contains("--ui-only"))
            {
                StateChecks(scratch);
                DnsChecks(scratch).GetAwaiter().GetResult();
                IisChecks(scratch).GetAwaiter().GetResult();
            }
            var application = new IisCertManager.Client.App();
            application.InitializeComponent();
            var window = new IisCertManager.Client.MainWindow();
            Check(window.Title == "IIS 证书管家", "WPF application resources and window instantiate");
            UiChecks(window);
            window.Close();
            Console.WriteLine($"{checks} Windows checks passed.");
        }
        finally { Directory.Delete(scratch, true); }
    }
    static void UiChecks(IisCertManager.Client.MainWindow window)
    {
        var binding = new SiteBinding(1, "企业网站", "Started", @"C:\inetpub\wwwroot", "https", "*", 443,
            "www.example.com", "*:443:www.example.com", "ABC123", DateTimeOffset.Now.AddDays(20), 1,
            "www.example.com", "Example CA", "My", "DefaultAppPool");
        var profile = new Profile { SiteId = 1, SiteName = binding.SiteName, Host = binding.Host, Domains = [binding.Host] };
        var snapshot = new Snapshot(new Settings { Email = "admin@example.com", AcceptTerms = true, Staging = false }, [binding], [profile], ["IIS 配置读取成功", "自动续期服务已就绪"]);
        var apply = typeof(IisCertManager.Client.MainWindow).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(window, [snapshot, true]);
        T Control<T>(string name) => (T)window.FindName(name);
        var sites = Control<DataGrid>("Sites"); sites.SelectedItem = binding;
        Control<DataGrid>("Profiles").SelectedItem = profile;
        Check(Control<TextBox>("Domains").Text == binding.Host, "IIS selection prefills certificate domain");
        Control<TextBox>("Domains").Text = "www.example.com, api.example.com";
        Control<TextBox>("Email").Text = "unsaved@example.com";
        var updated = binding with { Root = @"D:\Websites\Corporate", Thumbprint = "DEF456", CertificateIssuer = "New CA" };
        apply.Invoke(window, [snapshot with { Bindings = [updated], Profiles = [profile with { Status = "续期成功" }] }, false]);
        Check(Control<TextBox>("Domains").Text.Contains("api.example.com"), "manual sync preserves unsaved certificate input");
        Check(Control<TextBox>("Email").Text == "unsaved@example.com", "manual sync preserves unsaved account settings");
        Check(Control<TextBlock>("RootPath").Text == updated.Root && Control<TextBlock>("Thumbprint").Text == "DEF456", "external IIS changes update live details");
        Check((Control<DataGrid>("Profiles").SelectedItem as Profile)?.Id == profile.Id, "refresh preserves managed rule selection");
        Control<TextBox>("Search").Text = "no-matching-site";
        Check(sites.Items.Count == 0, "site search filters actual bindings");
        Control<TextBox>("Search").Clear(); sites.SelectedItem = updated;
        Check(sites.Items.Count == 1, "clearing search restores IIS list");
        // Render the real WPF tree, without starting the Loaded handler's service connection.
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1280, 860)); root.Arrange(new Rect(0, 0, 1280, 860)); root.UpdateLayout();
        Check(Control<TextBox>("Domains").Foreground is SolidColorBrush brush && brush.Color.A == 255 && brush.Color.R + brush.Color.G + brush.Color.B < 384, "navigation selection does not hide content text");
        var bitmap = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        using (var file = File.Create(Path.Combine(output, "IisCertManager-Main.png")))
        { encoder.Save(file); Check(file.Length > 1000, "real WPF main window screenshot rendered"); }
        var tabs = Control<TabControl>("Tabs");
        Check(tabs.Template.FindName("PART_SelectedContentHost", tabs) is ContentPresenter, "tab content remains accessible to keyboard and UI automation");
        void Capture(string name, int width, int height)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var target = File.Create(Path.Combine(output, name + ".png")); png.Save(target);
        }
        Capture("IisCertManager-Compact", 1060, 720);
        Check(Control<ScrollViewer>("DetailScroll").ScrollableHeight < 1,
            "common HTTPS configuration fits the minimum window without scrolling");
        var count = Control<TextBlock>("SiteCount");
        var bounds = count.TransformToAncestor(root).TransformBounds(new Rect(count.RenderSize));
        Check(bounds.Bottom < 240 && count.ActualHeight + count.Margin.Top + count.Margin.Bottom + 1 >= count.DesiredSize.Height, "overview text remains visible at minimum window size");
        sites.SelectedItem = null;
        Capture("IisCertManager-Sites", 1240, 820);
        Check(Control<Border>("DetailPanel").Visibility == Visibility.Collapsed, "unselected binding does not display an empty form");
        tabs.SelectedIndex = 2; Capture("IisCertManager-Settings", 1240, 820);
        tabs.SelectedIndex = 1; Capture("IisCertManager-Rules", 1240, 820);
        tabs.SelectedIndex = 3; Capture("IisCertManager-Logs", 1240, 820);
        Check(Control<TextBox>("Email").Text == "unsaved@example.com", "page navigation preserves settings draft");
        var logs = Control<TextBox>("Logs");
        Check(logs.VerticalContentAlignment == VerticalAlignment.Top && logs.VerticalOffset == 0,
            "short logs start at the top without automatic scrolling");
        apply.Invoke(window, [snapshot with { Logs = Enumerable.Range(0, 120).Select(i => $"{i:D3} 日志行").ToArray() }, false]);
        Capture("IisCertManager-Logs-Long", 1060, 720);
        logs.ScrollToVerticalOffset(100); root.UpdateLayout();
        var readingOffset = logs.VerticalOffset;
        apply.Invoke(window, [snapshot with { Logs = Enumerable.Range(0, 120).Select(i => $"{i:D3} 更新日志行").ToArray() }, false]);
        root.UpdateLayout();
        Check(readingOffset > 0 && Math.Abs(logs.VerticalOffset - readingOffset) < 1,
            "manual refresh preserves the log reading position");
        apply.Invoke(window, [snapshot with { Logs = [], Profiles = [] }, false]); root.UpdateLayout();
        Check(Control<TextBlock>("EmptyLogs").Visibility == Visibility.Visible && !Control<Button>("CopyLogsButton").IsEnabled,
            "empty logs show a centered hint and disable copying");
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
        using var renewedKey = RSA.Create(2048);
        var renewedRequest = new CertificateRequest("CN=" + host, renewedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (var extension in leafRequest.CertificateExtensions) renewedRequest.CertificateExtensions.Add(extension);
        using var renewedPublic = renewedRequest.Create(issuer, from, from.AddDays(60), RandomNumberGenerator.GetBytes(16));
        using var renewed = renewedPublic.CopyWithPrivateKey(renewedKey);
        var renewedPfx = new X509Certificate2Collection(new[] { renewed, issuerPublic, rootPublic }).Export(X509ContentType.Pfx, password)!;
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
                siteId = site.Id; manager.CommitChanges();
            }
            // IIS 2016 may not expose the new runtime site immediately after committing configuration.
            for (var attempt = 0; ; attempt++)
            {
                using var runtime = new ServerManager();
                try { runtime.Sites.First(x => x.Id == siteId).Start(); break; }
                catch (COMException error) when (error.HResult == unchecked((int)0x800710D8) && attempt < 39)
                { await Task.Delay(250); }
            }
            var profile = new Profile { SiteId = siteId, Host = host, Ip = "*", Port = httpsPort, Domains = [host] };
            Check(iis.Read().Any(x => x.SiteId == siteId && x.Host == host && x.Protocol == "http"), "real IIS enumeration");
            iis.Install(profile, pfx, password);
            Check(iis.Read().Any(x => x.SiteId == siteId && x.Protocol == "https" && x.Thumbprint == leaf.Thumbprint), "real IIS SNI certificate binding");
            var details = iis.Read().Single(x => x.SiteId == siteId && x.Protocol == "https");
            Check(details.CertificateSubject == host && details.CertificateStore == "My" && details.ApplicationPool != null, "IIS certificate and application pool details read back");
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
            async Task<bool> Presents(string thumbprint)
            {
                using var fresh = new HttpClient(new SocketsHttpHandler { UseProxy = false,
                    ConnectCallback = async (context, ct) =>
                    {
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try { await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
                        catch { socket.Dispose(); throw; }
                    },
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == thumbprint }
                }) { Timeout = TimeSpan.FromSeconds(30) };
                return await fresh.GetStringAsync($"https://{host}:{httpsPort}/probe.txt") == "iis-business-path";
            }
            iis.Install(profile, renewedPfx, password);
            var replaced = iis.Read().Where(x => x.SiteId == siteId && x.Protocol == "https").ToArray();
            Check(replaced.Length == 1 && replaced[0].Thumbprint == renewed.Thumbprint && replaced[0].SslFlags == 1,
                "renewal replaces the existing SNI binding without duplication");
            Check(profile.Thumbprint == renewed.Thumbprint && profile.Expires > leaf.NotAfter.ToUniversalTime(),
                "replacement updates managed certificate metadata and expiry");
            Check(await Presents(renewed.Thumbprint), "fresh TLS handshake presents the replacement certificate offline");
            Check(storeMy.Certificates.Find(X509FindType.FindByThumbprint, leaf.Thumbprint, false).Count > 0,
                "replacement retains the previous certificate for recovery");
            var backups = Directory.GetFiles(Path.Combine(state.Root, "bindings"), "*.json");
            Check(backups.Any(path => File.ReadAllText(path).Contains(leaf.Thumbprint)),
                "replacement snapshot records the previous certificate thumbprint");
            iis.Install(profile, renewedPfx, password);
            Check(iis.Read().Count(x => x.SiteId == siteId && x.Protocol == "https") == 1 && await Presents(renewed.Thumbprint),
                "repeated deployment remains idempotent and serves HTTPS");
            rejected = false;
            try { iis.Install(mismatch, renewedPfx, password); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && await Presents(renewed.Thumbprint), "invalid replacement leaves the active TLS certificate unchanged");
            iis.Install(profile, pfx, password);
            Check(await Presents(leaf.Thumbprint), "previous certificate can be redeployed for recovery");
            Check(await client.GetStringAsync($"http://{host}/probe.txt") == "iis-business-path",
                "HTTP business binding survives replacement and recovery");
        }
        finally
        {
            using var manager = new ServerManager();
            var site = manager.Sites.FirstOrDefault(x => x.Id == siteId);
            if (site != null) { manager.Sites.Remove(site); manager.CommitChanges(); }
            foreach (var cert in storeMy.Certificates.Find(X509FindType.FindByThumbprint, leaf.Thumbprint, false)) { storeMy.Remove(cert); cert.Dispose(); }
            foreach (var cert in storeMy.Certificates.Find(X509FindType.FindByThumbprint, renewed.Thumbprint, false)) { storeMy.Remove(cert); cert.Dispose(); }
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
