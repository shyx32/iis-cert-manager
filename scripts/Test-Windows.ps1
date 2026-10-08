#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# Intended only for disposable CI/test Windows hosts: enables IIS and creates temporary fixtures.
$feature = Install-WindowsFeature Web-Server, Web-Scripting-Tools, Web-Mgmt-Console
if (-not $feature.Success) { throw 'Unable to enable IIS test prerequisites.' }
if ($feature.RestartNeeded -eq 'Yes') { throw 'IIS needs a restart before native checks can run.' }
Start-Service W3SVC
& dotnet run --project "$root/tests/IisCertManager.WindowsTests" -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Native Windows checks failed.' }
try {
    & "$root/scripts/Install.ps1" -PackageRoot "$root/artifacts/package"
    & "$root/scripts/Verify-Windows.ps1"
    # Reinstall exercises the update path and verifies a quoted service executable path.
    & "$root/scripts/Install.ps1" -PackageRoot "$root/artifacts/package"
    $service = Get-CimInstance Win32_Service -Filter "Name='IisCertManager'"
    $expected = '"' + "$env:ProgramFiles\IisCertManager\service\IisCertManager.Service.exe" + '"'
    if ($service.PathName -ne $expected) { throw "Unexpected service executable path: $($service.PathName)" }
    & "$root/scripts/Verify-Windows.ps1"
} finally { & "$root/scripts/Uninstall.ps1" }
Write-Host 'PASS: native IIS/TLS, HTTP.sys, DPAPI, WPF construction, service install/update/control/uninstall.'
