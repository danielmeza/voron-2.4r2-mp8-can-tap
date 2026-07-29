# 0001 — One file owns each config section

**Status:** Accepted · 2026-07-24

## Context

This config splits logical sections across many files (`[extruder]` in three files,
`[tmc2209 stepper_z]` in two, `[resonance_tester]` in three, `[probe]` in three).

Klipper reads includes with `glob.glob()` — **not** recursive, so `**` behaves exactly
like `*` — sorts the matches, and parses everything with
`RawConfigParser(strict=False)`. Duplicate options are therefore accepted **silently**,
and the **last file loaded wins**. Nothing warns you.

The result was settings nobody chose. Verified against the running machine:

- `min_extrude_temp` — `190` in `extruder.cfg` shadowed by `160` in `heatend.cfg`
- `resonance_tester.probe_points` — `175,175,20` shadowed by `100,100,20`, so every
  input-shaper measurement was taken off-centre
- `tmc2209 stepper_z/z2/z3 run_current` — `0.8` shadowed by `1`, while `stepper_z1`
  had no entry in `stepper_drivers.cfg` at all and kept `0.8`. **Three Z motors ran
  at 1.0 A and the fourth at 0.8 A.**
- `[gcode_macro M190]` — two different bodies, one silently discarded

## Decision

Exactly one file owns each section, by domain:

| Concern | Owner |
|---|---|
| Stepper driver settings (`uart_pin`, `run_current`, `interpolate`, …) | `gantry/stepper_drivers.cfg` |
| Motor/kinematic values (`microsteps`, `rotation_distance`, `gear_ratio`) | `gantry/steppers.cfg` |
| Everything about the toolhead extruder | `stealburner/stepper.cfg` |
| `[resonance_tester]` | `gantry/input_shaping.cfg` |

Z run current is set to **0.9 A on all four motors** — 43 % of the OMC 17HS24-2104S
2.1 A rating, mid-band for Ellis' "start at 40–50 %, never exceed 70 %".

`scripts/klipper_config_lint.py` replicates Klipper's load semantics and fails CI on
any conflicting duplicate, so this cannot silently regress.

## Consequences

- Effective values are now intentional and greppable in one place.
- The linter must be run after config edits; it exits non-zero on ERROR.
- Two duplicate files (`gantry/rezonance.cfg`, `stealburner/resonance.cfg`) were
  deleted outright.
- `[probe]` is still split across three sections in two files. Left alone
  deliberately — it is safety-critical for TAP and merges without conflict today —
  but it is the same fragility and is tracked as backlog.
- Changing Z current from an accidental mix to a uniform 0.9 A is a **real behaviour
  change** and needs re-verification of QGL and Z motor temperature under load.
