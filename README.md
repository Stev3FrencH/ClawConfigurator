# msi-mcenter-lite

A lightweight front-end for the settings MSI's M Center owns, targeting **only** the MSI Claw 8 EX
AI+ (Panther Lake, `CG3EM` / board `1T91`), delivered as an Xbox Game Bar widget.

> **The goal is to replace MSI Center M, and it no longer has to be running.** This started as a
> front-end that drove MSI Center M's own registry model, and both shipped hardware features have
> since moved off it: power limits go through `MSI_ACPI` (ACPI-WMI) and controller mode through the
> controller's vendor HID channel. Neither needs MSI Center M running, and neither should need it
> installed. See [docs/hardware-notes.md](docs/hardware-notes.md).
>
> **Not yet proven:** that these paths survive an actual MSI Center M *uninstall*. Everything so far
> was verified with its stack **stopped**, which is not the same thing. MSI Center M is deliberately
> kept installed for now, both to compare against and because that assumption is untested.

> **Status: the widget builds, packages, installs, and runs on the real Claw.** Power limits,
> controller mode, CPU Boost and OS Power Mode are all verified working end-to-end, including
> reflecting changes made outside the widget — Windows Settings, the taskbar flyout, and the
> physical MSI mode button.
>
> **Scope has widened back.** Fan control, battery charge limit and RGB LED were removed
> (2026-08-08) on the reasoning that MSI Center did them better. **That reasoning expired** once the
> plan became to uninstall MSI Center M, and all three are now back — see
> [docs/status.md](docs/status.md) for what each one still carries.
> Intel GPU controls (G6) remain an unimplemented stub.

## Scope

Eight features were planned. Three were descoped on 2026-08-08 and have been brought back, because
the reason for dropping them — "MSI Center does it better" — stops applying once MSI Center M is
uninstalled. All three returned on 2026-08-12.

| # | Feature | Path | Status |
|---|---|---|---|
| 1 | TDP (PL1 / PL2) | `MSI_ACPI.Set_SlaveBattery` | ✅ verified on device, standalone |
| 2 | Fan control | `MSI_ACPI.Get_Fan`/`Set_Fan` + `Set_AP` flag | built; awaiting verification by ear |
| 3 | Battery charge limit | `MSI_ACPI.Get_AP`/`Set_AP` | ✅ verified on device, standalone |
| 4 | RGB LED | vendor HID report `0x0F` | ✅ verified on device, standalone |
| 5 | Controller mode — Gamepad / Desktop | vendor HID `0x24`/`0x26`/`0x27` | ✅ verified on device, standalone |
| 6 | CPU Boost | documented Win32 | ✅ verified on device |
| 7 | OS Power Mode | documented Win32 | ✅ verified on device |
| 8 | Intel GPU controls (IGCL) | `ControlLib.dll` | blocked on gate G6 |

Each returning feature carries the same gating question, and it is the first thing to settle in
each case: **can it be driven with MSI Center M absent?** That question changed the design of both
features shipped so far — in both, a registry value that round-tripped convincingly turned out to
be a *mirror* of the real control surface rather than the surface itself.

Live metrics and per-game profiles are out of scope.

## Design constraints

- **No kernel driver.** Only MSI ACPI-WMI, user-mode vendor HID, Intel IGCL, and documented
  Windows APIs. No WinRing0, inpoutx64, PawnIO, kx.exe, MSR or MCHBAR access. This rules out the
  `kx.exe` MCHBAR route to TDP — which costs nothing, because `MSI_ACPI` reaches the same register
  through firmware Windows already exposes.
- **Independent of MSI Center M, and increasingly so.** The original design accepted MSI Center M
  as a dependency to make the no-driver constraint affordable. That trade turned out to be
  unnecessary: both shipped features now drive firmware directly. **Prefer a firmware path over a
  registry one even when both work** — twice now, the registry value has been a mirror maintained
  by MSI Center M rather than a control surface, which means it vanishes with MSI Center M and can
  lag behind the hardware in the meantime.
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
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

It finds the newest `.msix` beneath its own folder and the `.cer` beside it, so no paths are
needed. Pass `-PackagePath` / `-CertificatePath` to override. The `-ExecutionPolicy Bypass` prefix
matters: a script copied from another machine is blocked by default, and the error
("running scripts is disabled on this system") does not mention the file's origin.

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
powershell -ExecutionPolicy Bypass -File .\src\Package\Install.ps1
```

This imports the signing certificate to `LocalMachine\TrustedPeople`, stops any running instance,
and installs the newest package it finds under `src/Package/AppPackages/`. It re-launches itself
under Windows PowerShell 5.1 and elevates on its own, so a plain prompt is fine — but it still has
to be *loadable*, hence `-ExecutionPolicy Bypass`.

### Seeing the UI on a machine that is not a Claw

The device gate hides every hardware card on anything that is not a Claw 8 EX, leaving only the
Windows power card. To exercise the whole UI on a development machine:

```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnostics\Start-FakeHelper.ps1
```

That runs the helper against simulated hardware so all four cards appear. CPU boost and OS power
mode stay real — the fake layer keeps the Win32 provider deliberately. See the script's help for
what it does to the scheduled task and how it puts it back.

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

### Editing the lighting profiles

The **Lighting** card has four buttons: **Off**, and three profiles. There is no colour picker in
the widget on purpose — the profiles are plain text files you edit outside it. Paste this into the
Explorer address bar:

```
%LOCALAPPDATA%\Packages\McenterLite_xq4frxrkckec6\LocalCache\McenterLite\Lighting
```

`McenterLite_xq4frxrkckec6` is the package family name. It is a hash of the `Identity` name and
publisher in `src/Package/Package.appxmanifest`, so it is the same on every machine and across
versions — but if you have changed either, `(Get-AppxPackage McenterLite).PackageFamilyName` prints
yours. `helper.log` is one folder up, in `McenterLite\`.

The folder contains `Profile_1.txt`, `Profile_2.txt`, `Profile_3.txt` and a `README.txt` with the
full reference. Edit a profile, then tap it in the widget — **the file is read at the moment you
tap**, so nothing needs restarting, and tapping a profile that is already selected re-reads it.
`Name` becomes the button label.

**To undo a bad edit: delete the file — or empty it and save — then tap that profile.** The default
comes back and the file is rewritten, with nothing to restart. Nothing you can type here breaks the
widget or the controller: a setting that cannot be read is skipped and the previous value kept, and
`helper.log` one folder up names everything it ignored.

The three defaults reproduce the profiles MSI Center M had configured, so the lights look the same
after switching over. Full syntax, the colour formats, and what the hardware actually stores are in
[docs/lighting-profiles.md](docs/lighting-profiles.md).

### Editing the fan profile

The **Fans** card has two buttons — **Auto** and your custom profile. Pressing one applies it.

- **Auto** hands the fans back to the firmware's own curve, and puts MSI's stock table back at the
  same time.
- **Custom** takes the fans over and runs the curve in `Custom.txt`, read at the moment you press
  the button.

```
%LOCALAPPDATA%\Packages\McenterLite_xq4frxrkckec6\LocalCache\McenterLite\Fan
```

The device has **two fans**, and each holds an idle duty used below 47 °C plus one duty at each of
47, 50, 57, 64, 71 and 78 °C. Duty is a percentage. **The temperatures are fixed** — they are not
editable here or in MSI Center M, so only the duties are yours. Use `Fan =` to set both fans at
once, or `Fan1` and `Fan2` to set them apart.

> [!WARNING]
> **A duty of 0 stops that fan**, including under load. The firmware enforces no floor — this was
> measured on the device, with both tachometers reading zero — and MSI Center M permits the same
> thing. The widget shows a warning whenever `Custom.txt` contains a 0 — before you press anything,
> not after — and the log records it, but neither refuses. If you did not mean it, press **Auto**.

Recovery is the same as for lighting: delete `Custom.txt`, or empty it and save, then press
**Custom** again. A setting that cannot be read is skipped and the previous value kept, and
`helper.log` two folders up names everything it ignored, on lines starting `Fan profile:`.

MSI Center M, while it is still installed, owns the same fans and does not know about us — if a
curve stops behaving, press the button again. The card reads which profile is running from the
firmware itself, so if MSI Center M has taken the fans back, the card will say **Auto** rather than
keep claiming your curve.

## Running

For development and discovery, without installing the packaged app — against simulated hardware,
on any Windows machine:

```
McenterLite.Helper.exe --fake-hardware
```

Discovery and verification, on the Claw, elevated:

```
McenterLite.Probe.exe --device            # confirm the model gate
McenterLite.Probe.exe --power             # CPU boost + power mode (real, works anywhere)
McenterLite.Probe.exe --dump-acpi C:\acpi
McenterLite.Probe.exe --wmi-classes MSI
McenterLite.Probe.exe --acpi-get Get_AP   # read-only MSI_ACPI call, e.g. the charge limit
McenterLite.Probe.exe --hid-list
McenterLite.Probe.exe --hid-watch 120     # live vendor-HID traffic; press the MSI button
McenterLite.Probe.exe --controller-mode
```

Read commands are safe; only `set-*` changes anything. `--hid-watch` is how the controller-mode
protocol was decoded and is the tool to reach for on the remaining gates — it prints every frame
the controller emits, which is where the RGB lead in
[docs/status.md](docs/status.md#next-up) came from.

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

**This writes real firmware.** An earlier version of this section claimed nothing here touched the
embedded controller; that was true only for the brief window when power limits went through MSI
Center's registry model and fan control had been removed. It is **not** true now:

- **Power limits** go to the EC through `MSI_ACPI.Set_SlaveBattery`.
- **Controller mode** writes the controller's own firmware over the vendor HID channel.
- **Charge limit** goes to the EC through `MSI_ACPI.Set_AP`.
- **Fan control** writes the EC's duty tables through `MSI_ACPI.Set_Fan`, and hands the fans to
  them with a flag on `Set_AP`. This is the riskiest thing here: it is the only feature that can
  make the device run hotter, and **the firmware enforces no duty floor** — a table of zeros stops
  both fans, which was measured rather than assumed. The ≤ 75 duty clamp this project long carried
  turned out to be [wrong for this model](docs/hardware-notes.md); duty is a plain 0–100 percentage.

All are constrained to registers whose meaning was established by measurement on this exact model,
and all are read back after every write.

The mitigations that apply are structural rather than advisory:

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
