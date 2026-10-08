#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallRoot = "$env:ProgramFiles\IisCertManager")
$ErrorActionPreference = 'Stop'
$service = Get-Service IisCertManager -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service IisCertManager
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    & sc.exe delete IisCertManager | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' }
}
$link = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'IIS 证书管家.lnk'
if (Test-Path $link) { Remove-Item $link }
Write-Host "Service removed. Program files retained: $InstallRoot"
Write-Host 'IIS bindings, certificates and encrypted state in %ProgramData%\IisCertManager are retained.'
