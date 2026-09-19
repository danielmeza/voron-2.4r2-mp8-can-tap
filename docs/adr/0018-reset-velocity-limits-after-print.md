# 0018 — Reset motion limits when a print ends or is cancelled

**Status:** Accepted · 2026-09-19

## Context

With `gcode_flavor: klipper`, OrcaSlicer emits `SET_VELOCITY_LIMIT ACCEL=... 
SQUARE_CORNER_VELOCITY=...` on nearly every feature change ([ADR-0012](0012-slicer-integration-for-tuning.md)
counted 1423 of them in one file). Nothing ever restores the `[printer]` values: the
slicer's end gcode is just `PRINT_END`, and `CANCEL_PRINT` does not touch motion limits.
Klipper only forgets the last value on `RESTART`.

Seen on 2026-09-18: the calibration cube had been sliced with Orca's *"Default Setting"*
process (`default_acceleration` 500, bridges at 50 %), so it emitted
`SET_VELOCITY_LIMIT ACCEL=250` on every bridge segment. The user cancelled the print just
after one of them, and KlipperScreen showed **max accel 250** for the rest of the
session. The user changed it back to 5700 by hand, not knowing where 250 had come from.
A later print would have started with whatever the cancelled file left behind.

## Decision

A single macro, `_RESET_VELOCITY_LIMITS` in `printing.cfg`, re-applies the `[printer]`
section values read from `printer.configfile.settings.printer`:

```
SET_VELOCITY_LIMIT VELOCITY={max_velocity} ACCEL={max_accel}
                   SQUARE_CORNER_VELOCITY={square_corner_velocity} MINIMUM_CRUISE_RATIO={minimum_cruise_ratio}
```

It is called from `PRINT_END` (after `M400`) and from `_USER_CANCEL`, which mainsail
runs before `CANCEL_PRINT_BASE` ([ADR-0002](0002-mainsail-owns-client-macros.md)).

Rejected alternative: **reset at the top of `PRINT_START` instead.** That would also
cover files from other sources, but it leaves the machine in the wrong state between
prints, which is exactly when the user reads the screen and jogs by hand.

## Consequences

- Values come from the config, so a change to `[printer]` propagates without editing the
  macro. `M204`-style shims from ADR-0012 are unaffected.
- The reset runs before the park move in `_USER_CANCEL`, so a cancel parks at full
  configured limits rather than at a bridge's 250 mm/s².
