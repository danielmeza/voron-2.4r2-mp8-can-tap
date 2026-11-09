# 0012 — Slicer integration for the tuning phase

**Status:** Accepted · 2026-07-25 · retraction ownership superseded by [0021](0021-slicer-retraction-by-default.md) on 2026-09-22; everything else stands

## Context

Before starting Ellis' tuning sequence, the question is whether the setup can even
*express* what we are about to tune. Measured by tokenising a real job, the slicer emits:

```
G0/G1, M82, G92, M140, M107, PRINT_START, PRINT_END
```

That is the whole vocabulary. Absent: `M204` (acceleration), `M900` (pressure advance),
`G10`/`G11` (firmware retraction), `M220`/`M221`, `SET_PRINT_STATS_INFO`/`M73`. The file
is Cura output (`FLAVOR:Marlin`).

Klipper's side, verified against the pinned `v0.13.0-708-g7046bd00e` source, is nearly
complete: `M204`, `M220`, `M221`, `M117`, `M73`, `SET_PRESSURE_ADVANCE`,
`SET_RETRACTION`, `SET_VELOCITY_LIMIT`, `SET_PRINT_STATS_INFO` and `TUNING_TOWER` all
exist. **`M205` and `M900` do not.**

That asymmetry matters because an unimplemented gcode raises `Unknown command`, which
**aborts the enclosing macro** — it is not silently ignored.

Two concrete consequences:

- **`[firmware_retraction]` is dead config.** `retract_length: 0.75`, `retract_speed: 35`
  and `unretract_extra_length: 0.05` are configured, but Cura bakes retraction into `E`
  moves and cannot emit `G10`/`G11`. Changing those values changes nothing, and
  `SET_RETRACTION` cannot tune retraction live — every experiment needs a re-slice,
  which is exactly the loop Ellis' tower methods exist to avoid.
- **Acceleration is global.** With no `M204`, every feature prints at `max_accel: 5700`.

## Decision

**1. Add compatibility shims** in `printer/macros/slicer_compat.cfg`:

- **`M900`** → `SET_PRESSURE_ADVANCE ADVANCE={K}`. Verified live: `M900 K0.0425` moved
  pressure advance from 0.055 to 0.0425 and back. Without this, any Marlin-flavoured
  tuning gcode — including Cura's Linear Advance plugin — aborts the print.
- **`M205`** → accepted and ignored, with a message pointing at
  `SET_VELOCITY_LIMIT SQUARE_CORNER_VELOCITY=`. Klipper has no jerk model; the point is
  only to stop it aborting.
- **`TEST_SPEED`** — Ellis' speed/accel test, for the "maximum speeds and accelerations"
  step.

**2. Use a calibration-capable slicer for the tuning phase.** Cura stays fine for
production work (object processing already covers adaptive meshing, ADR-0006), but it is
the weakest choice for tuning: no firmware retraction, no calibration generators, and
Ellis' guide is written around PrusaSlicer/SuperSlicer. **OrcaSlicer** ships Ellis'
methods as built-in tools (PA *pattern*, flow, temp tower, retraction test, VFA) and
emits `G10`/`G11`, `M204`, `EXCLUDE_OBJECT_*` and layer info natively.

**3. Re-run input shaping.** `shaper_freq_x: 47.6`, `shaper_freq_y: 33`, both `mzv` —
measured while `resonance_tester.probe_points` was silently `100,100,20` instead of bed
centre (ADR-0001). They describe the machine at the wrong place.

## Consequences

- Marlin-flavoured tuning gcode no longer aborts, so tuning towers from any source work.
- `M900` changing pressure advance mid-print is a real behaviour change: a stray `M900`
  in old gcode now takes effect instead of failing loudly. Acceptable — that is the
  point — but worth knowing when reading back an old file.
- Two bugs in these macros were caught by checking Klipper's source rather than assuming:
  **`SET_VELOCITY_LIMIT` has no `ACCEL_TO_DECEL`** (removed in v0.12; it is
  `MINIMUM_CRUISE_RATIO` now), and **`toolhead` status exposes no
  `square_corner_velocity`** — it has to be read from `configfile.settings`. Both would
  have failed only at runtime, mid-test.
- Not changed pre-emptively: `max_extrude_cross_section` (default `4 × nozzle²`),
  `max_extrude_only_distance` (50 mm), `minimum_cruise_ratio`. PA *pattern* tests and fat
  purge lines can trip the first with *"Move exceeds maximum extrusion cross section"*.
  Raise them **only when a specific test fails** — they are genuine safety checks.
- `[firmware_retraction]` stays configured but inert until the slicer can drive it. If
  OrcaSlicer is adopted, those values become live and must be tuned, not assumed.

Full analysis in [`docs/SLICER_INTEGRATION.md`](../SLICER_INTEGRATION.md).

---

## Revisions

### 2026-07-26 — input shaping re-run; two root causes found first

Re-running `SHAPER_CALIBRATE` surfaced two problems that had to be fixed before the
measurement meant anything.

**1. `axes_map` was wrong.** A stationary `ACCELEROMETER_QUERY` reported gravity
(~9550 mm/s²) on the **sensor's X axis** while the config had the default identity map
`x,y,z` — i.e. Klipper was told sensor-X was printer-X while it was actually pointing
up. BTT's official sample config for this exact board specifies **`axes_map: z,-y,x`**.
After the fix, gravity reads on the third value (9474.6) as it should. The previous
shaper values were measured through this wrong mapping *and* at `probe_points 100,100`
instead of bed centre — both inherited from that same BTT sample, which targets a
smaller printer.

**2. The sweep shut the printer down.** The first attempt failed with:

```
Unable to obtain 'spi_transfer_response'
MCU 'MP8' shutdown: Timer too close
```

Klipper's message blames an overloaded host. It was not: host load was **0.42**, 550 MB
free, and the CAN bus showed **zero** errors — no bus-off, no arbitration lost, no
retransmits. The real cause is bandwidth and timing headroom. The ADXL streams
3200 samples/s from the EBB over the same 1 Mbit bus the MP8 uses for step timing, and
`accel_per_hz: 75` demands `75 × 133 = 9975 mm/s²` at the top of the sweep — nearly
double this printer's `max_accel` of 5700.

Fixed by lowering **`accel_per_hz` to 50** (peak ~6667 mm/s²) and running one axis per
invocation. Both axes then completed cleanly.

**Result** — measured values, now live:

| Axis | Old (invalid) | **New** | Runner-up |
|---|---|---|---|
| X | mzv 47.6 Hz | **2hump_ei 78.6 Hz** (0.0 % vib, smoothing 0.099) | mzv 51.2 Hz (4.7 %, 0.084) |
| Y | mzv 33.0 Hz | **mzv 35.8 Hz** (0.0 % vib, smoothing 0.159) | zv 36.8 Hz (6.3 %, 0.131) |

Written into `gantry/input_shaping.cfg` rather than via `SAVE_CONFIG`. `SAVE_CONFIG`
would append `[input_shaper]` to `klippy.conf`'s autosave block, which **overrides** the
repo file and violates [ADR-0001](0001-single-owner-config-sections.md) — the repo copy
would silently become decorative.

### 2026-07-26 — M201/M203 added; the gate is open

Auditing what a PrusaSlicer-lineage slicer can emit found two more unimplemented
gcodes beyond `M205`: **`M201`** (max acceleration) and **`M203`** (max feedrate). Both
appear when "emit machine limits to gcode" is enabled, and both would have raised
`Unknown command` and aborted the print. Added as accept-and-ignore shims.

They are deliberately **not** mapped onto `SET_VELOCITY_LIMIT`. Machine limits belong in
the Klipper config; a slicer silently lowering them mid-print is worse than ignoring the
command. The right setting is *"use for time estimate only"*.

Verified live — every gcode such a slicer emits now succeeds:

```
M201 X5700 Y5700  OK      M205 X8 Y8    OK      M73 P10 R5   OK
M203 X400 Y400    OK      G11           OK      M221 S100    OK
M204 P5700 T5700  OK      M900 K0.055   OK
```

`G10` returned an error, which turned out to be correct behaviour rather than a fault:
*"Extrude below minimum temp"* with the hotend at 47.9 °C against `min_extrude_temp: 160`.
The cold-extrude guard. `G11` passed because unretract-when-not-retracted is a no-op.
Firmware retraction itself is sound.

Concrete profile, with every value read from the live machine, is in
[`docs/SLICER_SETUP.md`](../SLICER_SETUP.md).

### 2026-07-30 — OrcaSlicer emits `SET_VELOCITY_LIMIT`, and Klipper does **not** clamp it

Two findings from verifying the first real OrcaSlicer slice
(`meter_stepper_adapter-Adapter_ABS_9m16s.gcode`, 29523 lines) against
[SLICER_SETUP.md](../SLICER_SETUP.md) §7.

**1. With `gcode_flavor: klipper`, there is no `M204` at all.** The slice contained
**1423 `SET_VELOCITY_LIMIT`** and **zero `M204`** — Orca uses Klipper's native command
instead of the Marlin one. `scripts/verify_slicer_gcode.py` required `M204` and
therefore failed a perfectly good file; the checker was wrong, not the gcode. It now
accepts either form, and the §7 checklist row was corrected.

Distinct values emitted, all inside the configured ceiling because Orca clamps to
`machine_max_acceleration_*` before writing:

```
404x  ACCEL=5700 ACCEL_TO_DECEL=2850 SQUARE_CORNER_VELOCITY=12
356x  ACCEL=5000 ACCEL_TO_DECEL=2500 SQUARE_CORNER_VELOCITY=7
178x  ACCEL=3000 ACCEL_TO_DECEL=1500
 10x  ACCEL=500  ACCEL_TO_DECEL=250  SQUARE_CORNER_VELOCITY=9
```

**2. The "machine limits belong in the Klipper config" assumption above is weaker than
it reads.** That revision argued M201/M203 should not map to `SET_VELOCITY_LIMIT`
because a slicer should not override machine limits. But `M204` and `SET_VELOCITY_LIMIT`
— both of which the slicer *does* emit — already can. Verified in the pinned source,
`klippy/toolhead.py:538`:

```python
def set_max_velocities(self, max_velocity, max_accel,
                       square_corner_velocity, min_cruise_ratio):
    ...
    if max_accel is not None:
        self.max_accel = max_accel          # no min() against the config value
```

`cmd_M204` calls this directly. There is **no clamp to `[printer] max_accel`** — a
slicer emitting a higher value genuinely raises the machine's limit for the rest of the
print. Note the emitted `SQUARE_CORNER_VELOCITY=12` above is already above this
printer's configured `8.0`, and takes effect.

The protection today is **OrcaSlicer's own clamping**, not Klipper's. So the printer
profile's `machine_max_acceleration_*` fields are load-bearing safety values, not just
time-estimate hints — which is why they were corrected to this machine's real
5700/350 rather than left at Orca's generic Voron defaults of 20000/500.

**3. `ACCEL_TO_DECEL` is emitted but was removed in Klipper v0.12+** (replaced by
`MINIMUM_CRUISE_RATIO` — see the note in `macros/slicer_compat.cfg`'s `TEST_SPEED`).
**Resolved — harmless.** Tested live with the exact emitted string:

```
SET_VELOCITY_LIMIT ACCEL=5700 ACCEL_TO_DECEL=2850 SQUARE_CORNER_VELOCITY=12
  -> {"result":"ok"}, no error
```

Klipper silently ignores the unknown parameter; `minimum_cruise_ratio` stayed at its
configured `0.5`. No shim needed, and no abort risk across the 1423 lines that carry it.

The same test **empirically confirmed the non-clamping** in point 2 — it is not just a
source reading. `square_corner_velocity` went to **12.0** against a configured `8.0`,
and stayed there until explicitly restored. A slicer can and does raise this printer's
motion limits mid-print; only OrcaSlicer's own clamping keeps acceleration in bounds.

### 2026-09-19 — printer bundle: `wipe 0`, `single_extruder_multi_material 0`, `time_cost 440`

Three profile fields corrected in `voron_2.4r2_350_can.orca_printer` so it can replace
the installed profile as-is. `wipe: 1` conflicts with firmware retraction: the headless
CLI run of 2026-08-03 refused to slice until it was cleared, and the GUI-sliced file of
2026-09-19 shows `wipe = 0`, so the GUI had been dropping it silently.
`single_extruder_multi_material: 1` makes OrcaSlicer skip `EXCLUDE_OBJECT_DEFINE`
(found on 08-03); the machine has one extruder, and with 0 the slicer labels objects
itself instead of relying on Moonraker's preprocessor. `time_cost` was 0; now 440
(COP/h, derivation in `slicer_profiles/README.md`). The installed profile also differs
in `z_hop` (0.3) and `retract_lift_above` (0): the repo's 0.2 / 0.3 stand, per the
2026-07-30 correction in the README, and importing the bundle restores them.

### 2026-09-19 — presets are sourced as JSON in the repo; the bundle is generated

The `.orca_printer` used to be an opaque zip edited by unzipping, patching and re-zipping,
and the filament and process presets were untracked exports that drifted (flow ratio 1.0
vs the documented 0.9334; the process export from 08-01 never made it in). Now
`slicer_profiles/orcaslicer/src/{printer,filament,process}/<name>.json` are the sources
(all three as `inherits` deltas, the shape OrcaSlicer itself writes; the printer was a full
158-key export until 2026-09-24, when installing it as-is made Orca error on selection) and `scripts/build_orca_bundle.py` produces one bundle carrying all three,
with `--check` to catch a stale bundle. The generated file stays committed because the
user imports it directly from the repo. The validator encodes what was learned from
OrcaSlicer's `PresetBundle::import_json_presets`: the importer walks every JSON in the zip,
skips `bundle_structure.json`, routes by `printer_settings_id` / `filament_settings_id` /
`print_settings_id`, and silently drops a delta whose `inherits` parent is missing — the
first build caught the renamed printer still carrying its old `printer_settings_id`.
Rejected: shipping resolved full JSONs for filament and process (duplicates ~200 system
keys that go stale across Orca versions) and shipping three separate bundles (three
imports, three chances to forget one). Not yet verified: that the GUI import picks up all
three presets from one bundle; if it imports only the printer, export a bundle from Orca
and `--extract` it to compare the layout.

### 2026-09-22 — two process presets, a second filament, and a direct installer

The bundle now carries `0.20mm Standard @Voron (repo)` at the **slicer's default speeds**
(the Voron system preset: walls 120/200, infill 200, first layer 50) and
`0.20mm Fast @Voron (repo)` with the speeds the user had been running since 08-01
(250/300/300, first layer 30, travel 400). The user asked for the default as the normal
profile and the faster one as an explicit choice; the filament's `max_volumetric_speed`
is what keeps the fast one honest, which is why that number now has a real value (23)
on the new `Creality Hyper ABS (repo)` preset instead of the inherited 250.

OrcaSlicer's *Import Configs* dropped the process preset three times in a row even after
`compatible_printers` was added, and its 2.4.2 source shows no compatibility check in the
import path, so the cause stayed unknown. Rather than keep guessing,
`scripts/install_slicer_presets.py` writes the presets straight into
`user/default/{machine,filament,process}/` with the `.info` sidecar, which is exactly
what the GUI's "save preset" produces. Same script installs the Cura material
(`cura/creality_hyper_abs.xml.fdm_material`); Cura already had normal and fast quality
profiles for the Voron, so only the material was missing. The bundle remains for
portability. Verified on disk 2026-09-22: 1 printer, 2 filaments, 2 processes in Orca,
1 material in Cura; both apps were closed at the time.
