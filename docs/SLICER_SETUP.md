# Slicer setup — opening the tuning gate

Concrete profile for driving this printer from a PrusaSlicer-lineage slicer
(**OrcaSlicer** recommended — see [ADR-0012](adr/0012-slicer-integration-for-tuning.md)).
Every value below was read from the **live machine**, not transcribed from memory.

Cura can stay for production work; this is about unblocking Phase 3 tuning, where
retraction and pressure advance are untunable from Cura.

---

## 1. Machine limits

| Slicer field | Value | Source |
|---|---|---|
| Max print speed | **400** mm/s | `printer.max_velocity` |
| Max acceleration | **5700** mm/s² | `printer.max_accel` |
| Max Z speed | **20** mm/s | `printer.max_z_velocity` |
| Max Z acceleration | **350** mm/s² | `printer.max_z_accel` |
| Jerk | *not applicable* | Klipper uses `square_corner_velocity: 8.0` |

**Set "machine limits" to _"Use for time estimate only"_.** Klipper owns these; a slicer
re-emitting them is at best redundant. If your slicer emits them anyway, `M201`/`M203`/
`M205` are now accepted and ignored (`printer/macros/slicer_compat.cfg`) rather than
aborting the print — but silence is better than a shim firing every job.

## 2. Build volume

- **350 × 350 × 300 mm**, origin at front-left.
- **Keep prints inside 30–320 mm in X and Y.** That is `bed_mesh`'s
  `mesh_min`/`mesh_max`; outside it the mesh extrapolates rather than measures.

## 3. Extruder / filament

- Nozzle **0.4 mm**, filament **1.75 mm**
- `min_extrude_temp` is **160 °C** — nothing extrudes below that, including test towers
- `max_extrude_cross_section` is **0.64 mm²** (Klipper default, `4 × nozzle²`).
  For scale, the existing purge line already runs at ~0.48 mm² — **75 % of the limit**.
  Wide PA-pattern lines or a fat purge can exceed it and abort with
  *"Move exceeds maximum extrusion cross section"*. Raise it **only if a specific test
  trips it**; it is a real safety check.

## 4. Retraction — slicer by default, firmware when tuning

**Leave "Use firmware retraction" OFF for normal printing** (the repo printer profile
ships it off since 2026-09-22, [ADR-0021](adr/0021-slicer-retraction-by-default.md)).
The slicer then owns retraction: length and speed per filament, "wipe while
retracting", retract-on-layer-change, minimum travel. Files carry `G1 E-…` moves and
Klipper's `[firmware_retraction]` section stays loaded but receives nothing.

**Tick it only while calibrating a new filament.** With it on the slicer emits
`G10`/`G11`, Klipper's values take over, the slicer's retraction fields are ignored,
and `SET_RETRACTION` tunes retraction **live, mid-print** with no re-slice per
experiment. When the number is found, copy it into the filament preset and untick.
The two methods never stack: a file uses one or the other, and the verifier (§7)
fails a file that carries both.

Current Klipper values (the starting point to tune from):

| | |
|---|---|
| `retract_length` | 0.75 mm |
| `retract_speed` | 35 mm/s |
| `unretract_extra_length` | 0.05 mm |
| `unretract_speed` | 35 mm/s |

## 5. Object exclusion

Enable **"Exclude objects"** / label objects. Klipper has `[exclude_object]` and this
also drives **adaptive bed meshing** ([ADR-0006](adr/0006-adaptive-bed-mesh.md)) — the
difference between a 9-point mesh and a 121-point one.

Moonraker's `enable_object_processing` is on and injects these for slicers that cannot.
Once the slicer emits them natively the preprocessor becomes a harmless no-op.

## 6. Start / end gcode

**Start:**

```
PRINT_START EXTRUDER=[nozzle_temperature_initial_layer] BED=[bed_temperature_initial_layer_single] CHAMBER=[chamber_temperature]
```

**End:**

```
PRINT_END
```

Two traps:

- **Do not let the slicer heat before the macro.** `PRINT_START` sequences heating,
  soak gating, QGL and meshing itself. A slicer that emits `M190`/`M109` first will heat
  in the wrong order and defeat the thermal work. Clear any temperature commands from
  the machine start gcode.
- **Placeholder names differ between slicers and versions.** Do not trust the snippet
  above blindly — verify against the emitted file (§7). If `CHAMBER` comes through empty,
  `PRINT_START` defaults it to 40.

## 7. Verification — do this before trusting anything

Slice any small object and check the output.

```bash
python3 scripts/verify_slicer_gcode.py path/to/sliced.gcode
```

Checks the placeholder/ordering/presence rows below automatically. The two
console-only rows at the end (`Found N objects`, `Adapted probe count`) still need a
real print to observe.

| Must be present | Why |
|---|---|
| `PRINT_START ... EXTRUDER=<n> BED=<n>` with **real numbers** | placeholders resolved |
| exactly one of `G10`/`G11` **or** `G1 E-…` | one retraction owner per file (ADR-0021); default is the slicer |
| `EXCLUDE_OBJECT_DEFINE` | adaptive meshing works |
| `M204` **or** `SET_VELOCITY_LIMIT ACCEL=` | per-feature acceleration |
| `SET_PRINT_STATS_INFO` or `M73` | layer progress in the UI |

**On the acceleration row:** with `gcode_flavor: klipper` OrcaSlicer emits Klipper's
native `SET_VELOCITY_LIMIT ACCEL=... SQUARE_CORNER_VELOCITY=...` instead of `M204` —
that is the *better* output, not a missing feature. Verified 2026-07-30 on a real
slice: 1423 `SET_VELOCITY_LIMIT` and zero `M204`.

| Should be absent | Why |
|---|---|
| `M190` / `M109` **before** `PRINT_START` | breaks the heating order |
| `M201` / `M203` / `M205` | harmless now, but means limits are being emitted |

Then print once and confirm the console shows `Found N objects` and an
`Adapted probe count` smaller than 11×11.

## 8. What tuning this unlocks

With the above in place, Ellis' sequence becomes practical:

- **Pressure advance** — `SET_PRESSURE_ADVANCE` live, `TUNING_TOWER` for towers, or
  OrcaSlicer's built-in **pattern** method (the one Ellis recommends)
- **Retraction** — `SET_RETRACTION` live instead of a re-slice per data point
- **Flow / extrusion multiplier** — `M221` live
- **Speeds and accelerations** — `TEST_SPEED` (in `slicer_compat.cfg`) plus per-feature
  `M204`

Input shaping is already done ([ADR-0012 revisions](adr/0012-slicer-integration-for-tuning.md)):
**x `2hump_ei` @ 78.6 Hz, y `mzv` @ 35.8 Hz.**
