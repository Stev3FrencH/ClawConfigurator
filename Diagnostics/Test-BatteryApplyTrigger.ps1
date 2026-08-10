<#
.SYNOPSIS
    Finds what makes MSI Center apply a BatteryLevel value that our helper wrote.

.DESCRIPTION
    Writing BatteryLevel from our helper stores the value and MSI Center's UI reflects it, but
    charging behaviour does not change. Changing the SAME value in MSI Center's own UI does work.
    So MSI Center does something beyond writing the registry - see Gate G3 in
    docs/hardware-notes.md.

    Before reaching for the hardware interfaces, this checks the cheap possibility: that MSI
    Center's battery server applies the stored value on some TRIGGER we are simply not hitting.
    If so, the fix is to fire that trigger after our write, and no firmware write is needed at all.

    Triggers, in order of how invasive they are:

      1. Nothing        - the baseline. Known to fail; run it to confirm the setup is sound.
      2. AC transition  - unplug and replug. EC charge thresholds are commonly re-evaluated when
                          the charger is attached, and this costs nothing to try.
      3. MSI Center UI  - open its window. Its UI may push the value on load.
      4. Server restart - kill MSI_Center_M_Server_Battery and let the launcher respawn it. This
                          is a PROCESS, not a service, so it is the most invasive option here.

    THE ORACLE. The registry reading back correctly proves nothing - it already does that. What
    matters is whether charging actually stops or starts, so this reports the real charging state
    from Win32_Battery before and after.

.PARAMETER Limit
    The limit to write. 100 means "no limit" - the off state, since the device stores a
    three-state selector rather than a percentage.

.PARAMETER Trigger
    Which trigger to fire after writing. 'None' is the baseline.

.PARAMETER Restore
    Skip the test and put back the value recorded in the backup file.

.NOTES
    Requires elevation (writes HKLM).

    Writes nothing to firmware. The only value touched is MSI Center's own registry entry, inside
    the range its UI offers, and the original is saved beside this script for -Restore.

    To see a limit take effect, the battery must be ABOVE the limit and the charger connected -
    then a working limit shows up as charging stopping. To see one release, set 100 while below
    full and watch charging resume.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Test-BatteryApplyTrigger.ps1 -Limit 60 -Trigger None

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Test-BatteryApplyTrigger.ps1 -Limit 60 -Trigger AcCycle
#>

[CmdletBinding()]
param(
    [ValidateSet(60, 80, 100)]
    [int]$Limit = 60,

    [ValidateSet('None', 'AcCycle', 'MsiCenterUi', 'RestartServer')]
    [string]$Trigger = 'None',

    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$KeyPath     = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery'
$ValueName   = 'BatteryLevel'
$ServerName  = 'MSI_Center_M_Server_Battery'
$BackupFile  = Join-Path $PSScriptRoot 'battery-level-backup.txt'

# MSI stores a three-state selector, and the numbering runs OPPOSITE to the percentage.
# Measured on device 2026-08-07. A higher stored number means a lower limit.
$ToMsiLevel   = @{ 100 = '0'; 80 = '1'; 60 = '2' }
$FromMsiLevel = @{ '0' = 100; '1' = 80; '2' = 60 }

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-BatteryState {
    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $battery) { return $null }

    # BatteryStatus: 1 = discharging, 2 = on AC and NOT charging, 6/7/8/9 = charging variants.
    # "2 while plugged in" is exactly what a working limit produces - and also what a full
    # battery looks like, hence reporting the percentage alongside it.
    [pscustomobject]@{
        Percent  = [int]$battery.EstimatedChargeRemaining
        Status   = [int]$battery.BatteryStatus
        Charging = $battery.BatteryStatus -notin @(1, 2)
        OnAc     = $battery.BatteryStatus -ne 1
    }
}

function Show-State {
    param([string]$Label)

    $state = Get-BatteryState
    if (-not $state) {
        Write-Host "  $Label : no battery reported"
        return $null
    }

    $stored = (Get-ItemProperty -Path $KeyPath -Name $ValueName -ErrorAction SilentlyContinue).$ValueName
    $asPercent = if ($stored -ne $null -and $FromMsiLevel.ContainsKey($stored)) {
        "$($FromMsiLevel[$stored])%"
    } else { "raw '$stored'" }

    Write-Host ("  {0,-22} {1,3}%  charging={2,-5}  onAC={3,-5}  stored={4}" -f
        $Label, $state.Percent, $state.Charging, $state.OnAc, $asPercent)

    return $state
}

if (-not (Test-Elevated)) { throw 'Run this elevated - it writes HKLM.' }
if (-not (Test-Path $KeyPath)) { throw "MSI Center's battery key is not present: $KeyPath" }

# ── Restore ─────────────────────────────────────────────────────────────────

if ($Restore) {
    if (-not (Test-Path $BackupFile)) { throw "No backup file at $BackupFile" }

    $saved = (Get-Content $BackupFile -Raw).Trim()
    Set-ItemProperty -Path $KeyPath -Name $ValueName -Value $saved -Type String
    Write-Host "Restored $ValueName to '$saved'." -ForegroundColor Green
    return
}

Write-Host ''
Write-Host '=== What makes MSI Center apply a written BatteryLevel? ===' -ForegroundColor Cyan
Write-Host ''

$original = (Get-ItemProperty -Path $KeyPath -Name $ValueName -ErrorAction Stop).$ValueName
if (-not (Test-Path $BackupFile)) {
    Set-Content -Path $BackupFile -Value $original -Encoding utf8
    Write-Host "Saved original $ValueName='$original' to $(Split-Path -Leaf $BackupFile)" -ForegroundColor DarkGray
}

$start = Show-State -Label 'before'
if (-not $start) { throw 'No battery reported by Win32_Battery.' }

if (-not $start.OnAc) {
    Write-Warning 'Not on AC power. Connect the charger - a discharging battery cannot show a limit working.'
}

# ── Write ───────────────────────────────────────────────────────────────────

$level = $ToMsiLevel[$Limit]
Write-Host ''
Write-Host "Writing $ValueName = '$level'  (${Limit}%)" -ForegroundColor Yellow
Set-ItemProperty -Path $KeyPath -Name $ValueName -Value $level -Type String

Start-Sleep -Seconds 3
$null = Show-State -Label 'after write'

# ── Trigger ─────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host "Trigger: $Trigger" -ForegroundColor Yellow

switch ($Trigger) {
    'None' {
        Write-Host '  Baseline - nothing fired. This is the case already known to fail.' -ForegroundColor DarkGray
    }

    'AcCycle' {
        Write-Host '  UNPLUG the charger now. Waiting 20 seconds...' -ForegroundColor Cyan
        Start-Sleep -Seconds 20
        $null = Show-State -Label 'unplugged'

        Write-Host '  REPLUG the charger now. Waiting 20 seconds...' -ForegroundColor Cyan
        Start-Sleep -Seconds 20
    }

    'MsiCenterUi' {
        Write-Host '  Open MSI Center M''s window now, then leave it open. Waiting 25 seconds...' -ForegroundColor Cyan
        Start-Sleep -Seconds 25
    }

    'RestartServer' {
        $server = Get-Process -Name $ServerName -ErrorAction SilentlyContinue
        if (-not $server) {
            Write-Warning "$ServerName is not running, so there is nothing to restart."
        }
        else {
            Write-Host "  Stopping $ServerName (pid $($server.Id))..." -ForegroundColor Cyan
            Stop-Process -Id $server.Id -Force
            Start-Sleep -Seconds 10

            $respawned = Get-Process -Name $ServerName -ErrorAction SilentlyContinue
            if ($respawned) {
                Write-Host "  Respawned as pid $($respawned.Id)." -ForegroundColor Green
            }
            else {
                Write-Warning ("$ServerName did not come back on its own. Restart MSI Center M " +
                               '(or reboot) to restore it - nothing here is permanent.')
            }
            Start-Sleep -Seconds 10
        }
    }
}

# ── Verdict ─────────────────────────────────────────────────────────────────

Write-Host ''
$end = Show-State -Label 'after trigger'

Write-Host ''
Write-Host '=== Reading this ===' -ForegroundColor Cyan

if ($end -and $end.OnAc -and $end.Percent -gt $Limit -and -not $end.Charging) {
    Write-Host "  APPLIED. Charging is stopped at $($end.Percent)% with the charger connected" -ForegroundColor Green
    Write-Host "  and a ${Limit}% limit written. This trigger is what MSI Center needs." -ForegroundColor Green
}
elseif ($end -and $end.OnAc -and $end.Percent -lt $Limit -and $end.Charging) {
    Write-Host "  INCONCLUSIVE - below the limit and charging, which is correct either way." -ForegroundColor Yellow
    Write-Host "  Charge above ${Limit}% first, or test the release direction with -Limit 100." -ForegroundColor Yellow
}
elseif ($end -and $end.Charging -and $end.Percent -gt $Limit) {
    Write-Host "  NOT APPLIED. Still charging at $($end.Percent)% despite a ${Limit}% limit." -ForegroundColor Red
    Write-Host '  This trigger is not the missing piece.' -ForegroundColor Red
}
else {
    Write-Host '  INCONCLUSIVE. Check the charger is connected and compare the rows above.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Other triggers to try:  -Trigger AcCycle | MsiCenterUi | RestartServer' -ForegroundColor DarkGray
Write-Host "Put the original value back with:  .\$(Split-Path -Leaf $PSCommandPath) -Restore" -ForegroundColor DarkGray
Write-Host ''
