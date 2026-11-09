# 0007 — Joystick jog drives the toolhead through Moonraker, not Klipper

**Status:** Accepted · 2026-07-24 · *hardware not yet fitted*

## Context

The goal is free-hand movement of the Stealthburner from a physical stick on the printer —
for nozzle inspection, bed levelling checks and general fiddling that today means clicking
Mainsail's jog buttons.

Klipper has no jog primitive. Three ways to build one were considered:

| Option | Where the logic lives | Verdict |
|---|---|---|
| `[gcode_button]` + `delayed_gcode` loop | Klipper config | Fixed-size steps only. A macro loop cannot vary speed with how the stick is held, and every iteration is a fresh move with its own accel/decel — visibly steppy |
| Klipper extra (Python module) | `klippy/extras/` | Real velocity control, but it forks Klipper. Every update becomes a merge, and a bug in it takes the printer down |
| External service → Moonraker | This repo's `extensions/` | Klipper stays stock. Worst case the service dies and the printer is unaffected |

The CB1 also constrains the hardware. Its 40-pin header exposes **no usable I2C** (the two
pins a Pi would use for it are marked NC), and `/dev/spidev*` is not guaranteed on a stock
image — so an analog stick cannot be assumed to have a bus to talk over.

Chosen hardware is a **Sanwa JLF-TP-8YT-SK**: an arcade stick with four microswitches and no
potentiometers. It cannot be proportional at all.

## Decision

A **separate .NET service** on the CB1 drives the toolhead over Moonraker's websocket.

*Motion.* A held direction becomes a stream of short **absolute** `G1` moves at the sample
rate (50 Hz → 2 mm segments at 100 mm/s). Collinear segments coast through Klipper's
lookahead, which is what makes it continuous rather than steppy. The target is integrated in
the service and read from Klipper only once per session, so rounding cannot accumulate.

Exactly `LookaheadSeconds` of motion is kept queued — 80 ms by default. Below that Klipper's
queue starves and stutters; above it the toolhead coasts further after release, because a
move handed to Klipper cannot be recalled. That single number *is* the smoothness/latency
trade-off and is the only thing that normally needs tuning.

*Input is abstracted.* `IJoystickInputSource` has three implementations — digital switches,
ADS1115 over I2C, MCP3008/3208 over SPI (hardware or bit-banged over four GPIO pins). The jog
loop is identical for all of them. Bit-banged SPI exists specifically because it needs no
device-tree overlay, so an analog stick stays possible on a stock CB1 image.

Because the JLF is on/off, the digital source supplies speed with a **ramp**: a tap creeps at
12 % of full speed, a held direction winds up to 100 % over 1.2 s.

*Safety.* Never during a print, gated on `print_stats.state`. Unhomed plus a stick input runs
`_CG28` and then waits for the stick to return to centre. Targets are clamped inside the
soft limits with a 1 mm margin, and jogged Z is floored at 0 even though Klipper allows −5.

## Consequences

- **Klipper stays stock.** No forked extras, no merge burden on update.
- The service is one more thing to deploy. It ships as a self-contained linux-arm64 build, so
  the CB1 needs no .NET runtime, and a systemd unit restarts it on failure.
- Jogging costs ~50 websocket round trips per second while the stick is held. That is
  negligible next to Moonraker's normal traffic, and the loop stops entirely at centre.
- **Nothing arbitrates GPIO between Klipper and this service.** Pins used by
  `[gcode_button]` in `cb1.cfg` (PI1, PI9, PI13) are off limits, and there is no error if
  that rule is broken — only misbehaviour. Hence the `--probe` mode, which shows live pin
  states without moving anything.
- Release latency is bounded by the queue, not by the stick: ~8 mm of coast at full speed,
  under 1 mm at precision speed. It cannot be eliminated without starving the lookahead.
- `idle_timeout.state` is unusable as a print guard here — it reads `Printing` for the jog
  moves the service issues itself. Same trap as the standby macros in commit b8438d8.
