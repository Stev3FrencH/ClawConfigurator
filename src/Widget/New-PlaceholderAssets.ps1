<#
.SYNOPSIS
    Generates the placeholder PNG assets the MSIX manifest requires.

.DESCRIPTION
    Both manifests reference five images. They are not optional: MakeAppx fails the build if any
    of them is missing, and the failure names the file rather than explaining that no art exists
    yet - which reads like a broken repository on a first build.

    These are deliberately plain: the accent colour with the app's initials. Enough to build,
    install and pin the widget, and obviously provisional so nobody mistakes them for finished
    artwork.

    Run once per clone. The output is git-ignored, because generated binaries in a repository are
    a merge conflict waiting to happen and this takes two seconds to recreate.

.NOTES
    Windows only - it uses System.Drawing. Run from anywhere; output goes to the Assets folder
    beside this script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\New-PlaceholderAssets.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Assets')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Matches AccentBrush in App.xaml, so the tile reads as the same app as the widget.
$background = [System.Drawing.Color]::FromArgb(255, 27, 33, 41)
$accent = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)

# Name, width, height. Every one of these is referenced by src/Package/Package.appxmanifest;
# Wide310x150Logo is used only by the shipping manifest's DefaultTile.
$assets = @(
    @{ Name = 'StoreLogo.png';          Width = 50;  Height = 50 },
    @{ Name = 'Square44x44Logo.png';    Width = 44;  Height = 44 },
    @{ Name = 'Square150x150Logo.png';  Width = 150; Height = 150 },
    @{ Name = 'Wide310x150Logo.png';    Width = 310; Height = 150 },
    @{ Name = 'SplashScreen.png';       Width = 620; Height = 300 }
)

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

Write-Host ''
Write-Host "=== Generating placeholder assets into $OutputDirectory ===" -ForegroundColor Cyan

foreach ($asset in $assets) {
    $bitmap = New-Object System.Drawing.Bitmap($asset.Width, $asset.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $graphics.Clear($background)

        # A ring rather than filled text: it stays legible at 44px, where any glyph large enough
        # to read would touch the edges.
        $inset = [Math]::Max(2, [int]([Math]::Min($asset.Width, $asset.Height) * 0.18))
        $diameter = [Math]::Min($asset.Width, $asset.Height) - ($inset * 2)
        $x = ($asset.Width - $diameter) / 2
        $y = ($asset.Height - $diameter) / 2

        $penWidth = [Math]::Max(2, [int]($diameter * 0.12))
        $pen = New-Object System.Drawing.Pen($accent, $penWidth)
        try { $graphics.DrawEllipse($pen, $x, $y, $diameter, $diameter) }
        finally { $pen.Dispose() }

        # The centre dot only fits once the tile is big enough for it to read as deliberate.
        if ($diameter -ge 60) {
            $dot = [int]($diameter * 0.22)
            $brush = New-Object System.Drawing.SolidBrush($accent)
            try {
                $graphics.FillEllipse($brush,
                    ($asset.Width - $dot) / 2, ($asset.Height - $dot) / 2, $dot, $dot)
            }
            finally { $brush.Dispose() }
        }

        $path = Join-Path $OutputDirectory $asset.Name
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("  {0,-24} {1}x{2}" -f $asset.Name, $asset.Width, $asset.Height) -ForegroundColor Green
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

Write-Host ''
Write-Host 'Done. Copy this Assets folder into the packaging project as well - the shipping' -ForegroundColor DarkGray
Write-Host 'manifest resolves its paths relative to itself, not to the widget project.' -ForegroundColor DarkGray
