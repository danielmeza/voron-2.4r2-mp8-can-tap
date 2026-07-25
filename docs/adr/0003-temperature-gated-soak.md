# 0003 — Gate the heat soak on measured temperature, not a timer

**Status:** Accepted · 2026-07-24

## Context

Adhesion was poor away from the bed centre. Ellis' guide is blunt about enclosed
printers: *"ensure that you are heat soaking for **at least an hour**."*

But this printer's workload is back-to-back **5–10 minute** parts with a 10–15 min
gap to swap them. A flat one-hour soak per session would dwarf the print. Ellis'
figure is **cold-start advice, not a per-session tax**.

Two things were found while implementing:

1. **There was no soak at all.** `PRINT_START` computed `heatsoack_duration`
   (15/10/5 min), printed it in a status message, and then ran `M190 S{target_bed}`
   in *both* branches of the `heatsoak_enabled` conditional. The real `HEAT_SOAK`
   call was commented out. The variable was never used for anything.
2. `TEMPERATURE_WAIT` **returns immediately** when the sensor is already in range,
   and accepts any passive `temperature_sensor` (verified in
   `klippy/extras/heaters.py` — it falls back to `printer.lookup_object`).

## Decision

Gate on measured chamber temperature instead of elapsed time. One mechanism serves
both cases: cold machine waits as long as it genuinely needs, warm machine waits
**zero seconds**.

Gates are material-dependent, because the chamber ceiling depends on bed temperature.
Measured from Moonraker's temperature store during a real print: with the bed at
100 °C the chamber plateaus at **44–47 °C**.

| Bed target | Chamber gate | Reasoning |
|---|---|---|
| > 90 °C (ABS/ASA) | 40 °C | Plateau is 44–47 °C, so 40 °C has margin on a cool day |
| > 65 °C (PETG) | 30 °C | Chamber never approaches 40 °C at these bed temps |
| ≤ 65 °C (PLA) | none | A 40 °C gate with a 60 °C bed would **hang forever** |

Plus a `cold_start_extra_min` dwell (10 min) applied only when the chamber is below
30 °C at `PRINT_START`, letting the frame catch up to the air on the day's first print.

The existing `HEAT_SOAK` macro was **not** used despite being more sophisticated
(it detects equilibrium by rate-of-change). It is asynchronous and calls `PAUSE`
when a print is active, which does not compose with a blocking `PRINT_START`.

## Consequences

- The first print of a session pays a real soak; subsequent ones pay nothing.
- Material-dependent gates are essential, not a nicety — a single fixed gate would
  deadlock on PLA. This is the main trap for anyone editing these values.
- **`TEMPERATURE_WAIT` has no timeout.** If the chamber cannot reach its gate (door
  open, failed bed heater) `PRINT_START` waits indefinitely. Accepted because the
  gate has ~5 °C of measured margin and the operator can cancel. Revisit if it ever
  bites.
- Chamber *air* warms well before the *frame* does, so this reduces but does not
  eliminate thermal drift. It buys speed; [0004](0004-frame-temperature-sensing.md)
  buys accuracy. Both are needed.
- Gate values are `PRINT_START` variables, tunable without touching logic.
