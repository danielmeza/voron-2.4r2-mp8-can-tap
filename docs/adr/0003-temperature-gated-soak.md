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

---

## Revisions

### 2026-07-24 — gate relocated, exhaust conflict fixed, wait bounded

Three defects surfaced the first time this ran on the machine. The decision (gate on
measured temperature, not a timer) stands; the implementation was wrong three ways.

1. **The chamber exhaust fan was fighting the gate.** `PRINT_START` runs
   `M141 S{CHAMBER}` — 36 from the slicer — and a `[temperature_fan]` runs its fan
   *above* its target. The exhaust (`MP8:FAN3`) therefore sat at speed 1.0 venting the
   chamber while the gate waited for 40 °C. **Evidence:** chamber pinned at ~38 °C
   climbing 0.29 °C/min with `fan=1.0`. The exhaust target is now held at
   `max(slicer target, gate + 10)` from the top of `PRINT_START`.

2. **The gate sat too early**, right after `M190`, where only the bed heats the chamber.
   **Evidence:** 0.29 °C/min there, versus the chamber reaching 44.9 °C within a minute
   of the hotend hitting temperature. Moved after `CLEAN_NOZZLE` + the 150 °C nozzle
   preheat + QGL, so that time counts and the hotend contributes.

3. **`TEMPERATURE_WAIT` could not be interrupted.** `CANCEL_PRINT` **queues behind it**,
   so a cancel did nothing for ~15 minutes. This ADR had listed the missing timeout as an
   accepted risk; it bit. Replaced with a bounded series of `_SOAK_STEP` calls — each a
   separate macro invocation, so it re-reads the temperature and no-ops once the gate is
   met. A Jinja loop cannot do this (`printer.*` evaluates once at expansion) and a
   self-recursive macro is rejected by Klipper's recursion guard.

**Result:** the gate cost **0 s** on the next warm start (chamber already 44 °C).

### 2026-07-24 — hotend preheat moved to the top of PRINT_START

Once [ADR-0005](0005-between-session-keep-warm.md) made `M190` return in ~2 s, the
preheat placed just before it had no runway. **Evidence:** hotend still at 52 °C when
`CLEAN_NOZZLE` ran, whose `M109` then blocked ~46 s. `M104 S150` now issues at the very
top so it heats during homing (~41 s) and the pre-QGL (~94 s), which are dead time.
