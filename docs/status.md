# Status — 2026-08-10

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, what's
broken, and exactly what to run next. See [`hardware-notes.md`](hardware-notes.md) for the full
gate-by-gate detail.

## Current build

**0.2.0.0, Release configuration.** First release build — the baseline for Intel/G6 work.

```
src/Package/AppPackages/McenterLite.Package_0.2.0.0_Test/
```

Install from the repo, in any PowerShell — the script elevates and re-launches under 5.1 itself:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\Package\Install.ps1
```

It picks the newest package under `src/Package/AppPackages/` on its own.

To see the whole UI on a machine that is not a Claw (the device gate otherwise hides every
hardware card):

```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnostics\Start-FakeHelper.ps1
```

## Confirmed working on the Claw

Verified on device **2026-08-10**, against the current UI rather than an earlier layout.

- **Power limits** — sliders apply, the mode selector gates them, and the two limits move
  independently under the `PL2 ≥ PL1 + 2` rule.
- **Windows power** — CPU Boost (Off / On) and OS Power Mode (Efficiency / Balanced / Performance),
  including AC↔DC sync and reflecting changes made from Windows Settings or the taskbar flyout.
- **Controller mode (G5)** — switching between Gamepad and Desktop works in both directions from
  the widget. The physical MSI button does not sync back; see the limitation below.
- **Gamepad navigation** — confirmed on device 2026-08-10 after the proxyStub fix, in compact mode.
  Focus stays where the user put it; the `VisibleChanged` focus-steal hazard did not materialise.

The Graphics card does not appear on the Claw and is not expected to: `HasIgcl` is hard-coded false
until G6 is implemented. It is only visible under `--fake-hardware`.

### Known limitation: the physical MSI button does not sync

**Measured on the Claw 2026-08-10.** Widget → hardware works in both directions and stays in sync.
Hardware → widget does not: pressing the physical MSI button changes the mode and the widget's
buttons do not follow. Using only the widget keeps everything consistent.

The helper is not the problem — `Program.RunTelemetryLoopAsync` already pushes `HwMouseMode` once a
second whenever the pipe is connected and the widget is visible. The open question is whether
`OsdEditor\ControlModeUserSet` reflects the button at all, and `hardware-notes.md` gate G5 has
always listed that as unknown.

**Accepted for now, not being chased.** Using the widget alone is consistent, which is the normal
case. `Diagnostics/Watch-ControlMode.ps1` is there for whenever it is worth settling: it polls the
value at 4 Hz and prints every change, and the two outcomes distinguish the causes cleanly —

- **No change when the button is pressed** → the registry is a software-write mirror, not live
  device state. MSI Center writes it when something asks *it* to change mode; the button talks to
  firmware directly. Polling cannot fix that, and reading the true state needs the vendor HID
  channel (opcode `0x04`), which is undecoded. Document the limitation rather than chase it.
- **It does change** → the fault is ours, most likely the `WidgetVisible` gate on the telemetry
  loop: Game Bar toggles `Visible` every two to three seconds in compact mode and the loop skips a
  tick whenever it is false.

Switch mode from the widget during the same run as a control — that path is known to work, so it
proves the watch itself is functioning.

### Still unverified

**The premise of the whole feature:** that desktop mode still works on the UAC secure desktop. That
is why the firmware route matters over software cursor injection. Trigger any elevation prompt and
try to move the cursor.

## Widget placement in the Game Bar — answered

**Measured on the Claw 2026-08-10** with `Diagnostics/Get-GameBarWidgets.ps1`; raw output kept as
`Diagnostics/widget-export.txt`.

Where we land: **left of everything except MSI Quick Settings, Home and Settings.** MSI takes the
far-left slot with the visual divider.

**There is no manifest property we are missing.** MSI declares exactly the placement set we do:

```
IsDeviceWidget=true   HomeMenuVisible=true   FavoriteAfterInstall=true   ActivateAfterInstall=true
```

Notably MSI does **not** declare `CompactModePriorityPlacement`, which had been added here on the
theory that it was the mechanism. It is a real property — it appears in `GameBar.exe`'s
manifest-parser string table beside the others — but the widget that actually wins the slot does
not use it, so it is not the answer. **Removed rather than left in on a guess.**

`IsDeviceWidget` is the divider slot, there is evidently only **one** of it, and MSI wins. This
settles a question the manifest had carried as unverified since it was written. The tiebreak is not
reachable from our manifest, so **the way to take that slot is to remove MSI Quick Settings**, which
is the plan anyway.

Ruled out along the way: Game Bar does **not** carry an OEM allowlist. No MSI or ASUS package
identity appears anywhere in its binaries. (`Armoury` does appear in `GameBar.exe`, but in the
game-launcher tile list beside Steam, Epic, GOG and Alienware Command Center — unrelated to
widgets.)

The full property set Game Bar's parser understands, for future reference:

```
ActivateAfterInstall   CompactModePriorityPlacement   FavoriteAfterInstall   HomeMenuVisible
IsDeviceWidget         PinningSupported               SettingsSupported      Window/Size/ResizeSupported
```

## Removed features

All three removed 2026-08-08, for the same reason: MSI Center already does them, and does them
better than this widget could. Narrowing scope, not abandoning work.

**Fan presets.** Gate G2 never resolved the byte layout — the six-point curve MSI ships could not
be reconciled with the five-point model the desk research described — so nothing was ever written
to the EC. `Function` ordinals 20–23 are retired, along with 81 (`IntelThermalCmd`), which existed
only to stop Intel's thermal stack latching the fan above an EC table we no longer write.

**Battery charge limit.** Set in MSI Center, changes rarely, and the registry path this app could
reach did not enforce it. `Function` ordinals 30 and 31 are retired.

**RGB LED.** Mode, colour and effect ride a vendor HID report that was never decoded (Gate G4), so
the most this widget could offer was an on/off toggle sitting next to MSI Center's far richer
control. `Function` ordinal 40 is retired.

All findings are kept in [`hardware-notes.md`](hardware-notes.md) as a device record rather than
deleted — the charge limit in particular was eventually traced to `MSI_ACPI.Get_AP` / `Set_AP`,
sub-function 0, byte 5, encoded `percent | 0x80`, which took several rounds of on-device
measurement to find.

**A consequence worth noting:** nothing in the app writes to the embedded controller any more.
Fan control was the only feature that would have.

## Gamepad navigation — fixed 2026-08-10, confirmed working

**The root cause was a missing manifest entry, not any of the widget code below.**

The Game Bar SDK's readme documents a **package-level** `windows.activatableClass.proxyStub`
extension as a required step for every widget. It registers Metadata Based Marshaling for Game
Bar's private COM interfaces — `IXboxGameBarWidgetHost1-9`, `IXboxGameBarWidgetPrivate1-6` and
`IXboxGameBarNavigationKeyCombo`. This project omitted it from the start, with a manifest comment
claiming it was "needed only for programmatic widget-bar navigation, which is out of scope". That
was **our own inference and it was wrong** — the SDK states no such limitation.

Without it the widget rendered, connected, resized and reported `VisibleChanged` correctly, and
was completely inert to the controller AND the keyboard. Adding it fixed navigation immediately.

**It was never version-dependent**, which is why reverting to known-good builds never helped: the
entry had been missing since the package was first authored. Several hours went into focus code
before anyone read the SDK's own setup instructions. **Check the SDK readme first** — it is
regenerated per NuGet version and says so.

### The focus defects found on the way

All four were real bugs, and **none of them was why navigation did not work.** Worth keeping
because they are all still latent hazards, but they were symptoms, not the cause.

1. **Nothing was ever focused.** `SetInitialFocus` ran in the same dispatcher callback that made
   the cards visible, and XAML defers layout to the next frame — so every candidate still measured
   `ActualHeight == 0`, the size guard skipped all of them, and focus was never set. Nothing called
   it again, because the snapshot normally arrives once. With no focused element there is no origin
   for XY focus navigation, so the D-pad had nothing to move from. `OnLayoutUpdated` now does it,
   once the sizes are real.
2. **Selecting a segment threw focus away.** `SegmentedControl.Show` assigned a different `Style`
   object to each button; assigning `Style` re-applies the control template, and a control that
   rebuilds its template loses focus. The selected state is now three brushes set in place by
   `Paint`, so the template is built once and kept.
3. **Focus landed on the second card.** The first fix left the synchronous `SetInitialFocus()` call
   in `OnSnapshotApplied`, where the just-unhidden TDP card still measured zero but the
   never-hidden Windows power card already had a real height — so focus went to CPU boost and
   latched, and the `LayoutUpdated` retry unsubscribed without reconsidering. That call is gone.
4. **Dead on every show after the first.** `_initialFocusSet` latched for the session. Game Bar
   hiding the widget un-focuses the focused element and nothing puts it back, so the second open
   was indistinguishable from having no focus code at all. `ArmInitialFocus` re-arms on
   `VisibleChanged → true` — the one moment there is nothing to disturb.

Initial focus uses `FocusState.Keyboard` rather than `Programmatic`, which is what actually reveals
the focus rectangle. On a device with no cursor, a focused control you cannot see is
indistinguishable from broken navigation.

**Why every earlier test missed these:** a mouse click sets focus, so it papered over all four. The
`--fake-hardware` pass had the same blind spot. Nothing but a controller finds these — worth
remembering for any future UI change, since none of it is reachable by a unit test either.

**Two presses of Up to leave the widget is NOT a bug.** The first lands on something invisible, the
second exits to Game Bar. Every Game Bar widget behaves this way, Microsoft's bundled ones
included, so it is the platform's navigation model. `IsTabStop="False"` on the `ScrollViewer`
suppresses it and was deliberately not kept — matching every other widget beats saving a press, and
the mechanism was never confirmed.

### Open risk: the VisibleChanged re-arm still steals focus

`ArmInitialFocus` runs on every `VisibleChanged → true` and unconditionally re-focuses the top
control. In **compact mode Game Bar toggles `Visible` every two to three seconds** — captured
directly in the widget trace — so this can drag focus back to Endurance while the user is
navigating.

It has not bitten since the proxyStub fix, so it is shipped as-is rather than churned again, but it
is a live hazard. **Symptom to watch for: focus jumping back to the top control on its own after a
few seconds of no input.** The fix, if it appears, is a guard that makes `ArmInitialFocus` a no-op
when `FocusManager.GetFocusedElement()` is already inside `RootContent` — tried in 0.1.0.39 and
reverted, but only because it was bundled with three extra event subscriptions that broke the
first open. The guard alone was never the problem.

### Pinning loses focus — accepted, not fixed

Re-arming keys off Game Bar's `VisibleChanged`, and a **pinned** widget stays `Visible` when the
overlay is dismissed, so that event never fires on the way back. Confirmed on 2026-08-10: focus is
lost the moment the widget is pinned and does not come back.

**Deliberately not fixed.** This device uses the Game Bar in **compact mode, which has no pinning**,
so the broken path is one the user cannot reach. `PinningSupported` stays true in the manifest
because it costs nothing and Game Bar shows the affordance in desktop mode.

An attempt was made and **reverted** (0.1.0.39). It made `ArmInitialFocus` refuse to steal focus
already inside the widget — checking `FocusManager.GetFocusedElement()` against the visual tree —
so it could safely hang off `PinnedChanged`, `GameBarDisplayModeChanged` and
`Window.Current.Activated`. That broke navigation on the **first** open of the Game Bar, before
pinning was involved at all. The cause was never established; the likeliest candidate is that
something in our tree (the `ScrollViewer` is focusable) holds focus early, so the steal-guard reads
as "focus is already inside" and suppresses the initial focus that the user actually needs. **If
this is ever revisited, start by testing that hypothesis** rather than re-adding the same three
event subscriptions. 0.1.0.40 is 0.1.0.38's navigation code, re-versioned so it installs over the
reverted build.

## Needs testing on the Claw

- **UAC secure desktop** — the premise of G5's firmware route, still never tested.

## Outstanding work

- **Intel GPU controls (G6)** remain an unimplemented stub. This is the next piece of work.
- **Uninstall/restore flow**, never tested end-to-end. Deliberately **not** treated as
  release-blocking: this widget writes MSI Center's own `ManualPL*` values, MSI Center stays
  installed, and its UI rewrites all four whenever a limit is changed there — so a user always has
  a way back to any value they want, through the app that owns the setting. Restoring on uninstall
  is a courtesy, not a safety net. Controller mode is deliberately not restored at all: the
  physical button owns that state as much as we do, so replaying an old value would be a guess
  rather than a restore.
