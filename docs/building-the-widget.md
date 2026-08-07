# Building the widget and package

Everything under `src/Shared`, `src/Hardware`, `src/Helper`, `src/Probe` and `tests/` builds
anywhere with `dotnet build McenterLite.sln`, including macOS.

**`src/Widget` and the packaging project do not.** They need MSBuild from Visual Studio, and they
are deliberately **not** in `McenterLite.sln` — adding them would break the cross-platform build
that the whole authoring workflow depends on.

## Prerequisites (Windows only)

- Visual Studio 2022 with the **Universal Windows Platform development** workload
- **Windows 11 SDK 10.0.26100**
- Windows 10 version 22000 or later

## Status of this code

> The widget has been **authored but never compiled**. It was written on macOS, where no UWP
> toolchain exists. Expect to fix compile errors on the first VM build — particularly around
> Game Bar API surface, which could not be checked against the real reference assemblies.

Known places to verify first:

1. **`XboxGameBarWidget` construction** in `App.xaml.cs`. The activation-args cast and constructor
   signature vary across `Microsoft.Gaming.XboxGameBar` versions.
2. **Widget visibility.** `MainWidget` uses `Window.Current.VisibilityChanged`, which is plain UWP
   and certain to exist. `XboxGameBarWidget` also exposes its own visibility events; if the plain
   one proves unreliable inside the Game Bar host, switch to those. This drives whether the helper
   pushes fan telemetry, so getting it wrong costs battery, not correctness.
3. **The two manifests.** `src/Widget/Package.appxmanifest` is minimal and exists only so the
   widget project builds standalone. `src/Package/Package.appxmanifest` is the one that ships and
   declares the Game Bar extension plus the full-trust helper. Keep `Identity` and the
   `Application Id` in sync.
4. **Assets.** Neither manifest's referenced PNGs exist yet. Generate placeholders or the package
   will not build:
   `Assets\StoreLogo.png`, `Square150x150Logo.png`, `Square44x44Logo.png`,
   `Wide310x150Logo.png`, `SplashScreen.png`.

## Creating the packaging project

Not created here, because a `.wapproj` cannot be authored blind with any confidence. On the VM:

1. Add a **Windows Application Packaging Project** to the solution as `src/Package`.
2. Add a reference from it to `McenterLite.Widget`.
3. Replace its generated manifest with `src/Package/Package.appxmanifest`.
4. Add the published helper output so it lands at `Helper\McenterLite.Helper.exe` inside the
   package — that path is what the manifest's `windows.fullTrustProcess` extension names.

Publish the helper self-contained first:

```powershell
dotnet publish src\Helper\McenterLite.Helper.csproj -c Release -r win-x64 --self-contained
```

## Signing and installing

Sideloading needs a signed package and a trusted certificate.

```powershell
# One-off: create a self-signed certificate whose subject MATCHES the manifest Publisher exactly.
New-SelfSignedCertificate -Type Custom -Subject "CN=msi-mcenter-lite" `
  -KeyUsage DigitalSignature -FriendlyName "msi-mcenter-lite" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

A mismatch between the certificate subject and the manifest `Publisher` is the most common cause
of a package that builds but refuses to install.

Then build the package in Visual Studio, and install with `src/Package/Install.ps1`.

## What the installer does and does not do

`Install.ps1` installs the package and nothing else. It does **not** copy the helper anywhere and
does **not** create a scheduled task — the signed helper does both itself on first run, behind a
single elevation prompt.

That split is not stylistic. A PowerShell script that copies an executable into LocalAppData and
then registers a HIGHEST-privilege ONLOGON task is behaviourally indistinguishable from
persistence malware; the reference project documents that exact approach being detected as
`Behavior:Win32/Persistence.A!ml` and having its helper quarantined. The same work done in-process
by a signed binary is not that pattern.

## First-run sequence to expect

1. Open the Game Bar (`Win+G`), pin **M Center Lite**.
2. The widget calls `FullTrustProcessLauncher`, which starts the helper from inside the package.
3. That instance sees nothing deployed, relaunches itself elevated with `--setup` — **one UAC
   prompt** — copies itself to `LocalCache\McenterLite\Helper\`, registers the scheduled task, and
   exits.
4. The task starts the deployed helper, which opens the pipe.
5. The widget reconnects on its own. This can take a few seconds after the prompt is accepted.

If the prompt is declined the widget says so and offers a retry; it does not re-prompt in a loop.

## Verifying on the VM before touching the Claw

These are real tests even with no MSI hardware present:

- The widget renders in the Game Bar and its cards appear.
- **The AppContainer can open the helper's pipe.** This is the single most likely thing to fail
  silently. If it does, check the `S-1-15-2-1` ACE in `PipeServer.BuildSecurity`.
- One UAC prompt, not several.
- The scheduled task exists at `\McenterLite\McenterLiteHelper` and survives a reboot.
- **CPU boost and power mode work for real** — they are plain Windows APIs.
  Cross-check with `powercfg /q SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE`.
- `--uninstall` removes the task and the deployed folder.

Run the helper with `--fake-hardware` for everything above; simulated hardware reports
`Supported=false`, so the hardware cards stay hidden and nothing pretends to work.

Logs: `%LOCALAPPDATA%\Packages\<package family>\LocalCache\McenterLite\helper.log`
