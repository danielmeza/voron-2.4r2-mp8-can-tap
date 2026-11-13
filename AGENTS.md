# AGENTS.md — Voron 2.4r2 Klipper config

Read `CLAUDE.md` before making any change. It contains the five repo rules (ADR
discipline, verify-vs-machine, lint gate, single-owner sections, safety checks).

## Config lint gate (Rule 3)

```bash
python3 scripts/klipper_config_lint.py     # exit 1 on ERROR
```

Replicates Klipper's real load semantics: non-recursive `glob`, sorted includes,
`RawConfigParser(strict=False)` — duplicates merge silently with last-loaded-wins.
Must be clean before committing any config change.

## Klipper merge trap

Duplicate config sections merge silently; **last file loaded wins** (sorted include
order). `[include mainsail.cfg]` is the last include in `config/klippy.conf`, so
mainsail wins every conflict. Never spread one section across files — see
[ADR-0001](docs/adr/0001-single-owner-config-sections.md).

Extend mainsail via `_CLIENT_VARIABLE` macros, never by editing `mainsail.cfg`
(Moonraker's update manager manages it). See [ADR-0002](docs/adr/0002-mainsail-owns-client-macros.md).

## ADR discipline (Rule 1)

Every decision with a trade-off or rejected alternative gets an ADR in `docs/adr/`
in the same change. A refined decision updates the existing ADR's Revisions section
with *what changed* and *what evidence forced the change*. A reversed decision gets
a new ADR that supersedes the old one (leave old body intact). See `docs/adr/README.md`.

## Machine facts

| Item | Value |
|---|---|
| Klipper | `v0.13.0-827-g29d4ec2d6` (all 3 MCUs match, since 2026-11-13) |
| MCUs | CB1 Linux · Manta M8P v2.0 (STM32H723, USB-CAN bridge) · EBB SB2209 RP2040 |
| CAN | 1000000 bitrate everywhere; Katapult on both CAN boards |
| Z endstop | `probe:z_virtual_endstop` (TAP on EBB — the **only** Z endstop) |

## Firmware update order

**Linux MCU → EBB → Manta.** The Manta is the USB-CAN bridge and reflashing it drops
the whole bus. See [ADR-0008](docs/adr/0008-mcu-firmware-update-flow.md) and
`firmware/README.md` for manual steps.

Pre-built `.config` files for all three boards are in `firmware/`. Automated flash:
```bash
pwsh ./scripts/Update-VoronMcuFirmware.ps1
```

## Physical safety

- After any EBB firmware change: `QUERY_PROBE` before `G28 Z` — or the toolhead
  drives into the bed (TAP is the only Z endstop).
- Never home or print without confirming the build plate is clear (crowsnest
  returns 502, no working remote camera).
- Flash order (above) is a safety requirement, not just convenience.

## Reaching the printer and syncing config

- Host: `voron.local` or the static IP `192.168.68.63` (since 2026-11-12) (Moonraker on port 80/7125), SSH alias `voron` (key auth, user
  `biqu`). `.vscode/sftp.json now points at 192.168.68.63 and uses the SSH key.` Use the Moonraker upload API
  (`POST /server/files/upload`, `root=config`, `path=printer/<dir>`) or `scp` over the alias.
- **Check `print_stats.state` before every RESTART, FIRMWARE_RESTART, macro, heater or
  motion command.** `printing` or `paused` means hands off; uploading a file is fine
  (it only takes effect on the next restart). This rule exists because an agent
  restarted Klipper mid-print on 2026-08-01. There is no camera to catch it.
- After upload, `RESTART`, then query `/printer/objects/query?configfile` to confirm
  the machine actually got what the file says.

## Verification disciplines

- After any config change: sync, RESTART, query machine (`/printer/objects/query?configfile`)
- Prefer measurement over inference. Quote real numbers in commits and ADRs.
- Slicer output: `scripts/verify_slicer_gcode.py path/to/sliced.gcode` checks
  required commands, resolved placeholders, and correct heating order before PRINT_START.

## Extensions (.NET joystick jog service)

Runs on CB1 next to Klipper. Deploy from Windows:
```powershell
cd extensions
.\deploy\install.ps1
```
GPIO pins must not conflict with those defined in `config/printer/mcu/cb1.cfg`
(especially PI1/PI9/PI13).

## Config layout

| Path | Contents |
|---|---|
| `config/klippy.conf` | Root — includes `printer/` then `mainsail.cfg` last |
| `config/printer/` | Machine config split by subsystem |
| `config/printer/macros/` | PRINT_START, PRINT_END, soak, etc. |
| `config/moonraker/` | moonraker.conf + KlipperScreen.conf |
| `firmware/` | Per-MCU `.config` files for `make olddefconfig` |
| `scripts/` | Linter, firmware updater, slicer gcode verifier, `build_orca_bundle.py` (rebuild/check the Orca preset bundle from `slicer_profiles/orcaslicer/src/`), `install_slicer_presets.py` (write presets into Orca/Cura user folders) |
| `docs/FILAMENT_CALIBRATION.md` | per-filament calibration order; results go to `src/` presets and TUNING_PLAN |

## KlipperScreen gotcha

`KlipperScreen.conf` cannot have square brackets anywhere except section headers,
even inside comments — KlipperScreen's regex parser does not skip comments and errors
on `]` followed by any character.
