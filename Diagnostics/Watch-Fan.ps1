<#
.SYNOPSIS
    Watches BOTH surfaces MSI Center M could be using for fan control, and diffs them across its
    own settings. Read-only.

.DESCRIPTION
    Gate G2 is blocked on a question that only observation can answer: when you change the fan in
    MSI Center M, WHERE does the change land?

    Two candidate surfaces, and this project has been fooled by the difference before - the charge
    limit and TDP both looked like registry settings and turned out to be mirrors of something the
    firmware actually owns:

      * ACPI-WMI  - MSI_ACPI.Get_Fan / Get_Thermal / Get_Temperature. If these bytes move, the
                    firmware holds the curve and there is a path that does not need MSI Center.
      * Registry  - Component\User Scenario: Fan (1=Auto, 3=Advanced) and the curve strings
                    Default_Temp / Default_Fan / High_Fan.

    Capturing them TOGETHER in one snapshot is the point. Either alone is ambiguous: registry-only
    movement means MSI Center's service is the actor and we would be writing to a mirror, while
    ACPI movement means the curve is readable and writable underneath it.

    HOW IT DECIDES. One snapshot per MSI Center setting, then diff. A byte that carries a setting
    must hold still when nothing changes and differ when the setting does, so every ACPI read is
    taken TWICE and any byte that disagrees with itself is marked unstable and excluded. That
    matters more here than it did for the battery: fan telemetry is live. Tach counts and
    temperatures drift between reads and would otherwise be a false positive in every comparison.

    READ-ONLY. Only methods named Get_* are ever called - see the guard in Get-TargetMethods - and
    the registry is exported, never written. Nothing here can change the fan.

.PARAMETER Snapshot
    Label for this capture, e.g. auto, advanced-low, advanced-high. Writes fan-snapshot-<label>.json
    beside the script.

.PARAMETER Diff
    Compare every snapshot on disk and report what moved on each surface.

.PARAMETER Selectors
    Sub-function values to try in input byte 0. Defaults to 0-2, the only ones that carry fan data.

.PARAMETER Method
    Restrict the ACPI sweep. Defaults to the four methods known to carry fan state.

.PARAMETER Wide
    Restores the original discovery sweep - six methods across sub-functions 0-7. That is roughly
    96 ACPI control-method calls per snapshot, into sub-functions the firmware may never be asked
    for in normal operation. It found the table; it is no longer the default. Use it only when
    looking for something the narrow set does not explain.

.PARAMETER EcAddresses
    Additionally read these EC addresses through Get_EC. Off by default because Get_EC's first byte
    is an ADDRESS, not a sub-function, so it does not mean the same thing as -Selectors. The
    reference project puts a fan table at block 152 (0x98), which makes 0x90..0xA0 the range worth
    asking for:  -EcAddresses (0x90..0xA0)

.NOTES
    Requires elevation. root\wmi vendor classes are not readable otherwise.

.EXAMPLE
    # In MSI Center M, set the fan to Auto, then:
    .\Watch-Fan.ps1 -Snapshot auto
    # switch to Advanced and drag the curve DOWN as far as it goes, then:
    .\Watch-Fan.ps1 -Snapshot advanced-low
    # drag the same curve UP as far as it goes, then:
    .\Watch-Fan.ps1 -Snapshot advanced-high
    # then let it find what moved:
    .\Watch-Fan.ps1 -Diff
#>

[CmdletBinding(DefaultParameterSetName = 'Capture')]
param(
    [Parameter(ParameterSetName = 'Capture', Mandatory = $true)]
    [string]$Snapshot,

    [Parameter(ParameterSetName = 'Diff', Mandatory = $true)]
    [switch]$Diff,

    [Parameter(ParameterSetName = 'Capture')]
    [int[]]$Selectors,

    [Parameter(ParameterSetName = 'Capture')]
    [string[]]$Method,

    # Restores the original discovery sweep: every fan-adjacent Get_* across sub-functions 0-7.
    [Parameter(ParameterSetName = 'Capture')]
    [switch]$Wide,

    [Parameter(ParameterSetName = 'Capture')]
    [int[]]$EcAddresses
)

$ErrorActionPreference = 'Stop'

$Namespace = 'root/wmi'
$AcpiClass = 'MSI_ACPI'
$ScenarioKey = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario'

# NARROWED 2026-08-12, after the discovery run answered the question this script was written for.
#
# The first version swept six methods across sub-functions 0-7 - about 96 ACPI control-method calls
# per snapshot, most of them into sub-functions the firmware was probably never asked for. That was
# defensible while looking for the table and is not defensible now that it has been found: these
# execute in kernel context against an undocumented implementation, and calls that return nothing
# are pure risk once they have been shown to return nothing.
#
# The device hard-locked shortly after a run on 2026-08-12. The cause was almost certainly a
# storage dropout unrelated to this - controller errors on secondary disks predate the fan work by
# four days, and no fan or thermal path can remove an NVMe device - but "almost certainly" is not a
# reason to keep sweeping blind. See docs/hardware-notes.md, Gate G2.
#
# Get_AP is not a fan method. It is kept as a control: the charge limit lives in byte 5, and it also
# carries the suspected fan-mode flag at sub-function 1 byte 1.
$NarrowMethods = @('Get_Fan', 'Get_Temperature', 'Get_Thermal', 'Get_AP')
$NarrowSelectors = @(0, 1, 2)

$WideMethods = @('Get_Fan', 'Get_Thermal', 'Get_Thermal_64', 'Get_Temperature', 'Get_Device', 'Get_AP')
$WideSelectors = @(0, 1, 2, 3, 4, 5, 6, 7)

$DefaultMethods = if ($Wide) { $WideMethods } else { $NarrowMethods }
if (-not $Selectors) { $Selectors = if ($Wide) { $WideSelectors } else { $NarrowSelectors } }

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) { throw 'Run this elevated. root\wmi vendor classes are not readable otherwise.' }

# ── Diff mode ───────────────────────────────────────────────────────────────

function Show-AcpiDiff {
    param($Snapshots, $Labels)

    Write-Host "=== ACPI-WMI: bytes STABLE within each snapshot and DIFFERENT across them ===" -ForegroundColor Cyan
    Write-Host "Comparing: $($Labels -join ', ')" -ForegroundColor DarkGray
    Write-Host ''

    $reference = $Snapshots[$Labels[0]].Acpi
    if (-not $reference) {
        Write-Host '  (no ACPI rows in the first snapshot)' -ForegroundColor Yellow
        return 0
    }

    $hits = 0

    foreach ($entry in $reference.PSObject.Properties) {
        $key = $entry.Name   # "Method|selector"

        $readsPerLabel = @{}
        $usable = $true

        foreach ($label in $Labels) {
            $row = $Snapshots[$label].Acpi.PSObject.Properties[$key]
            if (-not $row -or -not $row.Value.ReadA -or -not $row.Value.ReadB) { $usable = $false; break }
            $readsPerLabel[$label] = $row.Value
        }
        if (-not $usable) { continue }

        $length = @($readsPerLabel[$Labels[0]].ReadA).Count

        for ($index = 0; $index -lt $length; $index++) {
            $stable = $true
            foreach ($label in $Labels) {
                if ($readsPerLabel[$label].ReadA[$index] -ne $readsPerLabel[$label].ReadB[$index]) {
                    $stable = $false; break
                }
            }
            if (-not $stable) { continue }

            $values = @($Labels | ForEach-Object { $readsPerLabel[$_].ReadA[$index] })
            if (@($values | Sort-Object -Unique).Count -le 1) { continue }

            $rendered = for ($i = 0; $i -lt $Labels.Count; $i++) {
                '{0}=0x{1:X2}({1})' -f $Labels[$i], $values[$i]
            }

            Write-Host ("  {0,-26} byte {1,2}   {2}" -f $key, $index, ($rendered -join '  ')) -ForegroundColor Green
            $hits++
        }
    }

    if ($hits -eq 0) {
        Write-Host '  Nothing on this surface tracks the setting.' -ForegroundColor Yellow
    }
    Write-Host ''
    return $hits
}

function Show-RegistryDiff {
    param($Snapshots, $Labels)

    Write-Host "=== Registry: User Scenario values that DIFFER across snapshots ===" -ForegroundColor Cyan
    Write-Host ''

    $names = @()
    foreach ($label in $Labels) {
        $reg = $Snapshots[$label].Registry
        if ($reg) { $names += @($reg.PSObject.Properties.Name) }
    }
    $names = @($names | Sort-Object -Unique)

    if ($names.Count -eq 0) {
        Write-Host '  (the User Scenario key was not readable in any snapshot)' -ForegroundColor Yellow
        Write-Host ''
        return 0
    }

    $hits = 0

    foreach ($name in $names) {
        $values = @($Labels | ForEach-Object {
            $reg = $Snapshots[$_].Registry
            $prop = if ($reg) { $reg.PSObject.Properties[$name] } else { $null }
            if ($prop) { [string]$prop.Value } else { '(absent)' }
        })

        if (@($values | Sort-Object -Unique).Count -le 1) { continue }

        Write-Host ("  {0}" -f $name) -ForegroundColor Green
        for ($i = 0; $i -lt $Labels.Count; $i++) {
            Write-Host ("      {0,-16} {1}" -f $Labels[$i], $values[$i])
        }
        $hits++
    }

    if ($hits -eq 0) {
        Write-Host '  Nothing on this surface tracks the setting.' -ForegroundColor Yellow
    }
    Write-Host ''
    return $hits
}

if ($Diff) {
    $files = Get-ChildItem -Path $PSScriptRoot -Filter 'fan-snapshot-*.json' | Sort-Object Name
    if ($files.Count -lt 2) {
        throw "Need at least two snapshots to compare; found $($files.Count) in $PSScriptRoot."
    }

    $snapshots = @{}
    foreach ($file in $files) {
        $label = $file.BaseName -replace '^fan-snapshot-', ''
        $snapshots[$label] = Get-Content $file.FullName -Raw | ConvertFrom-Json
        Write-Host "Loaded $label from $($file.Name)" -ForegroundColor DarkGray
    }

    # Capture order, not alphabetical: 'auto' before 'advanced-low' says more than the reverse,
    # and these labels are words rather than the numbers Sweep-MsiAcpi.ps1 sorts.
    $labels = @($files | ForEach-Object { $_.BaseName -replace '^fan-snapshot-', '' } |
                Sort-Object { $snapshots[$_].CapturedAt })

    Write-Host ''
    $acpiHits = Show-AcpiDiff -Snapshots $snapshots -Labels $labels
    $regHits  = Show-RegistryDiff -Snapshots $snapshots -Labels $labels

    Write-Host '=== Reading this ===' -ForegroundColor Cyan
    if ($acpiHits -gt 0) {
        Write-Host '  ACPI moved. The firmware holds fan state we can read, so a path that does not' -ForegroundColor Green
        Write-Host '  need MSI Center is plausible. Check whether the moving bytes look like the' -ForegroundColor Green
        Write-Host '  six-point curve (temps rising 47..78, duties rising 70..84) or like a mode number.' -ForegroundColor Green
    }
    if ($regHits -gt 0 -and $acpiHits -eq 0) {
        Write-Host '  Registry moved but ACPI did not. On this device that has meant a MIRROR twice' -ForegroundColor Yellow
        Write-Host '  before - MSI Center''s service reads the value and applies it. Writing there would' -ForegroundColor Yellow
        Write-Host '  make fan control depend on MSI Center, which is the opposite of the point.' -ForegroundColor Yellow
        Write-Host '  Widen the sweep before concluding: -Selectors (0..31), and -EcAddresses (0x90..0xA0).' -ForegroundColor Yellow
    }
    if ($acpiHits -eq 0 -and $regHits -eq 0) {
        Write-Host '  Neither surface moved. Most likely the setting did not actually change between' -ForegroundColor Yellow
        Write-Host '  snapshots, or MSI Center defers the write until its window closes. Confirm the' -ForegroundColor Yellow
        Write-Host '  fan audibly changed, then widen: -Selectors (0..31).' -ForegroundColor Yellow
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
        Get_* only. This is the read-only boundary: Set_Fan and friends are never returned, so
        nothing this script can reach is able to write to the controller.
    #>
    $names = if ($Method) { $Method } else { $DefaultMethods }

    $declared = @($class.CimClassMethods | ForEach-Object { $_.Name })

    return @($names |
        Where-Object { $_ -like 'Get_*' } |
        Where-Object { $declared -contains $_ } |
        Sort-Object -Unique)
}

function Invoke-AcpiRead {
    param([string]$MethodName, [byte]$Selector)

    $methodDecl = $class.CimClassMethods[$MethodName]
    if (-not $methodDecl) { return $null }

    $arguments = @{}

    foreach ($p in @($methodDecl.Parameters | Where-Object { $_.Qualifiers['In'] })) {
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

function Read-ScenarioKey {
    if (-not (Test-Path $ScenarioKey)) {
        Write-Warning "Registry key not found: $ScenarioKey"
        Write-Warning '  MSI Center M may not be installed, or the key moved. The ACPI half still runs.'
        return $null
    }

    $item = Get-ItemProperty -Path $ScenarioKey
    $values = [ordered]@{}

    # Everything in the key, not just the names already known. Which value moves is exactly what
    # is being looked for, and a fan setting could land somewhere this project has not named yet -
    # 'Intelligent' and 'CurrentShiftType' are both recorded as meaning-unknown.
    foreach ($property in $item.PSObject.Properties) {
        if ($property.Name -like 'PS*') { continue }
        $values[$property.Name] = $property.Value
    }

    return $values
}

$targets = Get-TargetMethods
Write-Host ''
Write-Host "=== Fan watch: $($targets.Count) Get_* method(s) x $($Selectors.Count) sub-function(s), plus the registry ===" -ForegroundColor Cyan
Write-Host "Snapshot label: $Snapshot" -ForegroundColor DarkGray
Write-Host ''

$acpiCapture = [ordered]@{}
$captured = 0

foreach ($name in $targets) {
    $rowsForMethod = 0

    foreach ($selector in $Selectors) {
        # Twice, so live telemetry can be told from a stored setting.
        $readA = Invoke-AcpiRead -MethodName $name -Selector ([byte]$selector)
        Start-Sleep -Milliseconds 120
        $readB = Invoke-AcpiRead -MethodName $name -Selector ([byte]$selector)

        if (-not $readA -or -not $readB) { continue }
        if (@($readA | Where-Object { $_ -ne 0 }).Count -eq 0) { continue }

        $acpiCapture["$name|$selector"] = [ordered]@{ ReadA = $readA; ReadB = $readB }
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

if ($EcAddresses) {
    $ecRows = 0
    foreach ($address in $EcAddresses) {
        $readA = Invoke-AcpiRead -MethodName 'Get_EC' -Selector ([byte]$address)
        Start-Sleep -Milliseconds 120
        $readB = Invoke-AcpiRead -MethodName 'Get_EC' -Selector ([byte]$address)

        if (-not $readA -or -not $readB) { continue }

        # Unlike the sub-function sweep, an all-zero EC block is kept. Zero is a legitimate reading
        # of an address, and 'was zero, now is not' is exactly the signal wanted here.
        $acpiCapture[("Get_EC|0x{0:X2}" -f $address)] = [ordered]@{ ReadA = $readA; ReadB = $readB }
        $ecRows++
        $captured++
    }
    Write-Host ("  {0,-22} {1} address(es)" -f 'Get_EC', $ecRows)
}

$registry = Read-ScenarioKey
if ($registry) {
    Write-Host ("  {0,-22} {1} value(s)" -f 'User Scenario', $registry.Count)

    # Echoed at capture time because these are the ones with a known meaning, and seeing them go by
    # is how you notice the setting did not actually take before you take three more snapshots.
    foreach ($name in @('Fan', 'Default_Temp', 'Default_Fan', 'High_Fan')) {
        if ($registry.Contains($name)) {
            Write-Host ("      {0,-14} {1}" -f $name, $registry[$name]) -ForegroundColor DarkGray
        }
    }
}

$capture = [ordered]@{
    CapturedAt = (Get-Date).ToString('o')
    Label      = $Snapshot
    Acpi       = $acpiCapture
    Registry   = $registry
}

$outputPath = Join-Path $PSScriptRoot "fan-snapshot-$Snapshot.json"
$capture | ConvertTo-Json -Depth 8 | Set-Content -Path $outputPath -Encoding utf8

Write-Host ''
Write-Host "Captured $captured ACPI row(s) -> $(Split-Path -Leaf $outputPath)" -ForegroundColor Green
Write-Host ''
Write-Host 'Change the fan setting in MSI Center M, take another snapshot, then compare:' -ForegroundColor Cyan
Write-Host '  .\Watch-Fan.ps1 -Diff' -ForegroundColor Cyan
Write-Host ''
