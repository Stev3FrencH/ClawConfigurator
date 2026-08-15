**The app is called Claw Configurator.** 

A replacement for MSI Center M on the MSI Claw 8 EX AI+ (Panther Lake, `CG3EM` / board `1T91`),
delivered as an Xbox Game Bar widget. **MSI Center M is not required, and is not installed on the
development device** — though a Windows install that has never seen it needs it once, briefly, to
register the WMI class this app calls. See [`docs/install.md`](docs/install.md#before-you-start).

> **Status: released as `0.2.0.40`, verified on device across cold boots.** Seven features work,
> the first real Release build is cut and signed, and install and uninstall are scripted and
> documented for someone who did not build it — see [`docs/install.md`](docs/install.md).
>
> **A clean Windows needs MSI Center M installed once, then removed**, before this app has anything
> to drive — it is what registers the `MSI_ACPI` WMI class. The registration outlives the uninstall;
> MSI Center M itself is not needed and is not installed here. See
> [`docs/install.md`](docs/install.md#before-you-start).

## Scope

Eight features were planned. Three were descoped on 2026-08-08 and brought back on 2026-08-12,
because the reason for dropping them — "MSI Center does it better" — stopped applying once MSI
Center M was going to be uninstalled. Two more were added afterwards that were never planned at all,
both discovered while removing MSI Center M.

| # | Feature | Path | Status |
|---|---|---|---|
| 1 | TDP (PL1 / PL2) | `MSI_ACPI.Set_SlaveBattery` | ✅ verified, incl. cold boot |
| 2 | Fan control | `MSI_ACPI.Get_Fan`/`Set_Fan` + `Set_AP` flag | ✅ verified, probe and widget |
| 3 | Battery charge limit | `MSI_ACPI.Get_AP`/`Set_AP` | ✅ verified on device |
| 4 | RGB LED | vendor HID report `0x0F` | ✅ verified on device |
| 5 | Controller mode — Gamepad / Desktop | vendor HID `0x24`/`0x26`/`0x27` | ✅ verified on device |
| 6 | CPU Boost | documented Win32 | ✅ verified on device |
| 7 | OS Power Mode | documented Win32 | ✅ verified on device |
| 8 | Intel GPU controls (IGCL) | `ControlLib.dll` | unimplemented, blocked on gate G6 |
| 9 | Performance mode — AI Engine / Endurance / Manual | `MSI_ACPI.Set_AP` sub-fn 0, byte 3 | ✅ verified, incl. cold boot |
| 10 | The MSI Center hardware button re-mapper | `MSI_Event` `0x220029` | ✅ verified on device |

**Feature 9 was not a feature at first — it was a bug.** The mode byte *gates* the power limits:
with the wrong value, PL1/PL2 are accepted, read back correctly, and ignored. MSI Center M's
service had been setting it at every boot, so removing MSI Center M broke TDP in a way no reboot
would reveal, because the embedded controller keeps the value across warm boots and resets it only
on a true power cycle. Once understood it was cheap to expose properly.

**Feature 10 was never broken.** The button on the top of the device stopped doing anything when
MSI Center M was uninstalled, and it turned out nothing was listening — the firmware raises the
same event either way. It now toggles the RTSS overlay by default, and is configurable.

Every feature had to clear the same gate, and it is the first thing to settle in each case: **can
it be driven with MSI Center M absent?** That question changed several designs. Twice, a registry
value that round-tripped convincingly turned out to be a *mirror* of the real control surface
rather than the surface itself — it vanishes with MSI Center M, and lags the hardware while it is
there.

Live metrics and per-game profiles are out of scope.

## The name

The project began as a thin front-end over settings **MSI Center M** owned, driving its registry
model rather than the hardware. "M Center Lite" described that accurately, and `msi-mcenter-lite`
followed from it.

It stopped being true. Every feature now drives firmware directly — ACPI-WMI and vendor HID — MSI
Center M has been uninstalled since 2026-08-13, and the app is no longer *lite* anything: it does
things MSI Center M does not, like the fan duty floor and the repurposed hardware button. A name
that describes a dependency the project spent its life removing is a name that misleads.

So on **2026-08-14** the app became **Claw Configurator**, including its package identity. Three
consequences worth knowing:

- **The Package Family Name changed** to `ClawConfigurator_xq4frxrkckec6`. Windows treats a changed
  identity as a *different app*, so an older M Center Lite install does not upgrade — uninstall it
  first, or you get both. Settings and profiles start fresh under the new identity.
- **The repository keeps its name.** Renaming it would break every existing clone URL and inbound
  link to buy a tidier name, and the two are easy enough to hold apart: `msi-mcenter-lite` is where
  the code lives, Claw Configurator is what installs.
- **Two internal names were deliberately left alone**, both documented where they appear: the
  widget assembly `McenterLite.Widget`, because the Appx tooling rewrites the manifest's
  `EntryPoint` to `<AssemblyName>.App` and renaming the assembly without the namespace produces a
  package that builds cleanly and cannot launch; and the helper's single-instance mutex, which is
  the interlock stopping an old and a new build from driving the embedded controller at once, and
  therefore has to match across the very versions the rename separates.

## Design constraints

- **No kernel driver.** Only MSI ACPI-WMI, user-mode vendor HID, Intel IGCL, and documented
  Windows APIs. No WinRing0, inpoutx64, PawnIO, kx.exe, MSR or MCHBAR access. This rules out the
  `kx.exe` MCHBAR route to TDP — which costs nothing, because `MSI_ACPI` reaches the same register
  through firmware Windows already exposes.
- **Independent of MSI Center M — settled, not aspirational.** The original design accepted MSI
  Center M as a dependency to make the no-driver constraint affordable. That trade turned out to be
  unnecessary: every feature drives firmware directly, and MSI Center M has been uninstalled since
  2026-08-13 with all of them still working. `MSI_ACPI` comes from the ACPI tables via Windows' own
  WMI mapper, not from anything MSI ships. **The one exception is registration, not operation**: on a
  Windows install that has never had MSI Center M, the class is not registered until MSI Center M is
  installed once. That is a one-time setup step, after which MSI Center M can be removed and the
  registration stays — see [`docs/install.md`](docs/install.md#before-you-start).
  **Prefer a firmware path over a registry one even when
  both work** — twice the registry value proved to be a mirror maintained by MSI Center M rather
  than a control surface, so it vanishes with MSI Center M and lags the hardware while it is there.
  See [`Diagnostics/msi-center-m-after.md`](Diagnostics/msi-center-m-after.md).
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

> **Installing it, rather than developing it?** Take the packaged build from
> [Releases](https://github.com/Stev3FrencH/msi-mcenter-lite/releases) and follow
> [`docs/install.md`](docs/install.md) — written for someone who did not build this, including what
> the certificate import actually grants and how to fix a failed framework dependency. It carries
> the uninstall too. **The rest of this section is about building and installing from source**, and
> assumes you have the repository and the signing key.

Built from source, the widget has to be built, packaged and signed first, on
whatever Windows machine has Visual Studio 2022 set up (see
[`docs/building-the-widget.md`](docs/building-the-widget.md)). That machine does **not** have to
be the Claw. Signing happens there too, using that machine's certificate private key — the Claw
only ever needs the *output* and a copy of the *public* certificate, never the build tooling or
the key itself.

### Building on one machine, installing on the Claw

Copy this to the Claw — a USB drive or network share is fine:

- `src/Package/AppPackages/McenterLite.Package_<version>_Test/` — **the whole folder**, not just the
  package inside it. It already holds the signed `.msixbundle` and the `Dependencies\x64\*.appx`
  files (VCLibs, the .NET Native framework and runtime, Microsoft.UI.Xaml) that `Install.ps1` needs.
  A Debug build names the folder `..._Debug_Test`; Release omits the word.
- `src/Package/Install.ps1` and `src/Package/Uninstall.ps1`
- `src/Package/msi-mcenter-lite.cer` — the exported *public* certificate. Never the private key,
  which stays on the build machine.

Two one-time things on the Claw itself, neither of which travels with the files above:

- Enable **Developer Mode** — *Settings → Privacy & security → For developers*. Required for
  sideloading.
- On a Windows install that has never had **MSI Center M** on it, install it from MSI's support
  page, reboot when prompted, and then uninstall it immediately. That registers `MSI_ACPI`, which
  everything here calls; the registration survives the uninstall. Already had it at some point?
  Nothing to do. Full detail in [`docs/install.md`](docs/install.md#before-you-start).

Then, in an **elevated** PowerShell on the Claw, from wherever the folder above was copied to:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

It finds the newest package beneath its own folder and the `.cer` beside it, so no paths are
needed. Pass `-PackagePath` / `-CertificatePath` to override. **Keep the package inside its folder**
— the dependencies are located relative to it, so moving it out on its own silently leaves them
behind. The `-ExecutionPolicy Bypass` prefix
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

### Giving it to someone else

Hand over three things — the `AppPackages/...` folder, `Install.ps1` and `Uninstall.ps1`, and
`msi-mcenter-lite.cer`. Never the `.pfx`: that is the signing key itself, and anyone holding it can
sign anything in this name.

Tell them these five things, because none of them are discoverable and the first one deserves an
informed decision rather than a click-through:

1. **Importing the certificate means trusting a stranger's signing key.** This app is signed with a
   self-signed certificate rather than one from a certificate authority, so Windows will not install
   it until the machine is told to trust that key. `Install.ps1` does that, with elevation, by
   importing the `.cer` into `LocalMachine\TrustedPeople`. From then on the machine will accept
   **anything** signed by that key, not just this app, until the certificate is removed or it expires
   on 2027-08-11. `Uninstall.ps1 -RemoveCertificate` removes it again. It goes into `TrustedPeople`
   and never into `Root` — a root CA would be trusted for every purpose, including impersonating web
   sites.
2. **Sideloading has to be allowed.** *Settings → Privacy & security → For developers → Developer
   Mode*. Without it the install fails on policy grounds and the error does not mention sideloading.
3. **This is an MSI Claw 8 EX AI+ build.** On any other machine the device gate hides every hardware
   card and only CPU Boost and OS Power Mode do anything, so it is worth saying up front rather than
   letting someone install it and find a near-empty widget.
4. **The one elevation prompt on first run is not optional.** The helper uses it to deploy itself and
   register its scheduled task; decline it and every hardware control stays dead with no obvious
   reason why.
5. **A Windows that has never had MSI Center M on it needs it once.** Install it from MSI's support
   page, reboot when prompted, uninstall it straight away — that is what registers the `MSI_ACPI`
   class, and the registration outlives the uninstall. Without it the app installs and opens with
   every hardware card dead, which looks like a broken build rather than a missing prerequisite.

To uninstall, they run `Uninstall.ps1` — not *Settings → Apps*, which does only half the job and
leaves the device on this app's settings. See [Uninstalling](#uninstalling).

### Seeing the UI on a machine that is not a Claw

The device gate hides every hardware card on anything that is not a Claw 8 EX, leaving only the
Windows power card. To exercise the whole UI on a development machine:

```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnostics\Start-FakeHelper.ps1
```

That runs the helper against simulated hardware so all five hardware cards appear — Controller,
Power, Battery, Fans and Lighting. CPU boost and OS power
mode stay real — the fake layer keeps the Win32 provider deliberately. See the script's help for
what it does to the scheduled task and how it puts it back.

### After installing

1. Open the Game Bar (**Win+G**) and pin **Claw Configurator**.
2. Accept the one elevation prompt on first run — the helper uses it to deploy itself and
   register a scheduled task. Hardware controls do not work until this is accepted.
3. The widget reconnects on its own a few seconds after the prompt.

Logs land at `%LOCALAPPDATA%\Packages\<package family>\LocalCache\ClawConfigurator\helper.log`.

**What a fresh install changes.** Your power limits, charge limit and controller mode are read off
the hardware and left exactly as they are. Two things are set once, and are then yours to change:

| | First run applies | Why not "leave it alone" |
|---|---|---|
| Fans | **Auto** | An install otherwise inherits whatever curve and control flag the last owner left behind — including one you can no longer see or change |
| Lighting | **profile 1**, seeded as Purple | The controller keeps lighting in RAM and forgets it on a power cycle, so writing nothing leaves the LEDs on a firmware default while the card claims something else |

**Controller mode is never written except on uninstall.** The physical MSI button switches the same
mode and the firmware handles it alone, so this app does not assert it at startup or on install — a
mode of ours would silently undo the button. Uninstall is the exception, because being left in
desktop-mouse mode with the app that switched you gone is a state worth rescuing you from.

### Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
```

That does the whole thing in the right order, closes the widget so it cannot undo the first step
half way through, and saves your profile files and the final `helper.log` to a folder on the Desktop
before the app removal deletes them. Add `-RemoveCertificate` to also stop trusting the signing key,
or `-SkipBackup` if you want nothing kept.

The rest of this section is what the script is doing, and is worth reading if you ever have to do it
by hand.

**Order matters, and it is the opposite of what you would guess.** Run the helper's `--uninstall`
**first**, then remove the app:

```powershell
& "$env:LOCALAPPDATA\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Helper\ClawConfigurator.Helper.exe" --uninstall
```

That puts every feature back to its default, unregisters the scheduled task and removes the deployed
copy. Then remove **Claw Configurator** from *Settings > Apps*.

Doing it the other way round cannot work: the deployed helper and its settings both live inside the
package's `LocalCache`, so removing the app first deletes the executable that would do the restore.
That leaves the scheduled task orphaned against a missing file and the device on whatever limits
were last set, with nothing left able to change them.

> **Don't open the Game Bar between the two steps.** The widget would redeploy the helper and
> re-register its task, undoing the first step. The restore clears the saved settings as well as
> writing the hardware, so nothing would be re-applied — but you would be back to having an app to
> uninstall.

To see what the restore does **without** uninstalling anything, close the Game Bar and run the same
code path on its own:

```powershell
& "$env:LOCALAPPDATA\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Helper\ClawConfigurator.Helper.exe" --restore
```

It applies every default, **forgets your saved choices**, and exits with the app still installed —
so the next time the helper starts it re-applies nothing and the defaults stand. Treat it as a reset:
your power limits, charge limit, and fan and lighting selections are cleared, though the profile
*files* themselves are untouched.

(`Test-Helper.ps1 -Restore` sends the same message over the pipe, but the pipe serves one client at a
time and the widget never releases it, so that route only works if the Game Bar has not been opened
since the helper started.)

What the restore puts back — chosen values, not whatever happened to be there before:

| | Default |
|---|---|
| Power limits | 17 W / 19 W |
| Battery charge limit | 100% — charge to full |
| Fans | Auto: MSI's factory table, fans handed back to the firmware |
| Controller mode | Gamepad |
| CPU boost | On |
| OS power mode | Balanced |
| Lighting | **left alone** — it lives in the controller's RAM and a power cycle clears it anyway |

After `--restore` the saved selections are gone, so the next helper start looks like a fresh install
and applies the first-run defaults above — including the lighting profile. "Leaves the lights alone"
is true at the moment of the restore, not forever.

This only applies to the MSI Claw 8 EX AI+ (see [Scope](#scope)) — on any other device the
hardware-specific cards stay hidden, and only CPU Boost and OS Power Mode do anything.

### Editing the lighting profiles

The **Lighting** card has four buttons: **Off**, and three profiles. There is no colour picker in
the widget on purpose — the profiles are plain text files you edit outside it. Paste this into the
Explorer address bar:

```
%LOCALAPPDATA%\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Lighting
```

`ClawConfigurator_xq4frxrkckec6` is the package family name. It is a hash of the `Identity` name and
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
%LOCALAPPDATA%\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Fan
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
curve stops behaving, press the button again. Setting **its** fans to Auto does **not** take yours
away, measured on device 2026-08-13; its UI simply stops agreeing with the machine. The card reads
which profile is running from the firmware itself and re-reads it every few seconds while the widget
is open, so if anything ever does take the fans back, the card changes to **Auto** rather than going
on claiming your curve.

### The hardware button

The MSI button appeared to die with MSI Center M. **It didn't.** It raises a firmware event and does
nothing else — it reports, and leaves the decision to software. MSI Center M was the only thing
subscribed, so removing it left the event firing into an empty room.

Put one action name in the file, edit and press — it is read at the moment of the press:

```
%LOCALAPPDATA%\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Button\Button.txt
```

| | |
|---|---|
| `none` | Do nothing. **The default** — a fresh install gains no new behaviour. |
| `rtss-overlay` | Toggle RivaTuner Statistics Server's on-screen display. |
| `fan-profile` | Cycle the fan profile: Auto, custom, Auto… |
| `performance-mode` | Cycle the performance mode: Endurance, AI Engine, Manual. |
| `lighting` | Cycle the lighting: off, then each profile in turn. |
| `controller-mode` | Toggle the controller between Gamepad and Desktop. |

Every press is logged whatever the outcome, so "nothing happened" is diagnosable — look for lines
starting `Button` in `helper.log` two folders up. A press that never reaches the log is a different
problem from one that reaches it and fails.

> **`rtss-overlay` does not send RTSS's hotkey**, though RTSS has one and doing so would have been
> simpler. It calls the same function RTSS's own hotkey handler calls, so nothing reaches the input
> layer. A synthesised keystroke goes into the *system* input queue, where the foreground game
> receives it too, flagged as injected — and this button exists to be pressed while games are
> running, so that exposure would be constant rather than occasional. For the same reason there is
> deliberately no "send a hotkey" action.

## Running

For development and discovery, without installing the packaged app — against simulated hardware,
on any Windows machine:

```
ClawConfigurator.Helper.exe --fake-hardware
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
- **Performance mode** writes the low nibble of `Set_AP` sub-function 0, byte 3. This is the byte
  that *gates* the power limits, which is why it is a feature at all — read-modify-write of one
  nibble, because the rest of that byte is not ours.
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
- **Uninstalling restores every feature to a known default**, so the device does not keep our
  numbers forever. See [Uninstalling](#uninstalling) — the restore replaces an earlier scheme that
  replayed captured "original" values, which on this machine meant replaying whatever MSI Center M
  held at one arbitrary moment.
- **The device gate is exact.** Another Claw generation is treated as unsupported rather than close
  enough — the power ceilings differ, and a wrong limit is a real write to real firmware.

**Do not run this and ClawTweaks at the same time.** Both write the same EC.

## Licence

MIT — see [`LICENSE`](LICENSE). All code here is written from scratch; see
[`LICENSE-NOTES.md`](LICENSE-NOTES.md) for why that was the deliberate line held throughout, not
an accident of how the choice turned out.
