# 0002 — mainsail.cfg owns PAUSE / RESUME / CANCEL_PRINT

**Status:** Accepted · 2026-07-24

## Context

`macros/printing.cfg` defined `PAUSE`, `RESUME` and `CANCEL_PRINT`. So does
`mainsail.cfg`. Because `[include mainsail.cfg]` is the **last** include in
`klippy.conf`, and Klipper merges duplicates last-wins (see
[0001](0001-single-owner-config-sections.md)), mainsail won all three.

Confirmed on the live machine — `rename_existing` read `PAUSE_BASE` / `RESUME_BASE` /
`CANCEL_PRINT_BASE`, i.e. mainsail's, not the local copies. The local macros had been
dead for as long as `mainsail.cfg` has been included.

Real behaviour that was silently lost:

- `CANCEL_PRINT` never called `PRINT_END`, so chamber-to-40 °C and part-fan-off never ran
- `CANCEL_PRINT` never called `STOP_HEAT_SOAK`, so **a heat soak survived a cancel**
- `PAUSE`/`RESUME` never disabled or restored the filament sensor

This was invisible until `mainsail.cfg` was tracked in git — it is a *literal*
(non-wildcard) include, so Klipper hard-errors without it and the repo could not
restore a working printer.

## Decision

Keep mainsail's macros. Delete the local copies. Reattach the missing behaviour via
`_CLIENT_VARIABLE`, the supported extension point.

Rejected alternative — reordering the include so the local copies win:

1. `mainsail.cfg` states in its own header *"This file is read-only"* and is managed by
   Moonraker's update manager (`mainsail-config`), so the fight resumes on every update.
2. Mainsail's versions are genuinely better: runout-sensor state, `can_extrude` checks,
   idle-timeout save/restore, UI prompts.
3. The local copies are already dead, so keeping mainsail's is the *status quo*.

Wiring lives in `printer/macros/client_variables.cfg`: `_USER_PAUSE`, `_USER_RESUME`
and `_USER_CANCEL`, referenced from `_CLIENT_VARIABLE`.

## Consequences

- Mainsail updates no longer conflict with local macros.
- `_USER_CANCEL` deliberately does **not** call `PRINT_END`. Mainsail's `CANCEL_PRINT`
  already does `TURN_OFF_HEATERS`, `M106 S0`, retract and park; duplicating those moves
  would fight it. Only the genuinely missing actions are reattached.
- The `user_*_macro` variables accept a **single line only**, hence the indirection
  through named macros.
- `variable_runout_sensor` must be the exact object name —
  `filament_motion_sensor filament_sensor`, a *motion* sensor, not a switch sensor.
- Park-at-cancel coordinates are now configuration, not code.
