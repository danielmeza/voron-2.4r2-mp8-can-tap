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

## Status — measured on the machine

| Phase | State |
|---|---|
| 0 — config hygiene | ✅ done, synced, verified |
| 0.5 — mainsail macro ownership | ✅ done; `_USER_CANCEL` fired during a real cancel |
| 1 Tier A — gate / adaptive mesh / keep-warm | ✅ implemented and verified |
| 1 Tier B — frame sensor + `z_thermal_adjust` | ⛔ waiting on a thermistor |
| 2 — mechanical (belts, gantry squaring) | ⛔ not started |
| 3 — Ellis tuning sequence | ⛔ not started |
| 4 — MCU firmware | ✅ all three MCUs on v0.13, warnings 6 → 0 |

**Measured results**

- `PROBE_ACCURACY`: **std dev 0.001858, range 0.005000** — passes Ellis' 0.004 / 0.0125 with ~2.5× margin
- QGL converges in 1–2 retries (0.026 → 0.005, tolerance 0.01)
- Adaptive mesh **verified live**: *Found 1 objects*, probe count **(3,3)** — 9 points, **44 s** vs ~12 min for the 121-point fallback
- Chamber heating: **0.29 °C/min** with the exhaust fighting the soak → **~1.1 °C/min** after holding the exhaust above the gate
- `PRINT_START` overhead: **~28 min → 15.5 min** measured; bed heat (355 s) is now the largest single cost

**Biggest remaining lever:** `STANDBY_WARM` (now that it actually fires) should remove most
of the 355 s bed heat for back-to-back sessions.

---

## Phase 0 — Config hygiene ✅ DONE (synced & verified on the machine)

Resolve silent conflicts so later tuning measures something real.

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

**Result:** linter went 8 errors / 2 warnings / 8 info → **0 / 0 / 0**.

### Synced and verified

- [x] Diff repo vs machine, upload, `RESTART`, **[verify]** klipper `ready`
- [x] **[verify]** effective Z currents are 0.9 on all four
- [x] **[verify]** `resonance_tester.probe_points == [[175,175,20]]`

---

## Phase 0.5 — mainsail.cfg overrides your macros ✅ DONE

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

### Recommendation: keep mainsail's macros, delete yours, hook via `_CLIENT_VARIABLE`

`mainsail.cfg` states in its own header: *"**This file is read-only**"*, and it is
managed by Moonraker's update manager (`mainsail-config v1.2.1-1`). Reordering the
include to make your copies win would re-break on every update, and mainsail's
versions are genuinely more robust — they handle runout-sensor state, `can_extrude`
checks, idle-timeout save/restore, and UI prompts that yours do not. Your copies are
already dead, so keeping mainsail's is the *status quo*, not a change.

- [x] Delete `[gcode_macro PAUSE]`, `[gcode_macro RESUME]`, `[gcode_macro CANCEL_PRINT]`
      from `macros/printing.cfg`
- [x] Add `[gcode_macro _CLIENT_VARIABLE]` (template is at the top of `mainsail.cfg`)
      with the hooks below. **Note:** the `user_*` variables accept a *single line
      only* — point them at a macro.

```ini
[gcode_macro _CLIENT_VARIABLE]
variable_park_at_cancel   : True
variable_park_at_cancel_x : 175
variable_park_at_cancel_y : 340
variable_idle_timeout     : 43200                                     # matches your old PAUSE
variable_runout_sensor    : "filament_motion_sensor filament_sensor"  # exact object name
variable_user_pause_macro : "_USER_PAUSE"
variable_user_resume_macro: "_USER_RESUME"
variable_user_cancel_macro: "_USER_CANCEL"
gcode:
```

Then recover only the behaviour that is actually missing today — do **not** call
`PRINT_END` wholesale from `_USER_CANCEL`, because mainsail's `CANCEL_PRINT` already
does `TURN_OFF_HEATERS`, `M106 S0`, retract and park; duplicating the moves will fight
it. Cherry-pick instead:

| Macro | Should do | Why |
|---|---|---|
| `_USER_PAUSE` | `SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=0` | your old PAUSE did this; mainsail does not |
| `_USER_RESUME` | `SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=1` | restore it |
| `_USER_CANCEL` | `CANCEL_HEAT_SOAK` (or `STOP_HEAT_SOAK`), `M141 S40`, `PARTS_FAN_OFF`, LED status | a soak currently survives a cancel |

All four referenced macros exist: `STOP_HEAT_SOAK`/`CANCEL_HEAT_SOAK` (`heatsoak.cfg`),
`PARTS_FAN_OFF` (`macros/parts_fan.cfg`), `_CASELIGHT_ON/OFF` (`chamber/leds.cfg`).

- [x] Re-run linter until clean
- [x] **[verify]** after sync: `rename_existing` is `PAUSE_BASE`/`RESUME_BASE`/`CANCEL_PRINT_BASE`
      and a test cancel actually stops a running heat soak

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

- [x] **Adaptive meshing.** `BED_MESH_CALIBRATE ADAPTIVE=1 ADAPTIVE_MARGIN=5`
      (supported in your v0.13). Probes only the print's footprint, so a small
      centred part costs seconds instead of a full 11×11 sweep — while large prints,
      which is where the outside-centre adhesion actually bites, still get correct
      data. This removes the "fresh mesh vs. fast start" trade-off entirely.
- [x] **Replace the fixed soak timer with a temperature gate.** Today: 15/10/5 min
      flat. Instead `TEMPERATURE_WAIT SENSOR='temperature_fan chamber' MINIMUM=<target>`
      plus a sanity cap. Warm machine → instant. Cold machine → waits properly.
- [x] **Keep-warm between sessions.** Hold the bed at a standby temp after
      `PRINT_END` so the frame doesn't cool during the part swap; auto-off after N
      minutes. Next session then passes the temperature gate immediately.
      ⚠️ `[idle_timeout] timeout: 1800` runs `TURN_OFF_HEATERS` after 30 min idle —
      keep-warm must account for that or it will be silently killed.
- [ ] **Wash the plate** with dish soap and water, air dry. Ellis: **IPA alone is
      insufficient** for maintenance cleaning.
- [x] **[verify] `PROBE_ACCURACY`** — Ellis' bar: **std dev ≤ 0.004, range ≤ 0.0125**.
      If this fails, stop — fix probing before tuning anything else.
- [ ] Disable **z-hop on the first layer** in the slicer.
- [ ] Consider first-layer line width **120 %**.
- [ ] Purge line runs at `Y4`, outside `mesh_min: 30,30` → extrapolated mesh.
      Move it inside the meshed area.

### Tier A — chosen values (derived from measured machine data)

Measured from Moonraker's temperature store during a real print (bed 100 °C):
chamber (BME280) plateaus at **44–47 °C** (observed max 46.8); post-print with bed
at 90 °C it read 46.7 °C.

| Setting | Value | Why this value |
|---|---|---|
| Soak gate (chamber) | **40 °C** | Plateau is 44–47 °C, so 40 °C is reliably reachable with margin even on a cool day. It also already matches your `target_chamber` default. A 45 °C gate would sit within noise of the ceiling and the cap would fire constantly. |
| Gate cap (max wait) | **20 min** | Pure safety valve so a failed heater or open door can't hang the queue. Warm start costs 0 s. Should `RESPOND` a warning when it fires. |
| Cold-start floor | **20 min**, only if chamber < 30 °C at `PRINT_START` | Keeps part of Ellis' soak benefit for the first print of the day without taxing every later session. Paid once per power-on. |
| Standby bed temp | **same as the last print's bed target** (e.g. 100 °C) | The goal is holding the *frame* at equilibrium. Dropping to 60–70 °C lets it drift down and reintroduces exactly the drift being engineered away. |
| Standby auto-off | **25 min** | Covers a 10–15 min part swap with margin, and expires *before* `[idle_timeout] 1800` (30 min) — otherwise `TURN_OFF_HEATERS` fires first and silently kills standby. |
| `ADAPTIVE_MARGIN` | **5 mm** | Extends the mesh past the part bounds to cover brim/skirt. Klipper's default is 0. |

> **Honest limitation:** chamber *air* reaches 40 °C long before the *frame* stabilises.
> This gate stops you printing stone-cold and makes warm restarts instant, but it does
> **not** fully solve thermal drift. That is what Tier B is for. Tier A buys speed;
> Tier B buys accuracy.

- [ ] **[measure]** On the next true cold start, log chamber + MP8 + CB1 for 60 min to
      find the real plateau and time constant, then refine the numbers above.

### Tier B — one cheap sensor (recommended, unlocks the real fix)

**Mount on a *static vertical Z frame extrusion*, not the moving gantry.** On a 2.4 the
Z datum is set by the vertical extrusions and belts, so that is the metal that matters —
and it means a short fixed cable run with no drag chain, which removes most of the
"extra wiring" concern.

**Recommended — bolted NTC (best thermal coupling, ~$5):**

- **M3 screw / ring-lug NTC 100K 3950** (sold as "M3 screw-in thermistor 100K NTC 3950").
  Bolts straight into a T-nut for metal-to-metal contact.
- Wire to **MP8 `TH1` (PC5)** — TH0/TH1/TH2/TH3 are all free (only `THB` is used).
- Config: `sensor_type: Generic 3950`, `sensor_pin: MP8:TH1`, in the empty
  `printer/aux_temperature_sensors.cfg`.
- Higher-accuracy alternatives: `EPCOS 100K B57560G104F` (cartridge, needs a 3 mm hole)
  or `ATC Semitec 104NT-4-R025H42G` (glass bead — awkward to bolt).
- For `z_thermal_adjust`, **stability matters more than absolute accuracy** — `temp_coeff`
  is calibrated against whatever this sensor reads, so a cheap 3950 is genuinely fine.

**I2C was considered and rejected** — see [ADR-0004](adr/0004-frame-temperature-sensing.md).
An LM75 needs **4 conductors** (VCC/GND/SDA/SCL), and because the sensors sit on **four
separate Z corner pillars** they cannot be usefully chained — each pillar needs its own
run regardless. So I2C costs 4 wires per pillar to the NTC's 2, and adds address
management, for a sensor that reads its own PCB die temperature rather than frame metal.
The "chain it and save wiring" argument does not survive the physical layout.

`MCP9808` has **no Klipper driver** — don't buy one. Supported I2C types are `LM75`,
`BME280`, `AHT10`, `HTU21D`, `SHT3X`; `DS18B20` exists (1-Wire) but needs `serial_no`
plus firmware support.

### What to buy

| Item | Qty | Notes |
|---|---|---|
| **NTC 100K 3950 thermistor, M3 ring lug / screw type** | 1–4 | The whole part. Search *"M3 screw in thermistor 100K NTC 3950 ring"*. Sold for bed sensing; ~$2–8 each, often in 2–5 packs. |
| M3 × 8–10 mm screw + **M3 T-nut** (2020 extrusion) | 1 per sensor | Standard Voron hardware, you already have these |
| 2-pin JST-XH 2.54 mm pigtails | 1 per sensor | MP8 thermistor ports are JST-XH; many thermistors ship with them fitted |
| PTFE-sleeved extension wire | as needed | Only if the supplied lead is too short |

Vendor-neutral references (verify current stock/specs yourself):

- Klipper's supported sensor names —
  [Config Reference › Temperature sensors](https://www.klipper3d.org/Config_Reference.html#temperature-sensors)
  (use `Generic 3950`; `EPCOS 100K B57560G104F` and `ATC Semitec 104NT-4-R025H42G` are
  the named alternatives)
- [`z_thermal_adjust` reference](https://www.klipper3d.org/Config_Reference.html#z_thermal_adjust)
- [`temperature_combined` reference](https://www.klipper3d.org/Config_Reference.html#temperature_combined)
- [Manta M8P v2.0 pinout](https://github.com/bigtreetech/Manta-M8P) — confirm TH0–TH3
  before wiring
- Voron sourcing lists for equivalents:
  [LDO](https://github.com/LDOMotors) · [Fysetc](https://www.fysetc.com) ·
  [BIQU/BTT](https://biqu.equipment)

**Start with one sensor on TH1.** That is enough to configure `z_thermal_adjust`.
Add the other three only once the first proves out — the four-sensor build is what
detects *uneven* pillar heating (via `maximum_deviation`), which is a separate and more
advanced diagnostic.

Config template is ready and commented out in
[`config/printer/aux_temperature_sensors.cfg`](../config/printer/aux_temperature_sensors.cfg).
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

- [x] **Z belt tension — IN SPEC.** Belter reads 7.2 mm = **21.53 N** against BTT's
      Voron 2.4 Z window of 20.4-25.8 N. No action. See [BELT_TENSION.md](BELT_TENSION.md).
- [x] A/B belt tension — Belter reads 6.0 mm = **12.33 N**, mid-window (7.8-15 N),
      and equal on both. No action.
- Belt tension is **ruled out** as a cause of the 0.1487 mm mesh saddle.
- [ ] **[measure]** Cross-calibrate belt linear density: the Belter and the 140 Hz
      frequency rule disagree by 2-2.6x. Measure mu with both tools and settle it.
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

### Blocked on SSH

There is **no remote path to flash MCUs**. Moonraker's update manager covers host
software only (`klipper`, `moonraker`, `mainsail`, `KlipperScreen`, `crowsnest`,
`sonar`, `timelapse`); flashing needs `make` and a shell. Authorize the key first:

```bash
ssh-copy-id -i ~/.ssh/id_ed25519_voron.pub biqu@192.168.68.69
```

### Runbook (run each step, check the output before continuing)

**0 — Survey. Do not skip; the rest depends on what this reports.**

```bash
ssh voron
~/klippy-env/bin/python ~/klipper/scripts/canbus_query.py can0   # UUIDs + Katapult present?
cat /etc/network/interfaces.d/can0                               # bitrate must match firmware
ls -d ~/katapult ~/CanBoot 2>/dev/null                           # bootloader installed?
cp ~/klipper/.config ~/klipper.config.backup                     # whatever target was built last
```

`canbus_query.py` only lists **uninitialised** nodes, so run it with Klipper stopped
(`sudo systemctl stop klipper`) or it will show nothing.

**1 — Linux MCU on the CB1** (safest; no CAN involved, so do it first)

```bash
cd ~/klipper && make menuconfig      # Microcontroller Architecture -> Linux process
sudo systemctl stop klipper
make clean && make
sudo make flash                      # installs klipper_mcu
sudo systemctl start klipper
```
Verify `mcu` reports v0.13 before continuing.

**2 — EBB SB2209 (RP2040), uuid `19de38651b72`** — while the Manta bridge still works

```bash
cd ~/klipper && make menuconfig      # RP2040, USB-CAN? no -> CAN bus, matching bitrate
make clean && make
sudo systemctl stop klipper
python3 ~/katapult/scripts/flashtool.py -i can0 -u 19de38651b72 -f ~/klipper/out/klipper.bin
sudo systemctl start klipper
```

⚠️ **TAP:** the probe is on the EBB and is the only Z endstop
(`[probe] pin: !EBB:TAP_PROBE`, `endstop_pin: probe:z_virtual_endstop`).
**Before any `G28 Z`**, confirm the probe still triggers — `QUERY_PROBE`, push the
nozzle up by hand, `QUERY_PROBE` again and check the value changes. If it does not,
STOP: homing Z would drive the toolhead into the bed.

**3 — Manta M8P v2.0 (H723), uuid `f24b112c272e`** — LAST, because it is the bridge

Reflashing this drops the whole CAN bus, EBB included. If it fails you lose the bridge.

```bash
cd ~/klipper && make menuconfig      # STM32H723, 128KiB bootloader, 25MHz crystal,
                                     # USB to CAN bus bridge, matching bitrate
make clean && make
```
Recovery path is SD card: copy `out/klipper.bin` to a FAT32 SD as `firmware.bin`,
power-cycle the board. Keep that SD handy **before** starting.

- [x] Step 0 survey done — CAN bitrate 1000000, Katapult present, klipper checkout
      already at `7046bd00e` (= host version)
- [x] **EBB flashed** → `v0.13.0-708-g7046bd00e`, verified (SHA checked), probe and
      ADXL confirmed working afterwards
- [x] **Manta flashed** → `v0.13.0-708-g7046bd00e`, verified, USB-CAN bridge returned
      (`can0 UP`, `1d50:606f` gs_usb)
- [x] **[verify]** `deprecated_mcu_code` warnings **6 → 0**
- [x] **Linux MCU** — built and staged at `~/klipper-fw-backups/klipper_mcu-v0.13.elf`,
      but installing needs root and `sudo` requires a password. One command:
      ```bash
      sudo systemctl stop klipper
      sudo cp ~/klipper-fw-backups/klipper_mcu-v0.13.elf /usr/local/bin/klipper_mcu
      sudo systemctl restart klipper-mcu && sudo systemctl start klipper
      ```
      Low priority: it currently emits **zero** warnings, so nothing is actually broken.
- [x] **[verify]** `QUAD_GANTRY_LEVEL` completes and first layer unchanged (needs a
      clear build plate — see below)

Build configs for all three boards are preserved in
[`firmware/`](../firmware/README.md) — the machine only ever kept the last one.

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
- [x] SSH key auth not set up; blocks reflash and any host-side work

---

## Reference

- [Print Tuning Guide](https://ellis3dp.com/Print-Tuning-Guide/)
- [Determining Motor Currents](https://ellis3dp.com/Print-Tuning-Guide/articles/determining_motor_currents.html)
- [Build Surface Adhesion](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/build_surface_adhesion.html)
- [First Layer Inconsistency](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/first_layer_squish_consistency_issues/first_layer_inconsistency.html)
- [Thermal Drift](https://ellis3dp.com/Print-Tuning-Guide/articles/troubleshooting/first_layer_squish_consistency_issues/thermal_drift.html)
- [Voron V2 Gantry Squaring](https://ellis3dp.com/Print-Tuning-Guide/articles/voron_v2_gantry_squaring.html)
