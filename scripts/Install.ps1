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
$service = Get-Service IisCertManager -ErrorAction SilentlyContinue
if ($service) { Stop-Service IisCertManager; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
New-Item $InstallRoot -ItemType Directory -Force | Out-Null
# Remove inherited write access; only local Administrators and SYSTEM can modify elevated executables.
& icacls.exe $InstallRoot '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Install directory ACL failed.' }
Copy-Item "$PackageRoot/service" "$InstallRoot/" -Recurse -Force
Copy-Item "$PackageRoot/client" "$InstallRoot/" -Recurse -Force
Copy-Item "$PackageRoot/scripts" "$InstallRoot/" -Recurse -Force
$binPath = '"' + (Join-Path $InstallRoot 'service/IisCertManager.Service.exe') + '"'
if ($service) { & sc.exe config IisCertManager binPath= $binPath start= auto | Out-Null }
else { & sc.exe create IisCertManager binPath= $binPath start= auto DisplayName= 'IIS Certificate Manager' | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'Service registration failed.' }
& sc.exe description IisCertManager 'Local IIS ACME certificate issuance and renewal.' | Out-Null
& sc.exe failure IisCertManager reset= 86400 actions= restart/60000/restart/120000/restart/300000 | Out-Null
Start-Service IisCertManager
(Get-Service IisCertManager).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'IIS 证书管家.lnk'))
$link.TargetPath = Join-Path $InstallRoot 'client/IisCertManager.Client.exe'
$link.WorkingDirectory = Join-Path $InstallRoot 'client'
$link.Save()
Write-Host 'Installed. Open IIS 证书管家 from the desktop (UAC required).'
Write-Host 'For HTTP-01, allow inbound public TCP 80 on the host/router/cloud firewall; no firewall rules were changed by this installer.'
