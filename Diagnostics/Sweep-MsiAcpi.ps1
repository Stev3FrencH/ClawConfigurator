<#
.SYNOPSIS
    Reads every MSI_ACPI Get_* method across a range of sub-functions, and diffs snapshots taken
    at different device settings to find which byte carries a setting.

.DESCRIPTION
    Guessing one method at a time has failed repeatedly - Get_MasterBattery does not carry the
    charge limit, and three rounds of Get_EC probing were lost to harness bugs. Every Get_* method
    is read-only and most take the same Package_32, so it is cheaper to read all of them and let
    the data say which one matters.

    HOW IT FINDS THE ANSWER. Take one snapshot per setting, then diff. A byte that carries the
    setting must do two things: hold still when nothing changes, and differ when the setting does.
    Each sub-function is therefore read TWICE per snapshot, and any byte that disagrees with itself
    within a snapshot is marked unstable and excluded. That filters live telemetry automatically -
    the pack voltage under Get_MasterBattery sub-function 0x03 drifts by a millivolt or two between
    reads and would otherwise show up as a false positive in every comparison.

    READ-ONLY. Only methods named Get_* are ever called; see the guard in Get-TargetMethods. This
    cannot write to the embedded controller.

    Useful beyond the battery: the same sweep should locate the fan table for Gate G2, by
    snapshotting across MSI Center's fan settings instead.

.PARAMETER Snapshot
    Label for this capture, e.g. 100, 80, 60. Writes acpi-snapshot-<label>.json beside the script.

.PARAMETER Diff
    Compare every snapshot on disk and report only bytes that are stable within each and differ
    across them.

.PARAMETER Selectors
    Sub-function values to try in input byte 0. Default 0-7.

.PARAMETER Method
    Restrict the sweep to specific methods. Default is every Get_* the class declares.

.NOTES
    Requires elevation.

.EXAMPLE
    # In MSI Center, set the charge limit to 100, then:
    .\Sweep-MsiAcpi.ps1 -Snapshot 100
    # set it to 80, then:
    .\Sweep-MsiAcpi.ps1 -Snapshot 80
    # set it to 60, then:
    .\Sweep-MsiAcpi.ps1 -Snapshot 60
    # then let it find what moved:
    .\Sweep-MsiAcpi.ps1 -Diff
#>

[CmdletBinding(DefaultParameterSetName = 'Capture')]
param(
    [Parameter(ParameterSetName = 'Capture', Mandatory = $true)]
    [string]$Snapshot,

    [Parameter(ParameterSetName = 'Diff', Mandatory = $true)]
    [switch]$Diff,

    [Parameter(ParameterSetName = 'Capture')]
    [int[]]$Selectors = @(0, 1, 2, 3, 4, 5, 6, 7),

    [Parameter(ParameterSetName = 'Capture')]
    [string[]]$Method
)

$ErrorActionPreference = 'Stop'

$Namespace = 'root/wmi'
$AcpiClass = 'MSI_ACPI'

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) { throw 'Run this elevated. root\wmi vendor classes are not readable otherwise.' }

# ── Diff mode ───────────────────────────────────────────────────────────────

if ($Diff) {
    $files = Get-ChildItem -Path $PSScriptRoot -Filter 'acpi-snapshot-*.json' | Sort-Object Name
    if ($files.Count -lt 2) {
        throw "Need at least two snapshots to compare; found $($files.Count) in $PSScriptRoot."
    }

    $snapshots = @{}
    foreach ($file in $files) {
        $label = $file.BaseName -replace '^acpi-snapshot-', ''
        $snapshots[$label] = Get-Content $file.FullName -Raw | ConvertFrom-Json
        Write-Host "Loaded $label from $($file.Name)" -ForegroundColor DarkGray
    }

    # Numeric where the labels are numbers, so 100/80/60 do not read as "100, 60, 80" and hide
    # whether a candidate byte actually orders with the setting.
    $labels = if (@($snapshots.Keys | Where-Object { $_ -notmatch '^\d+$' }).Count -eq 0) {
        @($snapshots.Keys | Sort-Object { [int]$_ } -Descending)
    } else {
        @($snapshots.Keys | Sort-Object)
    }

    Write-Host ''
    Write-Host "=== Bytes that are STABLE within each snapshot and DIFFER across them ===" -ForegroundColor Cyan
    Write-Host "Comparing: $($labels -join ', ')" -ForegroundColor DarkGray
    Write-Host ''

    # Every snapshot records the same keys, so the first one defines what to walk.
    $reference = $snapshots[$labels[0]]
    $hits = 0

    foreach ($entry in $reference.PSObject.Properties) {
        $key = $entry.Name   # "Method|selector"

        $readsPerLabel = @{}
        $usable = $true

        foreach ($label in $labels) {
            $row = $snapshots[$label].PSObject.Properties[$key]
            if (-not $row -or -not $row.Value.ReadA -or -not $row.Value.ReadB) { $usable = $false; break }
            $readsPerLabel[$label] = $row.Value
        }
        if (-not $usable) { continue }

        $length = @($readsPerLabel[$labels[0]].ReadA).Count

        for ($index = 0; $index -lt $length; $index++) {
            # Stable within every snapshot? An unstable byte is telemetry, not a setting.
            $stable = $true
            foreach ($label in $labels) {
                if ($readsPerLabel[$label].ReadA[$index] -ne $readsPerLabel[$label].ReadB[$index]) {
                    $stable = $false; break
                }
            }
            if (-not $stable) { continue }

            $values = @($labels | ForEach-Object { $readsPerLabel[$_].ReadA[$index] })
            if (@($values | Sort-Object -Unique).Count -le 1) { continue }   # unchanged across settings

            $rendered = for ($i = 0; $i -lt $labels.Count; $i++) {
                '{0}=0x{1:X2}({1})' -f $labels[$i], $values[$i]
            }

            Write-Host ("  {0,-28} byte {1,2}   {2}" -f $key, $index, ($rendered -join '  ')) -ForegroundColor Green
            $hits++
        }
    }

    Write-Host ''
    if ($hits -eq 0) {
        Write-Host '  Nothing tracks the setting in the sweep taken.' -ForegroundColor Yellow
        Write-Host '  Widen it: -Selectors 0..31, or check the setting really changed between snapshots.' -ForegroundColor Yellow
    }
    else {
        Write-Host "  $hits candidate byte(s). Any that orders sensibly with the setting is the carrier." -ForegroundColor Green
    }
    Write-Host ''
    return
}

# ── Capture mode ────────────────────────────────────────────────────────────

try {
    $class = Get-CimClass -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop
}
catch {
    throw "$AcpiClass is not present in $Namespace - this class is MSI-specific."
}

$acpi = @(Get-CimInstance -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop) |
        Select-Object -First 1
if (-not $acpi) { throw "$AcpiClass exists but reported no instances." }

function Get-TargetMethods {
    <#
        Get_* only. This is the read-only boundary: Set_EC and friends are never returned, so
        nothing this script can reach is able to write to the controller.
    #>
    $names = if ($Method) { $Method } else { $class.CimClassMethods | ForEach-Object { $_.Name } }

    return @($names | Where-Object { $_ -like 'Get_*' } | Sort-Object)
}

function Get-InputParameters {
    param($MethodDecl)
    return @($MethodDecl.Parameters | Where-Object { $_.Qualifiers['In'] })
}

function Invoke-AcpiRead {
    param([string]$MethodName, [byte]$Selector)

    $methodDecl = $class.CimClassMethods[$MethodName]
    if (-not $methodDecl) { return $null }

    $arguments = @{}

    foreach ($p in (Get-InputParameters -MethodDecl $methodDecl)) {
        $embedded = $p.Qualifiers['EmbeddedInstance']

        if ($embedded) {
            $size = 32
            $arrayName = 'Bytes'
            try {
                $decl = Get-CimClass -Namespace $Namespace -ClassName $embedded.Value -ErrorAction Stop
                $arrayDecl = $decl.CimClassProperties | Where-Object { $_.CimType -match 'Array' } | Select-Object -First 1
                if ($arrayDecl) {
                    $arrayName = $arrayDecl.Name
                    $max = $arrayDecl.Qualifiers['MAX']
                    if ($max -and $max.Value) { $size = [int]$max.Value }
                    elseif ($embedded.Value -match '_(\d+)$') { $size = [int]$Matches[1] }
                }
            }
            catch { }

            $buffer = New-Object byte[] $size
            $buffer[0] = $Selector

            try {
                $arguments[$p.Name] = New-CimInstance -Namespace $Namespace -ClassName $embedded.Value `
                                          -Property @{ $arrayName = $buffer } -ClientOnly -ErrorAction Stop
            }
            catch { return $null }
        }
        elseif ($p.CimType -match 'Array') {
            $buffer = New-Object byte[] 32
            $buffer[0] = $Selector
            $arguments[$p.Name] = $buffer
        }
        else {
            $arguments[$p.Name] = [uint32]$Selector
        }
    }

    try {
        $result = if ($arguments.Count -gt 0) {
            Invoke-CimMethod -InputObject $acpi -MethodName $MethodName -Arguments $arguments -ErrorAction Stop
        } else {
            Invoke-CimMethod -InputObject $acpi -MethodName $MethodName -ErrorAction Stop
        }
    }
    catch { return $null }

    foreach ($prop in $result.PSObject.Properties) {
        if ($prop.Value -is [byte[]]) { return $prop.Value }
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

$targets = Get-TargetMethods
Write-Host ''
Write-Host "=== Sweeping $($targets.Count) Get_* methods x $($Selectors.Count) sub-functions ===" -ForegroundColor Cyan
Write-Host "Snapshot label: $Snapshot" -ForegroundColor DarkGray
Write-Host ''

$capture = [ordered]@{}
$captured = 0

foreach ($name in $targets) {
    $rowsForMethod = 0

    foreach ($selector in $Selectors) {
        # Twice, so a byte that cannot hold still can be told from one that carries a setting.
        $readA = Invoke-AcpiRead -MethodName $name -Selector ([byte]$selector)
        Start-Sleep -Milliseconds 120
        $readB = Invoke-AcpiRead -MethodName $name -Selector ([byte]$selector)

        if (-not $readA -or -not $readB) { continue }

        # All-zero rows carry nothing and would only pad the diff.
        if (@($readA | Where-Object { $_ -ne 0 }).Count -eq 0) { continue }

        $capture["$name|$selector"] = [ordered]@{ ReadA = $readA; ReadB = $readB }
        $rowsForMethod++
        $captured++
    }

    if ($rowsForMethod -gt 0) {
        Write-Host ("  {0,-22} {1} sub-function(s) with data" -f $name, $rowsForMethod)
    }
    else {
        Write-Host ("  {0,-22} -" -f $name) -ForegroundColor DarkGray
    }
}

$outputPath = Join-Path $PSScriptRoot "acpi-snapshot-$Snapshot.json"
$capture | ConvertTo-Json -Depth 6 | Set-Content -Path $outputPath -Encoding utf8

Write-Host ''
Write-Host "Captured $captured rows -> $(Split-Path -Leaf $outputPath)" -ForegroundColor Green
Write-Host ''
Write-Host 'Change the setting in MSI Center, take another snapshot, then compare:' -ForegroundColor Cyan
Write-Host '  .\Sweep-MsiAcpi.ps1 -Diff' -ForegroundColor Cyan
Write-Host ''
