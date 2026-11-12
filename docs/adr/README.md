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
| [0012](0012-slicer-integration-for-tuning.md) | Slicer integration for the tuning phase | Accepted; retraction part superseded by [0021](0021-slicer-retraction-by-default.md) |
| [0013](0013-probe-samples-reduced.md) | Probe samples: 3 → 2 | Accepted |
| [0014](0014-nonblocking-print-end-lights.md) | Post-print light show is non-blocking | Accepted |
| [0015](0015-purge-into-bucket-not-bed.md) | Purge into the trash bin, not onto the print bed | Accepted |
| [0016](0016-squish-test-grid-not-live-babystep.md) | First-layer squish: a static test grid, not live babystepping | Accepted |
| [0017](0017-refuse-slicer-preheat.md) | PRINT_START refuses a file whose slicer already heated the hotend | Accepted |
| [0018](0018-reset-velocity-limits-after-print.md) | Reset motion limits when a print ends or is cancelled | Accepted |
| [0019](0019-cold-start-qgl-waits-for-probe-temp.md) | Cold start: wait for probe temperature before the first QGL | Accepted |
| [0020](0020-cool-the-toolhead-board.md) | Cool the toolhead board instead of lowering the chamber | Accepted |
| [0021](0021-slicer-retraction-by-default.md) | Retraction belongs to the slicer by default; firmware retraction stays for live tuning | Accepted |
| [0022](0022-power-off-relay-on-ps-on.md) | Hold the PSU relay closed from boot; POWER_OFF cuts mains | Accepted; 5 V source revised 2026-11-10, FAN-topology scope corrected 2026-11-11, 5 V feed found unnecessary and PD14 set at MCU startup 2026-11-11, cold start verified and unwired buttons disabled 2026-11-12 — see Revisions |
| [0023](0023-fan5-four-wire-sunon.md) | FAN5 and FAN6 drive 4-wire SUNON fans with hardware PWM | Accepted; header topology corrected 2026-11-11 — see Revisions |
| [0024](0024-sfs-v2-runout-and-jam.md) | The SFS V2.0 drives a runout switch and a jam encoder, alongside the EBB sensor | Accepted 2026-11-11 |

## Why these exist

Nearly every problem found in this config came from the same root cause: Klipper
merges duplicate sections silently with **last-loaded-wins**, so settings ended up
*accidental* rather than *chosen*. These records exist so the next change knows
which values were deliberate and why.
