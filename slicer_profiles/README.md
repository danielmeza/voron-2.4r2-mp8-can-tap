# Slicer profiles

Built from [docs/SLICER_SETUP.md](../docs/SLICER_SETUP.md) (values read live from
the machine) plus the two still-open Tier A settings in
[docs/TUNING_PLAN.md](../docs/TUNING_PLAN.md) — first-layer line width 120%, z-hop
disabled.

Once configured (either slicer), run:

```bash
python3 scripts/verify_slicer_gcode.py path/to/sliced.gcode
```

against a real test slice before trusting a print from either.

## OrcaSlicer — `orcaslicer/voron_2.4r2_350_can.orca_printer`

**File > Import > Import Configs**, select this file.

First attempt at this (a hand-authored PrusaSlicer-style `.ini`) failed —
OrcaSlicer's import only accepts its own native bundle types (`.orca_printer`,
`.orca_filaments`, `.zip`), and the exact internal schema isn't public/stable enough
to hand-author blind. Fixed 2026-07-30 by starting from a **real export** of the
user's own OrcaSlicer printer preset (already inheriting Orca's built-in Voron 2.4
350 system profile) and correcting only the fields that were wrong against this
specific machine's measured data:

| Field | Was (Orca's generic Voron default) | Now | Source |
|---|---|---|---|
| `printable_height` | 325 | 300 | `kinematics.cfg` `position_max` |
| `machine_max_speed_x/y` | 500/200 | 400/400 | `SLICER_SETUP.md` §1 |
| `machine_max_speed_z` | 12/12 | 20/20 | same |
| `machine_max_acceleration_x/y/z` (+ extruding/retracting/travel) | 20000/5000/500 mix | 5700/5700/350 | same |
| `use_firmware_retraction` | 0 | 1 | §4 — "the setting that actually opens the gate" |
| `retraction_length` | 0.8 | 0.75 | live `firmware_retraction.cfg` |
| `retraction_speed` / `deretraction_speed` | 30/30 | 35/35 | same |
| `retract_restart_extra` | 0 | 0.05 | `unretract_extra_length` |
| `z_hop` | 0.4 | **0.2** | see the correction below |
| `retract_lift_above` | 0 | **0.3** | suppresses lift on the first layer only |
| `emit_machine_limits_to_gcode` | 1 | 0 | §1 — "silence is better than a shim firing every job" |
| `machine_start_gcode` | `M190`/`M109` **before** `PRINT_START` | just `PRINT_START EXTRUDER=... BED=... CHAMBER=...` | §6 — the exact trap the doc warns about |

The original export is kept at `orcaslicer/samples/` for reference/diffing.

### Correction 2026-07-30 — z-hop was disabled too broadly

The first version of this profile set `z_hop: 0` **globally**, reading TUNING_PLAN's
*"disable z-hop on the first layer"* as if it meant everywhere. On the PA pattern print
the nozzle then **dragged across already-printed blocks** on every Y travel, because
without lift a travel move runs at exactly the current layer height. First-layer squish
and adhesion were good, which is what identified it as a travel problem rather than a Z
offset one.

Correct form: `z_hop: 0.2` for normal lift, plus `retract_lift_above: 0.3` so the lift
is skipped below 0.3 mm — i.e. on the first layer, and only there.

⚠️ **Re-importing this file does not update a preset you already imported.** Change the
two fields by hand in **Printer Settings > Extruder > Z-Hop** (`Z hop height` and
`Only lift Z above`), or delete the existing preset before re-importing.

**Not covered — this only exports the printer preset, not print/process settings.**
Still need to set by hand in **Print Settings**: "Label objects" on (Others tab),
first-layer line width **120%** (First layer tab). Two fields, not the whole table
this used to be.

### Machine cost — `time_cost: 440` (per hour, 2026-09-19)

OrcaSlicer has no electricity field; `time_cost` on the printer profile is the hourly
machine cost it multiplies into the estimate. Derived from the completed ABS print of
2026-09-18 (bed 100 °C, 1214 one-second samples in `klippy.log`):

| Consumer | Measured duty | Average power |
|---|---|---|
| Bed, 650 W (`heated_bed.cfg`) | 44 % | ~285 W |
| Hotend (~65 W assumed) | 47 % | ~30 W |
| Electronics, fans, motors | estimate | ~50 W |
| **Total** | | **~365 W** |

At **1200 COP/kWh** (the tariff the user quoted) that is **~440 COP/h** for ABS; a PLA
print at 55 °C bed runs about 250 COP/h. The tariff and the hotend wattage are the two
inputs to revisit. `filament_cost` (per kg) lives on the filament profile and is still
unset — it needs the price of the spool.

### Filament-profile values (not in the printer bundle)

The `.orca_printer` above carries **printer** settings only. These are per-filament and
must be set by hand in Filament Settings — they are calibration results, so they are
recorded here rather than left to memory:

| Setting | ABS | Source |
|---|---|---|
| Flow ratio | **0.9334** | Flow Rate calibration 2026-07-31, [TUNING_PLAN](../docs/TUNING_PLAN.md) |
| Pressure advance | **leave unchecked** | Klipper's `0.022` applies already; ticking it here silently *overrides* the calibrated value |

Also still set by hand in **Print Settings**: "Label objects" on (Others), first-layer
line width **120 %** (Quality > Line width). Top surface line width must stay at
**0.4 mm = 100 % of nozzle** — Ellis requires that for the flow test to be valid.

## Cura — `cura/voron_2.4r2_350_can.def.json`

Custom printer definition. Drop into Cura's `definitions/` folder (Help > Show
Configuration Folder), restart Cura, then Settings > Printer > Add Printer > Custom.
`gcode_flavor: Marlin` — matches this repo's existing production Cura output
(`SLICER_INTEGRATION.md` confirms the live file already emits `FLAVOR:Marlin`), not a
guess. **Additive, not destructive**: adding this file creates a new selectable
printer, it does not alter your already-configured production printer instance unless
you explicitly switch its printer type to this one.

Object exclusion is deliberately **not** set here — per
[ADR-0006](../docs/adr/0006-adaptive-bed-mesh.md), Cura doesn't need a slicer-side
toggle for it; Moonraker's `preprocess_cancellation` injects `EXCLUDE_OBJECT_DEFINE`
on upload.

`CHAMBER=` is omitted from the Cura start gcode (no standard Cura placeholder for it
without enclosure support) — `PRINT_START` defaults it to 40 °C when absent.
