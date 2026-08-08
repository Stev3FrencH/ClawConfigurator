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

    # Two different shapes arrive here and they enumerate differently. Invoke-CimMethod returns a
    # PSCustomObject whose properties are the out parameters - it has no CimInstanceProperties, so
    # reading that (as an earlier version did) silently yielded nothing and hid the answer. A
    # nested embedded instance IS a CimInstance and does use CimInstanceProperties.
    $properties = if ($Result -is [Microsoft.Management.Infrastructure.CimInstance]) {
        @($Result.CimInstanceProperties | ForEach-Object {
            [pscustomobject]@{ Name = $_.Name; Value = $_.Value } })
    }
    else {
        @($Result.PSObject.Properties | ForEach-Object {
            [pscustomobject]@{ Name = $_.Name; Value = $_.Value } })
    }

    foreach ($prop in $properties) {
        if ($prop.Name -in @('PSComputerName', 'CimClass', 'CimInstanceProperties', 'CimSystemProperties')) {
            continue
        }

        if ($prop.Value -is [byte[]]) {
            Write-Host "$Indent$($prop.Name) = byte[$($prop.Value.Count)]"
            Show-Bytes -Bytes $prop.Value -Indent "$Indent  "
        }
        elseif ($prop.Value -is [Microsoft.Management.Infrastructure.CimInstance]) {
            # The payload of an EmbeddedInstance method rides in here, so this recursion is
            # where the answer actually shows up - not at the top level.
            Write-Host "$Indent$($prop.Name) = [$($prop.Value.CimSystemProperties.ClassName)]"
            Show-CimResult -Result $prop.Value -Indent "$Indent  "
        }
        elseif ($prop.Value -is [bool]) {
            # Booleans are value types but have no sensible hex rendering. These methods return
            # one, so without this the result line reads "0xTrue".
            Write-Host "$Indent$($prop.Name) = $($prop.Value)"
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

        # An EmbeddedInstance parameter names the class it expects. Without that value there is
        # nothing to construct, which is why every plain byte/scalar shape was rejected.
        $embedded = $p.Qualifiers['EmbeddedInstance']
        if ($embedded) {
            Write-Host "        EmbeddedInstance -> $($embedded.Value)" -ForegroundColor Green
        }
    }
}

Write-Host ''

# ── 2b. The embedded payload class ──────────────────────────────────────────

$EmbeddedClassName = $null
$readMethodDecl = $class.CimClassMethods[$ReadMethod]
if ($readMethodDecl) {
    foreach ($p in $readMethodDecl.Parameters) {
        $embedded = $p.Qualifiers['EmbeddedInstance']
        if ($embedded) { $EmbeddedClassName = $embedded.Value; break }
    }
}

$EmbeddedClass = $null
if ($EmbeddedClassName) {
    Write-Host "[2b] Embedded payload class: $EmbeddedClassName" -ForegroundColor Yellow
    try {
        $EmbeddedClass = Get-CimClass -Namespace $Namespace -ClassName $EmbeddedClassName -ErrorAction Stop
        foreach ($prop in $EmbeddedClass.CimClassProperties) {
            $quals = ($prop.Qualifiers | ForEach-Object { $_.Name }) -join ','
            Write-Host "      $($prop.Name) : $($prop.CimType)   [$quals]"
        }
    }
    catch {
        Write-Warning "Could not read $EmbeddedClassName in ${Namespace}: $($_.Exception.Message)"
    }
    Write-Host ''
}

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

function New-EmbeddedPayload {
    <#
        Builds the [EmbeddedInstance] object a method expects, filling its properties from the
        byte payload. Array properties get a buffer of the given size; scalars get the first
        byte. -ClientOnly because this object is an argument, not something WMI already holds.
    #>
    param([string]$ClassName, $ClassDecl, [byte[]]$Payload, [int]$BufferSize)

    $properties = @{}

    if ($ClassDecl) {
        foreach ($prop in $ClassDecl.CimClassProperties) {
            # Key/system properties are not ours to populate.
            if ($prop.Qualifiers['key']) { continue }

            if ($prop.CimType -match 'Array') {
                $buffer = New-Object byte[] $BufferSize
                for ($i = 0; $i -lt $Payload.Count -and $i -lt $BufferSize; $i++) {
                    $buffer[$i] = $Payload[$i]
                }
                $properties[$prop.Name] = $buffer
            }
            elseif ($prop.CimType -match 'UInt8|SInt8|UInt16|SInt16|UInt32|SInt32|UInt64|SInt64') {
                $properties[$prop.Name] = if ($Payload.Count -gt 0) { [uint32]$Payload[0] } else { [uint32]0 }
            }
        }
    }

    return New-CimInstance -Namespace $Namespace -ClassName $ClassName `
                           -Property $properties -ClientOnly -ErrorAction Stop
}

function Get-ArgumentShapes {
    <#
        Candidate argument sets for a method whose shape is not yet fully known, most likely
        first. EmbeddedInstance parameters are CONSTRUCTED from the class the qualifier names -
        skipping them, as an earlier version did, left the method with nothing to act on.
    #>
    param($Method, [byte[]]$Payload)

    $shapes = @()
    $inputs = Get-InputParameters -Method $Method

    if ($inputs.Count -eq 0) {
        return @(, @{ Label = 'no arguments'; Arguments = $null })
    }

    foreach ($size in 32, 256, 8, 4) {
        # NOT $args - that is an automatic variable in PowerShell functions.
        $argSet = @{}
        $varies = $false
        $ok = $true

        foreach ($p in $inputs) {
            $embedded = $p.Qualifiers['EmbeddedInstance']

            if ($embedded) {
                try {
                    $decl = $null
                    try { $decl = Get-CimClass -Namespace $Namespace -ClassName $embedded.Value -ErrorAction Stop }
                    catch { $decl = $null }

                    $argSet[$p.Name] = New-EmbeddedPayload -ClassName $embedded.Value `
                        -ClassDecl $decl -Payload $Payload -BufferSize $size
                    $varies = $true
                }
                catch {
                    Write-Host "    could not build $($embedded.Value): $($_.Exception.Message)" -ForegroundColor DarkYellow
                    $ok = $false
                }
            }
            elseif ($p.CimType -match 'Array') {
                $buffer = New-Object byte[] $size
                for ($i = 0; $i -lt $Payload.Count -and $i -lt $size; $i++) { $buffer[$i] = $Payload[$i] }
                $argSet[$p.Name] = $buffer
                $varies = $true
            }
            elseif ($p.CimType -match 'Instance|Reference') {
                $ok = $false   # an instance parameter with no class named - nothing to build
            }
            else {
                $argSet[$p.Name] = if ($Payload.Count -gt 0) { [uint32]$Payload[0] } else { [uint32]0 }
            }
        }

        if ($ok -and $argSet.Count -gt 0) {
            $shapes += @{ Label = "payload $size"; Arguments = $argSet }
        }

        if (-not $varies) { break }   # nothing size-dependent, so retrying sizes is pointless
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

# ── 3b/4. Read-only survey ──────────────────────────────────────────────────
#
# The shape is known now (one EmbeddedInstance parameter carrying a 32-byte Package_32), so these
# can call directly instead of re-searching shapes. Everything below is a Get_, and every round
# trip to the device is expensive, so one run gathers as much as possible.

function Invoke-Package32 {
    <#
        Calls a method with the now-known shape and returns the returned Bytes array, or $null.
        Never throws - a rejected sub-function is a measurement.
    #>
    param([string]$MethodName, [byte[]]$Payload)

    if (-not $EmbeddedClassName) { return $null }

    $method = $class.CimClassMethods[$MethodName]
    if (-not $method) { return $null }

    $parameterName = (Get-InputParameters -Method $method |
                      Where-Object { $_.Qualifiers['EmbeddedInstance'] } |
                      Select-Object -First 1).Name
    if (-not $parameterName) { return $null }

    $buffer = New-Object byte[] 32
    for ($i = 0; $i -lt $Payload.Count -and $i -lt 32; $i++) { $buffer[$i] = $Payload[$i] }

    try {
        $payloadInstance = New-CimInstance -Namespace $Namespace -ClassName $EmbeddedClassName `
                               -Property @{ Bytes = $buffer } -ClientOnly -ErrorAction Stop

        $result = Invoke-CimMethod -InputObject $acpi -MethodName $MethodName `
                      -Arguments @{ $parameterName = $payloadInstance } -ErrorAction Stop

        $embeddedOut = $result.PSObject.Properties |
                       Where-Object { $_.Value -is [Microsoft.Management.Infrastructure.CimInstance] } |
                       Select-Object -First 1

        if ($embeddedOut) { return $embeddedOut.Value.Bytes }
        return $null
    }
    catch {
        return $null
    }
}

function Format-BytesInline {
    param([byte[]]$Bytes, [int]$Count = 16)
    if (-not $Bytes) { return '(no data)' }
    $take = @($Bytes[0..([Math]::Min($Count, $Bytes.Count) - 1)])
    return (($take | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
}

Write-Host ''
Write-Host "[3b] $ReadMethod sub-function probe (input byte 0 varied, read-only)" -ForegroundColor Yellow
Write-Host '     A zeroed input returned 01 09, which is not a percentage - so byte 0 may select' -ForegroundColor DarkGray
Write-Host '     what is being asked for. Watch for a row containing BC / D0 / E4.' -ForegroundColor DarkGray

foreach ($selector in 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0xEF, 0xD7) {
    $bytes = Invoke-Package32 -MethodName $ReadMethod -Payload @([byte]$selector)
    $rendered = if ($bytes) { Format-BytesInline -Bytes $bytes } else { '(rejected)' }
    Write-Host ("     in[0]=0x{0:X2} -> {1}" -f $selector, $rendered)
}

Write-Host ''
Write-Host '[4] Get_EC block reads (read-only, address in input byte 0)' -ForegroundColor Yellow
Write-Host '    Tests the msi-ec hypothesis directly: the charge threshold is reported to live at' -ForegroundColor DarkGray
Write-Host '    EC 0xD7 (gen 2) or 0xEF (gen 1), holding percent|0x80. The input convention here is' -ForegroundColor DarkGray
Write-Host '    itself a guess, so a rejected row means the guess was wrong, not that the EC is.' -ForegroundColor DarkGray

if ($class.CimClassMethods['Get_EC']) {
    foreach ($base in 0x00, 0xC0, 0xD0, 0xE0, 0xF0) {
        $bytes = Invoke-Package32 -MethodName 'Get_EC' -Payload @([byte]$base)
        $rendered = if ($bytes) { Format-BytesInline -Bytes $bytes } else { '(rejected)' }
        Write-Host ("    base 0x{0:X2} -> {1}" -f $base, $rendered)
    }
}
else {
    Write-Host '    (Get_EC not present on this class)' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Expected if the percent|0x80 encoding holds:' -ForegroundColor DarkGray
foreach ($level in 100, 80, 60) {
    $expected = $level -bor $EnableBit
    Write-Host ("  {0,3}% -> 0x{1:X2} ({1})" -f $level, $expected) -ForegroundColor DarkGray
}
Write-Host 'If nothing shows those, the plain percentages 64 / 50 / 3C are worth looking for too.' -ForegroundColor DarkGray

# ── 5. Optional write ───────────────────────────────────────────────────────

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
Write-Host ("[5] Writing {0}% as 0x{1:X2} ({1}) via {2}.{3}" -f
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
