<#
.SYNOPSIS
    Watches MSI's controller-mode registry value, to find out whether the PHYSICAL MSI button
    updates it.

.DESCRIPTION
    The widget reads and writes controller mode at

        HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor    value ControlModeUserSet
        "XInput" = gamepad, "Desktop" = mouse

    Writing it works: setting the mode from the widget changes the device and the widget stays in
    sync. The reverse does not. Pressing the physical MSI button changes the mode, and the widget's
    buttons do not follow.

    The helper already pushes this value once a second while the widget is visible
    (Program.RunTelemetryLoopAsync), so the polling is not the missing piece. That leaves one
    question, which this script answers:

        Does ControlModeUserSet change AT ALL when the physical button is pressed?

    NO  -> the registry is a software-write mirror, not the device's live state. MSI Center writes
           it when something asks IT to change the mode; the button talks to firmware directly and
           nothing mirrors it back. No amount of polling can fix that, and reading the true state
           would need the vendor HID command channel (opcode 0x04), which is undecoded. The
           honest outcome is to document the limitation.

    YES -> the registry is fine and the fault is ours - most likely the WidgetVisible gate on the
           telemetry loop, since Game Bar toggles Visible every two to three seconds in compact
           mode and the loop skips a tick whenever it is false.

.NOTES
    Read-only. Writes nothing. Needs elevation only if the key's ACL requires it - it is normally
    readable without.

    Poll interval is deliberately faster than the helper's 1 Hz, so a value that changes and is
    then reverted by something else is still caught.

.EXAMPLE
    .\Watch-ControlMode.ps1

    Then press the physical MSI button a few times. Every observed change prints with a timestamp.

.EXAMPLE
    .\Watch-ControlMode.ps1 -IntervalMs 100 -Seconds 120
#>

[CmdletBinding()]
param(
    [int]$IntervalMs = 250,
    [int]$Seconds = 90
)

$ErrorActionPreference = 'Stop'

# WOW6432Node is already in the path, so the 32-bit view must NOT also be requested - that would
# redirect to WOW6432Node\WOW6432Node and silently find nothing. Same rule as
# RegistryHwMouseProvider.
$KeyPath = 'SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor'
$ValueName = 'ControlModeUserSet'

function Read-ControlMode {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($KeyPath, $false)
        if ($null -eq $key) { return '<key missing>' }
        try {
            $v = $key.GetValue($ValueName)
            if ($null -eq $v) { return '<value missing>' }
            return [string]$v
        }
        finally { $key.Dispose() }
    }
    finally { $base.Dispose() }
}

$current = Read-ControlMode

if ($current -like '<*>') {
    Write-Warning "Cannot read ${ValueName}: $current"
    Write-Warning "Expected under HKLM\$KeyPath. Is MSI Center M installed?"
    return
}

Write-Host ''
Write-Host "Watching HKLM\$KeyPath\$ValueName" -ForegroundColor Cyan
Write-Host "Starting value: $current" -ForegroundColor Green
Write-Host ''
Write-Host 'PRESS THE PHYSICAL MSI BUTTON a few times now. Also switch mode from the widget, as a' -ForegroundColor Yellow
Write-Host 'control - that one is known to work, so it proves the watch itself is functioning.' -ForegroundColor Yellow
Write-Host ''
Write-Host "Ctrl+C to stop early. Runs for $Seconds seconds." -ForegroundColor DarkGray
Write-Host ''

$deadline = (Get-Date).AddSeconds($Seconds)
$changes = 0

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds $IntervalMs

    $latest = Read-ControlMode
    if ($latest -eq $current) { continue }

    $changes++
    '{0:HH:mm:ss.fff}  {1}  ->  {2}' -f (Get-Date), $current, $latest | Write-Host -ForegroundColor Green
    $current = $latest
}

Write-Host ''
if ($changes -eq 0) {
    Write-Host 'NO CHANGES OBSERVED.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'If you pressed the physical button during this run, the registry does NOT reflect it.'
    Write-Host 'The value is a software-write mirror and cannot be used to follow the button. Reading'
    Write-Host 'the real state needs the vendor HID channel (opcode 0x04), which is undecoded - see'
    Write-Host 'docs/hardware-notes.md gate G5.'
    Write-Host ''
    Write-Host 'If you did NOT touch anything, this run proves nothing. Run it again and press the'
    Write-Host 'button, and switch mode from the widget too so there is a known-good control.'
}
else {
    Write-Host "$changes change(s) observed." -ForegroundColor Green
    Write-Host ''
    Write-Host 'If any of those came from the PHYSICAL button, the registry does track it - so the'
    Write-Host 'fault is on our side, most likely the WidgetVisible gate on the telemetry loop in'
    Write-Host 'Program.RunTelemetryLoopAsync. Game Bar toggles Visible every two to three seconds'
    Write-Host 'in compact mode, and that loop skips its tick whenever Visible is false.'
}
Write-Host ''
