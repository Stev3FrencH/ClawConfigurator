# Status — 2026-08-08

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, what's
broken, and exactly what to run next. See [`hardware-notes.md`](hardware-notes.md) for the full
gate-by-gate detail.

## Current build

**0.1.0.31, Release configuration.**

```
src/Package/AppPackages/McenterLite.Package_0.1.0.31_x64_Test/
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

- CPU Boost toggle
- OS Power Mode segmented control (Efficiency / Balanced / Performance), including AC↔DC sync
- Power Limits (TDP) card: sliders, mode selector (Endurance / User Scenario / AI Engine) as
  segmented buttons, sliders correctly disabled outside User Scenario
- Fan percentage: confirmed settable 0–100% via MSI Center's own UI (not yet via this app — fan
  control is still unimplemented, Gate G2)

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

## No open bugs

The Lighting card not appearing was the last one, and removing the feature closed it.

## Needs testing on the Claw

**Controller mode (G5), newly implemented.** `RegistryHwMouseProvider` writes
`OsdEditor\ControlModeUserSet` (`"XInput"` ↔ `"Desktop"`). Worth checking:

1. **Desktop** actually turns the right stick into a cursor.
2. **Gamepad** puts it back.
3. **The physical MSI button.** Press it with the widget open — the selected segment should follow
   within a second, because the helper pushes this on its telemetry tick. This is the part most
   likely to be wrong, since it is the only control the hardware can change behind our back.
4. **The premise of the whole feature:** that desktop mode still works on the UAC secure desktop.
   That is why the firmware route matters over software cursor injection, and it has never been
   tested. Trigger any elevation prompt and try to move the cursor.

**CPU boost is now two buttons** (Off / On) rather than a toggle, so it lines up with the Power
mode segments. Same underlying control — worth a quick confirm it still applies.

## Outstanding work

- **Uninstall/restore flow**, end-to-end on the Claw — should put back the captured original power
  limits. Note controller mode is deliberately *not* restored: the physical button owns that state
  as much as we do, so replaying an old value would be a guess, not a restore.
- Intel GPU controls (G6) remain an unimplemented stub.
