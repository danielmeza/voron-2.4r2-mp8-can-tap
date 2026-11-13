# 0017 — PRINT_START refuses a file whose slicer already heated the hotend

**Status:** Accepted · 2026-09-19

## Context

`docs/SLICER_SETUP.md` §6 and `slicer_profiles/README.md` both warn: *do not let the
slicer emit `M190`/`M109` before `PRINT_START`*. `scripts/verify_slicer_gcode.py` checks
for it. Neither is enforced, and the trap came back through a side door: the project
`.3mf` carried its own embedded printer profile, *"Voron 2.4 350 0.4 nozzle(Orcaslicer
project.3mf)"*, with OrcaSlicer's stock start gcode (`M190 / M109 / PRINT_START`),
firmware retraction **off** and `z_hop 0.4`. Every ABS job on 2026-09-18 and 09-19 was
sliced with it. The PLA job sliced with the repo profile (then named *"Voron 2.4 350 0.4"*,
renamed *"Voron 2.4r2 350 TAP (repo)"* on 2026-09-19 so it can never be confused with an
embedded copy) was clean.

Measured on `klippy.log`, both days:

| Phase | 09-18 cube (warm, bed 66 °C) | 09-19 "HMI Main Body" (cold, bed 28 °C) |
|---|---|---|
| slicer `M190` | 144 s | 334 s |
| slicer `M109 S260` | 74 s | 84 s |
| cool-down 260 → 155 °C for the TAP guard | 120 s (inside `CLEAN_NOZZLE`'s `M109 S150`) | ~90 s (inside `[probe] activate_gcode`) |

About **3.3 min per print** of pure waste, with the nozzle oozing at 260 °C over the
bed. Worse: on 09-19 a cancel was sent at 15:00:39 during that cool-down. Moonraker
logged `pause_resume/cancel pending: 60.00 seconds`; it never landed because
`virtual_sdcard` only honours a cancel between SD lines and the whole of `PRINT_START`
is one line. The user emergency-stopped at 15:02:18 with the gantry mid-QGL. (The cancels of 09-18
and 09-19 were the user's own, on realising the file carried the wrong material or
profile; the failure was that a cancel could not take effect.)

The same session showed a second profile-hygiene leak: the calibration cube was sliced
with Orca's *"Default Setting"* process, which emits `SET_VELOCITY_LIMIT ACCEL=250` on
bridges ([ADR-0018](0018-reset-velocity-limits-after-print.md)). And the ABS filament
profile is untracked and has drifted from the documented flow ratio
([TUNING_PLAN](../TUNING_PLAN.md)). Documentation alone does not hold.

## Decision

`PRINT_START` **fails fast** if `printer.extruder.target` is already above
`preheated_z_home_temp` (150 °C) when it is entered:

```
{% if refuse_slicer_preheat == 1 and printer.extruder.target|float > preheated_z_home_temp %}
    { action_raise_error("PRINT_START refused: ... M104/M109 before PRINT_START ...") }
{% endif %}
```

`action_raise_error` aborts the macro; mainsail's `[virtual_sdcard] on_error_gcode:
CANCEL_PRINT` then cancels the job and turns the heaters off. The print stops in under a
second with a message that names the fix, instead of three minutes later in a state that
cannot be cancelled.

`variable_refuse_slicer_preheat: 1` on `PRINT_START` turns the guard off for one print
(`SET_GCODE_VARIABLE`), for the case where the user preheated the hotend by hand.

Rejected alternatives:

- **Adapt instead of refuse** (skip the 150 °C step, probe hot). Probing TAP at 260 °C
  marks the PEI and the guard in `[probe] activate_gcode` exists precisely to stop that.
- **Detect and just warn.** The waste and the uncancellable wait would still happen; a
  warning in a console nobody watches during warm-up changes nothing.
- **Fix it in Moonraker's upload hook.** Possible, but the macro sees the real heater
  state and works for files that arrive by any route (USB, KlipperScreen, API).

## Consequences

- A hand-preheated hotend (e.g. after loading filament at 240 °C) is refused too. The
  message says how to bypass; `M104 S0` before pressing print is the other way.
- The guard checks the *target*, not the temperature: a nozzle still cooling from a
  previous print with target 0 passes, and the TAP guard handles the cool-down as before.
- The root cause stays in the slicer. The checklist for any new project file: select the
  repo printer profile, the *"0.20mm Standard @Voron"* process, and run
  `scripts/verify_slicer_gcode.py` on the output before uploading.

## Revisions

### 2026-11-12 — the refusal message names the hand-preheat case

On 2026-11-12 19:10 a correct Cura file was refused with "hotend target is already 250c, so
the slicer emitted M104/M109 before PRINT_START". The file had no heating line before
`PRINT_START`; the 250 °C had been set by hand to load filament. That case was already
accepted in Consequences, but the message blamed the slicer and named a fix that did not
apply. The message now gives both cases. For a hand-set target, the print has been
cancelled and `CANCEL_PRINT` → `TURN_OFF_HEATERS` has switched the heaters off, so starting
the print again simply works. Otherwise the slicer is the cause, as before. The guard's
condition and behaviour are unchanged.
