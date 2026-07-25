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
