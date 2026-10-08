#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$PackageRoot = (Split-Path $PSScriptRoot -Parent),
      [string]$InstallRoot = "$env:ProgramFiles\IisCertManager")
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PackageRoot 'service/IisCertManager.Service.exe'
$client = Join-Path $PackageRoot 'client/IisCertManager.Client.exe'
if (-not (Test-Path $exe) -or -not (Test-Path $client)) { throw 'Use a published package. To build source: ./scripts/Build.ps1' }
if (-not (Test-Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll")) {
    throw 'Enable IIS and IIS Management Scripts and Tools before installing.'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$programRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $InstallRoot.StartsWith($programRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot must be under Program Files so elevated executables cannot be replaced by ordinary users.'
}
if ((Test-Path $InstallRoot) -and ((Get-Item $InstallRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'InstallRoot cannot be a junction or symbolic link.'
}
if (Get-Process -Name 'IisCertManager.Client' -ErrorAction SilentlyContinue) {
    throw 'Close IIS Certificate Manager before updating, then run the installer again.'
}
# Installer payloads store identical client/service files only once. The portable ZIP remains complete.
function Get-PayloadHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$sharedFiles = @()
$sharedManifest = Join-Path $PackageRoot 'shared-files.json'
if (Test-Path -LiteralPath $sharedManifest) {
    $sharedFiles = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $sharedManifest -Raw)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $sharedFiles) {
        if ($entry.Name -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]*$' -or $entry.Name -in @('.', '..') -or
            $entry.SHA256 -notmatch '^[a-f0-9]{64}$' -or -not $seen.Add($entry.Name)) { throw 'Invalid shared file manifest.' }
        $source = Join-Path (Join-Path $PackageRoot 'client') $entry.Name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-PayloadHash $source) -ne $entry.SHA256) {
            throw "Shared payload file verification failed: $($entry.Name)"
        }
    }
}
$service = Get-Service IisCertManager -ErrorAction SilentlyContinue
if ($service) { Stop-Service IisCertManager; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
New-Item $InstallRoot -ItemType Directory -Force | Out-Null
# Remove inherited write access; only local Administrators and SYSTEM can modify elevated executables.
& icacls.exe $InstallRoot '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Install directory ACL failed.' }
Copy-Item "$PackageRoot/service" "$InstallRoot/" -Recurse -Force
Copy-Item "$PackageRoot/client" "$InstallRoot/" -Recurse -Force
foreach ($entry in $sharedFiles) {
    $source = Join-Path (Join-Path $InstallRoot 'client') $entry.Name
    $target = Join-Path (Join-Path $InstallRoot 'service') $entry.Name
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-PayloadHash $target) -ne $entry.SHA256) {
        throw "Installed shared file verification failed: $($entry.Name)"
    }
}
Copy-Item "$PackageRoot/scripts" "$InstallRoot/" -Recurse -Force
$binPath = '"' + (Join-Path $InstallRoot 'service/IisCertManager.Service.exe') + '"'
if ($service) {
    $registered = Get-CimInstance Win32_Service -Filter "Name='IisCertManager'"
    $result = Invoke-CimMethod -InputObject $registered -MethodName Change -Arguments @{
        PathName = $binPath; StartMode = 'Automatic'; StartName = 'LocalSystem'
    }
    if ($result.ReturnValue -ne 0) { throw "Service update failed ($($result.ReturnValue))." }
} else {
    New-Service -Name IisCertManager -BinaryPathName $binPath -StartupType Automatic -DisplayName 'IIS Certificate Manager' | Out-Null
}
& sc.exe description IisCertManager 'Local IIS ACME certificate issuance and renewal.' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service description failed.' }
& sc.exe failure IisCertManager reset= 86400 actions= restart/60000/restart/120000/restart/300000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed.' }
Start-Service IisCertManager
(Get-Service IisCertManager).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
if ([string]::IsNullOrWhiteSpace($desktop)) { throw 'Cannot locate the common desktop directory.' }
New-Item -Path $desktop -ItemType Directory -Force | Out-Null
# ASCII file name also works on server images with no Chinese system locale.
$link = $shell.CreateShortcut((Join-Path $desktop 'IIS Certificate Manager.lnk'))
$link.TargetPath = Join-Path $InstallRoot 'client/IisCertManager.Client.exe'
$link.WorkingDirectory = Join-Path $InstallRoot 'client'
$link.IconLocation = (Join-Path $InstallRoot 'client/Assets/App.ico') + ',0'
$link.Save()
Write-Host 'Installed. Open IIS Certificate Manager from the desktop (UAC required).'
Write-Host 'For HTTP-01, allow inbound public TCP 80 on the host/router/cloud firewall; no firewall rules were changed by this installer.'
