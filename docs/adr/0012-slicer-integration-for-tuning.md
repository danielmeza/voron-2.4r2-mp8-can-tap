# 0012 — Slicer integration for the tuning phase

**Status:** Accepted · 2026-07-25

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
