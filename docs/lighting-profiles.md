# Editing the lighting profiles

The widget's **Lighting** card has four buttons: **Off**, and three profiles. There is no colour
picker in the widget on purpose — the three profiles are plain text files, and this page is how you
change them.

## Where the files are

```
%LOCALAPPDATA%\Packages\ClawConfigurator_xq4frxrkckec6\LocalCache\ClawConfigurator\Lighting
```

`ClawConfigurator_xq4frxrkckec6` is the package family name — a hash of the `Identity` name and publisher
in `src/Package/Package.appxmanifest`, so it is stable across versions and machines. If either has
been changed, `(Get-AppxPackage McenterLite).PackageFamilyName` prints the real one.

Two shortcuts to finding it without typing that:

- The helper logs the full path on every start. Look for `Lighting profiles:` in `helper.log`, which
  sits one folder up from the profiles.
- The folder also contains a `README.txt` with the same reference as this page, so once you are
  there you do not need to come back here.

The files are `Profile_1.txt`, `Profile_2.txt` and `Profile_3.txt`.

## The workflow

1. Edit a file in any text editor and save it.
2. Tap that profile in the widget.

The file is read **at the moment you tap**, so there is nothing to restart — not the widget, not the
helper. Tapping a profile that is already selected re-reads it, which is how you preview an edit.

## Undoing a bad edit

**Delete the file, or delete everything in it and save. Then tap that profile.** The default comes
back and the file is rewritten in place, so recovery is one action in the folder you are already in,
with nothing to restart and no syntax to remember. An empty file and a missing one mean the same
thing deliberately — select-all-and-delete is what people reach for first.

Nothing you can type in these files can break the widget or the controller:

- A setting that cannot be read is **skipped, and the previous value kept**. The worst case is a
  profile that ignores part of your edit, never one that fails to apply.
- A colour list where **nothing** parses keeps the colours that were there before. This matters more
  than it looks: an empty `Colors` means "built-in palette", so committing an empty list after a
  typo would silently swap your profile for a different, perfectly valid-looking one.
- A file that cannot be read at all — open in another program, or a permissions problem — falls back
  to the default but is **not** rewritten. That condition is transient, and overwriting a file we
  merely failed to read would destroy work.

**`helper.log`, one folder up, names every setting that was skipped** and the value kept instead,
prefixed `Profile 1:`, `Profile 2:` or `Profile 3:`.

Two settings are valid but look exactly like a fault: **`Style=Off`** and **`Brightness=0`**. Both
turn the lights off, and both say so in the log, so that "I edited the file and the lights went out"
leaves a trace pointing at the edit rather than at a failure.

Nothing else rewrites these files: once they exist, they are yours.

## What goes in a file

```ini
Name=Purple
Style=Steady
Colors=#7F00FF
Speed=Medium
Direction=Clockwise
Brightness=100
```

| Setting | Values | Notes |
|---|---|---|
| `Name` | any text | Shown on the widget button. Keep it short — four buttons share one row. |
| `Style` | `Off`, `Steady`, `Breath`, `ColorCycle`, `Wave` | |
| `Colors` | a list, or empty | **Empty means the built-in palette.** See below. |
| `Speed` | `Slow`, `Medium`, `Fast` | `Steady` ignores it. |
| `Direction` | `Clockwise`, `Counterclockwise` | `Wave` only. |
| `Brightness` | `0`–`100` | |

Lines starting with `#` or `;` are comments. Unknown settings are ignored, and a value that cannot
be read keeps its default rather than breaking the file — the helper log names anything it skipped,
so a typo tells you about itself instead of silently doing nothing.

### Colours

Any of these spellings work, and they can be mixed:

```ini
Colors=#FF0000, #00FF00, #0000FF
Colors=FF0000, 00FF00
Colors=#F00
Colors=255,0,0
```

Decimal `R,G,B` is supported specifically so values can be pasted straight out of MSI Center M's own
profile files without translation.

**Leaving `Colors` empty selects the built-in palette for that style** — which is what MSI Center M
did for its Wave and ColorCycle presets, so that is how profiles 2 and 3 ship.

How many colours each style uses:

| Style | Colours | Behaviour |
|---|---|---|
| `Steady` | 1 | One colour, no animation. |
| `Breath` | up to 4 | Each colour fades in turn, with a dark frame between. |
| `ColorCycle` | up to 3 | Whole controller changes colour in sequence. |
| `Wave` | 4 | One per corner of each stick ring; the colours rotate. |

Give `Wave` fewer than four colours and they repeat to fill the corners — one colour makes it a
solid rotation, which looks like `Steady`.

## The nine LEDs

The controller has nine addressable LEDs:

| Index | Where |
|---|---|
| 0–3 | left stick ring |
| 4–7 | right stick ring |
| 8 | ABXY cluster |

There is no per-LED setting in these files. The firmware animates whole frames of nine, and a style
is a recipe for producing those frames — per-LED control would mean writing keyframes by hand, which
is what these files exist to avoid.

## What the hardware actually stores

Worth knowing, because it explains two behaviours that otherwise look like bugs.

The controller **does not know what "Wave" means.** It stores up to eight keyframes of nine colours
each, plus a speed and a brightness, and loops them. A `Style` here is a recipe for producing those
keyframes, reproduced from MSI Center M so an existing profile looks exactly as it did before.

That has two consequences:

- **The widget cannot tell you which profile is active** by asking the controller — there is no
  profile number stored in it, and two profiles could render identically. The selected profile is
  remembered by the helper instead.
- **Lighting is written to RAM, never flash.** It resets if the controller loses power, and the
  helper re-applies your last choice when it starts. This is deliberate: flash has a limited number
  of writes, and a button you can tap repeatedly should not spend them.

## Where these came from

The three defaults reproduce the profiles MSI Center M had configured on 2026-08-12:

| Profile | Style | Colours |
|---|---|---|
| 1 — Purple | Steady | `#7F00FF` |
| 2 — Wave | Wave, clockwise, medium | built-in palette |
| 3 — Cycle | ColorCycle, medium | built-in palette |

The originals are archived at [`Diagnostics/mystic-light-profiles/`](../Diagnostics/mystic-light-profiles/)
in MSI's own `.cfg` format, because those files live in MSI Center M's install and **do not survive
its uninstall**.

The style-to-keyframe recipes are reproduced from MSI's `API_ControlMode.dll` and pinned by tests in
`tests/Shared.Tests/LightingTests.cs`. The constants look arbitrary — Medium is 14 for `Breath`, 15
for `ColorCycle` and 17 for `Wave` — because they are copied rather than derived. See
[`hardware-notes.md`](hardware-notes.md) gate G4.
