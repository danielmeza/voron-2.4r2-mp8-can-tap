# 0022 — Hold the PSU relay closed from boot; POWER_OFF cuts mains

**Status:** Accepted · 2026-11-10 · config in the repo, module not yet wired

## Context

A BIGTREETECH **Relay V1.2** "auto power off" module is being added so `POWER_OFF` can
remove AC power instead of only stopping heaters and steppers. The module is a
self-contained mains switch:

- **AC side:** its own AC-DC supply, 85–265 VAC in, 5 V / 3 W out. Mains passes through
  the relay contact.
- **`5V IN` + `GND`:** the module's own low-voltage supply and its short-circuit /
  power-loss sensing input. It must see 5 V for as long as the printer is meant to stay
  on; losing it makes the module drop the relay after a few seconds. This was observed
  on this machine: the printer powered itself off a few seconds after boot.
- **Control input:** `P3.2` is the pin that judges the shutdown signal and `P5.5` drives
  the relay. BTT's Marlin example binds `PS_ON_PIN` and asserts it for the whole run,
  then runs `M81` in the end gcode — assert to stay powered, deassert to cut AC.

The Manta M8P v2.0 has a dedicated 2-pin **PS-ON** header carrying `GND` and `PD14`, and
the BTT Manta M8P V2.0 user manual ("Auto Power Off (Relay V1.2) Wiring", printed page
14) wires the relay module to exactly those two pins. This repo's board map already
carries the alias — `config/printer/mcu/btt_manta_mp8_v2.0.cfg` → `aliases_extra:
PS_ON=PD14` — so `MP8:PD14` and `MP8:PS_ON` are the same pin.

### The FAN4 trap, and the numbers

The first attempt fed the module from **FAN4** and the printer powered off a few seconds
after boot. The measurement that explains it: a voltmeter between `VF4` and `GND` reads
5 V with the fan output "off", which looks like a usable 5 V supply and is not one.

On this board every fan header's **positive** pin (`VF0`…`VF6`) is the permanently live
rail selected by that header's voltage jumper, while the **return** is the drain of a
low-side N-MOSFET gated by the fan pin (`FAN4` → `FAN4_MOSFET`, gate `PA4`). With the
output off the MOSFET is open, so no current flows: the meter reads the rail's potential
through the module's own input, but the module is not being supplied. As soon as the
MOSFET state depends on Klipper — or Klipper stops driving it, at shutdown or restart —
the supply disappears and the module drops the relay. FAN4 is a switched load output,
not a 5 V source.

Always-on 5 V + `GND` pairs that do exist on the board, live as soon as the board is
powered and needing no config:

| Header | Pins |
|---|---|
| Probe | `GND` `PD12` `+5V` `GND` |
| I2C | `SDA` `SCL` `GND` `+5V` |
| SPI | `3.3V` `GND` `+5V` |
| 5V-DET | `PB10` `GND` `+5V` |
| TFT | `+5V` (GND elsewhere) |
| RGB | `+5V` `PD15` `GND` |
| Motor connector | `+5V` `GND` `PE9` |

## Decision

**Two connections, kept separate by job:**

1. **Signal:** `[output_pin PS_ON]` on `MP8:PD14`, in
   `config/printer/electronic_bay/psu.cfg` — `pwm: false`, `value: 1`,
   `shutdown_value: 1`. The pin is asserted the moment Klipper loads, which is what
   keeps the relay closed, and `POWER_OFF` deasserts it as its last action
   (`SET_PIN PIN=PS_ON VALUE=0`).
2. **Power:** the module's `5V IN` / `GND` come from an always-on header — **Probe or
   I2C** — never from a fan header.

`shutdown_value: 1` is deliberate. A Klipper error (MCU shutdown, config error, host
exception) must **not** cut mains: the relay dropping would also kill the CB1 running
Klipper and Moonraker, so a recoverable software fault would become a power cut with the
log and the recovery path gone. Mains is released by an explicit `POWER_OFF`, and by the
module's own power-loss detection if the 5 V really disappears.

## Rejected alternatives

- **Feed the module from FAN4 with an `[output_pin]` on `PA4`.** `PA4` is PWM-capable
  and would let the firmware "hold" the supply, but the header is a switched **return**
  behind a low-side MOSFET on a jumper-selected 24/12/5 V rail: any config change that
  redefines `FAN4`, any Klipper restart and any MCU shutdown drops it, and the module
  then drops mains a few seconds later. A power supply must not depend on the thing it
  powers. This is the alternative that was actually built and measured, and it failed.
- **Use the `5V-DET` header as the 5 V source.** `V5_DET` (`PB10`) exists to *detect*
  the loss of 5 V, not to source current for an external module; drawing the relay's
  3 W budget through a detection tap is the wrong job for that net.
- **Leave the module's 5 V unconnected.** The module would have no supply and no
  power-loss sensing, so it would either never close the relay or drop it at random.
- **Drive the relay from a spare heater or fan output instead of PS-ON.** Same class of
  fault as FAN4 (switched output, shutdown behaviour outside our control) plus it
  consumes a heater output and misleads the next reader about what that connector does.

## Consequences

- **`POWER_OFF` now removes AC power.** It must not be reachable while the print head is
  in motion or while the bed is hot for a reason that needs cooling first — the machine
  cannot be cooled after mains is gone. Treat it as the last step of a shutdown, not as
  a "stop the printer" button.
- **Polarity of `P3.2` is NOT verified on this machine.** Before connecting the module's
  control cable, measure the PS-ON header with that cable unplugged: `PD14` must read
  ~3.3 V with Klipper running and ~0 V after `POWER_OFF`. If it is inverted for this
  module, change the pin to `!PD14` and record the measurement in **Revisions** below.
  Until that measurement exists, the config and the hardware are not known to agree.
- `M84` and `TURN_OFF_HEATERS` run before the pin drops, and the module's own shutdown
  delay gives the fans a moment after that. **Do not rely on it for cooling**: the
  exhaust and Nevermore behaviour stays governed by the existing macros
  ([0014](0014-nonblocking-print-end-lights.md), [0020](0020-cool-the-toolhead-board.md)),
  and a `POWER_OFF` issued immediately after a print may still cut mains with the
  chamber hot.
- The 5 V tap and the PS-ON cable are two separate harnesses to the same module; losing
  the 5 V side alone still drops mains after the module's delay, which is the intended
  fail-safe.
- Deploy: this is a config-only change. It is safe to sync before the module is fitted
  (an unconnected `PD14` drives nothing), but the polarity measurement above is the gate
  for connecting the control cable.

## Revisions

**2026-11-10 — the 5 V source moves back to FAN4.** The module's `5V IN` is now fed from
the FAN4 header (`VF4` jumper on 5 V), with its low-side return held by
`[output_pin psu_relay_5v]` on `MP8:FAN4` (`pwm: false`, `value: 1`, `shutdown_value: 1`)
in the same `psu.cfg`. This reverses Decision item 2 ("never from a fan header") and
adopts rejected alternative 1. Both are left above, unedited, because the reasoning still
explains the failure that led here.

*What forced it:* the first attempt did not merely fail to drive `PA4` — nothing claimed
`PA4`. FAN4 had no section at all, so `FAN4_MOSFET` was never switched on, the module's
`5V IN` never saw current, and the module dropped the relay a few seconds after boot. That
is a **config gap**, not evidence that the header cannot supply the module, and the
multimeter reading at `VF4` (5 V with the output off) is the same trap described in the
FAN4 trap section above. With the pin asserted by config, the rail is present from the
moment Klipper loads, and `shutdown_value: 1` keeps it present through an MCU shutdown.

*What is still not proven:*

- **The restart window.** For a `RESTART` or `FIRMWARE_RESTART`, the MCU re-initialises its
  outputs and the pin is only re-asserted when the config loads. The module does not drop
  the relay instantly — that delay is the same one the Consequences above count on after
  `POWER_OFF` — so a sub-second gap is *expected* to be harmless, but it has **not** been
  measured. A shutdown that outlasts the module's delay drops mains with Klipper's own log
  on the machine that just lost power. Measure it before relying on remote restarts.
- **A config error that removes or breaks this section** now cuts mains instead of leaving
  the printer running. That is strictly worse than the always-on-header arrangement, and it
  is the accepted cost of using the header the module was designed to attach to.
- **FAN4 is consumed.** `MP8:FAN4` was otherwise unused (no section referenced it before
  this change), so no fan lost its output — but that header can no longer be used for one.
  FAN5 is now the SUNON's ([0023](0023-fan5-four-wire-sunon.md)); FAN6 is the last free one.

**2026-11-11 — "every fan header" above means FAN0–FAN4, not FAN5/FAN6.** The body says
"on this board **every** fan header's … return is the drain of a low-side N-MOSFET". That
holds for FAN0–FAN4 and is wrong for the last two: FAN5 and FAN6 are **4-pin headers**
(PWM out, tach in, rail, ground) with no transistor in their sub-circuit, which is why
their fans are driven by a logic PWM into the fan, not by switching a supply return
([0023](0023-fan5-four-wire-sunon.md) records the pinout labelling and the schematic
evidence).

*What forced it:* the board pinout labels FAN0–FAN4 with two signals each (`VFx` and one
MCU pin) but FAN5 `PA6 | PC2 | VF5 | GND` and FAN6 `PA2 | PC1 | VF6 | GND`; schematic
page 7 shows P18/P20 with a 10 Ω series resistor from their buffered PWM net and a 10 kΩ
pull-up plus optocoupler on their tach net, and no MOSFET.

*What this does not change:* the FAN4 trap, the measurement behind it, and the decision to
hold `FAN4` with `[output_pin psu_relay_5v]`. `MP8:FAN4` is one of the five switched-return
headers, so the failure it records is real. It also does not make a fan header a 5 V
source: on FAN5/FAN6 the rail is live and the *PWM input* is what is switched, which is
still not a supply for the relay module. FAN6 is no longer free — [0023](0023-fan5-four-wire-sunon.md)
claims it for the second 4-wire fan.

**2026-11-11 — the pin does not stay high on its own; a re-assert was added.** Measured on
the machine: after `RESTART`, `output_pin PS_ON` reads **1.0** while Klipper is in
`startup` and **0.0 the moment it reaches `ready`**, reproduced with nothing in the
Moonraker gcode store during the window. A `SET_PIN PIN=PS_ON VALUE=1` issued after
`ready` then holds indefinitely (checked at +5 s, +20 s and +45 s), so the config gained
`[delayed_gcode PSU_RELAY_ASSERT]` (`initial_duration: 2.0`) which re-asserts the pin.

*What this does not establish:* the cause. Klipper's `output_pin` initialises
`last_value` from `value:`, and the parsed section in `klippy.log` shows `value = 1`, so
something writes 0 exactly at the ready transition. KlipperScreen was the suspect and is
not proven: it was frozen with `SIGSTOP` for one attempt and that run was inconclusive.
Until the cause is named, treat `value:` as insufficient — the `delayed_gcode` is what
holds the pin.

*Also measured in the same session:* `POWER_OFF` takes the pin 1.0 → 0.0 as intended, and
`[output_pin psu_relay_5v]` (FAN4) holds 1.0 from boot with no help.

**2026-11-11 — FAN4 did not hold the module in practice; the 5V moves to the 5V-DET
header.** With `[output_pin psu_relay_5v]` reporting 1.0 and `PS_ON` at 1.0, the module
still dropped the relay a few seconds after boot. The owner then moved the module's `5V IN`
to the board's **`5V-DET` header** (`PB10` / `GND` / `+5V`, permanently live as soon as the
board is powered), and `[output_pin psu_relay_5v]` was deleted, releasing FAN4.

*What this refutes:* that the FAN4 route was fixed once the MOSFET was held high. Holding
the pin high was necessary and is not sufficient — whatever the header does with a load on
it instead of a fan, the module did not stay alive on it. The measurement is the module's
own behaviour, not the pin's reported value: `output_pin` reported 1.0 the whole time.

*Also recorded here because it interacts with this decision:* the klippy log shows two
`gcode_button` callbacks firing during startup and failing with `Printer is not ready`
(`extras/gcode_button.py:46` → `gcode.CommandError`). Their macros therefore did not run,
so they did not power anything off — but a button that fires at boot is one that can also
fire while the printer is `ready`, and `POWER_OFF` now removes AC. If a spurious power-off
is ever seen, suspect that button wiring first.

*Left as is, deliberately:* `PS_ON` keeps `value: 1` **and** the `[delayed_gcode
PSU_RELAY_ASSERT]` re-assert. With the delayed_gcode in place, `PS_ON` read 1.0 at `ready`
and 4 s later on every restart of this session, with KlipperScreen running; with
KlipperScreen frozen it also held. The zeroing seen before the delayed_gcode was added did
not reproduce afterwards, so the re-assert stays until the cause is named.
