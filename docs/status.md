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

## Removed: battery charge limit

**Descoped 2026-08-08.** It is set in MSI Center, changes rarely, and the registry path this app
could reach did not enforce it. Removed from the widget, the helper, the IPC contract and the
hardware layer; `Function` ordinals 30 and 31 are retired and must never be reused.

The research is kept rather than thrown away — the limit was eventually traced to
`MSI_ACPI.Get_AP` / `Set_AP`, sub-function 0, byte 5, encoded `percent | 0x80`. See
[`hardware-notes.md` Gate G3](hardware-notes.md#gate-g3--battery-charge-limit) if it is ever
revisited. `Diagnostics/Sweep-MsiAcpi.ps1` is worth keeping regardless: the same sweep-and-diff
approach should locate the fan table for Gate G2.

## One open bug

### Lighting card does not appear at all

Expected `HasLed` to be true — `LightingBrightness` was captured directly on this same device
during Phase 0's mode-switch testing, so the value should exist. Needs a direct registry check to
tell apart "value doesn't exist yet" (a bootstrap issue — MSI Center may have to write it once)
from "value exists but isn't the type the provider expects" (an actual code bug in
`RegistryLedProvider`).

**Next step — needs to run on the Claw:**

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor' -Name LightingBrightness
```

- Errors "property does not exist" → open MSI Center's own app, toggle its lighting control once,
  then recheck.
- Prints a value → report what type/data it shows, so the type check in `RegistryLedProvider` can
  be corrected if it's wrong.

## Also still outstanding (lower priority, not blocking)

- Uninstall/restore flow, end-to-end on the Claw (uninstall should put back every captured
  original value, including the new Lighting on/off state).
- Gate G2, fan control: still needs the byte layout resolved before anything is written.
  `Diagnostics/Sweep-MsiAcpi.ps1` is the tool to try — snapshot across MSI Center's fan settings
  and diff, the same way the charge limit was eventually located.
