# Architecture Decision Records

Decisions taken while cleaning up and tuning this printer, with the reasoning and
the trade-offs that were accepted.

Format: Context → Decision → Consequences, after
[Michael Nygard's template](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

## Process (see [`CLAUDE.md`](../../CLAUDE.md) Rule 1)

**Every decision gets a record, and every drift updates it.**

| Situation | What to do |
|---|---|
| New decision with a trade-off or a rejected alternative | New ADR, written **in the same change** as the code |
| Decision **refined or retuned**, same intent | Add a dated entry to that ADR's **Revisions** section |
| Decision **reversed** | New ADR that supersedes it; mark the old one `Superseded by NNNN` and **leave its body intact** |

A Revisions entry must say *what changed* **and** *what evidence forced the change* —
a measurement, a log line, a failure. "Tuned the value" is not a revision entry;
"the exhaust fan held the chamber at 38 °C and the gate never cleared" is.

Never delete the reasoning that turned out to be wrong. It is the most useful part of
the record, and this repo has already re-learned the same lesson twice without it.

| # | Title | Status |
|---|---|---|
| [0001](0001-single-owner-config-sections.md) | One file owns each config section | Accepted |
| [0002](0002-mainsail-owns-client-macros.md) | mainsail.cfg owns PAUSE/RESUME/CANCEL_PRINT | Accepted |
| [0003](0003-temperature-gated-soak.md) | Gate the heat soak on measured temperature, not a timer | Accepted |
| [0004](0004-frame-temperature-sensing.md) | Frame sensing via bolted NTCs, not I2C | Accepted |
| [0005](0005-between-session-keep-warm.md) | Hold the bed warm between sessions | Accepted |
| [0006](0006-adaptive-bed-mesh.md) | Adaptive mesh per print instead of a stale saved mesh | Accepted |
| [0007](0007-joystick-jog-via-moonraker.md) | Joystick jog drives the toolhead through Moonraker, not Klipper | Accepted |
| [0008](0008-mcu-firmware-update-flow.md) | MCU firmware updates run outside Moonraker | Accepted |
| [0009](0009-nozzle-clean-pressure-relief.md) | Relieve nozzle pressure before wiping | Accepted |
| [0010](0010-belt-tension-method.md) | BTT Belter is the belt-tension method of record | Accepted |
| [0011](0011-standby-signalling-and-chamber-on-screen.md) | Signal standby on the LEDs; chamber temp on the print screen | Accepted |
| [0012](0012-slicer-integration-for-tuning.md) | Slicer integration for the tuning phase | Accepted |

## Why these exist

Nearly every problem found in this config came from the same root cause: Klipper
merges duplicate sections silently with **last-loaded-wins**, so settings ended up
*accidental* rather than *chosen*. These records exist so the next change knows
which values were deliberate and why.
