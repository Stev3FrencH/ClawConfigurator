# Installing Claw Configurator

A Game Bar widget for the MSI Claw 8 EX AI+: fan control, power limits, battery charge limit,
lighting, controller mode, and the hardware button.

Download `ClawConfigurator-<version>.zip` from
[Releases](https://github.com/Stev3FrencH/msi-mcenter-lite/releases) and extract it anywhere.

---

## Before you start

**This is a Claw 8 EX AI+ (`CG3EM`) build.** On any other machine the device gate hides every
hardware card and only *CPU Boost* and *OS Power Mode* do anything. Nothing breaks — but you would
be installing a nearly empty widget, so it is worth knowing first.

**Turn on Developer Mode**, at *Settings → Privacy & security → For developers*. This app is
sideloaded rather than installed from the Store, and without this the install fails on policy
grounds with an error that does not mention sideloading.

**Understand what the certificate does, because it is the one real decision here.** The app is
signed with a self-signed certificate rather than one from a certificate authority, so Windows will
refuse it until the machine is told to trust that key. `Install.ps1` does this, with elevation, by
importing `msi-mcenter-lite.cer` into `LocalMachine\TrustedPeople`.

From then on your machine accepts **anything** signed by that key — not just this app — until the
certificate is removed or it expires on **2027-08-11**. That is a genuine extension of trust to
someone else's signing key, and you should be comfortable with it before continuing.
`Uninstall.ps1 -RemoveCertificate` revokes it later.

Two things that make it narrower than it sounds: the certificate goes into `TrustedPeople` and never
into `Root`, so it is trusted for signing apps and *not* for impersonating web sites; and only the
public `.cer` is ever distributed — the private key that does the signing never leaves the build
machine.

---

## Install

Open **PowerShell as Administrator**, `cd` to the extracted folder, and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1
```

`-ExecutionPolicy Bypass` matters: a script that arrived from another machine is blocked by default,
and the error ("running scripts is disabled on this system") does not mention the file's origin.

The script finds the newest package beneath its own folder and the `.cer` beside it, so no paths are
needed. It imports the certificate, stops anything already running, installs the framework
dependencies, and installs the app.

> **Keep the folder together.** `Install.ps1` locates the framework dependencies *relative to the
> package* — `<folder holding the .msixbundle>\Dependencies\x64`. Move the `.msixbundle` out on its
> own and that lookup silently finds nothing; the install then proceeds without dependencies and
> fails on a clean machine with the error in [the dependencies section](#if-the-install-fails-on-a-framework-dependency).

### First run

1. Open the Game Bar with **Win+G** and pin **Claw Configurator**.
2. **Accept the one elevation prompt.** The helper uses it to deploy itself and register its
   scheduled task. Decline it and every hardware control stays dead with no obvious reason why.
   You should see this prompt **once, ever** — if it returns on later launches, something is wrong.
3. The widget reconnects on its own a few seconds later.

Logs live at:

```
%LOCALAPPDATA%\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\helper.log
```

### What a fresh install changes

Your power limits, charge limit and controller mode are read off the hardware and left alone. Two
things are set once, then yours to change:

| | First run applies | Why not leave it alone |
|---|---|---|
| Fans | **Auto** | Otherwise you inherit whatever curve and control flag the last owner left behind — possibly one you can no longer see or change |
| Lighting | **Profile 1** (Purple) | The controller keeps lighting in RAM and forgets it on a power cycle, so writing nothing leaves the LEDs on a firmware default while the card claims something else |

---

## If the install fails on a framework dependency

The app needs five framework packages. **All five ship inside the download**, at
`McenterLite.Package_<version>_Test\Dependencies\x64\` — there is nothing to fetch from the
internet, and `Install.ps1` installs them for you. This section is for when that does not happen.

The failure is `Add-AppxPackage` reporting **`0x80073CF3`**, saying the package depends on a
framework that could not be found and naming one of the packages below. It reads like a corrupt
download; it almost never is.

**Check what you already have:**

```powershell
Get-AppxPackage Microsoft.NET.Native.Framework.2.2, Microsoft.NET.Native.Runtime.2.2,
                Microsoft.UI.Xaml.2.8, Microsoft.VCLibs.140.00, Microsoft.VCLibs.140.00.UWPDesktop |
    Select-Object Name, Version
```

**The names on disk are not the names Windows uses**, which is the single most common reason people
conclude a dependency is missing when it is present:

| Windows knows it as | The file is called | Needs at least | On a clean Win 11? |
|---|---|---|---|
| `Microsoft.NET.Native.Framework.2.2` | `Microsoft.NET.Native.Framework.2.2.appx` | 2.2.27912.0 | **Usually absent** |
| `Microsoft.NET.Native.Runtime.2.2` | `Microsoft.NET.Native.Runtime.2.2.appx` | 2.2.27328.0 | **Usually absent** |
| `Microsoft.UI.Xaml.2.8` | `Microsoft.UI.Xaml.2.8.appx` | 8.2501.31001.0 | Sometimes |
| `Microsoft.VCLibs.140.00` | `Microsoft.VCLibs.x64.14.00.appx` | 14.0.33519.0 | Usually present |
| `Microsoft.VCLibs.140.00.UWPDesktop` | `Microsoft.VCLibs.x64.14.00.Desktop.appx` | 14.0.33728.0 | Usually present |

The two **.NET Native** packages are the ones a fresh machine reliably lacks. They are the runtime
the widget is compiled against, and they do not arrive with Windows.

**The fix — install the frameworks yourself, then the app:**

```powershell
cd <the extracted folder>
Get-ChildItem .\McenterLite.Package_0.2.0.40_Test\Dependencies\x64 -Filter *.appx |
    ForEach-Object { Add-AppxPackage -Path $_.FullName }

Add-AppxPackage -Path .\McenterLite.Package_0.2.0.40_Test\McenterLite.Package_0.2.0.40_x64.msixbundle
```

If a single dependency is the problem, install just that one `.appx` and retry `Install.ps1`.

Two things that are **not** failures: a dependency reporting it is already installed at an equal or
newer version, and a version *higher* than the table above. The manifest asks for a minimum, not an
exact match.

### Other ways the install goes wrong

| Symptom | Cause | Fix |
|---|---|---|
| "running scripts is disabled on this system" | The script came from another machine and is blocked | Use the `-ExecutionPolicy Bypass` form above |
| Fails citing trust, or a signature error | The certificate was not imported | Confirm `msi-mcenter-lite.cer` sits beside `Install.ps1`; check with `Get-ChildItem Cert:\LocalMachine\TrustedPeople` |
| Fails on policy / sideloading | Developer Mode off | *Settings → Privacy & security → For developers* |
| **`0x80073CFB`** | Same version already installed with different contents | `Install.ps1` handles this by removing the old one first; it preserves your `settings.json` |
| Widget says **"could not reach the helper"** forever | The elevation prompt was declined, or the helper cannot start | Check whether `helper.log` exists at all — if it is absent the helper never started, which is not a settings problem |
| Elevation prompt on **every** launch | The deployed helper is being judged stale every start | A defect; report it with `helper.log` |

---

## Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
```

**Use the script rather than *Settings → Apps*.** Removing the app on its own does only half the
job, and the half it skips is the half that matters.

**The required order is the opposite of the obvious one.** The helper has to restore your device
*before* the app is removed, because the helper and its settings both live inside the package's
own storage. Remove the app first and you have deleted the executable that would have put things
back — leaving a scheduled task pointing at a missing file, and your machine on whatever power
limits, fan curve and charge limit were last set, with nothing installed able to change them.

Doing it in the wrong order looks like it worked.

The script handles all of it: it closes the widget first (an open Game Bar would redeploy the helper
mid-uninstall), runs the restore, removes the app, and checks nothing was left behind.

**It also rescues your files.** Removing the app deletes its storage, which takes your lighting and
fan profile *files* with it, along with the log of the uninstall that just ran. The script copies
them to a timestamped folder on your Desktop first. Fresh ones are seeded on the next install, so
nothing breaks — but hand-edited curves and colours would otherwise be gone.

| Option | Effect |
|---|---|
| *(none)* | Restore, back up to the Desktop, remove the app, leave the certificate trusted |
| `-RemoveCertificate` | Also stop trusting `CN=msi-mcenter-lite`. Use this if you are removing the app for good |
| `-SkipBackup` | Keep nothing |
| `-BackupPath <dir>` | Back up somewhere other than the Desktop |

The certificate is **kept by default**, because reinstalling needs it again and removing it costs
you the import step next time. There is no harm in removing it — the app is already installed by
the time it matters, and nothing checks it again until the next install.

### What the restore puts back

Chosen defaults, not whatever happened to be set before:

| | Default |
|---|---|
| Power limits | 17 W / 19 W |
| Battery charge limit | 100% — charge to full |
| Fans | Auto: MSI's factory table, handed back to the firmware |
| Controller mode | Gamepad |
| CPU boost | On |
| OS power mode | Balanced |
| Lighting | **Left alone** — it lives in the controller's RAM and a power cycle clears it anyway |

### If something is left behind

An uninstall that went wrong in the old order leaves a scheduled task pointing at a deleted file,
which then fails at every logon forever:

```powershell
Get-ScheduledTask -TaskName ClawConfiguratorHelper
Unregister-ScheduledTask -TaskName ClawConfiguratorHelper -TaskPath \ClawConfigurator\
```

If the app is already gone, the restore cannot be run — the helper that performs it lived inside the
package. Reinstall, then uninstall properly with the script.
