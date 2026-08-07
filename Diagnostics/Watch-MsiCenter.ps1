<#
.SYNOPSIS
    Captures what MSI Center M changes when you adjust a setting. Read-only observation.

.DESCRIPTION
    Answers gate G1 - how TDP actually reaches the hardware - by observing MSI's own software
    rather than guessing magic numbers.

    Run it, change ONE setting in MSI Center M while it waits, then let it diff. Repeat with a
    second value to confirm units: 17 W could be stored as 17, as 17000, or as a raw EC count,
    and one sample cannot tell you which.

    Captures two things:
      * Registry writes under the MSI keys, which is where the 'User Scenario\ManualPL*' mirror
        lives if this device uses it.
      * WMI activity, if -TraceWmi is given, which shows direct ACPI-WMI method calls.

    Read-only: it observes, snapshots and diffs. It never writes to the device.

.EXAMPLE
    # Set PL1 to 17 W in MSI Center first, then:
    .\Watch-MsiCenter.ps1 -Label pl1-17

    # Then set PL1 to 25 W and run again:
    .\Watch-MsiCenter.ps1 -Label pl1-25

    # Compare the two 'after' files to see which value moved and in what units.
#>
[CmdletBinding()]
param(
    # Included in output filenames so several captures can be compared.
    [Parameter(Mandatory)]
    [string] $Label,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'transcripts'),

    # Seconds to wait while you change the setting in MSI Center M.
    [int] $WaitSeconds = 25,

    # Also capture the WMI activity trace. Needs elevation.
    [switch] $TraceWmi
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# Every root MSI is known to use. Exported wholesale rather than reading specific values,
# because which value moves is exactly what we do not yet know.
$registryRoots = @(
    'HKLM\SOFTWARE\MSI',
    'HKLM\SOFTWARE\WOW6432Node\MSI',
    'HKCU\SOFTWARE\MSI'
)

function Export-MsiRegistry([string] $tag) {
    $files = @()
    foreach ($root in $registryRoots) {
        $safe = ($root -replace '[\\:]', '_')
        $path = Join-Path $OutputDirectory "$Label-$tag-$safe.reg"

        # reg.exe fails loudly on a missing key; that is fine and expected for some roots.
        & reg.exe export $root $path /y 2>$null | Out-Null
        if (Test-Path $path) { $files += $path }
    }
    return $files
}

Write-Host "=== Snapshot: before ===" -ForegroundColor Cyan
$before = Export-MsiRegistry 'before'
Write-Host "  captured $($before.Count) registry root(s)"

if ($TraceWmi) {
    Write-Host "  enabling the WMI activity trace"
    & wevtutil.exe sl Microsoft-Windows-WMI-Activity/Trace /e:true 2>$null | Out-Null
    $traceStart = Get-Date
}

Write-Host ""
Write-Host "NOW: change exactly ONE setting in MSI Center M." -ForegroundColor Yellow
Write-Host "Waiting $WaitSeconds seconds..." -ForegroundColor Yellow
for ($i = $WaitSeconds; $i -gt 0; $i--) {
    Write-Host -NoNewline "`r  $i  "
    Start-Sleep -Seconds 1
}
Write-Host "`r         "

Write-Host "=== Snapshot: after ===" -ForegroundColor Cyan
$after = Export-MsiRegistry 'after'
Write-Host "  captured $($after.Count) registry root(s)"

if ($TraceWmi) {
    & wevtutil.exe sl Microsoft-Windows-WMI-Activity/Trace /e:false 2>$null | Out-Null

    $wmiPath = Join-Path $OutputDirectory "$Label-wmi.txt"
    Get-WinEvent -LogName Microsoft-Windows-WMI-Activity/Trace -ErrorAction SilentlyContinue |
        Where-Object { $_.TimeCreated -ge $traceStart } |
        Where-Object { $_.Message -match 'root\\wmi|MSI|ACPI' } |
        Select-Object TimeCreated, Id, Message |
        Format-List | Out-File -FilePath $wmiPath -Encoding utf8

    Write-Host "  WMI trace -> $wmiPath"
}

Write-Host ""
Write-Host "=== Differences ===" -ForegroundColor Cyan

$diffPath = Join-Path $OutputDirectory "$Label-diff.txt"
$diffLines = @()

foreach ($beforeFile in $before) {
    $afterFile = $beforeFile -replace '-before-', '-after-'
    if (-not (Test-Path $afterFile)) { continue }

    # .reg exports are UTF-16; Compare-Object on the decoded lines is enough to spot a changed
    # value, and keeps this dependency-free.
    $b = Get-Content $beforeFile -Encoding Unicode -ErrorAction SilentlyContinue
    $a = Get-Content $afterFile -Encoding Unicode -ErrorAction SilentlyContinue

    $delta = Compare-Object -ReferenceObject $b -DifferenceObject $a -ErrorAction SilentlyContinue
    if ($delta) {
        $diffLines += "--- $(Split-Path -Leaf $beforeFile) ---"
        foreach ($d in $delta) {
            $marker = if ($d.SideIndicator -eq '=>') { 'AFTER ' } else { 'BEFORE' }
            $diffLines += "  $marker  $($d.InputObject)"
        }
        $diffLines += ''
    }
}

if ($diffLines.Count -eq 0) {
    Write-Warning "No registry changes detected."
    Write-Host ""
    Write-Host "That is a meaningful result, not a failure. It suggests MSI Center wrote straight"
    Write-Host "to ACPI-WMI or through a private driver rather than through the registry mirror."
    Write-Host "Re-run with -TraceWmi, and check the decompiled helper's TDP backend."
} else {
    $diffLines | Tee-Object -FilePath $diffPath
    Write-Host ""
    Write-Host "Diff -> $diffPath" -ForegroundColor Green
}

Write-Host ""
Write-Host @"
Interpreting this:
  * A changed value under a 'User Scenario' key is the registry-mirror path. Record the exact
    key, value name and type.
  * Run this at TWO different settings before trusting the units. 17 W may be stored as 17,
    as 17000, or as a raw EC count.
  * Then check whether the value still takes effect with the MSI Center SERVICE stopped. If it
    does not, this path hard-requires MSI Center to keep running - which matters, because
    replacing MSI Center is the point of this project.
  * To see which process performs the write, run Procmon alongside, filtered to
    Operation is RegSetValue and Path contains 'User Scenario'.
"@
