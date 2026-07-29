# 0006 — Adaptive mesh per print instead of a stale saved mesh

**Status:** Accepted · 2026-07-24

## Context

`PRINT_START` had `variable_mesh_bed_before_print: 0`, so every print loaded a saved
`default` profile captured at some earlier, unknown thermal state. Ellis, on exactly
the symptom seen here (squish inconsistent across the bed):

> *"I personally recommend generating a bed mesh before every print"* — because
> *"The bed and gantry can warp with heat."*

The measured saved mesh has a **0.1487 mm range**, strongly Y-dominated: front
`+0.014`, middle `−0.055`, back `+0.033`, worst point `x=30, y=175 → −0.0988`. Applying
a stale mesh of that magnitude produces error that grows with distance from the homing
point — which matches "worse away from the centre" precisely.

A full 11×11 mesh is 121 probe points, far too slow to run before a 5–10 minute part.

## Decision

`BED_MESH_CALIBRATE ADAPTIVE=1 ADAPTIVE_MARGIN=5`, with
`variable_mesh_bed_before_print: 1`.

Adaptive meshing probes only the bounding box of the objects being printed, plus a
margin, clamped to the configured `mesh_min`/`mesh_max`. A small centred part costs
seconds; a bed-filling print still gets full coverage where it matters. The trade-off
between "fresh mesh" and "fast start" **disappears** rather than being split.

`ADAPTIVE_MARGIN: 5` mm covers brim and skirt beyond the part outline (Klipper's
default is 0).

## Consequences

- Requires the slicer to emit `EXCLUDE_OBJECT_DEFINE`. `[exclude_object]` is already
  enabled in `klippy.conf`.
- **Fails safe.** Verified in `bed_mesh.py`: with no `exclude_object` module it logs
  *"Exclude objects not enabled. Using full mesh..."*, and with an empty object list it
  returns `False` — both fall back to a full mesh. It does **not** error out mid-print.
- The fallback is the *slow* path, so if the slicer is not emitting object definitions
  every print silently pays a 121-point mesh. **Verify on the first print** by looking
  for `Found N objects` in the console.
- Object definitions must appear *before* `PRINT_START` runs. This is slicer- and
  version-dependent and is the most likely thing to need adjusting.
- A prebuilt full mesh remains useful as a reference and for
  [0004](0004-frame-temperature-sensing.md)'s calibration; adaptive meshing does not
  replace having one good equilibrium mesh on file.

---

## Revisions

### 2026-07-24 — Moonraker's preprocessor makes this work with Cura

This ADR noted the fallback to a full mesh was "safe but slow" and that object
definitions were slicer-dependent. On this machine they were **never** present: Cura
does not emit `EXCLUDE_OBJECT_DEFINE`, so every print silently paid the 121-point path —
measured at **~12 minutes**, because `[probe] samples: 3` makes that 363 probe cycles.

Moonraker **bundles** `preprocess_cancellation` and can inject the definitions on upload.
It was simply off: `moonraker.conf` had no `[file_manager]` section at all.

```ini
[file_manager]
enable_object_processing: True
```

**Verified:** the same Cura file went from **0 → 36** `EXCLUDE_OBJECT_DEFINE` entries with
correct polygon bounds. On the next print Klipper reported *"Found 1 objects"*,
*"Adapted probe count: (3,3)"* — **9 points, 44 s** instead of 121 points and ~12 min.

No slicer change was needed. This also enables per-object cancellation in Mainsail.
