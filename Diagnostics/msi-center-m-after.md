# MSI Center M — after the uninstall

The other half of [`msi-center-m-before.md`](msi-center-m-before.md). Uninstalled on **2026-08-13**:
the MSI Center M Appx, the MSI Quick Settings Game Bar widget, and the MSI Center M SDK, followed by
a full shutdown and reboot.

## The gate question, answered: `MSI_ACPI` survived

`Get-CimClass -Namespace root\wmi MSI_ACPI` returns the class with all 38 methods — `Get_AP`,
`Set_AP`, `Get_Fan`, `Set_Fan`, `Get_SlaveBattery`, `Set_SlaveBattery` and the rest, unchanged.

The stronger evidence is the helper's own elevated start after the reboot, because it does not ask
whether the class exists — it calls every method for real, on a machine with no MSI Center M on it:

```
11:52:23  Starting. role=service elevated=True
11:52:23  Detected: Claw 8 EX AI+ CG3EM
11:52:24  Power limits: Wmi.
11:52:24  Controller mode: firmware (vendor HID).
11:52:24  Charge limit: MSI_ACPI Get_AP/Set_AP.
11:52:24  Lighting: vendor HID profile block (RAM).
11:52:24  Fan control: MSI_ACPI Get_Fan/Set_Fan, two fans.
11:52:24  Re-applied PL1=15W PL2=17W.
11:52:24  Re-applied charge limit = 80%.
11:52:25  Re-applied lighting profile 1 'Purple'.
11:52:25  Re-applied fan profile 'Auto'; fans left to the firmware.
11:52:25  Re-applied CPU boost = False.
```

Followed by live use — all four lighting profiles, and the fans moved to Custom and back to Auto.

**Both registry mirrors lost to the firmware paths**, which is the specific outcome that unblocks
their deletion: `Power limits: Wmi` not `RegistryMirror`, and `Controller mode: firmware (vendor
HID)` not the mirror. Neither had a mirror left to read, and neither needed one.

This was predicted from the driver evidence in the before-record — nothing MSI ships runs in kernel
mode, so the class comes from `_WDG` in the DSDT through Windows' own ACPI-WMI mapper. The prediction
held.

## Removed cleanly

| Gone | Check |
|---|---|
| Both Appx packages | no `9426MICRO-STAR*` remains |
| `MSI Foundation Service` | no service, no `MSIAPService.exe` |
| Every MSI process | none running |
| `C:\Program Files (x86)\MSI\MSI Center M\` | the whole binary folder, including `MSIWMIACPI2.dll` |
| Uninstall entries | no MSI/Micro-Star publisher rows |

Intel's thermal stack is untouched and still running, as it should be — `ipfsvc` and `dptftcs` are
Intel's, from DriverStore, and were never MSI's to remove.

## Left behind

All inert: nothing is running, and the two tasks point at executables that no longer exist.

### Scheduled tasks (2) — the uninstaller did not remove these

| Task | Target | Target exists? |
|---|---|---|
| `\MSI_Center_M_Server` | `…\MSI Center M\MSI_Center_M_Server.exe` | **No** |
| `\MSI_Center_M_Updater` | `…\MSI Center M\MSI Center M Updater.exe` | **No** |

Both now sit in `Ready` rather than `Running`. They will fail silently at logon and boot. The
updater is the one worth removing on principle: logon-triggered, `RunLevel Highest`, and its whole
job is fetching and installing MSI software.

### Folders — logs only, no binaries

- `C:\Program Files (x86)\MSI\MSI_Center_M\` — its own `Log`, `Battery`, `Game Library`,
  `Media Gallery` and `OSD` folders, all `.log` and `.txt`
- `C:\Program Files (x86)\MSI\NoteBook\MSI NBFoundation Service\WmiAcpi2.log`

### Registry (4 roots under `HKLM\SOFTWARE\WOW6432Node\MSI`)

`MSI Center M` (version records plus ~12 subkeys of per-feature config), `MSI NBFoundation Service`
(a version string), `NB` (stale run state — `AppReady`, `LoopRestart`, a timestamp from just before
the uninstall), `One Dragon Center`.

Worth noting for the record: these include the registry values this project once read through
`RegistryTdpProvider` and `RegistryHwMouseProvider`. They are now what they were always suspected of
being — **records with nothing behind them**. Nothing writes them, nothing acts on them, and the
device does not care what they say.

### Cleanup, if wanted

Elevated. Optional — none of this does anything as it stands.

```powershell
Unregister-ScheduledTask -TaskName MSI_Center_M_Server  -Confirm:$false
Unregister-ScheduledTask -TaskName MSI_Center_M_Updater -Confirm:$false
Remove-Item 'C:\Program Files (x86)\MSI' -Recurse -Force
Remove-Item 'HKLM:\SOFTWARE\WOW6432Node\MSI' -Recurse -Force
```
