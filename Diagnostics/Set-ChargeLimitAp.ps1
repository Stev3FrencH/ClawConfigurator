<#
.SYNOPSIS
    Reads and sets the battery charge limit through MSI_ACPI.Get_AP / Set_AP - the path measured
    to actually carry it.

.DESCRIPTION
    Located 2026-08-08 by sweeping every Get_* method across sub-functions and diffing snapshots
    taken at MSI Center's 100 / 80 / 60 settings (Sweep-MsiAcpi.ps1). The limit is NOT in
    Get_MasterBattery, despite the name. It is:

        MSI_ACPI.Get_AP / Set_AP, input byte 0 = 0x00, value in output byte 5,
        encoded percent -bor 0x80

        100% -> 0xE4     80% -> 0xD0     60% -> 0xBC

    The full package reads 01 00 00 C6 80 XX 00 ... with only byte 5 moving. Bytes 3 and 4
    (0xC6, 0x80) are constant and presumed to identify the register; byte 0 is presumed a status
    flag. NEITHER IS VERIFIED, which is why this does read-modify-write rather than building a
    buffer: it changes byte 5 and nothing else, so bytes whose meaning is unknown are preserved
    exactly as the firmware reported them.

    WRITE FORMAT IS THE REMAINING UNKNOWN. Set_AP takes the same Package_32, but whether the
    request mirrors the response is unestablished - the response's byte 0 = 0x01 reads like a
    status flag, and that need not mean the same thing inbound. So the write is attempted in
    ordered shapes, each VERIFIED BY READING BACK before the next is tried. Nothing is repeated
    blindly.

.PARAMETER Limit
    The limit to set. Omit to read only.

.PARAMETER NoRegistrySync
    Skip updating MSI Center's BatteryLevel value. By default it is kept in step, so MSI Center's
    own UI does not sit showing a limit the hardware no longer has.

.NOTES
    Requires elevation.

    READ-ONLY unless -Limit is given. Only Set_AP is ever called; no other Set_ method, and never
    Set_EC.

    A changed read-back proves the value landed, not that charging obeys it. For that, discharge
    below the limit, plug in, and run Watch-Battery.ps1.

.EXAMPLE
    .\Set-ChargeLimitAp.ps1

.EXAMPLE
    .\Set-ChargeLimitAp.ps1 -Limit 60
#>

[CmdletBinding()]
param(
    [ValidateSet(60, 80, 100)]
    [int]$Limit,

    [switch]$NoRegistrySync
)

$ErrorActionPreference = 'Stop'

$Namespace   = 'root/wmi'
$AcpiClass   = 'MSI_ACPI'
$ReadMethod  = 'Get_AP'
$WriteMethod = 'Set_AP'

$SubFunction   = 0x00   # input byte 0
$ThresholdByte = 5      # output byte carrying the limit
$EnableBit     = 0x80   # bit 7 set; bits 0-6 are the percentage

$RegistryKey  = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery'
$RegistryName = 'BatteryLevel'
$ToMsiLevel   = @{ 100 = '0'; 80 = '1'; 60 = '2' }

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) { throw 'Run this elevated. root\wmi vendor classes are not readable otherwise.' }

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

function Invoke-Ap {
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
    $head = @($Bytes[0..7])
    Write-Host ("  {0,-14} {1}" -f $Label, (($head | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))
}

function Get-ChargingState {
    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $battery) { return $null }
    [pscustomobject]@{
        Percent  = [int]$battery.EstimatedChargeRemaining
        Charging = $battery.BatteryStatus -notin @(1, 2)
        OnAc     = $battery.BatteryStatus -ne 1
    }
}

# ── Read ────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '=== Charge limit via MSI_ACPI.Get_AP / Set_AP ===' -ForegroundColor Cyan
Write-Host ''

$request = New-Object byte[] 32
$request[0] = $SubFunction

$current = Invoke-Ap -MethodName $ReadMethod -Buffer $request
if (-not $current) { throw "$ReadMethod returned nothing. Cannot proceed without a baseline." }

Show-Package -Bytes $current -Label 'current'

$raw = $current[$ThresholdByte]
$decoded = $raw -band 0x7F
$enabled = ($raw -band $EnableBit) -ne 0

Write-Host ("  byte {0}        0x{1:X2} -> {2}% (enable bit {3})" -f
    $ThresholdByte, $raw, $decoded, $(if ($enabled) { 'set' } else { 'CLEAR' }))

$state = Get-ChargingState
if ($state) {
    Write-Host ("  battery        {0}%  charging={1}  onAC={2}" -f
        $state.Percent, $state.Charging, $state.OnAc)
}

# The model has to hold before anything is written on the strength of it.
if (-not $enabled -or $decoded -lt 20 -or $decoded -gt 100) {
    Write-Host ''
    Write-Warning ("Byte $ThresholdByte does not look like a charge threshold " +
                   "(0x{0:X2}). Refusing to write against a model that no longer fits." -f $raw)
    return
}

if (-not $PSBoundParameters.ContainsKey('Limit')) {
    Write-Host ''
    Write-Host "Read-only. To set:  .\$(Split-Path -Leaf $PSCommandPath) -Limit 60" -ForegroundColor Cyan
    Write-Host ''
    return
}

if ($decoded -eq $Limit) {
    Write-Host ''
    Write-Host "Already at $Limit%. Nothing to write." -ForegroundColor Green
    Write-Host ''
    return
}

# ── Write ───────────────────────────────────────────────────────────────────

$target = [byte]($Limit -bor $EnableBit)

Write-Host ''
Write-Host ("Setting {0}% -> byte {1} = 0x{2:X2}" -f $Limit, $ThresholdByte, $target) -ForegroundColor Yellow

# Read-modify-write. Only byte 5 changes; bytes 3 and 4 carry whatever the firmware reported,
# because their meaning is not established and a hand-built buffer could zero them.
$modified = @($current.Clone())
$modified[$ThresholdByte] = $target

# The request convention is the open question. Shape A keeps byte 0 as the sub-function selector,
# matching how the READ is addressed. Shape B echoes the response verbatim. Each is verified by
# reading back before the next is tried.
$shapes = @(
    @{ Label = 'selector in byte 0'; Buffer = @($modified.Clone()) }
    @{ Label = 'response echoed';    Buffer = @($modified.Clone()) }
)
$shapes[0].Buffer[0] = $SubFunction

$applied = $false

foreach ($shape in $shapes) {
    Show-Package -Bytes $shape.Buffer -Label "try [$($shape.Label)]"

    $null = Invoke-Ap -MethodName $WriteMethod -Buffer $shape.Buffer
    Start-Sleep -Milliseconds 500

    $after = Invoke-Ap -MethodName $ReadMethod -Buffer $request
    if (-not $after) {
        Write-Host '    could not read back' -ForegroundColor DarkYellow
        continue
    }

    Show-Package -Bytes $after -Label '  read back'

    if ($after[$ThresholdByte] -eq $target) {
        Write-Host "    APPLIED - byte $ThresholdByte is now 0x$('{0:X2}' -f $target)" -ForegroundColor Green
        $applied = $true
        break
    }

    Write-Host ("    unchanged (still 0x{0:X2})" -f $after[$ThresholdByte]) -ForegroundColor DarkYellow
}

if (-not $applied) {
    Write-Host ''
    Write-Warning 'No write shape took. Nothing changed - the read-back confirms the old value stands.'
    return
}

# ── Keep MSI Center's own view in step ──────────────────────────────────────

if (-not $NoRegistrySync) {
    try {
        Set-ItemProperty -Path $RegistryKey -Name $RegistryName -Value $ToMsiLevel[$Limit] -Type String
        Write-Host "  registry      BatteryLevel = '$($ToMsiLevel[$Limit])' (MSI Center UI in step)" -ForegroundColor DarkGray
    }
    catch {
        Write-Warning "Applied, but could not update MSI Center's registry value: $($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host 'The value landed. That is not the same as charging obeying it - for that:' -ForegroundColor Cyan
Write-Host "  discharge below $Limit%, plug in, then:  .\Watch-Battery.ps1 -Limit $Limit" -ForegroundColor Cyan
Write-Host ''
