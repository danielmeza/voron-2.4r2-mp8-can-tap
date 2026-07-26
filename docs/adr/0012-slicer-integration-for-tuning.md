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
