# Status — 2026-08-12

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, and exactly
what to do next. See [`hardware-notes.md`](hardware-notes.md) for gate-by-gate detail, and its
[What's next](hardware-notes.md#whats-next--fan-charge-limit-and-rgb) section for the technical
notes behind the roadmap below.

## The headline

**The MSI Center M dependency is nearly gone.** All three hardware features that ship now talk to
the firmware directly and need MSI Center M neither running nor installed:

- **Power limits** via `MSI_ACPI.Set_SlaveBattery` (ACPI-WMI), merged 2026-08-11.
- **Controller mode** via the controller's vendor HID channel, merged 2026-08-12.
- **Battery charge limit** via `MSI_ACPI.Set_AP` (ACPI-WMI), merged 2026-08-12.

Everything remaining is either the two features still descoped and coming back — RGB then fan — or
cleanup that waits on MSI Center M actually being uninstalled.

## Current build

**0.2.0.8, Debug.** Installed and verified on the Claw 2026-08-12.

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

## Confirmed working on the Claw

| Feature | Path | Verified |
|---|---|---|
| Power limits (PL1/PL2) | `MSI_ACPI.Set_SlaveBattery` | 2026-08-11, against sustained-clock oracle |
| Controller mode | vendor HID `0x24`/`0x26`/`0x27` | 2026-08-12, both directions + physical button |
| Battery charge limit | `MSI_ACPI.Set_AP` | 2026-08-12, charging resumed past the old limit |
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

### 1. RGB LED (G4) — next

**Much closer than the G4 section suggests.** That section still says report `0x0F` is unverified
and unstarted; G5 has since proven it, established its framing, and shipped `MsiVendorHidChannel`,
which speaks it. Finding and opening the channel — most of the work — is done.

The concrete lead: with MSI Center M restarting, the controller emits a long multi-frame `0x05`
dump whose payload is full of plausible RGB triples (`FF 00 00`, `FF A0 00`, `C8 C8 FF`) alongside
per-zone records. **Capture that dump while changing one colour in MSI Center M, diff it, and that
is very likely the whole gate.** `--hid-watch` already records it.

**Scope, set 2026-08-12:** the widget does *not* need a colour picker. MSI Center M holds **three
saved profiles**, and those three are the ones to keep. The widget should **cycle between the three
and turn the lighting off and back on** — four states, no authoring.

That makes the gating question narrower but also sharper: **where do those three profiles live?**

- *If the controller stores them*, this is small — find the "select profile N" command and the
  on/off command, and nothing needs to be captured at all.
- *If MSI Center M stores them* and merely pushes the resulting colours down, then the profiles die
  with the uninstall. In that case each profile must be **captured as its literal payload while MSI
  Center M is still installed** and replayed by us afterwards. There is no second chance at that
  capture once MSI Center M is gone.

Settle that question first — it decides whether this feature is a lookup or an archive. Either way,
**capture all three profiles before uninstalling anything.**

### 2. Fan control (G2) — hardest, do last

The only one of the three that would write the embedded controller, and the only one blocked on a
real contradiction rather than unfinished work: MSI's curve on this device is **six** points, while
the model implemented here is a five-point 8-byte table taken from a **different machine** (the
Lunar Lake A2VM). Duty scales were never reconciled.

Start read-only: `MSI_ACPI.Get_Fan` diffed against MSI Center's six-point curve answers the layout
question without writing anything. Note also that Intel's thermal stack (`ipfsvc`) is an
independent fan actor here, so "our table is correct" and "the fan behaves" are separate claims.

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

1. **Nothing was ever focused.** `SetInitialFocus` ran in the same dispatcher callback that made
   the cards visible, and XAML defers layout a frame — every candidate measured `ActualHeight == 0`
   and the size guard skipped all of them. `OnLayoutUpdated` now does it once sizes are real.
2. **Selecting a segment threw focus away.** `SegmentedControl.Show` assigned a different `Style`
   per button; assigning `Style` re-applies the control template, and a control that rebuilds its
   template loses focus. Now three brushes set in place by `Paint`.
3. **Focus landed on the second card.** A leftover synchronous `SetInitialFocus()` in
   `OnSnapshotApplied` ran while the TDP card still measured zero but the never-hidden power card
   did not. That call is gone.
4. **Dead on every show after the first.** `_initialFocusSet` latched for the session.
   `ArmInitialFocus` re-arms on `VisibleChanged → true`.

Initial focus uses `FocusState.Keyboard`, not `Programmatic` — that is what reveals the focus
rectangle. On a device with no cursor, a focused control you cannot see is indistinguishable from
broken navigation.

**Why every earlier test missed these:** a mouse click sets focus, papering over all four. The
`--fake-hardware` pass had the same blind spot. Nothing but a controller finds these, and no unit
test reaches them.

**Two presses of Up to leave the widget is NOT a bug** — every Game Bar widget behaves that way,
Microsoft's bundled ones included.

### Open risk: the VisibleChanged re-arm still steals focus

`ArmInitialFocus` runs on every `VisibleChanged → true` and unconditionally re-focuses the top
control. **In compact mode Game Bar toggles `Visible` every two to three seconds**, so this can
drag focus back while the user is navigating. It has not bitten since the proxyStub fix, so it
ships as-is.

**Symptom to watch for: focus jumping back to the top control on its own after a few seconds of no
input.** The fix, if needed, is a guard making `ArmInitialFocus` a no-op when
`FocusManager.GetFocusedElement()` is already inside `RootContent`. That was tried in 0.1.0.39 and
reverted — but only because it was bundled with three extra event subscriptions that broke the
first open. **The guard alone was never the problem**; if this is revisited, test that hypothesis
before re-adding the subscriptions.

### Pinning loses focus — accepted, not fixed

Re-arming keys off `VisibleChanged`, and a pinned widget stays `Visible` when the overlay is
dismissed, so the event never fires on the way back. **Deliberately not fixed:** this device uses
compact mode, which has no pinning, so the broken path is unreachable here.

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
