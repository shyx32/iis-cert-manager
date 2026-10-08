#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$service = Get-Service IisCertManager
if ($service.Status -ne 'Running') { throw 'IisCertManager service is not running.' }
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'IisCertManager.v1', [System.IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect(5000)
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = New-Object System.IO.StreamWriter($pipe, $encoding, 1024, $true)
    $reader = New-Object System.IO.StreamReader($pipe, $encoding, $false, 1024, $true)
    $writer.AutoFlush = $true
    $writer.WriteLine('{"Action":"snapshot"}')
    $line = $reader.ReadLine()
    $reply = $line | ConvertFrom-Json
    if (-not $reply.Ok) { throw $reply.Message }
    if ($reply.Snapshot.Settings.AccessKeySecret -or $reply.Snapshot.Settings.AccessKeyId) { throw 'Credentials were exposed by API.' }
    $reply.Snapshot.Bindings | Format-Table SiteName, Protocol, Host, Port, Expires
    Write-Host 'PASS: service, named pipe, IIS enumeration, credential redaction.'
    Write-Host 'Real CA issuance, HTTPS handshake and restart/renewal acceptance still require your domain and DNS credentials.'
} finally { $pipe.Dispose() }
