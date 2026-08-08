<#
.SYNOPSIS
    Decodes how MSI's ACPI-WMI interface carries the battery charge limit, and optionally sets it.

.DESCRIPTION
    The registry path does not enforce the charge limit. Measured on device 2026-08-08: setting
    MSI Center's BatteryLevel value through our helper and through MSI Center's own UI reads back
    identical either way, but only the MSI-Center-driven change actually stops or resumes charging.
    So the registry is a display mirror and something else is the apply path. See Gate G3 in
    docs/hardware-notes.md.

    MSI_ACPI is the only driver-free alternative - Windows itself has no charge-threshold API, and
    never has. This script reads that interface so the encoding can be established before anything
    is written to it.

    Needs no compiled tool. WMI is native to PowerShell, so this does the same job as
    McenterLite.Probe.exe --battery without transferring a 68 MB binary to the device.

    THE METHOD SIGNATURES ARE NOT KNOWN AHEAD OF TIME. These methods are generated from the DSDT
    method each one fronts, so this script dumps their declared shape first and then tries several
    argument shapes against the READ method, reporting each attempt. Learning the shape is the
    point; a failed attempt is data, not an error, so no single failure stops the run.

    WORKING HYPOTHESIS, NOT YET CONFIRMED ON THIS DEVICE: the threshold byte is
    'percent -bor 0x80', where bit 7 is an enable/commit flag and bits 0-6 are the percentage.
    That comes from the msi-ec Linux driver, which documents MSI firmware's EC layout - a property
    of the embedded controller, independent of the operating system. It has not been measured on
    the Claw, which is a handheld rather than one of the laptops that driver covers.

        60% -> 0xBC (188)    80% -> 0xD0 (208)    100% -> 0xE4 (228)

.PARAMETER SetLimit
    Write a charge limit, via MSI_ACPI.Set_MasterBattery and no other method. Omit to read only.

    Reads back before and after so the operation self-verifies. A changed read-back still only
    proves the value landed - use Watch-Battery.ps1 to prove charging actually stops.

.NOTES
    Requires elevation.

    READ-ONLY unless -SetLimit is given.

    Deliberately does NOT expose MSI_ACPI.Set_EC, which writes a raw byte to an arbitrary embedded
    controller address. A wrong address there reaches fan or thermal registers on real firmware.
    Set_MasterBattery is purpose-built, so the firmware validates the value instead.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Test-BatteryWmi.ps1

    Read only. Run this three times, setting MSI Center's own charge limit to 100, then 80, then
    60 in between. Whichever value tracks those three is the threshold.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Test-BatteryWmi.ps1 -SetLimit 60
#>

[CmdletBinding()]
param(
    [ValidateSet(60, 80, 100)]
    [int]$SetLimit
)

$ErrorActionPreference = 'Stop'

$Namespace   = 'root/wmi'
$AcpiClass   = 'MSI_ACPI'
$ReadMethod  = 'Get_MasterBattery'
$WriteMethod = 'Set_MasterBattery'

# Bit 7 marks the threshold active; bits 0-6 carry the percentage.
$EnableBit = 0x80

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Bytes {
    param([byte[]]$Bytes, [string]$Indent = '    ')

    if (-not $Bytes -or $Bytes.Count -eq 0) {
        Write-Host "$Indent(empty)"
        return
    }

    # Indexed, because finding WHICH index carries the value is the entire point.
    for ($offset = 0; $offset -lt $Bytes.Count; $offset += 16) {
        $end = [Math]::Min($offset + 15, $Bytes.Count - 1)
        $slice = @($Bytes[$offset..$end])

        $hex = ($slice | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        $dec = ($slice | ForEach-Object { '{0,3}' -f $_ }) -join ' '

        Write-Host ("{0}[{1,2}] {2}" -f $Indent, $offset, $hex)
        Write-Host ("{0}     {1}" -f $Indent, $dec) -ForegroundColor DarkGray
    }
}

function Show-CimResult {
    param($Result, [string]$Indent = '      ')

    if ($null -eq $Result) {
        Write-Host "$Indent(nothing returned)"
        return
    }

    foreach ($prop in $Result.CimInstanceProperties) {
        if ($prop.Value -is [byte[]]) {
            Write-Host "$Indent$($prop.Name) = byte[$($prop.Value.Count)]"
            Show-Bytes -Bytes $prop.Value -Indent "$Indent  "
        }
        elseif ($null -ne $prop.Value -and $prop.Value.GetType().IsValueType) {
            Write-Host ("{0}{1} = 0x{2:X} ({2})" -f $Indent, $prop.Name, $prop.Value)
        }
        else {
            Write-Host "$Indent$($prop.Name) = $($prop.Value)"
        }
    }
}

# ── Preconditions ───────────────────────────────────────────────────────────

if (-not (Test-Elevated)) {
    throw 'Run this elevated. root\wmi vendor classes are not readable otherwise.'
}

Write-Host ''
Write-Host '=== MSI battery charge limit over ACPI-WMI ===' -ForegroundColor Cyan
Write-Host ''

# ── 1. MSI_Master_Battery - the zero-risk read ──────────────────────────────
#
# A plain WMI class with a readable property, so it needs no method call at all. If its value
# tracks MSI Center's setting, the encoding is decoded without invoking anything.

Write-Host '[1] MSI_Master_Battery (property read, no method call)' -ForegroundColor Yellow
try {
    foreach ($batteryInstance in @(Get-CimInstance -Namespace $Namespace -ClassName 'MSI_Master_Battery' -ErrorAction Stop)) {
        foreach ($prop in $batteryInstance.CimInstanceProperties) {
            if ($prop.Value -is [byte[]]) {
                Write-Host "    $($prop.Name) = byte[$($prop.Value.Count)]"
                Show-Bytes -Bytes $prop.Value -Indent '      '
            }
            elseif ($null -ne $prop.Value -and $prop.Value.GetType().IsValueType) {
                Write-Host ("    {0} = 0x{1:X} ({1})" -f $prop.Name, $prop.Value)
            }
            else {
                Write-Host "    $($prop.Name) = $($prop.Value)"
            }
        }
    }
}
catch {
    Write-Warning "Could not read MSI_Master_Battery: $($_.Exception.Message)"
}

Write-Host ''

# ── 2. MSI_ACPI method signatures ───────────────────────────────────────────
#
# Printed BEFORE anything is called, and with every qualifier, because when an invocation fails
# this is the output that explains why.

Write-Host "[2] $AcpiClass declared method shapes (nothing called)" -ForegroundColor Yellow

try {
    $class = Get-CimClass -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop
}
catch {
    throw "$AcpiClass is not present in $Namespace. This class is MSI-specific - " +
          "confirm with: Get-CimClass -Namespace $Namespace -ClassName MSI_*"
}

foreach ($name in @($ReadMethod, $WriteMethod)) {
    $method = $class.CimClassMethods[$name]
    if (-not $method) {
        Write-Warning "$AcpiClass has no method named $name."
        continue
    }

    Write-Host "    $name  (returns $($method.ReturnType))"

    if ($method.Parameters.Count -eq 0) {
        Write-Host '      (no declared parameters)'
        continue
    }

    foreach ($p in $method.Parameters) {
        $quals = ($p.Qualifiers | ForEach-Object { $_.Name }) -join ','
        Write-Host "      $($p.Name) : $($p.CimType)   [$quals]"
    }
}

Write-Host ''

# ── 3. Call the READ method, trying each plausible argument shape ───────────

$acpi = @(Get-CimInstance -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop) |
        Select-Object -First 1

if (-not $acpi) { throw "$AcpiClass exists but reported no instances." }

function Invoke-Attempt {
    <#
        One invocation attempt. Returns the result, or $null with the reason printed.
        Never throws: a failed shape is a measurement, not an error.
    #>
    param([string]$MethodName, $Arguments, [string]$Label)

    try {
        $result = if ($Arguments -and $Arguments.Count -gt 0) {
            Invoke-CimMethod -InputObject $acpi -MethodName $MethodName -Arguments $Arguments -ErrorAction Stop
        }
        else {
            Invoke-CimMethod -InputObject $acpi -MethodName $MethodName -ErrorAction Stop
        }

        Write-Host "    [$Label] OK" -ForegroundColor Green
        Show-CimResult -Result $result
        return $result
    }
    catch {
        Write-Host "    [$Label] $($_.Exception.Message)" -ForegroundColor DarkYellow
        return $null
    }
}

function Get-InputParameters {
    <#
        Strictly those marked [in]. Anything not explicitly an input is left alone - assigning to
        an output parameter is what produced the InstanceHandle cast error this replaces.
    #>
    param($Method)
    return @($Method.Parameters | Where-Object { $_.Qualifiers['In'] })
}

function Get-ArgumentShapes {
    <#
        Candidate argument sets for a method whose shape is not yet known, most likely first.
        Instance/Reference parameters are skipped rather than synthesised - they cannot be
        conjured from a byte payload, and guessing produced the original failure.
    #>
    param($Method, [byte[]]$Payload)

    $shapes = @()
    $inputs = Get-InputParameters -Method $Method

    if ($inputs.Count -eq 0) {
        return @(, @{ Label = 'no arguments'; Arguments = $null })
    }

    $unsupported = @($inputs | Where-Object { $_.CimType -match 'Instance|Reference' })
    if ($unsupported.Count -gt 0) {
        Write-Host ("    note: skipping parameter(s) of type Instance/Reference: {0}" -f
            (($unsupported | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor DarkGray
    }

    $usable = @($inputs | Where-Object { $_.CimType -notmatch 'Instance|Reference' })

    foreach ($size in 32, 8, 4) {
        # NOT $args - that is an automatic variable in PowerShell functions.
        $argSet = @{}
        $applies = $false

        foreach ($p in $usable) {
            if ($p.CimType -match 'Array') {
                $buffer = New-Object byte[] $size
                for ($i = 0; $i -lt $Payload.Count -and $i -lt $size; $i++) { $buffer[$i] = $Payload[$i] }
                $argSet[$p.Name] = $buffer
                $applies = $true
            }
            else {
                $argSet[$p.Name] = if ($Payload.Count -gt 0) { [uint32]$Payload[0] } else { [uint32]0 }
            }
        }

        if ($argSet.Count -gt 0) { $shapes += @{ Label = "buffer $size"; Arguments = $argSet } }
        if (-not $applies) { break }   # no array parameter, so buffer size is irrelevant
    }

    # Last resort: some ACPI methods accept a bare call despite declaring inputs.
    $shapes += @{ Label = 'no arguments'; Arguments = $null }

    return $shapes
}

function Invoke-AcpiMethod {
    param([string]$MethodName, [byte[]]$Payload = @())

    $method = $class.CimClassMethods[$MethodName]
    if (-not $method) {
        Write-Warning "$AcpiClass has no method named $MethodName."
        return $null
    }

    foreach ($shape in Get-ArgumentShapes -Method $method -Payload $Payload) {
        $result = Invoke-Attempt -MethodName $MethodName -Arguments $shape.Arguments -Label $shape.Label
        if ($null -ne $result) {
            Write-Host "    -> shape that worked: $($shape.Label)" -ForegroundColor Green
            return $result
        }
    }

    Write-Warning "Every argument shape tried for $MethodName failed. The declared shape is in [2] above."
    return $null
}

Write-Host "[3] $AcpiClass.$ReadMethod" -ForegroundColor Yellow
$before = Invoke-AcpiMethod -MethodName $ReadMethod

Write-Host ''
Write-Host 'Expected if the percent|0x80 encoding holds:' -ForegroundColor DarkGray
foreach ($level in 100, 80, 60) {
    $expected = $level -bor $EnableBit
    Write-Host ("  {0,3}% -> 0x{1:X2} ({1})" -f $level, $expected) -ForegroundColor DarkGray
}

# ── 4. Optional write ───────────────────────────────────────────────────────

if (-not $PSBoundParameters.ContainsKey('SetLimit')) {
    Write-Host ''
    Write-Host 'Read-only run. Set MSI Center''s own limit to 100/80/60 and re-run each time;' -ForegroundColor Cyan
    Write-Host 'whichever value tracks those three is the threshold.' -ForegroundColor Cyan
    Write-Host 'When that is established:  .\Test-BatteryWmi.ps1 -SetLimit 60' -ForegroundColor Cyan
    Write-Host ''
    return
}

$payload = [byte]($SetLimit -bor $EnableBit)

Write-Host ''
Write-Host ("[4] Writing {0}% as 0x{1:X2} ({1}) via {2}.{3}" -f
    $SetLimit, $payload, $AcpiClass, $WriteMethod) -ForegroundColor Yellow

$written = Invoke-AcpiMethod -MethodName $WriteMethod -Payload @($payload)

if ($null -eq $written) {
    Write-Host ''
    Write-Warning 'The write did not go through. Nothing was changed. Report section [2] above.'
    return
}

Write-Host ''
Write-Host "    After ($ReadMethod)" -ForegroundColor Yellow
$after = Invoke-AcpiMethod -MethodName $ReadMethod

Write-Host ''
Write-Host 'A changed read-back only proves the value landed. What matters is whether charging' -ForegroundColor Cyan
Write-Host 'actually stops - discharge below the limit, plug in, then run:' -ForegroundColor Cyan
Write-Host "  .\Watch-Battery.ps1 -Limit $SetLimit" -ForegroundColor Cyan
Write-Host ''
