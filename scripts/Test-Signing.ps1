# Test-only Authenticode signatures. Never add this certificate to any trust store.
function New-TestSigner {
    param([string]$OutputCertificate, [string]$OpenSsl, [string]$OsslSignCode)
    if ($env:OS -eq 'Windows_NT') {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=IIS Cert Manager TEST ONLY' -HashAlgorithm SHA256 -KeyLength 3072 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(14) -CertStoreLocation Cert:\CurrentUser\My
        try { Export-Certificate -Cert $certificate -FilePath $OutputCertificate | Out-Null }
        catch { Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey -Force; throw }
        return @{ Certificate = $certificate }
    }
    if (-not (Test-Path -LiteralPath $OsslSignCode)) { throw 'Test signing on macOS requires -OsslSignCode pointing to osslsigncode.' }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('IisCertManager-sign-' + [Guid]::NewGuid().ToString('N'))
    New-Item $scratch -ItemType Directory | Out-Null
    try {
        & chmod 700 $scratch
        if ($LASTEXITCODE -ne 0) { throw 'Cannot protect temporary signing directory.' }
        & $OpenSsl req -x509 -newkey rsa:3072 -sha256 -nodes -days 14 -subj '/CN=IIS Cert Manager TEST ONLY' -addext 'extendedKeyUsage=codeSigning' -addext 'keyUsage=critical,digitalSignature' -addext 'basicConstraints=critical,CA:FALSE' -keyout "$scratch/key.pem" -out "$scratch/cert.pem" 2> "$scratch/openssl.log"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot generate test certificate.' }
        & $OpenSsl x509 -in "$scratch/cert.pem" -outform der -out $OutputCertificate
        if ($LASTEXITCODE -ne 0) { throw 'Cannot export public test certificate.' }
        return @{ Scratch = $scratch; Tool = $OsslSignCode }
    } catch { Remove-Item $scratch -Recurse -Force; throw }
}
function Set-TestSignature {
    param($Signer, [string]$File)
    if ($Signer.Certificate) {
        $result = Set-AuthenticodeSignature -FilePath $File -Certificate $Signer.Certificate -HashAlgorithm SHA256
        if ($result.SignerCertificate.Thumbprint -ne $Signer.Certificate.Thumbprint -or $result.Status -eq 'HashMismatch' -or $result.Status -eq 'NotSigned') { throw "Test signing failed: $File ($($result.Status))" }
    } else {
        & $Signer.Tool sign -certs "$($Signer.Scratch)/cert.pem" -key "$($Signer.Scratch)/key.pem" -h sha256 -n 'IIS Cert Manager TEST ONLY' -in $File -out "$File.signed"
        if ($LASTEXITCODE -ne 0) { throw "Test signing failed: $File" }
        Move-Item -LiteralPath "$File.signed" -Destination $File -Force
        # Trust is supplied for this verification command only, not installed in the OS.
        & $Signer.Tool verify -CAfile "$($Signer.Scratch)/cert.pem" -in $File
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $File" }
    }
}
function Remove-TestSigner {
    param($Signer)
    if (-not $Signer) { return }
    if ($Signer.Certificate) { Remove-Item "Cert:\CurrentUser\My\$($Signer.Certificate.Thumbprint)" -DeleteKey -Force }
    elseif ($Signer.Scratch) { Remove-Item -LiteralPath $Signer.Scratch -Recurse -Force }
}
