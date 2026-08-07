<#
.SYNOPSIS
    Baseline hardware and software inventory for the target device. Read-only.

.DESCRIPTION
    Run this first, before anything else in Phase 0, and keep the transcript. It establishes what
    the firmware calls this machine (which the model gate depends on), which MSI software is
    present and running (which several features have to coexist with), and what the root\WMI
    namespace exposes (where MSI's ACPI methods surface).

    Everything here is a read. Nothing is written to the device.

.EXAMPLE
    .\Get-DeviceReport.ps1 -Transcript .\device-report.txt
#>
[CmdletBinding()]
param(
    # Capture everything to a file so the raw output can be analysed off-device.
    [string] $Transcript
)

$ErrorActionPreference = 'Continue'

if ($Transcript) { Start-Transcript -Path $Transcript -Force | Out-Null }

function Write-Section($title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

Write-Section 'Identity'
# Win32_ComputerSystemProduct.Name is what the model gate matches on. Expect CG3EM or a string
# containing "Claw 8 EX"; Win32_BaseBoard.Product should be 1T91.
Get-CimInstance Win32_ComputerSystemProduct |
    Select-Object Vendor, Name, Version, IdentifyingNumber | Format-List
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product, Version | Format-List
Get-CimInstance Win32_BIOS |
    Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate | Format-List

Write-Section 'Processor and graphics'
Get-CimInstance Win32_Processor |
    Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed | Format-List
Get-CimInstance Win32_VideoController |
    Select-Object Name, DriverVersion, DriverDate, PNPDeviceID | Format-List

Write-Section 'Intel Graphics Control Library'
# ControlLib.dll ships with the Intel Arc driver and is the IGCL implementation. Its absence is
# gate G6: without it the entire Intel graphics tab has to be hidden.
$controlLib = Join-Path $env:SystemRoot 'System32\ControlLib.dll'
if (Test-Path $controlLib) {
    $item = Get-Item $controlLib
    Write-Host "  present: $($item.FullName)"
    Write-Host "  version: $($item.VersionInfo.FileVersion)"
} else {
    Write-Warning "  ControlLib.dll NOT found - IGCL features are unavailable (gate G6 fails)."
}

Write-Section 'MSI software'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Company -match 'Micro-?Star|MSI' -or $_.ProcessName -match 'MSI' } |
    Select-Object ProcessName, Id, Company, @{n = 'Path'; e = { $_.Path } } |
    Format-Table -AutoSize

Get-Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'MSI|Micro.?Star|Mystic' -or $_.DisplayName -match 'MSI' } |
    Select-Object Name, DisplayName, Status, StartType |
    Format-Table -AutoSize

Write-Section 'Intel thermal stack (IPF / DTT)'
# These own a fan participant ABOVE the embedded controller and can hold the fan at maximum
# regardless of any table we write. Knowing their state is a prerequisite for fan work.
Get-Service -Name 'ipfsvc', 'dptftcs', 'esifsvc' -ErrorAction SilentlyContinue |
    Select-Object Name, DisplayName, Status, StartType | Format-Table -AutoSize

Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'INTC106A|TFN1' } |
    Select-Object Status, Class, FriendlyName, InstanceId | Format-List

Write-Section 'MSI HID interfaces (VID_0DB0)'
Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match 'VID_0DB0' } |
    Select-Object Status, Class, FriendlyName, InstanceId | Format-List

Write-Section 'root\WMI classes with an ACPI GUID qualifier'
# The 'guid' class qualifier is the same GUID that appears in the firmware's _WDG table, so it
# is what links a callable class name to a method in the disassembled DSDT.
try {
    Get-CimClass -Namespace root\wmi -ErrorAction Stop |
        ForEach-Object {
            $guid = $_.CimClassQualifiers['guid']
            if ($guid) {
                [pscustomobject]@{
                    Class   = $_.CimClassName
                    Guid    = $guid.Value
                    Methods = ($_.CimClassMethods.Name) -join ','
                }
            }
        } | Sort-Object Class | Format-Table -AutoSize -Wrap
} catch {
    Write-Warning "Could not enumerate root\WMI: $_"
}

Write-Section 'Candidate MSI WMI classes'
try {
    Get-CimClass -Namespace root\wmi -ErrorAction Stop |
        Where-Object { $_.CimClassName -match 'MSI|MS_|AckSys|SysCtl|Micro' } |
        ForEach-Object {
            [pscustomobject]@{
                Class      = $_.CimClassName
                Guid       = $_.CimClassQualifiers['guid'].Value
                Methods    = ($_.CimClassMethods.Name) -join ','
                Properties = ($_.CimClassProperties.Name) -join ','
            }
        } | Format-List
} catch {
    Write-Warning "Could not enumerate candidate classes: $_"
}

Write-Section 'Power configuration'
powercfg /getactivescheme
Write-Host ""
powercfg /q SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE

Write-Host ""
Write-Host "Baseline complete." -ForegroundColor Green
Write-Host "Next: Probe --dump-acpi C:\acpi, then disassemble and search for _WDG."

if ($Transcript) { Stop-Transcript | Out-Null }
