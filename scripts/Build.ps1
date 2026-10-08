[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
      [string]$Version = '0.1.0-dev',
      [string]$NativeToolchain = '',
      [switch]$TestSign,
      [string]$OpenSsl = 'openssl',
      [string]$OsslSignCode = '',
      [switch]$WorkingTreeSource)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid version.' }
$root = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0' }
. (Join-Path $PSScriptRoot 'Test-Signing.ps1')
$testSigner = $null
try {
function Invoke-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE)" } }
Invoke-Dotnet restore "$root/IisCertManager.sln"
Invoke-Dotnet build "$root/IisCertManager.sln" -c Release --no-restore "-p:Version=$Version"
Invoke-Dotnet run --project "$root/tests/IisCertManager.Tests" -c Release --no-build
$package = Join-Path $root 'artifacts/package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item $package -ItemType Directory -Force | Out-Null
foreach ($project in @('Service','Client')) {
    Invoke-Dotnet publish "$root/src/IisCertManager.$project" -c Release "-p:Version=$Version" -r $Runtime --self-contained true "-p:PublishSingleFile=false" -o "$package/$($project.ToLowerInvariant())"
}
Copy-Item "$root/scripts" "$package/scripts" -Recurse
Copy-Item "$root/README.md" "$package/README.md"
Copy-Item "$root/docs" "$package/docs" -Recurse
Copy-Item "$root/VALIDATION.md" "$package/VALIDATION.md"
Copy-Item "$root/REVIEW.md" "$package/REVIEW.md"
if ($TestSign) {
    $testSigner = New-TestSigner -OutputCertificate (Join-Path $root 'artifacts/TEST-SIGNING.cer') -OpenSsl $OpenSsl -OsslSignCode $OsslSignCode
    foreach ($project in @('Service','Client')) { Set-TestSignature -Signer $testSigner -File (Join-Path $package "$($project.ToLowerInvariant())/IisCertManager.$project.exe") }
} else { Remove-Item (Join-Path $root 'artifacts/TEST-SIGNING.cer') -ErrorAction SilentlyContinue }
$zip = Join-Path $root "artifacts/IisCertManager-$Runtime.zip"
Compress-Archive "$package/*" $zip -Force
# Native Win32 installer: no managed runtime or single-file extraction is needed to open the UI.
if ($env:OS -eq 'Windows_NT' -or $NativeToolchain) {
    if ($Runtime -ne 'win-x64') { throw 'Native installer currently supports win-x64 only.' }
    $native = Join-Path $root 'artifacts/native-setup'
    New-Item $native -ItemType Directory -Force | Out-Null
    # Keep the portable ZIP complete; deduplicate identical files in the installer payload only.
    $nativePackage = Join-Path $root 'artifacts/native-package'
    if (Test-Path $nativePackage) { Remove-Item $nativePackage -Recurse -Force }
    New-Item $nativePackage -ItemType Directory -Force | Out-Null
    Copy-Item "$package/*" $nativePackage -Recurse
    $shared = @()
    foreach ($file in (Get-ChildItem (Join-Path $nativePackage 'service') -File | Sort-Object Name)) {
        $clientFile = Join-Path (Join-Path $nativePackage 'client') $file.Name
        if (-not (Test-Path -LiteralPath $clientFile -PathType Leaf)) { continue }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne (Get-FileHash -LiteralPath $clientFile -Algorithm SHA256).Hash.ToLowerInvariant()) { continue }
        $shared += [pscustomobject]@{ Name = $file.Name; SHA256 = $hash }
        Remove-Item -LiteralPath $file.FullName
    }
    ConvertTo-Json -InputObject @($shared) | Set-Content -Encoding utf8 (Join-Path $nativePackage 'shared-files.json')
    $nativePayload = Join-Path $native 'payload.zip'
    Compress-Archive "$nativePackage/*" $nativePayload -Force
    Write-Host ("Installer payload: removed {0} duplicate files." -f $shared.Count)
    $manifestPath = (Join-Path $root 'src/NativeSetup/app.manifest').Replace('\', '/')
    $bootstrapPath = (Join-Path $root 'src/NativeSetup/Bootstrap.ps1').Replace('\', '/')
    $payloadPath = $nativePayload.Replace('\', '/')
    $iconPath = (Join-Path $root 'src/Assets/App.ico').Replace('\', '/')
    @(('1 24 "' + $manifestPath + '"'), ('101 RCDATA "' + $payloadPath + '"'), ('102 RCDATA "' + $bootstrapPath + '"'), ('103 ICON "' + $iconPath + '"')) | Set-Content -Encoding utf8 (Join-Path $native 'setup.rc')
    if ($env:OS -ne 'Windows_NT') {
        $compiler = Join-Path $NativeToolchain 'bin/x86_64-w64-mingw32-clang++'
        $resources = Join-Path $NativeToolchain 'bin/x86_64-w64-mingw32-windres'
        & $resources --codepage=65001 -i (Join-Path $native 'setup.rc') -o (Join-Path $native 'setup.res.o')
        if ($LASTEXITCODE -ne 0) { throw 'Native resource compilation failed.' }
        & $compiler -std=c++17 -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers -DUNICODE -D_UNICODE -municode -mwindows -static (Join-Path $root 'src/NativeSetup/Setup.cpp') (Join-Path $native 'setup.res.o') -o (Join-Path $native 'IisCertManager-Setup-win-x64.exe') -luser32 -lshell32 -ladvapi32 -lole32 -lgdi32 -lcomctl32 '-Wl,--dynamicbase,--nxcompat'
        if ($LASTEXITCODE -ne 0) { throw 'Native cross-compilation failed.' }
    } else {
    $buildCommand = '""{0}" "{1}""' -f (Join-Path $root 'scripts/Build-NativeSetup.cmd'), $root
    & $env:ComSpec /d /s /c $buildCommand
    if ($LASTEXITCODE -ne 0) { throw 'Native installer build failed. Install Visual Studio C++ Build Tools with Windows SDK.' }
    }
    $setup = Join-Path $root "artifacts/IisCertManager-Setup-$Runtime.exe"
    Copy-Item (Join-Path $native 'IisCertManager-Setup-win-x64.exe') $setup -Force
    if ($TestSign) { Set-TestSignature -Signer $testSigner -File $setup }
} else {
    Write-Host 'Native installer requires Windows + Visual Studio C++ Build Tools; producing ZIP packages only on this host.'
    $setup = $null
}
$sourceZip = Join-Path $root 'artifacts/IisCertManager-source.zip'
$hasRepository = $false
if ((Test-Path -LiteralPath (Join-Path $root '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    & git -C $root rev-parse --git-dir 2>$null | Out-Null
    $hasRepository = $LASTEXITCODE -eq 0
}
if ($hasRepository -and -not $WorkingTreeSource) {
    & git -C $root archive --format=zip --prefix=IisCertManager/ -o $sourceZip HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
} else {
    # Release source archives do not contain .git. Keep them buildable, and exclude build/runtime secrets.
    $sourceStage = Join-Path $root 'artifacts/source/IisCertManager'
    if (Test-Path $sourceStage) { Remove-Item $sourceStage -Recurse -Force }
    New-Item $sourceStage -ItemType Directory -Force | Out-Null
    $sourcePrefix = (Get-Item $root).FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $sourceFiles = Get-ChildItem $root -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($sourcePrefix.Length)
        $relative -notmatch '(^|[\\/])(bin|obj|artifacts|\.git|\.vs)([\\/]|$)' -and
        ($_.Extension -in @('.cs','.csproj','.sln','.xaml','.manifest','.props','.targets','.md','.yml','.yaml','.ps1','.cmd','.cpp','.ico','.png','.svg') -or
         $_.Name -in @('.gitignore','.gitattributes','packages.lock.json','aliyun-ram-policy.json'))
    }
    foreach ($file in $sourceFiles) {
        $target = Join-Path $sourceStage ($file.FullName.Substring($sourcePrefix.Length))
        New-Item ([IO.Path]::GetDirectoryName($target)) -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    Compress-Archive -Path $sourceStage -DestinationPath $sourceZip -Force
}
$publicCertificate = if ($TestSign) { Join-Path $root 'artifacts/TEST-SIGNING.cer' } else { $null }
$hashes = @($setup, $zip, $sourceZip, $publicCertificate) | Where-Object { $_ } | ForEach-Object {
    $hash = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
}
$hashes | Set-Content -Encoding ascii (Join-Path $root 'artifacts/SHA256SUMS.txt')
$hashes | Write-Host
Write-Host "Package: $zip"

} finally { Remove-TestSigner -Signer $testSigner }
