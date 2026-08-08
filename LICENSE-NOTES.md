# Licensing notes

**Status: MIT.** See [`LICENSE`](LICENSE). Chosen deliberately, for the reasons below - this file
stays as the record of why a copyleft licence was never forced.

## Why this matters

The reference project, [ClawTweaks](https://github.com/enterTheVoidCode/ClawTweaks), is
licensed **AGPLv3**. Its `LICENSING.md` is explicit:

> building on ClawTweaks is welcome, but you cannot take it closed-source — any product
> or service derived from it must itself be open under AGPLv3.

Portions inherited from GoTweaks and the original Microsoft Xbox Game Bar sample remain
**MIT** (`LICENSE.MIT` in that repo).

## The line this project holds

**Facts are fine. Code is not.**

| Category | Example | Status |
|---|---|---|
| Hardware facts | WMI class/method names, EC register offsets, byte-table layouts, HID report formats, duty→RPM measurements, model id strings | ✅ Not copyrightable. Free to use. Record in `docs/hardware-notes.md`. |
| API facts | Win32 GUIDs, P/Invoke signatures, documented struct layouts | ✅ Free to use — these come from Microsoft/Intel documentation, not from ClawTweaks. |
| Expressive code | Any `.cs` file copied or adapted from ClawTweaks | ❌ Makes this project a derivative work → forces AGPLv3. |

### Consequences of that line

Every file in this repo is **written from scratch**, including the pieces where copying
would have been easy and was actively considered:

- `src/Hardware/Windows/PowrProf.cs` / `PowerGuids.cs` — thin P/Invoke over documented
  Win32 power APIs. Written from Microsoft docs. The power-scheme GUIDs are published
  by Microsoft and appear in `powercfg /q` output on any Windows machine.
- The named-pipe AppContainer ACL — uses SIDs `S-1-15-2-1` and `S-1-15-2-2`, both
  documented by Microsoft as `ALL_APPLICATION_PACKAGES` and
  `ALL_RESTRICTED_APPLICATION_PACKAGES`.
- The IPC envelope, `Function` enum, feature dispatcher, settings store and all UI —
  original designs, deliberately different in shape from ClawTweaks'.

This kept the licence choice **open**: AGPLv3 remained available if wanted, but was never
forced - MIT was chosen instead, precisely because nothing here required the alternative.

## Decompilation

Phase 0 involves decompiling the ClawTweaks helper **that is already installed on the
target device** in order to learn how the hardware is addressed.

- Permitted output: hardware facts, recorded in `docs/hardware-notes.md`.
- Not permitted: pasting decompiled source into this repo, in any form, transformed or not.
- Decompiler scratch space is gitignored (`/decompiled/`, `/thirdparty-bin/`).

Note also that ClawTweaks' own `LICENSING.md` states the repository *"does not ship
ready-to-run signed binaries"* — the installed helper comes from a release build, so
treat its contents as third-party material regardless of the source licence.

## Third-party runtime dependencies

| Component | Licence | How it is used |
|---|---|---|
| Intel IGCL (`ControlLib.dll`) | proprietary, ships with the Intel Arc driver | Loaded at runtime via `LoadLibrary`. **Never redistributed.** |
| Intel IGCL headers | Apache-2.0 | Vendored at a pinned commit, for reference only. |
| HidSharp | Apache-2.0 / MIT | NuGet reference. |
| MSI Center M | proprietary | Not redistributed; only detected and optionally coexisted with. |

## To do

- [x] Choose the project licence (AGPLv3, or a permissive option such as MIT/Apache-2.0)
      and add a `LICENSE` file. → MIT.
- [ ] Keep enforcing the "facts not code" line above - MIT does not change it. Anything
      copied from ClawTweaks would still make this a derivative work bound by AGPLv3,
      regardless of what licence this repo declares for its own original code.
