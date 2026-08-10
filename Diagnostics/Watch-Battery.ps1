<#
.SYNOPSIS
    Watches the battery to prove a charge limit is actually being enforced.

.DESCRIPTION
    Setting the limit and reading it back only proves the value was stored. What matters is
    whether charging stops, and that is a claim about the hardware that only observation settles.

    This samples charge percentage and charging state over time and reports whether charging
    stopped at the limit. Leave it running with the charger CONNECTED and the battery below the
    limit; a pass looks like the percentage climbing to the limit and then holding while
    Windows still reports AC power.

.PARAMETER Limit
    The limit you set, so the script can say whether the plateau landed in the right place.

.PARAMETER Minutes
    How long to watch. Charging the last few percent is slow; 20+ is realistic if you started
    well below the limit.

.NOTES
    Two failure modes look identical for the first minute, so give it time:
      - a limit that works    -> percentage rises, then stops at the limit, AC still connected
      - a limit that does not -> percentage rises straight past the limit

    A battery already at or above the limit proves nothing either way. Discharge below it first.

.EXAMPLE
    .\Watch-Battery.ps1 -Limit 60 -Minutes 30
#>

[CmdletBinding()]
param(
    [ValidateSet(60, 80, 100)]
    [int]$Limit = 60,

    [int]$Minutes = 30
)

$ErrorActionPreference = 'Stop'

function Get-BatteryState {
    $b = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $b) { return $null }

    # BatteryStatus is the reliable field. 1 = discharging, 2 = on AC and not charging,
    # 6/7/8/9 = charging variants. "2 while plugged in" is exactly the state a working limit
    # produces, and is also what a full battery looks like - hence checking the percentage too.
    $charging = $b.BatteryStatus -notin @(1, 2)

    [pscustomobject]@{
        Percent  = [int]$b.EstimatedChargeRemaining
        Status   = [int]$b.BatteryStatus
        Charging = $charging
        OnAc     = $b.BatteryStatus -ne 1
    }
}

$start = Get-BatteryState
if (-not $start) { throw 'No battery reported by Win32_Battery.' }

Write-Host ''
Write-Host "=== Watching for a $Limit% limit ===" -ForegroundColor Cyan
Write-Host ("  start: {0}%  onAC={1}  charging={2}" -f $start.Percent, $start.OnAc, $start.Charging)

if (-not $start.OnAc) {
    Write-Warning 'Not on AC power. Connect the charger - a discharging battery cannot show a charge limit working.'
}
if ($start.Percent -ge $Limit) {
    Write-Warning ("Battery is already at $($start.Percent)%, at or above the $Limit% limit. " +
                   'This run cannot distinguish a working limit from a full battery. Discharge below the limit first.')
}

Write-Host ''
$deadline = (Get-Date).AddMinutes($Minutes)
$peak = $start.Percent
$exceeded = $false
$plateauSamples = 0

while ((Get-Date) -lt $deadline) {
    $s = Get-BatteryState
    if ($s) {
        if ($s.Percent -gt $peak) { $peak = $s.Percent; $plateauSamples = 0 } else { $plateauSamples++ }
        if ($s.Percent -gt $Limit + 1) { $exceeded = $true }

        $note = ''
        if ($s.Percent -ge $Limit -and -not $s.Charging -and $s.OnAc) { $note = '  <- holding at the limit' }

        Write-Host ("  {0}  {1,3}%  status={2}  charging={3}{4}" -f `
            (Get-Date -Format 'HH:mm:ss'), $s.Percent, $s.Status, $s.Charging, $note)
    }
    Start-Sleep -Seconds 30
}

$end = Get-BatteryState

Write-Host ''
Write-Host '=== Verdict ===' -ForegroundColor Cyan
Write-Host ("  start {0}%   peak {1}%   end {2}%" -f $start.Percent, $peak, $end.Percent)

if ($exceeded) {
    Write-Host "  NOT ENFORCED. The battery charged past $Limit%, so the stored value is not" -ForegroundColor Red
    Write-Host '  reaching the controller. The registry write is persistence only, and the' -ForegroundColor Red
    Write-Host '  charge limit needs MSI_ACPI.Set_MasterBattery instead.' -ForegroundColor Red
}
elseif ($peak -ge $Limit -and -not $end.Charging -and $end.OnAc) {
    Write-Host "  ENFORCED. Charging stopped at $peak% with AC still connected." -ForegroundColor Green
}
elseif ($end.Percent -lt $Limit -and $end.Charging) {
    Write-Host '  INCONCLUSIVE - still charging and below the limit. Run for longer.' -ForegroundColor Yellow
}
else {
    Write-Host '  INCONCLUSIVE. Check the charger was connected and the battery started below the limit.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Then reboot and re-check: the limit is meant to persist in the controller.' -ForegroundColor DarkGray
