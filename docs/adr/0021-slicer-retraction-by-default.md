# 0021 — Retraction belongs to the slicer by default; firmware retraction stays for live tuning

**Status:** Accepted · 2026-09-22 · supersedes the retraction part of [0012](0012-slicer-integration-for-tuning.md)

## Context

ADR-0012 switched the slicer to firmware retraction (`G10`/`G11`) because it opened the
tuning gate: `SET_RETRACTION` changes the value mid-print, so a retraction sweep is one
print instead of one slice per data point. That was the right call for July, when every
number on this machine was unmeasured.

Two months on the cost side has grown:

- **No wipe.** OrcaSlicer cannot wipe while retracting when it does not know the
  retraction length, so it refuses the pair in CLI mode and silently drops `wipe` in the
  GUI (found 2026-09-19, ADR-0012 revision). Wipe is the cheapest stringing and seam
  fix there is, and the new ABS (Creality Hyper ABS) strings at its best temperature.
- **One value for every filament.** `[firmware_retraction]` holds one length; per-filament
  values need a `SET_RETRACTION` in each filament's start gcode, a second place to keep
  in sync.
- The slicer's per-feature rules (retract on layer change, minimum travel, retract
  before wipe) are all inert under firmware retraction.

Both mechanisms can coexist on the printer: Klipper only retracts on a `G10`, and a file
sliced with the box off never contains one. They cannot stack, because the slicer emits
one kind or the other.

## Decision

**One printer profile, "Use firmware retraction" OFF, `wipe` ON.** The slicer owns
retraction: the calibrated 0.75 mm / 35 mm/s move from `[firmware_retraction]` into the
printer profile's extruder defaults, and each filament preset may override them.

**`[firmware_retraction]` stays in `extruder.cfg`**, inert. When a new filament needs a
retraction sweep, tick the box in the printer profile, slice one test, tune live with
`SET_RETRACTION`, copy the winner into the filament preset, untick. The user chose a
single profile with a manual toggle over two printer profiles: fewer presets to keep
compatible, and the toggle is used a few times per new filament, not per print.

`scripts/verify_slicer_gcode.py` no longer requires `G10`/`G11`; it requires **exactly
one** method per file (`G10`/`G11` or `G1 E-` moves) and fails on both or neither.
`scripts/build_orca_bundle.py` enforces the pairing: `wipe` must be 0 when firmware
retraction is on, and 1 when it is off.

Rejected alternatives:

- **Two printer profiles in the bundle** (one per method). Works, and the build script
  could validate both, but every filament and process preset would have to list both
  names, and the user preferred one profile.
- **Keep firmware retraction and add wipe by hand in a macro.** Klipper has no notion
  of "the line I just printed", so a wipe move cannot be synthesised on the firmware
  side.
- **Per-filament `SET_RETRACTION` in filament start gcode.** Solves one value per
  filament, not the missing wipe.

## Consequences

- Files sliced from the updated bundle carry `G1 E-…` retractions and `G1` wipe moves;
  `SET_RETRACTION` on the console does nothing to them. Do not be surprised.
- The retraction number is the filament preset's `filament_retraction_length` /
  `filament_retraction_speed` (or the printer defaults when unset). TUNING_PLAN's
  retraction entry should record the value per filament.
- The Creality Hyper ABS calibration sequence stands: temperature (done, 230 °C),
  flow, pressure advance, retraction (with the box ticked for the sweep), max
  volumetric speed. Its result seeds the first per-filament override.
- Re-import the bundle (printer overwrite) and re-save the project `.3mf` so the
  embedded profile picks up the change.
