# MSI Center M — what was installed, before the uninstall

Captured on the Claw 8 EX AI+ on **2026-08-13**, immediately before uninstalling. This is the
"before" half of a diff: the point is that the after-check can be mechanical instead of a memory
test. Everything here is read-only inventory.

## The finding that matters most: there is no MSI kernel driver

`Get-CimInstance Win32_SystemDriver` matched **nothing** from Micro-Star. Every "MSI" hit is an inbox
Windows driver with a coincidental name:

| Driver | Actually |
|---|---|
| `msisadrv.sys` | Microsoft, ISA driver, boot-start |
| `msiscsi.sys` | Microsoft, iSCSI |
| `rtksdcaxumsi.inf` | Realtek audio |

**`MSI_ACPI` therefore cannot be MSI Center M's to remove.** The class is served by Windows' inbox
ACPI-WMI mapper off `_WDG` in the DSDT — firmware plus in-box Windows, which is exactly what
`--dump-acpi` showed during Phase 0. `MSIWMIACPI2.dll` in MSI's folder is a **user-mode client
wrapper** that calls the same class we call; it is not a provider, and nothing in kernel mode belongs
to MSI at all.

This is the standing gate question for the whole project — *"does `MSI_ACPI` survive an actual
uninstall"* — and this is the strongest evidence available short of doing it. It is still a
prediction until the after-check confirms it.

## Inventory

### Appx packages (2)

| Package | What it is |
|---|---|
| `9426MICRO-STARINTERNATION.64797CC12EF8E_3.0.60630.0_x64__kzh8wxbdkxb8p` | MSI Center M itself |
| `9426MICRO-STARINTERNATION.MSIQuickSettings_2.0.65.0_x64__kzh8wxbdkxb8p` | **MSI's own Game Bar widget** — the thing this project replaces |

### Service (1)

`MSI Foundation Service` → `C:\Program Files (x86)\MSI\MSI Center M\MSIAPService.exe`,
**Running**, start mode **Auto**.

### Scheduled tasks (2)

Both authored by `MSI`, both at the **root** task path, both **RunLevel Highest**.

| Task | Action | Triggers | State |
|---|---|---|---|
| `\MSI_Center_M_Server` | `MSI_Center_M_Server.exe` | logon + **boot** | Running |
| `\MSI_Center_M_Updater` | `"MSI Center M Updater.exe" MSI_Center_M` | logon | Ready |

The updater is the one to watch: a logon-triggered updater running as Highest is exactly the shape of
a thing that reinstalls itself.

### Processes running at capture (2)

- `Gamebar_Widget.exe` (pid 17668) — from the MSIQuickSettings package
- `MSI Center M.exe` (pid 3044)

### Win32 installation

`MSI Center M SDK` 3.0.2606.3001, publisher MSI, uninstaller
`C:\Program Files (x86)\MSI\MSI Center M\unins000.exe`.

Folders: `C:\Program Files (x86)\MSI\` containing `MSI Center M`, `MSI_Center_M`, `NoteBook`.

### Registry roots

- `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M`
- `HKLM\SOFTWARE\WOW6432Node\MSI\MSI NBFoundation Service`
- `HKLM\SOFTWARE\WOW6432Node\MSI\NB`
- `HKLM\SOFTWARE\WOW6432Node\MSI\One Dragon Center`

`Run` keys under HKLM and HKCU carry **nothing** from MSI — it starts entirely through the service
and the two tasks.

## What is NOT MSI's, and must still be there afterwards

Confusing these with MSI's own components is the easiest way to misread the after-check.

| Keep | Why it looks related |
|---|---|
| `ipfsvc`, `dptftcs` | Intel DTT / IPF, both from `C:\WINDOWS\System32\DriverStore`. **Independent fan actors**, and not MSI's to uninstall. |
| `IntelGraphicsSoftwareService`, `IntelDisplayUMService`, `IntelAudioService` | Intel, from DriverStore / the Intel Arc Store app |
| Realtek audio | `RtkAudUService`, `RtkSdcaXu` |

## Risks this inventory raises

1. **MSI bundles Intel IPF extension providers.** `IGCLIPF-16.1.0.257-v4`,
   `Intel_PMT_IPF_Extension_Provider` and `Intel_SoC_Thermal_IPF_Extension_Provider` all live under
   MSI's folder. `ipfsvc` itself is Intel's and survives, but if the uninstaller takes those
   *extensions* with it, Intel's thermal stack may behave differently around the fans. **Fan
   behaviour is the thing to re-check most carefully**, because it is the one place we already know
   two actors compete. Our own Intel graphics features are unaffected — `IIgclProvider` is
   `UnavailableIgcl`, not implemented, so nothing of ours loads anything from that folder.
2. **Audio features may go with it.** `API_Nahimic.dll`, `NoiseCancellation`, `InstallNahimic.exe`.
   Nothing to do with this project, but it is a real change to the machine and worth knowing before
   rather than discovering later.
3. **Whether the uninstaller resets EC state on the way out** — the charge limit, the fan tables and
   the fan-control flag are all firmware state it knows how to write. This is what the hardware
   baseline below is for.

## The hardware baseline

Captured with the probe **before** uninstalling, so that "the uninstall changed the EC" and "the EC
was always like that" stay separable afterwards. Reads only; elevation is needed because `MSI_ACPI`
requires it.

```powershell
$p = ".\src\Probe\bin\x64\Debug\net8.0-windows\McenterLite.Probe.exe"
& $p --device       > Diagnostics\before-device.txt
& $p --fan          > Diagnostics\before-fan.txt
& $p --charge-limit > Diagnostics\before-charge.txt
& $p --power        > Diagnostics\before-power.txt
& $p --controller-mode > Diagnostics\before-controller.txt
```

Re-run the same five after the reboot as `after-*.txt` and diff.
