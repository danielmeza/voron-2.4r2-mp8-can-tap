# 0023 — FAN5 drives a 4-wire SUNON on its own section, with the tach wired

**Status:** Accepted · 2026-11-10 · config in the repo, two bench checks still open

## Context

A **SUNON MF60101V1-1Q03C-S99** was fitted on FAN5 and has no config section yet. Its
datasheet numbers:

| Spec | Value |
|---|---|
| Frame | 60 × 60 mm |
| Rated voltage | 12 V |
| Power | 1.46 W |
| Wires | 4 — supply, GND, **PWM** speed control, **FG** tachometer |
| Speed range | 800–4000 rpm |

It is not interchangeable with the three fans already in
`[multi_pin electronic_bay_fans]` (`MP8:FAN0`, `MP8:FAN1`, `MP8:HE0`), for two reasons:

1. **A `multi_pin` group drives every member with one speed.** That is exactly right for
   the three electronic-bay fans, which are different fans doing one job (ADR-0001's
   split table keeps the group in `fans.cfg`). A fan whose speed has to be set on its own
   cannot share it.
2. **A `multi_pin` member has no place for a tachometer.** `tachometer_pin` belongs to the
   fan section that owns the pin, and `[multi_pin]` has no such key. Wiring the fan into
   the group would discard the FG signal, which is the only feedback that proves the fan
   is actually turning.

The fan is currently wired with its **PWM wire (green) on PA6** and its **tach wire
(yellow) on PC2**.

### The FAN5 pin map, from the schematic

Manta M8P v2.0 schematic, sheet 7 (`FAN5`):

| Pin | Net / part | MCU |
|---|---|---|
| 1 | R132 10 Ω → `FAN5_MOSFET` gate | `PA6`, alias `FAN5_P1` |
| 2 | `FAN5-TACH`, 10 kΩ pull-up to `VF5` | `PC2`, alias `FAN5_P2` |
| 3 | F9 1 A fuse → `VF5` rail (jumper-selected 24/12/5 V) | — |
| 4 | GND (with D44, C64) | — |

Two consequences follow directly from that table, and both matter here:

- **Pin 1 is the gate of the low-side N-MOSFET**, not a dedicated 4-wire PWM input. The
  header's positive pin is the jumper-selected rail, permanently live; the *return* is
  what the MOSFET switches. This is the same topology as every other fan header on this
  board, and it is why every fan section here either sets `cycle_time: 0.01` or leaves
  Klipper's own 0.010 default — 100 Hz software PWM, never `hardware_pwm`.
- **Pin 2 is a tachometer input**, not a second power pin. The 10 kΩ pull-up to `VF5`
  confirms it: it is an open-collector/open-drain FG output being sensed, not a supply.

`config/printer/mcu/btt_manta_mp8_v2.0.cfg` already carries both aliases
(`FAN5_P1=PA6`, `FAN5_P2=PC2`), and no other section in `config/printer/` claims either
pin or `FAN4`/`PA4`.

## Decision

**Give the SUNON its own `[fan_generic]` section in
`config/printer/electronic_bay/fans.cfg`, driven the way this board drives fans.**

```ini
[fan_generic Sunon]
pin: MP8:FAN5_P1
cycle_time: 0.01
kick_start_time: 0.5
off_below: 0.1
shutdown_speed: 0.0
tachometer_pin: MP8:FAN5_P2
tachometer_ppr: 2
```

- **`pin: MP8:FAN5_P1` is the low-side drive**, so `S` values are duty cycle of the fan's
  *supply return* — the same control path as the bay fans, at 100 Hz.
- **`tachometer_ppr: 2`** takes the SUNON's two FG pulses per revolution as the
  revolution count, which is what `tachometer_ppr` measures (pulses per *revolution*,
  not per electrical cycle). The reported `rpm` is therefore trustworthy only as a
  presence/speed indicator, not a calibrated number.
- **`shutdown_speed: 0.0`** means an MCU shutdown leaves this fan off instead of latching
  the last duty. That follows the bay and chamber fans (`[controller_fan electronic_bay]`,
  `[temperature_fan chamber]`, both `shutdown_speed: 0.0`). The deliberate exception on
  this machine is the hotend's `[heater_fan hotend_fan]` at `shutdown_speed: 1`: the
  heatsink fan must keep running when the hotend may still be hot, and that is a different
  job from this one.

### Open item — the panel connector is not a 4-wire PWM input (must be measured)

Pin 1 is a **switched low-side drive**. A 4-wire fan's dedicated PWM input was designed
to be fed by a logic-level, high-impedance PWM source referenced to the fan's own GND;
this header chops the fan's supply return instead. Those are not the same thing, and
which one the fan tolerates is a property of the fan, not of the schematic. Two bench
tests decide it. **Record both results in Revisions below; until they exist, the config
and the hardware are not known to agree.**

1. **Is the fan's control electronics independently powered?** With only `+12 V` and
   `GND` connected — PWM and tach wires off — power the fan.
   - *Spins at full speed* → the fan's electronics run off the supply rail, its PWM input
     is a real logic input, and pin 1's low-side switching would have to be replaced by a
     genuine PWM source for speed control (a spare MCU pin with `hardware_pwm`, or an
     external driver). The section below would then be controlling the fan's supply and
     not its speed.
   - *Does not spin* → the fan's electronics are fed through the PWM input, the header's
     switched return **is** the control path, and this section is correct as written.

2. **Does `SET_FAN_SPEED` actually change the speed?** With the fan fully wired, run
   `SET_FAN_SPEED FAN=Sunon SPEED=0.5` and read `rpm` from
   `printer["fan_generic Sunon"]` (Mainsail's fan panel, or a `QUERY_FAN_SPEED`-style
   macro). Repeat at `SPEED=1.0`.
   - *rpm changes with the command* → the duty cycle is reaching the fan's control, and
     the wiring assumption holds.
   - *rpm is reported but ignores the command* → the PWM wire is loading the gate net.
     On this header pin 1 is the MOSFET's gate behind R132, so whatever the fan's PWM
     input pulls that node to also drives the gate: the MCU's duty cycle and the fan's own
     pull-up fight over one net, and the fan can settle at a speed the firmware never
     asked for. **Disconnect the PWM wire and keep the tach**, then decide between running
     the fan at full speed or moving the PWM wire to a real PWM source with the bench
     evidence in hand. Do not leave a wire connected to a node the schematic says is a
     gate.

## Rejected alternatives

- **`hardware_pwm: true` with `cycle_time: 0.00004`.** That is the correct setting only
  for a genuine 4-wire PWM input carrying a logic signal. Applied to this header it chops
  the fan's **supply** at 25 kHz: peak currents through the 10 Ω gate resistor and the
  header's FET, extra EMI on a wire run that leaves the board, and supply ripple, all for
  a control path that is not the one the connector provides. The 10 ms `cycle_time` of
  every other fan section here is the evidence for what the hardware expects.
- **Put the fan on the idle part of `multi_pin electronic_bay_fans`.** The group already
  drives three fans with one duty cycle; adding the SUNON would tie a 12 V 4000 rpm fan
  to the bay fans' `fan_speed: 0.6` (and to their `heater: heater_bed` trigger), and would
  throw away the tachometer, because `[multi_pin]` has no `tachometer_pin`. Two
  independent jobs, one control knob — the coupling ADR-0001 exists to prevent.
- **Leave the fan unconfigured.** The fan is on a live 12 V rail with its return switched;
  unconfigured, the MOSFET stays off and the fan never runs. Worse, the next reader sees a
  populated connector, no section, and cannot tell whether that is a choice.
- **Wire the tach to a spare endstop or thermistor input.** Those inputs have different
  pull-ups and filters, and the fan's FG net is already on PC2 with a 10 kΩ pull-up to
  `VF5` designed for it. Using an endstop input would put a fan's signal on a net the
  safety logic scans.

## Consequences

- **`off_below: 0.1`** keeps a 10 % request from stalling the fan: below that duty the
  output is switched off entirely rather than left at a duty that may not start the rotor.
  `kick_start_time: 0.5` gives a cold start the half second it needs to break away.
- **`shutdown_speed: 0.0`** means an MCU shutdown stops the fan instead of freezing it at
  the last duty cycle. A fan that must keep running through a shutdown needs a
  `heater_fan`-style section, like the hotend's — not this one.
- **The tach only reports while Klipper polls it.** `rpm` is a value Klipper samples, not a
  hardware latch: with Klipper not running (or the MCU in shutdown) the status has no
  meaning, and a stopped fan shows `0.0` only once a poll has seen no pulses.
- **The fan is not part of any cooling strategy yet.** It is a `fan_generic` with no macro
  driving it: nothing starts it at print start, and nothing stops it at print end. Slicer
  gcode or a `PRINT_START`/`PRINT_END` edit would have to be a separate decision with its
  own record — this one only makes the fan controllable and observable.
- **Fan headers are now nearly all spoken for.** Counting the sections that claim one:
  FAN0, FAN1 and HE0 in `[multi_pin electronic_bay_fans]`, FAN2 for the Nevermore, FAN3
  for the chamber exhaust, FAN4 held by `psu_relay_5v` from boot (see
  [0022](0022-power-off-relay-on-ps-on.md)) and FAN5 for this fan. **FAN6 (`PA2`, tach
  `PC1`) is the only one left**, so the next fan needs a record that says what gave way.

## Revisions

None yet. The two bench checks in **Open item** above are unreported: no measurement of
the fan's independent power-up, and no `SET_FAN_SPEED`/`rpm` reading. Add one dated entry
here per result, with the number — "spins at full speed with PWM and tach disconnected" or
"`SPEED=1.0` gave 3180 rpm, `SPEED=0.5` gave 1740 rpm" — because that evidence is what
turns this section from plausible into correct.
