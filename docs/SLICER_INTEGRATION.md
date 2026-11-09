# Slicer ↔ Klipper integration — gap analysis before tuning

Written before starting Phase 3 (Ellis' tuning sequence). The question: **can the
current setup actually express the things we are about to tune?**

Short answer: **partly.** Klipper's side is nearly complete. The slicer's side is close
to empty.

---

## What your slicer actually emits

Measured by tokenising a real job (`V350_meter_stepper_adapter-Adapter.gcode`, 7401 lines):

| Command | Count |
|---|---|
| `G1` / `G0` | 3853 / 3359 |
| `M82`, `G92` | 2 each |
| `PRINT_START`, `PRINT_END` | 1 each |
| `M140`, `M107` | 1 each |

**That is the entire command vocabulary.** Specifically absent: `M204` (acceleration),
`M900` (pressure advance), `G10`/`G11` (firmware retraction), `M220`/`M221` (speed/flow
factor), `SET_PRINT_STATS_INFO`/`M73` (layer progress).

The file is sliced by **Cura** (`FLAVOR:Marlin`, Elegoo Neptune thumbnail plugin).

## What Klipper supports

Verified against the pinned source (`v0.13.0-708-g7046bd00e`):

| Command | Where | Status |
|---|---|---|
| `M204` acceleration | `toolhead.py` | ✅ native |
| `M220` / `M221` speed & flow factor | `extras/gcode_move.py` | ✅ native |
| `M117` / `M73` | `extras/display_status.py` | ✅ native |
| `SET_PRESSURE_ADVANCE` | `kinematics/extruder.py` | ✅ native |
| `SET_RETRACTION` | `extras/firmware_retraction.py` | ✅ native |
| `SET_VELOCITY_LIMIT` | `extras/resonance_tester.py` | ✅ native |
| `SET_PRINT_STATS_INFO` | `extras/print_stats.py` | ✅ native |
| `TUNING_TOWER` | `extras/tuning_tower.py` | ✅ native |
| **`M205`** (Marlin jerk) | — | ❌ **not implemented** (Klipper uses `square_corner_velocity`) |
| **`M900`** (Marlin linear advance) | — | ❌ **not implemented** |

## The gaps that matter for tuning

| What we want to tune | Klipper side | Slicer side | Verdict |
|---|---|---|---|
| **Pressure advance** | `SET_PRESSURE_ADVANCE` + `TUNING_TOWER` ✅ | emits nothing | **gap** |
| **Retraction** | `[firmware_retraction]` + `SET_RETRACTION` ✅ | **Cura cannot emit `G10`/`G11`** | **gap — the config is dead** |
| **Acceleration** | `M204` ✅ | emits none | **gap** — everything runs at the global `max_accel: 5700` |
| **Flow / extrusion multiplier** | `M221` ✅ | emits none | gap (tune in the slicer profile) |
| **Layer progress in the UI** | `SET_PRINT_STATS_INFO`, `M73` ✅ | emits neither | gap (cosmetic) |
| **Z hop** | slicer-side only | done inline in G1 moves | works, but **must be off on layer 1** |
| **Exclude objects** | `[exclude_object]` ✅ | Moonraker injects on upload ✅ | ✅ solved (ADR-0006) |
| **Input shaping** | `[input_shaper]` ✅ `mzv` x=47.6 y=33 | n/a | ⚠️ **measured at the old off-centre point** |

### The retraction gap is the sharp one

`[firmware_retraction]` is configured (`retract_length: 0.75`, `retract_speed: 35`,
`unretract_extra_length: 0.05`) — and **none of it is in use**, because Cura bakes
retraction into `E` moves and has no firmware-retraction option. Consequences:

- Those three values are decorative; changing them changes nothing about a Cura print.
- `SET_RETRACTION` cannot tune retraction live, so every retraction experiment means a
  re-slice — exactly the loop Ellis' tower methods exist to avoid.

### Input shaper needs re-running

`shaper_freq_x: 47.6`, `shaper_freq_y: 33`, both `mzv`. These were measured while
`resonance_tester.probe_points` was silently `100,100,20` instead of bed centre
(ADR-0001), so they describe the machine's behaviour at the wrong place. Re-run
`SHAPER_CALIBRATE` now that `probe_points` is `175,175,20`.

### Values not set that tuning patterns tend to trip over

All currently at Klipper defaults:

| Setting | Default | Why it matters |
|---|---|---|
| `max_extrude_cross_section` | `4 × nozzle²` | PA **pattern** tests and fat purge lines draw wide extrusions and get rejected with *"Move exceeds maximum extrusion cross section"* |
| `max_extrude_only_distance` | `50 mm` | long purges / filament loads abort past this |
| `minimum_cruise_ratio` | `0.5` | shapes how accel is applied on short moves; interacts with speed/accel tuning |

These are worth raising **only if a test actually fails** — raising them pre-emptively
removes a genuine safety check.

---

## Recommendation

### 1. Use a calibration-capable slicer for the tuning phase

Cura is the weakest choice for Klipper tuning: no firmware retraction, no object
exclusion, no calibration generators, and Ellis' guide is written around
PrusaSlicer/SuperSlicer. **OrcaSlicer** ships Ellis' methods as built-in tools:

- Pressure advance — **pattern** method (the one Ellis recommends), plus tower and line
- Flow rate / extrusion multiplier, temperature tower, retraction test, VFA test
- Emits `G10`/`G11`, `M204`, `EXCLUDE_OBJECT_*` and layer info natively

Cura can stay for production work — object processing (ADR-0006) already covers the
adaptive-mesh gap. This is specifically about the tuning phase, where re-slicing loops
are the bottleneck.

### 2. Klipper-side shims worth having regardless

In `printer/macros/slicer_compat.cfg`:

- **`M900`** → `SET_PRESSURE_ADVANCE`, so Cura's Linear Advance plugin and any
  Marlin-flavoured tuning gcode works instead of erroring
- **`M205`** → accepted and ignored, mapping nothing (Klipper has no jerk); prevents
  `Unknown command` noise from Marlin-flavoured output
- **`TEST_SPEED`** — Ellis' speed/accel test, for the "maximum speeds and accelerations"
  step

### 3. Sequence

Do **[input shaper](https://ellis3dp.com/Print-Tuning-Guide/)** first (it changes how
everything else prints), then follow Ellis' order: extruder calibration → first layer
squish → pressure advance → extrusion multiplier → cooling → retraction → overlap →
stepover → flow → motor currents → speeds.

Tracked in [`TUNING_PLAN.md`](TUNING_PLAN.md) Phase 3.
