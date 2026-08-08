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

.PARAMETER ChargeLimit
    Convenience: 60, 80 or 100. 100 switches the limiter off, because "charge to full" is how the
    device represents "no limit".

.NOTES
    Run elevated. The pipe grants the current user full control, and the helper itself must be
    running - either from its scheduled task or started by hand:

        McenterLite.Helper.exe --no-deploy

.EXAMPLE
    .\Test-Helper.ps1
    .\Test-Helper.ps1 -Tdp 25
    .\Test-Helper.ps1 -ChargeLimit 60
    .\Test-Helper.ps1 -Set PerfMode -Value 1
#>

[CmdletBinding()]
param(
    [switch]$Snapshot,
    [string]$Get,
    [string]$Set,
    [string]$Value,
    [int]$Tdp,
    [int]$ChargeLimit
)

$ErrorActionPreference = 'Stop'

$PipeName = 'McenterLiteHelper'

# Must match src/Shared/Ipc/Function.cs. Ordinals there are explicit and never reused, so this
# map only ever grows.
$Fn = @{
    Hello = 1; Snapshot = 2; DeviceCaps = 3; WidgetVisible = 4; PrepareForUninstall = 5
    Pl1 = 10; Pl2 = 11; TdpBackend = 12; PerfMode = 13
    FanEnabled = 20; FanPreset = 21; FanState = 22; FanFullSpeed = 23
    ChargeLimitEnabled = 30; ChargeLimitPercent = 31
    LedSpec = 40; HwMouseMode = 50
    CpuBoost = 60; OsPowerMode = 61
    IntelFpsTier = 70; IntelLowLatency = 71; IntelFrameSync = 72
    IntelAdaptiveSharpness = 73; IntelSaturation = 74; IntelContrast = 75; IntelGamma = 76
    MsiCenterRunning = 80; IntelThermalCmd = 81
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
               "scheduled task at \McenterLite\McenterLiteHelper.")
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

function Invoke-Helper {
    param([int]$Cmd, [int]$Function, [string]$Val = $null)

    $id = $script:nextId++
    $v = if ($null -eq $Val) { 'null' } else { '"' + ($Val -replace '\\', '\\\\' -replace '"', '\"') + '"' }
    $script:writer.WriteLine("{`"id`":$id,`"cmd`":$Cmd,`"fn`":$Function,`"v`":$v}")

    # Read until the reply with OUR id. The helper can push unsolicited telemetry (cmd 3), and
    # taking the next line blindly would attribute a fan reading to a power-limit request.
    for ($i = 0; $i -lt 50; $i++) {
        $line = $script:reader.ReadLine()
        if ($null -eq $line) { throw 'The helper closed the pipe.' }
        if ($line -notmatch '"id"\s*:\s*(\d+)') { continue }
        if ([int]$Matches[1] -ne $id) { continue }

        $cmdOut = if ($line -match '"cmd"\s*:\s*(\d+)') { [int]$Matches[1] } else { -1 }
        $valOut = if ($line -match '"v"\s*:\s*"((?:[^"\\]|\\.)*)"') { $Matches[1] -replace '\\"', '"' -replace '\\\\', '\' } else { $null }
        $errOut = if ($line -match '"err"\s*:\s*"((?:[^"\\]|\\.)*)"') { $Matches[1] -replace '\\"', '"' } else { $null }

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

function Show-TdpRegistry {
    $path = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario'
    try {
        $k = Get-ItemProperty -Path $path -ErrorAction Stop
        Write-Host ''
        Write-Host '=== What landed in MSI Center''s model ===' -ForegroundColor Cyan
        Write-Host ("  AC  PL1={0}  PL2={1}" -f $k.ManualPL1AC, $k.ManualPL2AC)
        Write-Host ("  DC  PL1={0}  PL2={1}   <- battery ceiling applies here" -f $k.ManualPL1DC, $k.ManualPL2DC)
        Write-Host ("  Mode={0}  (4 = User Scenario, the only mode that honours manual limits)" -f $k.Mode)
    } catch {
        Write-Warning "Could not read $path"
    }
}

function Show-BatteryRegistry {
    $path = 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery'
    try {
        $level = (Get-ItemProperty -Path $path -ErrorAction Stop).BatteryLevel
        $meaning = switch ("$level") { '0' { '100% (no limit)' } '1' { '80%' } '2' { '60%' } default { 'unrecognised' } }
        Write-Host ''
        Write-Host '=== What landed in MSI Center''s model ===' -ForegroundColor Cyan
        Write-Host ("  BatteryLevel = `"{0}`"  ->  {1}" -f $level, $meaning)
        Write-Host '  (the numbering is inverted: a higher level is a LOWER limit)' -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not read $path"
    }
}

Connect-Helper
try {
    if ($PSBoundParameters.ContainsKey('Tdp')) {
        Write-Host ''
        Write-Host "=== Setting PL1 to $Tdp W ===" -ForegroundColor Cyan
        Show-Reply 'Pl1 ->' (Invoke-Helper -Cmd $CmdSet -Function $Fn.Pl1 -Val "$Tdp")
        Show-Reply 'Pl1 (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl1)
        Show-Reply 'Pl2 (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.Pl2)
        Show-Reply 'PerfMode' (Invoke-Helper -Cmd $CmdGet -Function $Fn.PerfMode)
        Show-TdpRegistry
        Write-Host ''
        Write-Host 'PL2 follows PL1 by the firmware headroom, so a different number is correct.' -ForegroundColor DarkGray
        Write-Host 'If PerfMode is not 1 (User Scenario) the limits are stored but not applied.' -ForegroundColor DarkGray
        return
    }

    if ($PSBoundParameters.ContainsKey('ChargeLimit')) {
        $enable = if ($ChargeLimit -ge 100) { '0' } else { '1' }
        Write-Host ''
        Write-Host "=== Setting the charge limit to $ChargeLimit% ===" -ForegroundColor Cyan
        Show-Reply 'ChargeLimitEnabled ->' (Invoke-Helper -Cmd $CmdSet -Function $Fn.ChargeLimitEnabled -Val $enable)
        if ($enable -eq '1') {
            Show-Reply 'ChargeLimitPercent ->' (Invoke-Helper -Cmd $CmdSet -Function $Fn.ChargeLimitPercent -Val "$ChargeLimit")
        }
        Show-Reply 'Enabled (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.ChargeLimitEnabled)
        Show-Reply 'Percent (read back)' (Invoke-Helper -Cmd $CmdGet -Function $Fn.ChargeLimitPercent)
        Show-BatteryRegistry
        Write-Host ''
        Write-Host 'This proves the value was stored. It does NOT prove charging stops -' -ForegroundColor DarkGray
        Write-Host 'for that, run Watch-Battery.ps1 with the charger connected.' -ForegroundColor DarkGray
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
