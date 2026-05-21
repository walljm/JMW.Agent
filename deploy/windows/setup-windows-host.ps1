# setup-windows-host.ps1
# One-time setup so the deploy script can SSH in passwordlessly with full admin rights.
#
# Run on the Windows box in an ELEVATED PowerShell session.
#
# What it does:
#   1. Adds your SSH public key to C:\ProgramData\ssh\administrators_authorized_keys
#      (admins MUST use that file on Windows OpenSSH; ~/.ssh/authorized_keys is ignored).
#   2. Locks the file ACL to Administrators + SYSTEM (sshd refuses it otherwise).
#   3. Sets LocalAccountTokenFilterPolicy=1 so a remote local-admin login gets the full
#      (un-UAC-filtered) token, which is what we need to write to Program Files etc.
#   4. Makes sure sshd is running and set to auto-start.
#
# Usage from the Mac:
#   ssh walljm@192.168.1.60 "powershell -NoProfile -ExecutionPolicy Bypass -Command -" \
#       < deploy/windows/setup-windows-host.ps1
#
# Or copy and run interactively as Admin:
#   .\setup-windows-host.ps1 -PublicKey "ssh-ed25519 AAAA... user@host"

[CmdletBinding()]
param(
    [Parameter(HelpMessage = "Full ssh public key line, e.g. 'ssh-ed25519 AAAA... user@host'")]
    [string]$PublicKey,
    [Parameter(HelpMessage = "Path to a file containing the ssh public key (alternative to -PublicKey)")]
    [string]$PublicKeyFile = "$HOME\admin_pubkey.txt"
)

$ErrorActionPreference = 'Stop'

if (-not $PublicKey) {
    if (-not (Test-Path $PublicKeyFile)) {
        throw "no -PublicKey provided and no key file at $PublicKeyFile"
    }
    $PublicKey = (Get-Content -Raw $PublicKeyFile).Trim()
}
if (-not $PublicKey) {
    throw "public key is empty"
}

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Must run as Administrator."
}

# 1. Ensure ssh dir + admin authorized_keys file
$sshDir   = 'C:\ProgramData\ssh'
$keyFile  = Join-Path $sshDir 'administrators_authorized_keys'
New-Item -ItemType Directory -Force -Path $sshDir | Out-Null

if (-not (Test-Path $keyFile)) {
    New-Item -ItemType File -Path $keyFile | Out-Null
}

$existing = Get-Content $keyFile -ErrorAction SilentlyContinue
if ($existing -notcontains $PublicKey) {
    Add-Content -Path $keyFile -Value $PublicKey
    Write-Host "added public key to $keyFile"
} else {
    Write-Host "public key already present in $keyFile"
}

# 2. Fix ACL — sshd ignores the file unless owned by Administrators/SYSTEM with no inheritance.
icacls $keyFile /inheritance:r              | Out-Null
icacls $keyFile /grant 'Administrators:F'   | Out-Null
icacls $keyFile /grant 'SYSTEM:F'           | Out-Null
Write-Host "ACL on $keyFile locked to Administrators + SYSTEM"

# 3. Allow remote admin token (skip UAC filtering for local accounts over network logon).
$polPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
New-ItemProperty -Path $polPath -Name 'LocalAccountTokenFilterPolicy' -PropertyType DWord -Value 1 -Force | Out-Null
Write-Host "LocalAccountTokenFilterPolicy = 1"

# 4. sshd up + auto-start
Set-Service -Name sshd -StartupType Automatic
if ((Get-Service sshd).Status -ne 'Running') {
    Start-Service sshd
    Write-Host "sshd started"
} else {
    Write-Host "sshd already running"
}

# Optional: make PowerShell the default shell for ssh sessions (nicer than cmd).
$pwshPath = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path $pwshPath) {
    New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell `
        -Value $pwshPath -PropertyType String -Force | Out-Null
    Write-Host "DefaultShell -> powershell.exe"
}

Write-Host "`nDone. Test from the Mac with:  ssh $env:USERNAME@<this-host>"
