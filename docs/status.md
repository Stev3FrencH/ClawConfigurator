# Status — 2026-08-10

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, what's
broken, and exactly what to run next. See [`hardware-notes.md`](hardware-notes.md) for the full
gate-by-gate detail.

## Current build

**0.1.0.40, Release configuration.**

```
src/Package/AppPackages/McenterLite.Package_0.1.0.40_Test/
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
- **Controller mode (G5)** — switching between Gamepad and Desktop works in both directions.

The Graphics card does not appear on the Claw and is not expected to: `HasIgcl` is hard-coded false
until G6 is implemented. It is only visible under `--fake-hardware`.

### Still unverified within controller mode

Switching works; these two sub-cases were not part of that test and remain open:

1. **The physical MSI button.** Press it with the widget open — the selected segment should follow
   within a second, off the helper's telemetry tick. This is the part most likely to be wrong,
   since it is the only control the hardware can change behind our back.
2. **The premise of the whole feature:** that desktop mode still works on the UAC secure desktop.
   That is why the firmware route matters over software cursor injection. Trigger any elevation
   prompt and try to move the cursor.

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

**The widget could not be navigated with a controller at all, only with a mouse.** Found on the
Claw, then reproduced and fixed on the dev machine with an Xbox controller under
`--fake-hardware` — the fault is entirely in the widget, so it reproduces anywhere. **Four**
defects, not one; each alone was enough to break it.

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

**Why every earlier test missed this:** a mouse click sets focus, so it papered over all four. The
`--fake-hardware` pass had the same blind spot. Nothing but a controller finds these — worth
remembering for any future UI change, since none of it is reachable by a unit test either.

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

- **Gamepad navigation**, as a re-confirm. Verified on the dev machine with a controller, and the
  code involved is device-independent, but the Claw is the target.
- The two controller-mode sub-cases listed under Confirmed working.

## Outstanding work

- **Uninstall/restore flow**, end-to-end on the Claw — should put back the captured original power
  limits. Note controller mode is deliberately *not* restored: the physical button owns that state
  as much as we do, so replaying an old value would be a guess, not a restore.
- Intel GPU controls (G6) remain an unimplemented stub.
