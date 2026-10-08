#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Backup)
$ErrorActionPreference = 'Stop'
$service = Get-Service IisCertManager -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service IisCertManager
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}
Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll"
$data = Get-Content -LiteralPath $Backup -Raw | ConvertFrom-Json
$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    $site = @($manager.Sites | Where-Object Id -eq $data.SiteId)[0]
    if (-not $site) { throw 'Original IIS site no longer exists.' }
    $binding = @($site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq $data.BindingInformation })[0]
    if ($binding -and $data.InstalledThumbprint) {
        $currentThumbprint = [BitConverter]::ToString($binding.CertificateHash).Replace('-', '')
        if ($currentThumbprint -ne $data.InstalledThumbprint) { throw 'Binding was changed after this deployment; inspect and restore manually.' }
    }
    if ($data.Existed) {
        if (-not $binding) { throw 'Binding no longer exists; restore manually after review.' }
        if (-not $data.Thumbprint) { throw 'Snapshot has no certificate thumbprint.' }
        $hash = New-Object byte[] ($data.Thumbprint.Length / 2)
        for ($i = 0; $i -lt $hash.Length; $i++) { $hash[$i] = [Convert]::ToByte($data.Thumbprint.Substring($i * 2, 2), 16) }
        $binding.CertificateHash = $hash
        $binding.CertificateStoreName = $data.CertificateStore
        $binding.SslFlags = [Microsoft.Web.Administration.SslFlags][int]$data.SslFlags
    } elseif ($binding) { $site.Bindings.Remove($binding) }
    $manager.CommitChanges()
} finally { $manager.Dispose() }
Write-Host 'Original binding restored. The renewal service is STOPPED. Review/disable the managed rule before enabling renewal again.'
