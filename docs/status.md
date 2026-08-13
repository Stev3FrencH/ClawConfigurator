# Status — 2026-08-12

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, and exactly
what to do next. See [`hardware-notes.md`](hardware-notes.md) for gate-by-gate detail, and its
[What's next](hardware-notes.md#whats-next--fan-charge-limit-and-rgb) section for the technical
notes behind the roadmap below.

## The headline

**The MSI Center M dependency is gone.** All five hardware features talk to the firmware directly
and need MSI Center M neither running nor installed:

- **Power limits** via `MSI_ACPI.Set_SlaveBattery` (ACPI-WMI), merged 2026-08-11.
- **Controller mode** via the controller's vendor HID channel, merged 2026-08-12.
- **Battery charge limit** via `MSI_ACPI.Set_AP` (ACPI-WMI), merged 2026-08-12.
- **RGB lighting** via the vendor HID profile block, merged 2026-08-12.
- **Fan control** via `MSI_ACPI.Set_Fan` (ACPI-WMI), 2026-08-12.

**Every feature on the roadmap is now built.** What remains is not new features but removal: the
cleanup that waits on MSI Center M actually being uninstalled — deleting `RegistryTdpProvider` and
`RegistryHwMouseProvider`, which are mirrors rather than real paths.

## Current build

**0.2.0.24, Debug.** The fan-control flag on the telemetry tick. 0.2.0.23 read the flag from the
firmware — the right source — but only once, in the connect-time snapshot, so an open widget could
not notice anything else moving the fans. The helper now re-reads it while the widget is visible and
pushes `FanProfile` as an event, like the power and controller modes.

**Every fifth tick, not every tick.** The other two are cheap OS reads; this is an ACPI-WMI round
trip to the EC on a battery-powered handheld, and what it watches for is a person pressing a button
in another app. ~5 s is well inside the time it takes to hear a fan change. The one worry on record
against this — that the loop "carried fan telemetry once and lost it" — turned out not to apply:
`295f68b` removed live RPM and temperatures at 1 Hz, and it went because the whole feature went, not
because the polling was ever a problem.

**That also makes the open MSI Center M question testable from the card.** Hold Custom, set MSI
Center M to Auto, wait ~5 s: if the card holds, MSI Center M leaves the flag alone and we own the
fans; if it flips to Auto, MSI Center M takes them back and startup re-apply is not enough. Until
0.2.0.24 the card could not answer this, so the observation below was never evidence either way.

**0.2.0.23, verified on the Claw, 2026-08-12**, through the probe and then through the widget, by
ear both times. 0.2.0.22 had written the duty tables but never set the flag that tells the EC to
read them, so every write stored, read back and logged as a success while the firmware went on
running its own curve. A full-duty table written with the flag clear is silent; setting the flag
alone — tables untouched — makes both fans go loud. Auto and Custom are both audible from the card.
0.2.0.21 was verified the same day for lighting, gamepad and keyboard navigation, and the
charge-limit slider.

**MSI Center M did not follow along**, which is the fourth time its UI has been shown to be a cache
rather than a view of the device.

> **This device only ever runs the widget in compact mode.** Pinning is not a case to design for —
> see the focus notes below, where that fact is what makes the re-arm guard safe.

```powershell
powershell -ExecutionPolicy Bypass -File .\src\Package\Install.ps1
```

It picks the newest package under `src/Package/AppPackages/` on its own and elevates itself.

To see the whole UI on a machine that is not a Claw:

```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnostics\Start-FakeHelper.ps1
```

> **The manifest version must be bumped BY HAND** in `src/Package/Package.appxmanifest` before every
> rebuild. Nothing auto-increments it, and installing two different builds under the same version
> fails with `0x80073CFB`. Both documented MSBuild overrides
> (`AppxManifestPackageVersion`, `AppxManifestPackageVersionRevision`) were tried and **neither is
> honoured by this project template** — do not retry them without new evidence.

> **Install through `Install.ps1`, elevated. Do not call `Add-AppxPackage` directly.**
> The running helper holds `helper.log` open inside the package's LocalCache, and Windows must
> delete that app-data store to re-register the package. A live helper therefore fails the install
> with `0x80073CF3` (updating) or `0x80073D05` (after the old package is already gone), and
> **neither message mentions the helper**. Learned the slow way on 2026-08-12, after `0x80073CF3`
> was first misread as a missing framework dependency.
>
> Killing the process is not enough either: the `McenterLiteHelper` scheduled task owns its
> lifetime and can restart it mid-install. `Install.ps1` now stops *and disables* the task, then
> re-enables it in a `finally`. Same supervisor trap as `MSI_Center_M_Server`, on our own code.

> **Build the package as a `.msixbundle`, the way step 7 of `building-the-widget.md` says.**
> Driving MSBuild directly with `/p:Platform=x64` and no bundle properties produces a bare `.msix`
> instead, and that cost a whole install cycle twice over on 2026-08-12. Once because `Install.ps1`
> ranked *any* bundle above *any* `.msix` and so reinstalled a version-older bundle while printing
> the file it chose (fixed — one ranking across both extensions now); and again because replacing an
> installed bundle with a bare `.msix` of the same identity fails `0x80073CF3`. The command that
> reproduces the documented shape:
>
> ```powershell
> & $msbuild .\src\Package\McenterLite.Package.wapproj /p:Configuration=Debug /p:Platform=x64 `
>   /p:AppxBundlePlatforms=x64 /p:AppxBundle=Always /p:UapAppxPackageBuildMode=SideloadOnly `
>   /p:AppxPackageSigningEnabled=true
> ```
>
> **Read the `Found package:` line `Install.ps1` prints.** It names the file and its timestamp, and
> it is the only place a wrong-package install announces itself.

## A slider needs BOTH StepFrequency and SmallChange

Fixed 2026-08-12, verified on device. The Battery card's slider declared `StepFrequency="10"` and
still moved in ones.

**They govern different inputs, and only one of them is stepping.** `StepFrequency` controls tick
snapping and pointer dragging; **`SmallChange`, inherited from `RangeBase` and defaulting to `1`, is
what a single arrow-key or D-pad press uses.** Declaring the step alone does nothing for the input
this device is actually driven with. `ChargeLimitSlider` now sets both; PL1/PL2 deliberately set
neither, because they want steps of one.

Worth remembering when adding any slider: the step you declare is not necessarily the step the user
gets.

Not a bug, and deliberate: the percentage **text** can show a value that is not a multiple of ten
while the thumb sits on the nearest step. The text reports what the hardware said, so the widget
never rounds and misreports the device.

## Confirmed working on the Claw

| Feature | Path | Verified |
|---|---|---|
| Power limits (PL1/PL2) | `MSI_ACPI.Set_SlaveBattery` | 2026-08-11, against sustained-clock oracle |
| Controller mode | vendor HID `0x24`/`0x26`/`0x27` | 2026-08-12, both directions + physical button |
| Battery charge limit | `MSI_ACPI.Set_AP` | 2026-08-12, charging resumed past the old limit |
| RGB lighting | vendor HID `0x04`/`0x05`/`0x21` | 2026-08-12, wave visible on the device |
| CPU Boost | documented Win32 | 2026-08-10 |
| OS Power Mode | documented Win32 | 2026-08-10 |
| Gamepad navigation | — | 2026-08-10, compact mode |

The Graphics card does not appear on the Claw and is not expected to: `HasIgcl` is hard-coded false
until G6 is implemented. It is only visible under `--fake-hardware`.

### The physical MSI button now syncs — resolved

Earlier versions of this file described this as an accepted limitation. **It is fixed.** The
firmware owns the button and announces every change on HID input report `0x27`; the helper reads
the live device rather than MSI Center M's registry mirror, so the widget follows the button.

The old diagnosis was close but aimed at the wrong layer: the registry value *does* track the
button, but only because MSI Center M watches the hardware and writes it — and it lags about a
second doing so. `Diagnostics/Watch-ControlMode.ps1` is kept as a record of how that was chased;
`Diagnostics/Test-ControllerModeStandalone.ps1` is the one to use now.

## Next up

Three features return, in this order. **The gating question for all three is the same: can it be
driven with MSI Center M absent?** Settle that first in each case — it changed the design of both
features shipped so far.

### ~~1. Battery charge limit (G3)~~ — DONE 2026-08-12

Shipped in 0.2.0.8 as the widget's **Battery** card, on `MSI_ACPI.Set_AP`, standalone of MSI
Center M. Wire ordinal is `Function.ChargeLimitPercent = 32` — a **new** number, because the
retired 30/31 must never be reused. **Offers 50–100 in steps of 10**, with a 1 s write debounce
like the TDP sliders. The firmware accepts **20**–100; the 50 floor is a product choice, because
nothing below half charge is useful for longevity. Do not let that floor turn into folklore about
the hardware — that is exactly what happened to the retired version's 60.

Verified on device: the write applied, the battery resumed charging past its old limit, the value
persisted across a helper restart, and `Get_AP` confirmed every write.

**MSI Center M does not notice** and goes on showing its own cached value. Untested: whether it
re-asserts that value later, on its own tick or on resume. It has no bearing on the standalone
case, but it decides whether the two can coexist until the uninstall.

### ~~2. RGB LED (G4)~~ — DONE 2026-08-12

Shipped in 0.2.0.10 as the widget's **Lighting** card, **last** in the card order. Four segments:
off plus three profiles. `Function.LightingProfile = 41` — a **new** ordinal, because the retired
40 must never be reused — with `LightingProfileNames = 42` carrying the button labels.

**No colour picker.** The three profiles are text files the user edits, seeded to reproduce what
MSI Center M had configured; see [`lighting-profiles.md`](lighting-profiles.md). The widget only
chooses between them, and the card carries **no explanatory text** — the folder has its own
`README.txt` and the repo README has the path.

**A bad edit cannot produce a broken profile.** Every setting falls back field by field and names
what it skipped in `helper.log`. The one asymmetry worth knowing about: an empty `Colors` *means*
"built-in palette", so a colour list where nothing parses keeps the previous colours rather than
committing the empty list — otherwise one typo would silently swap the profile for a different,
valid-looking one. Recovery is to empty or delete the file and tap the profile; `Load` restores the
default **and rewrites the file**, so nothing needs restarting.

**Decoded from MSI's own binary, not by observation.** `API_ControlMode.dll` is unobfuscated .NET
and carries the whole protocol. That also independently confirmed G5's opcodes. Full detail in
[`hardware-notes.md`](hardware-notes.md#the-lighting-protocol--decoded-2026-08-12).

Two facts worth carrying forward:

- **The profiles were MSI Center M's, not the controller's.** They are archived at
  `Diagnostics/mystic-light-profiles/`. The controller stores only flattened keyframes, which is
  also why the selected profile is helper state rather than something readable back.
- **Lighting is written to RAM, never flash.** `SyncToROM` (`0x22`) exists and is deliberately not
  used. The helper re-applies at startup instead — including "off", so a power cycle cannot quietly
  turn the lights back on.

Untested: whether MSI Center M re-asserts its own lighting later while both are installed. Same
open question as the charge limit, and with the same non-bearing on the standalone case.

### 2. Fan control (G2) — built 2026-08-12

The contradiction that blocked this since August dissolved the moment the firmware was read
directly instead of through the registry. MSI's six registry points are bytes 2–7 of an eight-byte
table; the "five-point model from a different machine" was right about the *structure*, and its
`58` floor and `94` ceiling are the bytes on either side. Duty is a plain 0–100 percentage, which
also retired the never-measured "MSI caps at 75" assumption. Full detail in
[hardware-notes.md](hardware-notes.md#gate-g2--fan-control).

**Two fans, addressed separately.** `Set_Fan` proven on device with the second fan held as a
control — it did not move when only the first was written, which is what confirmed the sub-function
is a fan selector rather than something we had misread.

**There IS an auto mode, and missing it cost a build.** `Get_AP` sub-function 1, byte 1, bit `0x80`:
clear, the firmware runs its own curve and the duty tables are stored and ignored; set, it follows
them. 0.2.0.22 shipped without it and did nothing audible while reporting success at every layer —
the provider read back and compared, the helper logged the duties, the probe confirmed with a second
read, and all three were measuring whether the write was *stored* rather than whether it was
*obeyed*. The flag had actually been spotted during G2 and dismissed in the same paragraph that
named it, on an assumption the write test could not have tested. See
[hardware-notes.md](hardware-notes.md#the-fan-control-flag--get_ap--set_ap-sub-function-1).

Applying is therefore two writes — the tables, then the flag — and Auto writes the factory table
*and* clears the flag rather than merely leaving ours behind.

**No Apply button.** The card originally required a second press so a curve would not follow a
control as it was being pressed; it was removed on request, and the card now behaves like every
other selector. The stopped-fan warning moved with it: it used to appear once Custom was selected,
which worked while there was still a press left in which to read it, and now shows whenever the
profile on disk contains a zero.

**The card is a live readout, from 0.2.0.24.** The helper pushes `FanProfile` every fifth telemetry
tick while the widget is visible, read from the flag rather than echoed from what we last wrote — so
a curve something else took back shows on the card instead of only in the noise.

Still open, and both about *persistence* rather than mechanism:

- Whether MSI Center M overwrites a curve we wrote, and on what trigger. Startup re-applies for
  this reason, and re-selecting the profile already running is deliberately allowed. The tick above
  is how this now gets observed rather than guessed at.
- Intel's thermal stack (`ipfsvc`) is an independent fan actor here, so "our table is correct" and
  "the fan behaves" remain separate claims — which is precisely the gap the flag fell into.

> **The firmware enforces no duty floor.** An all-zero table was accepted with both tachometers
> reading zero. The app warns and does not refuse — a deliberate decision, matching what MSI
> Center M itself permits.

## Cleanup waiting on the uninstall

- **`RegistryTdpProvider` and `RegistryHwMouseProvider`** are both inert and both provably weaker
  than the firmware paths that replaced them. Delete once MSI Center M is uninstalled and both
  firmware paths are confirmed without it. That is also what finally removes `PerfMode`,
  `TdpBackendKind.RegistryMirror` and `IsMsiCenterRunning`.
- **Automate the package version bump.** Worth doing *before* three features' worth of install
  cycles.

## Still unverified

- **That `MSI_ACPI` survives an actual MSI Center M uninstall.** Everything so far was proven with
  its stack *stopped*, which is not the same thing. **This is the assumption the entire plan rests
  on**, and the cheapest way to settle it is to uninstall MSI Center M on a spare image, or accept
  the risk and keep the registry fallbacks until the real uninstall happens.
- **That desktop mode works on the UAC secure desktop.** This is the whole premise of the firmware
  route over software cursor injection, and it has never been tested. Trigger any elevation prompt
  and try to move the cursor.
- **Uninstall/restore flow**, never tested end-to-end. Lower stakes than it sounds while MSI Center
  M is still installed, but that changes when it is not: once MSI Center M is gone, this app is the
  only way back to a default. Controller mode is deliberately never restored — the physical button
  owns that state as much as we do.

## Widget placement in the Game Bar — answered

**Measured 2026-08-10** with `Diagnostics/Get-GameBarWidgets.ps1`; raw output in
`Diagnostics/widget-export.txt`.

We land **left of everything except MSI Quick Settings, Home and Settings.** MSI takes the far-left
slot with the visual divider, and **there is no manifest property we are missing** — MSI declares
exactly the placement set we do (`IsDeviceWidget`, `HomeMenuVisible`, `FavoriteAfterInstall`,
`ActivateAfterInstall`). Notably MSI does *not* declare `CompactModePriorityPlacement`, which had
been added here on the theory that it was the mechanism; it was removed rather than left in on a
guess.

`IsDeviceWidget` is the divider slot, there is evidently only one of it, and MSI wins. The tiebreak
is not reachable from our manifest, so **the way to take that slot is to remove MSI Quick
Settings** — which is the plan anyway. Ruled out along the way: Game Bar carries no OEM allowlist.

Full property set Game Bar's parser understands, for reference:

```
ActivateAfterInstall   CompactModePriorityPlacement   FavoriteAfterInstall   HomeMenuVisible
IsDeviceWidget         PinningSupported               SettingsSupported      Window/Size/ResizeSupported
```

## Gamepad navigation — fixed 2026-08-10

**The root cause was a missing manifest entry, not widget code.** The Game Bar SDK documents a
package-level `windows.activatableClass.proxyStub` extension as a required step for every widget,
registering Metadata Based Marshaling for Game Bar's private COM interfaces. This project omitted
it from the start, with a manifest comment claiming it was needed only for programmatic widget-bar
navigation — **our own inference, and wrong.**

Without it the widget rendered, connected, resized and reported `VisibleChanged` correctly, and was
completely inert to both controller and keyboard. It was never version-dependent, which is why
reverting to known-good builds never helped. **Check the SDK readme first** — it is regenerated per
NuGet version.

### The focus defects found on the way

All four were real bugs and **none was why navigation did not work.** Kept because they remain
latent hazards.

1. ~~**Nothing was ever focused.**~~ `SetInitialFocus` ran before layout, so every candidate
   measured `ActualHeight == 0`. **All of this code was deleted in 0.2.0.17** — see below.
2. **Selecting a segment threw focus away.** `SegmentedControl.Show` assigned a different `Style`
   per button; assigning `Style` re-applies the control template, and a control that rebuilds its
   template loses focus. Now three brushes set in place by `Paint`. **Still live.**
3. ~~**Focus landed on the second card.**~~ Superseded by the deletion.
4. ~~**Dead on every show after the first.**~~ Superseded by the deletion.

**Why every earlier test missed these:** a mouse click sets focus, papering over all four. The
`--fake-hardware` pass had the same blind spot. Nothing but a controller finds these, and no unit
test reaches them.

**Two presses of Up to leave the widget is NOT a bug** — every Game Bar widget behaves that way,
Microsoft's bundled ones included.

### The widget no longer places focus at all — 0.2.0.17, 2026-08-12

**`SetInitialFocus`, `ArmInitialFocus`, `OnLayoutUpdated`, `IsFocusInsideContent`,
`_initialFocusSet`, `_snapshotApplied` and `SegmentedControl.FirstSegment` are gone.** Game Bar
places focus; this widget does not. Do not reintroduce a `Focus()` call on load, on show, or on a
snapshot.

**What the grab actually cost.** It pulled focus inside as soon as layout ran, and again on every
`VisibleChanged` — every two to three seconds in compact mode, the only mode this device uses. Two
consequences, neither obvious from the code:

- **Game Bar's "press Down to enter the widget" stopped working**, because focus was already inside
  before the user pressed anything.
- **Focus was parked on `Pl1Slider`**, and a UWP `Slider` consumes all four arrow keys, so no key
  could move off it.

While the TDP card was first this was invisible: the grab landed exactly where Down would have put
you. Moving the Controller card to the top made the grab land in the *second* card, and the whole
thing surfaced as "reordering the cards broke keyboard navigation."

**It was not the card order.** Five builds went into the card position, XY keyboard navigation, a
slider `KeyDown` handler and the focus-candidate list. Every one was reverted.

**How it was finally found:** a temporary on-screen readout of `FocusManager.GotFocus`, printing the
focused element into the widget itself. It showed `(null)` → `Pl1Slider` immediately on open, and
that single observation invalidated every theory at once. **Reach for that instrument early.** The
symptom is only ever visible on device, and no amount of code reading substitutes for seeing where
focus actually is.

**Method note worth more than the fix:** the report was "keyboard navigation worked perfectly before
we moved the card." That was literally true and should have been treated as data. Two rounds were
spent constructing explanations for why the old behaviour had only *appeared* to work.

### Sliders use focus engagement — 0.2.0.17

`IsFocusEngagementEnabled` is set in `CardSliderStyle` (App.xaml), so it covers all three sliders.
Enter/A engages, Esc/B releases.

**Correction: engagement does NOT help the keyboard.** It was turned on believing it would fix the
keyboard slider trap. It cannot — engagement applies to **gamepad and remote input only**, so
`IsFocusEngaged` is never true on a keyboard path. It is kept because it works well on the
controller, which is how this device is actually driven. `Slider_KeyDown` exists to let Escape
release a *gamepad*-engaged slider instead of closing the Game Bar.

### `XYFocusKeyboardNavigation` is required, and was wrongly blamed twice

It is set on `RootContent` and **must stay**. A gamepad gets XY focus navigation for free; a
keyboard does not, and without this its arrows do nothing.

Its history is a case study in blaming the wrong change. It went in at 0.2.0.12, was reverted at
0.2.0.14 after keyboard navigation got *worse*, and went back in at 0.2.0.18. The revert was a
misread: the widget was still grabbing focus onto the `ScrollViewer` and the PL1 slider, so the
arrows had no sane origin, and the resulting mess was attributed to XY. Once the grab was deleted in
0.2.0.17, the same line simply worked.

**The tell was there the whole time:** the gamepad was always fine and the keyboard never was. That
is precisely the difference XY navigation makes, and it should have pointed here far sooner.

### Arrows cannot leave a focused slider — accepted, not fixed

A UWP `Slider` consumes all four arrow keys, so keyboard arrows navigate the cards right up until
they land on a slider, and then only change its value. **Tab is the way off.**

0.2.0.19 fixed this with a `Slider_KeyDown` that took Up/Down and redirected them through
`FocusManager.FindNextElement`, leaving Left/Right to adjust. **It was reverted without being
shipped:** other Game Bar widgets behave the same way, so this is platform-consistent, and matching
the platform beats carrying code to deviate from it. Same reasoning as "two presses of Up to leave
the widget".

Likewise **Enter is needed to enter the widget** — Down will not do it. Also platform behaviour.

### ~~Pinning loses focus~~ — moot since 0.2.0.17

This described the re-arm keying off `VisibleChanged`, which a pinned widget never raises on the way
back. **There is no re-arm any more** — the widget does not place focus at all, so there is nothing
to miss. Doubly moot: this device uses compact mode, which has no pinning.

## History: the three removed features

Removed 2026-08-08 on the reasoning that MSI Center did them better. **That reasoning has expired**
— see [Next up](#next-up). Their `Function` ordinals were retired: 20–23 and 81 (fan), 30–31
(charge limit), 40 (RGB).

> **Correction (2026-08-12): do NOT reuse the retired ordinals.** An earlier version of this line
> said to. `Function.cs` states the opposite as a hard rule, and it is right — an old widget meeting
> a new helper would route a stale message onto whatever took the number, and that fails silently.
> Bringing a feature back means taking the **next free number in its group's gap**: the charge limit
> is `32`, not `30`/`31`.

A line that used to appear here and in the README — *"nothing in this app writes to the embedded
controller"* — **is no longer true.** Power limits write it through `MSI_ACPI`, and controller mode
writes the controller's firmware over HID.
