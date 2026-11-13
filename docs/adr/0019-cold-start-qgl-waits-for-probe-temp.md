# 0019 — Cold start: wait for the nozzle to reach probe temperature before the first QGL

**Status:** Accepted · 2026-09-19

## Context

`PRINT_START` issues `M104 S150` at the top so the hotend heats during homing and the
pre-QGL ([ADR-0003](0003-temperature-gated-soak.md), revision 2026-07-24). On a warm
start that is enough. On a cold start the first `QUAD_GANTRY_LEVEL` begins while the
hotend is still climbing, and TAP probes with a nozzle that is expanding under it.

Evidence, recovered from the OpenCode session of 2026-08-03 (never recorded at the time):
a print started with bed 38 °C, chamber 34.8 °C. During the first QGL the probe readings
at one corner drifted **-0.319 → -0.55 mm** across retries while the hotend rose to
150 °C. That blew `samples_tolerance: 0.005` after `samples_tolerance_retries: 3`, and
`[virtual_sdcard] on_error_gcode: CANCEL_PRINT` cancelled the job after 32 s. The retry
ten minutes later, on a warmer machine, went through.

Retries also cost time when they succeed. The completed print of 2026-09-18 (warm,
nozzle already at 150 °C) needed **three** QGL retries at `retry_tolerance: 0.01`, with
probed ranges 0.031, 0.015, 0.021 and finally 0.009 mm: about 100 s. Several individual
points also re-probed on "samples exceed tolerance". The cold start of 2026-09-19 opened
with a range of **0.98 mm** because the Z2 corner had dropped 1.3 mm while the motors
were off after an emergency stop (the same corner, 0.85 mm, after the 08-03 restart).
Z2 is the **rear-right** motor (`steppers.cfg`: "Rear Right Z2 Stepper", Manta MOT6): the
rear-right probe point (300, 275) read 0.56 mm high and QGL lifted `stepper_z2` by
1.33 mm. Belt and pulley on that motor are the first thing to check.

QGL on a 350 mm gantry is the part of this machine the user names as the main pain, so
the record has to say what was tried and what was not.

## Decision

Before `_QUAD_GANTRY_LEVEL_IF_NEEDED`, `PRINT_START` waits for the nozzle to **reach**
150 °C, but only when QGL is not yet applied:

```
{% if printer.quad_gantry_level.applied == False %}
    M109 S{preheated_z_home_temp}
{% endif %}
```

On a warm start (standby held the motors, QGL still applied) this is skipped and costs
0 s. On a cold start the bed is already heating from the top of the macro (M140, ADR-0003
revision 2026-09-19), so the ~40 s nozzle wait overlaps the 5-minute bed heat and is free.

**Not changed:** `retry_tolerance` (0.01) and `samples_tolerance` (0.005). Loosening
them would hide drift instead of removing it, and the one number that would justify a
change, `PROBE_ACCURACY` at 150 °C over a 100 °C bed, has not been measured; the 0.0019 mm
figure in [ADR-0013](0013-probe-samples-reduced.md) was taken on a warm machine.

Rejected alternatives:

- **`retry_tolerance: 0.015–0.02`**, the common TAP setting. Would have saved ~60 s on
  09-18 but would not have saved the 08-03 print, whose drift was 0.23 mm.
- **A fixed settle dwell after 150 °C.** Plausible (the heat block keeps expanding after
  the sensor reads 150), but unmeasured. Left as the next experiment.

## Consequences

- A cold start does homing → nozzle wait → QGL → ... → second QGL after the soak, as
  before. Only the wait is new.
- Open items, in order: measure `PROBE_ACCURACY` hot; then decide on `retry_tolerance`;
  investigate why Z2 drops when motors release (belt tension or motor holding torque).
- If a cold-start QGL still cancels after this change, the record above says the drift
  was ~0.23 mm at 0.005 tolerance, so the next lever is the settle dwell, not the samples.

## Revisions

### 2026-09-19 — why Z2 drops on motor release: cable-chain load (user's account)

The user's explanation for the Z2 drop in Context: the rear-right corner carries more
weight. The cable chain and the CAN cable to the toolhead board both run down that side,
so when the Z motors are released the belted Z has no holding torque and the heaviest
corner settles first. That makes it a static load, not a belt or pulley fault, and fits
the pattern of always Z2, always after a motor release (e-stop, power cut, standby
expiry). It is the user's account, not a measurement; the cheap check would be the drop
on release with the chain load taken off that corner.

If it holds, the open item "investigate why Z2 drops" needs no action beyond what is in
place: the pre-QGL runs whenever `quad_gantry_level.applied` is false, and PRINT_END keeps
the motors energised while standby holds ([ADR-0005](0005-between-session-keep-warm.md)),
so a warm restart does not need it. Recorded here instead of rewriting Context: the
first version of this entry (b3fcc0c) edited Context in place and dropped the
belt-and-pulley reasoning, which the ADR rules say to keep.

### 2026-11-12 — cooling waits use the part fan

A back-to-back start waited passively from 217.9 °C to 155 °C before the probe could run, with the part fan off. `_NOZZLE_COOL_TO` (in `gantry/z_calibration.cfg`) sets the hotend target to the wanted temperature, turns the part fan on at 255 while the nozzle is hot, waits, then restores the previous fan speed. It is called from `[probe] activate_gcode`, in place of the bare `TEMPERATURE_WAIT`, and from both pre-QGL `M109 S{preheated_z_home_temp}` waits in `PRINT_START` (with `MARGIN=1`).

Trade-off: the fan blows briefly while the nozzle is over the bed, before the first probe. Accepted because the passive wait it removes took minutes.

`_NOZZLE_COOL_TO` only ever *lowers* the hotend target. A nozzle that is hot with its target at 0, as after `TURN_OFF_HEATERS`, cools with the fan and stays off. The first draft set `M104 S150` unconditionally, which would have left the heater on at 150 °C in that case; the draft was caught in review before it was synced.
