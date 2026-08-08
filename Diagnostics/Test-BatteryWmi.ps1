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

$Namespace  = 'root/wmi'
$AcpiClass  = 'MSI_ACPI'
$ReadMethod = 'Get_MasterBattery'
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
        $slice = $Bytes[$offset..$end]

        $hex = ($slice | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        $dec = ($slice | ForEach-Object { '{0,3}' -f $_ }) -join ' '

        Write-Host ("{0}[{1,2}] {2}" -f $Indent, $offset, $hex)
        Write-Host ("{0}     {1}" -f $Indent, $dec) -ForegroundColor DarkGray
    }
}

function Show-CimResult {
    param($Result, [string]$Indent = '    ')

    if ($null -eq $Result) {
        Write-Host "$Indent(nothing returned)"
        return
    }

    foreach ($prop in $Result.CimInstanceProperties) {
        if ($prop.Name -eq 'ReturnValue' -and $prop.Value -eq 0) { continue }

        if ($prop.Value -is [byte[]]) {
            Write-Host "$Indent$($prop.Name) = byte[$($prop.Value.Count)]"
            Show-Bytes -Bytes $prop.Value -Indent "$Indent  "
        }
        elseif ($prop.Value -is [int] -or $prop.Value -is [uint32] -or $prop.Value -is [byte]) {
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

Write-Host 'MSI_Master_Battery (property read, no method call)' -ForegroundColor Yellow
try {
    $master = Get-CimInstance -Namespace $Namespace -ClassName 'MSI_Master_Battery' -ErrorAction Stop
    foreach ($instance in @($master)) {
        foreach ($prop in $instance.CimInstanceProperties) {
            if ($prop.Value -is [byte[]]) {
                Write-Host "    $($prop.Name) = byte[$($prop.Value.Count)]"
                Show-Bytes -Bytes $prop.Value -Indent '      '
            }
            elseif ($prop.Name -notin @('InstanceName', 'Active')) {
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

Write-Host "$AcpiClass method signatures (declared shape, nothing called)" -ForegroundColor Yellow

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

    Write-Host "    $name"
    foreach ($p in $method.Parameters) {
        $direction = if ($p.Qualifiers['Out']) { 'OUT' } else { 'IN ' }
        Write-Host "      $direction $($p.Name) : $($p.CimType)"
    }
}

Write-Host ''

# ── 3. Call Get_MasterBattery ───────────────────────────────────────────────

$instance = Get-CimInstance -Namespace $Namespace -ClassName $AcpiClass -ErrorAction Stop |
            Select-Object -First 1

if (-not $instance) { throw "$AcpiClass exists but reported no instances." }

function Invoke-AcpiMethod {
    param([string]$MethodName, [byte[]]$Payload)

    $method = $class.CimClassMethods[$MethodName]
    if (-not $method) { throw "$AcpiClass has no method named $MethodName." }

    # Build arguments from the DECLARED input parameters rather than a guessed name - these
    # methods are generated from the DSDT method each one fronts, so their shape is exactly
    # what this script is trying to establish.
    $arguments = @{}
    foreach ($p in $method.Parameters) {
        if ($p.Qualifiers['Out'] -and -not $p.Qualifiers['In']) { continue }

        if ($p.CimType -match 'Array') {
            # MSI's ACPI buffer methods conventionally take a 32-byte package.
            $buffer = New-Object byte[] 32
            for ($i = 0; $i -lt $Payload.Count -and $i -lt 32; $i++) { $buffer[$i] = $Payload[$i] }
            $arguments[$p.Name] = $buffer
        }
        else {
            $arguments[$p.Name] = if ($Payload.Count -gt 0) { [uint32]$Payload[0] } else { [uint32]0 }
        }
    }

    if ($arguments.Count -gt 0) {
        $shown = $arguments.GetEnumerator() | ForEach-Object {
            if ($_.Value -is [byte[]]) {
                "$($_.Key)=[$(($_.Value[0..7] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') ...]"
            } else {
                "$($_.Key)=$($_.Value)"
            }
        }
        Write-Host "    in : $($shown -join ', ')" -ForegroundColor DarkGray
    }

    return Invoke-CimMethod -InputObject $instance -MethodName $MethodName -Arguments $arguments
}

Write-Host "$AcpiClass.$ReadMethod" -ForegroundColor Yellow
try {
    $before = Invoke-AcpiMethod -MethodName $ReadMethod -Payload @()
    Show-CimResult -Result $before
}
catch {
    Write-Warning "$ReadMethod failed: $($_.Exception.Message)"
    $before = $null
}

Write-Host ''
Write-Host 'Expected if the percent|0x80 encoding holds:' -ForegroundColor DarkGray
foreach ($level in 100, 80, 60) {
    $byte = $level -bor $EnableBit
    Write-Host ("  {0,3}% -> 0x{1:X2} ({1})" -f $level, $byte) -ForegroundColor DarkGray
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
Write-Host ("Writing {0}% as 0x{1:X2} ({1}) via {2}.{3}" -f $SetLimit, $payload, $AcpiClass, $WriteMethod) -ForegroundColor Yellow

try {
    $result = Invoke-AcpiMethod -MethodName $WriteMethod -Payload @($payload)
    Show-CimResult -Result $result
}
catch {
    throw "$WriteMethod failed: $($_.Exception.Message)"
}

Write-Host ''
Write-Host "After ($ReadMethod)" -ForegroundColor Yellow
try {
    Show-CimResult -Result (Invoke-AcpiMethod -MethodName $ReadMethod -Payload @())
}
catch {
    Write-Warning "$ReadMethod failed: $($_.Exception.Message)"
}

Write-Host ''
Write-Host 'A changed read-back only proves the value landed. What matters is whether charging' -ForegroundColor Cyan
Write-Host 'actually stops - discharge below the limit, plug in, then run:' -ForegroundColor Cyan
Write-Host "  .\Watch-Battery.ps1 -Limit $SetLimit" -ForegroundColor Cyan
Write-Host ''
