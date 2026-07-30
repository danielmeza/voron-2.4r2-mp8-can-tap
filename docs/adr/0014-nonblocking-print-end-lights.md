# 0014 — Post-print light show runs off a delayed_gcode, not a blocking dwell

**Status:** Accepted · 2026-07-25

## Context

`PRINT_END` ended with `_Party_Idle; G4 P120000; Party_Off` — a **blocking** two-minute
dwell. `G4` halts the gcode queue, so for those 2 minutes per print:

- The machine reported busy/unavailable even though the part was done and safe to remove.
- [`STANDBY_WARM`](0005-between-session-keep-warm.md) could not engage until the dwell
  finished, so the bed kept cooling for the full 2 minutes before holding began — cutting
  directly into the thing 0005 exists to prevent.

On a 5–10 minute print, 2 minutes of dead time at the end is not a rounding error.

## Decision

Move the light show into `[delayed_gcode _PARTY_FINISH]` (`macros/leds.cfg`), fired with
`UPDATE_DELAYED_GCODE ID=_PARTY_FINISH DURATION={party_seconds}` from `PRINT_END`, which
then returns immediately. `party_seconds` (120) is declared as a `PRINT_END` variable —
declaring it on `PRINT_START` was tried first and rendered as empty, since a macro's
`variable_*` names are only in scope inside that same macro.

`_PARTY_FINISH` re-checks state when it fires rather than assuming what was true when it
was scheduled:

```
{% if printer.heater_bed.target > 0 %}
    STATUS_READY        # standby still holding -- keep signalling it (0011)
{% else %}
    STATUS_OFF
{% endif %}
```

This matters because standby can expire, or a new print can start, in the 2 minutes
between scheduling and firing — a hardcoded `Party_Off` would go dark on a bed that's
still hot, which is exactly the false "looks safe to touch" signal 0011 exists to prevent.

Rejected alternative: **just delete the dwell**. The light sequence itself (idle → off)
is cosmetic but intentional; the bug was blocking on it, not having it.

## Consequences

- `PRINT_END` now returns in ~0 s instead of 120 s. Measured as part of the combined
  **927 s → 219 s** `PRINT_START`/`PRINT_END` overhead reduction (see
  [TUNING_PLAN.md](../TUNING_PLAN.md)).
- Standby engages up to 2 minutes earlier, holding the bed at temperature instead of
  letting it cool during the dwell — directly extends 0005's benefit.
- `_PARTY_FINISH` reads live state at fire time, so it stays correct even if standby's
  timeout, a cancel, or a new print changes things mid-wait.
- One more scheduled `[delayed_gcode]` alongside `_STANDBY_WARM_OFF` — both must be kept
  in mind if `PRINT_END`/standby timing is changed again.
