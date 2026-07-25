# Repo rules

Klipper/Moonraker configuration for a Voron 2.4r2 350 (TAP, CAN toolhead).
Read this before changing anything.

---

## Rule 1 — Every decision gets an ADR. Every drift updates it.

**This is the primary rule of this repo.**

Records live in [`docs/adr/`](docs/adr/). See [`docs/adr/README.md`](docs/adr/README.md)
for the format and the index.

- **Taking a decision** — anything with a trade-off, a rejected alternative, or a
  "why is it like this?" answer — means writing an ADR **in the same change**.
  If a future reader could reasonably ask *"why not the obvious thing?"*, it needs a record.
- **A decision that drifts** — refined, retuned, or partly walked back — means going
  back and **updating the existing ADR**, not silently diverging from it. Add a dated
  entry to its **Revisions** section saying what changed and, crucially, *what evidence
  forced the change*.
- **A decision that is reversed** gets a **new** ADR that supersedes the old one. Mark
  the old one `Superseded by NNNN` and leave its body intact — the reasoning that turned
  out wrong is the most useful part.
- Config comments should point at the ADR (`see docs/adr/0003-...`) rather than
  re-explaining the reasoning inline.

Config and ADRs drifting apart is the failure mode this repo exists to prevent —
almost every bug found here was a setting nobody chose.

## Rule 2 — Verify against the machine, don't assume

Klipper merges duplicate config sections silently with **last-loaded-wins**, so what a
file *says* and what the printer *does* are different questions.

- After any config change: sync, `RESTART`, and **query the machine** to confirm the
  effective value (`/printer/objects/query?configfile`).
- Prefer measurement over inference. Several findings here — the exhaust fan fighting
  the soak, the uncancellable `TEMPERATURE_WAIT`, `STANDBY_WARM` never firing — were
  invisible in the config and only appeared under instrumentation.
- Quote real numbers in commits and ADRs. "Chamber climbs at 0.29 °C/min" beats "slow".

## Rule 3 — Run the linter

```bash
python3 scripts/klipper_config_lint.py     # exit 1 on ERROR
```

It replicates Klipper's actual load semantics (non-recursive `glob`, sorted includes,
`RawConfigParser(strict=False)`). It must be clean before committing config changes.

## Rule 4 — One file owns each config section

See [ADR-0001](docs/adr/0001-single-owner-config-sections.md). Do not spread one section
across files; the merge is silent and the winner is decided by filename sort order.

## Rule 5 — Physical-safety checks are not optional

- **TAP**: the EBB carries the *only* Z endstop (`probe:z_virtual_endstop`). After any
  EBB firmware change, `QUERY_PROBE` before a `G28 Z`, or the toolhead drives into the bed.
- **Never home or print without confirming the build plate is clear.** There is no
  working camera (crowsnest returns 502), so this cannot be checked remotely.
- **Never flash the Manta first** — it is the USB-CAN bridge and reflashing it drops the
  whole bus. Order: Linux MCU → EBB → Manta. See [ADR-0008](docs/adr/0008-mcu-firmware-update-flow.md).

---

## Layout

| Path | What |
|---|---|
| `config/` | mirrors `~/printer_data/config/` on the printer |
| `docs/adr/` | architecture decision records — **start here** |
| `docs/TUNING_PLAN.md` | phased plan + measured results |
| `docs/BELT_TENSION.md` | belt tension, both measurement methods |
| `firmware/` | per-board Klipper `.config` files (the machine keeps only the last one) |
| `scripts/` | config linter, MCU firmware updater |

## Machine facts worth knowing

- Klipper host and all three MCUs pinned to **v0.13.0-708-g7046bd00e**
- MCUs: CB1 Linux · Manta M8P v2.0 (STM32H723, **USB-CAN bridge**) · EBB SB2209 RP2040
- CAN bitrate **1000000** everywhere; Katapult on both CAN boards
- `[include mainsail.cfg]` is **last**, so mainsail wins every duplicate — extend it via
  `_CLIENT_VARIABLE`, never by editing it ([ADR-0002](docs/adr/0002-mainsail-owns-client-macros.md))
- Moonraker runs `preprocess_cancellation` on upload, which is what makes adaptive
  meshing work with Cura ([ADR-0006](docs/adr/0006-adaptive-bed-mesh.md))
