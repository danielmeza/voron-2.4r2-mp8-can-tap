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

---

## Revisions

### 2026-08-01 — first live `PRINT_START` run; purge/prime quantities tuned, prime line moved to the front border

The 2026-07-31 fix shipped, and the owner ran a full `PRINT_START` (logged: purge at
X335 into the bin worked, extruder at 230 °C, wipe, `G28 Z`). That first live run
surfaced two follow-up complaints, both about **quantity**, not the bucket logic:

1. **Purge length is too small.** `purge_len:10` into the bin reads as "not enough
   filament" for the owner's filament/PA setup.
2. **The prime line is too short and thin, and the nozzle "hangs"/drags at its start.**
   The `E4` over 20 mm line was too little to re-pressurize the nozzle, and the
   post-purge retract (`purge_ret:5`, raised to 8) leaves a void, so the line starts
   starved — thin, low-width, and the nozzle drags the strand.

**What changed:**

- `purge_len: 10 → 50`, `purge_ret: 5 → 8` in `clean_nozzle.cfg` (both call sites move
  together, as the decision requires). The purge is emitted as **two `purge_len / 2`
  moves** because Klipper's default `max_extrude_only_distance` (50 mm) rejects a single
  extrude-only move at the original 100 mm attempt — hit live with `Extrude only move
  too long (100.000mm vs 50.000mm)`. 100 mm was then ruled out by the owner as too much
  filament per clean; 50 mm (2 × 25 mm moves) is the settled value.
- Prime line moved from `mesh_max_x-10, mesh_min_y+5` (Y35, in-mesh) to a **front-border
  line at fixed `Y10`** (`mesh_max_x-5` → `mesh_max_x-105`, ~100 mm long) — reversing the
  2026-07-29 in-mesh placement. Trade-off accepted: a sacrificial prime line at Y10 is
  outside the mesh region, but it is a wipe/skirt line laid once per print on a cold-ish
  plate and then fully covered by the object; being inside the probed area only mattered
  for Z-compensated *object* moves. The in-mesh constraint is dropped for the prime line
  because the user explicitly wants the front border and there is no object geometry there
  in the current prints.
- Prime extrusion: slow-start `E5` over the first 20 mm (F600) to fill the 8 mm retract
  void before the nozzle is expected to lay line, then `E11` over the remaining ~80 mm
  (F2000) for a fat, pressure-backed line.

**Evidence:** live `CLEAN_NOZZLE` run 2026-08-01 verified the 100 mm purge on hardware
(E position went 0→100 then 92 after the 8 mm retract; wipe executed at Z5.2, the same
`tube_top` as before the change — the wipe gcode section is byte-identical to the
pre-change backup). 100 mm was then rejected by the owner as too much; the value is now
50 mm, not yet re-verified on hardware. Owner reported hang/thin-line at prime start on
the earlier `PRINT_START` run. Quantities remain first guesses — revisit after
pressure-advance calibration, as the 2026-07-30 consequences note already anticipated.

### 2026-07-31 — the print-start purge landed on the scrubber tube; now it uses the bucket centre

The "printer goes brr" block purged at `X{bucket_x}` where `bucket_x = tube_x` (290).
`tube_x` is the **scrubber tube**, not a bucket — `CLEAN_NOZZLE`'s own purge targets the
bucket centres (`X{bucket_start + bucket_left_width/(2-bucket_pos) + ...}`, i.e. 265 or
335). Net effect: every print-start purge deposited 10 mm of filament onto the tube/brush
instead of into the bin, which reads as "the purge isn't working" (nothing in the bucket)
and gums up the brush.

**Fix:** the purge now targets the right bucket's centre, computed from the same
`CLEAN_NOZZLE` geometry variables (`bucket_start + bucket_gap + bucket_right_width/2`
= 265 + 45 + 25 = **335**) — the exact position the 2026-07-30 live verification landed
filament in the bin. Still no second magic-number copy; retuning CLEAN_NOZZLE's geometry
moves both call sites.

Also fixes the "clean nozzle doesn't purge" half of the complaint, which was a separate
dead-code path: `params.PURGE` defaulted to `"0"`, so the purge block was skipped in
**every** normal invocation — the UI's bare `CLEAN_NOZZLE` click purged nothing, and
`PRINT_START`'s early call passes no `PURGE`. A bare call now defaults `PURGE` to
`purge_temp_default` (230 °C). `PRINT_START` passes `PURGE=0` explicitly so its early
pre-QGL clean stays purge-free — that half of ADR-0009's rejection is preserved, and
ADR-0009 has its own revision note.

**Evidence:** owner observed filament in the wrong place (not in the bin); machine config
verified byte-identical to the repo before the change (so this was a config bug, not a
sync gap). Position fix not yet re-verified on hardware at time of writing — the live
verification of the same X335 position is the 2026-07-30 `CLEAN_NOZZLE PURGE=230 CLEAN=150`
run recorded above.

### 2026-07-30 — (original adoption)
