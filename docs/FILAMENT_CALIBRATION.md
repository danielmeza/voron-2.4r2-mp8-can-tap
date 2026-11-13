# Calibrating a new filament on this machine

The order matters: each step is judged with the previous ones already settled, and a
step done out of order gets redone. Machine-level items (input shaping, belts, Z
offset, `pressure_advance_smooth_time`) are done and stay; this list is **per filament**.

Everything here runs from OrcaSlicer's *Calibration* menu with the repo presets
installed (`python3 scripts/install_slicer_presets.py`). Slice with the printer
`Voron 2.4r2 350 TAP (repo)`, the process `0.20mm Standard @Voron (repo)` and the
filament preset under test. Run `scripts/verify_slicer_gcode.py` on the first file.

| # | Step | Tool | Judge by | Result goes to |
|---|---|---|---|---|
| 0 | **Dry the spool** | 70–80 °C, 4–6 h | gloss, popping, bubbles in walls and stringing that no setting fixes are moisture | — |
| 1 | **Temperature** | Calibration › Temperature tower | overhang and bridge quality, least stringing; pick the *lowest* good temperature, first layer +5 °C | `nozzle_temperature`, `nozzle_temperature_initial_layer` |
| 2 | **Flow ratio** | Calibration › Flow Rate, pass 1 then pass 2 | smoothest top surface. Over = ridges, proud lines, rough; under = valleys, gaps. **Pass 2 only spans −9…0**, so the pass-1 pick must sit *above* the answer | `filament_flow_ratio` |
| 3 | **Pressure advance** | Calibration › Pressure Advance › **PA Pattern** | corners: no bulge (too little), no gap (too much). Direct drive: 0 → 0.08, step 0.002 | `pressure_advance` (+ `enable_pressure_advance 1`) |
| 4 | **Retraction** | live, see below | least stringing on a two-tower test; stop increasing when strings stop, do not go past ~1.5 mm on the CW2 | `filament_retraction_length`, `filament_retraction_speed` |
| 5 | **Max volumetric speed** | Calibration › Max Flowrate, 5 → 25 mm³/s step 0.5 | the height where the line starts to break up or thin; take 10 % under it | `filament_max_volumetric_speed` |
| 6 | **Cooling / layer time** | a real small part | ABS: fan 10–20 %, overhang 50–60 %, min layer time 12 s; more fan cracks layers | `fan_min_speed`, `fan_max_speed`, `overhang_fan_speed`, `slow_down_layer_time` |
| 7 | *Only if flow moved > 3 %* | `SQUISH_TEST_GRID` (ADR-0016) | first-layer squish re-check | `z_offset` (rare) |

Why this order: temperature changes how every later test looks; flow must be right
before the PA pattern's corners mean anything; retraction depends on both; the
volumetric cap is what makes the `Fast` process preset safe; cooling is judged last on a
real part because the tests print too slowly to show it.

## Retraction on this machine (step 4)

Retraction belongs to the slicer by default ([ADR-0021](adr/0021-slicer-retraction-by-default.md)),
so the fast way to sweep it is to borrow firmware retraction for one print:

Simplest, verified 2026-09-24: leave firmware retraction **off** and run Orca's
Calibration › Retraction tower (0–2 mm, step 0.1). With slicer retraction Orca changes
the `G1 E-` length itself every ~1 mm of height (`; Calib_Retraction_tower` comments
give the length per Z), so no console driving is needed. Pick the lowest clean band,
add margin (the Hyper ABS was clean from 0.1 mm; 0.5 mm was chosen), and write it to
`filament_retraction_length` / `filament_retraction_speed`. Micro-hairs present in
*every* band are travel ooze, not retraction: check travel speed (the "Default Setting"
process travels at 120 mm/s, the repo process at 350) and wipe, then moisture.

Firmware retraction remains the tool for a *live* sweep on a real part
(`SET_RETRACTION` mid-print, ADR-0021); tick it only for that slice.

## Recording the result

1. Edit the filament JSON in `slicer_profiles/orcaslicer/src/filament/`, and the Cura
   material in `slicer_profiles/cura/` if you print from Cura.
2. `python3 scripts/build_orca_bundle.py && python3 scripts/install_slicer_presets.py`
3. Write the numbers and the date in `TUNING_PLAN.md` (Phase 3 list), with what you saw,
   not just the value. If a machine default changes too (PA in `extruder.cfg`,
   `[firmware_retraction]`), sync, `RESTART`, query, and revise the ADR that owns it.

**Orca numbers in Cura** (measured 2026-11-12, `TUNING_PLAN.md`):
- *Flow carries over 1:1.* Cura extrudes `width × height` per line and spaces lines a
  full width apart; Orca extrudes the rounded section `h × (w − h(1 − π/4))` and spaces
  lines by that same reduced width. Both lay `h × flow` per unit area, so Orca's 0.903 is
  90.3 % in Cura. Hole sizes on a real Cura part confirmed it.
- *PA does not.* Cura sends none. The print runs at the `extruder.cfg` default unless a plugin
  sends `M900` (the repo's `M900` macro maps it to `SET_PRESSURE_ADVANCE`) or
  `SET_PRESSURE_ADVANCE`.

## Current filaments

| Filament | Temp | Flow | PA | Retraction | Max vol. | Status |
|---|---|---|---|---|---|---|
| Generic ABS (repo) | 260 | 0.9334 | 0.022 | 0.75 @ 35 | not measured (250 inherited) | flow re-test open since 2026-08-15 |
| Creality Hyper ABS (repo) | 230 / 235 | **0.912**: pass 2's block −1, raised 2026-11-12 because a real part's top skin showed the layer below. Was 0.903 (pass 1: 0.97 → −5 → 0.9215; pass 2: −2 → 0.9215 × 0.98 = 0.903; 2026-09-22, 230 °C) | **0.036** (PA Pattern DDE 0–0.08, 2026-09-24) | **0.5 @ 35** (tower 0–2 mm: only 0.0 strung, clean from 0.1; 0.5 = margin) | 23 (Creality's) | steps 1–4 done in Orca, probably on the black spool (never a grey one); next: max volumetric speed (step 5) |
