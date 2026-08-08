# msi-mcenter-lite

A lightweight front-end for the settings MSI's M Center owns, targeting **only** the MSI Claw 8 EX
AI+ (Panther Lake, `CG3EM` / board `1T91`), delivered as an Xbox Game Bar widget.

> **MSI Center M must stay installed and running.** This is not a replacement for it — power
> limits are applied *through* it, by writing the model its own service reads. That is the same
> arrangement ClawTweaks uses, and the reason it works on this device. See
> [docs/hardware-notes.md](docs/hardware-notes.md#relationship-to-msi-center-m).

> **Status: the widget builds, packages, installs, and runs.** CPU Boost and OS Power Mode are
> verified working end-to-end through the real widget, including reflecting changes made outside
> it (Windows Settings, the taskbar flyout, the physical mode button). Everything else is
> implemented but unverified against the real Claw hardware — the only device this has run on so
> far is a different, correctly-gated-off-as-unsupported machine. Fan (gate G2) and RGB LED
> (gate G4) also need Phase 0 discovery finished before they can be turned on at all; desktop/
> gamepad mode (G5) and Intel GPU controls (G6) are unimplemented stubs regardless of their gate
> status. See [docs/hardware-notes.md](docs/hardware-notes.md) for the full gate-by-gate picture.

## Scope

Eight features, deliberately few:

| # | Feature | Status |
|---|---|---|
| 1 | TDP (PL1 / PL2) — via MSI Center's registry model | ✅ implemented, unverified on device |
| 2 | Fan presets — 3 fixed profiles, no custom curve | blocked on gate G2 |
| 3 | Battery charge limit — 60 / 80 / 100 % | ✅ implemented, unverified on device |
| 4 | RGB LED | blocked on gate G4 |
| 5 | Desktop / gamepad mode (firmware) | blocked on gate G5 |
| 6 | CPU Boost | ✅ implemented |
| 7 | OS Power Mode | ✅ implemented |
| 8 | Intel GPU controls (IGCL) | blocked on gate G6 |

Live metrics and per-game profiles are out of scope.

## Design constraints

- **No kernel driver.** Only MSI ACPI-WMI, user-mode vendor HID, Intel IGCL, and documented
  Windows APIs. No WinRing0, inpoutx64, PawnIO, kx.exe, MSR or MCHBAR access. This rules out the
  `kx.exe` MCHBAR route to TDP — which costs nothing, because power limits go through MSI Center's
  registry model instead.
- **Runs alongside MSI Center M, not instead of it.** Accepting that dependency is what makes the
  no-driver constraint affordable. The trade is a real one: TDP rides on an undocumented,
  MSI-owned registry schema that an MSI Center update can change without warning.
- **The helper is authoritative.** Every write is read back and the actual value returned; the
  widget renders that, never its own optimistic value.
- **Every value is clamped server-side.** The pipe is ACL'd to all app packages, so the widget's
  slider bounds are a convenience, not enforcement.
- **One writer for settings.** The helper owns `settings.json`; the widget persists nothing
  functional. This removes the whole family of "my setting reset itself" bugs.
- **Single device.** Another Claw generation is treated as unsupported, not as close enough — the
  EC layout, duty floor and power ceilings differ.

## Layout

```
src/Shared/      netstandard2.0   IPC contract, fan model, payload encodings. No dependencies.
src/Hardware/    net8.0-windows   Provider interfaces, fakes, Windows power, device detection.
src/Helper/      net8.0-windows   Elevated pipe server. Owns all hardware access.
src/Probe/       net8.0-windows   Phase-0 discovery tool and regression harness.
src/Widget/      UAP              Game Bar widget.
src/Package/     wapproj          MSIX packaging.
tests/           net8.0           Unit tests for Shared.
Diagnostics/     PowerShell       Phase-0 scripts. Read-only.
docs/            hardware-notes.md — the most important file in the repo.
```

## Building

The .NET projects build **on macOS, Linux or Windows**:

```bash
dotnet build McenterLite.sln
dotnet test  McenterLite.sln
```

This is deliberate. Every project uses plain `net8.0-windows` with no SDK-version suffix, so no
Windows targeting pack is needed and the code can be authored anywhere.

The widget and MSIX package are the exception: UAP `.csproj` and `.wapproj` **cannot** be built by
`dotnet build`. They need **Visual Studio 2022 with the UWP workload and Windows 11 SDK 10.0.26100**.
A real Windows machine or VM is a hard dependency for producing a package — see
[`docs/building-the-widget.md`](docs/building-the-widget.md) for the full setup and build walkthrough.

If you have no .NET SDK, install one without admin rights:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

## Installing

There is no pre-built release yet — the widget has to be built, packaged and signed locally
first. The full walkthrough (Visual Studio setup, compiling the widget, creating the packaging
project, signing) is [`docs/building-the-widget.md`](docs/building-the-widget.md). Once a signed
`.msix` exists under `src/Package/AppPackages/`, on the target device:

```powershell
.\src\Package\Install.ps1
```

This imports the signing certificate to `LocalMachine\TrustedPeople`, stops any running instance,
and installs the package. Then:

1. Open the Game Bar (**Win+G**) and pin **M Center Lite**.
2. Accept the one elevation prompt on first run — the helper uses it to deploy itself and
   register a scheduled task. Hardware controls do not work until this is accepted.
3. The widget reconnects on its own a few seconds after the prompt.

Logs land at `%LOCALAPPDATA%\Packages\<package family>\LocalCache\McenterLite\helper.log`.

To uninstall: remove the app from *Settings > Apps*, then run the deployed helper once with
`--uninstall` to remove its scheduled task and restore any captured original values.

This only applies to the MSI Claw 8 EX AI+ (see [Scope](#scope)) — on any other device the
hardware-specific cards stay hidden, and only CPU Boost and OS Power Mode do anything.

## Running

For development and discovery, without installing the packaged app — against simulated hardware,
on any Windows machine:

```
McenterLite.Helper.exe --fake-hardware
```

Discovery and verification, on the Claw, elevated:

```
McenterLite.Probe.exe --device        # confirm the model gate
McenterLite.Probe.exe --power         # CPU boost + power mode (real, works anywhere)
McenterLite.Probe.exe --dump-acpi C:\acpi
McenterLite.Probe.exe --wmi-classes MSI
McenterLite.Probe.exe --hid-list
```

Read commands are safe; only `set-*` changes anything.

## Phase 0 — the hard gate

The hardware protocol is not documented anywhere public. Discovery runs before implementation, and
its output is [`docs/hardware-notes.md`](docs/hardware-notes.md).

The shortcut: **ClawTweaks is installed and working on the target device**, and its helper is a
.NET assembly. Its source repository does not contain the hardware layer, but the compiled binary
on the device does. `Diagnostics\Find-ClawTweaksHelper.ps1` locates it.

Extract **facts** — WMI class and method names, register offsets, byte layouts, HID report formats.
Do not copy code: ClawTweaks is AGPLv3, and copying it would force the same licence here. See
[`LICENSE-NOTES.md`](LICENSE-NOTES.md).

```powershell
.\Diagnostics\Get-DeviceReport.ps1 -Transcript .\device-report.txt
.\Diagnostics\Find-ClawTweaksHelper.ps1
.\Diagnostics\Watch-MsiCenter.ps1 -Label pl1-17 -TraceWmi
```

## Safety

Fan control writes to an embedded controller. The mitigations are structural, not advisory:

- Only three fixed presets; no user-authored duty value ever reaches the EC.
- Every duty is clamped to ≤ 75, MSI's own ceiling.
- Only table indices 1..6 are written; the EC's own boundary bytes are preserved.
- Curves are forced monotonic — a duty that falls as temperature rises is the one shape that can
  actually cook the device.
- Every write is read back and compared before being reported as successful.
- The factory table is captured before the first write and restored on uninstall.
- The Intel IPF escape hatch ships before any EC write.

The table builder lives in `Shared` with no platform dependencies specifically so it is unit-tested
without the device.

**Do not run this and ClawTweaks at the same time.** Both write the same EC.

## Licence

MIT — see [`LICENSE`](LICENSE). All code here is written from scratch; see
[`LICENSE-NOTES.md`](LICENSE-NOTES.md) for why that was the deliberate line held throughout, not
an accident of how the choice turned out.
