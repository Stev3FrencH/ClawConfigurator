<#
.SYNOPSIS
    Dumps every installed Game Bar widget's manifest declaration, to find out how OEM widgets win
    the far-left slot in the compact nav bar.

.DESCRIPTION
    ASUS Armoury and MSI Quick Settings appear at the far left of the Game Bar compact nav bar,
    ahead of Home. That placement is driven by properties inside the <GameBarWidget> element of the
    widget's AppxManifest.xml, and those properties are undocumented publicly.

    The complete set Game Bar's parser understands was recovered from the string table around
    "IsDeviceWidget" in GameBar.exe:

        ActivateAfterInstall   CompactModePriorityPlacement   FavoriteAfterInstall
        HomeMenuVisible        IsDeviceWidget                 PinningSupported
        SettingsSupported      Window / Size / ResizeSupported

    The NAMES are certain. The VALUE FORMS are not - most siblings are plain booleans, but
    SettingsSupported takes an attribute instead (Microsoft.GamingApp declares
    <SettingsSupported AppExtensionId="XboxSettingsWidget" />), so a priority integer or an
    attribute form is equally plausible for CompactModePriorityPlacement.

    No widget on the development machine declares CompactModePriorityPlacement, so there is nothing
    to copy from there. THE CLAW IS DIFFERENT: MSI Quick Settings is installed and demonstrably
    does this. Run this there and copy whatever MSI actually declares.

    Also prints Game Bar's own per-widget runtime state, so a manifest declaration can be compared
    against what Game Bar recorded from it. Those are not the same thing - Edge GameAssist has
    compact placement set at runtime while declaring nothing in its manifest.

.NOTES
    Read-only. Changes nothing, needs no elevation.

    Run it on the CLAW. On a machine with no OEM widget installed it will simply confirm that
    nobody declares the interesting properties, which is the result the dev machine already gave.

.EXAMPLE
    .\Get-GameBarWidgets.ps1

.EXAMPLE
    .\Get-GameBarWidgets.ps1 | Tee-Object -FilePath gamebar-widgets.txt
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# The properties that decide placement, as opposed to sizing or install-time behaviour. These are
# what to compare between our widget and the OEM ones.
$PlacementProperties = @(
    'IsDeviceWidget'
    'CompactModePriorityPlacement'
    'HomeMenuVisible'
    'FavoriteAfterInstall'
    'ActivateAfterInstall'
)

function Get-WidgetPackages {
    foreach ($package in Get-AppxPackage) {
        if (-not $package.InstallLocation) { continue }

        $manifestPath = Join-Path $package.InstallLocation 'AppxManifest.xml'
        if (-not (Test-Path $manifestPath)) { continue }

        # Some packages under WindowsApps are unreadable even to their owner. Skip rather than
        # throw - one inaccessible package must not stop the sweep.
        $text = $null
        try { $text = Get-Content $manifestPath -Raw -ErrorAction Stop } catch { continue }

        if ($text -notmatch 'gameBarUIExtension') { continue }

        [pscustomobject]@{
            Name     = $package.Name
            Version  = $package.Version
            Manifest = $text
        }
    }
}

function Get-GameBarRuntimeState {
    $path = Join-Path $env:LOCALAPPDATA `
        'Packages\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\LocalState\profileDataSettings.txt'

    if (-not (Test-Path $path)) { return $null }

    try {
        # settingsStorage, not widgetProperties - the latter exists and is empty, which is an easy
        # way to conclude wrongly that Game Bar has recorded nothing.
        return ((Get-Content $path -Raw) | ConvertFrom-Json).profile.settingsStorage
    }
    catch {
        Write-Warning "Could not parse Game Bar's settings: $($_.Exception.Message)"
        return $null
    }
}

$widgets = @(Get-WidgetPackages)

if ($widgets.Count -eq 0) {
    Write-Warning 'No Game Bar widgets found. That is surprising - Game Bar ships several itself.'
    return
}

Write-Host ''
Write-Host "=== Game Bar widget manifests ($($widgets.Count) found) ===" -ForegroundColor Cyan
Write-Host ''

foreach ($widget in $widgets) {
    Write-Host "--- $($widget.Name)  $($widget.Version)" -ForegroundColor Green

    $block = [regex]::Match($widget.Manifest, '(?s)<GameBarWidget.*?</GameBarWidget>')
    if ($block.Success) {
        $block.Value
    }
    else {
        # Registers the extension but declares no widget properties - Game Bar itself does this.
        Write-Host '  (declares gameBarUIExtension but has no <GameBarWidget> block)' -ForegroundColor DarkGray
    }

    Write-Host ''
}

Write-Host '=== Placement properties, side by side ===' -ForegroundColor Cyan
Write-Host 'Blank = not declared. This is the comparison that matters.' -ForegroundColor DarkGray
Write-Host ''

$rows = foreach ($widget in $widgets) {
    $row = [ordered]@{ Widget = $widget.Name }

    foreach ($property in $PlacementProperties) {
        # Matches both <Prop>value</Prop> and <Prop attr="value" />, because the element form is
        # not guaranteed - SettingsSupported proves the attribute form exists.
        $element = [regex]::Match($widget.Manifest, "<$property>([^<]*)</$property>")
        $attrs = [regex]::Match($widget.Manifest, "<$property\s+([^/>]*)/>")

        $row[$property] =
            if ($element.Success) { $element.Groups[1].Value }
            elseif ($attrs.Success) { '[attr] ' + $attrs.Groups[1].Value.Trim() }
            else { '' }
    }

    [pscustomobject]$row
}

$rows | Format-Table -AutoSize -Wrap

$state = Get-GameBarRuntimeState
if ($state) {
    Write-Host '=== Game Bar runtime state ===' -ForegroundColor Cyan
    Write-Host 'What Game Bar RECORDED, which is not the same as what a manifest DECLARES.' -ForegroundColor DarkGray
    Write-Host ''

    foreach ($property in $state.PSObject.Properties) {
        $value = $property.Value
        $name = $property.Name -replace '^widget_', ''
        if ($name.Length -gt 44) { $name = $name.Substring(0, 44) }

        '{0,-46} fav={1,-5} pinned={2,-5} compactPlacement={3}' -f `
            $name, $value.isFavorite, $value.pinned, $value.useCompactModeFavoritePlacement
    }

    Write-Host ''
}

Write-Host 'What to look for:' -ForegroundColor Yellow
Write-Host '  An OEM widget (MSI Quick Settings, ASUS Armoury) declaring CompactModePriorityPlacement'
Write-Host '  or IsDeviceWidget. Copy its exact value form into src/Package/Package.appxmanifest.'
Write-Host '  If NOTHING declares CompactModePriorityPlacement, the far-left slot is won some other'
Write-Host '  way and the manifest is the wrong place to keep looking.'
Write-Host ''
