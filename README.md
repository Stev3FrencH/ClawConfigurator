# msi-mcenter-lite

A lightweight front-end for the settings MSI's M Center owns, targeting **only** the MSI Claw 8 EX
AI+ (Panther Lake, `CG3EM` / board `1T91`), delivered as an Xbox Game Bar widget.

> **MSI Center M must stay installed and running.** This is not a replacement for it — power
> limits are applied *through* it, by writing the model its own service reads. That is the same
> arrangement ClawTweaks uses, and the reason it works on this device. See
> [docs/hardware-notes.md](docs/hardware-notes.md#relationship-to-msi-center-m).

> **Status: the widget builds, packages, installs, and runs on the real Claw.** CPU Boost, OS Power
> Mode and the Power Limits card are verified working end-to-end, including reflecting changes made
> outside the widget (Windows Settings, the taskbar flyout, the physical mode button).
>
> **The scope has narrowed deliberately.** Fan control, battery charge limit and RGB LED were all
> removed (2026-08-08): each is set in MSI Center, none changes often, and MSI's own controls are
> better than anything this widget could offer for them. Desktop/gamepad mode (G5) and Intel GPU
> controls (G6) are unimplemented stubs. See
> [docs/hardware-notes.md](docs/hardware-notes.md) for the full gate-by-gate picture, including the
> findings from the removed features, which are kept as a device record.
>
> What is left is the part MSI Center is *worse* at: changing power limits quickly, from a
> controller, without leaving the game.

## Scope

Eight features were planned. Three were removed once it was clear MSI Center does them better:

| # | Feature | Status |
|---|---|---|
| 1 | TDP (PL1 / PL2) — via MSI Center's registry model | ✅ verified on device |
| 2 | ~~Fan presets~~ — **removed**, set it in MSI Center | descoped 2026-08-08 |
| 3 | ~~Battery charge limit~~ — **removed**, set it in MSI Center | descoped 2026-08-08 |
| 4 | ~~RGB LED~~ — **removed**, MSI Center's lighting control is far richer | descoped 2026-08-08 |
| 5 | Desktop / gamepad mode (firmware) | blocked on gate G5 |
| 6 | CPU Boost | ✅ verified on device |
| 7 | OS Power Mode | ✅ verified on device |
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
  power ceilings differ, and a wrong limit is a real write to real firmware.

## Layout

```
src/Shared/      netstandard2.0   IPC contract, device caps, payload encodings. No dependencies.
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

There is no pre-built release yet — the widget has to be built, packaged and signed first, on
whatever Windows machine has Visual Studio 2022 set up (see
[`docs/building-the-widget.md`](docs/building-the-widget.md)). That machine does **not** have to
be the Claw. Signing happens there too, using that machine's certificate private key — the Claw
only ever needs the *output* and a copy of the *public* certificate, never the build tooling or
the key itself.

### Building on one machine, installing on the Claw

Copy this to the Claw — a USB drive or network share is fine:

- `src/Package/AppPackages/McenterLite.Package_<version>_x64_Test/` — the whole folder. It already
  contains the signed `.msix` and the `Dependencies\x64\*.appx` files (VCLibs, the .NET runtime,
  Microsoft.UI.Xaml) that `Install.ps1` needs.
- `src/Package/Install.ps1`
- `src/Package/msi-mcenter-lite.cer` — the exported *public* certificate. Never the private key,
  which stays on the build machine.

One-time, on the Claw itself (a machine setting, so it does not travel with the files above):
enable **Developer Mode** — *Settings → Privacy & security → For developers*. Required for
sideloading.

Then, in an **elevated** PowerShell on the Claw, from wherever the folder above was copied to:

```powershell
.\Install.ps1 -PackagePath ".\McenterLite.Package_<version>_x64_Test\McenterLite.Package_<version>_x64.msix" -CertificatePath ".\msi-mcenter-lite.cer"
```

No .NET SDK, Visual Studio, or any build tooling is needed on the Claw — the helper is
self-contained and the widget's framework dependencies come from the `Dependencies` folder copied
alongside it.

For every later fix: rebuild and sign on the build machine as before, copy the new
`AppPackages/...` folder over, and rerun `Install.ps1` on the Claw —
`-ForceUpdateFromAnyVersion` in the script replaces whatever is already installed.

### Installing on the same machine you built on

If Visual Studio is already on the target device, once a signed `.msix` exists under
`src/Package/AppPackages/`:

```powershell
.\src\Package\Install.ps1
```

This imports the signing certificate to `LocalMachine\TrustedPeople`, stops any running instance,
and installs the package.

### After installing

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

**Nothing here writes to the embedded controller.** Fan control was the only feature that would
have, and it was removed — so the EC duty tables, the ≤ 75 clamp, the monotonic-curve rule and the
Intel IPF escape hatch that used to live here are all gone with it. What remains writes MSI
Center's own registry model and documented Windows power APIs, nothing lower.

The mitigations that do still apply are structural rather than advisory:

- **Every value is clamped in the helper**, never only in the UI. The pipe is ACL'd to all app
  packages, so slider bounds are a convenience and the helper is the enforcement point.
- **Every write is read back** and the actual value returned. A write the hardware ignored is
  reported as a failure, not as success.
- **Power limits are captured before the first write** and restored on uninstall, so the device
  does not keep our numbers forever.
- **The device gate is exact.** Another Claw generation is treated as unsupported rather than close
  enough — the power ceilings differ, and a wrong limit is a real write to real firmware.

**Do not run this and ClawTweaks at the same time.** Both write the same EC.

## Licence

MIT — see [`LICENSE`](LICENSE). All code here is written from scratch; see
[`LICENSE-NOTES.md`](LICENSE-NOTES.md) for why that was the deliberate line held throughout, not
an accident of how the choice turned out.
