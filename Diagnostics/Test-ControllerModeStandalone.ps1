<#
.SYNOPSIS
    Answers the last open question in gate G5: does the PHYSICAL MSI button still switch controller
    mode when MSI Center M is not running?

.DESCRIPTION
    The vendor HID channel is already proven in both directions without MSI Center M
    (--controller-mode reads, --set-controller-mode writes). What is NOT yet known is who handles
    the physical button:

        FIRMWARE owns it -> the button keeps working with MSI Center M gone, and the helper only
                            has to LISTEN so the widget shows the right state. Nothing to build
                            beyond a reader.

        MSI CENTER owns it -> the button goes dead once MSI Center M is uninstalled, and the helper
                            has to detect the press and issue the 0x24 switch itself. Considerably
                            more work, and it has to run all the time rather than only while the
                            widget is open.

    A first attempt at this test was inconclusive, and the way it failed is worth knowing:
    MSI_Center_M_Server_ControlMode was killed on its own and came straight back, because
    MSI_Center_M_Server respawns its children. MSI_Center_M_Server is in turn a SCHEDULED TASK, not
    a service - so stopping the service is not enough either. This script stops the task, which is
    what actually makes the stack stay down, and verifies it before trusting a single result.

.NOTES
    Restores MSI Center M on the way out, including on Ctrl+C. Nothing is uninstalled and nothing
    is disabled permanently - the task is stopped, then started again.

    Needs elevation to stop the task. The HID probing itself does NOT need elevation, which is
    itself a finding: unlike the ACPI-WMI TDP path, this channel opens from a normal user process.

.EXAMPLE
    .\Test-ControllerModeStandalone.ps1

.EXAMPLE
    .\Test-ControllerModeStandalone.ps1 -Seconds 90
#>

[CmdletBinding()]
param(
    [int]$Seconds = 60,
    [switch]$KeepMsiCenterStopped
)

$ErrorActionPreference = 'Stop'

# ── Elevate if needed ─────────────────────────────────────────────────────────
# Only the task stop/start needs this. The probe runs fine unelevated.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevating..." -ForegroundColor Yellow

    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                      '-Seconds', $Seconds)
    if ($KeepMsiCenterStopped) { $argumentList += '-KeepMsiCenterStopped' }

    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argumentList -Wait -PassThru
    exit $process.ExitCode
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
$probe = Join-Path $scriptDirectory '..\src\Probe\bin\x64\Debug\net8.0-windows\McenterLite.Probe.exe'
$probe = [System.IO.Path]::GetFullPath($probe)

if (-not (Test-Path $probe)) {
    Write-Error "Probe not built. Run: dotnet build src\Probe\McenterLite.Probe.csproj -c Debug -p:Platform=x64"
    exit 1
}

$TaskName = 'MSI_Center_M_Server'

# Elevation opens a NEW console window, so whatever this prints is gone the moment it closes.
# A transcript is the only way the run is reviewable afterwards.
$LogPath = Join-Path $env:TEMP 'controller-mode-standalone.log'
try { Start-Transcript -Path $LogPath -Force | Out-Null } catch { }

function Get-MsiProcesses {
    Get-Process | Where-Object { $_.ProcessName -like 'MSI_Center_M*' }
}

function Get-ModeName {
    param([string]$Hex)

    switch ($Hex) {
        '01' { 'XInput' }
        '02' { 'DirectInput' }
        '04' { 'Desktop' }
        default { "0x$Hex" }
    }
}

function Read-Mode {
    $output = & $probe --controller-mode 2>&1
    return ($output | Out-String).Trim()
}

function Stop-MsiCenter {
    Write-Host 'Stopping MSI Center M...' -ForegroundColor Yellow

    # The task first. Killing the processes while the task still runs is what made the first
    # attempt at this test useless.
    try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch {
        Write-Warning "Could not stop scheduled task ${TaskName}: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 1

    foreach ($p in Get-MsiProcesses) {
        # Children exit with their supervisor, so by the time we get here most are already gone.
        # That is the expected case, not a failure worth warning about.
        if ($null -eq (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { continue }

        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {
            Write-Warning "Could not stop $($p.ProcessName) ($($p.Id)): $($_.Exception.Message)"
        }
    }

    # Give the supervisor a chance to respawn anything, so we notice if it does.
    Start-Sleep -Seconds 3
}

function Restore-MsiCenter {
    Write-Host ''
    Write-Host 'Restarting MSI Center M...' -ForegroundColor Yellow
    try { Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch {
        Write-Warning "Could not restart ${TaskName}: $($_.Exception.Message)"
        Write-Warning 'Start it from Task Scheduler, or reboot.'
    }
}

try {
    Write-Host ''
    Write-Host '=== Before ===' -ForegroundColor Cyan
    Write-Host "MSI processes running: $((Get-MsiProcesses).Count)"
    Write-Host "Mode via HID         : $(Read-Mode)"
    Write-Host ''

    Stop-MsiCenter

    $remaining = Get-MsiProcesses
    Write-Host ''
    Write-Host '=== With MSI Center M stopped ===' -ForegroundColor Cyan

    if ($remaining.Count -gt 0) {
        Write-Host "STILL RUNNING - this run proves nothing:" -ForegroundColor Red
        $remaining | Select-Object Id, ProcessName | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Something is respawning them. Do not trust a result from this state.' -ForegroundColor Red
        return
    }

    Write-Host 'No MSI Center M processes running. Good.' -ForegroundColor Green

    $modeBefore = Read-Mode
    Write-Host "Mode via HID         : $modeBefore"

    if ($modeBefore -notlike '*Controller mode*') {
        Write-Host ''
        Write-Host 'NOTE: the HID query failed with MSI Center M stopped.' -ForegroundColor Red
        Write-Host 'That would mean the channel itself depends on MSI Center M - report it as such.'
    }

    Write-Host ''
    Write-Host '>>> PRESS THE PHYSICAL MSI BUTTON two or three times now. <<<' -ForegroundColor Yellow
    Write-Host ''

    # Collect while still printing live, so the tester can see frames land as they press.
    $watchOutput = & $probe --hid-watch $Seconds 2>&1 | ForEach-Object { Write-Host $_; $_ }

    $modeAfter = Read-Mode

    # The verdict comes from the 0x27 announcements, NOT from comparing before against after.
    # An even number of presses lands back where it started, and a before/after comparison reads
    # that as "nothing happened" - which is exactly how this script drew the wrong conclusion on
    # 2026-08-12 while the button was demonstrably working.
    $announced = $watchOutput |
        Select-String -Pattern '3C 27 ([0-9A-Fa-f]{2})' |
        ForEach-Object { $_.Matches[0].Groups[1].Value.ToUpperInvariant() }

    Write-Host ''
    Write-Host '=== Result ===' -ForegroundColor Cyan
    Write-Host "Mode before presses  : $modeBefore"
    Write-Host "Mode after presses   : $modeAfter"
    Write-Host "Announcements (0x27) : $(if ($announced) { ($announced | ForEach-Object { Get-ModeName $_ }) -join ' -> ' } else { '(none)' })"
    Write-Host ''

    if ($announced.Count -gt 0) {
        Write-Host 'THE BUTTON WORKS WITHOUT MSI CENTER M.' -ForegroundColor Green
        Write-Host 'The firmware owns the button. The helper only needs to LISTEN for 0x27 so the'
        Write-Host 'widget follows it - there is no press to intercept and no switch to re-issue.'

        $distinct = $announced | Select-Object -Unique
        if ($distinct -contains '02') {
            Write-Host ''
            Write-Host 'NOTE: DirectInput (0x02) was announced. The button is a three-way cycle, and' -ForegroundColor Yellow
            Write-Host 'IHwMouseProvider''s boolean cannot represent that.' -ForegroundColor Yellow
        }
    }
    else {
        Write-Host 'No mode announcement arrived.' -ForegroundColor Red
        Write-Host 'If you definitely pressed the button, MSI Center M was doing the switching, and'
        Write-Host 'the helper will have to detect the press and send 0x24 itself.'
    }
}
finally {
    if ($KeepMsiCenterStopped) {
        Write-Host ''
        Write-Host 'Leaving MSI Center M stopped as asked. Start-ScheduledTask -TaskName MSI_Center_M_Server' -ForegroundColor Yellow
    }
    else {
        Restore-MsiCenter
    }

    Write-Host ''
    Write-Host "Transcript: $LogPath" -ForegroundColor DarkGray
    try { Stop-Transcript | Out-Null } catch { }

    if ($Host.Name -eq 'ConsoleHost') {
        Write-Host 'Press Enter to close.' -ForegroundColor DarkGray
        try { [void](Read-Host) } catch { }
    }
}

