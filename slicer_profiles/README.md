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

**File > Import > Import Configs**, select this file. One import brings all three presets:

| Preset | Name in OrcaSlicer | Source file |
|---|---|---|
| Printer | `Voron 2.4r2 350 TAP (repo)` | `orcaslicer/src/printer/…json` (delta, inherits `Voron 2.4 350 0.4 nozzle`) |
| Filament | `Generic ABS (repo)` — flow 0.9334, 260 °C | `orcaslicer/src/filament/…json` (delta, inherits `Generic ABS @System`) |
| Filament | `Creality Hyper ABS (repo)` — starting values, see below | same folder |
| Process | `0.20mm Standard @Voron (repo)` — **slicer default speeds** (Voron system: walls 120/200, infill 200, first layer 50), label objects on, first layer 120 % | `orcaslicer/src/process/…json` (delta, inherits `0.20mm Standard @Voron`) |
| Process | `0.20mm Fast @Voron (repo)` — same, with the 2026-08-01 speeds: outer 250, inner 300, infill 300, first layer 30, travel 400; the filament's max volumetric speed caps it | same folder |

**Installing without the importer.** OrcaSlicer 2.4.2's *Import Configs* dropped the
process preset from this bundle three times running (2026-09-19; the importer has no
compatibility check, so the cause is still unknown). Its own user presets are plain
files, so the repo writes them directly:

```bash
python3 scripts/install_slicer_presets.py            # Orca + Cura, both apps closed
python3 scripts/install_slicer_presets.py --dry-run
```

It writes `user/default/{machine,filament,process}/<name>.json` plus the `.info` Orca
keeps beside each preset, backs up anything it replaces under `user/backup-install-*`,
and copies `cura/*.xml.fdm_material` into Cura's `materials/`. The bundle stays as the
portable copy for another machine.

### Creality Hyper Series ABS — starting values (2026-09-22, not yet calibrated)

Preset `Creality Hyper ABS (repo)` in the bundle and `cura/creality_hyper_abs.xml.fdm_material`
for Cura. Where each number comes from:

| Setting | Value | Source |
|---|---|---|
| Nozzle / first layer | 230 / 235 °C | user's temperature tower, 230 best (Creality range 230–270, its own presets sit at 260) |
| Bed first layer / after | 100 / 95 °C | Creality prints it at 80–90; kept ≥ 95 for the rear exhaust draft, and > 90 so `PRINT_START` takes the ABS soak branch |
| Chamber | 50 °C | Creality's K2 Plus preset; matches the exhaust target in `PRINT_START` |
| Flow ratio | **0.903** | measured 2026-09-22 at 230 °C: pass 1 from 0.97, −5 → 0.9215; pass 2 from 0.9215, −2 → 0.903 (−3 already showed the layer below, 0/−1/−2 looked crowded). Creality's presets say 0.95 |
| Max volumetric speed | 23 mm³/s | same; measured on a high-flow hotend of the Dragon HF's class |
| Pressure advance | **0.036** | measured 2026-09-24, Orca PA Pattern, DDE 0–0.08 step 0.002, at 230 °C and flow 0.903: last value before the lines after the spike stop touching, corners still sharp. Creality's presets say 0.03; the other ABS gave 0.022 |
| Part fan min / max / overhang | 10 / 60 / 60 % | same; spool says 0–20 % |
| Min layer time / min speed | 12 s / 20 mm/s | same |
| Density / HDT | 1.08 / 80 °C | same |
| Retraction | **0.5 mm @ 35 mm/s** | tower 2026-09-24 (slicer retraction, 0–2 mm): only the 0.0 band strung, clean from 0.1; 0.5 keeps margin. Spool's 5 mm is a Bowden figure |

Calibration order for it is in [`docs/FILAMENT_CALIBRATION.md`](../docs/FILAMENT_CALIBRATION.md); results replace these values.

After importing, select the three `(repo)` presets in the project and save the `.3mf`
so its embedded copies are the good ones, then delete the old `Voron 2.4 350 0.4` and any
`…(Orcaslicer project.3mf)` printer the project brought in.

**The bundle is generated — never edit the `.orca_printer` by hand.** Edit the JSON under
`orcaslicer/src/` and run:

```bash
python3 scripts/build_orca_bundle.py            # rebuild (deterministic, no git churn)
python3 scripts/build_orca_bundle.py --check    # exit 1 if the bundle is stale vs src/
python3 scripts/build_orca_bundle.py --extract some_export.orca_printer /tmp/x
                                                  # unpack an OrcaSlicer export to diff against src/
```

The build validates what OrcaSlicer's importer actually relies on: it ignores folders and
`bundle_structure.json` lists and routes every JSON by its `printer_settings_id` /
`filament_settings_id` / `print_settings_id` key, which must equal the preset name; a
filament or process whose `inherits` parent is missing is skipped silently. It also
refuses a start gcode that heats before `PRINT_START` and `wipe: 1` with firmware
retraction (see the table below). To adopt a change you made in the OrcaSlicer GUI, export
the preset, `--extract` it, copy the changed keys into `src/`, rebuild.

One setting from the installed printer preset was carried into the repo source on
2026-09-19: `print_host: voron.local`. `nozzle_type` is **`brass`**, not the
`hardened_steel` the installed preset had: the Formbot kit ships a V6 clone and this
machine has the optional Phaetus Dragon HF (`heatend.cfg`, PT1000), whose box contains a
0.4 mm plated-copper V6 nozzle (3DJake's scope of delivery: "Incl. coated V6 nozzle
0.4 mm"; Voron3D wiki lists brass as the Dragon's standard nozzle, hardened steel as an
upgrade). OrcaSlicer has no plated-copper option and only uses `nozzle_type` to warn when
a filament needs a hardened nozzle, so `brass` is the honest value for a copper-alloy
nozzle. Change it to `hardened_steel` only after physically fitting one. Two were **not**
and stay as documented below: `z_hop 0.3 / Auto Lift` and `retract_when_changing_layer 0`
(installed) vs `z_hop 0.2 / Normal Lift` and `retract_when_changing_layer 1` (repo). Say
if those were deliberate and they go into `src/`.

The process preset is the 2026-08-01 export that was sitting untracked in this folder
(honeycomb 30 %, walls 250/300 mm/s, 4 skirt loops, inner-outer-inner, monotonic top,
topmost ironing) plus the two fields that used to be "set by hand".

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
| `use_firmware_retraction` | 0 | 1 → **0** again (2026-09-22) | §4 — on only while tuning a filament live; slicer owns retraction otherwise, [ADR-0021](../docs/adr/0021-slicer-retraction-by-default.md) |
| `retraction_length` | 0.8 | 0.75 | the value calibrated in `[firmware_retraction]`, now applied by the slicer |
| `retraction_speed` / `deretraction_speed` | 30/30 | 35/35 | same |
| `retract_restart_extra` | 0 | 0.05 | `unretract_extra_length` |
| `z_hop` | 0.4 | **0.2** | see the correction below |
| `retract_lift_above` | 0 | **0.3** | suppresses lift on the first layer only |
| `emit_machine_limits_to_gcode` | 1 | 0 | §1 — "silence is better than a shim firing every job" |
| `machine_start_gcode` | `M190`/`M109` **before** `PRINT_START` | just `PRINT_START EXTRUDER=... BED=... CHAMBER=...` | §6 — the exact trap the doc warns about |
| `wipe` | 1 | 0 → **1** again (2026-09-22) | the wipe is the reason slicer retraction won (ADR-0021); with firmware retraction on it must be 0 and the bundle build enforces the pair |
| `single_extruder_multi_material` | 1 | **0** | single extruder; with 1 Orca disables object labelling and only Moonraker's preprocessor saved `EXCLUDE_OBJECT` (ADR-0006 rev. 2026-09-19) |
| `time_cost` | 0 | **440** | COP per hour, from measured heater duty — see below |
| preset name | `Voron 2.4 350 0.4` | **`Voron 2.4r2 350 TAP (repo)`** | distinct from any project-embedded copy; `printer_settings_id` in the gcode then says where the file came from (2026-09-19) |

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
Both former by-hand fields ("Label objects", first-layer line width **120%**) are now in
the bundled process preset; nothing is left to set by hand.

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
| Flow ratio | **0.9334** | Flow Rate calibration 2026-07-31, [TUNING_PLAN](../docs/TUNING_PLAN.md). Shipped inside the bundle as preset **Generic ABS (repo)** (260 °C) since 2026-09-19 — flow ratio is a *filament* setting in OrcaSlicer and cannot live on the printer preset |
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
