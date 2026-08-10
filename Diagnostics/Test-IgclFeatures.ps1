<#
.SYNOPSIS
    Asks the Intel graphics driver which 3D features it supports, at GLOBAL scope.

.DESCRIPTION
    The widget's Graphics card is meant to control Intel settings GLOBALLY, not per-game. This
    answers whether that is possible for each feature, on whatever machine it is run on.

    Everything here is READ-ONLY: ctlInit, enumerate adapters, one GET per feature, ctlClose.
    Nothing is written.

    WHY ONE GET PER FEATURE rather than ctlGetSupported3DCapabilities: that call hands back a
    driver-allocated ctl_3d_feature_details_t ARRAY, and walking it needs the element size exactly
    right - it depends on nested property-info unions this has not verified. Wrong stride and the
    pointer walks off the end. ctl_3d_feature_getset_t instead leads with a Size field the driver
    validates, so a layout mistake comes back as UNSUPPORTED_SIZE rather than as a fault.

    READ THE RESULT CODES CAREFULLY. Three different answers matter and two of them look alike:

      SUPPORTED             readable now at this scope. Usable.
      UNSUPPORTED_FEATURE   the driver does not implement it on this adapter. Not usable, ever.
      DATA_NOT_FOUND        the driver KNOWS the feature but has nothing stored at this scope.
                            NOT the same as unsupported - most likely nothing has been configured
                            globally yet, and a write would create it.

.NOTES
    No elevation needed. Struct layouts confirmed working against ControlLib 1.2.257; the Size
    fields mean a mismatch is rejected rather than misread, so it is safe to try on any version.

    Pass an executable name to query per-application scope instead, purely for comparison - the
    widget does not use per-app profiles.

.EXAMPLE
    .\Test-IgclFeatures.ps1

.EXAMPLE
    .\Test-IgclFeatures.ps1 -ApplicationName game.exe

.EXAMPLE
    .\Test-IgclFeatures.ps1 | Tee-Object -FilePath igcl-claw.txt
#>

[CmdletBinding()]
param(
    [string]$ApplicationName
)

$ErrorActionPreference = 'Stop'

$interop = @'
using System;
using System.Runtime.InteropServices;

public static class Igcl
{
    [StructLayout(LayoutKind.Sequential)]
    public struct AppId
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;
    }

    // ctl_init_args_t. Version is a uint8 followed by a uint32, so the compiler pads by three -
    // default packing on purpose, to match how the driver was compiled. 36 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public AppId ApplicationUID;
    }

    // ctl_property_t, sized to its widest member: {bool,float} and {bool,int32} are both 8 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyValue
    {
        public uint EnableOrType;
        public int Value;
    }

    // ctl_3d_feature_getset_t. 48 bytes. The Size field is what the driver validates.
    [StructLayout(LayoutKind.Sequential)]
    public struct FeatureGetSet
    {
        public uint Size;
        public byte Version;
        public int FeatureType;
        public IntPtr ApplicationName;
        public int ValueType;
        public PropertyValue Value;
        public int CustomValueSize;
        public IntPtr pCustomValue;
    }

    [DllImport("ControlLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ctlInit(ref InitArgs pInitDesc, out IntPtr phAPIHandle);

    [DllImport("ControlLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ctlClose(IntPtr hAPIHandle);

    [DllImport("ControlLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ctlEnumerateDevices(IntPtr hAPIHandle, ref uint pCount, IntPtr phDevices);

    [DllImport("ControlLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ctlGetSet3DFeature(IntPtr hDAhandle, ref FeatureGetSet pFeature);
}
'@

Add-Type -TypeDefinition $interop -ErrorAction Stop

# ctl_3d_feature_t, from igcl_api.h.
$features = [ordered]@{
    0  = 'FRAME_PACING';       1  = 'ENDURANCE_GAMING';  2  = 'FRAME_LIMIT'
    3  = 'ANISOTROPIC';        4  = 'CMAA';              5  = 'TEXTURE_FILTERING_QUALITY'
    6  = 'ADAPTIVE_TESSELLATION'; 7 = 'SHARPENING_FILTER'; 8 = 'MSAA'
    9  = 'GAMING_FLIP_MODES';  10 = 'ADAPTIVE_SYNC_PLUS'; 11 = 'APP_PROFILES'
    12 = 'APP_PROFILE_DETAILS'; 13 = 'EMULATED_TYPED_64BIT_ATOMICS'
    14 = 'VRR_WINDOWED_BLT';   15 = 'GLOBAL_OR_PER_APP'; 16 = 'LOW_LATENCY'
    17 = 'FRAME_GENERATION';   18 = 'PREBUILT_SHADER_DOWNLOAD'; 19 = 'LIVE_STATE'
}

# The three the widget cares about, called out in the summary.
$widgetFeatures = @(1, 16, 17)

$valueTypes = @('bool', 'float', 'int32', 'uint32', 'enum', 'custom')

function Get-CtlResultName([int]$code) {
    switch ($code) {
        0          { 'SUCCESS' }
        0x40000003 { 'DEVICE_LOST' }
        0x40000006 { 'INSUFFICIENT_PERMISSIONS' }
        0x40000007 { 'NOT_AVAILABLE' }
        0x40000009 { 'UNSUPPORTED_VERSION' }
        0x4000000A { 'UNSUPPORTED_FEATURE' }
        0x4000000B { 'INVALID_ARGUMENT' }
        0x4000000C { 'INVALID_API_HANDLE' }
        0x4000000E { 'INVALID_NULL_POINTER' }
        0x4000000F { 'INVALID_SIZE' }
        0x40000010 { 'UNSUPPORTED_SIZE' }
        0x40000014 { 'DATA_NOT_FOUND' }
        0x40000015 { 'NOT_IMPLEMENTED' }
        0x40000016 { 'OS_CALL' }
        0x40000017 { 'KMD_CALL' }
        0x40000020 { 'PLATFORM_NOT_SUPPORTED' }
        default    { '0x{0:X8}' -f $code }
    }
}

# ── Report the environment first, so a result is never read without its context ──────────────
$dll = Join-Path $env:SystemRoot 'System32\ControlLib.dll'
if (-not (Test-Path $dll)) {
    Write-Warning "ControlLib.dll not found at $dll. It ships with the Intel graphics driver."
    return
}

Write-Host ''
Write-Host "ControlLib.dll  $((Get-Item $dll).VersionInfo.FileVersion)" -ForegroundColor Cyan

Get-CimInstance Win32_VideoController |
    Where-Object { $_.PNPDeviceID -like '*VEN_8086*' } |
    ForEach-Object { Write-Host "Intel GPU:      $($_.Name)  driver $($_.DriverVersion)" -ForegroundColor Cyan }

$scope = if ($ApplicationName) { "per-application (`"$ApplicationName`")" } else { 'GLOBAL' }
Write-Host "Scope:          $scope" -ForegroundColor Cyan
Write-Host ''

# ── Initialise ──────────────────────────────────────────────────────────────────────────────
$args = New-Object Igcl+InitArgs
$args.Size = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Igcl+InitArgs])
$args.Version = 0
$args.AppVersion = (1 -shl 16) -bor 1      # CTL_MAKE_VERSION(1,1)
$args.Flags = 0
$args.SupportedVersion = 0
$uid = New-Object Igcl+AppId
$uid.Data4 = New-Object byte[] 8
$args.ApplicationUID = $uid

$api = [IntPtr]::Zero
$result = [Igcl]::ctlInit([ref]$args, [ref]$api)

if ($result -ne 0) {
    Write-Warning "ctlInit FAILED: $(Get-CtlResultName $result)"
    Write-Warning 'Everything downstream needs this handle. G6 is blocked here.'
    return
}

Write-Host "ctlInit OK (driver supports version $($args.SupportedVersion -shr 16).$($args.SupportedVersion -band 0xFFFF))" -ForegroundColor Green

$appNamePtr = [IntPtr]::Zero
if ($ApplicationName) {
    $appNamePtr = [System.Runtime.InteropServices.Marshal]::StringToHGlobalAnsi($ApplicationName)
}

try {
    # Two-call idiom: ask for the count, then fetch. Guessing the count truncates silently.
    $count = 0
    $result = [Igcl]::ctlEnumerateDevices($api, [ref]$count, [IntPtr]::Zero)
    if ($result -ne 0) {
        Write-Warning "ctlEnumerateDevices (count) FAILED: $(Get-CtlResultName $result)"
        return
    }

    Write-Host "Adapters:       $count" -ForegroundColor Green
    if ($count -eq 0) { Write-Warning 'No Intel adapter enumerated.'; return }

    $buffer = [System.Runtime.InteropServices.Marshal]::AllocHGlobal([IntPtr]::Size * $count)
    try {
        $result = [Igcl]::ctlEnumerateDevices($api, [ref]$count, $buffer)
        if ($result -ne 0) {
            Write-Warning "ctlEnumerateDevices (fetch) FAILED: $(Get-CtlResultName $result)"
            return
        }

        for ($i = 0; $i -lt $count; $i++) {
            $adapter = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($buffer, $i * [IntPtr]::Size)

            Write-Host ''
            Write-Host "--- adapter [$i] ---" -ForegroundColor Cyan
            Write-Host ''

            $rows = foreach ($id in $features.Keys) {
                $request = New-Object Igcl+FeatureGetSet
                $request.Size = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Igcl+FeatureGetSet])
                $request.Version = 0
                $request.FeatureType = $id
                $request.ApplicationName = $appNamePtr
                $request.ValueType = 0
                $request.CustomValueSize = 0
                $request.pCustomValue = [IntPtr]::Zero

                $r = [Igcl]::ctlGetSet3DFeature($adapter, [ref]$request)

                $status = if ($r -eq 0) { 'SUPPORTED' } else { Get-CtlResultName $r }
                $detail = ''
                if ($r -eq 0) {
                    $t = if ($request.ValueType -ge 0 -and $request.ValueType -lt $valueTypes.Count) {
                        $valueTypes[$request.ValueType]
                    } else { "type $($request.ValueType)" }
                    $detail = "$t  enable/type=$($request.Value.EnableOrType) value=$($request.Value.Value)"
                }

                [pscustomobject]@{
                    Feature = $features[$id]
                    Status  = $status
                    Detail  = $detail
                    Widget  = if ($widgetFeatures -contains $id) { '<--' } else { '' }
                }
            }

            $rows | Format-Table -AutoSize

            Write-Host 'The three the widget wants:' -ForegroundColor Yellow
            foreach ($id in $widgetFeatures) {
                $row = $rows | Where-Object Feature -eq $features[$id]
                $colour = switch -Wildcard ($row.Status) {
                    'SUPPORTED'           { 'Green' }
                    'UNSUPPORTED_FEATURE' { 'Red' }
                    default               { 'Yellow' }
                }
                Write-Host ("  {0,-20} {1}" -f $features[$id], $row.Status) -ForegroundColor $colour
            }
        }
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    }
}
finally {
    if ($appNamePtr -ne [IntPtr]::Zero) {
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($appNamePtr)
    }
    [void][Igcl]::ctlClose($api)
}

Write-Host ''
Write-Host 'How to read this:' -ForegroundColor Yellow
Write-Host '  SUPPORTED            usable globally, right now.'
Write-Host '  UNSUPPORTED_FEATURE  not implemented on this adapter. Hide the control.'
Write-Host '  DATA_NOT_FOUND       the driver knows it but has nothing stored at this scope.'
Write-Host '                       NOT the same as unsupported - most likely nothing has been'
Write-Host '                       configured globally yet, and a write would create it.'
Write-Host ''
