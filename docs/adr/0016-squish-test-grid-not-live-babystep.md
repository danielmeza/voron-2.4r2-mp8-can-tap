# 0016 — First-layer squish: a static test grid, not live babystepping

**Status:** Accepted · 2026-07-30

## Context

Ellis' guide tunes first-layer squish by printing a single test object and adjusting
Z live (babystepping) while watching it print, judging by eye in real time. Rejected
here by direct preference — the live-judgment method doesn't produce anything to
compare after the fact, and real-time visual judgment during a moving print is harder
to do well than comparing finished, stationary results side by side.

## Decision

`SQUISH_TEST_GRID` (`macros/squish_test.cfg`): prints 9 small squares in a 3x3 grid,
each at a different `SET_GCODE_OFFSET Z=` relative to the probe's calibrated baseline
(`z_offset: -0.529`, [TUNING_PLAN.md](../TUNING_PLAN.md)). Step size **0.04mm** between
adjacent squares, centred on the grid's middle square (row 1, col 1 = baseline,
delta 0) — range `-0.16` to `+0.16` relative to baseline across the 9 squares.

Deliberately thick layer height (0.25mm) and the Tier A first-layer line width
(0.48mm = 120% of the 0.4mm nozzle) — Ellis' convention for making squish differences
easy to see. The macro only prints the pattern; homing, QGL, meshing the grid's
footprint, and heating are the caller's job (kept separate — each of those is already
a proven, individually-tested console step in this project, no need to fold them into
one big macro).

Rejected alternatives:

- **Ellis' live-babystep method as documented.** Explicitly not wanted — see Context.
- **A single line/tower instead of a grid.** Would need serial reprints (one Z value
  per attempt) to get the same comparison; the grid gets all 9 data points in one
  heat cycle, one mesh, one job.
- **Fold heating/mesh/QGL into the macro.** Would duplicate console steps already
  proven working earlier in this project, for a macro whose only real job is the
  print pattern.

## Consequences

- Mesh **must** cover the grid's footprint (`X145-210, Y145-210` + margin) before
  running this — without it, real bed unevenness (saddle shape, up to ~0.15mm
  measured) would confound the intentional 0.04mm-per-square steps. Run
  `BED_MESH_CALIBRATE MESH_MIN=... MESH_MAX=...` bounded to the grid first.
- `line_width` and `filament_diameter` are hardcoded/read from live config
  respectively — this macro has no slicer behind it, so extrusion math it would
  normally get for free has to be supplied by hand.
- Reusable for any future re-squish (nozzle swap, plate change, etc.) — this is a
  tool, not a one-shot script, kept in the repo for that reason.
- **Run 2026-07-30.** All 9 squares printed clean, no errors, gcode offset correctly
  reset to 0 after. Back-left square (row 2, col 0, delta +0.08) judged best by eye
  and peel test — fully fused lines, no gaps, doesn't lift easily. Final
  `z_offset: -0.529 → -0.449` (see [TUNING_PLAN.md](../TUNING_PLAN.md)).
- Notably, the winning square was **less** squish than the paper test's baseline
  (+0.08, nozzle further from bed), not more — the grid caught something a single
  live-judged pass could easily have missed or second-guessed.

---

## Revisions

### 2026-08-02 — z_offset conversion had the wrong sign: -0.449 → -0.609

The grid's verdict was read correctly ("+0.08, nozzle further from bed") but
converted to a probe z_offset with the wrong sign. The original note
`-0.529 + 0.08 = -0.449` moved the nozzle **down** 0.08 mm from the paper-test
baseline the grid had already judged too squished — 0.16 mm off the tested
optimum. Evidence that forced the change (Klipper source, not inference):

- `klippy/extras/gcode_move.py`, `cmd_SET_GCODE_OFFSET`: `base_position[pos] +=
  delta` and `cmd_G1` commands `v + base_position[pos]` — a `Z=+0.08` offset
  raises the toolhead 0.08 mm. The winning square really was less squished.
- `klippy/extras/probe.py`, `HomingViaProbeHelper`: the probe's `z_offset` is
  passed in as the Z `position_endstop`, so the position assigned at trigger
  equals `z_offset`. Making it less negative lowers the nozzle at every gcode Z.
- `klippy/extras/probe.py`, `Z_OFFSET_APPLY_PROBE`: `new_calibrate = z_offset -
  offset` — Klipper's own formula for baking a +0.08 babystep into `z_offset` is
  to **subtract** it: `-0.529 - 0.08 = -0.609`.

Symptom that forced the change: first layers crushed (nominal 0.2 mm first layer
landing in a ~0.04–0.12 mm gap) and finished parts measuring ~0.3–0.5 mm short.
The grid method itself stands — the method found the right answer; only the
arithmetic applied to the config was wrong.

### 2026-08-02 — fine-grid re-run confirms -0.609; -0.569 was over-squish

Two follow-up grids at `z_offset: -0.609` (25 mm bordered squares, border rings +
interior fill):

1. **±0.04 mm step** (default `variable_z_step`): center square (delta 0) had the
   best vertical line pattern; its small fill/wall junction gaps were judged a
   flow/PA artifact, not under-squish. Center-left (delta −0.04, 0.04 mm more
   squash) showed gapless joins and was initially selected → baked
   `-0.609 - (-0.04) = -0.569`.
2. **-0.569 run**: every square over-squished — raised line borders, lines
   peaking at each edge. Empirically decisive that more squash is wrong; reverted
   to `-0.609`.
3. **±0.002 mm step** (`Z_STEP=0.002`, range center ±0.008): center square
   (delta 0) confirmed **best**. No change applied.

Lessons recorded:
- A "gapless" join can be the *over*-squish signature, not the correct contact —
  judge the whole square, not just junction gaps.
- Join gaps at correct squish are a flow/PA artifact; address at print speed, not
  by changing Z.
- `Z_OFFSET_APPLY_PROBE` direction already validated; the grid's center (delta 0)
  matching the live `z_offset` is the consistency check.

