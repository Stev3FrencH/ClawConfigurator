<#
.SYNOPSIS
    Captures a PNG of the Claw Configurator widget as it appears in the Game Bar.

.DESCRIPTION
    Read-only. Takes a picture and writes a file; touches no hardware and no settings.

    Screenshotting this widget is more awkward than it sounds, which is why this exists rather
    than "press PrtScn". The widget is a UWP window composited into the Game Bar's overlay, and a
    plain screen grab (BitBlt from the screen DC) frequently returns the desktop *without* the
    overlay drawn on it. Worse, the Game Bar closes when it loses focus - so anything that requires
    you to click elsewhere to start the capture closes the very thing being captured.

    So this does two things differently:

      1. It counts down, giving you time to open the Game Bar and leave it focused.
      2. It prefers PrintWindow with PW_RENDERFULLCONTENT against the widget's own window, which
         asks the window to render itself and works for DirectComposition content that BitBlt
         misses. That also crops to just the widget - which is what you actually want for a README
         or a release page, rather than a 1920x1200 desktop with a small panel in the corner.

    If the widget window cannot be found it falls back to a full-screen grab and says so, rather
    than failing and wasting the countdown.

.PARAMETER OutputPath
    Where to write the PNG. Defaults to the Desktop, named after the date.

.PARAMETER Delay
    Seconds to wait before capturing. Default 8 - enough to press Win+G and let the widget draw.

.PARAMETER FullScreen
    Skip the window hunt and capture the whole screen.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Capture-Widget.ps1
    # then press Win+G and leave the Game Bar open

.EXAMPLE
    .\Capture-Widget.ps1 -Delay 15 -OutputPath .\widget.png
#>
[CmdletBinding()]
param(
    [string] $OutputPath,
    [int]    $Delay = 8,
    [switch] $FullScreen
)

$ErrorActionPreference = 'Stop'

if (-not $OutputPath) {
    $name = "claw-configurator-widget-$(Get-Date -Format 'yyyy-MM-dd_HHmmss').png"
    $OutputPath = Join-Path ([Environment]::GetFolderPath('Desktop')) $name
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WidgetCapture
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static System.Collections.Generic.List<IntPtr> Windows = new System.Collections.Generic.List<IntPtr>();

    public static void Collect(IntPtr parent)
    {
        Windows.Clear();
        EnumProc proc = delegate(IntPtr h, IntPtr l) { Windows.Add(h); return true; };
        if (parent == IntPtr.Zero) EnumWindows(proc, IntPtr.Zero);
        else EnumChildWindows(parent, proc, IntPtr.Zero);
    }

    public static string Title(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        StringBuilder sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassOf(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessOf(IntPtr h)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return pid;
    }
}
"@

# ── Countdown ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Open the Game Bar now (Win+G) and leave the widget on screen." -ForegroundColor Cyan
Write-Host "Do not click away - the Game Bar closes when it loses focus." -ForegroundColor DarkGray
Write-Host ""
for ($i = $Delay; $i -gt 0; $i--) {
    Write-Host "`r  capturing in $i... " -NoNewline -ForegroundColor Yellow
    Start-Sleep -Seconds 1
}
Write-Host "`r  capturing now.      " -ForegroundColor Green

# ── Find the widget's window ──────────────────────────────────────────────────
$target = [IntPtr]::Zero
$targetName = ''

if (-not $FullScreen) {
    # The widget process is still called McenterLite.Widget - only the helper was renamed, because
    # the Appx tooling derives the manifest EntryPoint from the assembly name. See README, "The name".
    $pids = @(Get-Process -Name 'McenterLite.Widget' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })

    if ($pids.Count -eq 0) {
        Write-Warning "McenterLite.Widget is not running - the Game Bar may not have been opened yet."
    }
    else {
        [WidgetCapture]::Collect([IntPtr]::Zero)
        $best = 0
        foreach ($h in [WidgetCapture]::Windows) {
            if (-not [WidgetCapture]::IsWindowVisible($h)) { continue }
            if ($pids -notcontains [int][WidgetCapture]::ProcessOf($h)) { continue }

            $r = New-Object 'WidgetCapture+RECT'
            if (-not [WidgetCapture]::GetWindowRect($h, [ref] $r)) { continue }
            $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)

            # Largest visible window belonging to the widget. UWP apps carry several, most of them
            # zero-sized bookkeeping windows.
            if ($area -gt $best) {
                $best = $area
                $target = $h
                $targetName = "$([WidgetCapture]::ClassOf($h)) $([WidgetCapture]::Title($h))".Trim()
            }
        }
    }
}

# ── Capture ───────────────────────────────────────────────────────────────────
$captured = $false

if ($target -ne [IntPtr]::Zero) {
    $r = New-Object 'WidgetCapture+RECT'
    [void][WidgetCapture]::GetWindowRect($target, [ref] $r)
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top

    if ($w -gt 0 -and $h -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $gfx.GetHdc()

        # 2 = PW_RENDERFULLCONTENT. Without it, DirectComposition content comes back blank.
        $ok = [WidgetCapture]::PrintWindow($target, $hdc, 2)

        $gfx.ReleaseHdc($hdc)
        $gfx.Dispose()

        if ($ok) {
            $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "  captured the widget window ($w x $h) - $targetName" -ForegroundColor Green
            $captured = $true
        }
        else {
            Write-Warning "  PrintWindow refused; falling back to the full screen."
        }
        $bmp.Dispose()
    }
}

if (-not $captured) {
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen

    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
    $gfx.Dispose()
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host "  captured the full screen ($($bounds.Width) x $($bounds.Height))" -ForegroundColor Yellow
    Write-Host "  If the widget is missing from it, the overlay was not composited into the grab." -ForegroundColor DarkGray
    Write-Host "  Try again once the Game Bar is open, or use Snipping Tool's delay." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Saved: $OutputPath" -ForegroundColor Green
Write-Host ""
