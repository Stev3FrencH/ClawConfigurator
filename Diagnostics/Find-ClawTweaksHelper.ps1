<#
.SYNOPSIS
    Locates the installed ClawTweaks helper so its hardware protocol can be studied.

.DESCRIPTION
    ClawTweaks' hardware layer is not in its public source repository - the helper project was
    deleted from the tree and the MSI-specific version was never published. But a working
    ClawTweaks install puts a compiled copy of that helper on this machine, and it is a .NET
    assembly.

    That turns Phase 0 from blind reverse-engineering into reading. This script finds the binary
    and reports what is worth extracting from it.

    IMPORTANT - what may and may not come out of this:
      * Hardware FACTS (WMI class and method names, EC register offsets, byte-table layouts,
        HID report formats) are not copyrightable. Record them in docs/hardware-notes.md and
        implement against them freely.
      * The decompiled SOURCE must not be pasted into this repository in any form. ClawTweaks is
        AGPLv3; copying its code would make this project a derivative work and force the same
        licence. See LICENSE-NOTES.md.

    Read-only. Changes nothing.

.EXAMPLE
    .\Find-ClawTweaksHelper.ps1
    .\Find-ClawTweaksHelper.ps1 -CopyTo C:\decompile
#>
[CmdletBinding()]
param(
    # Copy the helper folder here for offline inspection. Keep this OUTSIDE the git repo.
    [string] $CopyTo
)

$ErrorActionPreference = 'Continue'

Write-Host "=== Locating the ClawTweaks helper ===" -ForegroundColor Cyan
Write-Host ""

# The helper deploys itself out of the MSIX into LocalCache, because running it from the
# read-only package location cannot survive an update. Search both.
$searchRoots = @(
    (Join-Path $env:LOCALAPPDATA 'Packages'),
    (Join-Path $env:ProgramFiles 'WindowsApps')
) | Where-Object { $_ -and (Test-Path $_) }

$found = @()
foreach ($root in $searchRoots) {
    Write-Host "Searching $root ..." -ForegroundColor DarkGray
    $hits = Get-ChildItem -Path $root -Recurse -Filter '*GamingBarHelper*.exe' -ErrorAction SilentlyContinue
    if ($hits) { $found += $hits }
}

if (-not $found) {
    Write-Warning "No ClawTweaks helper found."
    Write-Host ""
    Write-Host "If ClawTweaks is installed and working, it may use a different executable name."
    Write-Host "Try locating it by its running process instead:"
    Write-Host "    Get-Process | Where-Object Path -match 'Helper' | Select-Object Name, Path"
    Write-Host ""
    Write-Host "Or by its scheduled task:"
    Write-Host "    Get-ScheduledTask | Where-Object TaskPath -match 'Claw|GoTweaks'"
    exit 3
}

Write-Host ""
Write-Host "Found $($found.Count) candidate(s):" -ForegroundColor Green
foreach ($file in $found) {
    Write-Host ""
    Write-Host "  $($file.FullName)"
    Write-Host "    size     : $([math]::Round($file.Length / 1KB, 1)) KB"
    Write-Host "    version  : $($file.VersionInfo.FileVersion)"
    Write-Host "    modified : $($file.LastWriteTime)"
}

Write-Host ""
Write-Host "=== Scheduled tasks ===" -ForegroundColor Cyan
Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskPath -match 'Claw|GoTweaks|Tweaks' } |
    ForEach-Object {
        [pscustomobject]@{
            Task  = "$($_.TaskPath)$($_.TaskName)"
            State = $_.State
            Runs  = ($_.Actions | ForEach-Object { $_.Execute }) -join '; '
        }
    } | Format-List

Write-Host "=== Running processes ===" -ForegroundColor Cyan
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'GamingBar|Claw|MSI' } |
    Select-Object ProcessName, Id, @{n = 'Path'; e = { $_.Path } } |
    Format-Table -AutoSize

if ($CopyTo) {
    $source = Split-Path -Parent $found[0].FullName
    Write-Host ""
    Write-Host "Copying $source -> $CopyTo" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $CopyTo | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $CopyTo -Recurse -Force
    Write-Host "Done. Keep this folder OUT of the git repository." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== What to extract ===" -ForegroundColor Cyan
Write-Host @"
Open the assembly with ILSpy (ilspycmd runs on macOS too) or dnSpy and look for:

  MsiClawFanController      BuildFanTable / SetFanTable
                            -> fan WMI class + method, the 8-byte table, the duty scale
  MsiClawLedController      LedEffectMode
                            -> LED HID report bytes, zone and mode ids
  ClawButtonMonitor         and the desktop-mode forwarder
                            -> the firmware mouse-mode report
  MSIClawModels             Resolve
                            -> the model catalogue and per-model capabilities
  the TDP backend           -> whether it uses kx.exe MCHBAR (ring 0, out of scope for us)
                               or the HKLM 'User Scenario\ManualPL*' registry mirror.
                               THIS ANSWERS GATE G1. Check it first.
  the charge-limit writer   -> the WMI/ACPI method and its parameter encoding

Then grep the assembly for these strings:
  root\WMI   AckSysCtl   MSI_   Set_Fan   Adv_Fan   Set_Thermal   User Scenario   0DB0

Record findings as FACTS in docs/hardware-notes.md. Do not copy code.
"@
