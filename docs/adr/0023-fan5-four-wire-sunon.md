# 0023 — FAN5 and FAN6 drive 4-wire SUNON fans with hardware PWM

**Status:** Accepted 2026-11-10, corrected 2026-11-11 (see Revisions) · config in the
repo, four open items below

## Context

Two **SUNON** fans are fitted on the two headers this board reserves for 4-wire fans:

| Header | Fan | Supply | Speed | Wires |
|---|---|---|---|---|
| FAN5 | **MF60101V1-1Q03C-S99**, 60 × 60 mm | 12 V, 1.46 W | 800–4000 rpm | 4 |
| FAN6 | turbine-type **MagLev**, model not recorded yet | unknown — confirm 12 V | unknown | 4 |

The datasheet lead colours are the reason this record exists. On the
MF60101V1-1Q03C-S99 they are **BLUE = PWM, YELLOW = FG (tachometer), RED = +,
BLACK = −**, and there is no green lead on that model — the fan on FAN6 is the one
whose leads are **green = PWM, yellow = FG**.

### FAN5 and FAN6 are 4-pin headers; FAN0–FAN4 are not

The board's own pinout labels the fan headers asymmetrically, and the asymmetry is the
whole finding:

| Header | Labels on the connector |
|---|---|
| FAN0–FAN4 | **two** — the `VFx` rail and one MCU pin (`PF7`, `PF9`, `PF6`, `PF8`, `PA4`) |
| FAN5 | **four** — `PA6` `PC2` `VF5` `GND` |
| FAN6 | **four** — `PA2` `PC1` `VF6` `GND` |

Four signals on a fan header is PWM out, tach in, rail and ground. Two signals is a
switched return: those five headers switch the fan's **negative** side through a
low-side N-MOSFET, which is the topology `[controller_fan electronic_bay]` and the other
fan sections here are written for.

### What is behind the FAN5/FAN6 pins, from schematic page 7

The FAN5 connector (P18) contains **no transistor**. Its sub-circuit:

| Pin | Path | MCU |
|---|---|---|
| 1 | R132 **10 Ω** in series → net `FAN5_MOSFET` | PWM from **PA6** |
| 2 | R129 **10 kΩ** pull-up to `VFAN5` → `FAN5-TACH`, C64 **0.1 µF** to GND → EL357N optocoupler → `FAN5-DET` | **PC2** |
| 3 | F9 **1 A** / 24 V fuse → `VFAN5` (jumper P17 selects 5 V / 12 V / VBB) | — |
| 4 | GND, with D44 (SS2FH10HM3/H) | — |

`FAN5_MOSFET` is a misleading net name, not a gate: on the same page it is the
**output of U20, a 74HCT365 hex buffer powered from 5 V**, whose input is `FAN5_PWM`
from MCU pin 42 (`PA6`), with a 10 kΩ pull-down on the buffered side. So the header
carries a **5 V logic PWM signal through 10 Ω**, which is exactly what a 4-wire fan's
PWM input expects. The `_MOSFET` nets on FAN0–FAN4 come off the same buffers and *then*
drive a discrete MOSFET gate — that is the difference between the two header families.

The FAN6 header (P20, silk "SysCooling") is the same shape with different reference
numbers — R136 10 Ω, R135 10 kΩ, F10 1 A, D46, optocoupler E2 — and its nets are named
`Cool_PWM`, `Cool-TACH`, `Cool-DET` and `VCool`, which is why the aliases in
`config/printer/mcu/btt_manta_mp8_v2.0.cfg` are `FAN6_P1`/`FAN6_P2`.

The tachometer is therefore **conditioned, not direct**: the fan's open-collector FG
output is pulled up to the 12 V rail by R129, filtered by C64, and read through an
EL357N optocoupler (LED fed from `VFAN5` through R125 1.2 kΩ; phototransistor with a
4.7 kΩ pull-up to 3.3 V and 47 Ω into the MCU). Computed from those values, not
measured: the filter is 10 kΩ × 0.1 µF ≈ **1 ms**, which is short against the 7.5 ms
period of 2 pulses per revolution at 4000 rpm, but the `rpm` value this yields is a
**presence/speed indicator, not a calibrated number**.

PA6 appears in the MCU's own pin-mux text as `TIM3_CH1` (also `TIM13_CH1`), and PA2 as
`TIM5_CH3` / `TIM2_CH3` / `TIM9_CH1` — the timer channels `hardware_pwm` needs.

### Why neither fan joins the bay-fan group

`[multi_pin electronic_bay_fans]` drives `MP8:FAN0`, `MP8:FAN1` and `MP8:HE0` with one
duty cycle. Two reasons the SUNONs cannot be members:

1. **A `multi_pin` group drives every member with one speed.** That is exactly right for
   the three electronic-bay fans, which are different fans doing one job (ADR-0001's
   split table keeps the group in `fans.cfg`). A fan whose speed has to be set on its
   own cannot share it.
2. **A `multi_pin` member has no place for a tachometer.** `tachometer_pin` belongs to
   the fan section that owns the pin, and `[multi_pin]` has no such key. Wiring a fan
   into the group would discard the FG signal, which is the only feedback that proves
   the fan is actually turning.

## Decision

**Two `[fan_generic]` sections in `config/printer/electronic_bay/fans.cfg`, each driving
its header's PWM input with hardware PWM at 25 kHz and reading its tach:**

```ini
[fan_generic Sunon]
pin: MP8:FAN5_P1
hardware_pwm: true
cycle_time: 0.00004
off_below: 0.1
shutdown_speed: 0.0
tachometer_pin: MP8:FAN5_P2
tachometer_ppr: 2

[fan_generic SunonTurbine]
pin: MP8:FAN6_P1
hardware_pwm: true
cycle_time: 0.00004
off_below: 0.1
shutdown_speed: 0.0
tachometer_pin: MP8:FAN6_P2
tachometer_ppr: 2
```

- **`hardware_pwm: true` with `cycle_time: 0.00004`** is 25 kHz, the frequency the
  4-wire PWM interface is specified for, and it is above the audible band. It is
  achievable only on the timer channel, not by bit-banging the pin.
- **`tachometer_ppr: 2`** takes the SUNON's two FG pulses per revolution as the
  revolution count, which is what `tachometer_ppr` measures (pulses per *revolution*,
  not per electrical cycle).
- **`off_below: 0.1`** writes 0 below a 10 % request rather than a duty that a 4-wire
  input may ignore.
- **`shutdown_speed: 0.0`** means an MCU shutdown drops the PWM input low instead of
  latching the last duty. That follows the bay and chamber fans
  (`[controller_fan electronic_bay]`, `[temperature_fan chamber]`, both
  `shutdown_speed: 0.0`). The deliberate exception on this machine is the hotend's
  `[heater_fan hotend_fan]` at `shutdown_speed: 1`.
- **No `kick_start_time`.** A 4-wire fan's electronics are powered from the rail
  continuously; there is no stalled rotor to break away from, so the kick is a 2-wire
  workaround with no job here.

## Open items

Each of these needs a measurement, not an argument. **Record the result in Revisions
below; until then the config and the hardware are not known to agree.**

1. **PWM polarity and presence.** With each fan wired and turning, read
   `printer["fan_generic Sunon"].rpm` (and `SunonTurbine`) at `SPEED=1.0`, `0.5` and
   `0.0`. `rpm` must fall as the speed falls. **If a lower speed gives a *higher* rpm,
   the drive is inverted** and the pin needs the `!` prefix (`MP8:!FAN5_P1`).
   `SPEED=0.0` is expected to leave the fan idling at its minimum (~800 rpm on the
   FAN5 fan) rather than stopping, because the header cannot switch the rail.
2. **Whether the timer claim holds in Klipper.** The pin-mux list says PA6 is
   `TIM3_CH1` and PA2 is `TIM2_CH3`/`TIM5_CH3`; that the STM32H723 build Klipper uses
   accepts `hardware_pwm` on both is asserted from the board document, **not** proven
   by a config load. The first `RESTART` that loads this file is that proof: a bad
   timer shows up as a config error, with the fan unreachable until it is fixed.
3. **Nothing drives these fans.** They are `fan_generic` sections with no consumer:
   nothing in `PRINT_START`/`PRINT_END` sets their speed, so each runs at whatever was
   last commanded — including across prints. Whether they should follow the bed heater,
   the chamber, or a slicer command is a separate decision with its own record.
4. **FAN6 is unconfirmed hardware.** Its model number is not recorded, so its rated
   voltage, its minimum speed and its `tachometer_ppr` are unknown; the `VF6` jumper on
   P19 selects 5 V / 12 V / VBB and its position has not been read off the board.
   Do not run `SunonTurbine` until both are confirmed.

## Rejected alternatives

- **Software PWM at 100 Hz (`cycle_time: 0.01`), the value the other fan sections use.**
  A 4-wire fan's PWM input is specified at 25 kHz; a 10 ms period is two orders of
  magnitude below that, is audible as motor whine, and asks the fan to integrate a duty
  its input filter was not designed for. The 100 Hz sections keep their value because
  they switch a **supply return** through a MOSFET, which is a different job.
- **`[temperature_fan]` / `[controller_fan]` automatic control.** Neither fan is tied
  to a heater or to a sensor: a `temperature_fan` would need a target temperature that
  no measurement justifies, and it *runs the fan above* its target — the exact inversion
  that made the exhaust fan fight the heat soak (ADR-0003) and that the chamber section
  had to be re-documented for.
- **Joining `multi_pin electronic_bay_fans`.** That group is `FAN0`/`FAN1`/`HE0`, three
  2-pin switched returns on one knob; the SUNONs are 4-wire fans on their own headers.
  Joining would lose both the tach and the independent speed, and give a 4000 rpm fan
  the bay fans' `fan_speed: 0.6` and `heater: heater_bed` trigger.

## Consequences

- **The header cannot stop these fans.** The rail is permanently live and only the PWM
  input is switched, so `SPEED=0` is the fan's minimum speed, not off, and `M107`-style
  "fan off" semantics do not apply. Stopping the fan means cutting the rail, which this
  header cannot do; the PSU relay ([0022](0022-power-off-relay-on-ps-on.md)) is the only
  thing that does.
- **Two 25 kHz hardware timers are now in use.** PA6 (`TIM3_CH1`) and PA2
  (`TIM2_CH3`/`TIM5_CH3`) can each serve one PWM output; a future fan or a laser-style
  PWM consumer on those pins has to displace a fan, not share it.
- **`rpm` is a conditioned signal.** FG arrives through an optocoupler after a 10 kΩ ×
  0.1 µF filter, so the number is good for "turning / not turning / roughly how fast",
  and the tach only reports while Klipper polls it: with Klipper not running (or the MCU
  in shutdown) the status has no meaning, and a stopped fan shows `0.0` only once a poll
  has seen no pulses.
- **`shutdown_speed: 0.0` is not "fan off" here** either: it drops the PWM input low,
  which the 4-wire spec reads as minimum speed. A fan that must stop on shutdown would
  have to switch the rail.
- **All fan outputs are now spoken for.** Counting the sections that claim one: FAN0,
  FAN1 and HE0 in `[multi_pin electronic_bay_fans]`, FAN2 for the Nevermore, FAN3 for
  the chamber exhaust, FAN4 held by `psu_relay_5v` from boot (see
  [0022](0022-power-off-relay-on-ps-on.md)), FAN5 for the SUNON and FAN6 for the
  turbine. **The next fan needs a record that says what gave way.**

## Revisions

**2026-11-11 — the header topology in the 2026-11-10 body was wrong, and the decision
changed with it.** The first version of this record asserted that "pin 1 is the gate of
the low-side N-MOSFET … the same topology as every other fan header on this board", that
the fan's "PWM wire (green)" was on PA6, and that `hardware_pwm` was the wrong choice for
this header. All three were wrong, and they came from reading the net name `FAN5_MOSFET`
as a transistor gate instead of checking the connector's own sub-circuit.

*What forced it:*

- **The pinout labels the headers asymmetrically.** FAN0–FAN4 carry two labels each
  (`VFx` plus one MCU pin); FAN5 is `PA6 | PC2 | VF5 | GND` and FAN6 is
  `PA2 | PC1 | VF6 | GND` — four signals, i.e. PWM, tach, rail, ground.
- **Schematic page 7 has no transistor in the FAN5 block (P18).** Pin 1 goes through
  R132 10 Ω to the buffered `FAN5_MOSFET` net, which is an output of the 5 V 74HCT365
  (U20) fed by `FAN5_PWM` from PA6; pin 2 goes through R129 10 kΩ and an EL357N
  optocoupler to `FAN5-DET` on PC2; pin 3 through a 1 A fuse to the jumper-selected
  rail; pin 4 to GND. `FANx_MOSFET`-drives-a-gate is real on FAN0–FAN4 only.
- **The supplied datasheet lists BLUE = PWM, YELLOW = FG, RED = +, BLACK = −** on the
  MF60101V1-1Q03C-S99. There is no green lead on that model, so the old sentence about
  the green PWM wire described the *other* fan, the turbine on FAN6.

*What changed:* `cycle_time` 0.01 → 0.00004 with `hardware_pwm: true` (25 kHz on the
fan's PWM input); `kick_start_time: 0.5` dropped; PC2/PC1 documented as tach inputs
behind the optocoupler rather than direct FG; a second section added for the turbine on
FAN6. The `[fan_generic]`-per-fan decision, `off_below`, `shutdown_speed` and
`tachometer_ppr` are unchanged.

*What is still not proven:* every item under **Open items** above — no `SET_FAN_SPEED`/
`rpm` reading at three speeds, no config load exercising `hardware_pwm` on these two
pins, and no confirmed model or jumper position for the FAN6 fan.

**2026-11-11 — roles fixed, and the numbers this record left open.** The two fans are no
longer plain `[fan_generic]` sections; each is now attached to the thing it exists for:

- **FAN5 → `[controller_fan drivers]`**, the turbine over the stepper drivers:
  `stepper: stepper_x, stepper_y, stepper_z, stepper_z1, stepper_z2, stepper_z3`,
  `fan_speed: 1`, `idle_timeout: 120`. Measured: `FORCE_MOVE STEPPER=stepper_x
  DISTANCE=1 VELOCITY=10` takes it 0 → 1.0 and 0 → **2901 rpm**, still **3508 rpm**
  13 s later; it reads 0.0 when no stepper has moved since boot, which is the behaviour
  `controller_fan` is chosen for.
- **FAN6 → `[temperature_fan manta]`**, the auxiliary fan over the Manta and the CB1.

*What forced the swap:* the owner corrected which header feeds which purpose (FAN5 =
drivers, FAN6 = CPU/Manta). The first version of this change had them the other way round,
and the config now follows the corrected mapping; the fan models stay as wired (the
`MF60101V1-1Q03C-S99` on FAN5, the turbine on FAN6).

*Sensor.* `sensor_type: temperature_host` — the CB1's `[temperature_sensor CB1]` is a
`temperature_host`, `[mcu]` here is the Linux host and has no `ADC_TEMPERATURE`, and
`temperature_combined` in this Klipper build requires `maximum_deviation` and
`combination_method`. Two attempts failed and are recorded rather than quietly avoided:
`sensor_mcu: CB1` → `Unknown pin chip name 'CB1'` (`CB1` is a `[board_pins]` group, not an
mcu), and `sensor_type: temperature_combined` → `Option 'maximum_deviation' in section
'temperature_fan manta' must be specified`.

*Tachometer.* `tachometer_ppr` 4 on FAN5 is measured: with `ppr 2` it reported **7106 rpm at
100 %**, above the 4000 rpm datasheet maximum, while `ppr 4` puts the same run at **3553 rpm
at 100 %** and **818 rpm at 15 %**, against the datasheet's 800 rpm floor. The FAN6 fan
keeps `ppr 2` because its model is still unrecorded.

*PWM polarity.* Verified not inverted: measured with `ppr 2`, `SPEED` 0.0 / 0.15 / 0.5 /
0.85 gave 0 / 1637 / 4543 / 6402 rpm, so `SPEED=0` really is stopped and the duty responds
monotonically.

*Still open.* The FAN6 fan's model, its voltage and the `VF6` jumper position are
unconfirmed. `target_temp: 50` on the manta fan is a starting value — the host idled
between 45.6 °C and 59 °C across the tests, so the fan will cycle around the threshold.
And `shutdown_speed: 1.0` is only half a guarantee on a 4-wire fan: the Manta's PWM buffer
is what would have to drive it, so an MCU that is truly dead may leave the fan wherever its
own pull-up puts it.

**2026-11-11 (later) — the manta fan got a floor and a lower target: PID instead of
watermark.** The owner asked for a minimum of ~10 % (or ~1000 rpm) so the board is never
left unventilated, and a target of 45 °C instead of 50.

- **Why the controller had to change.** `min_speed` alone does not do it: Klipper's
  `set_tf_speed()` short-circuits an exact `0.0` (`if value <= 0.: value = 0.`) before the
  `min_speed` clamp, and `ControlBangBang` is precisely a 0-or-`max_speed` controller. So
  `control: watermark` with `min_speed: 0.1` still stops the fan. `ControlPID` builds its
  output as `max_speed - clamp(...)` and only floors it at `min_speed`, so it can hold a
  minimum.
- **Settings:** `target_temp: 45`, `control: pid`, `pid_Kp: 25` (≈ 0.1 speed per °C),
  `pid_Ki: 0.02`, `pid_Kd: 0`, `min_speed: 0.1`, `max_speed: 1.0`, and `off_below`
  removed — at `off_below: 0.1` a 0.1 command would have been zeroed, cancelling the floor.
- **Measured over 60 s:** 47.7 °C → speed 1.00 / 4129 rpm; 45.1 °C → 1.00 / 4105 rpm;
  44.7 °C → 0.96 / 4012; 43.2 °C → 0.83 / 3604; 42.8 °C → 0.74 / 3413. Monotonic, no
  visible oscillation, and the fan pulls the host down (47.7 → 42.8 °C) as it slows.

*Consequence worth knowing before trusting the floor:* the host idles **above** 45 °C, so
with this target the fan sits near 100 % most of the time and the 10 % floor is only
reached about 10 °C below target. If "quiet when idle, 10 % floor, ramp when hot" is what
is wanted, the target has to move above the idle temperature (~55–60 °C) rather than the
floor moving up.

*And the floor's actual speed:* 10 % is ~550 rpm at `tachometer_ppr: 4`, not 1000. Roughly
18–20 % would be needed for 1000 rpm — the duty, not the controller, decides that.
