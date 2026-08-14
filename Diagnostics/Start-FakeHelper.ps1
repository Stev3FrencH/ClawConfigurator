<#
.SYNOPSIS
    Runs the helper against simulated hardware, so the widget's cards can be seen on a machine
    that is not a Claw.

.DESCRIPTION
    On any non-Claw machine the device gate correctly reports "not an MSI Claw 8 EX AI+" and hides
    every hardware card - the widget shows only Windows power. That is right in production and
    useless for looking at layout.

    This starts the helper with --fake-hardware, which reports a simulated Claw 8 EX, so all four
    cards unhide and the whole UI can be exercised on the development machine.

    Note the fake layer deliberately keeps the REAL power provider: CPU boost and the OS power mode
    genuinely work here, because they are plain Win32 and need no MSI hardware. Only the
    MSI-specific parts are simulated.

    THE ORDER MATTERS. The helper holds a single-instance mutex so two builds can never drive the
    same controller at once. This script stops the real helper first and claims that mutex, so the
    widget's bootstrap - which tries to start the real one when the widget opens - finds it taken
    and exits instead of fighting.

    The scheduled task's previous state is restored on exit, including on Ctrl+C.

.NOTES
    Requires elevation - the helper's pipe ACL and the scheduled-task changes both need it.

    Leave this window open while using the widget. Ctrl+C stops the fake helper and re-enables the
    real one.

    To see the UNSUPPORTED-device case instead, run the helper with no arguments on this machine:
    the real gate rejects it and every hardware card stays hidden. There is no flag to simulate
    that, because the machine already is one.

.EXAMPLE
    .\Start-FakeHelper.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$TaskPath = '\ClawConfigurator\'
$TaskName = 'ClawConfiguratorHelper'

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    throw 'Run this elevated. The helper needs it for the pipe ACL and the scheduled task.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repoRoot 'src\Helper\bin\x64\Debug\net8.0-windows\ClawConfigurator.Helper.exe'

if (-not (Test-Path $helper)) {
    throw "Helper not built at $helper - run:  dotnet build McenterLite.sln"
}

# Warn when the installed widget predates the helper about to serve it. Retired Function ordinals
# mean an old widget and a new helper do not agree on the wire, and the symptom is a card that
# silently never populates rather than an error.
$package = Get-AppxPackage -Name ClawConfigurator -ErrorAction SilentlyContinue
$helperBuilt = (Get-Item $helper).LastWriteTime
if ($package) {
    Write-Host "Installed widget package: $($package.Version)" -ForegroundColor DarkGray
} else {
    Write-Warning 'No Claw Configurator package is installed. Install one first, or there is no widget to connect.'
}
Write-Host "Helper build: $($helperBuilt.ToString('yyyy-MM-dd HH:mm'))" -ForegroundColor DarkGray

$task = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
$wasEnabled = $task -and $task.State -ne 'Disabled'
$fake = $null

try {
    if ($task) {
        Write-Host 'Stopping the real helper so the fake one can hold the mutex...' -ForegroundColor Cyan
        try { Stop-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop } catch { }
        try { Disable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop | Out-Null } catch { }
    }

    Get-Process McenterLite.Helper -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | Stop-Process -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 500

    Write-Host ''
    Write-Host '=== Helper running against a simulated Claw 8 EX ===' -ForegroundColor Green
    Write-Host 'Open the Game Bar (Win+G) and the widget will connect to this process.' -ForegroundColor Green
    Write-Host 'All four cards should appear: Power limits, Controller, Windows power, Graphics.' -ForegroundColor DarkGray
    Write-Host 'CPU boost and OS power mode are REAL here - the fake layer keeps the Win32 provider.' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host 'Ctrl+C to stop and put the real helper back.' -ForegroundColor Cyan
    Write-Host ''

    # Start-Process -PassThru, not "& $helper". Invoked with the call operator the helper is a
    # child this script cannot address, so Ctrl+C returns control here - running the restore below
    # - while leaving the helper alive and still holding the build output locked. Keeping the
    # handle lets the finally guarantee it dies.
    $fake = Start-Process -FilePath $helper -ArgumentList '--fake-hardware' -PassThru -NoNewWindow
    $fake.WaitForExit()
}
finally {
    if ($fake -and -not $fake.HasExited) {
        Write-Host ''
        Write-Host 'Stopping the fake helper...' -ForegroundColor Cyan
        try { $fake.Kill(); $fake.WaitForExit(5000) } catch { }
    }

    Write-Host ''
    if ($wasEnabled) {
        Write-Host 'Re-enabling the real helper task...' -ForegroundColor Cyan
        try {
            Enable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop | Out-Null
            Start-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop
            Write-Host 'Restored.' -ForegroundColor Green
        }
        catch {
            Write-Warning ("Could not restore the task: $($_.Exception.Message). " +
                           "Re-enable it with:  Enable-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName'")
        }
    }
    elseif ($task) {
        Write-Host 'The task was already disabled before this ran; leaving it that way.' -ForegroundColor DarkGray
    }
}
