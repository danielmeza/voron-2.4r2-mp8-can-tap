# 0025 — Mainsail macro groups follow the printer state, and live in the repo

**Status:** Accepted · 2026-11-12

## Context

Mainsail runs in expert mode, so a macro gets a dashboard button only through a macro
group. Each group and each macro in it has its own *standby / printing / paused*
switches. The groups live in Moonraker's database (`mainsail` namespace, key `macros`),
not in `printer_data/config`, so nothing in this repo recorded them.

On 2026-11-12 there were three groups (Parking, Fans, Extrude) covering 16 of the 68
public macros, and the visibility had drifted into unsafe combinations:
- `CLEAN_NOZZLE` was offered while **paused**. It ends with `G28 Z`, and TAP homes Z at
  the bed centre, so the nozzle would come down onto the part.
- `CENTER` was offered while **printing**. It moves to X175 Y175 Z50 with no pause.
- One entry, `SET_ELECTRONIC_BAY_FAN`, was no longer a macro.
- `BED_FANS_VARS`, a variable holder with no gcode, was a button.

## Decision

**One rule set, applied per state.** Each public macro was mapped to whether it moves the
toolhead, homes, changes a heater, and which parameters it needs (2026-11-12 survey of
every `[gcode_macro]`).

- **Printing:** nothing that moves, homes or changes a heater target. Only fans, lights,
  read-only diagnostics, and arming a layer pause remain.
- **Paused:** moves that cannot come down onto the part (`PARK_FRONT`, `PARK_REAR`), and
  filament load/unload for a mid-print change. `CENTER`, `PARK` (Z30/Z50 over the bed
  centre) and `CLEAN_NOZZLE` (`G28 Z`) are standby-only.
- **Standby:** everything else that has a button.
- **No button at all:**
  - the slicer interface: `PRINT_START`, `PRINT_END`, and the M-code shims `M109`, `M140`,
    `M141`, `M190`, `M201`, `M203`, `M205`, `M900`, `SET_PRINT_STATS_INFO`;
  - `PAUSE`, `RESUME` and `CANCEL_PRINT`, which Mainsail's status panel already offers
    (ADR-0002);
  - `M600`, which only calls `PAUSE`;
  - internal helpers and state holders: `BED_FANS_VARS`, `SET_HEATER_TEMPERATURE`,
    `SHOW_MESSAGE`, `STATUS_*`;
  - aliases: `Home_Level` = `G32`, and `Party_On`/`Party_Off`, which duplicate the
    `CASE_LIGHTS` toggle;
  - `RETRACT_FILAMENT`, which is unsafe in absolute-E mode;
  - the generic `HEAT_SOAK` family: `SOAKER` is required, and `HEAT_SOAK_RESUME`'s
    default callback is undefined. KlipperScreen still has them.

| Group | Standby | Printing | Paused | Macros |
|---|---|---|---|---|
| Layer pause | – | ✓ | ✓ | `SET_PAUSE_NEXT_LAYER` (printing only), `SET_PAUSE_AT_LAYER` |
| Prepare | ✓ | – | – | `CONDITIONAL_HOME`, `G32`, `PREHEAT_ABS`, `STANDBY_WARM`, `STOP_STANDBY_WARM` |
| Toolhead | ✓ | – | ✓ | `PARK_FRONT`, `PARK_REAR`; `CENTER`, `PARK` standby only |
| Filament | ✓ | – | ✓ | `LOAD_FILAMENT`, `UNLOAD_FILAMENT`; `CLEAN_NOZZLE` standby only |
| Fans | ✓ | ✓ | ✓ | parts fan on/max/off, Nevermore fast/low/off; `SET_EXHAUST_FAN` not while printing (its default `S=0` vents the chamber; the chamber fan target stays editable in the temperature panel) |
| Lights | ✓ | ✓ | ✓ | `CASE_LIGHTS`, nozzle LEDs on/off, logo LEDs off |
| Calibration | ✓ | – | – | `SQUISH_TEST_GRID`, `TEST_SPEED` |
| Power | ✓ | – | – | `TURN_OFF_HEATERS`, `POWER_OFF` (cuts mains, ADR-0022) |
| Diagnostics | ✓ | ✓ | ✓ | `DUMP_VARIABLES`, `GET_VARIABLE`, `PRINT_TAP_STATE` |

**The repo is the source of truth.** [`system/mainsail/macros.json`](../../system/mainsail/macros.json)
is the whole `macros` value. Its `hiddenMacros` list holds every public macro without a
group, so simple mode agrees with expert mode. The existing group IDs were kept, renamed
Parking → Toolhead and Extrude → Filament; the new groups use fixed IDs, so a re-apply
does not duplicate panels. Apply it with:

```bash
curl -s -X POST -H "Content-Type: application/json" http://voron.local/server/database/item \
  -d "{\"namespace\":\"mainsail\",\"key\":\"macros\",\"value\":$(cat system/mainsail/macros.json)}"
```

Then reload every open Mainsail tab before touching its settings: a stale tab writes
its own copy back. If a group is changed in the Mainsail UI, pull it back with
`curl -s "http://voron.local/server/database/item?namespace=mainsail&key=macros"` and
commit it. The widescreen layout (`dashboard.widescreenLayout2`) lists the groups in
table order. The other layouts are Mainsail's defaults, which append groups by
themselves.

Rejected:
- **Hiding by renaming macros to `_NAME`.** That changes Klipper config to fix a UI
  concern, and it breaks the `KlipperScreen.conf` menus that call them by name.
- **Simple mode with `hiddenMacros`.** It has no per-state visibility, which is the
  whole point here.
- **One big group.** It mixes printing-safe buttons with ones that home the printer.

## Consequences

- When a macro is added or renamed, it needs a line in `macros.json`, or it shows up
  nowhere. A public macro missing from both the groups and `hiddenMacros` is the sign
  that this step was skipped.
- The survey found macro behaviour that is out of scope here but worth fixing. Each item
  below is a finding, not yet verified on the machine:
  - `LOAD_FILAMENT`/`UNLOAD_FILAMENT` heat to a fixed 250 °C and leave `G91` set. In a
    pause, `RESUME` restores the gcode state, but it restores the hotend temperature only
    when the printer went idle, so after a mid-print filament change the print may
    continue at 250 °C. **Fixed 2026-11-12** — see [ADR-0002](0002-mainsail-owns-client-macros.md).
  - `STANDBY_WARM`'s auto-off and `STOP_STANDBY_WARM` treat *paused* as not printing, so
    they can turn the bed off and release the motors during a pause. **Fixed 2026-11-12** —
    see [ADR-0005](0005-between-session-keep-warm.md).
  - ~~`SET_HEATER_TEMPERATURE HEATER=extruder` without `TARGET` sets 0~~: not a defect.
    Klipper's own command defaults `TARGET` to 0, and the override only mirrors it. Listed in error.
  - `HEAT_SOAK_RESUME`'s default `ON_RESUME=_HEAT_SOAK__BASE_RESUME` is not defined.
