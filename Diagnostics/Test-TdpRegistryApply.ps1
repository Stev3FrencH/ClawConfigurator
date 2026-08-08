<#
.SYNOPSIS
    Determines whether writing MSI Center M's power-limit registry values ACTUALLY APPLIES them,
    or merely persists them until MSI Center next reads them.

.DESCRIPTION
    This is the single experiment that decides this project's architecture.

    Every transcript captured so far recorded a change made in MSI Center's own UI, which may
    write the registry AND separately call the ACPI-WMI methods. If the registry is only
    persistence, then writing it from our helper does nothing, and the whole "front-end for
    MSI Center" approach has to be replaced with direct MSI_ACPI calls.

    Method: hold the CPU at full load, then drive PL1 to its minimum and its maximum and watch
    whether the sustained clock follows. A power limit that is genuinely in force is visible as a
    clock plateau; one that is merely written to the registry is not.

    The oracle is '% Processor Performance', which is the current clock as a percentage of base.
    It is used instead of Win32_Processor.CurrentClockSpeed because that property is cached by
    WMI and frequently returns a stale value for the whole session.

.PARAMETER Seconds
    Seconds to hold each power level before sampling. Below about 20 the package has not settled
    and the two levels are indistinguishable. Default 30.

.PARAMETER RestoreOnly
    Skip the test and restore the values recorded in the backup file. For use if a previous run
    was interrupted before its restore step.

.NOTES
    Requires elevation (writes HKLM).

    Always restores the original values, including on Ctrl+C, via a finally block. The values it
    writes are inside the range MSI Center's own UI offers, so nothing here is outside what the
    firmware already accepts.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Test-TdpRegistryApply.ps1
#>

[CmdletBinding()]
param(
    [int]$Seconds = 30,
    [switch]$RestoreOnly
)

$ErrorActionPreference = 'Stop'

$UserScenarioKey = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario'
$BackupPath      = Join-Path $PSScriptRoot 'tdp-test-backup.json'

# Measured 2026-08-07: watts, one-to-one. MSI Center's own UI offers exactly this range.
$MinPl1 = 8;  $MinPl2 = 10
# High point is 25 W, not the 35 W ceiling. The test only has to tell the two levels apart, and
# 8 -> 25 W is still a threefold change in sustained power - no reason to run the device at its
# limit for longer than a benchmark would. PL2 tracks PL1 + 2, the pairing MSI's own UI produced.
$MaxPl1 = 25; $MaxPl2 = 27

# Mode 4 = User Scenario. See docs/hardware-notes.md. Manual power limits are only expected to be
# honoured in this mode; in AI Engine (5) and Endurance (3) MSI drives the limits itself.
$ModeUserScenario = 4

function Assert-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell (Run as administrator).'
    }
}

function Get-PowerValues {
    $k = Get-ItemProperty -Path $UserScenarioKey
    [pscustomobject]@{
        ManualPL1AC = $k.ManualPL1AC
        ManualPL2AC = $k.ManualPL2AC
        ManualPL1DC = $k.ManualPL1DC
        ManualPL2DC = $k.ManualPL2DC
        Mode        = $k.Mode
        ShiftMode   = $k.ShiftMode
    }
}

function Set-PowerLimits {
    param([int]$Pl1, [int]$Pl2)
    # Both AC and DC, because which pair is live depends on whether the charger is connected and
    # the test should not silently depend on that.
    foreach ($pair in @(@('ManualPL1AC', $Pl1), @('ManualPL2AC', $Pl2),
                        @('ManualPL1DC', $Pl1), @('ManualPL2DC', $Pl2))) {
        Set-ItemProperty -Path $UserScenarioKey -Name $pair[0] -Value $pair[1] -Type DWord
    }
}

function Start-CpuLoad {
    $count = [Environment]::ProcessorCount
    Write-Host "  starting $count load threads..." -ForegroundColor DarkGray
    1..$count | ForEach-Object {
        Start-Job -ScriptBlock {
            $x = 0.0
            while ($true) { $x = [Math]::Sqrt($x + 1.0) + [Math]::Sin($x) }
        }
    }
}

function Measure-SustainedPerformance {
    param([int]$Hold)

    # Discard the first third: the package is still ramping and those samples flatter whichever
    # level happens to be measured first.
    $settle = [Math]::Max(5, [int]($Hold / 3))
    Start-Sleep -Seconds $settle

    $samples = @()
    $deadline = (Get-Date).AddSeconds($Hold - $settle)
    while ((Get-Date) -lt $deadline) {
        try {
            $v = (Get-Counter '\Processor Information(_Total)\% Processor Performance' `
                    -ErrorAction Stop).CounterSamples[0].CookedValue
            $samples += $v
        } catch { }
        Start-Sleep -Milliseconds 900
    }

    if ($samples.Count -eq 0) { return $null }
    [pscustomobject]@{
        Mean    = [Math]::Round(($samples | Measure-Object -Average).Average, 1)
        Min     = [Math]::Round(($samples | Measure-Object -Minimum).Minimum, 1)
        Max     = [Math]::Round(($samples | Measure-Object -Maximum).Maximum, 1)
        Samples = $samples.Count
    }
}

Assert-Elevated

if ($RestoreOnly) {
    if (-not (Test-Path $BackupPath)) { throw "No backup file at $BackupPath." }
    $b = Get-Content $BackupPath -Raw | ConvertFrom-Json
    Set-PowerLimits -Pl1 $b.ManualPL1AC -Pl2 $b.ManualPL2AC
    Write-Host "Restored PL1=$($b.ManualPL1AC) PL2=$($b.ManualPL2AC)." -ForegroundColor Green
    return
}

$original = Get-PowerValues
$original | ConvertTo-Json | Set-Content -Path $BackupPath -Encoding UTF8

Write-Host ''
Write-Host '=== Original state ===' -ForegroundColor Cyan
$original | Format-List
Write-Host "  (backed up to $BackupPath)" -ForegroundColor DarkGray

if ($original.Mode -ne $ModeUserScenario) {
    Write-Warning ("Mode is $($original.Mode), not $ModeUserScenario (User Scenario). Manual power " +
                   'limits are probably ignored in this mode, which would make the result a false ' +
                   'negative. Switch MSI Center to User Scenario and re-run.')
    Write-Host ''
    $answer = Read-Host 'Continue anyway? [y/N]'
    if ($answer -ne 'y') { return }
}

$jobs = @()
try {
    Write-Host ''
    Write-Host '=== Applying load ===' -ForegroundColor Cyan
    $jobs = Start-CpuLoad

    Write-Host ''
    Write-Host "=== A: PL1=$MinPl1 W / PL2=$MinPl2 W (minimum) ===" -ForegroundColor Cyan
    Set-PowerLimits -Pl1 $MinPl1 -Pl2 $MinPl2
    $low = Measure-SustainedPerformance -Hold $Seconds
    $low | Format-List

    Write-Host "=== B: PL1=$MaxPl1 W / PL2=$MaxPl2 W (maximum) ===" -ForegroundColor Cyan
    Set-PowerLimits -Pl1 $MaxPl1 -Pl2 $MaxPl2
    $high = Measure-SustainedPerformance -Hold $Seconds
    $high | Format-List

    Write-Host ''
    Write-Host '=== Verdict ===' -ForegroundColor Cyan
    if ($null -eq $low -or $null -eq $high) {
        Write-Warning 'Could not sample the performance counter. Result inconclusive.'
    }
    else {
        $delta = [Math]::Round($high.Mean - $low.Mean, 1)
        Write-Host ("  minimum : {0}%  maximum : {1}%  delta : {2} points" -f $low.Mean, $high.Mean, $delta)
        Write-Host ''
        if ($delta -ge 10) {
            Write-Host '  APPLIES. The registry write alone changed the sustained clock, so MSI' -ForegroundColor Green
            Write-Host '  Center watches these values and pushes them to the EC. The front-end' -ForegroundColor Green
            Write-Host '  approach works.' -ForegroundColor Green
        }
        else {
            Write-Host '  DOES NOT APPLY (or the signal is too small to call). The registry is' -ForegroundColor Yellow
            Write-Host '  most likely persistence only, and TDP has to go through MSI_ACPI' -ForegroundColor Yellow
            Write-Host '  Set_Power instead. Before concluding that, re-run with -Seconds 60 and' -ForegroundColor Yellow
            Write-Host '  confirm the device was in User Scenario mode.' -ForegroundColor Yellow
        }
    }
}
finally {
    Write-Host ''
    Write-Host '=== Restoring ===' -ForegroundColor Cyan
    if ($jobs) { $jobs | Stop-Job -ErrorAction SilentlyContinue; $jobs | Remove-Job -Force -ErrorAction SilentlyContinue }
    Set-PowerLimits -Pl1 $original.ManualPL1AC -Pl2 $original.ManualPL2AC
    $after = Get-PowerValues
    Write-Host "  PL1=$($after.ManualPL1AC) PL2=$($after.ManualPL2AC) (was PL1=$($original.ManualPL1AC) PL2=$($original.ManualPL2AC))"
    if ($after.ManualPL1AC -eq $original.ManualPL1AC -and $after.ManualPL2AC -eq $original.ManualPL2AC) {
        Write-Host '  Restored.' -ForegroundColor Green
        Remove-Item $BackupPath -ErrorAction SilentlyContinue
    }
    else {
        Write-Warning "Restore did not verify. Re-run with -RestoreOnly, or set the values by hand from $BackupPath."
    }
}
