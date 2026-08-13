<#
.SYNOPSIS
    Proves whether MSI_ACPI.Set_Fan actually takes, with a guaranteed restore. Gate G2's last
    unknown.

.DESCRIPTION
    This is the FIRST write this project has ever made to the fan table. Everything about the read
    is measured (docs/hardware-notes.md, Gate G2); nothing about the write is.

    The test is deliberately the smallest one that can answer the question:

      * ONE fan. Fan 2 is left alone and read throughout, so if both move when only one was
        addressed, that shows up immediately - and it is the failure mode that matters most,
        because it would mean the sub-function is not a fan selector at all.
      * ONE byte changed, the top curve point, and only upward. A fan that runs slightly harder at
        78 C is a safe direction to be wrong in. Nothing here can slow a fan down.
      * The idle duty is left at the factory 58, so the device cannot end up quieter than stock
        even if the write lands somewhere unexpected.

    RESTORE IS GUARANTEED. The factory table is written back in a finally block, so an exception
    mid-test still restores. Ctrl+C is the one case PowerShell cannot promise, so if you interrupt
    it, run this afterwards:

        McenterLite.Probe.exe --set-fan both auto

.PARAMETER TopPoint
    Duty for the 78 C point during the test. Default 90, against a factory 84.

.NOTES
    Requires elevation. Run it with nothing else heavy going on, so an audible fan change is
    attributable.

.EXAMPLE
    .\Test-SetFan.ps1
#>

[CmdletBinding()]
param(
    [ValidateRange(85, 100)]
    [int]$TopPoint = 90,

    [string]$ProbePath = (Join-Path $PSScriptRoot '..\src\Probe\bin\Debug\net8.0-windows\McenterLite.Probe.exe')
)

$ErrorActionPreference = 'Continue'

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) { throw 'Run this elevated. root\wmi vendor classes are not readable otherwise.' }

$probe = Resolve-Path -Path $ProbePath -ErrorAction SilentlyContinue
if (-not $probe) {
    throw "Probe not found at $ProbePath. Build it first:`n" +
          "  dotnet build .\src\Probe\McenterLite.Probe.csproj -c Debug"
}

# The factory table, measured on this device in Auto on 2026-08-12. Both fans read identically.
$factory = '58;70;74;76;78;80;84'
$test    = "58;70;74;76;78;80;$TopPoint"

$transcriptDirectory = Join-Path $PSScriptRoot 'transcripts'
New-Item -ItemType Directory -Force -Path $transcriptDirectory | Out-Null
$transcript = Join-Path $transcriptDirectory ("set-fan-test-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $transcript | Out-Null

# Deliberately returns NOTHING and reports through a script-scope variable.
#
# The first version ended `& $probe @ProbeArguments; return $LASTEXITCODE`, which returns the
# probe's stdout AND the exit code as one array - so the output never reached the host, and
# `(Invoke-Probe ...) -ne 0` filtered the array rather than comparing a number, yielding a
# non-empty array that is always truthy. A successful baseline read reported as a failure, and the
# reads at the call sites were being swallowed by `| Out-Null`. Out-Host keeps the output visible
# and out of the pipeline.
$script:ProbeExitCode = 0

function Invoke-Probe {
    param([string[]]$ProbeArguments, [string]$Heading)

    Write-Host ''
    Write-Host "--- $Heading ---" -ForegroundColor Cyan
    Write-Host "    $probe $($ProbeArguments -join ' ')" -ForegroundColor DarkGray

    & $probe @ProbeArguments | Out-Host
    $script:ProbeExitCode = $LASTEXITCODE
}

$restored = $false

try {
    Write-Host ''
    Write-Host '=== Gate G2: does Set_Fan take? ===' -ForegroundColor Cyan
    Write-Host "Factory : $factory" -ForegroundColor DarkGray
    Write-Host "Test    : $test   (fan 1 only, top point $TopPoint)" -ForegroundColor DarkGray

    Invoke-Probe @('--fan') 'BASELINE - both fans'
    if ($script:ProbeExitCode -ne 0) {
        throw "The baseline read failed (exit $script:ProbeExitCode). Nothing was written. Stopping here."
    }

    Write-Host ''
    Write-Host 'About to write to fan 1. Ctrl+C now if you have changed your mind.' -ForegroundColor Yellow
    Start-Sleep -Seconds 3

    Invoke-Probe @('--set-fan', '1', $test) 'WRITE - fan 1 only'
    $writeCode = $script:ProbeExitCode

    # A separate read of BOTH fans. The probe already confirms fan 1 internally; this is here to
    # show fan 2, which nothing addressed and which must therefore be unchanged.
    Invoke-Probe @('--fan') "READ BACK - fan 1 top should be $TopPoint, fan 2 must still be 84"

    Write-Host ''
    if ($writeCode -eq 0) {
        Write-Host 'WRITE TOOK. Check above that fan 2 is untouched.' -ForegroundColor Green
    }
    else {
        Write-Host "WRITE DID NOT TAKE (exit $writeCode). Read the FAILED lines above." -ForegroundColor Yellow
        Write-Host 'That is a clean negative result, not a broken device.' -ForegroundColor Yellow
    }
}
finally {
    Write-Host ''
    Write-Host '=== Restoring the factory table on BOTH fans ===' -ForegroundColor Cyan

    Invoke-Probe @('--set-fan', 'both', 'auto') 'RESTORE'
    $restored = ($script:ProbeExitCode -eq 0)

    Invoke-Probe @('--fan') 'FINAL STATE'

    Write-Host ''
    if ($restored) {
        Write-Host 'Restored. Both fans report the factory table.' -ForegroundColor Green
    }
    else {
        Write-Host 'RESTORE DID NOT CONFIRM. Check FINAL STATE above against:' -ForegroundColor Red
        Write-Host "  $factory" -ForegroundColor Red
        Write-Host 'If it does not match, set the fan back to Auto in MSI Center M.' -ForegroundColor Red
    }

    Stop-Transcript | Out-Null
    Write-Host ''
    Write-Host "Transcript -> $transcript" -ForegroundColor DarkGray
}
