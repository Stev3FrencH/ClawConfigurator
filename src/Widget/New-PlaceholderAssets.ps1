<#
.SYNOPSIS
    Generates the PNG tile assets the MSIX manifests require.

.DESCRIPTION
    Both manifests reference five images. They are not optional: MakeAppx fails the build if any of
    them is missing, and the failure names the file rather than explaining that no art exists yet -
    which reads like a broken repository on a first build.

    The art is THREE CLAW SLASHES in the device purple. Drawn rather than committed, for the same
    reason as before: generated binaries in a repository are a merge conflict waiting to happen, and
    this takes two seconds to recreate. Run once per clone.

    These stopped being placeholders on 2026-08-14, when the app became Claw Configurator. The
    previous ring-and-dot was deliberately provisional so nobody mistook it for finished artwork;
    this is meant to be the real thing, within what can be drawn cleanly with plain geometry.

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

# Matches the widget's card background, so the tile reads as the same app.
$background = [System.Drawing.Color]::FromArgb(255, 27, 33, 41)

# The device purple. LightingProfile.Default(1) - the seeded "Purple" profile - is #7F00FF, chosen
# because it matches the hardware; this is that hue lifted for legibility, because #7F00FF against
# #1B2129 goes muddy once the tile is 44px. Same colour family, readable at every size.
$claw = [System.Drawing.Color]::FromArgb(255, 161, 107, 255)

# Name, width, height. Every one is referenced by src/Package/Package.appxmanifest;
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

<#
    One claw gash: pointed at BOTH ends, fattest in the middle, and bowed.

    The first attempt drew each mark wide at the top tapering to a tail, which rendered as three
    blunt brush strokes - and at tile size read uncomfortably like an M, which is the branding this
    rename is leaving behind. A gash is a lens: two curved edges meeting at a sharp point at each
    end. That is what makes it read as something torn rather than something painted.

    Built from two beziers sharing both endpoints. The control points sit at a constant offset from
    the centreline, so the widest part lands mid-span and both ends converge to a point on their
    own - no taper arithmetic needed.
#>
function Add-Gash {
    param(
        [System.Drawing.Drawing2D.GraphicsPath]$Path,
        [float]$X0, [float]$Y0,   # leading point
        [float]$X1, [float]$Y1,   # trailing point
        [float]$Width,            # at its widest, mid-span
        [float]$Bow               # how far the whole mark curves, perpendicular to the run
    )

    $dx = $X1 - $X0
    $dy = $Y1 - $Y0
    $len = [Math]::Sqrt(($dx * $dx) + ($dy * $dy))
    if ($len -le 0) { return }

    # Unit perpendicular. Width and bow are both applied along it.
    $nx = -$dy / $len
    $ny = $dx / $len

    # Beziers reach roughly 3/4 of the way to their control points, so overshooting by 4/3 puts the
    # actual edge at the requested width rather than somewhere short of it.
    $reach = ($Width / 2) * 1.34
    $bowOut = $Bow * 1.34

    $gash = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        # One edge out, the other back - the two bows differ by the width, which is what opens the
        # lens rather than drawing a line twice.
        $gash.AddBezier(
            $X0, $Y0,
            ($X0 + $dx * 0.30 + $nx * ($bowOut + $reach)), ($Y0 + $dy * 0.30 + $ny * ($bowOut + $reach)),
            ($X0 + $dx * 0.70 + $nx * ($bowOut + $reach)), ($Y0 + $dy * 0.70 + $ny * ($bowOut + $reach)),
            $X1, $Y1)

        $gash.AddBezier(
            $X1, $Y1,
            ($X0 + $dx * 0.70 + $nx * ($bowOut - $reach)), ($Y0 + $dy * 0.70 + $ny * ($bowOut - $reach)),
            ($X0 + $dx * 0.30 + $nx * ($bowOut - $reach)), ($Y0 + $dy * 0.30 + $ny * ($bowOut - $reach)),
            $X0, $Y0)

        $gash.CloseFigure()
        $Path.AddPath($gash, $false)
    }
    finally {
        $gash.Dispose()
    }
}

Write-Host ''
Write-Host "=== Generating tile assets into $OutputDirectory ===" -ForegroundColor Cyan

foreach ($asset in $assets) {
    $bitmap = New-Object System.Drawing.Bitmap($asset.Width, $asset.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear($background)

        # The mark is sized off the SHORT edge and centred, so the wide tile and the splash screen
        # get the same glyph rather than a stretched one.
        $short = [Math]::Min($asset.Width, $asset.Height)

        $cx = $asset.Width / 2
        $cy = $asset.Height / 2

        # Deliberately diagonal. An earlier pass ran these closer to vertical and they read as three
        # brush strokes; the rake is what makes the trio look like one swipe.
        $run = $short * 0.42      # horizontal travel of each gash
        $rise = $short * 0.56     # vertical travel
        $width = $short * 0.115
        $gap = $short * 0.20      # between gashes, measured across the run
        $bow = $short * 0.055

        # Three gashes side by side. The middle one runs longest, which is what stops them reading
        # as a repeated shape and starts them reading as one mark.
        #
        # All three share ONE centreline. An earlier pass lifted the outer two to sit the trio on a
        # shallow arc, which pushed the middle gash visibly off centre - the extra length already
        # does the work of making it the focal one, and it only reads as deliberate when it is
        # squarely between its neighbours.
        for ($i = -1; $i -le 1; $i++) {
            $scale = if ($i -eq 0) { 1.18 } else { 1.0 }

            $halfRun = ($run * $scale) / 2
            $halfRise = ($rise * $scale) / 2

            $offsetX = $gap * $i

            Add-Gash -Path $path `
                -X0 ([float]($cx + $offsetX - $halfRun)) `
                -Y0 ([float]($cy - $halfRise)) `
                -X1 ([float]($cx + $offsetX + $halfRun)) `
                -Y1 ([float]($cy + $halfRise)) `
                -Width ([float]$width) `
                -Bow ([float]$bow)
        }

        $brush = New-Object System.Drawing.SolidBrush($claw)
        try { $graphics.FillPath($brush, $path) }
        finally { $brush.Dispose() }

        $file = Join-Path $OutputDirectory $asset.Name
        $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("  {0,-24} {1}x{2}" -f $asset.Name, $asset.Width, $asset.Height) -ForegroundColor Green
    }
    finally {
        $path.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

Write-Host ''
Write-Host 'Done. Copy this Assets folder into the packaging project as well - the shipping' -ForegroundColor DarkGray
Write-Host 'manifest resolves its paths relative to itself, not to the widget project.' -ForegroundColor DarkGray
