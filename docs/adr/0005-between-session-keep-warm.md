# 0005 — Hold the bed warm between sessions

**Status:** Accepted · 2026-07-24

## Context

The thermal gate in [0003](0003-temperature-gated-soak.md) only pays off if the
machine is still warm when the next print starts. The working pattern here is a
10–15 minute gap to remove the part and clean the plate — long enough for the bed and
frame to shed a lot of heat, which would make every session pay the cold-start cost.

## Decision

`PRINT_END` starts `STANDBY_WARM`, holding the bed at the temperature the finished
print was using, when that was ≥ 90 °C. A `[delayed_gcode]` releases it after
**25 minutes**.

Holding at the *print* temperature rather than something lower (60–70 °C) is
deliberate: the goal is keeping the **frame** at the equilibrium it already reached.
Letting it sag to a lower plateau reintroduces exactly the drift being engineered away.

`STOP_STANDBY_WARM` cancels manually, and `_USER_CANCEL` calls it so an aborted print
does not leave the bed holding heat.

## Consequences

- **The 25 minute timeout is not arbitrary.** `[idle_timeout]` is 1800 s (30 min) and
  runs `TURN_OFF_HEATERS`. Standby must expire *first*, or it gets silently killed and
  the behaviour becomes confusing. **Anyone changing `idle_timeout` must revisit
  `variable_timeout_min`.**
- Standby only engages for ABS/ASA-range beds (≥ 90 °C). Below that, frame drift
  matters much less and the energy is not worth it.
- The bed sits at ~100 °C unattended for up to 25 min. Bounded and auto-expiring, but
  it *is* an unattended heater — accepted deliberately.
- `_STANDBY_WARM_OFF` and `STOP_STANDBY_WARM` both guard on
  **`print_stats.state != "printing"`** so they cannot turn the bed off mid-print if a
  new job started during the window.
- **Do not use `idle_timeout.state` for that guard.** It is a *busy* flag, not a job
  flag: Klipper sets it to `Printing` while executing **any** command — including the
  guarding macro itself — so the condition is always false at the moment it matters and
  the bed is never released. This was caught by smoke-testing the macro and finding the
  bed still targeting 95 °C after `STOP_STANDBY_WARM` returned `OK`.
- Energy cost is real but small compared with re-soaking a 350 mm chamber.

---

## Revisions

### 2026-07-24 — it never actually fired

`PRINT_END` decided "was this an ABS print?" by reading `heater_bed.target`. Slicers emit
`M140 S0` immediately **before** `PRINT_END` — verified in the gcode: `M140 S0` on line
7383, `PRINT_END` on 7384 — so it always read 0 and standby never engaged.
`PRINT_START` now records the target into a `PRINT_END` variable.

**Result once fixed:** bed held at 100 °C between prints, and the next `M190` returned in
**~2 s instead of 355 s**.

### 2026-07-24 — standby now holds the motors too, and engages earlier

Two refinements, both from measured behaviour:

- **Motors are no longer released while standby holds.** `quad_gantry_level.applied` is
  reset by exactly one event — `stepper_enable:motor_off` (`klippy/extras/z_tilt.py`) —
  so the `M84` in `PRINT_END` was forcing the *next* print to run two full QGL passes.
  **Evidence:** first QGL of a print after `M84` started at a 0.626 mm range (the gantry
  sags unpowered) and took 94 s, then the real QGL ran again for 59 s.
  `PRINT_END` now skips `M84` while standby engages, so
  `_QUAD_GANTRY_LEVEL_IF_NEEDED` skips itself. Motors are released when standby expires
  or by `[idle_timeout]` at 30 min — so a genuinely cold start still gets both passes,
  which is the correct behaviour.
- **Standby engages immediately after `TURN_OFF_HEATERS`**, not at the end of
  `PRINT_END`. The macro takes ~2 min to finish (there is a `G4 P120000` party dwell),
  and the bed was cooling for all of it — defeating most of the point.
