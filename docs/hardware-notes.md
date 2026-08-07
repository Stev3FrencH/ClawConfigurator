# Hardware notes — MSI Claw 8 EX AI+

> **Status: on-device Phase 0 has NOT been run.** What exists below is desk research from the
> ClawTweaks public repository — see [Desk research](#desk-research-clawtweaks-public-repo-2026-08-07).
> Those facts narrow the search; they do not replace measurement on this device.
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

## Device baseline

Fill in from `Diagnostics\Get-DeviceReport.ps1`.

| Field | Value | Source |
|---|---|---|
| `Win32_ComputerSystemProduct.Vendor` | | |
| `Win32_ComputerSystemProduct.Name` | | |
| `Win32_BaseBoard.Product` | | expected `1T91` |
| BIOS version / date | | |
| CPU | | |
| GPU + driver version | | |
| `ControlLib.dll` present | | gate G6 |
| MSI Center M version | | |

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

**Result:** _not yet determined_

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

**Answer this before anything else in G2:** does ClawTweaks show a Fan tab on this EX? The
reference project's per-model capability flag may still have fan control disabled on Panther Lake
(see the desk-research section). If its author declined to ship EC fan writes here, find out why
before we ship them.

- [ ] ClawTweaks Fan tab visible on this device (yes/no — decides whether M3 proceeds as planned)
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
indistinguishable from Default (likely, since its low points sit under the duty floor), say so
here and change the UI rather than shipping a preset that does nothing.

| Preset | Idle RPM | Load RPM | Distinguishable? |
|---|---|---|---|
| Default | | | — |
| Quiet Idle | | | |
| Cooling | | | |

**Result:** _not yet determined_

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
| Accepted range | | reference project accepts **20–100**; our UI offers 60–100 by choice |

**Result:** _not yet determined_

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

**Result:** _not yet determined_

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

**Result:** _not yet determined_

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

**Result:** _not yet determined_

---

## Intel thermal stack

Intel's IPF/DTT owns a fan participant above the EC and can hold the fan at maximum regardless of
any table written. The escape hatch ships **before** any EC write.

| Fact | Value |
|---|---|
| Services present | expected `ipfsvc`, `dptftcs` |
| Fan participant device id | expected `ACPI\INTC106A\TFN1` |
| Effect of stopping them | |
| Does the curve take effect with IPF running? | |

---

## Open questions

_Anything discovered that does not fit above. Unknowns are worth writing down._
