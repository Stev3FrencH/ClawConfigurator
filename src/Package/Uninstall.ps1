<#
.SYNOPSIS
    Removes Claw Configurator, in the order that actually works.

.DESCRIPTION
    Two steps have to happen in this order, and the order is the opposite of the obvious one:

      1. Run the DEPLOYED helper with --uninstall. That restores every feature to its default,
         unregisters the scheduled task, and deletes the deployed copy of itself.
      2. Remove the app.

    Doing it the other way round cannot work. The deployed helper and its settings both live inside
    the package's LocalCache, so removing the app first deletes the very executable that would have
    done the restore. What survives is a scheduled task pointing at a missing file and a device left
    on whatever power limits, fan curve and charge limit were last set - with nothing installed that
    is able to change them back.

    This script exists because that is a lot to get right by hand, and because the failure is
    silent: uninstalling in the wrong order looks like it worked.

.PARAMETER BackupPath
    Where to copy the user's profile files and the final helper.log. Defaults to a timestamped
    folder on the Desktop. Removing the app deletes LocalCache, which takes hand-edited fan curves
    and lighting colours with it - they are re-seeded on the next install, so nothing breaks, but
    anything customised is gone.

.PARAMETER SkipBackup
    Do not copy anything out first.

.PARAMETER RemoveCertificate
    Also remove the CN=msi-mcenter-lite signing certificate from LocalMachine\TrustedPeople.
    Off by default: if this app is reinstalled later the certificate is needed again, and leaving
    it costs nothing but a machine that trusts one more publisher. Worth passing if you are
    removing this for good - see the note it prints.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1 -RemoveCertificate
#>
[CmdletBinding()]
param(
    [string] $BackupPath,
    [switch] $SkipBackup,
    [switch] $RemoveCertificate
)

$ErrorActionPreference = 'Stop'

# ── Re-launch under Windows PowerShell 5.1 if needed ─────────────────────────
# Same reason as Install.ps1: the Appx module is not natively available in PowerShell 7, and the
# -UseWindowsPowerShell compatibility shim behaves inconsistently for MSIX.
if ($PSVersionTable.PSVersion.Major -gt 5) {
    Write-Host "Re-launching under Windows PowerShell 5.1..." -ForegroundColor Yellow

    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($BackupPath)        { $argumentList += @('-BackupPath', "`"$BackupPath`"") }
    if ($SkipBackup)        { $argumentList += '-SkipBackup' }
    if ($RemoveCertificate) { $argumentList += '-RemoveCertificate' }

    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList $argumentList -Wait -PassThru
    exit $process.ExitCode
}

# ── Elevate if needed ─────────────────────────────────────────────────────────
# The helper's --uninstall self-elevates on its own, but doing it here means ONE prompt for the
# whole operation instead of one arriving partway through, and it lets this script unregister the
# scheduled task itself if the helper cannot.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevating..." -ForegroundColor Yellow

    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($BackupPath)        { $argumentList += @('-BackupPath', "`"$BackupPath`"") }
    if ($SkipBackup)        { $argumentList += '-SkipBackup' }
    if ($RemoveCertificate) { $argumentList += '-RemoveCertificate' }

    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argumentList -Wait -PassThru
    exit $process.ExitCode
}

# ── Find the installed package ────────────────────────────────────────────────
$package = Get-AppxPackage -Name 'ClawConfigurator' | Select-Object -First 1

if (-not $package) {
    Write-Host ""
    Write-Host "Claw Configurator is not installed for this user." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If a scheduled task or a deployed helper is still present from a failed removal,"
    Write-Host "this script cannot clean it up - the helper that performs the restore lives inside"
    Write-Host "the package that is already gone. Check for a leftover task with:"
    Write-Host ""
    Write-Host "    Get-ScheduledTask -TaskName ClawConfiguratorHelper" -ForegroundColor DarkGray
    Write-Host ""
    exit 0
}

$familyName = $package.PackageFamilyName
$localCache = Join-Path $env:LOCALAPPDATA "Packages\$familyName\LocalCache\ClawConfigurator"
$helperExe  = Join-Path $localCache 'Helper\ClawConfigurator.Helper.exe'

Write-Host ""
Write-Host "Package     : $($package.PackageFullName)"
Write-Host "Data        : $localCache"
Write-Host ""

# ── Close the widget ──────────────────────────────────────────────────────────
# THIS IS NOT TIDINESS. If the Game Bar is open, the widget re-runs its bootstrap, which redeploys
# the helper and re-registers the scheduled task - undoing step 1 while step 2 is still pending.
# The README tells a human "don't open the Game Bar between the two steps"; here we make it so.
Write-Host "Closing the widget..." -ForegroundColor Cyan
$closed = $false
Get-Process -Name 'McenterLite.Widget' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  stopping $($_.ProcessName) (pid $($_.Id))"
    try { $_.Kill(); $closed = $true } catch { Write-Warning "  could not stop pid $($_.Id): $_" }
}
if ($closed) { Start-Sleep -Milliseconds 500 }

# ── Step 1: restore defaults and tear down ────────────────────────────────────
$restored = $false

if (Test-Path $helperExe) {
    Write-Host "Restoring defaults and removing the scheduled task..." -ForegroundColor Cyan
    Write-Host "  (this writes hardware: power limits, fans, charge limit, controller mode)" -ForegroundColor DarkGray

    # Start-Process -Wait rather than the call operator: the helper is a windowed-console app and
    # invoking it directly can return before its work is finished, which would race the app removal
    # below - and losing that race deletes the helper mid-restore.
    $helperProcess = Start-Process -FilePath $helperExe -ArgumentList '--uninstall' -Wait -PassThru -NoNewWindow
    $exitCode = $helperProcess.ExitCode

    # EXIT CODE 1 IS EXPECTED HERE AND IS NOT A FAILURE.
    #
    # RunTeardown deletes the deployed helper's own directory while running from inside it, which
    # Windows refuses. It logs "Could not remove the deployed helper" and returns false, so the
    # process exits 1 - after the restore and the task removal have both already succeeded. The
    # directory then goes away with the app in step 2 regardless.
    #
    # Treating a non-zero exit as fatal here would abort a successful uninstall at the last step,
    # every single time.
    if ($exitCode -eq 0) {
        Write-Host "  defaults restored, task removed" -ForegroundColor Green
        $restored = $true
    }
    elseif ($exitCode -eq 1) {
        Write-Host "  defaults restored, task removed (the helper could not delete its own" -ForegroundColor Green
        Write-Host "   directory while running from it - expected; removing the app clears it)" -ForegroundColor DarkGray
        $restored = $true
    }
    else {
        Write-Warning "  the helper exited with code $exitCode."
        Write-Warning "  Continuing with the removal - but the device may be left on this app's"
        Write-Warning "  settings rather than the defaults. See the rescued helper.log below."
    }
}
else {
    Write-Warning "No deployed helper at:"
    Write-Warning "  $helperExe"
    Write-Warning ""
    Write-Warning "Nothing has been restored. This happens when the app was installed but the"
    Write-Warning "first-run elevation prompt was never accepted, in which case the helper never"
    Write-Warning "deployed and never changed anything - so there is nothing to put back. The app"
    Write-Warning "will still be removed."
}

# ── Rescue the log and the user's profiles ────────────────────────────────────
# ORDER MATTERS AGAIN: this has to happen after step 1 and before step 2. Removing the app deletes
# LocalCache, and helper.log lives there - so the log of the uninstall that just ran is destroyed
# by the uninstall itself, taking with it the only record of any hardware write that failed.
if (-not $SkipBackup) {
    if (-not $BackupPath) {
        $stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
        $BackupPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "ClawConfigurator-backup-$stamp"
    }

    Write-Host "Saving profiles and the final log..." -ForegroundColor Cyan

    $saved = 0
    foreach ($item in 'Lighting', 'Fan', 'Button', 'settings.json', 'helper.log') {
        $source = Join-Path $localCache $item
        if (-not (Test-Path $source)) { continue }

        try {
            New-Item -ItemType Directory -Path $BackupPath -Force | Out-Null
            Copy-Item -Path $source -Destination $BackupPath -Recurse -Force
            $saved++
        }
        catch {
            Write-Warning "  could not save ${item}: $_"
        }
    }

    if ($saved -gt 0) {
        Write-Host "  saved to $BackupPath" -ForegroundColor Green
        Write-Host "  (hand-edited fan curves and lighting colours are in there; a later install" -ForegroundColor DarkGray
        Write-Host "   seeds fresh ones, so copy these back over them if you want them again)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  nothing to save" -ForegroundColor DarkGray
    }
}

# ── Step 2: remove the app ────────────────────────────────────────────────────
Write-Host "Removing the app..." -ForegroundColor Cyan
Remove-AppxPackage -Package $package.PackageFullName
Write-Host "  removed" -ForegroundColor Green

# ── Check nothing was left behind ─────────────────────────────────────────────
# The scheduled task is the piece that matters: left behind, it points at a deleted executable and
# fails at every logon forever, which is exactly the sort of debris MSI Center M left on this
# machine and the reason any of this exists.
$leftoverTask = Get-ScheduledTask -TaskName 'ClawConfiguratorHelper' -ErrorAction SilentlyContinue
if ($leftoverTask) {
    Write-Warning "The scheduled task is still registered. Removing it directly..."
    try {
        Unregister-ScheduledTask -TaskName 'ClawConfiguratorHelper' -TaskPath $leftoverTask.TaskPath -Confirm:$false
        Write-Host "  task removed" -ForegroundColor Green
    }
    catch {
        Write-Warning "  could not remove it: $_"
        Write-Warning "  Remove it by hand in Task Scheduler under \ClawConfigurator\."
    }
}

# ── The certificate ───────────────────────────────────────────────────────────
if ($RemoveCertificate) {
    Write-Host "Removing the signing certificate..." -ForegroundColor Cyan

    $thumbprint = 'B1A696115DB9CA9B6F952306F266667DEAE1656A'
    $certificate = Get-ChildItem "Cert:\LocalMachine\TrustedPeople\$thumbprint" -ErrorAction SilentlyContinue
    if ($certificate) {
        Remove-Item "Cert:\LocalMachine\TrustedPeople\$thumbprint" -Force
        Write-Host "  removed CN=msi-mcenter-lite from LocalMachine\TrustedPeople" -ForegroundColor Green
    }
    else {
        Write-Host "  not present" -ForegroundColor DarkGray
    }
}
else {
    Write-Host ""
    Write-Host "The signing certificate was left in place." -ForegroundColor DarkGray
    Write-Host "Your machine still trusts anything signed by CN=msi-mcenter-lite. That is what" -ForegroundColor DarkGray
    Write-Host "makes reinstalling work without importing it again. To remove it now, re-run" -ForegroundColor DarkGray
    Write-Host "this script with -RemoveCertificate." -ForegroundColor DarkGray
}

Write-Host ""
if ($restored) {
    Write-Host "Done. The device is back on its default settings." -ForegroundColor Green
}
else {
    Write-Host "Done - but see the warnings above about what was not restored." -ForegroundColor Yellow
}
Write-Host ""
