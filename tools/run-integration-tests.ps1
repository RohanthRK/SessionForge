<#
.SYNOPSIS
  Runs the SSH/SFTP/forwarding integration tests against a throwaway local OpenSSH server.

.DESCRIPTION
  Downloads Microsoft's portable Win32-OpenSSH (signature-checked) into a work folder, generates a host key and a
  client key, starts sshd on 127.0.0.1:<Port> as the current user (no admin rights, no service installation),
  runs the tests tagged "Integration", then stops sshd. Nothing is installed system-wide.

.EXAMPLE
  ./tools/run-integration-tests.ps1
#>
param(
    [string]$WorkDir = (Join-Path $env:TEMP "sessionforge-it"),
    [int]$Port = 2222,
    [string]$OpenSshUrl = "https://github.com/PowerShell/Win32-OpenSSH/releases/download/10.0.0.0p2-Preview/OpenSSH-Win64.zip"
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$zip = Join-Path $WorkDir 'openssh.zip'
$bin = Join-Path $WorkDir 'OpenSSH-Win64'
if (-not (Test-Path (Join-Path $bin 'sshd.exe'))) {
    Invoke-WebRequest -UseBasicParsing $OpenSshUrl -OutFile $zip
    Expand-Archive $zip -DestinationPath $WorkDir -Force
}
$sig = Get-AuthenticodeSignature (Join-Path $bin 'sshd.exe')
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike '*O=Microsoft Corporation*') {
    throw "sshd.exe signature check failed: $($sig.Status) $($sig.SignerCertificate.Subject)"
}

$cfgDir = Join-Path $WorkDir 'config'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
foreach ($k in 'hostkey', 'client') {
    $path = Join-Path $cfgDir $k
    if (-not (Test-Path $path)) { & (Join-Path $bin 'ssh-keygen.exe') -q -t ed25519 -f $path -N '""' -C "sf-it-$k" | Out-Null }
}
$authKeys = Join-Path $cfgDir 'authorized_keys'
Copy-Item (Join-Path $cfgDir 'client.pub') $authKeys -Force
foreach ($f in (Join-Path $cfgDir 'hostkey'), (Join-Path $cfgDir 'client'), $authKeys) {
    icacls $f /inheritance:r /grant:r "${env:USERNAME}:F" | Out-Null
}
$config = @"
Port $Port
ListenAddress 127.0.0.1
HostKey $cfgDir\hostkey
AuthorizedKeysFile $authKeys
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
StrictModes no
AllowTcpForwarding yes
Subsystem sftp $bin\sftp-server.exe
PidFile $cfgDir\sshd.pid
"@
[IO.File]::WriteAllText((Join-Path $cfgDir 'sshd_config'), $config)

if (Get-Process sshd -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$WorkDir*" }) {
    Get-Process sshd | Where-Object { $_.Path -like "$WorkDir*" } | Stop-Process -Force   # stale instance from a previous run
}
$sshd = Start-Process (Join-Path $bin 'sshd.exe') -ArgumentList @('-D', '-e', '-f', "`"$cfgDir\sshd_config`"") `
    -RedirectStandardError (Join-Path $cfgDir 'sshd.log') -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    if ($sshd.HasExited) { throw "sshd exited: $(Get-Content (Join-Path $cfgDir 'sshd.log') -Raw)" }
    $env:SF_SSH_HOST = '127.0.0.1'
    $env:SF_SSH_PORT = "$Port"
    $env:SF_SSH_USER = $env:USERNAME
    $env:SF_SSH_KEY = Join-Path $cfgDir 'client'
    $env:SF_SSH_AUTHKEYS = $authKeys
    $repo = Split-Path -Parent $PSScriptRoot
    dotnet test (Join-Path $repo 'tests\SessionForge.Tests') --filter "FullyQualifiedName~Integration" --logger "console;verbosity=normal"
}
finally {
    if (-not $sshd.HasExited) { Stop-Process -Id $sshd.Id -Force }
}
