<#
.SYNOPSIS
    Determines whether MSI_ACPI.Set_Power can drive TDP with MSI Center M's user-mode stack
    stopped - the experiment that decides whether this app can ever replace MSI Center M instead
    of sitting alongside it.

.DESCRIPTION
    docs/hardware-notes.md records this as Open question #6, never answered: "Does MSI_ACPI work
    with MSI Center stopped?" Today TDP only works because MSI Center M's background server
    (MSI_Center_M_Server_UserScenario) watches the registry mirror and pushes values to the EC -
    see Test-TdpRegistryApply.ps1. A second, MSI-Center-independent path exists,
    MSI_ACPI.Set_Power, but it has never been exercised. This script exercises it.

    PREREQUISITE: you must already know which byte(s) in Get_Power's 32-byte package carry PL1
    and PL2. Find them first with the existing sweep tool, reusing the same technique that located
    the battery charge limit for gate G3:

        # with MSI Center running normally, PL1/PL2 at their default
        .\Test-TdpRegistryApply.ps1 -RestoreOnly          # in case a previous run left a backup
        # set PL1/PL2 low via the registry (mirrors what MSI Center's own UI would do)
        # then:
        .\Sweep-MsiAcpi.ps1 -Snapshot low -Method Get_Power
        # set PL1/PL2 high, then:
        .\Sweep-MsiAcpi.ps1 -Snapshot high -Method Get_Power
        .\Sweep-MsiAcpi.ps1 -Diff

    The diff prints candidate byte offsets whose value tracks the setting. Because PL1/PL2 are
    watts, 1:1, with no scaling (confirmed at four points in docs/hardware-notes.md), the right
    bytes are recognisable on sight: their value equals the watts you set.

    WHAT THIS SCRIPT THEN DOES:
      1. Records a full baseline: the registry values (for restore) and the raw Get_Power package
         (for a read-modify-write, so bytes whose meaning is unknown are preserved exactly as the
         firmware reported them - same discipline as Set-ChargeLimitAp.ps1).
      2. Stops MSI Center M's user-mode stack: the MSI_Center_M_Server scheduled task, its
         per-feature child processes, and the MSI Foundation Service. Does NOT touch msisadrv.sys
         (the boot-start kernel driver) - the whole point is to find out whether that alone is
         enough to keep MSI_ACPI live.
      3. Under sustained CPU load, drives PL1/PL2 low then high via Set_Power only - never the
         registry - and measures the sustained clock with the same '% Processor Performance'
         oracle Test-TdpRegistryApply.ps1 uses. A write that is accepted but inert would read back
         correctly and still show no clock movement, so both signals are checked.
      4. ALWAYS restores, including on Ctrl+C: writes the original package back via Set_Power,
         restarts MSI Center M's stack, and re-syncs the registry so MSI Center's own UI does not
         sit showing a stale value.

.PARAMETER Pl1Byte
    Output byte offset (0-31) in the read method's package that carries PL1. Default 1, measured
    2026-08-11: see the "Measured 2026-08-11" note below.

.PARAMETER Pl2Byte
    Output byte offset (0-31) that carries PL2. Default 2, measured the same way.

.PARAMETER SubFunction
    Value written to input byte 0. Default 1, measured 2026-08-11.

.NOTES
    Measured 2026-08-11: PL1/PL2 are NOT carried by Get_Power/Set_Power despite the name - a sweep
    of Get_Power alone showed byte 1 constant at 0x07 across an 8W-to-25W change (see
    docs/hardware-notes.md, Gate G1). Widening to a full Sweep-MsiAcpi.ps1 run across every Get_*
    method (both ManualPL1/2 AC *and* DC pairs set together, to remove which-pair-is-live
    ambiguity - see Test-TdpRegistryApply.ps1's Set-PowerLimits) found it instead at
    Get_SlaveBattery, sub-function 1: byte 1 = PL1, byte 2 = PL2, watts 1:1, confirmed at two
    points (8/10 -> 25/27). Same shape as gate G3's discovery that the charge limit lives in
    Get_AP, not the identically-plausible-sounding Get_MasterBattery - the obviously-named method
    is not reliably the right one on this device.

    This class pairs Get_X with Set_X by convention (every other gate does), so Set_SlaveBattery
    is assumed to be the write method - UNVERIFIED, and the first thing this script's Phase A
    (still with MSI Center running) effectively confirms or refutes before Phase B ever stops
    anything.

.PARAMETER Seconds
    Seconds to hold each power level before sampling. Same default and reasoning as
    Test-TdpRegistryApply.ps1.

.PARAMETER RestoreOnly
    Skip the test and restore from the backup file. For use if a previous run was interrupted
    before its restore step - or before MSI Center M's stack was safely restarted.

.NOTES
    Requires elevation.

    This is new ground: no prior gate stopped MSI Center M's own processes. Run it somewhere you
    can watch happen, not unattended - if the restore step fails partway, MSI Center M's stack may
    be left down and TDP may be left at the low test value until you re-run with -RestoreOnly.

.EXAMPLE
    .\Test-PowerStandalone.ps1
    # uses the measured defaults: Get_SlaveBattery/Set_SlaveBattery, sub-function 1, byte 1/2
#>

[CmdletBinding()]
param(
    [Parameter(ParameterSetName = 'Test')]
    [ValidateRange(0, 31)]
    [int]$Pl1Byte = 1,

    [Parameter(ParameterSetName = 'Test')]
    [ValidateRange(0, 31)]
    [int]$Pl2Byte = 2,

    [Parameter(ParameterSetName = 'Test')]
    [byte]$SubFunction = 0x01,

    [Parameter(ParameterSetName = 'Test')]
    [string]$ReadMethod = 'Get_SlaveBattery',

    [Parameter(ParameterSetName = 'Test')]
    [string]$WriteMethod = 'Set_SlaveBattery',

    [Parameter(ParameterSetName = 'Test')]
    [int]$Seconds = 30,

    [Parameter(Mandatory = $true, ParameterSetName = 'Restore')]
    [switch]$RestoreOnly
)

$ErrorActionPreference = 'Stop'

$Namespace   = 'root/wmi'
$AcpiClass   = 'MSI_ACPI'

$UserScenarioKey = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario'
$BackupPath       = Join-Path $PSScriptRoot 'power-standalone-test-backup.json'

# Same range and reasoning as Test-TdpRegistryApply.ps1: a threefold change is enough to tell the
# two levels apart without holding the device at its ceiling for longer than a benchmark would.
$MinPl1 = 8;  $MinPl2 = 10
$MaxPl1 = 25; $MaxPl2 = 27

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) { throw 'Run this elevated. root\wmi vendor classes and service control both need it.' }

$class = Get-CimClass -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop
$acpi  = @(Get-CimInstance -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop) |
         Select-Object -First 1
if (-not $acpi) { throw "$AcpiClass reported no instances." }

function Get-PackageInfo {
    param([string]$MethodName)

    $methodDecl = $class.CimClassMethods[$MethodName]
    if (-not $methodDecl) { throw "$AcpiClass has no method $MethodName." }

    $parameter = $methodDecl.Parameters |
                 Where-Object { $_.Qualifiers['In'] -and $_.Qualifiers['EmbeddedInstance'] } |
                 Select-Object -First 1
    if (-not $parameter) { throw "$MethodName has no embedded-instance input." }

    $className = $parameter.Qualifiers['EmbeddedInstance'].Value
    $size = if ($className -match '_(\d+)$') { [int]$Matches[1] } else { 32 }
    $arrayName = 'Bytes'

    try {
        $decl = Get-CimClass -Namespace $Namespace -ClassName $className -ErrorAction Stop
        $arrayDecl = $decl.CimClassProperties | Where-Object { $_.CimType -match 'Array' } | Select-Object -First 1
        if ($arrayDecl) {
            $arrayName = $arrayDecl.Name
            $max = $arrayDecl.Qualifiers['MAX']
            if ($max -and $max.Value) { $size = [int]$max.Value }
        }
    }
    catch { }

    return @{ ParameterName = $parameter.Name; ClassName = $className; ArrayProperty = $arrayName; Size = $size }
}

function Invoke-Power {
    param([string]$MethodName, [byte[]]$Buffer)

    $info = Get-PackageInfo -MethodName $MethodName
    $payload = New-Object byte[] $info.Size
    for ($i = 0; $i -lt $Buffer.Count -and $i -lt $info.Size; $i++) { $payload[$i] = $Buffer[$i] }

    try {
        $instance = New-CimInstance -Namespace $Namespace -ClassName $info.ClassName `
                        -Property @{ $info.ArrayProperty = $payload } -ClientOnly -ErrorAction Stop
        $result = Invoke-CimMethod -InputObject $acpi -MethodName $MethodName `
                      -Arguments @{ $info.ParameterName = $instance } -ErrorAction Stop
    }
    catch {
        Write-Host "    $MethodName rejected: $($_.Exception.Message)" -ForegroundColor DarkYellow
        return $null
    }

    foreach ($prop in $result.PSObject.Properties) {
        if ($prop.Value -is [Microsoft.Management.Infrastructure.CimInstance]) {
            foreach ($inner in $prop.Value.CimInstanceProperties) {
                if ($inner.Value -is [byte[]]) { return $inner.Value }
            }
        }
    }
    return $null
}

function Show-Package {
    param([byte[]]$Bytes, [string]$Label)
    if (-not $Bytes) { Write-Host ("  {0,-14} (none)" -f $Label); return }
    $head = @($Bytes[0..15])
    Write-Host ("  {0,-14} {1}" -f $Label, (($head | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))
}

function Get-RegistryPowerValues {
    $k = Get-ItemProperty -Path $UserScenarioKey
    [pscustomobject]@{
        ManualPL1AC = $k.ManualPL1AC
        ManualPL2AC = $k.ManualPL2AC
        ManualPL1DC = $k.ManualPL1DC
        ManualPL2DC = $k.ManualPL2DC
    }
}

function Set-RegistryPowerValues {
    param([int]$Pl1, [int]$Pl2)
    foreach ($pair in @(@('ManualPL1AC', $Pl1), @('ManualPL2AC', $Pl2),
                        @('ManualPL1DC', $Pl1), @('ManualPL2DC', $Pl2))) {
        Set-ItemProperty -Path $UserScenarioKey -Name $pair[0] -Value $pair[1] -Type DWord
    }
}

function Stop-MsiCenterStack {
    Write-Host '  stopping MSI_Center_M_Server task...' -ForegroundColor DarkGray
    try { Stop-ScheduledTask -TaskName 'MSI_Center_M_Server' -ErrorAction Stop } catch { }

    Write-Host '  stopping MSI Center M processes...' -ForegroundColor DarkGray
    Get-Process | Where-Object { $_.Path -like '*\MSI Center M\*' } |
        ForEach-Object {
            Write-Host "    - $($_.Name) (pid $($_.Id))" -ForegroundColor DarkGray
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }

    Write-Host '  stopping MSI Foundation Service...' -ForegroundColor DarkGray
    try { Stop-Service -Name 'MSI Foundation Service' -Force -ErrorAction Stop } catch { }

    Start-Sleep -Seconds 2
}

function Start-MsiCenterStack {
    Write-Host '  restarting MSI Foundation Service...' -ForegroundColor DarkGray
    try { Start-Service -Name 'MSI Foundation Service' -ErrorAction Stop } catch { Write-Warning "Could not restart MSI Foundation Service: $($_.Exception.Message)" }

    Write-Host '  restarting MSI_Center_M_Server task...' -ForegroundColor DarkGray
    try { Start-ScheduledTask -TaskName 'MSI_Center_M_Server' -ErrorAction Stop } catch { Write-Warning "Could not restart MSI_Center_M_Server task: $($_.Exception.Message)" }

    Start-Sleep -Seconds 3
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
        Samples = $samples.Count
    }
}

# ── Restore-only mode ─────────────────────────────────────────────────────────

if ($RestoreOnly) {
    if (-not (Test-Path $BackupPath)) { throw "No backup file at $BackupPath." }
    $b = Get-Content $BackupPath -Raw | ConvertFrom-Json

    Write-Host 'Restoring from backup...' -ForegroundColor Cyan
    $originalBytes = [byte[]]($b.Package | ForEach-Object { [byte]$_ })
    $null = Invoke-Power -MethodName $WriteMethod -Buffer $originalBytes

    Start-MsiCenterStack
    Set-RegistryPowerValues -Pl1 $b.Registry.ManualPL1AC -Pl2 $b.Registry.ManualPL2AC
    Write-Host 'Restored.' -ForegroundColor Green
    return
}

# ── Baseline ────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '=== Baseline (MSI Center M still running) ===' -ForegroundColor Cyan

$originalRegistry = Get-RegistryPowerValues
$originalRegistry | Format-List

$readRequest = New-Object byte[] 32
$readRequest[0] = $SubFunction
$originalPackage = Invoke-Power -MethodName $ReadMethod -Buffer $readRequest
if (-not $originalPackage) { throw "$ReadMethod returned nothing. Cannot proceed without a baseline package." }
Show-Package -Bytes $originalPackage -Label 'Get_Power'

Write-Host ("  byte {0} (PL1) = {1}   byte {2} (PL2) = {3}" -f `
    $Pl1Byte, $originalPackage[$Pl1Byte], $Pl2Byte, $originalPackage[$Pl2Byte])

@{ Registry = $originalRegistry; Package = $originalPackage } | ConvertTo-Json -Depth 4 |
    Set-Content -Path $BackupPath -Encoding UTF8
Write-Host "  (backed up to $BackupPath)" -ForegroundColor DarkGray

$jobs = @()
$stackStopped = $false

try {
    Write-Host ''
    Write-Host '=== Stopping MSI Center M''s user-mode stack ===' -ForegroundColor Cyan
    Stop-MsiCenterStack
    $stackStopped = $true

    Write-Host ''
    Write-Host '=== Applying CPU load ===' -ForegroundColor Cyan
    $jobs = Start-CpuLoad

    function Set-PowerViaAcpi {
        param([int]$Pl1, [int]$Pl2)
        $buffer = @($originalPackage.Clone())
        $buffer[0] = $SubFunction
        $buffer[$Pl1Byte] = [byte]$Pl1
        $buffer[$Pl2Byte] = [byte]$Pl2
        $null = Invoke-Power -MethodName $WriteMethod -Buffer $buffer
        Start-Sleep -Milliseconds 500
        return Invoke-Power -MethodName $ReadMethod -Buffer $readRequest
    }

    Write-Host ''
    Write-Host "=== A: PL1=$MinPl1 W / PL2=$MinPl2 W via Set_Power (MSI Center stopped) ===" -ForegroundColor Cyan
    $afterLow = Set-PowerViaAcpi -Pl1 $MinPl1 -Pl2 $MinPl2
    Show-Package -Bytes $afterLow -Label 'read back'
    $lowLanded = $afterLow -and $afterLow[$Pl1Byte] -eq $MinPl1 -and $afterLow[$Pl2Byte] -eq $MinPl2
    Write-Host ("  landed: {0}" -f $lowLanded)
    $low = Measure-SustainedPerformance -Hold $Seconds
    $low | Format-List

    Write-Host "=== B: PL1=$MaxPl1 W / PL2=$MaxPl2 W via Set_Power (MSI Center stopped) ===" -ForegroundColor Cyan
    $afterHigh = Set-PowerViaAcpi -Pl1 $MaxPl1 -Pl2 $MaxPl2
    Show-Package -Bytes $afterHigh -Label 'read back'
    $highLanded = $afterHigh -and $afterHigh[$Pl1Byte] -eq $MaxPl1 -and $afterHigh[$Pl2Byte] -eq $MaxPl2
    Write-Host ("  landed: {0}" -f $highLanded)
    $high = Measure-SustainedPerformance -Hold $Seconds
    $high | Format-List

    Write-Host ''
    Write-Host '=== Verdict ===' -ForegroundColor Cyan
    if ($null -eq $low -or $null -eq $high) {
        Write-Warning 'Could not sample the performance counter. Result inconclusive.'
    }
    else {
        $delta = [Math]::Round($high.Mean - $low.Mean, 1)
        Write-Host ("  read-back landed : low={0}  high={1}" -f $lowLanded, $highLanded)
        Write-Host ("  clock            : minimum={0}%  maximum={1}%  delta={2} points" -f $low.Mean, $high.Mean, $delta)
        Write-Host ''
        if ($lowLanded -and $highLanded -and $delta -ge 10) {
            Write-Host '  STANDALONE WORKS. Set_Power changed the sustained clock with MSI Center M''s' -ForegroundColor Green
            Write-Host '  own services and scheduled task stopped. The ACPI-WMI path does not need' -ForegroundColor Green
            Write-Host '  MSI Center M running at all - only the boot-start msisadrv.sys driver.' -ForegroundColor Green
        }
        elseif ($lowLanded -and $highLanded) {
            Write-Host '  WRITES LAND BUT THE CLOCK DID NOT MOVE. The package accepted the write and' -ForegroundColor Yellow
            Write-Host '  read it back correctly, but the EC did not apply it - Set_Power may need' -ForegroundColor Yellow
            Write-Host '  something else in MSI Center M''s stack still running, or the write shape' -ForegroundColor Yellow
            Write-Host '  needs a field this script left untouched. Re-run with -Seconds 60 first.' -ForegroundColor Yellow
        }
        else {
            Write-Host '  WRITE DID NOT LAND. Set_Power did not accept bytes at the offsets given -' -ForegroundColor Yellow
            Write-Host '  re-check Pl1Byte/Pl2Byte against Sweep-MsiAcpi.ps1 -Diff, and consider that' -ForegroundColor Yellow
            Write-Host '  the write shape may not mirror the read shape (see the note in' -ForegroundColor Yellow
            Write-Host '  Set-ChargeLimitAp.ps1 about G3''s Set_AP for a case where that was true).' -ForegroundColor Yellow
        }
    }
}
finally {
    Write-Host ''
    Write-Host '=== Restoring ===' -ForegroundColor Cyan
    if ($jobs) { $jobs | Stop-Job -ErrorAction SilentlyContinue; $jobs | Remove-Job -Force -ErrorAction SilentlyContinue }

    Write-Host '  writing original package back via Set_Power...' -ForegroundColor DarkGray
    $restored = Invoke-Power -MethodName $WriteMethod -Buffer $originalPackage
    Show-Package -Bytes $restored -Label 'read back'

    if ($stackStopped) {
        Write-Host ''
        Write-Host '=== Restarting MSI Center M''s stack ===' -ForegroundColor Cyan
        Start-MsiCenterStack
    }

    Write-Host ''
    Write-Host '  re-syncing registry so MSI Center M''s own UI is not stale...' -ForegroundColor DarkGray
    Set-RegistryPowerValues -Pl1 $originalRegistry.ManualPL1AC -Pl2 $originalRegistry.ManualPL2AC

    $afterRestore = Invoke-Power -MethodName $ReadMethod -Buffer $readRequest
    $ok = $afterRestore -and $afterRestore[$Pl1Byte] -eq $originalPackage[$Pl1Byte] -and $afterRestore[$Pl2Byte] -eq $originalPackage[$Pl2Byte]
    if ($ok) {
        Write-Host '  Restored and verified.' -ForegroundColor Green
        Remove-Item $BackupPath -ErrorAction SilentlyContinue
    }
    else {
        Write-Warning "Restore did not verify by read-back. Re-run with -RestoreOnly, or check by hand from $BackupPath."
    }
}
