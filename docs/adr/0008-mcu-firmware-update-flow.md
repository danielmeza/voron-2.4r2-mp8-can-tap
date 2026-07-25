# 0008 — MCU firmware updates run outside Moonraker, via an idempotent script

**Status:** Accepted · 2026-07-24

## Context

Updating Klipper through Moonraker's "update all" upgrades the **host** only. The
microcontrollers keep running whatever firmware they were last flashed with, and
Klipper then logs `deprecated_mcu_code` warnings — which is exactly how this printer
ended up with a v0.13 host driving three v0.12 MCUs.

The obvious wish is to hook flashing into "update all". **Moonraker cannot do this.**

Verified by reading `~/moonraker/moonraker/components/update_manager/` on the machine.
The options the updaters accept are:

```
channel, client_path, client_repo, env, install_script, moved_origin, origin,
path, pinned_commit, primary_branch, project_name, repo, requirements,
system_dependencies, venv_args, virtualenv
```

There is no post-update hook, no callback, and no notion of firmware. `install_script`
looks promising but is not it: it is a **deprecated** dependency-installer for the app
being updated (default `scripts/install-octopi.sh`), it only runs when *that* entry has
an update available, and it cannot be attached to the `klipper` entry to fire on a
Klipper update. `managed_services` only restarts systemd units.

Options considered:

| Approach | Verdict |
|---|---|
| Hook into Moonraker's update manager | **Not possible** — no such mechanism exists |
| Custom `[update_manager]` entry with `install_script` that flashes | Fires on *that repo's* updates, not Klipper's. Wrong trigger, and it would flash while Klipper runs |
| `gcode_shell_command` + a Mainsail macro button | Not installed here, and flashing requires stopping Klipper — a macro cannot stop the service it is running inside |
| External idempotent script, run after updates | **Chosen** |

## Decision

`scripts/Update-VoronMcuFirmware.ps1` — run it after `update all`.

The property that makes this workable instead of a chore is that it is
**version-aware and idempotent**: it queries each MCU through Moonraker, compares
against the host version, and flashes only genuine mismatches. When everything already
matches it exits 0 having changed nothing, so it is safe to run unconditionally after
every update. Verified against the real machine — a full non-dry run with both CAN
boards current made no changes and left Klipper `ready`.

Safety properties built in, all of them learned the hard way during the first manual
reflash (see `firmware/README.md`):

- **Refuses to run while printing or paused** (`print_stats.state`).
- **Board order is fixed** — Linux → EBB → **Manta last**, because the Manta is the
  USB-CAN bridge and reflashing it drops the bus including the EBB.
- **The Manta is flashed over USB, not CAN.** It cannot be flashed over the bus it
  provides: it is requested into Katapult over CAN, then addressed as a Katapult USB
  serial device (`1d50:6177`).
- **Refuses to flash a version mismatch** — the built firmware version is compared
  against the host before any write.
- **Verifies the probe after an EBB flash.** Under TAP the EBB carries the only Z
  endstop, so a bad flash there means `G28 Z` drives the toolhead into the bed.
- Stops/starts Klipper through Moonraker's service API, so no `sudo` is needed.
- `-DryRun` reports the plan without touching anything.

## Consequences

- Firmware updates stay a **deliberate, separate step**. Given that flashing the bridge
  board temporarily drops the CAN bus, having it fire automatically inside "update all"
  would arguably be worse than the manual step — a surprise mid-update bus drop is
  harder to reason about than an explicit command.
- The step must actually be remembered. Idempotency is what makes that cheap: run it
  every time, it usually does nothing.
- **The Linux MCU cannot be completed by the script** — installing to
  `/usr/local/bin/klipper_mcu` needs root and `sudo` requires a password on this host.
  The script builds and stages it and prints the three commands to finish. It is low
  priority: that MCU emits no warnings.
- Per-board build configs must be kept in `firmware/`, because Klipper's `.config`
  only ever holds the last board built.
- If Moonraker ever gains a genuine post-update hook, revisit this.
