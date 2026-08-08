# Status — 2026-08-08

A snapshot for picking this back up: what's built, what's confirmed on the real Claw, what's
broken, and exactly what to run next. See [`hardware-notes.md`](hardware-notes.md) for the full
gate-by-gate detail behind the two open bugs below.

## Current build

**0.1.0.26, Release configuration.**

```
src/Package/AppPackages/McenterLite.Package_0.1.0.26_x64_Test/
```

```powershell
.\Install.ps1 -PackagePath ".\McenterLite.Package_0.1.0.26_x64_Test\McenterLite.Package_0.1.0.26_x64.msix" -CertificatePath ".\msi-mcenter-lite.cer"
```

Contains everything through the Lighting on/off card (the last feature added this session).
Nothing is uncommitted right now beyond what's already on `feat/m0-scaffolding` — the two bugs
below are being diagnosed before any more code changes ship.

## Confirmed working on the Claw

- CPU Boost toggle
- OS Power Mode segmented control (Efficiency / Balanced / Performance), including AC↔DC sync
- Power Limits (TDP) card: sliders, mode selector (Endurance / User Scenario / AI Engine) as
  segmented buttons, sliders correctly disabled outside User Scenario
- Fan percentage: confirmed settable 0–100% via MSI Center's own UI (not yet via this app — fan
  control is still unimplemented, Gate G2)

## Two open bugs

### 1. Battery charge limit does not enforce when set from the widget

**Root cause found, fix blocked on a device measurement.** A/B test on device: setting the same
`BatteryLevel` registry value through the widget vs. through MSI Center's own UI reads back
identical either way, but only the MSI-Center-driven change actually changes charging behaviour.
Most visible at 100% ("off") — setting it from the widget does not resume charging.

This means the registry write (`RegistryChargeLimitProvider`) is real but is **not** the actual
apply path — unlike TDP, where `Test-TdpRegistryApply.ps1` proved the registry mirror alone is
sufficient. MSI Center's own UI is doing something extra, most likely calling
`MSI_ACPI.Set_MasterBattery` directly (the ACPI-WMI path the original desk research always expected
this feature to need). Full writeup: [`hardware-notes.md` Gate G3](hardware-notes.md#gate-g3--battery-charge-limit).

**Next step — needs to run on the Claw:**

1. Copy over the freshly published Probe: `src/Probe/bin/Release/net8.0-windows/win-x64/publish/McenterLite.Probe.exe`
2. Elevated PowerShell:
   ```powershell
   .\McenterLite.Probe.exe --wmi-method MSI_ACPI Set_MasterBattery
   .\McenterLite.Probe.exe --wmi-method MSI_ACPI Get_MasterBattery
   ```
   This is read-only — it dumps the method's declared parameters without calling it.
3. If either errors with "no method named", first run `--wmi-classes MSI_ACPI` to confirm the
   exact class is present and get the real method list, and report that instead.
4. Paste back whatever it prints. That parameter shape is what's needed to write
   `Set_MasterBattery` support with confidence instead of guessing at an EC write.

### 2. Lighting card does not appear at all

Expected `HasLed` to be true — `LightingBrightness` was captured directly on this same device
during Phase 0's mode-switch testing, so the value should exist. Needs a direct registry check to
tell apart "value doesn't exist yet" (bootstrap issue, same as Charge Limit needing MSI Center
opened once) from "value exists but isn't the type the provider expects" (an actual code bug in
`RegistryLedProvider`).

**Next step — needs to run on the Claw:**

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor' -Name LightingBrightness
```

- Errors "property does not exist" → open MSI Center's own app, toggle its lighting control once,
  recheck. This would match Charge Limit's own "open it once" bootstrap requirement.
- Prints a value → report what type/data it shows, so the type check in `RegistryLedProvider` can
  be corrected if it's wrong.

## Also still outstanding (lower priority, not blocking)

- Uninstall/restore flow, end-to-end on the Claw (uninstall should put back every captured
  original value, including the new Lighting on/off state).
- Once charge limit actually enforces: a long-run `Diagnostics/Watch-Battery.ps1` pass to confirm
  it holds at the limit over time and survives a reboot.
