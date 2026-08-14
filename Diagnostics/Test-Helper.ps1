<#
.SYNOPSIS
    Drives the elevated helper over its named pipe, the way the widget does.

.DESCRIPTION
    The widget cannot be built without a Visual Studio UWP toolchain, but the helper is a plain
    .NET executable that builds anywhere. This script speaks the same protocol over the same pipe,
    so everything except the XAML - IPC framing, the dispatcher, server-side clamping, the real
    hardware providers - can be exercised on the device today.

    It is also a better test subject than the widget for this purpose: it prints the helper's raw
    replies, including the post-clamp value and any hardware error text, rather than rendering
    them.

.PARAMETER Snapshot
    Connect, request the full snapshot, and print device capabilities plus every readable value.
    This is the default when no other action is given.

.PARAMETER Get
    Read one function by name, e.g. -Get Pl1.

.PARAMETER Set
    Write one function by name. Requires -Value.

.PARAMETER Value
    The value for -Set. Always sent as a string; the helper parses it.

.PARAMETER Tdp
    Convenience: set PL1 to this many watts and report what both limits ended up at, plus what
    landed in MSI's registry. The helper couples and clamps, so the result is often not the input.

.PARAMETER Restore
    Put every value back to what it was before this app first touched it. The helper captures the
    original on the first write of each setting, so this undoes a test session in one command.

.NOTES
    Run elevated. The pipe grants the current user full control, and the helper itself must be
    running - either from its scheduled task or started by hand:

        McenterLite.Helper.exe --no-deploy

.EXAMPLE
    .\Test-Helper.ps1
    .\Test-Helper.ps1 -Tdp 25
    .\Test-Helper.ps1 -Set Pl2 -Value 30
#>

[CmdletBinding()]
param(
    [switch]$Snapshot,
    [string]$Get,
    [string]$Set,
    [string]$Value,
    [int]$Tdp,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$PipeName = 'ClawConfiguratorHelper'

# Must match src/Shared/Ipc/Function.cs. Ordinals there are explicit and never reused, so this
# map only ever grows.
#
# RETIRED, deliberately absent: 20-23 (fan), 30/31 (charge limit), 40 (lighting) and 81 (Intel
# thermal, which only existed to keep Intel's stack off the fan), all removed 2026-08-08; plus
# 13 (PerfMode) and 80 (MsiCenterRunning), removed 2026-08-13 with the registry-mirror backend and
# MSI Center M itself. Their ordinals must never be reused - listing them here would invite
# exactly that.
#
# Fan, charge limit and lighting came BACK on 2026-08-12 on new ordinals, exactly as that rule
# requires: 24-26, 32, and 41/42. This map missed them until 2026-08-13, so -Restore could not read
# back most of what it had just changed.
$Fn = @{
    Hello = 1; Snapshot = 2; DeviceCaps = 3; WidgetVisible = 4; PrepareForUninstall = 5
    Pl1 = 10; Pl2 = 11; TdpBackend = 12
    FanProfile = 24; FanProfileName = 25; FanProfileStopsAFan = 26
    ChargeLimitPercent = 32
    LightingProfile = 41; LightingProfileNames = 42
    HwMouseMode = 50
    CpuBoost = 60; OsPowerMode = 61
    IntelFpsTier = 70; IntelLowLatency = 71; IntelFrameSync = 72
    IntelAdaptiveSharpness = 73; IntelSaturation = 74; IntelContrast = 75; IntelGamma = 76
}
$FnName = @{}
foreach ($k in $Fn.Keys) { $FnName[$Fn[$k]] = $k }

$CmdGet = 0; $CmdSet = 1; $CmdResponse = 2; $CmdEvent = 3; $CmdError = 4

$script:pipe = $null
$script:reader = $null
$script:writer = $null
$script:nextId = 1

function Connect-Helper {
    $script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)

    try { $script:pipe.Connect(5000) }
    catch {
        throw ("Could not open \\.\pipe\$PipeName. The helper is not running. Start it with " +
               "'McenterLite.Helper.exe --no-deploy' from an elevated prompt, or check its " +
               "scheduled task at \ClawConfigurator\ClawConfiguratorHelper.")
    }

    # UTF-8 with NO byte-order mark. Windows PowerShell's default StreamWriter encoding emits a
    # BOM, which would prepend three bytes to the first message and make the helper reject it as
    # malformed - and because malformed input is dropped silently by design, the symptom would be
    # a connection that simply never answers.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $script:reader = New-Object System.IO.StreamReader($script:pipe, $utf8)
    $script:writer = New-Object System.IO.StreamWriter($script:pipe, $utf8)
    $script:writer.AutoFlush = $true
}

# Decodes a JSON string body.
#
# NOT just -replace '\\"','"' -replace '\\\\','\', which is what this used to do. The snapshot's
# records are separated by U+001F, and the helper's JSON writer emits that as  - so without
# \uXXXX handling the whole payload stayed one unsplittable line and the "=== Values ===" section
# rendered empty every time. Written as a single pass so an escaped backslash cannot be re-read as
# the start of another escape.
function ConvertFrom-JsonString {
    param([string]$s)
    if ($null -eq $s) { return $null }

    [regex]::Replace($s, '\\(u[0-9a-fA-F]{4}|.)', {
        param($m)
        $esc = $m.Groups[1].Value
        if ($esc.Length -eq 5 -and $esc[0] -eq 'u') {
            [char][Convert]::ToInt32($esc.Substring(1), 16)
        }
        else {
            switch ($esc) {
                '"' { '"' }
                '\' { '\' }
                '/' { '/' }
                'n' { "`n" }
                'r' { "`r" }
                't' { "`t" }
                'b' { "`b" }
                'f' { "`f" }
                default { $esc }
            }
        }
    })
}

function Invoke-Helper {
    param([int]$Cmd, [int]$Function, [string]$Val = $null)

    $id = $script:nextId++
    $v = if ($null -eq $Val) { 'null' } else { '"' + ($Val -replace '\\', '\\\\' -replace '"', '\"') + '"' }
    $script:writer.WriteLine("{`"id`":$id,`"cmd`":$Cmd,`"fn`":$Function,`"v`":$v}")

    # Read until the reply with OUR id. The helper can push unsolicited telemetry (cmd 3), and
    # taking the next line blindly would attribute a telemetry push to a power-limit request.
    for ($i = 0; $i -lt 50; $i++) {
        $line = $script:reader.ReadLine()
        if ($null -eq $line) { throw 'The helper closed the pipe.' }
        if ($line -notmatch '"id"\s*:\s*(\d+)') { continue }
        if ([int]$Matches[1] -ne $id) { continue }

        $cmdOut = if ($line -match '"cmd"\s*:\s*(\d+)') { [int]$Matches[1] } else { -1 }
        $valOut = if ($line -match '"v"\s*:\s*"((?:[^"\\]|\\.)*)"') { ConvertFrom-JsonString $Matches[1] } else { $null }
        $errOut = if ($line -match '"err"\s*:\s*"((?:[^"\\]|\\.)*)"') { ConvertFrom-JsonString $Matches[1] } else { $null }

        return [pscustomobject]@{ Cmd = $cmdOut; Value = $valOut; Error = $errOut; Raw = $line }
    }
    throw 'No reply carrying our request id.'
}

function Show-Reply {
    param([string]$Label, $Reply)
    if ($Reply.Cmd -eq $CmdError) {
        Write-Host ("  {0,-22} ERROR  {1}" -f $Label, $Reply.Error) -ForegroundColor Red
    } else {
        Write-Host ("  {0,-22} {1}" -f $Label, $Reply.Value) -ForegroundColor Green
    }
}

function Show-Snapshot {
    $reply = Invoke-Helper -Cmd $CmdGet -Function $Fn.Hello
    if ($reply.Cmd -eq $CmdError) { throw "Handshake failed: $($reply.Error)" }

    # Records are separated by U+001F; each is either caps=... or <functionId>=<value>.
    $records = $reply.Value -split ([char]0x1F)

    Write-Host ''
    Write-Host '=== Device capabilities ===' -ForegroundColor Cyan
    foreach ($r in $records) {
        if ($r -notlike 'caps=*') { continue }
        foreach ($kv in ($r.Substring(5) -split ';')) {
            if ($kv) { Write-Host "  $kv" }
        }
    }

    Write-Host ''
    Write-Host '=== Values ===' -ForegroundColor Cyan
    foreach ($r in $records) {
        if ($r -like 'caps=*') { continue }
        $eq = $r.IndexOf('=')
        if ($eq -le 0) { continue }
        $id = 0
        if (-not [int]::TryParse($r.Substring(0, $eq), [ref]$id)) { continue }
        $name = if ($FnName.ContainsKey($id)) { $FnName[$id] } else { "fn$id" }
        Write-Host ("  {0,-22} {1}" -f $name, $r.Substring($eq + 1))
    }
}

# Reads MSI Center M's own TDP model, which this app once WROTE through the registry-mirror
# backend. That backend was deleted on 2026-08-13 and MSI Center M is uninstalled, so these keys
# are orphaned leftovers - the values here are frozen at whatever they held that morning.
#
# Kept because it is still the answer to one question worth asking: if these ever START moving
# again, something reinstalled MSI Center M and is driving the same hardware we are.
function Show-TdpRegistry {
    $path = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario'
    try {
        $k = Get-ItemProperty -Path $path -ErrorAction Stop
        Write-Host ''
        Write-Host '=== MSI Center''s own model (orphaned; nothing reads or writes it) ===' -ForegroundColor Cyan
        Write-Host ("  AC  PL1={0}  PL2={1}" -f $k.ManualPL1AC, $k.ManualPL2AC)
        Write-Host ("  DC  PL1={0}  PL2={1}" -f $k.ManualPL1DC, $k.ManualPL2DC)
        Write-Host ("  Mode={0}" -f $k.Mode)
        Write-Host ''
        Write-Host 'These are expected NOT to move. The helper writes the EC directly.' -ForegroundColor DarkGray
        Write-Host 'If they DO move, MSI Center M is back and contending for the hardware.' -ForegroundColor DarkGray
    } catch {
        Write-Host ''
        Write-Host 'MSI Center M''s registry model is gone, as expected after the uninstall.' -ForegroundColor DarkGray
    }
}

try {
    Connect-Helper

    if ($PSBoundParameters.ContainsKey('Tdp')) {
        Write-Host ''
        Write-Host "=== Setting PL1 to $Tdp W ===" -ForegroundColor Cyan
        Show-Reply 'Pl1 ->' (Invoke-Helper -Cmd $CmdSet -Function $Fn.Pl1 -Val "$Tdp")
        Show-Reply 'Pl1 (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl1)
        Show-Reply 'Pl2 (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl2)
        Show-Reply 'TdpBackend' (Invoke-Helper -Cmd $CmdGet -Function $Fn.TdpBackend)
        Show-TdpRegistry
        Write-Host ''
        Write-Host 'PL2 follows PL1 by the firmware headroom, so a different number is correct.' -ForegroundColor DarkGray
        Write-Host 'A read-back only proves the value was stored. To prove the EC obeyed, hold the' -ForegroundColor DarkGray
        Write-Host 'CPU under load and watch ''% Processor Performance'' across a low/high pair.' -ForegroundColor DarkGray
        return
    }


    if ($Restore) {
        # Sends the same message an uninstall now sends. Since 2026-08-13 this applies
        # FeatureDefaults - 17/19 W, charge 100%, fans Auto, controller Gamepad, boost on,
        # Balanced - rather than replaying captured Original_* values, which no longer exist.
        # Lighting is deliberately left alone.
        #
        # PREFER 'McenterLite.Helper.exe --restore' over this. The pipe is
        # maxNumberOfServerInstances:1 and the widget never disconnects once shown, so this path
        # cannot connect at all if the Game Bar has been opened since the helper started.
        Write-Host ''
        Write-Host '=== Restoring every feature to its default ===' -ForegroundColor Cyan
        Show-Reply 'Restore' (Invoke-Helper -Cmd $CmdSet -Function $Fn.PrepareForUninstall -Val '1')
        Show-Reply 'Pl1' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl1)
        Show-Reply 'Pl2' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl2)
        Show-Reply 'ChargeLimit' (Invoke-Helper -Cmd $CmdGet -Function $Fn.ChargeLimitPercent)
        Show-Reply 'FanProfile' (Invoke-Helper -Cmd $CmdGet -Function $Fn.FanProfile)
        Show-Reply 'HwMouseMode' (Invoke-Helper -Cmd $CmdGet -Function $Fn.HwMouseMode)
        Show-Reply 'CpuBoost' (Invoke-Helper -Cmd $CmdGet -Function $Fn.CpuBoost)
        Show-Reply 'OsPowerMode' (Invoke-Helper -Cmd $CmdGet -Function $Fn.OsPowerMode)
        Show-TdpRegistry
        return
    }

    if ($Get) {
        if (-not $Fn.ContainsKey($Get)) { throw "Unknown function '$Get'. Known: $($Fn.Keys -join ', ')" }
        Show-Reply $Get (Invoke-Helper -Cmd $CmdGet -Function $Fn[$Get])
        return
    }

    if ($Set) {
        if (-not $Fn.ContainsKey($Set)) { throw "Unknown function '$Set'. Known: $($Fn.Keys -join ', ')" }
        if (-not $PSBoundParameters.ContainsKey('Value')) { throw '-Set requires -Value.' }
        Show-Reply "$Set ->" (Invoke-Helper -Cmd $CmdSet -Function $Fn[$Set] -Val $Value)
        Show-Reply "$Set (read back)" (Invoke-Helper -Cmd $CmdGet -Function $Fn[$Set])
        return
    }

    Show-Snapshot
}
finally {
    if ($script:writer) { $script:writer.Dispose() }
    if ($script:reader) { $script:reader.Dispose() }
    if ($script:pipe) { $script:pipe.Dispose() }
}
