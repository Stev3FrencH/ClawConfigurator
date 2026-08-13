# Building the widget and the MSIX package

A step-by-step guide for the one part of this project that cannot be built on the authoring
machine.

Everything under `src/Shared`, `src/Hardware`, `src/Helper`, `src/Probe` and `tests/` builds
anywhere with `dotnet build McenterLite.sln`, including macOS. **`src/Widget` and the packaging
project do not.** They need MSBuild from Visual Studio, and they are deliberately **not** in
`McenterLite.sln` — adding them would break the cross-platform build the whole authoring workflow
depends on.

> **Read this first.** The widget has been **authored but never compiled**, and roughly 2,100
> lines have accumulated that way. Everything checkable without a compiler has been checked —
> the XAML parses, every `StaticResource` key resolves against `App.xaml`, and every `x:Name` and
> event handler is wired on both sides — but none of that catches a wrong API shape. **Expect
> compile errors on the first build.** That is the expected outcome of this session, not a sign
> something is broken.

---

## 0. What you are building

| Piece | Produces | Needs |
|---|---|---|
| `src/Widget` | `McenterLite.Widget` — the UWP AppContainer UI | VS 2022 + UWP workload |
| `src/Package` | the signed `.msix` that actually installs | Windows Application Packaging Project |
| `src/Helper` | `McenterLite.Helper.exe`, bundled into the package | plain .NET SDK |

The helper is already proven on the device. This session is about the UI and the packaging
around it.

---

## 1. Machine setup

A Windows 11 VM (or any Windows 11 box that is not the Claw — you want to fix compile errors
somewhere comfortable). Build 22000 or later.

Install Visual Studio 2022 with these components. From the installer UI pick the workloads, or
run this from an elevated prompt if VS is already present:

```powershell
# Adjust the path for Community/Professional/Enterprise as needed.
$vs = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vs_installer.exe"

& $vs modify `
  --installPath "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community" `
  --add Microsoft.VisualStudio.Workload.Universal `
  --add Microsoft.VisualStudio.Workload.ManagedDesktop `
  --add Microsoft.VisualStudio.Component.Windows11SDK.26100 `
  --passive --norestart
```

- **Universal Windows Platform development** — the UWP workload. Without it the `.csproj` will
  not even load, and VS reports it as an unsupported project type rather than a missing workload.
- **.NET desktop development** — for the helper and the packaging project.
- **Windows 11 SDK 10.0.26100** — the exact version `McenterLite.Widget.csproj` targets. A
  different SDK is the single most common reason a UAP project refuses to load.

Also enable **Developer Mode** (Settings → System → For developers). Sideloading a self-signed
package needs it.

Verify before going further:

```powershell
Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Platforms\UAP\10.0.26100.0"   # must be True
```

---

## 2. Get the code and prove the baseline

```powershell
git clone https://github.com/Stev3FrencH/msi-mcenter-lite.git
cd msi-mcenter-lite
dotnet build McenterLite.sln
dotnet test McenterLite.sln
```

**Both must pass before you touch the widget.** They exclude the widget entirely, so a failure
here is an environment problem, and diagnosing it alongside UWP errors is much harder than
diagnosing it alone. Expect 140 passing tests.

---

## 3. Generate the assets

Both manifests reference five PNGs that do not exist. `MakeAppx` fails the build naming the
missing file, which reads like a broken repository rather than absent artwork.

```powershell
powershell -ExecutionPolicy Bypass -File .\src\Widget\New-PlaceholderAssets.ps1
```

That writes `src/Widget/Assets/`. Copy the same folder into the packaging project once it exists
(step 5) — the shipping manifest resolves asset paths relative to itself.

---

## 4. First compile of the widget, alone

Open `src/Widget/McenterLite.Widget.csproj` in Visual Studio **on its own**, not through a
solution. Set the configuration to **Debug | x64** — the project is x64-only by design, and
`Any CPU` will fail with an unhelpful platform error.

Build it. **This is the step that will produce errors**, and fixing them is the point of the
session. Work through the list in [What is most likely to break](#what-is-most-likely-to-break)
below, which is ordered by risk.

Do not move on until this project builds clean. Everything after depends on it.

---

## 5. Create the packaging project

This does not exist in the repo, because a `.wapproj` cannot be authored blind with any
confidence — it embeds absolute-ish tooling paths and a project GUID.

1. Create a solution containing `McenterLite.Widget.csproj` and `McenterLite.Helper.csproj`.
2. **Add → New Project → Windows Application Packaging Project**, name it `McenterLite.Package`,
   and put it at `src/Package` so it lands beside the existing manifest and `Install.ps1`.
3. Target version 10.0.26100.0, minimum 10.0.22000.0 — matching the widget.
4. Under the new project's **Dependencies → Applications**, add a reference to
   **McenterLite.Widget**. Set it as the **entry point**.
5. **Replace the generated `Package.appxmanifest` with the one already in `src/Package`.** That
   is the file that declares the Game Bar extension and the full-trust helper; the generated one
   declares neither, and a package built from it installs and then does nothing.
6. Copy `src/Widget/Assets` into `src/Package/Assets`.

### Getting the helper into the package

The manifest declares `Executable="Helper\McenterLite.Helper.exe"`, so the helper must land in a
`Helper` folder inside the package, published **self-contained** — the package cannot rely on a
.NET runtime being present on the Claw.

**This is automatic.** The `PublishHelper` target in `McenterLite.Package.wapproj` runs
`dotnet publish` into `src/Package/Helper/` before every package build, and errors out if the
executable is not there afterwards. Nothing manual is required.

> It used to be a manual `dotnet publish` documented here and enforced nowhere, and it failed the
> way unenforced manual steps do: the packaged helper went eight hours and five features stale
> while every build succeeded and every package looked correct. A stale binary is still a valid
> binary, so nothing warns you — the symptom surfaces only on device, as a card that never
> populates or an IPC `Function` ordinal the widget and helper disagree about. If you ever suspect
> it again, check the timestamp of `src/Package/Helper/McenterLite.Helper.exe` against your last
> helper change.

A ProjectReference cannot replace that target: it would bring in a framework-dependent build, and
the target device has no runtime.

Verify the path inside the built package matches the manifest exactly. A mismatch here produces
a package that installs cleanly and whose widget then reports "could not reach the helper"
forever, with nothing in any log explaining why.

---

## 6. Create the signing certificate

Sideloading needs a signed package and a trusted certificate. The certificate subject must match
the manifest `Publisher` **exactly** — a mismatch is the most common cause of a package that
builds but refuses to install.

```powershell
New-SelfSignedCertificate -Type Custom -Subject "CN=msi-mcenter-lite" `
  -KeyUsage DigitalSignature -FriendlyName "msi-mcenter-lite" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

Note the thumbprint it prints, then point the packaging project at it: project properties →
**Packaging** → **Choose Certificate** → **Select from store**.

Export the public half for the install step:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=msi-mcenter-lite' }
Export-Certificate -Cert $cert -FilePath .\src\Package\msi-mcenter-lite.cer
```

---

## 7. Build and install the package

Right-click the packaging project → **Publish → Create App Packages** → **Sideloading**, x64 only.

Then, from an elevated PowerShell:

```powershell
.\src\Package\Install.ps1
```

It finds the newest `.msixbundle`/`.msix` and `.cer` beside itself, imports the certificate to
`LocalMachine\TrustedPeople` (never `Root`), stops any running helper, and installs.

**It deliberately does not copy the helper anywhere or create a scheduled task.** The signed
helper does both itself on first run. A PowerShell script that copies an executable into
LocalAppData and registers a HIGHEST-privilege ONLOGON task is behaviourally indistinguishable
from persistence malware — the reference project documents exactly that being quarantined as
`Behavior:Win32/Persistence.A!ml`.

---

## 8. First run

1. Open the Game Bar with **Win+G** and pin **M Center Lite**.
2. The widget calls `FullTrustProcessLauncher`, which starts the helper from inside the package.
3. That instance sees nothing deployed, relaunches itself elevated with `--setup` — **one UAC
   prompt** — copies itself to `LocalCache\McenterLite\Helper\`, registers the scheduled task,
   and exits.
4. The task starts the deployed helper, which opens the pipe.
5. The widget reconnects on its own. This can take a few seconds after the prompt is accepted.

If the prompt is declined the widget says so and offers a retry; it does not re-prompt in a loop.

Logs: `%LOCALAPPDATA%\Packages\<package family>\LocalCache\McenterLite\helper.log`

---

## What is most likely to break

Ordered by risk. The first two are the ones that stop the build; the rest mostly misrender.

### Game Bar API surface

None of this could be checked against the real reference assemblies.

1. **`XboxGameBarWidget` construction** in `App.xaml.cs`. The activation-args cast and constructor
   signature vary across `Microsoft.Gaming.XboxGameBar` versions. This is the single most likely
   compile error.
2. **The properties set in `ConfigureWidget`** — `MinWindowSize`, `MaxWindowSize`,
   `HorizontalResizeSupported`, `VerticalResizeSupported`, `PinningSupported`,
   `SettingsSupported`. Some are settable properties in some SDK versions and methods in others.
3. **`VisibleChanged` and `RequestedOpacityChanged`** handler signatures.
   `TypedEventHandler<XboxGameBarWidget, object>` is what the docs say; confirm against IntelliSense.
4. **`RequestedOpacity`** arrived in SDK 5.3. The package reference is 7.3.2506120, so it should be
   present — if it is not, that call is the first thing to drop.

### Manifest and project shape

- **The two manifests.** `src/Widget/Package.appxmanifest` is minimal and exists only so the widget
  project builds standalone. `src/Package/Package.appxmanifest` is the one that ships. Keep
  `Identity` and the `Application Id` in sync between them.
- **Window size is declared twice and they disagree.** The shipping manifest says min 380x420,
  max 640x1080; `ConfigureWidget` sets min 320x320, max 560x1000. The manifest is the initial
  declaration and the API overrides at runtime, so this is not fatal — but pick one and make them
  agree once you have seen the widget at its minimum size.

### Styling

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
- **Selector option lists live in `MainWidget.xaml.cs`, not in XAML.** `SegmentedControl` is
  constructed with them, and **the index is the wire value** — the lighting slot, the fan
  selection, and so on. Reordering a list silently changes what the helper is told.

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

### The proxyStub manifest entry is REQUIRED — never remove it

`src/Package/Package.appxmanifest` carries a **package-level** `windows.activatableClass.proxyStub`
extension, copied verbatim from the SDK's `readme.txt`. It registers Metadata Based Marshaling for
Game Bar's private COM interfaces.

**Without it the widget is completely inert to the controller and the keyboard**, while rendering,
connecting, resizing and reporting `VisibleChanged` perfectly — so it looks like a focus bug and is
not one. This cost most of a day: the entry had been missing since the package was first authored,
so no amount of reverting to "known-good" builds could restore it, and the symptom pointed
squarely at the wrong layer.

It was originally left out with a comment saying it was "needed only for programmatic widget-bar
navigation". That was an inference and it was wrong; the SDK documents it as a required step for
every widget. Re-copy the block from `readme.txt` when the NuGet package is updated — it says it
can change between versions.

### Gamepad navigation only breaks in ways a mouse cannot show you

Four separate defects shipped here before anyone drove the widget with a controller, because **a
mouse click sets focus** and so hides every one of them. If you change anything about focus, the
segments, or when cards are shown, retest with a controller and **do not touch the mouse first**.
See `docs/status.md` for the four and what each looked like. The recurring shapes:

- **Reading `ActualHeight` in the same callback that changed `Visibility`.** Layout is deferred to
  the next frame, so a just-unhidden control still measures zero. `LayoutUpdated` is the signal.
- **Assigning `Style` to a focused control.** It re-applies the control template and focus is lost.
  Set the individual properties instead.
- **Latching "focus has been set" for the session.** Game Bar hides and re-shows widgets, and a
  hidden widget's focused element stops being focused. Re-arm on `VisibleChanged`.
- **`FocusState.Programmatic` does not reveal the focus rectangle.** On a device with no cursor,
  focus you cannot see reads as broken navigation. Use `FocusState.Keyboard`.

### Why buttons instead of dropdowns

The Claw is driven with a game controller. A `ComboBox` costs a press to open, D-pad travel
through a popup that pulls focus out of the card, and a second press to commit — and the popup can
render outside the widget's bounds inside the Game Bar. A cycle button is one A press per step
with focus never leaving the card.

It also removes a bug class: a `ComboBox` raises `SelectionChanged` while XAML applies its markup
defaults during construction, which is exactly what `_applyingFromHelper` exists to suppress. A
`Button` raises `Click` only when something clicks it.

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

---

## Troubleshooting

Symptoms whose cause is not obvious from the message.

| Symptom | Cause |
|---|---|
| VS reports the widget project as an **unsupported project type** | UWP workload not installed. It is not a missing-SDK error even though it reads like one. |
| **"The project needs Windows SDK 10.0.26100"** | That exact SDK is missing. A newer one does not substitute — the version is pinned in the `.csproj`. |
| Build fails on **`Any CPU`** | The project is x64-only by design. Switch the configuration. |
| **`MakeAppx` fails naming a PNG** | Assets not generated. Run `New-PlaceholderAssets.ps1`, and copy the folder into the packaging project too. |
| Package builds but **will not install** | Certificate subject does not match the manifest `Publisher` exactly. Both must be `CN=msi-mcenter-lite`. |
| Package installs but **the widget never appears in the Game Bar** | The generated manifest was used instead of `src/Package/Package.appxmanifest`, so the `microsoft.gameBarUIExtension` registration is missing. |
| Widget appears but says **"could not reach the helper"** forever | Either the helper is not at `Helper\McenterLite.Helper.exe` inside the package, or the elevation prompt was declined. Check `helper.log`. |
| Widget renders but **every card is hidden** | Expected on a machine that is not a Claw 8 EX — `DeviceCaps.Supported` is false and hardware cards hide themselves. CPU boost and power mode should still show. |
| **Cards appear, controls do nothing** | The pipe connected but the AppContainer cannot write. Check the `S-1-15-2-1` ACE in `PipeServer.BuildSecurity`. |
| Widget crashes **immediately on open** | Almost certainly an unresolved `StaticResource`. Those are runtime failures, not build failures. |

### Testing without a Claw

Run the helper with `--fake-hardware` and every non-hardware layer becomes exercisable: IPC,
clamping, the deployment flow, settings, uninstall restore. Simulated hardware reports
`Supported=false`, so hardware cards stay hidden and nothing pretends to work.

`Diagnostics/Test-Helper.ps1` drives the helper over the same pipe the widget uses, which is worth
running **before** the widget works — if the script can talk to the helper and the widget cannot,
the problem is in the widget or the AppContainer boundary, not in anything below it.
