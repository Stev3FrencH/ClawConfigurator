# Building the widget and package

Everything under `src/Shared`, `src/Hardware`, `src/Helper`, `src/Probe` and `tests/` builds
anywhere with `dotnet build McenterLite.sln`, including macOS.

**`src/Widget` and the packaging project do not.** They need MSBuild from Visual Studio, and they
are deliberately **not** in `McenterLite.sln` — adding them would break the cross-platform build
that the whole authoring workflow depends on.

## Prerequisites (Windows only)

- Visual Studio 2022 with the **Universal Windows Platform development** workload
- **Windows 11 SDK 10.0.26100**
- Windows 10 version 22000 or later

## Status of this code

> The widget has been **authored but never compiled**. It was written on macOS, where no UWP
> toolchain exists. Expect to fix compile errors on the first VM build — particularly around
> Game Bar API surface, which could not be checked against the real reference assemblies.

Known places to verify first:

1. **`XboxGameBarWidget` construction** in `App.xaml.cs`. The activation-args cast and constructor
   signature vary across `Microsoft.Gaming.XboxGameBar` versions.
2. **Widget visibility.** `MainWidget` uses `Window.Current.VisibilityChanged`, which is plain UWP
   and certain to exist. `XboxGameBarWidget` also exposes its own visibility events; if the plain
   one proves unreliable inside the Game Bar host, switch to those. This drives whether the helper
   pushes fan telemetry, so getting it wrong costs battery, not correctness.
3. **The two manifests.** `src/Widget/Package.appxmanifest` is minimal and exists only so the
   widget project builds standalone. `src/Package/Package.appxmanifest` is the one that ships and
   declares the Game Bar extension plus the full-trust helper. Keep `Identity` and the
   `Application Id` in sync.
4. **Assets.** Neither manifest's referenced PNGs exist yet. Generate placeholders or the package
   will not build:
   `Assets\StoreLogo.png`, `Square150x150Logo.png`, `Square44x44Logo.png`,
   `Wide310x150Logo.png`, `SplashScreen.png`.
5. **The visual styling**, which is also unverified. Specifics below.

### Styling — what to check first

The palette and card styles in `App.xaml` are **deliberately self-contained** rather than built on
WinUI's theme-resource keys (`CardBackgroundFillColorDefaultBrush` and friends). Those would be
idiomatic, but a `StaticResource` that does not resolve is a **runtime crash**, and none of this
can be compiled here to find out. Every key `MainWidget.xaml` references is defined in `App.xaml`
— that much has been cross-checked mechanically. Watch for:

- **`ToggleSwitch` with blank `OnContent`/`OffContent` and `MinWidth="0"`.** This is what makes the
  label-left / switch-right rows work instead of the stock stacked header. If the switches come out
  mis-sized or the captions reappear, that style is why.
- **Icon glyphs.** Every glyph used here (`E945` lightning, `E72C` refresh, `E83F` battery, `E790`
  colour, `E7FC` game, `E713` settings) is one the reference widget already ships on this device,
  except **`E7F4`** on the Graphics card, which is a guess. A wrong glyph renders as a box — check
  it, but it cannot crash.
- **`FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`.** The comma fallback is used by the
  reference widget on this device, so it is known to work in this host.
- **`ConnectionDot`.** Its brush is looked up **by name** in `SetConnectionDot`
  (`SuccessBrush` / `TextSecondaryBrush`). Renaming either key in `App.xaml` breaks it at runtime
  with no compile error.
- **`TextBlock.FontFeatures` does not exist in UWP.** Value alignment uses `MinWidth` +
  `TextAlignment` instead of tabular figures. If you reach for font features later, it is not there.
- **`x:Double` and `Thickness` resources.** The size tokens are declared as
  `<x:Double x:Key="BodySize">14</x:Double>` and `<Thickness x:Key="CardPadding">16</Thickness>`.
  Both are standard UWP, but they are the newest thing in the file — if `App.xaml` fails to parse,
  start here.
- **`RootContent`.** The root `Grid` is named because Game Bar's transparency setting is applied to
  its `Opacity`. Renaming it breaks `ApplyRequestedOpacity` at runtime, not at build.
- **`CycleButtonStyle`'s `ContentTemplate`.** Every selector is a `Button` whose `Content` is a
  plain string, rendered by a `DataTemplate` that binds `{Binding}` and appends a chevron. If the
  buttons come out blank, that binding is the first thing to check — `{Binding}` against a
  `ContentPresenter` resolves to the content itself, which is correct but easy to break.
- **Selector option lists live in `MainWidget.xaml.cs`, not in XAML.** `OptionCycler` is
  constructed with them, and **the index is the wire value** — cast straight to `PerfMode`,
  `FanPreset`, `LedMode` and so on. Reordering a list silently changes what the helper is told.

### Sizing: the numbers and where they come from

Every size is an **effective pixel**, so it stays physically consistent whatever display scaling
the device is set to. The target panel is 8-inch, 1920x1200, **283 PPI**. Microsoft's touch
minimum is **40 epx**, recommended 7-9 mm:

| | 200% scaling | 250% | 300% |
|---|---|---|---|
| logical screen | 960x600 | 768x480 | 640x400 |
| 32 epx (old slider) | 5.7 mm | 7.2 mm | 8.6 mm |
| 38 epx (old button) | 6.8 mm | 8.5 mm | 10.2 mm |
| **44 epx (current)** | **7.9 mm** | **9.9 mm** | 11.8 mm |

The old 32 epx sliders were under the minimum at the likely 200% scaling, and the slider thumb is
the hardest thing on the page to hit. Sizes now come from tokens at the top of `App.xaml`
(`ControlHeight`, `CardTitleSize`, `BodySize`, `HintSize`, `CardPadding`, `CardGap`) — change the
scale there, not in the individual styles.

Note the logical screen height: at 250% the whole screen is 480 epx tall, so a widget cannot show
many cards at once. That is why `MaxWindowSize` is generous but the layout scrolls.

### Deliberate deviations from the Game Bar design guide

- **Dark only.** The guide says honour `RequestedTheme` *"if your widget is able"*. A light palette
  means a second set of brushes and a second rendering path that cannot be tested here. The Game
  Bar overlays gameplay and is dark in practice. Revisit if this ever ships beyond one device.
- **One size, not two.** The guide asks for larger content in Compact mode than Desktop. There is
  one scale here, permanently at the larger end, because the target is a handheld in both modes.
  `CompactModeEnabled` is deliberately not subscribed to. If the widget is ever shown on a desktop
  monitor it will look oversized.

### Gamepad buttons we must not take

From the guide's reserved list. Worth writing down because an earlier note in this project
suggested the opposite:

- **Left/right bumpers are reserved by Game Bar** for moving between widgets. Do not bind them —
  an earlier suggestion to use them for paging option lists was wrong.
- **B is Back/Close.** Do not handle it, and do not add a back button.
- Left/right **triggers** already page the `ScrollViewer`; that is built in.

Sliders deliberately do **not** set `IsFocusEngagementEnabled`. The documented fix for slider
focus-trapping is a vertical layout, which this already is: D-pad up/down moves between controls
while left/right adjusts the focused slider. Engagement would add an A press per slider for
nothing.

### Why buttons instead of dropdowns

The Claw is driven with a game controller. A `ComboBox` costs a press to open, D-pad travel
through a popup that pulls focus out of the card, and a second press to commit — and the popup can
render outside the widget's bounds inside the Game Bar. A cycle button is one A press per step
with focus never leaving the card.

It also removes a bug class: a `ComboBox` raises `SelectionChanged` while XAML applies its markup
defaults during construction, which is exactly what `_applyingFromHelper` exists to suppress. A
`Button` raises `Click` only when something clicks it.

## Creating the packaging project

Not created here, because a `.wapproj` cannot be authored blind with any confidence. On the VM:

1. Add a **Windows Application Packaging Project** to the solution as `src/Package`.
2. Add a reference from it to `McenterLite.Widget`.
3. Replace its generated manifest with `src/Package/Package.appxmanifest`.
4. Add the published helper output so it lands at `Helper\McenterLite.Helper.exe` inside the
   package — that path is what the manifest's `windows.fullTrustProcess` extension names.

Publish the helper self-contained first:

```powershell
dotnet publish src\Helper\McenterLite.Helper.csproj -c Release -r win-x64 --self-contained
```

## Signing and installing

Sideloading needs a signed package and a trusted certificate.

```powershell
# One-off: create a self-signed certificate whose subject MATCHES the manifest Publisher exactly.
New-SelfSignedCertificate -Type Custom -Subject "CN=msi-mcenter-lite" `
  -KeyUsage DigitalSignature -FriendlyName "msi-mcenter-lite" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

A mismatch between the certificate subject and the manifest `Publisher` is the most common cause
of a package that builds but refuses to install.

Then build the package in Visual Studio, and install with `src/Package/Install.ps1`.

## What the installer does and does not do

`Install.ps1` installs the package and nothing else. It does **not** copy the helper anywhere and
does **not** create a scheduled task — the signed helper does both itself on first run, behind a
single elevation prompt.

That split is not stylistic. A PowerShell script that copies an executable into LocalAppData and
then registers a HIGHEST-privilege ONLOGON task is behaviourally indistinguishable from
persistence malware; the reference project documents that exact approach being detected as
`Behavior:Win32/Persistence.A!ml` and having its helper quarantined. The same work done in-process
by a signed binary is not that pattern.

## First-run sequence to expect

1. Open the Game Bar (`Win+G`), pin **M Center Lite**.
2. The widget calls `FullTrustProcessLauncher`, which starts the helper from inside the package.
3. That instance sees nothing deployed, relaunches itself elevated with `--setup` — **one UAC
   prompt** — copies itself to `LocalCache\McenterLite\Helper\`, registers the scheduled task, and
   exits.
4. The task starts the deployed helper, which opens the pipe.
5. The widget reconnects on its own. This can take a few seconds after the prompt is accepted.

If the prompt is declined the widget says so and offers a retry; it does not re-prompt in a loop.

## Verifying on the VM before touching the Claw

These are real tests even with no MSI hardware present:

- The widget renders in the Game Bar and its cards appear.
- **The AppContainer can open the helper's pipe.** This is the single most likely thing to fail
  silently. If it does, check the `S-1-15-2-1` ACE in `PipeServer.BuildSecurity`.
- One UAC prompt, not several.
- The scheduled task exists at `\McenterLite\McenterLiteHelper` and survives a reboot.
- **CPU boost and power mode work for real** — they are plain Windows APIs.
  Cross-check with `powercfg /q SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE`.
- `--uninstall` removes the task and the deployed folder.

Design-guide conformance, all observable without MSI hardware:

- **No horizontal scrollbar at the minimum window size** (320x320). The value columns are the
  first thing to collide with their labels if a font size grows.
- **Pin the widget, then dismiss the Game Bar.** `Visible` must stay true and the fan telemetry
  must keep flowing. This is the one bug the `VisibleChanged` change fixes and the only way to see
  it — the old `Window.Current.VisibilityChanged` reported a pinned widget as hidden.
- **Drag Game Bar's transparency slider** — the whole widget should fade, text included.
- **Gamepad:** D-pad reaches every control, the focus rectangle is always visible against the dark
  cards, sliders adjust with left/right without trapping focus, B closes the widget.
- **Initial focus** lands on the first available control rather than nowhere.
- **Touch:** every control is comfortably hittable with a thumb.

Run the helper with `--fake-hardware` for everything above; simulated hardware reports
`Supported=false`, so the hardware cards stay hidden and nothing pretends to work. That also
exercises the initial-focus fallback, since it has to skip the hidden cards.

Logs: `%LOCALAPPDATA%\Packages\<package family>\LocalCache\McenterLite\helper.log`
