using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IisCertManager.Contracts;
namespace IisCertManager.Service;

public static class DiagnosticReport
{
    public static string KeyInfo(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) return "HasPrivateKey=false";
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng cng)
                return $"RSA/CNG; Provider={cng.Key.Provider}; MachineKey={cng.Key.IsMachineKey}; Ephemeral={cng.Key.IsEphemeral}";
            if (rsa is RSACryptoServiceProvider csp)
                return $"RSA/CAPI; Provider={csp.CspKeyContainerInfo.ProviderName}; MachineKey={csp.CspKeyContainerInfo.MachineKeyStore}; Accessible={csp.CspKeyContainerInfo.Accessible}; KeyNumber={csp.CspKeyContainerInfo.KeyNumber}";
            using var ec = cert.GetECDsaPrivateKey();
            if (ec is ECDsaCng ecng)
                return $"ECDSA/CNG; Provider={ecng.Key.Provider}; MachineKey={ecng.Key.IsMachineKey}; Ephemeral={ecng.Key.IsEphemeral}";
            return "HasPrivateKey=true; Algorithm=" + cert.PublicKey.Oid.Value;
        }
        catch (Exception e) { return $"PrivateKeyError=0x{e.HResult:X8}: {e.Message}"; }
    }

    public static string Create(StateStore state, IisManager iis)
    {
        var report = new StringBuilder();
        report.AppendLine("IIS Certificate Manager Diagnostics");
        report.AppendLine($"Time: {DateTimeOffset.Now:O}");
        report.AppendLine($"Version: {typeof(DiagnosticReport).Assembly.GetName().Version}");
        report.AppendLine($"OS: {RuntimeInformation.OSDescription}; Runtime: {RuntimeInformation.FrameworkDescription}; Architecture: {RuntimeInformation.ProcessArchitecture}");
        try
        {
            var revision = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", "unknown");
            report.AppendLine($"OS build: {Environment.OSVersion.Version.Build}.{revision}");
        }
        catch (Exception e) { report.AppendLine("OS revision unavailable: " + e.Message); }
        report.AppendLine($"Service identity: {WindowsIdentity.GetCurrent().Name}");
        report.AppendLine("Private keys, PFX passwords, ACME account files and DNS credentials are excluded.");
        report.AppendLine("\n--- IIS bindings ---");
        var thumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bindings = iis.Read();
            report.AppendLine(JsonSerializer.Serialize(bindings, Wire.Json));
            foreach (var binding in bindings) if (binding.Thumbprint != null) thumbs.Add(binding.Thumbprint);
        }
        catch (Exception e) { report.AppendLine(e.ToString()); }
        report.AppendLine("\n--- LocalMachine/My certificate metadata ---");
        try
        {
            using var certificates = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            certificates.Open(OpenFlags.ReadOnly);
            foreach (var cert in certificates.Certificates)
            {
                using (cert)
                {
                    if (!thumbs.Contains(cert.Thumbprint) && cert.NotBefore < DateTime.Now.AddDays(-7)) continue;
                    report.AppendLine($"Thumbprint={cert.Thumbprint}; Subject={cert.Subject}; Expires={cert.NotAfter:O}; {KeyInfo(cert)}");
                }
            }
        }
        catch (Exception e) { report.AppendLine(e.ToString()); }
        report.AppendLine("\n--- Recent Windows TLS events ---");
        try
        {
            using var system = new EventLog("System");
            var matched = 0;
            for (var i = system.Entries.Count - 1; i >= 0 && matched < 30 && i >= system.Entries.Count - 2000; i--)
            {
                var entry = system.Entries[i];
                if (entry.TimeGenerated < DateTime.Now.AddHours(-24)) break;
                if (!entry.Source.Contains("Schannel", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Source.Contains("HttpEvent", StringComparison.OrdinalIgnoreCase)) continue;
                report.AppendLine($"{entry.TimeGenerated:O} {entry.Source} {entry.InstanceId}: {entry.Message}");
                matched++;
            }
        }
        catch (Exception e) { report.AppendLine("Event log unavailable: " + e.Message); }
        report.AppendLine("\n--- Recent service log (up to 500 lines) ---");
        report.AppendLine(string.Join(Environment.NewLine, state.Logs(500)));
        return state.Redact(report.ToString());
    }
}
