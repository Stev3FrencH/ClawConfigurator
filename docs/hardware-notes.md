# Hardware notes — MSI Claw 8 EX AI+

> **Status: EMPTY. Phase 0 has not been run yet.**
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

## Gate G1 — TDP

**The hard gate. M2 does not start until this is green.**

The question is whether power limits can be set *without a ring-0 shim*. The reference project
lists an `IntelKxExe` method — MCHBAR MMIO through a kernel extension — which is out of scope
here. If that is the only path this model supports, the registry mirror is the sole option.

- [ ] Decompiled helper's TDP backend identified
- [ ] Direct ACPI-WMI method found (class, method, parameter encoding)
- [ ] Registry mirror found (exact key, value names, type)
- [ ] **Units confirmed at two different values** (17 W vs 25 W)
- [ ] Behaviour with the MSI Center service *stopped* established

| Fact | Value | Source |
|---|---|---|
| WMI class | | |
| WMI method | | |
| Registry key | | |
| Value names | | |
| Units | | watts / milliwatts / raw |
| Works without MSI Center | | **decides whether this project can replace MSI Center** |
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
| Table is 8 bytes, `{0, 0, D0, D1, D2, D3, D4, D4}` | unverified | |
| Only indices 1..6 may be written | unverified | index 0 and 7 are EC state; EX ships index 7 = 94 |
| Duty is the raw EC byte, no scaling | unverified | |
| MSI's own cap is duty 75 | unverified | this app never exceeds it |
| Firmware idle duty floor is 58 | unverified | below this the firmware overrides the curve |
| Full-speed override at block 152 (0x98), bit 7 | unverified | separate control from the curve |

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
| Accepted range | | plan assumes 60–100 |

**Result:** _not yet determined_

---

## Gate G4 — RGB LED

- [ ] Vendor HID collection identified (usage page ≥ 0xFF00)
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

| Fact | Value | Source |
|---|---|---|
| Report to enable | | |
| Report to disable | | |
| How to read current mode | | |
| Physical button behaviour | | |

**Result:** _not yet determined_

---

## Gate G6 — Intel IGCL

- [ ] `ControlLib.dll` present
- [ ] `ctlInit` succeeds
- [ ] Adapter enumerated (`pci_vendor_id == 0x8086`)
- [ ] Per-feature support established via `ctlGetSupported*`

| Feature | Supported | Notes |
|---|---|---|
| Endurance Gaming (FPS tier) | | **per-application, not global** — affects the UI wording |
| Low latency | | |
| Frame sync | | |
| Adaptive sharpness | | |
| Colour (saturation / contrast / gamma) | | |

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
