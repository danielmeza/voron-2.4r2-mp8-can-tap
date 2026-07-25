# 0009 — Relieve nozzle pressure before wiping

**Status:** Accepted · 2026-07-24

## Context

Filament strings were staying attached to the nozzle *after* `CLEAN_NOZZLE` ran — the
wipe visibly happened, and the nozzle came away stringy anyway.

`CLEAN_NOZZLE` (adapted from the Decontaminator purge-bucket mod) has two independent
halves:

- a **purge** half, gated on `params.PURGE > 150`
- a **wipe** half, gated on `params.CLEAN > 0`

`PRINT_START` calls it as:

```
CLEAN_NOZZLE CLEAN={preheated_z_home_temp}      # CLEAN=150, no PURGE
```

`params.PURGE` therefore defaults to `"0"` and **the entire purge block is skipped** —
including the only two things in the macro that deal with pressure and residue:

| Skipped | What it does |
|---|---|
| `RETRACT_FILAMENT LENGTH=5` | the macro's **only** retraction |
| `M106 S127` | part fan, to chill and harden the ooze before wiping |

So the nozzle was wiped with the previous print's residual pressure still behind it. It
oozed back onto itself as fast as the tube scrubbed it off — the wipe was working, it was
simply losing a race.

Wiping happens at 150 °C, which is the *TAP probe* temperature (`preheated_z_home_temp`),
chosen for probing consistency rather than for cleaning. ABS at 150 °C is tacky rather
than brittle, which makes the race harder to win.

## Decision

Add a small **pressure-relief retract on the no-purge path only**, immediately before the
cooling/wipe section:

```
{% if params.PURGE|default("0")|int <= 150
      and printer.extruder.temperature >= min_extrude_temp %}
    M83
    RETRACT_FILAMENT LENGTH={clean_pre_retract}   # 1.5 mm
    G92 E0
{% endif %}
```

Guarded on `min_extrude_temp` so it can never be commanded against a cold nozzle, which
Klipper would reject and which would abort `PRINT_START`.

Rejected alternatives:

- **Pass `PURGE={target_extruder}`** — runs the macro as designed and does fix the
  pressure, but costs 10 mm of filament and ~30–40 s per print, on a machine where the
  whole point of recent work has been cutting `PRINT_START` time.
- **Lower the wipe temperature** — probably effective (brittle residue wipes off), but
  `preheated_z_home_temp` is shared with TAP probing, so decoupling them means an extra
  reheat before QGL. Held in reserve if the retract alone is not enough.

## Consequences

- Costs ~1.5 mm of filament and well under a second per print.
- Does **not** change probing temperature, so TAP behaviour is untouched.
- The retract is unretracted by the slicer's own priming on the purge line, so no
  under-extrusion at the start of the print.
- If stringing persists, the next lever is temperature: wipe at ~130 °C and reheat to 150
  for probing. That trade is a reheat of roughly 15–20 s.
- **The 40 mm `ooze_offset_z` move at 120 mm/min is a ~20 s no-op on this path.** It
  exists to stretch a molten string until it snaps, which only does anything when the
  nozzle is cooling *with pressure behind it* — i.e. right after a purge. With no purge
  there is nothing to draw out. Left in place (it matters if purging is ever enabled) but
  documented in the macro so it is not mistaken for useful work.

---

## Revisions

### 2026-07-24 — the ooze-pull is now skipped outright when no purge ran

This ADR documented the 40 mm / 120 mm-per-min climb as a ~20 s no-op on the no-purge
path but left it in place. It is now conditional: the slow climb only runs when
`PURGE > 150`, and the no-purge path just clears the tube at travel speed.

**Measured:** `CLEAN_NOZZLE` went from **48.7 s to 18.0 s**, confirmed by the toolhead
sitting at Z10.2 instead of Z45.2 during the wipe. Part of that gain is a companion
change — `PRINT_START` now passes `Z_HOME=0`, because `QUAD_GANTRY_LEVEL` runs
immediately afterwards and ends with its own `G28 Z`, making the macro's trailing home a
duplicate TAP probe cycle.
