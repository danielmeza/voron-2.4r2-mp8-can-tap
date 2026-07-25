# Architecture Decision Records

Decisions taken while cleaning up and tuning this printer, with the reasoning and
the trade-offs that were accepted. Each record is immutable once **Accepted** — if a
decision changes later, add a new ADR that supersedes it rather than editing history.

Format: Context → Decision → Consequences, after
[Michael Nygard's template](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

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

## Why these exist

Nearly every problem found in this config came from the same root cause: Klipper
merges duplicate sections silently with **last-loaded-wins**, so settings ended up
*accidental* rather than *chosen*. These records exist so the next change knows
which values were deliberate and why.
