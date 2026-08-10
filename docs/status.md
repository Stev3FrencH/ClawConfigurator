# Status — 2026-08-08

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, what's
broken, and exactly what to run next. See [`hardware-notes.md`](hardware-notes.md) for the full
gate-by-gate detail.

## Current build

**0.1.0.26, Release configuration.**

```
src/Package/AppPackages/McenterLite.Package_0.1.0.26_x64_Test/
```

```powershell
.\Install.ps1 -PackagePath ".\McenterLite.Package_0.1.0.26_x64_Test\McenterLite.Package_0.1.0.26_x64.msix" -CertificatePath ".\msi-mcenter-lite.cer"
```

Note this predates the charge-limit removal — the next build will need a version bump and a
fresh package.

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

## Outstanding work

- **Uninstall/restore flow**, end-to-end on the Claw — should put back the captured original power
  limits. Much less to restore now that three features are gone.
- Desktop/gamepad mode (G5) and Intel GPU controls (G6) remain unimplemented stubs. G5 is the
  cheaper of the two: it is green via registry (`OsdEditor\ControlModeUserSet`, REG_SZ, `"XInput"`
  ↔ `"Desktop"`, confirmed both directions), so it is the same provider pattern as TDP rather than
  new discovery work.
