[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Stage)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    $payload = Join-Path $Stage 'payload'
    Expand-Archive -LiteralPath (Join-Path $Stage 'payload.zip') -DestinationPath $payload
    & (Join-Path $payload 'scripts/Install.ps1') -PackageRoot $payload
    exit 0
} catch {
    Write-Output ($_ | Out-String)
    exit 1
}
