# 0028 — The SFS encoder doubles as a flow meter

**Status:** Proposed · 2026-11-12. This is a design card awaiting review: no code yet,
and nothing installed.

## Context

The SFS V2.0 encoder (`filament_motion_sensor sfs_motion`, `MP8:PF1`, ADR-0024) only
answers "has the filament stopped?". On 2026-11-12 it caught a real jam 22 s into a
first layer. That is a full stop, the last stage of a failure. Slip and grinding come
earlier: a drive gear chewing filament, a spool dragging, a part clog. They show up as
*less* filament moving than the extruder commands, long before none at all. The encoder
already sees every millimetre that passes the spool. Today nothing counts it.

Owner's call (2026-11-12): use it as a flow meter.

## What it must do

- Count encoder edges and know the commanded extruder position at each edge.
- Report, per print and per rolling window, *measured mm ÷ commanded mm*. This is the
  flow ratio at the spool, not the volume at the nozzle.
- Put that number on the `TUNE end` line (ADR-0026), and in Klipper status, so a macro
  can read it.
- Keep runout and jam detection exactly as they are.
- A *warning* at a threshold comes later, after real data says what normal scatter is.
  No automatic pause in the first version.

## Owner of each datum

| Datum | Owner | Notes |
|---|---|---|
| encoder edges (count, eventtime) | the sensor object, inside its edge callback | one source of truth; nothing else touches the pin |
| commanded E at an edge | `extruder.find_past_position(print_time)`, already used by `EncoderSensor` | the true position at that instant, no look-ahead error |
| mm per edge | config `mm_per_edge`, measured once (below) | the "2.88 mm" detection accuracy is not assumed to be the pitch |
| window ratio, print ratio | the sensor object's status | read by `_TUNE_STAMP_END` |

## Alternatives, judged by the same criteria

Criteria: accuracy, per-edge cost, how much it leans on Klipper internals, install
footprint, how easy it is to undo.

1. **Subclass `EncoderSensor` (plain polymorphism) — recommended.** A small extras
   module, `sfs_flow_sensor.py`, defines `FlowEncoderSensor(EncoderSensor)`. It
   overrides `encoder_event` to record `(count, extruder_pos)`, then calls `super()`, so
   runout and jam behaviour are inherited unchanged. It extends `get_status` with
   `edges`, `measured_mm`, `commanded_mm`, `ratio` and `window_ratio`. The section
   becomes `[sfs_flow_sensor sfs_motion]` with the same keys plus `mm_per_edge`.
   - *Accuracy:* the best. It compares at the edge with the extruder's past position, so
     there is no look-ahead error.
   - *Cost:* one Python call and one `find_past_position` per edge, which is ~0.5–2 Hz
     at print flows. Negligible.
   - *Leans on:* `EncoderSensor`'s public shape (`encoder_event`,
     `_get_extruder_pos`), not on button internals.
   - *Footprint:* one file outside `printer_data/config`. It lives in the repo under
     `system/klipper_extras/`, symlinked into `~/klipper/klippy/extras/` and listed in
     `.git/info/exclude` so Moonraker does not call Klipper's repo dirty.
   - *Undo:* rename the section back.
   - *Cost to the UI:* Mainsail's filament-sensor toggle lists only the stock prefixes,
     so this sensor leaves that panel. The macros already arm and disarm it
     (`SET_FILAMENT_SENSOR SENSOR=sfs_motion` keeps working, because the runout helper
     registers by name).
2. **`gcode_button` on the same pin, behind `[duplicate_pin_override]`, with macros
   counting edges.** It needs no Python. But `duplicate_pin_override` is documented for
   diagnostics, every edge runs gcode through the gcode mutex during a print, and the
   commanded E comes from the planned `toolhead.position`, which runs up to the
   look-ahead ahead. That makes it accurate only over long windows.
3. **Hook the button callback list.** Wrap the stored callback inside
   `buttons.MCU_buttons`. It needs no section change, but it patches private
   structures. That is the most fragile option across a Klipper update, and an update
   is coming.
4. **Use a fan tachometer as a frequency counter** (`fan_generic` with
   `tachometer_pin` on the shared pin). It needs a dummy PWM output and gives a
   frequency per ~1 s window. At 0.5–2 Hz that is 0 or 1 edge per window, too coarse.

~~Option 1 wins on accuracy and cost, and it is the plainest design: inheritance, not
patching. Option 2 is the fallback if installing a Python module is unwanted.~~
*Revised after the source check below: option 2 is now recommended.*

## Source check (2026-11-12)

The card was checked against Klipper at the pinned 7046bd00 and at master 29d4ec2d
(119 commits later). The relevant files are identical in both: filament_motion_sensor,
filament_switch_sensor, buttons, duplicate_pin_override, extruder, configfile and
src/buttons.c. Moonraker and Mainsail master were checked too. Klipper runs Python
3.11.2.

**What was wrong or missing in the card:**
- **Option 1's `get_status`.** `EncoderSensor.__init__` assigns
  `self.get_status = runout_helper.get_status` on the instance, which hides any method
  a subclass defines. The subclass would have to reassign it after `super().__init__`.
- **Klipper's own `-dirty` mark.** `util.py:209` runs `git status --porcelain
  --ignored`, and `klippy.py:311-321` appends `-dirty` for any untracked or ignored
  `.py` file in `klippy/extras`, whether or not it is excluded.
  - The host version would read `v0.13.0-708-g7046bd00e-dirty`. That breaks this
    repo's "all pinned to one version" fact, and every MCU firmware built from that tree
    (ADR-0008) would carry the mark too.
  - `.git/info/exclude` only hides the file from Moonraker. There it would be a warning,
    "Repo has untracked source files", never "dirty".
- **A Moonraker hard recovery re-clones Klipper** (`git_deploy.py:926-956`). That loses
  the symlink, and Klipper then refuses to start: `[sfs_flow_sensor …]` "is not a valid
  config section". A config that depends on a file outside the repo turns a routine
  recovery into a printer that does not boot.
- **Option 2's commanded position was misjudged.** `motion_report.live_position` gives
  the *executed* E, refreshed every 250 ms. Over a window of ≥ 50 edges that error is
  ~2 % or less.
- **The edge rate is higher than the card said.** The callback fires on both edges, so
  6 mm/s of filament at 2.88 mm per cycle is ~4 Hz, not 0.5–2 Hz. The cost is still
  negligible.
- **The encoder has no direction, and commanded E is signed.** Retract and unretract
  pairs and wipes can add edges with no net travel, and PTFE slack blurs short moves.
  So: compare against the *net* commanded advance over the window, require a minimum
  commanded distance (report `n/a` below it), and measure the bias on real prints.
- **Counting must not depend on the sensor being armed.** `SFS_DISABLE_AT_START`
  disarms it at boot, and the `mm_per_edge` measurement happens while it is disarmed.
- **The red test needs more stubs.** `EncoderSensor` builds a RunoutHelper, which needs
  `gcode`, `pause_resume`, `gcode_macro` and `idle_timeout`, and runout fires only from
  the 0.25 s timer.

## Recommendation (revised)

**Option 2: config only.** Use a `gcode_button` on `MP8:PF1` behind
`[duplicate_pin_override]`, and count both edges in a macro variable. A
`delayed_gcode` closes a window every N edges or T seconds, against the net advance of
`motion_report.live_position` E. It reports `n/a` below a minimum commanded distance,
and its result feeds the `TUNE end` line.
- *Why it fits this machine better:* nothing outside `printer_data/config`. No
  `-dirty` mark. It survives Klipper updates and a Moonraker recovery. The stock
  `[filament_motion_sensor]` stays as it is for runout, and Mainsail keeps showing it.
  Undo is deleting two sections.
- *What it costs:*
  - `duplicate_pin_override` is a diagnostic feature;
  - the polarity check is skipped on PF1, and the MCU gets a second `buttons_add` for
    the same pin, so this must be verified on the machine first;
  - each edge runs a tiny macro (~4 Hz), which waits behind long-running commands but
    is not lost.

Option 1 stays as the precise alternative, should a ~2 % window error ever matter. It
would need the fixes above, plus an install check that restores the symlink.

**Before any code:**
1. Measure `mm_per_edge`.
2. Confirm on the machine that the second pin registration works and that both edges
   arrive.
3. Choose the window: at least 50 edges.

## Seams

- **In:** the encoder pin (unchanged), the extruder object, and `mm_per_edge` from
  config.
- **Out:** Klipper status `sfs_flow_sensor sfs_motion.{edges, measured_mm,
  commanded_mm, ratio, window_ratio}`. `_TUNE_STAMP` resets the print counters
  through a `SET_FLOW_METER RESET=1` command the module registers. `_TUNE_STAMP_END`
  appends `flow <ratio>%`.
- **Not touched:** runout and jam decisions, `detection_length`, `PAUSE`.

## First red test

A plain-Python unit test, `tests/test_sfs_flow_sensor.py`, with a stub extruder whose
`find_past_position` is driven by a list. Feed 100 edges while the stub advances
`100 × mm_per_edge / 0.9`, as when the drive gear slips 10 %. The expected `ratio` is
90 %. With equal motion it must be 100 %. A third case: no edges while the commanded position advances 20 mm gives 0 %,
and the inherited runout logic still fires (asserted through the stub runout helper).

## Measurements needed before it is trusted

1. **`mm_per_edge`:** with the hotend hot and filament loaded, extrude 200 mm at
   5 mm/s, three times, and read `edges`. `mm_per_edge` = 200 / mean edges.
2. **Normal scatter:** ten real prints of `window_ratio`. A warning threshold is set
   only from that spread.

## Per-tick cost

Nothing per reactor tick. Per encoder edge: one dictionary update, one
`find_past_position`, and two float adds. The existing 0.25 s runout timer is
unchanged.

## Open questions for the review

- Does `EncoderSensor` survive subclassing cleanly at the pinned Klipper version, i.e.
  is `load_config_prefix` the only factory, and does the button registration in
  `__init__` take the overridden method? It should, because `self.encoder_event`
  resolves through the subclass.
- Does Moonraker's update manager ignore an untracked file listed in
  `.git/info/exclude`?
- Should `ratio` be measured/commanded or commanded/measured in the TUNE line? This
  card says measured/commanded: under 100 % means less filament moved than asked.
