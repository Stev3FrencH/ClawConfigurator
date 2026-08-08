# Hardware notes — MSI Claw 8 EX AI+

> **Status: Phase 0 ran on the device on 2026-08-07.** `Diagnostics/device-report.txt` and
> `Diagnostics/watch-msicenter-transcripts.zip` are the raw evidence, captured on a **fresh Windows
> install with only MSI Center M present** — no ClawTweaks, so every change observed was made by
> MSI's own software. **G1 is green. G3, G5 and G6 are green. G2 and G4 are partly answered.**
>
> Measured facts live in [Measured on device](#measured-on-device-2026-08-07) and **supersede**
> the [desk research](#desk-research-clawtweaks-public-repo-2026-08-07) below, which was taken from
> the ClawTweaks source and describes a **different model** (the Lunar Lake A2VM) wherever the two
> disagree. Where they disagree, the disagreement is called out.
>
> This file is the single most important artifact in the repository. Everything the app does to
> the hardware is implemented against what is written here, so an unverified guess recorded as a
> fact becomes a wrong byte sent to a real embedded controller.

## Rules for this document

1. **Record facts, not code.** Class names, method names, register offsets, byte layouts, report
   formats, measured values. Never paste decompiled source — see [`LICENSE-NOTES.md`](../LICENSE-NOTES.md).
2. **Cite how each fact was established.** Decompilation, ACPI disassembly, observation of MSI
   Center, or direct measurement. A fact with no provenance cannot be re-checked when a firmware
   update breaks it.
3. **Mark anything unverified as unverified.** "Probably" belongs in this file; it does not belong
   in an EC write.
4. **Re-check after a BIOS update.** Record the BIOS version every fact was established against.

## Measured on device (2026-08-07)

Source: `Diagnostics/device-report.txt` (baseline sweep) and
`Diagnostics/watch-msicenter-transcripts.zip` (before/after `HKLM\SOFTWARE\WOW6432Node\MSI`
exports around one deliberate change each, made in MSI Center M's own UI).

**Why this evidence is unusually strong.** The capture was taken on a clean Windows install with
no ClawTweaks and no third-party tooling. Every delta below is MSI's own software changing MSI's
own model — not a reimplementation's guess at it.

### Baseline

| Field | Value |
|---|---|
| Model / board | `Claw 8 EX AI+ CG3EM` / `MS-1T91` |
| BIOS | `E1T91IMS.10A`, 2026-07-28 |
| EC firmware | `1T91EMS1.10` (from `ECversion`) |
| CPU | Intel Arc G3 Extreme, 14C/14T, 1.9 GHz |
| GPU | Intel Arc B390, driver `32.0.101.8801` |
| OS | Windows 11 build 26200 |
| `ControlLib.dll` | **present**, `C:\Windows\System32`, v1.2.288.0 |
| MSI Center M | running, with per-feature server processes |
| Intel thermal | `ipfsvc` running; fan participant `ACPI\INTC10D6\TFN1` |

### Two independent control surfaces

The device exposes **both** of the paths this project was weighing, and they are not exclusive:

**1. MSI Center M's registry model** — what its UI writes, and what its services then apply.
Requires MSI Center M installed and running.

**2. `MSI_ACPI`, a direct ACPI-WMI class** in `root\WMI`, GUID
`{ABBC0F6E-8EA1-11d1-00A0-C90629100000}`, exposing paired get/set methods:

```
GetPackage/SetPackage  Get_EC/Set_EC        Get_BIOS/Set_BIOS      Get_SMBUS/Set_SMBUS
Get_MasterBattery/Set_MasterBattery         Get_SlaveBattery/Set_SlaveBattery
Get_Temperature/Set_Temperature             Get_Thermal/Set_Thermal
Get_Fan/Set_Fan        Get_Device/Set_Device Get_Power/Set_Power   Get_Debug/Set_Debug
Get_AP/Set_AP          Get_Data/Set_Data     Get_WMI               Get_PE/Set_PE
Get_EC2                Get_BIOS_64/Set_BIOS_64                     Get_SMBUS_64/Set_SMBUS_64
Get_Thermal_64/Set_Thermal_64
```

This is the driver-free EC path the project was designed around, and it is **present and
enumerable without MSI Center**. Sibling classes: `MSI_AP`, `MSI_CPU`, `MSI_Device`, `MSI_Event`,
`MSI_Master_Battery`, `MSI_Power`, `MSI_Slave_Battery`, `MSI_Software`, `MSI_System`, `MSI_VGA`.

> **Settled 2026-08-07: writing the registry alone DOES apply.** `Test-TdpRegistryApply.ps1` drove
> PL1 from 8 W to 25 W by registry write only, under sustained load, and the clock followed.
>
> **MSI Center's own UI updated to match while the test ran.** That is not a confound — it is the
> mechanism becoming visible. Nothing but the registry was written, so MSI Center must be watching
> those values, and it is MSI Center that pushes them to the EC. The registry is a live control
> surface.
>
> **The applier is the background server, not the window.** Re-run with the MSI Center UWP window
> closed and only `MSI_Center_M_Server_UserScenario` running: still applies. So this works in the
> normal state of the machine, with MSI Center installed and its services up but nothing on screen.
>
> **What follows: MSI Center is an active participant, not a passive store.** It watches these
> values, so it can also overwrite them — from its own UI, and plausibly on mode changes, AC↔DC
> transitions and resume. The helper re-reads after every write and must not assume its last write
> still stands.
>
> **Consequence for `IsMsiCenterRunning()`:** it must detect the *server*, not the window. Matching
> the UWP process would report "MSI Center is not running" on a machine where everything works, and
> the widget would show a warning the hardware contradicts.

### Registry map

All under `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M`.

| Setting | Subkey | Value | Type | Observed |
|---|---|---|---|---|
| PL1 on AC | `Component\User Scenario` | `ManualPL1AC` | DWORD | watts, 1:1 |
| PL2 on AC | `Component\User Scenario` | `ManualPL2AC` | DWORD | watts, 1:1 |
| PL1 on DC | `Component\User Scenario` | `ManualPL1DC` | DWORD | watts, 1:1 |
| PL2 on DC | `Component\User Scenario` | `ManualPL2DC` | DWORD | watts, 1:1 |
| Fan mode | `Component\User Scenario` | `Fan` | DWORD | `1` = Auto, `3` = Advanced |
| Fan curve | `Component\User Scenario` | `Default_Temp`, `Default_Fan`, `High_Fan` | SZ | see below |
| Charge limit | `Battery` | `BatteryLevel` | **SZ** | `"0"`=100%, `"1"`=80%, `"2"`=60% |
| Controller mode | `OsdEditor` | `ControlModeUserSet` | SZ | `"XInput"` / `"Desktop"` |
| LED brightness | `OsdEditor` | `LightingBrightness` | DWORD | `0` = off, `1` = on |

Other values in `Component\User Scenario`, meaning not yet established: `Intelligent`, `MSI_CH`,
`OverBoostSup` (=1), `OverBoost` (=0), `CurrentMode` (=2), `CurrentShiftType`,
`PowerMode` (=`"AC"`), `AutoSwitchMode` (=`"0"`).

### Performance mode — decoded

`Diagnostics/transcripts-changing-device-modes.zip` captured all three transitions of MSI's
performance-mode selector. Three values move together and agree across every transition, so the
mapping is solid rather than inferred from a single sample:

| MSI Center mode | `Mode` | `ShiftMode` | `GamingEvent` |
|---|---|---|---|
| Endurance | 3 | 3 | 1 |
| **User Scenario** | **4** | **6** | **4** |
| AI Engine | 5 | 2 | 2 |

Verified round-trip: User Scenario → AI Engine → Endurance → User Scenario returns every value to
its starting number.

**Two consequences that matter more than the mapping itself.**

1. **`ManualPL*` did not change during any mode transition.** The mode selector does not overwrite
   the manual power limits — they persist underneath it. But "User Scenario" is evidently the mode
   in which MSI *honours* them; Endurance and AI Engine are MSI driving the limits itself. So
   **setting `ManualPL*` while the device is in AI Engine or Endurance most likely does nothing
   visible**. Unverified, but it is the obvious reading.

   **How the app handles it:** the mode is exposed as its own control above the power sliders, and
   the sliders are disabled in the two automatic modes with a line saying why. Neither forcing
   `Mode = 4` behind a slider nor merely failing after the fact — the precondition is made visible
   and the user changes it deliberately. Writing the mode writes **all three** values
   (`Mode`, `ShiftMode`, `GamingEvent`), because all three moved together in every transition and
   there is no evidence a partial write is noticed. **Still to verify on device: that writing them
   actually switches the mode.** Only reading was confirmed.

2. **Changing mode changes the LED.** `LightingBrightness` went 1 → 0 entering Endurance and
   0 → 1 leaving it. So MSI couples lighting to performance mode, and our LED state can be
   overwritten by something that has nothing to do with lighting. Any LED feature needs to re-read
   after a mode change rather than trusting its last write.

`AIModeM` also moved (5 → 2) entering AI Engine. `CurrentShiftType` moved on every transition
(1 → 2, then 2 → 3) without matching a mode, so it is probably a sequence counter rather than a
mode id — do not treat it as one.

> `ControlModeUserSet` exists in **both** `Component\User Scenario` (empty) and `OsdEditor`
> (populated). The `OsdEditor` one is the live value. Writing the wrong one would look correct in
> a registry diff and do nothing.

### Units and ranges — confirmed at four points

`ManualPL*` are **watts, one-to-one, no scaling**. Four independent captures agree:

| Capture | PL1 | PL2 |
|---|---|---|
| minimum | `0x08` = 8 | `0x0A` = 10 |
| mid | `0x11` = 17 | `0x13` = 19 |
| max TDP | `0x23` = 35 | `0x25` = 37 |
| max TDP + max PL2 | `0x23` = 35 | `0x2D` = 45 |

**This confirms the clamps already in `DeviceCaps` exactly**: PL1 ∈ [8, 35], PL2 ≤ 45, and
`Pl2MinOffset = 2` holds at every point (8→10, 17→19, 35→37). Nothing to change.

AC and DC are **separate values**; MSI Center wrote both identically in every capture, so whether
it ever diverges them is unknown.

### Fan — the EX model is not the model we implemented

```
Default_Temp = "47;50;57;64;71;78;47;50;57;64;71;78;"
Default_Fan  = "70;74;76;78;80;84;70;74;76;78;80;84;"
High_Fan     = "70;74;76;78;80;84;70;74;76;78;80;84;"
```

**Six points, not five.** Twelve values per string, i.e. the six-point curve stated twice —
almost certainly two zones (CPU and GPU), unverified.

This contradicts the ClawTweaks-derived model now in `src/Shared/Fan/FanProfiles.cs` on nearly
every axis, and the explanation is simply that **that model describes the A2VM**:

| | Implemented (from ClawTweaks, A2VM) | Measured (MSI Center, EX) |
|---|---|---|
| Points | 5 | **6** |
| Temp axis | `{44, 54, 64, 74, 82}` | `{47, 50, 57, 64, 71, 78}` |
| Duty curve | `{40, 49, 58, 67, 75}` | `{70, 74, 76, 78, 80, 84}` |
| Duty ceiling | 75 (`DutyCap`) | **84 observed, above our cap** |
| Transport | 8-byte EC table, indices 1..6 | registry string, or `Set_Fan`/`Set_Thermal` |

Whether the two describe the same duty scale is **unknown**. If they do, the EX's factory curve
starts at 70 — comfortably above the duty floor of 58 — which would mean Quiet Idle is genuinely
quieter and the earlier worry was unfounded. If they are different scales, none of the ClawTweaks
duty numbers transfer at all. **Do not write a fan table until this is resolved.**

### Answered by omission

Switching RGB profile 1 → 2 produced **no change in this registry hive**. LED effect and colour
therefore live somewhere else — `MSI_Center_M_Server_MysticLight` has its own store, or writes the
device directly. Only brightness on/off is here, so **the registry is not a usable LED path**.

---

## Relationship to MSI Center M

**Decided 2026-08-07: MSI Center M stays installed and running. This app is a simplified
front-end that sits alongside it, not a replacement for it.**

That is the same arrangement the reference project uses, and the reference project works on this
device — which is the whole argument for it. The name `msi-mcenter-lite` now means "a lighter way
to reach the settings M Center owns", not "a lightweight M Center".

What follows from it:

| Feature | Path | Needs MSI Center running? |
|---|---|---|
| TDP (PL1/PL2) | MSI Center's registry model; its service applies to the EC | **Yes** — hard dependency |
| Fan presets | ACPI-WMI to the EC | No, but contention is possible (see below) |
| Charge limit | ACPI-WMI / ACPI | No |
| RGB LED | Vendor HID | No, but MSI may hold the device |
| Desktop/gamepad mode | Vendor HID firmware command | No |
| CPU boost, OS power mode | Windows APIs | No |
| Intel GPU | IGCL / Intel driver | No |

The reference project gates only **controller emulation and gyro** on MSI Center being active —
both outside our scope — and explicitly ungated TDP once the registry mirror existed. Fan, LED and
charge limit are not gated there at all, which suggests they coexist, though "not gated" is weaker
evidence than "verified to coexist".

**The contention that remains.** MSI Center owns the same hardware and will not know about us:

- Its own thermal/fan profiles may overwrite a fan table we wrote. Re-read state periodically
  rather than assuming our last write still holds.
- It may hold the vendor HID collection, which is why the LED path opens **non-exclusive**. If
  that fails while MSI Center runs, LED may simply be unavailable in this configuration.
- Changing a setting in MSI Center's own UI will diverge from what our widget shows.

**The new dependency risk.** TDP now rides on an undocumented, MSI-owned registry schema. An MSI
Center update can change it and break power limits with no error — the write still succeeds. Record
the MSI Center version every fact here was established against, and treat a silent no-op as the
expected failure mode.

---

## Desk research: ClawTweaks public repo (2026-08-07)

Read from `github.com/enterTheVoidCode/ClawTweaks` at `e86e56c` (branch `release/v0.3.98.0`,
2026-08-06 — the local clone is exactly current with it). **The helper source is still absent from
every ref**, including `origin/master`, which carries only the older AMD/Legion/GPD ancestor. The
one submodule is AMD's ADLX. `ClawTweaksSetup/Core/DeviceDetect.cs:11` cites
`XboxGamingBarHelper/Devices/MSIClaw/MSIClawModels.cs::MSIClawModelCatalog.Resolve` — a path that
exists in no ref, confirming the hardware layer only ever shipped as a compiled binary.

Everything here is **a fact about the device recorded from someone else's observations**, not a
measurement of *this* device, and not copied code. Two of them are strategic rather than technical
and are called out under [Findings that change the plan](#findings-that-change-the-plan).

### Confirms what is already implemented — no change needed

| Fact | Source in the reference repo |
|---|---|
| Fan table is 8 bytes `{0, 0, D0, D1, D2, D3, D4, D4}` | `MsiFanControl.cs:1136` |
| Only indices 1..6 are written; 0 and 7 are EC state | `MsiFanControl.cs:1141-1146` |
| The EX ships index 7 = 94 | same |
| Duty is the raw EC byte, no ×1.5 scaling | `MsiFanControl.cs:31-33` |
| MSI's own presets cap at duty 75 | `MsiFanControl.cs:53` |
| EX firmware idle duty floor is 58 (~3570 RPM); A2VM is 40 | `MsiFanControl.cs:64-80`, "verified from EC-tach logs 2026-07-20" |
| Preset duties: Default/Cooling `{40,49,58,67,75}`, Quiet Idle `{20,30,45,67,75}` | `MsiFanControl.cs:36-38` |
| Cooling differs from Default **only** by a −10 °C axis shift | `MsiTempsCooling = {34,44,54,64,72}` vs `{44,54,64,74,82}` |
| Temp breakpoints bounded 10..99, strictly increasing | `MsiFanControl.cs:47-48`, `ClampMsiTemp` |
| EX caps: PL1 ≤ 35 W, PL2 ≤ 45 W, PL2 ≥ PL1 + 2 | `DeviceInfo.cs:100-113`, `GamingWidget.xaml.cs:1663` |
| EX exposes the CPU **Boost toggle only** (no advanced CPU controls) | `DeviceInfo.cs:92-96` — "not reliably persistent" on Panther Lake |
| Full-speed override is EC block 152 (0x98), enable bit `0x80` | `MsiFanControl.cs:1417-1421` — probe presets are `0x80 \| duty` |
| EX duty→RPM tach anchors | `MsiRpmDutyEx` / `MsiRpmValEx`, `MsiFanControl.cs:87-88` |

Our `src/Shared/Fan/FanProfiles.cs` and `src/Hardware/Fake/FakeHardware.cs` already match all of
the above byte for byte. That is a genuine cross-check, not a coincidence — but it is still a
cross-check against *the A2VM*, and the fan table has never been read on an EX by us.

### New facts — G4 and G5 are largely pre-answered

Recovered from `docs/CONTROLLER_FEATURE_PIPELINE.md` and `Doku/PLAN_Standard_Controller_Mode.md`,
deleted at commit `78662ab` ("Remove internal RE/plan/diagnostics docs from the repo", 2026-07-14)
and still present in history.

**Vendor HID interfaces under `VID_0DB0`:**

| Interface | PID | UsagePage / Usage |
|---|---|---|
| XInput | `0x1901` | `0xFFA0` / `0x0001` |
| DirectInput (normal operation) | `0x1902` | `0xFFF0` / `0x0040` |

> **Search both usage pages.** The reference notes say searching only `0xFFA0` finds the wrong
> interface. Our plan said "usage page ≥ 0xFF00", which covers both — but the probe must not stop
> at the first match.

**Report IDs on the vendor collection:**

| Report ID | Purpose | Layout |
|---|---|---|
| `0x0F` | Mode switch / M1-M2 / **LED** | `0F 00 00 3C …`, **64 bytes** |
| `0x05` | Rumble | `05 01 00 00 <small> <large> 00…` (small @4, large @5) |

**Firmware command channel** (vendor HID): opcodes `0x21` write · `0x04` read · `0x22` SyncROM ·
`0x24` SwitchMode. **SwitchMode values: `0x04` = MODE_DESKTOP (mouse), `0x02` = MODE_DINPUT.**
That pair is the entirety of feature 5's write path — G5 reduces to confirming the read-back path
and the physical-button interaction.

A known-good read probe frame appears in the widget's debug panel: `0F 00 00 3C 26`
(`MsiFanControl.cs:1493`).

**LED wire model** (`Shared/Led/LedCompositeSpec.cs`, `Shared/Enums/LedMainMode.cs`) — 3 zones:
`Right = 0`, `Left = 1`, `Buttons = 2`. Modes `Static/Breathing/ColorCycle/Wave` = 0..3 (4 =
Battery, a software SoC tint, out of our scope). Speed index 0..2, brightness 0..100 (0 = off).
This is the *helper's* model, not the firmware report layout — the byte layout of report `0x0F`
is still unknown and still needs G4.

**Charge limit** accepts **20..100**, not the 60..100 our widget offers
(`GamingWidget.MsiClawSettings.cs:558`). Keeping 60 as the floor is a deliberate lite choice, not
a hardware limit — worth a comment rather than a change. Wire shape is `"enabled:percent"` out and
`"enabled:percent:readok"` back, which is the same read-back-and-confirm discipline we use.

**Intel IGCL value ranges** (`Shared/Enums/Function.cs:474-480, 602-604`), previously unspecified
in our plan:

| Function | Range |
|---|---|
| Adaptive sharpness | 0 = off, 1..100 intensity |
| Colour saturation | 0..100, 50 neutral |
| Colour hue | −180..180, 0 neutral |
| Display contrast | 0..100, 50 neutral |
| Display brightness | 0..100, 50 neutral |
| Display gamma | ×100, 30..280, 100 = 1.0 |
| Low latency | 0 = Off, 1 = On, 2 = On+Boost (`CTL_3D_FEATURE_LOW_LATENCY`) |
| Frame sync | 0 = App default, 1 = VSync off, 2 = VSync on, 3 = Smooth Sync, 4 = Speed Sync |

**PawnIO is a sensor driver here**, not a TDP path — `TdpMethod.PawnIO` is RyzenSMU (AMD), and the
README lists PawnIO as "required for extended sensors (fan speed, GPU power draw)". Dropping live
metrics from our scope removes the only reason we would have wanted it, so the no-driver
constraint costs us nothing extra.

### Findings that change the plan

**1. ClawTweaks requires MSI Center M to be installed *and running*.** The README states it twice:
*"Before installing CTW - MSI Center M must be installed and running on your device"* and *"Center
M needed as a base"*. Its TDP path is explicitly a mirror into MSI Center M's own model:

> *"The helper mirrors PL1/PL2 into MSI Center M's own model (`HKLM\...\User Scenario\ManualPL*`),
> which MSI watches and applies to the EC itself — so setting TDP works AND stays MSI-conform
> while MSI Center M runs."* — `GamingWidget.MsiCenterGating.cs:67-71`

**Resolved 2026-08-07 by accepting the dependency** — see
[Relationship to MSI Center M](#relationship-to-msi-center-m). Keeping MSI Center installed and
running turns this from the project's largest risk into its cheapest path: the registry mirror
needs no kernel driver, no WMI reverse-engineering, and no decompilation, and it is the route the
reference project already proves works on this device.

For the record, the same comment carries the road not taken — *"The old lock only existed because
the direct EC/WMI write was refused **while MSI held the ACPI WMI**"* — implying a direct WMI path
exists when MSI Center is not holding it. That is what a future standalone mode would need.

`TdpMethod` (`Shared/Enums/TdpMethod.cs`) has exactly three values — `ManufacturerWMI` (Legion),
`PawnIO` (RyzenSMU, AMD), `IntelKxExe` (MCHBAR via ring-0, and its comment names **Lunar Lake /
A2VM only**). The registry mirror is not one of them; it is an always-on side-channel. So nothing
in the enum tells us what the EX uses, and the ring-0 path is not documented as covering it —
another reason the mirror is the sensible target.

**2. Fan control may be disabled on the Claw 8 EX upstream.** Three places say so, and the README
scopes the feature to Lunar Lake:

> *"Custom fan curve written directly to the EC (Lunar Lake)."* — `README.md:209`
> *"off on the Claw 8 EX for now"* — `Function.cs:583`, `GamingWidget.xaml.cs:1658`
> *"the Claw 8 EX, where MSI's own custom curves still have issues"* — `MsiFanControl.cs:162`

**But those comments are probably stale.** All three were written in `1d2fd4e` (2026-07-14). Six
days later `425fabc` (2026-07-20) added an EX-specific duty floor and EX tach anchors "verified
from EC-tach logs" — work nobody does for an editor that stays hidden on that model. The
capability flag itself (`DeviceSupportsFanControl`) is set by the absent `MSIClawModels.cs`, so
the public repo genuinely cannot settle this.

**It is settled in five seconds on the device: open ClawTweaks on the Claw and look for the Fan
tab.** Visible → fan control is enabled on the EX and M3 proceeds as planned. Absent → the
reference project's author chose not to ship EC fan writes on this model, which is a strong signal
about M3's risk and worth understanding before writing a single byte.

---

## Gate G1 — TDP

**No longer a hard gate** (scope decision, 2026-08-07: MSI Center M stays installed and running —
see [Relationship to MSI Center M](#relationship-to-msi-center-m)). The registry mirror is a known,
documented, driver-free path, so the question is no longer *whether* TDP is reachable but *what the
key layout is*. That is a `reg export` diff, not a decompilation.

The direct ACPI-WMI path is explicitly **not** being pursued. It only matters for running without
MSI Center M, which is no longer a goal.

- [ ] Registry key path identified
- [ ] Value names for PL1 and PL2 identified
- [ ] Value type identified (`REG_DWORD` vs `REG_SZ`)
- [ ] **Units confirmed at two different values** (17 W vs 25 W) — watts or milliwatts
- [ ] Latency measured: how long after the write does the EC actually change?
- [ ] Confirmed the value survives, or is overwritten, when MSI Center's own UI is opened

Method: `reg export HKLM\SOFTWARE\MSI` before and after a known change made **in MSI Center M
itself**, diff the two. Procmon filtered to `RegSetValue` + `User Scenario` names which process
writes, confirming the service is the applier. No decompilation needed for any of this.

- [ ] Decompiled helper's TDP backend identified
- [ ] Direct ACPI-WMI method found (class, method, parameter encoding)
- [ ] Registry mirror found (exact key, value names, type)
- [ ] **Units confirmed at two different values** (17 W vs 25 W)
- [ ] Behaviour with the MSI Center service *stopped* established

| Fact | Value | Source |
|---|---|---|
| Registry key | | expected under `HKLM\SOFTWARE\MSI\...\User Scenario` |
| Value names | | expected `ManualPL1` / `ManualPL2` or similar |
| Value type | | `REG_DWORD` / `REG_SZ` |
| Units | | watts / milliwatts / raw |
| Apply latency | | how long until the EC reflects it |
| Overwritten by MSI Center's own UI? | | decides whether we re-assert periodically |
| PL1 range accepted | | plan assumes 8–35 W |
| PL2 range accepted | | plan assumes ≥ PL1+2, ≤ 45 W |

**Result: GREEN, and the mechanism is confirmed working.**
`HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario`, values
`ManualPL1AC` / `ManualPL2AC` / `ManualPL1DC` / `ManualPL2DC`, REG_DWORD, **watts 1:1**, confirmed
at four points. Ranges and `Pl2MinOffset = 2` match `DeviceCaps` exactly.

**A registry write alone applies.** Measured 2026-08-07: PL1 driven 8 W -> 25 W by registry write
only, under sustained load, and the sustained clock followed. MSI Center's UI updated to match,
which is the mechanism made visible - it watches these values and pushes them to the EC.

A second, MSI-Center-independent path exists via `MSI_ACPI.Set_Power`, unexercised.

Remaining before M2: confirm it still applies with the MSI Center **window closed** (the run that
settled this had the UI open), and find out what re-asserts MSI's own values over ours.

---

## Gate G2 — Fan presets

Only three fixed profiles are exposed; no custom curve. The values below come from the reference
project's widget code and are **carried over as hypotheses** — every one must be confirmed on this
device before a single write.

| Assumption | Status | Notes |
|---|---|---|
| Table is 8 bytes, `{0, 0, D0, D1, D2, D3, D4, D4}` | corroborated, not measured | reference repo, A2VM |
| Only indices 1..6 may be written | corroborated, not measured | index 0 and 7 are EC state; EX ships index 7 = 94 |
| Duty is the raw EC byte, no scaling | corroborated, not measured | reference repo is explicit: no ×1.5 |
| MSI's own cap is duty 75 | corroborated, not measured | this app never exceeds it |
| Firmware idle duty floor is 58 | corroborated, **EX-specific** | from the reference author's EC-tach logs, 2026-07-20 |
| Full-speed override at block 152 (0x98), bit 7 | corroborated, not measured | enable bit is `0x80`; probe presets are `0x80 \| duty` |

> "Corroborated" means someone else measured it and wrote it down — mostly on an **A2VM**, not this
> device. It lowers the odds of a wrong byte; it does not license skipping the read-back.

**Answered 2026-08-07: fan control works on this EX.** ClawTweaks shows the Fan tab, presets can be
selected, and they apply. The "off on the Claw 8 EX for now" comments in the reference repo are
stale — as suspected, they predate the commit that added EX tach data. M3 proceeds as planned.

**Presets: exactly the three ClawTweaks ships.** Its dropdown also carries "Custom" and
"EC Sport default (debug)"; neither is in scope.

| # | Our label | Temps | Duties |
|---|---|---|---|
| 0 | MSI Default | device axis | device factory curve |
| 1 | Quiet Idle | device axis | `{20, 30, 45, 67, 75}` before the floor |
| 2 | Cooling · Early Ramp | device axis −10 °C | `{40, 49, 58, 67, 75}` before the floor |

**The duty floor is part of preset resolution, not a UI nicety.** The reference implementation runs
its equivalent of `EnforceDutyFloor` after loading every preset: raise everything below the model
floor (58 here) to it, then re-separate collided points so the curve still rises. On this device
Quiet Idle therefore reaches the EC as roughly `{58, 59, 60, 67, 75}`, not what the table says.

That changes the old "Quiet Idle may be pointless on the EX" note into a sharper question. Both
Default and Quiet Idle get floored, so whether they differ depends **entirely on the EX's own
factory curve**, which we have not read. If the factory curve sits well above 58 — plausible, since
the EX ships table index 7 = 94 — Quiet Idle is genuinely quieter. If it sits at 58, the two
presets converge and Quiet Idle should be relabelled or dropped.

- [x] ClawTweaks Fan tab visible and presets apply on this device
- [ ] **Factory duty curve read from the EC** — decides whether Quiet Idle is distinguishable
- [ ] Fan WMI class and method identified
- [ ] Read-back verified to reflect a write
- [ ] Factory table captured (needed for uninstall restore)
- [ ] Temperature axis (`Set_Thermal`) format identified
- [ ] Tachometer/RPM read identified
- [ ] Behaviour with Intel IPF running vs stopped established

| Fact | Value | Source |
|---|---|---|
| Fan WMI class | | |
| Set method | | |
| Get method | | |
| Factory duty table | | capture before any write |
| Factory temperature axis | | |

**Preset measurements** — record real RPM under sustained load. If Quiet Idle proves
indistinguishable from Default once both are floored, say so here and change the UI rather than
shipping a preset that does nothing.

| Preset | Resolved duties after floor | Idle RPM | Load RPM | Distinguishable? |
|---|---|---|---|---|
| MSI Default | | | | — |
| Quiet Idle | | | | |
| Cooling · Early Ramp | | | | |

**Result: AMBER — the transport is found, the model we implemented is wrong.**
MSI's own curve on this device is **six** points (`Default_Temp` / `Default_Fan` / `High_Fan` in
`Component\User Scenario`), not the five-point 8-byte EC table taken from ClawTweaks, and its
duties reach 84 against our cap of 75. `MSI_ACPI` also exposes `Get_Fan`/`Set_Fan` and
`Get_Thermal`/`Set_Thermal` directly. **`src/Shared/Fan/FanProfiles.cs` describes the A2VM, not
this device** — do not write a fan table until the duty scales are reconciled.

---

## Gate G3 — Battery charge limit

There is no fallback for this one. Without a driver-free method it is cut, not worked around.

- [ ] WMI/ACPI method identified
- [ ] Read-back works
- [ ] Setting survives a reboot (proves it persists in the EC)
- [ ] Accepted percentage range established

| Fact | Value | Source |
|---|---|---|
| WMI class / method | | |
| Parameter encoding | | |
| Accepted range | | see result — it is not a range |

**Result: GREEN, but it is not a percentage.**
`HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery`, value `BatteryLevel`, **REG_SZ**, with
exactly three states: `"0"` = 100%, `"1"` = 80%, `"2"` = 60%. Confirmed by three transitions
(100→80, 80→60, 60→100). `MSI_ACPI.Set_MasterBattery` is the direct alternative and may accept a
wider range, unverified. Our 60–100 slider with 5% steps offers values the device has no way to
represent.

---

## Gate G4 — RGB LED

Partly pre-answered by desk research: LED rides on **report ID `0x0F`, 64 bytes**, on the vendor
collection, shared with mode-switch and the M1/M2 buttons. The interfaces are PID `0x1901`
(usage page `0xFFA0`/usage `0x0001`) and PID `0x1902` (`0xFFF0`/`0x0040`) — **enumerate both**.
The byte layout inside report `0x0F` is still unknown and is the actual work here.

- [ ] Vendor HID collection identified (usage page ≥ 0xFF00 — expect `0xFFA0` *and* `0xFFF0`)
- [ ] Feature report length recorded
- [ ] Report byte layout decoded
- [ ] Zone ids mapped to physical zones
- [ ] Mode ids mapped
- [ ] Behaviour while MSI Center holds the device established

| Fact | Value | Source |
|---|---|---|
| Device path | | from `Probe --hid-list` |
| PID | | |
| Usage page / usage | | |
| Feature report length | | |
| Report layout | | byte-by-byte |
| Zone ids | | |
| Mode ids | | |

**Result: AMBER.** Brightness on/off is at `OsdEditor\LightingBrightness` (DWORD, `0`/`1` only).
**Effect and colour are not in this registry hive at all** — switching RGB profile produced no
delta, so `MSI_Center_M_Server_MysticLight` keeps them elsewhere or writes the device directly.
The vendor HID collection is present as `HID\VID_0DB0&PID_1901&MI_01`
("HID-compliant vendor-defined device"), so the HID path from the desk research remains the plan.
Report `0x0F` (64 bytes) is still unverified on this device.

---

## Gate G5 — Firmware desktop-mouse mode

The firmware variant, not software cursor injection — that is the whole point, because a real HID
mouse keeps working on the UAC secure desktop.

- [ ] Write report identified
- [ ] **Read-back path identified** (the physical MSI button changes this too, so the helper
      cannot assume it owns the state)
- [ ] Verified working on the UAC secure desktop

Desk research gives the write path outright: the vendor HID command channel uses opcode `0x24`
(SwitchMode) with **`0x04` = MODE_DESKTOP (mouse)** and **`0x02` = MODE_DINPUT**. The other
opcodes on that channel are `0x21` write, `0x04` read, `0x22` SyncROM. What remains is the
read-back path and the physical-button interaction.

| Fact | Value | Source |
|---|---|---|
| Report to enable | opcode `0x24`, mode `0x04` — **verify on device** | reference RE notes |
| Report to disable | opcode `0x24`, mode `0x02` — **verify on device** | reference RE notes |
| How to read current mode | | opcode `0x04` is the read; framing unknown |
| Physical button behaviour | | the MSI button changes mode behind our back |

**Result: GREEN via registry.** `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\OsdEditor`,
value `ControlModeUserSet`, REG_SZ, `"XInput"` ↔ `"Desktop"`, confirmed in both directions.
Note the same value name also exists (empty) under `Component\User Scenario` — writing that one
would diff convincingly and do nothing.

The firmware HID route (`0x24` SwitchMode, `0x04` desktop / `0x02` DInput) remains the fallback,
and is still the only route that works when MSI Center is not running.

---

## Gate G6 — Intel IGCL

- [ ] `ControlLib.dll` present
- [ ] `ctlInit` succeeds
- [ ] Adapter enumerated (`pci_vendor_id == 0x8086`)
- [ ] Per-feature support established via `ctlGetSupported*`

Value ranges for every one of these are recorded in the desk-research section, so the interop can
be written before the device is available — only `ctlGetSupported*` needs the hardware.

| Feature | Supported | Notes |
|---|---|---|
| Endurance Gaming (FPS tier) | | **per-application, not global** — affects the UI wording |
| Low latency | | 0 = Off, 1 = On, 2 = On+Boost |
| Frame sync | | 0 = App default, 1 = VSync off, 2 = VSync on, 3 = Smooth Sync, 4 = Speed Sync |
| Adaptive sharpness | | 0 = off, 1..100 |
| Colour (saturation / contrast / gamma) | | 0..100 (50 neutral) · 0..100 (50) · ×100, 30..280 (100 = 1.0) |

**Result: GREEN on the prerequisite.** `C:\Windows\System32\ControlLib.dll` is present at
v1.2.288.0, on an Intel Arc B390 with driver `32.0.101.8801`. `ctlInit` and per-feature
`ctlGetSupported*` still have to be exercised, but nothing blocks writing the interop.

---

## Intel thermal stack

Intel's IPF/DTT owns a fan participant above the EC and can hold the fan at maximum regardless of
any table written. The escape hatch ships **before** any EC write.

| Fact | Value |
|---|---|
| Services present | **`ipfsvc` and `dptftcs`**, both Running/Automatic. No `esifsvc`. (`dptftcs` was absent from the first capture and present in the second, so it starts on demand — do not assume either state.) |
| Fan participant device id | **`ACPI\INTC10D6\TFN1`** — measured. Not `INTC106A`, which was the carried-over guess. |
| Effect of stopping them | |
| Does the curve take effect with IPF running? | |

The panic action therefore has a smaller job than planned: one service and one PnP device, not
three services. `Diagnostics/Get-DeviceReport.ps1` still filters on the old `INTC106A` string and
only found this by also matching `TFN1` — worth correcting before it misses something.

---

## Open questions

Ordered by how much they block. The first one decides the architecture.

1. ~~**Does writing the registry alone apply anything?**~~ **Answered: yes.** Measured 2026-08-07
   with `Diagnostics/Test-TdpRegistryApply.ps1` — see
   [Two independent control surfaces](#two-independent-control-surfaces).

   ~~**Does it still apply with the MSI Center window closed?**~~ **Answered: yes.** The
   background server `MSI_Center_M_Server_UserScenario` is the applier, so no window is needed.

1. **When does MSI Center overwrite our values?** It watches the registry, so it presumably also
   re-asserts its own view on some events — mode change, AC↔DC, resume from sleep, its UI opening.
   Each one that overwrites us is a case the helper has to detect and re-apply after.

2. **Are the ClawTweaks duty numbers and MSI's `Default_Fan` on the same scale?** ClawTweaks says
   raw EC byte 0–150 with 75 = half fan; MSI ships `{70,74,76,78,80,84}` as a factory curve. If
   the same scale, the EX idles around 70 and every duty-floor conclusion in the desk-research
   section needs revisiting. If not, none of the ClawTweaks duty values transfer.
   **Test:** `MSI_ACPI.Get_Fan` and compare the bytes it returns against the registry string.

3. **What do the six values mean?** Six points doubled to twelve — CPU and GPU zones is the
   obvious reading, but `Default_Temp` and `Default_Fan` being identical in both halves means the
   capture cannot distinguish "two zones" from "one curve written twice".

4. ~~**What are `Mode`, `ShiftMode`, `CurrentShiftType`?**~~ **Answered** — see
   [Performance mode](#performance-mode--decoded). `Mode`/`ShiftMode`/`GamingEvent` select
   Endurance / User Scenario / AI Engine, and mode changes do **not** overwrite `ManualPL*`.
   What remains: confirming that `ManualPL*` is only *honoured* in User Scenario (Mode 4), which
   `Test-TdpRegistryApply.ps1` will show as a side effect.

5. **Where does MysticLight keep LED effect and colour?** Not in this hive. Either its own store
   or straight to the device.

6. **Does `MSI_ACPI` work with MSI Center stopped?** If yes, the standalone mode ruled out earlier
   becomes available again, and the MSI Center dependency becomes a choice rather than a
   constraint.

7. **What is `MSI Foundation Service` / `MSIAPService`?** Suspected to be the SDK layer the
   per-feature servers call. Worth identifying, because it may be the documented-ish seam.
