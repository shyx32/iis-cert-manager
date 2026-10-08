[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
      [string]$Version = '0.1.0-dev')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid version.' }
$root = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0' }
function Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" } }
Dotnet restore "$root/IisCertManager.sln"
Dotnet build "$root/IisCertManager.sln" -c Release --no-restore "-p:Version=$Version"
Dotnet run --project "$root/tests/IisCertManager.Tests" -c Release --no-build
$package = Join-Path $root 'artifacts/package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item $package -ItemType Directory -Force | Out-Null
foreach ($project in @('Service','Client')) {
    Dotnet publish "$root/src/IisCertManager.$project" -c Release "-p:Version=$Version" -r $Runtime --self-contained true -p:PublishSingleFile=false -o "$package/$($project.ToLowerInvariant())"
}
Copy-Item "$root/scripts" "$package/scripts" -Recurse
Copy-Item "$root/README.md" "$package/README.md"
Copy-Item "$root/docs" "$package/docs" -Recurse
Copy-Item "$root/VALIDATION.md" "$package/VALIDATION.md"
$zip = Join-Path $root "artifacts/IisCertManager-$Runtime.zip"
Compress-Archive "$package/*" $zip -Force
$sourceZip = Join-Path $root 'artifacts/IisCertManager-source.zip'
& git -C $root archive --format=zip --prefix=IisCertManager/ -o $sourceZip HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
$hashes = @($zip, $sourceZip) | ForEach-Object {
    $hash = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
}
$hashes | Set-Content -Encoding ascii (Join-Path $root 'artifacts/SHA256SUMS.txt')
$hashes | Write-Host
Write-Host "Package: $zip"
