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

**No Windows API exists for this.** Microsoft has never shipped one — charge thresholds are vendor
EC/BIOS features, which is why every OEM ships its own utility. MSI's `MSI_ACPI` ACPI-WMI class is
the only driver-free route, and it is confirmed present on the device
(`Diagnostics/device-report.txt:489`). ClawTweaks cannot be consulted: its public repo contains
only the pipe-client side, never the hardware layer, which ships as a compiled binary only.

**Working hypothesis for the encoding:** the threshold byte is `percent | 0x80` — bit 7 an
enable/commit flag, bits 0–6 the percentage. Sourced from the `msi-ec` Linux driver, which
documents MSI firmware's EC layout; that is a property of the embedded controller and independent
of the OS, so nothing Linux is used or ported. **Unverified on the Claw** (a handheld, not one of
the laptops that driver covers) — confirm by reading before writing. Expected: 60% → `0xBC`,
80% → `0xD0`, 100% → `0xE4`.

**Decision: writes go through `Set_MasterBattery` only.** No raw `MSI_ACPI.Set_EC` — a wrong
address there puts a raw byte into real firmware that could land on fan or thermal registers.
This is enforced in code, not just documented: `--acpi-get` refuses any method not named `Get_*`.

### Step 1 — read, and decode the encoding

**No compiled tool needed.** WMI is native to PowerShell, so this needs only one small script —
copy `Diagnostics/Test-BatteryWmi.ps1` to the Claw (nothing else) and run it in an **elevated**
PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\Test-BatteryWmi.ps1
```

Read-only. It does three things in one pass: dumps `MSI_Master_Battery`'s properties (a plain
class read, no method call at all), prints `Get_MasterBattery` / `Set_MasterBattery`'s declared
parameter shapes, then calls `Get_MasterBattery` and dumps the buffer as indexed hex.

Run it three times, setting **MSI Center's own** charge limit to 100 / 80 / 60 in between.
Whichever value tracks those three is the threshold — that is the fact the fix depends on.

> Equivalent Probe commands exist (`--wmi-instances MSI_Master_Battery`, `--wmi-method`,
> `--battery`, `--set-charge-limit`) if the binary is already on the device. The Probe is a single
> self-contained `.exe` at
> `src/Probe/bin/Release/net8.0-windows/win-x64/publish/McenterLite.Probe.exe` — copy that one
> file and run it elevated; it needs no installer, certificate or dependencies. The PowerShell
> script is the lighter option when it is not already there.

### Step 2 — the write, once the read makes sense

```powershell
powershell -ExecutionPolicy Bypass -File .\Test-BatteryWmi.ps1 -SetLimit 60
```

Prints the byte it will send, and reads back before and after. Then the test that actually matters,
since a changed read-back only proves the value landed:

```powershell
.\Watch-Battery.ps1 -Limit 60      # plug in, below 60%; reports ENFORCED / NOT ENFORCED
```

Then `--set-charge-limit 100` and confirm charging *resumes* — that is the case that failed via the
registry, so it is the sharpest signal the WMI path is genuinely different. Finally reboot and
re-read to confirm it persists in the controller.

If any of this errors with "no method named", run `--wmi-classes MSI` first and report the real
method list instead.

**Then:** implement `WmiChargeLimitProvider` against the measured facts and swap it into
`WindowsHardware.cs:66`. Likely shape is registry for read/display (it already round-trips and
keeps MSI Center's UI in sync) plus WMI for apply — but that is a decision to make against
measurements, not in advance.

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
