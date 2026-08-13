# Hardware notes — MSI Claw 8 EX AI+

> **Status: Phase 0 ran on the device on 2026-08-07.** `Diagnostics/device-report.txt` and
> `Diagnostics/watch-msicenter-transcripts.zip` are the raw evidence, captured on a **fresh Windows
> install with only MSI Center M present** — no ClawTweaks, so every change observed was made by
> MSI's own software. **G1 and G5 are green AND standalone of MSI Center M. G6 is green on its
> prerequisite only. G2, G3 and G4 are amber and are the next work — see
> [What's next](#whats-next--fan-charge-limit-and-rgb).**
>
> Measured facts live in [Measured on device](#measured-on-device-2026-08-07) and **supersede**
> the [desk research](#desk-research-clawtweaks-public-repo-2026-08-07) below, which was taken from
> the ClawTweaks source and describes a **different model** (the Lunar Lake A2VM) wherever the two
> disagree. Where they disagree, the disagreement is called out.
>
> **Update 2026-08-08:** an on-device A/B test overturned part of G3 — the registry write that
> looked sufficient for the charge limit does not reliably apply. G3 moved from green to amber; see
> its section below.
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

## What's next — fan, charge limit and RGB

**Added 2026-08-12.** Three features were removed on 2026-08-08 because MSI Center M did them
better. That reasoning expired: MSI Center M is being **uninstalled** once this widget is complete,
so "set it in MSI Center" stops being an answer. All three come back.

**Every one of them now carries the same gating question, and it is the first thing to settle in
each case: can it be driven with MSI Center M absent?** Not stopped — absent. Both features shipped
so far were built by answering exactly that, and in both cases the answer changed the design:

- **G1 / TDP** — the registry model needed MSI Center M's service to *apply* it. `MSI_ACPI` did not.
- **G5 / controller mode** — the registry value turned out to be a mirror MSI Center M maintained
  by watching the vendor HID channel. Going to the channel directly removed the dependency and the
  lag with it.

The lesson both times: **a registry value that round-trips is not evidence of a control surface.**
It may be a shadow of one. Prove the write reaches hardware with MSI Center M's stack stopped, and
prove the read reflects hardware rather than MSI Center M's opinion of it.

### The three, in the order they should probably be done

| Gate | Feature | Standalone path | Confidence | Biggest unknown |
|---|---|---|---|---|
| ~~**G3**~~ | ~~Battery charge limit~~ | `MSI_ACPI.Get_AP` / `Set_AP` | **DONE 2026-08-12** | — |
| ~~**G4**~~ | ~~RGB LED~~ | vendor HID report `0x0F` | **DONE 2026-08-12** | — |
| **G2** | Fan control | `MSI_ACPI.Get_Fan` / `Set_Fan` | **Medium–high** | does `Set_Fan` take, and does MSI Center overwrite it |

**G2's confidence was raised from Low on 2026-08-12**, after the read described in
[Gate G2](#gate-g2--fan-control) settled both of the unknowns it had been stuck on since August.
The remaining risk is no longer *what to write* — it is whether the write takes and holds.

**G3 is done.** It was the obvious first pick and it went the way the confidence suggested: the
read was already measured, the write turned out to be plain read-modify-write, and it needed no new
transport. **The firmware range is 20–100**; the widget deliberately offers only 50–100 in steps of
10, which is a product choice and must not be recorded as a hardware limit — that is precisely the
error the retired version's 60 floor caused.

**G4 got substantially easier and the notes below understate it.** The G4 section still says report
`0x0F` is "unverified on this device, and that work has not started" — that is now out of date. G5
proved `0x0F` is the vendor command channel, established its framing, and shipped a class that
speaks it (`MsiVendorHidChannel`). LED work no longer needs to find or open a channel, only to
learn the opcode and payload for lighting.

There is also a concrete lead sitting in the G5 capture: with MSI Center M restarting, the device
emitted a long multi-frame `0x05` dump whose payload is full of plausible **RGB triples** —
`FF 00 00`, `FF A0 00`, `C8 C8 FF`, `00 FF 00` — alongside what look like per-zone records. That is
MSI Center M reading the current lighting configuration out of the controller. **Capturing that
dump while changing one colour in MSI Center M, and diffing, is very likely the whole of G4.** The
existing `--hid-watch` already records it.

Known LED model from desk research, still unverified on this device: 3 zones (`Right = 0`,
`Left = 1`, `Buttons = 2`), modes `Static/Breathing/ColorCycle/Wave` = 0..3, speed 0..2, brightness
0..100. Only on/off was ever confirmed, via `OsdEditor\LightingBrightness` — a registry value, and
therefore suspect for the reasons above.

**G2 is the hardest and should be last.** It is the only one of the three that would write the
embedded controller, it is blocked on a genuine contradiction rather than missing work — MSI's own
curve on this device is six points while the model implemented here is a five-point 8-byte table
from a *different machine* (the Lunar Lake A2VM) — and the duty scales have never been reconciled.
`MSI_ACPI` exposes `Get_Fan`/`Set_Fan` and `Get_Thermal`/`Set_Thermal`, which are the standalone
candidates and are unexercised. Start by reading `Get_Fan` and diffing it against MSI Center's
six-point curve; that answers the layout question without writing anything.

Also unresolved for G2: Intel's thermal stack (`ipfsvc`, participant `ACPI\INTC10D6\TFN1`) is an
independent fan actor on this device, so "our table is correct" and "the fan does what we asked"
are two different claims.

### Housekeeping that should happen alongside

- **`RegistryTdpProvider` and `RegistryHwMouseProvider` are both inert** while their firmware paths
  work, and both are provably the weaker option. Delete them once MSI Center M is actually
  uninstalled and both firmware paths are confirmed on a machine without it. That deletion is also
  what finally removes `PerfMode`, `TdpBackendKind.RegistryMirror` and `IsMsiCenterRunning`.
- **The package version is bumped by hand** and nothing catches a missed bump. Both documented
  MSBuild overrides are proven not to work with this template. Worth solving *before* three
  features' worth of install cycles, not after.
- **`MSI_ACPI` surviving an actual uninstall is still unverified.** Everything so far was proven
  with MSI Center M's stack *stopped*, which is not the same thing. This is the single assumption
  the whole plan rests on, and it stays unproven until the uninstall happens.

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
its own UI ever diverges them is unknown.

### The two limits are independent, bound by one rule

| | AC | On battery |
|---|---|---|
| PL1 range | 8 – **35 W** | 8 – **25 W** |
| PL2 range | 10 – **45 W** | 10 – **30 W** |
| The only rule between them | **PL2 ≥ PL1 + 2** | same |

Each limit moves on its own. Any gap of 2 W or more is legal and is **kept** — lowering PL1 widens
the gap rather than dragging PL2 down behind it. The rule is enforced only when a move would
otherwise break it, and always by moving the *other* limit out of the way rather than by refusing
the one the user asked for:

- **PL1 rising into PL2 pushes PL2 up** to `PL1 + 2`. PL1 is not blocked at `PL2 − 2`. Blocking
  would satisfy the same rule, but it makes the ordinary case — both limits at the bottom, raise
  the sustained limit — impossible without first walking the other slider up a watt at a time.
- **PL2 descending into PL1 pulls PL1 down** to `PL2 − 2`. The mirror of the above.

> **Corrected 2026-08-09.** This was previously recorded as a *rigid coupling* — move either
> slider and the other tracks it exactly 2 W away, with a "knee" at 35/37 past which only PL2
> continues. That was an **inference from the four captured pairs above**, every one of which
> happens to sit at the minimum gap, and it was wrong. Watching MSI Center M directly showed the
> weaker constraint. The four captures are still valid evidence for the units and the ranges; they
> simply never distinguished "the limits are welded together" from "the limits have a floor
> between them". Worth remembering as a general caution about this whole document: several entries
> here are single-shape inferences from a handful of snapshots.

`DeviceCaps.ConstrainFromPl1` / `ConstrainFromPl2` implement the two bullets, and the helper
re-applies the same invariant through `ClampPowerLimits` on every write, since the widget's copy
is a UX affordance and the helper is the boundary that actually has to hold.

**AC and DC no longer diverge (changed 2026-08-11).** This app used to write a lower ceiling to the
DC pair — 25 W PL1 / 30 W PL2 — on the theory that an 8-inch handheld running 35 W unplugged
empties itself fast. Confirmed on device that this was never a firmware limit: MSI Center's own UI
offers the same PL1/PL2 range on battery as on AC, and `WmiTdpProvider` (the preferred TDP
backend, see the "Confirmed 2026-08-11" section above) writes a single EC register with no AC/DC
distinction at all. `RegistryTdpProvider`, now only a fallback, writes the same value to both the
AC and DC registry pairs rather than deriving a lower one — there is nothing left to derive.
`DeviceCaps.MaxPl1Dc`/`MaxPl2Dc` and `ClampPowerLimitsForBattery` were removed with it.

### Fan — the EX model is not the model we implemented

> **RESOLVED 2026-08-12 — read this section as history.** Everything below is a correct reading of
> the *registry*, and the contradiction it describes is real but was never the whole picture. The
> ACPI read in [Gate G2](#gate-g2--fan-control) shows the two models agree once the firmware table
> is read directly: the six registry points are bytes 2–7 of an eight-byte table, and ClawTweaks'
> `58` floor and `94` ceiling are bytes 1 and 8. The registry carries only the part MSI's UI edits.

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

**Result: GREEN, and verified end to end through the helper (2026-08-07).**
`Diagnostics/Test-Helper.ps1 -Tdp 25` over the named pipe: the helper wrote MSI's model, the
values read back correctly, and `Test-TdpRegistryApply.ps1` independently confirmed the registry
still moves the sustained clock. That closes the whole chain — widget protocol → helper →
registry → MSI Center service → EC — with only the UWP layer untested.


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

## Gate G2 — Fan control

> **REOPENED 2026-08-12, and the blocking question is answered.** The gate was removed on
> 2026-08-08 because the six-point curve MSI ships could not be reconciled with the five-point
> model the desk research described, and the duty scale was never shown to match. **Both are now
> settled by direct measurement** — see [The fan table, measured](#the-fan-table-measured-2026-08-12)
> immediately below, which supersedes the historical material after it wherever they disagree.
>
> The approach was the one this file predicted: snapshot across MSI Center M's own fan settings and
> diff. `Diagnostics/Watch-Fan.ps1` does it for both surfaces at once.

### The fan table, measured (2026-08-12)

Captured with `Diagnostics/Watch-Fan.ps1` across three MSI Center M states — Auto, Advanced with
every point dragged to minimum, and Advanced with every point dragged to maximum. Raw snapshots are
`Diagnostics/fan-snapshot-{auto,custom-low,custom-high}.json`.

**Two fans, independently addressed.** `MSI_ACPI.Get_Fan` sub-function **1** and **2** each return
one fan's table. Sub-function **0** returns live tachometers for both, at bytes 2 and 4.

| Byte | Meaning | Auto (factory) | custom-low | custom-high |
|---|---|---|---|---|
| 0 | WMI status | 1 | 1 | 1 |
| 1 | idle duty, below the first breakpoint | **58** | 0 | 100 |
| 2–7 | duty at 47, 50, 57, 64, 71, 78 °C | **70 74 76 78 80 84** | 0 ×6 | 100 ×6 |
| 8 | ceiling — EC state, never written by MSI | **94** | 94 | 94 |

**Duty is a percentage, 0–100.** MSI Center M's own slider at maximum wrote exactly `100` into
every entry and at minimum exactly `0`. This **refutes** two assumptions carried from the desk
research and never measured here: that MSI caps duty at 75, and that duty might be a raw EC byte on
a 0–150 scale. Neither holds for this table on this device.

**The five/six-point contradiction was an artefact of reading only the registry.** The registry
carries the six points MSI's UI edits. The firmware table wraps them with an idle duty and a
ceiling, and those two are exactly the values the desk research recorded — ClawTweaks' EX idle
floor of `58` and its `index 7 = 94`, one position over because byte 0 is the WMI status byte.
**The structural model was right all along; only the A2VM's duty numbers were wrong for an EX.**

**The temperature axis is fixed and is not part of a profile.** `Get_Temperature` sub-functions 1
and 2, and `Get_Thermal` sub-functions 1 and 2, are byte-identical across all three snapshots. MSI
Center M's Advanced UI edits duty only. A custom profile is therefore **seven duty percentages per
fan**, not a curve editor.

| Method | Layout | Value |
|---|---|---|
| `Get_Temperature\|1` | `[1]`, then `[4..8]` | **47**, 50, 57, 64, 71, 78 — the breakpoints |
| `Get_Temperature\|1` | `[2]`, `[3]` | 85, 105 — meaning not established; likely throttle and critical |
| `Get_Temperature\|2` | as above, `[3]` = 0 | fan 2 has no third value |
| `Get_Thermal\|1,2` | `[2..7]` | 48, 51, 58, 65, 72, 79 — the breakpoints **+1**, likely hysteresis |

**The registry is a mirror, for the third time on this device.** In the `auto` snapshot `High_Fan`
still held the stale `100;100;…` left by the previous capture, while `Get_Fan` correctly returned
the factory curve. The firmware table is the live state; `Component\User Scenario` is MSI Center
M's own store of what its UI last showed. `Fan` = `1` Auto / `3` Advanced was confirmed, and
`High_Fan` — not `Default_Fan` — is the editable curve. `Default_Temp` and `Default_Fan` did not
move in any snapshot and are the factory reference.

**Possibly a firmware-side mode flag.** `Get_AP` sub-function 1 byte 1 read `0x80` in both Advanced
snapshots and `0x00` in Auto. Byte 3 read `4`, matching the registry `Mode` value for User Scenario.
**Hypothesis only** — one transition, n=1. It is not needed to write a curve and should not be
relied on until it has been seen to toggle both ways on its own.

#### ⚠ The firmware does not enforce a duty floor

In `custom-low` both tachometers read **0**. The fans genuinely stopped, at every temperature, and
nothing below MSI Center refused the setting. The `58` idle duty is **the factory curve's first
value, not a floor the EC imposes** — the desk research's phrase "firmware idle duty floor" is
misleading and should not be read as a guarantee.

**Consequence: any custom profile this app applies must clamp its own minimum.** There is no layer
underneath that will do it. This is the single most important fact in this section.

#### Incident: hard lock on 2026-08-12, and why the sweep was narrowed

The device hard-locked and had to be cold-booted shortly after the capture run. The SSD vanished
and did not return until a full shutdown.

**Most likely unrelated to this work**, on the evidence:

- `disk` event 11 (controller error) appears on `Harddisk1`, `2` and `3` on **8 August**, four days
  before this branch existed, and again on 8/11 and on 8/12 three hours before the lock. The `DR`
  numbers shift between sessions, which is what removable devices being re-enumerated look like.
  The internal drive is `DeviceId 0` and is not among them.
- **No fan or thermal path can remove an NVMe device.** A fan fault throttles or shuts down
  cleanly. The fans were on `Fan = 1` with the factory table intact throughout.
- There is no crash dump and **no System-log entry at all** for the seventeen minutes before the
  lock. That is the signature, not missing evidence: Windows cannot flush the event log or write a
  dump to a disk that is gone, so it hangs instead of bugchecking.

**What is not ruled out.** The capture ran elevated and made roughly 96 ACPI control-method calls
per snapshot, into sub-functions the firmware may never be asked for in normal operation. Those
execute in kernel context against an undocumented implementation. Nothing points at it, and the
timing is a poor fit, but it is the only thing in this workstream that touched firmware.

**What changed as a result.** `Watch-Fan.ps1` now defaults to four methods across sub-functions
0–2 — the ones measured to carry fan state — and the original sweep is behind `-Wide`. A broad
sweep is worth its risk while hunting for a table and is not worth it once the table is known.

**The standing rule this reinforces:** never sweep a firmware surface wider or longer than the
question requires, and narrow the tool as soon as it has answered.

#### What the factory table is, for restore

Auto is fully captured, which satisfies the "capture before any write" item this gate has carried
since August. Both fans read identically:

```
idle 58 | 70 74 76 78 80 84 | ceiling 94
```

#### `Set_Fan` proven on device (2026-08-12)

Run with `Diagnostics/Test-SetFan.ps1`; transcript in `Diagnostics/transcripts/`. One fan, one
byte, upward only — the 78 °C point moved 84 → 90 on fan 1 with the idle duty left at factory, so
no failure mode could make the device quieter than stock.

| | Baseline | After write to fan 1 | After restore |
|---|---|---|---|
| Fan 1 | `58;70;74;76;78;80;84` | **`58;70;74;76;78;80;90`** | `58;70;74;76;78;80;84` |
| Fan 2 | `58;70;74;76;78;80;84` | **unchanged** | `58;70;74;76;78;80;84` |

- **The write takes**, confirmed by a separate read.
- **The sub-function is a real per-fan selector.** Fan 2 was never addressed and did not move. This
  was the control that mattered: if both had moved, the table model would have been wrong.
- **`Set_Fan` returns a bare status** (`01 00 00 …`), not an echo — same as `Set_SlaveBattery`. Its
  reply is not evidence, which is why every write here is confirmed by a read.
- Tachometers held at 134–136 across all three states. Correct, and a useful sanity check: editing
  the 78 °C point cannot do anything at a 44 °C idle.

#### "Auto" is not a mode — it is the factory table

The test wrote a **non-factory** table while MSI Center M's registry still read `Fan = 1` (Auto),
and the EC applied it. The firmware does not gate the table behind a mode, and nothing had to be
switched before writing or after.

**Consequence for the app: applying Auto is a plain write of the factory table**, which the restore
path does and which has now round-tripped twice. No mode register, no MSI Center involvement. The
suspected mode flag at `Get_AP|1` byte 1 is therefore *not needed* and remains an unconfirmed
curiosity rather than a dependency.

#### Still open

- [ ] Whether byte 1 (idle duty) is independently settable, or whether MSI mirrors the first curve
      point into it. Both custom snapshots set every entry to the same value, and the write test
      left it at factory, so nothing so far distinguishes the two.
- [ ] Whether MSI Center M overwrites a table we wrote, and on what trigger. It is still installed,
      and it is an active participant elsewhere — re-read rather than assuming our write still
      stands.
- [ ] Whether the write survives with MSI Center M's service stopped — the standing gate question
      for every feature in this project. The write needs no MSI Center to *succeed*; whether it
      *persists* is a separate claim.
- [ ] Meaning of `Get_Temperature|1` bytes 2 and 3 (85, 105).
- [ ] Tachometer units. Idle reads ~135, which is above 100, so it is **not** the duty percentage
      read back and must not be presented as one.

---

### Historical — the pre-2026-08-12 record

Everything below predates the measurement above and is kept for provenance. Where it disagrees,
the measurement wins.

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

**Answered 2026-08-08: MSI Center M's own fan control UI on this device accepts a 0–100% range,
not capped at 75.** Confirmed directly in MSI Center's own fan slider on the EX, not inferred from
the reference project. This refutes the "MSI's own cap is duty 75" assumption in the table above,
which was carried over from the A2VM reference project and had never been independently measured
on this device.

It does **not** by itself resolve the byte-layout question this gate is actually blocked on: a UI
accepting a 0–100% input says nothing about how that value gets encoded into the EC table this app
would have to write, or whether the UI's percentage even maps 1:1 to the raw duty byte (`Duty is
the raw EC byte, no scaling` above is itself still only "corroborated, not measured"). Still do not
write a fan table until the six-point curve and its byte encoding are reconciled — but when that
work happens, the ≤75 cap this project has assumed throughout (see `README.md`'s Safety section)
needs revisiting against this finding, not carried forward unquestioned.

---

## Gate G3 — Battery charge limit

> **FEATURE REMOVED 2026-08-08.** The charge limit is no longer part of this app: it is set in MSI
> Center, changes rarely, and the registry path was measured not to enforce it. The code is gone
> from the widget, helper, IPC contract and hardware layer, and `Function` ordinals 30/31 are
> retired.
>
> **Everything below is kept as a device record, not as live work.** It cost several rounds of
> on-device measurement and correctly identifies the working mechanism, so it stays for anyone who
> revisits this. The short version: the limit lives at `MSI_ACPI.Get_AP` / `Set_AP`, sub-function
> 0, byte 5, encoded `percent | 0x80`. The write was never exercised.

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

> **GREEN as of 2026-08-12 — `Set_AP` is verified, and the registry model below is superseded.**
> The charge limit is written through `MSI_ACPI.Set_AP` and needs nothing from MSI Center M. See
> [the write verification](#verified-2026-08-12-the-set_ap-write-works) below. The registry
> material that follows is kept as the device record of how this was chased, and because its two
> traps are a good illustration of why a round-tripping registry value proves nothing.

**Result (superseded): AMBER — the registry value is real and round-trips, but does not reliably drive the EC.**
`HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Battery`, value `BatteryLevel`, **REG_SZ**, with
exactly three states: `"0"` = 100%, `"1"` = 80%, `"2"` = 60%. Confirmed by three transitions
(100→80, 80→60, 60→100).

Two traps worth restating, because both fail quietly:

- **The numbering is inverted.** A higher stored level is a *lower* charge limit. Encoding and
  decoding live in `ChargeLevels.ToMsiLevel` / `TryFromMsiLevel` so they can be unit tested,
  including a test asserting the mapping is monotonically *decreasing*.
- **REG_SZ, not REG_DWORD**, unlike the power limits in the neighbouring key. Writing a DWORD
  would change the value's type under MSI Center rather than its content.

**"Charge to 100%" is the off state** — there is no separate enable flag on the device. So the
helper remembers the user's last chosen limit in settings and restores it when the limiter is
switched back on; the device cannot, because turning it off overwrites the only field that held it.

> **Answered 2026-08-08, and it overturns the assumption below: the registry write alone does NOT
> reliably apply.** A/B tested on device: `BatteryLevel` set to the identical value two ways —
> through the helper's registry write, and through MSI Center's own UI — read back identical
> either way, but only the MSI-Center-driven change actually changed charging behaviour. The
> helper's own write left the previous charging state in effect regardless of what the registry
> now said. Most visible at `"0"` (100%, the "off" state): setting it through the widget did not
> resume charging, while setting the same "0" through MSI Center did.
>
> This directly contradicts the "by the same logic `MSI_Center_M_Server_Battery` is the presumed
> applier" assumption previously recorded here. That assumption was carried over from TDP by
> analogy, not independently measured the way `Test-TdpRegistryApply.ps1` measured TDP — and unlike
> TDP, it does not hold. Whatever MSI Center's own UI does beyond writing the registry value (most
> likely a direct call through `MSI_ACPI.Set_MasterBattery`, the ACPI-WMI path the original
> [desk-research table](#relationship-to-msi-center-m) always expected this feature to need) is
> necessary, and the helper does not currently do it. `RegistryChargeLimitProvider` should be
> treated as read/persistence-only until this is resolved, not as a working apply path.
>
> **Next step:** `MSI_ACPI.Set_MasterBattery` / `Get_MasterBattery`'s parameter schema is unknown -
> the class and method names are known from desk research, but not their argument shape. Use the
> new `--wmi-method MSI_ACPI Set_MasterBattery` Probe command (read-only — it dumps the method's
> declared in/out parameters without calling it) to find that out before writing any code that
> calls it.

#### No Windows API exists for this (established 2026-08-08)

Worth recording so it is not re-investigated. **Microsoft has never shipped a battery
charge-threshold API.** ACPI's `_BTP` is a *notification* trip point, not a charge limiter; the
WinRT `Windows.Devices.Power` / `Windows.System.Power` surfaces are read-only. Charge thresholds are
vendor EC/BIOS features, which is why Lenovo Vantage, Dell Power Manager, MyASUS and Acer Care
Center all ship their own utilities to do it. So MSI's own ACPI-WMI interface is not merely the
preferred route — it is the only driver-free one.

#### ClawTweaks cannot answer this (checked 2026-08-08)

Re-verified against the live GitHub tree, not just the 2026-08-07 note above: the public repo
contains only `ClawTweaksSetup/Core/HelperControl.cs` and `HelperPipeClient.cs` — the *client* side
that talks to a helper over a pipe. **No battery, EC, ACPI or hardware-layer source exists in any
ref.** Their implementation ships only as a compiled binary, so answering "how does ClawTweaks do
it" would require decompiling the installed helper. Permitted under `LICENSE-NOTES.md`, but a much
bigger lift than the route below, and unnecessary given it.

#### Threshold byte encoding — hypothesis, source: `msi-ec` (2026-08-08)

From the **`msi-ec` Linux driver** (`BeardOverflow/msi-ec`, GPL), which documents MSI firmware's EC
layout. Recorded here as a hardware fact under rule 1 of this document — it describes the embedded
controller, which does not know or care what OS is running, and **no Linux code is used, ported or
shipped**. It is also cleaner provenance than ClawTweaks for an MIT project, carrying no
AGPL-derivative risk.

| Fact | Value |
|---|---|
| Threshold encoding | **`percent \| 0x80`** — bit 7 is an enable/commit flag, bits 0-6 the percent |
| Write | `ec_write(addr, value \| BIT(7))` |
| Read | masks `~BIT(7)` to recover the percent |
| Charge-control EC address | `0xEF` on gen-1 MSI configs, `0xD7` on gen-2 |

Expected bytes if the encoding holds: 60% → `0xBC`, 80% → `0xD0`, 100% → `0xE4`.

> **Unverified on this device.** Those addresses are laptop configurations and the Claw is a
> handheld, so **the address is not assumed** — only the encoding is carried forward, and only as a
> hypothesis to confirm by reading. Per rule 3 of this document, this does not go into a write
> until it has been read back on the EX.

**Decision: writes go through `Set_MasterBattery` only.** `MSI_ACPI` also exposes `Set_EC`, which
writes a raw byte to an arbitrary controller address — a wrong address reaches fan or thermal
registers on real firmware. The purpose-built method lets the firmware validate instead. Enforced
in code, not merely documented: `Probe`'s `--acpi-get` refuses any method not named `Get_*`, and
`--set-charge-limit` calls `Set_MasterBattery` and nothing else.

#### The ACPI-WMI interface, decoded (measured 2026-08-08)

| Fact | Value |
|---|---|
| `Get_MasterBattery` / `Set_MasterBattery` | take one `[EmbeddedInstance, in, out]` parameter, `Data` |
| Embedded class | `Package_32`, one property `Bytes : UInt8Array` |
| Accepted payload | 32 bytes |
| Return | `Boolean` |
| `MSI_Master_Battery` (the plain class) | **"Not supported"** — the property read is unavailable, so the method call is the only route |

**Input byte 0 is a sub-function selector, not a value.** `Get_MasterBattery` returns different
data for `0x00`–`0x03`, which is what establishes this. It also means a payload of
`percent | 0x80` in byte 0 would be read as *sub-function `0xBC`*, not as a threshold — so the
obvious write is malformed, and the write shape is still unknown.

#### `Get_MasterBattery` does NOT carry the charge limit (measured 2026-08-08)

Three read-only runs, with MSI Center's own limit set to 100%, 80% and 60% between them
(`Diagnostics/battery-limit-results.zip`). Sub-functions `0x00`–`0x05` were byte-identical across
all three, with one exception that does not survive scrutiny:

| Selector | Behaviour across 100 / 80 / 60 |
|---|---|
| `0x00` | identical — `01 09 00 …` |
| `0x01` | identical — `01 88 13 C8 3C F4 01 E0` |
| `0x02` | identical — `01 00 00 64 00 E2 13`; the `0x64` is a constant 100, i.e. design capacity, not the limit |
| `0x03` | `…9E 45 1B…` / `…A0 45 1B…` / `…9F 45 1C…` |
| `0x04`, `0x05`, `0xEF`, `0xD7` | all zeroes |

`0x03` moves, but **not with the setting**. Read as little-endian pairs it is live telemetry:
bytes 3–4 give 17822 / 17824 / 17823 mV, a plausible 4S pack voltage drifting by ±2 mV between
reads, and bytes 5–6 give 3099 / 3099 / 3100. A threshold field would also be monotonic in the
limit, and this is not — 100% → `9E`, 80% → `A0`, 60% → `9F`.

> **The `Get_EC` rows in that capture are void.** They all reported "rejected", but that was a bug
> in the harness, not a property of the device: it built every payload from the class taken off
> `Get_MasterBattery` and reused it for all methods, so any method wanting a different package was
> handed the wrong one. The EC hypothesis is **untested**, not refuted. Fixed by resolving the
> package class per method.

#### FOUND: the charge limit is `MSI_ACPI.Get_AP` / `Set_AP`, sub-function 0, byte 5

**Measured on device 2026-08-08.** Method: a read-only sweep of every `Get_*` method across
sub-functions 0–7, snapshotted at MSI Center's 100 / 80 / 60 settings and diffed
(`Diagnostics/Sweep-MsiAcpi.ps1`, raw data in `Diagnostics/acpi-snapshots-plus-diff.zip`). Each
sub-function was read twice per snapshot so that any byte which could not hold still within a
snapshot was excluded as telemetry.

| Fact | Value |
|---|---|
| Method pair | **`MSI_ACPI.Get_AP` / `Set_AP`** |
| Parameter | one `[EmbeddedInstance, in, out]` `Data`, class `Package_32`, property `Bytes` |
| Sub-function | **input byte 0 = `0x00`** |
| Threshold location | **output byte 5** |
| Encoding | **`percent \| 0x80`** — bit 7 set, bits 0–6 the percentage |
| Confirmed values | 100% → `0xE4`, 80% → `0xD0`, 60% → `0xBC` |

Full response package, constant except byte 5:

```
byte:  0  1  2  3  4  5   6..31
      01 00 00 C6 80 XX   00 …
```

Bytes 3 and 4 (`0xC6`, `0x80`) are constant across all three settings and are presumed to identify
the register being reported; byte 0 (`0x01`) is presumed a success flag. **None of that is
verified** — only byte 5 is.

**This confirms the `msi-ec` encoding on this device**, which had been carried as a hypothesis
since 2026-08-08. Note the carrier is a WMI method, not a raw EC address we reach directly, so the
`0xEF` / `0xD7` addresses from that driver remain irrelevant here — the encoding transferred, the
addressing did not.

**Why the earlier attempts missed it.** `Get_MasterBattery` is the obviously-named method and does
not carry the limit at all. `Get_AP` is not a name anyone would guess for a battery setting, which
is the argument for sweeping every method and diffing rather than reasoning about names.

The other nine bytes the diff flagged are the device warming during the capture — `Get_Temperature`
sub-function 0 byte 1 moved 52 → 54 → 55 and `Get_Thermal` sub-function 3 byte 7 moved 47 → 48 → 50,
monotonic with elapsed time rather than with the setting. Do not re-chase them.

#### VERIFIED 2026-08-12: the `Set_AP` write works

**Read-modify-write, exactly as this section predicted.** Read sub-function 0, change byte 0 to the
sub-function and byte 5 to the new value, send the rest back untouched. Implemented as
`--set-charge-limit` in the Probe.

```
Before          01 00 00 C6 80 BC        60%
Sent Set_AP     00 00 00 C6 80 D0        80%
Set_AP reply    01 00 00 00 00 00        bare ack - does NOT echo the value
After (Get_AP)  01 00 00 C6 80 D0        80%
```

**The hardware is the oracle, not the read-back.** The battery sat at 74% and had stopped charging
against the 60% limit; raising the limit to 80% made it *resume charging*. That is the firmware
acting on the value. Read-back alone would not have been evidence — `Set_AP`'s own reply is a bare
`0x01` status with the value zeroed out, the same trap `Set_SlaveBattery` sets in G1.

**Only byte 5 ever moves.** Re-confirmed at three points against MSI Center's own setting on
2026-08-12 — 60% → `0xBC`, 80% → `0xD0`, 100% → `0xE4` — with bytes 0–4 (`01 00 00 C6 80`)
identical in all three. That is what makes echoing bytes 3 and 4 back safe rather than hopeful.

**Encoding: `percent | 0x80` and `percent + 0x80` are the same thing here.** Bit 7 is clear for
every value in the accepted 20–100 range, so the two forms cannot be distinguished and the
distinction does not matter. Use either.

**MSI Center M did NOT notice.** Its UI still showed 60% afterwards. That is the useful outcome:
it means `Set_AP` is the live register and MSI Center keeps its own cached copy, rather than both
reading one source. A value the two agreed on could still have been a shadow.

> **Unverified:** whether MSI Center M re-asserts its own value later — on its next tick, on
> resume, or when its UI is opened. It has no bearing on the standalone case, which is the one that
> matters, but it decides whether the two can coexist until the uninstall.

---

## Gate G4 — RGB LED

> **FEATURE REMOVED 2026-08-08.** Lighting is no longer part of this app. Mode, colour and effect
> ride a vendor HID report that was never decoded, so the most the widget could offer was an on/off
> toggle sitting next to MSI Center's far more capable control — not worth the surface. The code is
> gone from every layer and `Function` ordinal 40 is retired.
>
> **Everything below is kept as a device record, not as live work.**

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

**Result: AMBER for mode/colour/effect, GREEN for on/off.** Brightness on/off is at
`OsdEditor\LightingBrightness` (DWORD, `0`/`1` only). **Effect and colour are not in this registry
hive at all** — switching RGB profile produced no delta, so `MSI_Center_M_Server_MysticLight` keeps
them elsewhere or writes the device directly. The vendor HID collection is present as
`HID\VID_0DB0&PID_1901&MI_01` ("HID-compliant vendor-defined device"), so the HID path from the
desk research remains the plan for mode/colour/effect. Report `0x0F` (64 bytes) is still unverified
on this device, and that work has not started.

> **Superseded 2026-08-12 — the sentence above is out of date.** G5 verified report `0x0F` on this
> device, established its framing, and shipped `MsiVendorHidChannel`, which speaks it. The channel
> is found, opened and proven; only the lighting opcode and payload remain. There is also a
> captured `0x05` config dump that appears to contain the current per-zone RGB triples. See
> [What's next](#whats-next--fan-charge-limit-and-rgb).

> **Superseded again, 2026-08-12 — G4 is fully decoded.** See
> [The lighting protocol](#the-lighting-protocol--decoded-2026-08-12) below. The `0x05` dump was
> not a config broadcast at all; it is `ReadProfileAck`, the answer to a read we can issue
> ourselves. Read and write are both proven.

### The lighting protocol — decoded 2026-08-12

**Read from MSI's own code, not guessed.** `C:\Program Files (x86)\MSI\MSI Center M\API_ControlMode.dll`
is unobfuscated .NET and carries the entire protocol: the `CommandType` enum, the packet framing,
and a complete parser for the controller's configuration blob. `ilspycmd` decompiles it in seconds,
and **it needs no elevation to read**.

That also independently confirms everything gate G5 decoded by observation — `0x24` SwitchMode,
`0x26` ReadGamepadMode, `0x27` GamepadModeAck, and the `0F 00 00 3C` header are all exactly as
`MsiVendorHidChannel` already builds them. Two derivations, one answer.

#### Lighting is not its own feature

There is no "set colour" command. Lighting is a **slice of the controller's single 1478-byte
configuration blob** — the same one holding key mappings, macros, stick calibration and rumble.
Changing the lighting means writing bytes 586..1468 and letting the firmware animate what it finds.

```
profile blob, 1478 bytes
  0     custom data, keys, sticks, triggers, macros   NOT OURS - never write this range
  586   light block, 883 bytes
        586   active animation index, 0-3
        587   animation 0    220 bytes
        807   animation 1    220 bytes
        1027  animation 2    220 bytes
        1247  animation 3    220 bytes
        1467  audio rhythm enable
        1468  reserved

animation, 220 bytes
  0     active keyframe count, 1-8
  1     effect number, always 9
  2     speed, STORED INVERTED: raw = 20 - speed
  3     brightness, 0-100
  4     8 keyframes of 27 bytes each: 9 RGB triples

the 9 LEDs
  0-3   left stick ring
  4-7   right stick ring
  8     ABXY cluster
```

#### Opcodes

| Op | Name | Direction | Notes |
|---|---|---|---|
| `0x04` | ReadProfile | out | `04 00 <offH> <offL> <len>`, len ≤ 55 |
| `0x05` | ReadProfileAck | in | same header, payload from byte 9 |
| `0x21` | WriteProfileToRAM | out | `21 00 <offH> <offL> <len> <data…>` |
| `0x22` | SyncToROM | out | persists across power cycle — **we do not send this** |
| `0x06` | Ack | in | bare, no payload |

**`0x21` is the write, not `0x03`.** MSI's enum names `WriteProfile = 3` but `GetWriteProfileCommand`
puts **33** in the opcode byte. Trust the code that builds the packet over the enum that names it;
`0x21`–`0x24` are the coherent family the firmware actually implements.

We write to RAM and never to ROM. Flash has a write budget and this is driven by a widget the user
can poke repeatedly; the helper re-applies on start instead, exactly as the charge limit does.

#### Verified on the device, 2026-08-12

`--lighting` read the live block and decoded animation 0 as one keyframe of `#7F00FF` on all nine
LEDs, brightness 100, speed 17. That matches `Profile_1.cfg`'s `Button_Style_Steady_Color1=127,0,255`
and MSI's own `SetAsSteadyOp1` speed of 17 — **an independent oracle for the offset arithmetic**,
which was computed from the parser rather than found by poking.

#### Why the read side had to come from the binary

Controller mode announced itself: the firmware pushes `0x27` on every physical button press, so
watching input reports revealed the protocol. **Lighting has no physical control, so nothing
pushes.** Listening while changing lighting in MSI Center M produces only bare `0x06` acks — the
write is an *output* report, and one process cannot see another's writes. The observable side
proves *when* but never *what*.

#### Where the three profiles live

**In MSI Center M, not the controller.** `C:\MSI\MSI Center M\Mystic Light\Profile\Profile_{1,2,3}.cfg`
are plain INI, keyed by `VID_0DB0&PID_1901`. They hold a `StyleSelectIndex` into `EnumStyle`
(`Off=0, Steady=1, Breath=2, ColorCycle=3, Wave=4, Customize=5, InfoMode=6`) plus per-style colours,
speed and direction. **They do not survive an uninstall**, so they are archived at
`Diagnostics/mystic-light-profiles/`.

The controller stores only the flattened *result*: the keyframes MSI computed from a style. So a
style is a recipe, not a device concept — `LightPresetServices` in `API_ControlMode.dll` is the
recipe book, and reproducing a profile means re-deriving its keyframes the way that class does.

`SaveDeviceLightinROM()` in `API_MysticLight.dll` is **an empty method**. There is no ROM-backed
profile store to inherit.

**Decided 2026-08-08: ship on/off now, scope mode/colour/effect out until report `0x0F` is
decoded.** `RegistryLedProvider` (`src/Hardware/Windows/RegistryLedProvider.cs`) reads and writes
`LightingBrightness` through the same mirror-and-read-back model already verified for TDP, fan mode
and charge limit — no new mechanism, just the one lighting fact this hive actually holds. The
widget's Lighting card is a single toggle rather than the mode/brightness controls it exposed
before. Same caveat as the [performance mode](#performance-mode--decoded) section: MSI's own mode
selector writes this same value (Endurance turns it off, leaving it turns it back on), so a read
here can legitimately disagree with the toggle's last write — the widget re-syncs the whole
snapshot after a `PerfMode` change rather than trusting a stale local value.

---

## Gate G5 — Firmware desktop-mouse mode

The firmware variant, not software cursor injection — that is the whole point, because a real HID
mouse keeps working on the UAC secure desktop.

- [x] Write report identified
- [x] **Read-back path identified** (the physical MSI button changes this too, so the helper
      cannot assume it owns the state)
- [ ] Verified working on the UAC secure desktop

**Result: GREEN via vendor HID, standalone of MSI Center M (changed 2026-08-12).**

### The vendor command channel

Interface `VID_0DB0&PID_1901&MI_01`, usage page `0xFFA0` / usage `0x0001`. Two reports, both 64
bytes: **output `0x0F`** and **input `0x10`**. There is no feature report, so a read is a send
followed by a listen.

```
byte 0   report id   0x0F outbound, 0x10 inbound
byte 1   0x00
byte 2   0x00
byte 3   0x3C        60 - payload length, i.e. the 63 report bytes less this header
byte 4   opcode
byte 5+  arguments, zero padded to 64
```

| Opcode | Direction | Meaning |
|---|---|---|
| `0x26` | out | query mode |
| `0x27` | in | **mode is now `<mode>`** — answers `0x26`, and is *pushed* on a button press |
| `0x24` | out | switch to `<mode>` |
| `0x06` | in | follows every mode change; also seen alone. Undecoded, and not needed |
| `0x05` | in | long multi-frame config dump, including LED colours. Relevant to **G4**, not here |

**Modes: `0x01` XInput · `0x02` DirectInput · `0x04` Desktop.** The reference notes had only
`0x24` with `0x04`/`0x02`, no `0x01`, and no read path at all — which is precisely why this gate
sat open. The `0x26`/`0x27` pair and `0x01` were established on device by watching the channel
(`--hid-watch`), not by guessing, and the read was cross-checked against MSI Center's own registry
value as an independent oracle.

### The registry was downstream all along

Writing the mode over HID makes MSI Center M update `ControlModeUserSet` to match. It *watches* the
device and mirrors it; it was never the source of truth. That retires the objection that the
registry path was "verified" — it was, but it was verifying a shadow.

**And the shadow lags.** Immediately after a HID switch the registry still read `XInput` while the
device reported `Desktop`, catching up about a second later. The widget polls this at ~1 Hz, so the
old path could show a mode the device was not in — briefly, but for longer than a frame.

### The firmware owns the physical button

Confirmed 2026-08-12 with the **whole MSI Center M stack stopped** and verified down first —
`MSI_Center_M_Server` supervises every other server and is a **scheduled task, not a service**, so
killing a child alone respawns it and stopping the service does nothing. With it genuinely gone the
button still switched modes, still announced each change on `0x27`, and `0x26` still answered.

**Consequence: there is no button press to intercept and no switch to re-issue.** The helper only
has to listen so the widget follows the hardware. Repeatable via
`Diagnostics/Test-ControllerModeStandalone.ps1`.

The button toggles `0x01` ↔ `0x04` only; DirectInput was never observed from it.

### Notes for the implementation

- **No elevation needed**, unlike the ACPI-WMI TDP path, and the collection opens **shared** — it
  worked alongside a running MSI Center M holding the same handle.
- **A software `0x24` produces no `0x27`.** Only the button announces. So a write must be confirmed
  by a `0x26` query; waiting for an announcement that never comes would hang.
- **Mode does not re-enumerate.** The PID stays `0x1901` across every switch, so the device list is
  *not* a read-back path. Worth stating because it is the obvious thing to reach for.
- **Three states, one boolean.** `IHwMouseProvider` models this as `bool desktopMode`, which cannot
  express DirectInput. Nothing we or the button produce reaches that state today, so the boolean is
  not wrong — but it is a narrowing, and it is deliberate rather than inherited.

**How the shared-ownership problem is handled.** The physical MSI button switches the same mode, so
a read can disagree with our last write at any moment through no fault of ours. Two consequences,
both deliberate:

- **Nothing re-applies a stored mode at startup**, and nothing is captured for uninstall restore.
  We do not own this state, so "putting it back" would mean overwriting whatever the user or the
  button last chose with a value from an arbitrary earlier moment.
- **The helper pushes the mode on its ~1 Hz telemetry tick** while the widget is visible, so the
  buttons follow the hardware. Without that the widget would show whatever was true at connect
  time and silently disagree with the device after one button press.

The widget shows this as two named buttons, Gamepad and Desktop, rather than a toggle: "off" on a
switch labelled *desktop mouse mode* leaves the actual resulting state unnamed.

**Unverified:** that desktop mode still works on the UAC secure desktop. That is the whole premise
of the firmware route over software cursor injection, and it has not been tested on device.

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
| Endurance Gaming (FPS tier) | | **per-application, not global.** Shown in the widget as "FPS limit": Off / 30 / 40 / 60 |
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
