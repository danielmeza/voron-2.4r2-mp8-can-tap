# 0024 — The SFS V2.0 runs both of its sensors, alongside the EBB one

**Status:** Accepted · 2026-11-11

## Context

A BigTreeTech Smart Filament Detection Module V2.0 is installed at the spool. It is a
different animal from the V1.0 the vendor's README mostly documents: the V2 has **two**
outputs and a stated detection accuracy of **2.88 mm** (the V1 was 7 mm), and BTT's own
Klipper example configures a `[filament_switch_sensor]` *and* a
`[filament_motion_sensor]` for it.

The vendor's *SFS V2.0 User Manual* (v1.10, 2023-11-23) adds three facts this record
depends on:

- **Rated voltage 3.3–5 V**, so the endstop header's `+5V` is in spec.
- **"Detection Length: 2.88 mm (theoretical detection length, may need adjustment)"** —
  the vendor does not claim it holds on a given machine.
- Its own Manta M8P V2.0 wiring section uses `switch_pin: ^PF0` for the switch and
  `switch_pin: ^PC15` for the encoder, i.e. the last two endstop headers, **with pull-ups
  and no invert flag**. The installed wiring uses `PF2` (blue, switch) and `PF1` (green,
  motion) instead — electrically the same block of endstop inputs, with the same `+5V`
  and `GND`, so the choice is equivalent and not arbitrary.

Wiring as installed: **blue = switch, on PF2** (the `ESTOP3` endstop header) and
**green = motion, on PF1** (`ESTOP4`). Both endstop headers carry `+5V` and `GND`, which
is where the module is powered from. Neither pin was referenced anywhere in the config
before this change, so no endstop lost its input.

The printer already has a runout/jam guard: `[filament_motion_sensor filament_sensor]` on
`EBB:FILAMENT_PROBE`, `detection_length: 10`, `pause_on_runout: True`, sitting at the
toolhead.

## Decision

**Add both SFS sensors and keep the EBB one.** Three sensors, three places filament can
fail to move:

| Sensor | Pin | Length | Covers |
|---|---|---|---|
| `filament_switch_sensor sfs_switch` | `MP8:PF2` | — | spool empty, filament pulled out of the module |
| `filament_motion_sensor sfs_motion` | `MP8:PF1` | 2.88 mm | jam or slip between the spool and the toolhead |
| `filament_motion_sensor filament_sensor` | `EBB:FILAMENT_PROBE` | 10 mm | jam in the last stretch before the nozzle |

All three pause on runout, with `event_delay: 3.0` and `pause_delay: 0.5` so a single
momentary stall does not stop a print — the delay is carried over from the EBB sensor,
which was given it for exactly that reason.

**They are also armed exactly like the EBB sensor**, because leaving a sensor armed
outside a print turns any stray state change into a `PAUSE` with nothing to pause:
`SFS_DISABLE_AT_START` disarms both 1 s after boot, `PRINT_START` arms all three from
`variable_filament_sensor_enabled`, and `PRINT_END`, `_USER_PAUSE` and `_USER_CANCEL`
disarm them; `_USER_RESUME` re-arms from the same variable. That variable was `0` and is
now **`1`**: the sensors are worth nothing if the default is off, and the runout is the
reason this module was bought. Mainsail's toggle still turns them off in one click.

A runout therefore ends in `PAUSE`: the head parks (mainsail's `PAUSE_BASE` plus the
repo's `_USER_PAUSE`), heaters hold, and `variable_idle_timeout: 43200` keeps the machine
paused for 12 h instead of timing out.

Inputs use `^` (hardware pull-up) because the module's outputs may be open-collector; a
pull-up is harmless on a push-pull output and required on an open-collector one.

Rejected alternatives:

- **Replace the EBB sensor with the SFS pair.** Fewer sensors to explain, but the two are
  not redundant: the SFS sits at the spool and sees the whole bowden run; the EBB sensor
  sees the tens of millimetres before the nozzle, which is where a partial clog shows up
  first and where a spool sensor would notice too late.
- **Motion sensor only, no switch.** The encoder cannot tell "spool empty" from "jam";
  the switch can, and the message a pause shows should say which.
- **Copy BTT's example verbatim** (`pause_on_runout: False` plus a `PAUSE` inside
  `runout_gcode`). Same effect, one more place to get wrong, and the repo already uses
  `pause_on_runout: True` everywhere. The one thing taken from the vendor example is the
  `^` pull-up on both pins.
- **Use the vendor's own pins (`PF0`/`PC15`).** Equally valid; the installed wiring is
  already on `PF2`/`PF1` and there is no electrical difference between them.

## Consequences

- A real runout now pauses from whichever sensor notices first; the others will also
  fire, and a second `PAUSE` on an already-paused print is a no-op.
- `detection_length: 2.88` is the vendor's figure, not a measurement on this machine. If
  it produces false jams, raise it — the value is a modelling choice, not a hardware
  constant, and the ADR should be revised with the number that actually held.
- The switch's logic level is unverified: the first query after wiring decides whether it
  needs `!` on the pin. If `filament_detected` reads true with no filament in the module,
  invert the pin and record it here.
- Nothing in this change touches the relay, the fans or the heaters.

## Open items

1. Confirm the switch polarity (filament present / absent) with the module loaded and
   empty, and correct the pin if needed.
2. Confirm the encoder actually pulses on this build — the goal is a non-zero
   `filament_motion_sensor sfs_motion` state change while extruding 20 mm.
3. Measure whether 2.88 mm is too tight on this machine, and revise this record if the
   shipped value changes.

## Revisions

**2026-11-11 — the EBB sensor is gone; the SFS pair is the only filament detection.**
The decision above kept `[filament_motion_sensor filament_sensor]` on
`EBB:FILAMENT_PROBE` and rejected "replace it with the SFS pair". That is reversed:

- **It was never installed.** The toolhead's filament sensor needs a resistor that was
  never fitted, which is why its `ENABLE` default was `0` — the sensor was armed by
  nothing and caught nothing.
- **One sensor is enough, and the SFS is the right one.** It sits outside the chamber
  with room to open it and clear a jam mid-print, which the toolhead position does not
  give.

*What changed:*

- `[filament_motion_sensor filament_sensor]` and the `[gcode_macro PRINT_FILAMENT_STATE]`
  that printed its `last_query` are deleted with
  `config/printer/stealburner/filament_runout.cfg` (recoverable from git if the EBB
  sensor is ever fitted).
- `[delayed_gcode DISABLEFILAMENTSENSOR]` in `printer/macros.cfg` is deleted; its job is
  done by `SFS_DISABLE_AT_START` in `printer/filament/sfs_v2.cfg`.
- The `SET_FILAMENT_SENSOR SENSOR=filament_sensor` calls in `PRINT_START`, `PRINT_END`,
  `_USER_PAUSE`, `_USER_RESUME` and `_USER_CANCEL` are removed; the two SFS calls remain.
- `variable_runout_sensor` now names **`filament_switch_sensor sfs_switch`**. It is not
  decoration: `config/mainsail.cfg` reads `printer[client.runout_sensor].filament_detected`
  to refuse a `RESUME` when no filament is detected, so pointing it at a deleted object
  would have made every resume fail.

*What this costs:* a jam in the last stretch before the nozzle is now only visible through
the encoder at the spool. The SFS still catches it — it is the same "filament is not
moving" measurement, just taken further upstream — but it will notice later, after the
`detection_length` of slack plus the bowden path has been consumed.

### 2026-11-12 — first real jam caught

On 2026-11-12, 22 s into the first layer of `V350_base_ei468_flow101.gcode`, `sfs_motion`
paused the print with `SFS encoder: jam` on the display. The owner confirmed the filament
really was jammed. This was a true positive, not a false trip, so `detection_length: 2.88`
stays unchanged.
