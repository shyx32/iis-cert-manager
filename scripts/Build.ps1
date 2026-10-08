[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0' }
function Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" } }
Dotnet restore "$root/IisCertManager.sln"
Dotnet build "$root/IisCertManager.sln" -c Release --no-restore
Dotnet run --project "$root/tests/IisCertManager.Tests" -c Release --no-build
$package = Join-Path $root 'artifacts/package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item $package -ItemType Directory -Force | Out-Null
foreach ($project in @('Service','Client')) {
    Dotnet publish "$root/src/IisCertManager.$project" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o "$package/$($project.ToLowerInvariant())"
}
Copy-Item "$root/scripts" "$package/scripts" -Recurse
Copy-Item "$root/README.md" "$package/README.md"
Copy-Item "$root/docs" "$package/docs" -Recurse
$zip = Join-Path $root "artifacts/IisCertManager-$Runtime.zip"
Compress-Archive "$package/*" $zip -Force
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "Package: $zip"
