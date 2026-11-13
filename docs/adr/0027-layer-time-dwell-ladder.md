# 0027 — Minimum layer time is measured with a dwell ladder

**Status:** Accepted · 2026-11-12 (result pending)

## Context

Cura's `cool_min_layer_time` of 12 s came from Creality's own preset, not from this
printer. On the 2026-11-12 V350 support part it cut the outer wall of a 4.5 × 35.6 mm
section from 54–57 mm/s to 22 mm/s. A 0.2–0.4 mm band appeared on layers 82–105
(z 16.6–21.6 mm). The chamber peaked at 43.9 °C during that run (bed 80).

The owner wants the minimum layer time set for this chamber and this fan, with a
measurement rather than a guess.

## Decision

[`scripts/gen_layer_time_tower.py`](../../scripts/gen_layer_time_tower.py) generates
a test print: a solid 10 mm square column, 101 layers, bed 80, chamber 50 (the
`PRINT_START` arguments), fan fixed at 70 % from layer 1.

- Bands run **12 → 10 → 8 → 6 s → natural** from the bottom up, 20 layers each (layer
  0 joins the first band). A short band that fails sits on top, so it cannot spoil the
  bands below it. "Natural" (`--times …,0`) has no dwell: the layer's own time, about
  5.5 s for this column at 60 mm/s, which is the floor of the method.
- Each layer prints at the same speed (60 mm/s; 30 mm/s on layer 0). Its total time is
  set by a park-and-dwell after the fill: retract, lift 2 mm, park 25 mm past the
  right edge, `G4` dwell, then return. The dwell is the band target minus the layer's
  estimated own time. The slicer's slowdown is never used.
- A small solid column is the worst case for heat, so it is the test that matters.
- The speed is fixed, so the band edges isolate layer time: any change in the walls
  across the bands is the cooling, not the speed.
- Judge: the shortest band whose corners stay sharp and walls straight (no bulge,
  gloss or rounding), and whose layers do not crack when flexed.

Rejected:

- **The slicer's own slowdown** (`slow_down_layer_time`). It changes the speed, which
  is the very confound this test removes.
- **Several separate prints**, one per target. Each brings its own `PRINT_START` and a
  different chamber state, so the bands would not be comparable.
- **Cura's ChangeAtZ speed factor.** It also changes the speed.

## Consequences

- The result goes to Cura's `cool_min_layer_time` in the Hyper ABS materials profile.
- The dwell is computed from an ideal-motion estimate (extrusion length at the set
  speed, travel at travel speed, 0.03 s per corner). It ignores acceleration, so the
  real layer time is a little longer than the estimate. **The layer time recorded with
  the result is the measured one:** each band starts with a
  `RESPOND MSG="layer time band …"` and the file ends with `layer time ladder end`, so
  the gap between console timestamps (Moonraker's gcode store), divided by 20, is the
  real mean layer time of that band. The generator warns when a band's target is below
  the layer's own time; the first draft's 4 s band could not be reached (about 5.5 s
  per layer), which is why the last band is "natural" instead.
- Cura's Minimum Speed should be raised near the wall speed and Lift Head enabled, so
  a short layer waits away from the part instead of slowing the walls. Orca's
  equivalent is "Don't slow down outer walls".
- The fan is fixed at 70 %, the value the material uses at its minimum layer time. A
  different fan needs another run.
- The travel from the outer perimeter to the fill start is not retracted. It is short
  and inside the column, so a little stringing is accepted for the test.
