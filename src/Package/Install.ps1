<#
.SYNOPSIS
    Installs the M Center Lite Game Bar widget from a signed MSIX bundle.

.DESCRIPTION
    Deliberately minimal. This script installs the package and nothing else.

    In particular it does NOT copy the helper anywhere, and does NOT create a scheduled task.
    Those are done by the signed helper itself on first run. That split is not stylistic: a
    PowerShell script that copies an executable into LocalAppData and then registers a
    HIGHEST-privilege ONLOGON task is, behaviourally, indistinguishable from persistence malware.
    The reference project documents that exact approach being detected as
    Behavior:Win32/Persistence.A!ml and having its helper quarantined.

.PARAMETER PackagePath
    Path to the .msixbundle. Defaults to the newest one beside this script.

.PARAMETER CertificatePath
    Path to the signing certificate (.cer). Defaults to the one beside this script.

.EXAMPLE
    .\Install.ps1
#>
[CmdletBinding()]
param(
    [string] $PackagePath,
    [string] $CertificatePath
)

$ErrorActionPreference = 'Stop'

# ── Re-launch under Windows PowerShell 5.1 if needed ─────────────────────────
# The Appx module is not natively available in PowerShell 7; loading it there requires
# -UseWindowsPowerShell compatibility and still behaves inconsistently for MSIX. Simpler and more
# predictable to just run under 5.1.
if ($PSVersionTable.PSVersion.Major -gt 5) {
    Write-Host "Re-launching under Windows PowerShell 5.1..." -ForegroundColor Yellow

    $argumentList = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
    )
    if ($PackagePath) { $argumentList += @('-PackagePath', "`"$PackagePath`"") }
    if ($CertificatePath) { $argumentList += @('-CertificatePath', "`"$CertificatePath`"") }

    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList $argumentList -Wait -PassThru
    exit $process.ExitCode
}

# ── Elevate if needed ─────────────────────────────────────────────────────────
# Needed for the certificate import, not for Add-AppxPackage.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevating..." -ForegroundColor Yellow

    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($PackagePath) { $argumentList += @('-PackagePath', "`"$PackagePath`"") }
    if ($CertificatePath) { $argumentList += @('-CertificatePath', "`"$CertificatePath`"") }

    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argumentList -Wait -PassThru
    exit $process.ExitCode
}

$scriptDirectory = Split-Path -Parent $PSCommandPath

# ── Locate the package and certificate ────────────────────────────────────────
if (-not $PackagePath) {
    $candidate = Get-ChildItem -Path $scriptDirectory -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) {
        $candidate = Get-ChildItem -Path $scriptDirectory -Filter '*.msix' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $candidate) { throw "No .msixbundle or .msix found in $scriptDirectory." }
    $PackagePath = $candidate.FullName
}

if (-not $CertificatePath) {
    $candidate = Get-ChildItem -Path $scriptDirectory -Filter '*.cer' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($candidate) { $CertificatePath = $candidate.FullName }
}

Write-Host ""
Write-Host "Package     : $PackagePath"
Write-Host "Certificate : $(if ($CertificatePath) { $CertificatePath } else { '(none - assuming already trusted)' })"
Write-Host ""

# ── Trust the signing certificate ─────────────────────────────────────────────
if ($CertificatePath) {
    Write-Host "Importing the signing certificate..." -ForegroundColor Cyan

    # TrustedPeople, never Root. Sideloading only requires that the publisher be trusted for
    # this purpose; installing a self-signed certificate as a root CA would make it trusted for
    # ALL purposes on this machine, including impersonating any web site. It is also a
    # well-known EDR alert.
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Host "  imported to LocalMachine\TrustedPeople" -ForegroundColor Green
}

# ── Stop anything already running ─────────────────────────────────────────────
# The helper holds hardware handles. Letting an old build stay alive while a new one registers
# risks two versions writing the same embedded controller at once, which the reference project
# reports can hard-reset the machine (Kernel-Power 41). We have no ring-0 code, but the EC is
# still a single shared resource.
Write-Host "Stopping any running instance..." -ForegroundColor Cyan

$stopped = $false
foreach ($name in 'McenterLite.Helper', 'McenterLite.Widget') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  stopping $($_.ProcessName) (pid $($_.Id))"
        try { $_.Kill() } catch { Write-Warning "  could not stop pid $($_.Id): $_" }
        $stopped = $true
    }
}

if ($stopped) {
    # Wait for handles to actually close, not just for the processes to disappear.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $alive = Get-Process -Name 'McenterLite.Helper', 'McenterLite.Widget' -ErrorAction SilentlyContinue
        if (-not $alive) { break }
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 500
}

# ── Install ───────────────────────────────────────────────────────────────────
Write-Host "Installing..." -ForegroundColor Cyan

# -ForceUpdateFromAnyVersion permits downgrades, which matters when bisecting a regression on the
# device. -ForceApplicationShutdown closes anything still holding the old package.
Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "Installed." -ForegroundColor Green
Write-Host ""
Write-Host @"
Next steps
  1. Open the Game Bar (Win+G) and pin "M Center Lite".
  2. On first run the helper asks for elevation ONCE, to deploy itself and register its
     scheduled task. Accept it, or no hardware control will work.
  3. The widget reconnects on its own once the helper is running; this can take a few seconds
     after the prompt.

Logs
  %LOCALAPPDATA%\Packages\<package family>\LocalCache\McenterLite\helper.log

To uninstall
  Remove the app from Settings > Apps, then run the deployed helper once with --uninstall to
  remove its scheduled task and restore captured system values.
"@
