# Voron 2.4r2 350 — Config Cleanup & Tuning Plan

Working plan for getting this printer properly tuned, following
[Ellis' Print Tuning Guide](https://ellis3dp.com/Print-Tuning-Guide/).

**Presenting problem:** poor bed adhesion, noticeably worse away from the centre of
the bed. Improved (but not solved) by adding a bed mesh and a 15 min bed heat.

**Machine:** Voron 2.4r2 350mm · CoreXY · TAP (`probe:z_virtual_endstop`) ·
Manta M8P v2.0 (STM32H723, CAN bridge) + EBB SB2209 RP2040 (CAN) + CB1 Linux MCU
· Klipper host `v0.13.0-708-g7046bd00e`

---

## How to use this file

Tick boxes as you go. Anything marked **[verify]** means "confirm the result before
moving on" — several findings below only surfaced because a prediction was checked
against the live machine instead of being assumed.

Run the config linter after any config edit:

```bash
python3 scripts/klipper_config_lint.py     # exit 1 on ERROR
```

It replicates Klipper's real load semantics (non-recursive `glob`, sorted includes,
`RawConfigParser(strict=False)` → **duplicates merge silently, last file wins**).
That last-wins rule is the root cause of most findings here.

---

## Phase 0 — Config hygiene ✅ DONE

Resolve silent conflicts so later tuning measures something real. **Repo only —
not yet synced to the machine.**

- [x] `[tmc2209 stepper_z/z1/z2/z3] run_current` → **0.9 A on all four**
      (43 % of the OMC 17HS24-2104S 2.1 A rating; Ellis: start 40–50 %, max 70 %).
      Was silently z=1.0 / **z1=0.8** / z2=1.0 / z3=1.0 — the rear-left corner ran
      20 % lower than the other three.
- [x] `stepper_drivers.cfg` is now the **sole owner** of all driver settings;
      `steppers.cfg` keeps only motor/kinematic values.
- [x] `[tmc2209 extruder] run_current` — removed the shadowed `0.55`; `0.5` in
      `stealburner/stepper.cfg` was already the effective value. No behaviour change.
- [x] `[resonance_tester]` consolidated into `gantry/input_shaping.cfg`.
      `probe_points` → **175,175,20** (centre of a 350 bed; was silently 100,100,20).
      Deleted duplicate `gantry/rezonance.cfg` and `stealburner/resonance.cfg`.
- [x] Removed dead `[include macros/*.cfg]` / `macros/**/*.cfg` — matched nothing
      (macros live under `printer/macros/`, already covered). Silent because wildcards.
- [x] Removed the obsolete `relative_reference_index` comment block. That option no
      longer exists in Klipper v0.13, and per Ellis it is **only needed with a
      physical Z endstop** — irrelevant for TAP.
- [x] Commented out dangling aliases `IND`/`FAN` → `VF6`, `MOT1_DIAG` → `ESTOP0`,
      `MOT8_DIAG` → `ESTOP7` (targets never defined; unused today).
- [x] Removed the dead second `[gcode_macro M190]` in `chamber/nevermore.cfg`.
      Bed-fan behaviour is preserved via `M140` → `SET_HEATER_TEMPERATURE`.
- [x] Removed duplicate `control: pid` inside `heatend.cfg`.
- [x] Tracked `config/mainsail.cfg` — a **literal** (non-wildcard) include, so
      Klipper hard-errors without it and the repo could not restore.

**Result:** linter goes from 8 errors / 2 warnings / 8 info → only the mainsail
override conflicts below (which are a design decision, not hygiene).

### Not yet synced to the machine

Phase 0 lives in git only. Syncing changes real behaviour — the Z current change
(z1 0.8→0.9, others 1.0→0.9) is the only functional one.

- [ ] Diff repo vs machine, upload, `RESTART`, **[verify]** klipper `ready`
- [ ] **[verify]** effective Z currents are 0.9 on all four
- [ ] **[verify]** `resonance_tester.probe_points == [[175,175,20]]`

---

## Phase 0.5 — Decision needed: mainsail.cfg overrides your macros ⚠️

Discovered only after tracking `mainsail.cfg`. Because `[include mainsail.cfg]` is
the **last** include, it wins every conflict. **Confirmed live on the machine:**

| Macro | Live version | Your version in `macros/printing.cfg` |
|---|---|---|
| `PAUSE` | mainsail (`PAUSE_BASE`) | **dead** |
| `RESUME` | mainsail (`RESUME_BASE`) | **dead** |
| `CANCEL_PRINT` | mainsail (`CANCEL_PRINT_BASE`) | **dead** |

Real consequences today:

- `CANCEL_PRINT` **never calls `PRINT_END`** → your chamber-to-40 °C, part-fan-off
  and park-on-cancel logic never runs
- `CANCEL_PRINT` **never calls `STOP_HEAT_SOAK`** → a heat soak can survive a cancel
- `PAUSE`/`RESUME` never disable/restore the filament sensor

Also `[virtual_sdcard] path`: `printer.cfg` says `/home/biqu/printer_data/gcodes`,
mainsail says `~/printer_data/gcodes` — same place, harmless.

- [ ] **Decide:** keep mainsail's macros (delete yours) **or** keep yours
      (move `[include mainsail.cfg]` above `[include printer/...]`, or use
      `_CLIENT_VARIABLE` + `user_pause_macro`/`user_resume_macro`/`user_cancel_macro`,
      which is the intended extension point)
- [ ] Re-run linter until clean

---

## Phase 1 — Adhesion fixes that respect a 5–10 min print workflow

**Constraint:** typical parts print in 5–10 min, in back-to-back sessions with a
10–15 min gap to remove the part and clean. A flat "soak 45–60 min, full mesh every
print" tax is unacceptable — it would dwarf the print itself.

**Principle: gate on measured temperature, never on elapsed time.**
`TEMPERATURE_WAIT` returns *immediately* if the sensor is already in range
(verified in `klippy/extras/heaters.py` — it also accepts any passive
`temperature_sensor`, not just heaters). So one macro serves both cases: a cold
machine waits as long as it needs, a warm machine waits zero seconds. This is
strictly better than tracking "time since last print", because it measures the
physical quantity that actually matters instead of a proxy for it.

What actually moves Z is **frame/gantry metal temperature**, not chamber air and
not the clock.

### Tier A — no hardware, do now

- [ ] **Adaptive meshing.** `BED_MESH_CALIBRATE ADAPTIVE=1 ADAPTIVE_MARGIN=5`
      (supported in your v0.13). Probes only the print's footprint, so a small
      centred part costs seconds instead of a full 11×11 sweep — while large prints,
      which is where the outside-centre adhesion actually bites, still get correct
      data. This removes the "fresh mesh vs. fast start" trade-off entirely.
- [ ] **Replace the fixed soak timer with a temperature gate.** Today: 15/10/5 min
      flat. Instead `TEMPERATURE_WAIT SENSOR='temperature_fan chamber' MINIMUM=<target>`
      plus a sanity cap. Warm machine → instant. Cold machine → waits properly.
- [ ] **Keep-warm between sessions.** Hold the bed at a standby temp after
      `PRINT_END` so the frame doesn't cool during the part swap; auto-off after N
      minutes. Next session then passes the temperature gate immediately.
      ⚠️ `[idle_timeout] timeout: 1800` runs `TURN_OFF_HEATERS` after 30 min idle —
      keep-warm must account for that or it will be silently killed.
- [ ] **Wash the plate** with dish soap and water, air dry. Ellis: **IPA alone is
      insufficient** for maintenance cleaning.
- [ ] **[verify] `PROBE_ACCURACY`** — Ellis' bar: **std dev ≤ 0.004, range ≤ 0.0125**.
      If this fails, stop — fix probing before tuning anything else.
- [ ] Disable **z-hop on the first layer** in the slicer.
- [ ] Consider first-layer line width **120 %**.
- [ ] Purge line runs at `Y4`, outside `mesh_min: 30,30` → extrapolated mesh.
      Move it inside the meshed area.

### Tier B — one cheap thermistor (recommended, unlocks the real fix)

- [ ] **Add an NTC 100k to MP8 `TH1` (PC5)** — 4 thermistor ports are free (TH0/1/2/3;
      only THB is used) and `printer/aux_temperature_sensors.cfg` is an empty
      placeholder waiting for exactly this. Clamp it to the **gantry X extrusion**
      (or a Z frame extrusion) — it must read *metal*, not air. Your existing
      chamber sensor is a BME280, which measures air and is the wrong signal here.
- [ ] Gate the soak on **frame temp** instead of chamber air.
- [ ] **Add `[z_thermal_adjust]`** (`temp_coeff`, `max_z_adjustment`, `smooth_time`
      — confirmed present in your Klipper). It continuously corrects Z as the frame
      expands, so you can start printing *before* thermal equilibrium instead of
      waiting for it. Ellis lists this as a legitimate thermal-drift mitigation.
      `temp_coeff` **must be calibrated empirically** (~0.02 mm/°C is only a
      starting ballpark for aluminium).

### Mesh strategy — keeps your prebuilt mesh idea

1. Take **one** high-quality full 11×11 mesh at true equilibrium (the 45–60 min soak,
   **once**). Save as `default`. Record the frame temp at that moment via
   `[save_variables]`.
2. Normal sessions: load `default` and let `z_thermal_adjust` cancel the global Z shift.
3. Large parts, or when frame temp deviates beyond a threshold from the mesh's
   reference: `BED_MESH_CALIBRATE ADAPTIVE=1` instead.

**Why this works:** frame expansion mostly produces a *uniform* Z offset, which
`z_thermal_adjust` cancels directly; mesh *shape* changes far more slowly. So a good
prebuilt mesh plus live Z compensation approximates a fresh mesh at a fraction of the
time cost.

**Honest caveat:** this is well-supported but not free — `temp_coeff` has to be
calibrated, and if the bed/gantry *shape* (not just height) shifts materially with
temperature, the prebuilt mesh will drift and adaptive re-meshing is the fallback for
critical prints.

**Recheck the symptom here before spending money or effort on Phase 2.**

---

## Phase 2 — Mechanical (only if mesh range stays > 0.10 mm after Phase 1)

Measured saved mesh: **range 0.1487 mm**, strongly Y-dominated —
front `+0.014`, middle `−0.055`, back `+0.033` (0.068 mm dip); X nearly flat
(0.010 mm span). Worst point **x=30, y=175 → −0.0988** (left-middle edge).

Ellis: *"Most bed mesh issues are caused by the gantry rather than the bed itself."*

- [ ] Z belt tension — equal on all three, **140 Hz over a 150 mm span**
- [ ] A/B belt tension — **110 Hz**
- [ ] [Voron V2 gantry squaring](https://ellis3dp.com/Print-Tuning-Guide/articles/voron_v2_gantry_squaring.html)
      — float gantry, de-rack, retension, **QGL repeatedly after full heat soak,
      then tighten Z joints while hot**
- [ ] Check for debris under the spring steel; check PEI for bubbling
- [ ] Revisit bed screw tightness while hot
- [ ] Re-mesh and compare range **[verify]**

---

## Phase 3 — Ellis' tuning order (once Z is repeatable)

Do **not** start until Phase 1/2 give a consistent first layer.

- [ ] Extruder calibration (rotation_distance)
- [ ] Build surface preparation
- [ ] First layer squish
- [ ] Pressure advance — pattern method
- [ ] Extrusion multiplier
- [ ] Cooling and layer times
- [ ] Retraction
- [ ] Infill/perimeter overlap
- [ ] Stepover
- [ ] Max volumetric flow rate
- [ ] Motor currents — **[verify]** motors stay < 80 °C in a heat-soaked chamber
- [ ] Max speeds and accelerations
- [ ] Re-run input shaping **at the corrected 175,175 point**

---

## Phase 4 — Firmware reflash (independent of tuning)

Host is `v0.13.0-708`; all three MCUs still run v0.12 — six `deprecated_mcu_code`
warnings. Self-healed after the update, but should be closed out.

| MCU | Board | Firmware | Transport |
|---|---|---|---|
| `mcu` | CB1 Linux MCU | v0.12.0-401 | `/tmp/klipper_host_mcu` |
| `MP8` | Manta M8P v2.0 (H723) `f24b112c272e` | v0.12.0-456 | CAN — **is the USB-CAN bridge** |
| `EBB` | EBB SB2209 RP2040 `19de38651b72` | v0.12.0-410 | CAN |

**Two hazards specific to this machine:**

1. **The Manta is the CAN bridge.** Reflashing it drops the whole bus, EBB included.
   Order: **Linux MCU → EBB → Manta last.** SD-card flashing is the Manta's recovery path.
2. **TAP means a bad EBB flash removes your only Z endstop**
   (`[probe] pin: !EBB:TAP_PROBE` + `endstop_pin: probe:z_virtual_endstop`).
   **[verify] the probe triggers before any `G28 Z`** or the toolhead drives into the bed.

- [ ] `~/klippy-env/bin/python ~/klipper/scripts/canbus_query.py can0` — confirm both
      UUIDs and whether Katapult is installed
- [ ] Confirm CAN bitrate matches `/etc/network/interfaces.d/can0` (docs default `1000000`)
- [ ] Capture each board's existing `~/klipper/.config` before rebuilding
- [ ] Flash in the order above, verifying after each

---

## Open items / backlog

- [ ] Repo is still not a complete backup — untracked machine files:
      `moonraker.conf` (**check for secrets first**), `KlipperScreen.conf`,
      `crowsnest.conf`, `timelapse.cfg`, `print_area_bed_mesh.cfg`, `sonar.conf`
- [ ] `[probe]` is split across three sections in two files — merges fine today,
      but this fragmentation is exactly how the Z-current bug happened
- [ ] `[safe_z_home] home_xy_position: 150,150` vs bed centre `175,175` — cosmetic
      for a virtual endstop, but inconsistent
- [ ] Extruder motor documented as `LDO-36STH20-1004AHG` in one file and
      `MOONS CSE14HRA1L410A` in another — which is actually installed?
- [ ] `mainsail.cfg` is managed by Moonraker's update manager and will be
      overwritten on update — expect churn now that it is tracked
- [ ] SSH key auth not set up; blocks reflash and any host-side work

---

## Reference

- [Print Tuning Guide](https://ellis3dp.com/Print-Tuning-Guide/)
- [Determining Motor Currents](https://ellis3dp.com/Print-Tuning-Guide/articles/determining_motor_currents.html)
- [Build Surface Adhesion](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/build_surface_adhesion.html)
- [First Layer Inconsistency](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/first_layer_squish_consistency_issues/first_layer_inconsistency.html)
- [Thermal Drift](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/first_layer_squish_consistency_issues/thermal_drift.html)
- [Voron V2 Gantry Squaring](https://ellis3dp.com/Print-Tuning-Guide/articles/voron_v2_gantry_squaring.html)
