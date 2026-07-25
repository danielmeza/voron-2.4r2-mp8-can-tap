# 0011 — Signal standby on the LEDs, and surface chamber temp on the print screen

**Status:** Accepted · 2026-07-24

## Context

Two small but related gaps in "what is the machine actually doing right now".

**1. Standby was invisible.** After [ADR-0005](0005-between-session-keep-warm.md) the
printer holds the bed at ~100 °C *and* keeps the steppers energised between prints. But
`PRINT_END` finishes with `Status_Off` and `_CASELIGHT_OFF`, so a machine that is hot and
powered looks exactly like one that is cold and off. That is the wrong signal on a
machine you reach into to swap parts, and it is actively misleading now that the gantry
is held rather than free.

**2. Chamber temperature was not on the print screen.** Worth knowing at a glance, since
the thermal gate and adhesion behaviour both hinge on it.

Investigating turned up two things that were not obvious:

- **`display.cfg` is dead config.** All 132 lines of `[display_data __voron_display ...]`
  — including a chamber readout — are inert, because there is **no `[display]` section
  anywhere**. The mini12864 was never configured. The real screen is **KlipperScreen** on
  a BTT-HDMI5.
- **KlipperScreen structurally cannot show a `temperature_fan` on the print titlebar.**
  `job_status.py` resolves `titlebar_items` by iterating `get_temp_sensors()`, and
  `ks_includes/printer.py` defines that as `get_config_section_list("temperature_sensor")`
  — `temperature_fan` sections are returned by a *different* method that the titlebar
  never calls. The chamber here is `[temperature_fan chamber]`, so no `titlebar_items`
  value could ever have matched it.

## Decision

**Standby LEDs.** Drive the Stealthburner LEDs to `STATUS_READY` while standby holds, and
`STATUS_OFF` when it ends. `STATUS_READY` already maps to a colour literally named
`standby` in `_sb_vars` (dim white, `w: 0.1`) — it was built for this.

- `STANDBY_WARM` → `STATUS_READY`
- `PRINT_END` re-asserts `STATUS_READY` **after** its existing `Status_Off`, which would
  otherwise clear it
- `_STANDBY_WARM_OFF` (expiry) and `STOP_STANDBY_WARM` → `STATUS_OFF`

The LEDs are therefore lit exactly while the bed is hot **and** the motors are held —
i.e. while it is unsafe to reach in and futile to hand-move the gantry.

**Chamber on screen.** Mirror the chamber as a real `[temperature_sensor]` using the
`temperature_combined` sensor type with a single input:

```ini
[temperature_sensor chamber]
sensor_type: temperature_combined
sensor_list: temperature_fan chamber
combination_method: mean
maximum_deviation: 999.9
```

plus `titlebar_items: chamber` under `[printer Printer]` in `KlipperScreen.conf`.

Rejected alternatives:

- **Convert the chamber to a plain `temperature_sensor`** — it would stop driving the
  exhaust fan, which is load-bearing for the soak gate ([ADR-0003](0003-temperature-gated-soak.md)).
- **Declare a second BME280** — a duplicate I2C device for a display label.
- **Patch KlipperScreen** — a local fork that any update would clobber.

`temperature_combined` consumes anything exposing `get_temp()`, which `temperature_fan`
does, so this costs one extra object and no extra hardware transaction.

## Consequences

- Verified: `temperature_sensor chamber` and `temperature_fan chamber` both read **47.39 °C**,
  and the sensor now appears in `available_sensors`, which is what KlipperScreen scans.
- The chamber appears **twice** in Mainsail's temperature list (fan and sensor). Mildly
  redundant, and arguably useful since the sensor entry charts cleanly.
- `KlipperScreen.conf` is otherwise auto-generated; the new `[printer Printer]` block sits
  above the `#~#` managed region so KlipperScreen will not rewrite it. Backed up first.
- **`display.cfg` remains dead** and is left in place deliberately — it is a complete,
  working mini12864 layout, worth keeping if a physical LCD is ever fitted. It should not
  be mistaken for live config; nothing in it runs today.
- LED state is now a real safety signal, so anything that changes standby must keep it
  truthful. A lit toolhead means hot bed and energised motors.

---

## Revisions

### 2026-07-24 — KlipperScreen rejects square brackets, even inside comments

Adding `titlebar_items` raised a KlipperScreen error banner:
*"Section headers have extra information after brackets possible newline issue"*.

Klipper itself was fine — 0 warnings, the print kept running. The banner comes from
KlipperScreen's own validator (`ks_includes/config.py`), which scans the raw file with a
regex matching *any content, a closing bracket, then at least one more character* — and
**does not skip comment lines**. The explanatory comment added alongside the setting
mentioned a config section name in brackets, and that was enough to trip it.

**Rule for `KlipperScreen.conf`: no square brackets anywhere except real section
headers — not even in comments.** The first attempt at a fix quoted the offending regex
and tripped the same check, which is a good illustration of how easy it is to reintroduce.

The file is now tracked in `config/moonraker/` so this cannot silently regress.
