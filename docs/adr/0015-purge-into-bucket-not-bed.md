# 0015 — Purge into the trash bin, not onto the print bed

**Status:** Accepted · 2026-07-30

## Context

The original finding was narrow: `PRINT_START`'s purge/prime line ran at a hardcoded
`X{x_wait-50} Y4`, and `Y4` sits outside `[bed_mesh] mesh_min: 30,30` — unmeasured,
extrapolated territory. Confirmed live on the machine 2026-07-29 (`X310→210 Y35`, both
ends inside `30-320`) and documented as fixed.

Verifying it on hardware surfaced the real complaint: the purge line's actual job was
never "stay inside the mesh," it was leaving a **visible blob of filament on the print
surface** every single print, right next to the bucket-and-brush assembly that exists
specifically to catch this — confirmed physically: bucket/brush sit at `tube_x:290`,
`Y=Ry` (350.01, bed max), rear-right corner.

`CLEAN_NOZZLE` already has this exact mechanism (`params.PURGE > 150` block: extrude,
retract, fan-harden) — but [ADR-0009](0009-nozzle-clean-pressure-relief.md) rejected
turning it on. That rejection still holds for the reason it was written: `PRINT_START`
calls `CLEAN_NOZZLE` **early**, at `preheated_z_home_temp` (150 °C, chosen for TAP-probe
consistency), before QGL and the heat soak. Enabling `PURGE` there forces the *whole*
temperature ladder to 230 °C+ at that point, plus a second full wipe pass once cooled
back down for cleaning — defeating the thermal sequencing several other ADRs
(0003, 0005) exist to protect.

## Decision

Don't touch the early `CLEAN_NOZZLE` call. Instead, add a standalone purge step
**after** `M109 S{target_extruder}` — the point where the nozzle is already at real
print temperature and about to start printing anyway:

1. Move to the bucket (`tube_x`, bed-max `Y`) and purge `purge_len` (10 mm), retract
   `purge_ret` (5 mm) — same lengths `CLEAN_NOZZLE` already uses.
2. Move to the meshed area (`mesh_max_x-10, mesh_min_y+5`) and lay a **short** prime
   line (4 mm over 20 mm, was 20 mm over 100 mm) so the first object move gets fresh
   extrusion pressure without the bulk of the purge sitting on the bed.

Coordinates and lengths are read live via `printer["gcode_macro CLEAN_NOZZLE"].tube_x`
etc., not copied as a second set of magic numbers — retuning `CLEAN_NOZZLE`'s purge
variables moves both call sites together.

Rejected alternatives:

- **Bucket purge only, no bed prime.** Simplest, fully solves the mess-on-bed
  complaint. Rejected because the travel from the bucket (`X290,Y350`) to the object's
  actual start is unprimed — real risk of a weak or skipped first extrusion move.
  Chosen deliberately over this: keep a short prime.
- **Re-invoke `CLEAN_NOZZLE PURGE={target_extruder}`** a second time, later in
  `PRINT_START`. Reuses more existing code, but `CLEAN_NOZZLE` always wipes when
  `CLEAN > 0` — a second full wipe pass at print temp, duplicating work the 150 °C
  call already did. Standalone gcode was less code than guarding a full macro call
  against re-wiping.
- **Leave the relocated 100 mm bed line as-is** (this ADR's starting point). Solves the
  extrapolated-mesh bug but not the actual complaint — still deposits the bulk of the
  purge on the print surface.

## Consequences

- **Verified live 2026-07-30:** ran `CLEAN_NOZZLE PURGE=230 CLEAN=150` directly —
  heated 230 °C, purged into the bin, retracted, cooled to 150 °C, wiped clean,
  confirmed visually (filament landed in the bin, brush cleaned it). This validates the
  bucket-purge mechanics the new code reuses.
- **Not yet verified as a unit:** the short bed-prime addition has not been exercised
  through an actual `PRINT_START` call — only its component pieces (bucket purge above;
  mesh-relative bed coordinates verified 2026-07-29) are individually proven.
- **Purge quantity is a first guess.** `purge_len:10 / purge_ret:5` into the bin plus
  `E4` on the short prime is carried over from `CLEAN_NOZZLE`'s existing defaults, not
  freshly tuned for this two-stage split. **Revisit after pressure-advance
  calibration** — PA changes how much priming the first extrusion actually needs, so
  tuning purge quantity before PA would mean re-tuning it again after.
- Net filament per print goes from 20 mm (one bed line) to `10 (purge) + 4 (prime)` mm
  forward plus a 5 mm retract in between — a small reduction, not the point of the
  change.
- Adds one extra travel leg (heat-center → bucket → mesh start) vs. the old single
  move. Time cost not yet measured on a full print.
