# 0026 — Every print stamps its effective tuning in one console line

**Status:** Accepted · 2026-11-12

## Context

On 2026-11-12 three A/B prints (PA, flow, shaper) had to be reconstructed after the
fact, from the gcode files, the Moonraker gcode store and klippy.log. Nothing recorded
the values each print ran with. Some were set by the slicer (`M900` from Cura's Linear
Advance plugin), some by hand (`M221`, `SET_INPUT_SHAPER`), so the file alone did not
say what the printer did.

## Decision

`_TUNE_STAMP`, called last in `PRINT_START`, prints one `// TUNE …` line: the file,
PA and smooth time, flow and speed factor, `z_offset` plus babystep, the shaper from
config, bed, hotend and chamber temperatures.

`_TUNE_STAMP_END`, called first in `PRINT_END`, prints a second line only if PA, flow
or the babystep changed during the print. It stores nothing new: it compares against the
values `_TUNE_STAMP` saved in its own variables.

The owner asked that it take little space: one line per print, no file, no Moonraker
database entry.

Rejected:

- Writing a CSV through a shell-command extension. It adds a dependency and a file that
  grows forever.
- Printing every value again at the end too. That doubles the lines for no information,
  because a print that changes nothing needs no second line.

## Consequences

- The line lives in the console, the Moonraker gcode store (which keeps only recent
  lines) and klippy.log (which rotates). It is a trace, not an archive. When a print is
  an experiment, copy its `TUNE` line into `docs/TUNING_PLAN.md`.
- The shaper is reported from config only. Klipper does not report a live
  `SET_INPUT_SHAPER`, so a shaper changed by hand during a session shows its config value.
- A cancelled print gets no end line, because `PRINT_END` does not run.

*Verified 2026-11-12, idle, after sync and `RESTART`:* `_TUNE_STAMP` printed
`// TUNE  | pa 0.0220/0.030 flow 100.0% speed 100% | z -0.559+0.000 | shaper(cfg) x 2hump_ei@78.6 y mzv@35.8 | bed 0 hot 0 chamber 36.6`
(no file name while idle). `_TUNE_STAMP_END` printed nothing, as expected with nothing
changed. The first real print will show the line with its file name.

## Revisions

### 2026-11-12 — phase times on the TUNE line

The line now ends with `start <total>s: home N preqgl N bed N clean N qgl N soak N mesh N heat N purge N`. `_PHASE_RESET` starts the timer at "Starting print", `_PHASE NAME=...` records each phase as it ends, and `purge` is the time from the last phase (heat) to the `_TUNE_STAMP` call.

The owner wants the long start kept: the gantry bends with chamber heat and has no titanium backers yet, so QGL and the mesh must wait for its final shape ([ADR-0003](0003-temperature-gated-soak.md)). The phase times are there to see where the time goes, not to shorten the soak.
