# MCU firmware build configs

Klipper's `~/klipper/.config` only ever holds the **last** board you built, so the
other boards' settings are lost the moment you build something else. These are the
three real, working configs captured from the machine — enough to rebuild any board's
firmware without guessing at `make menuconfig`.

Host Klipper is pinned to the same commit these were built from:
**`v0.13.0-708-g7046bd00e`**. Firmware and host must match, or Klipper logs
`deprecated_mcu_code` warnings.

| File | Board | Transport | App offset |
|---|---|---|---|
| `cb1-linux-mcu.config` | CB1 (host) | `/tmp/klipper_host_mcu` | n/a |
| `ebb-sb2209-rp2040.config` | EBB SB2209 RP2040, uuid `19de38651b72` | CAN, gpio4 RX / gpio5 TX | `0x10004000` (16 KiB Katapult) |
| `manta-m8p-h723.config` | Manta M8P v2.0 STM32H723, uuid `f24b112c272e` | **USB→CAN bridge** | `0x8020000` (128 KiB Katapult) |

CAN bitrate is **1000000** everywhere — `/etc/network/interfaces.d/can0`, all three
configs, and Katapult. Changing it means changing all of them together.

## Automated: run this after every Moonraker "update all"

```powershell
pwsh ./scripts/Update-VoronMcuFirmware.ps1            # ebb + manta
pwsh ./scripts/Update-VoronMcuFirmware.ps1 -DryRun    # show the plan only
pwsh ./scripts/Update-VoronMcuFirmware.ps1 -Boards all
```

It compares each MCU against the host and flashes **only** what mismatches, so running
it when everything is current is a no-op. Moonraker has no hook to do this itself —
see [ADR-0008](../docs/adr/0008-mcu-firmware-update-flow.md). The manual steps below
are the fallback and the explanation of what the script does.

## Rebuilding

```bash
cd ~/klipper
cp <this-repo>/firmware/<board>.config .config
make olddefconfig && make clean && make -j4
```

## Flashing

**Order matters: Linux MCU → EBB → Manta.** The Manta is the USB-CAN bridge, so
flashing it drops the whole bus including the EBB. Do it last.

```bash
# EBB -- over CAN via Katapult
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u 19de38651b72 -r
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u 19de38651b72 -f ~/klipper/out/klipper.bin

# Manta -- request over CAN, then flash over USB. Katapult here is built USBSERIAL
# (1d50:6177), so once it reboots it appears as /dev/serial/by-id/usb-katapult_*
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u f24b112c272e -r
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -d /dev/serial/by-id/usb-katapult_stm32h723xx_* -f ~/klipper/out/klipper.bin

# Linux MCU -- needs root
sudo systemctl stop klipper
sudo cp ~/klipper/out/klipper.elf /usr/local/bin/klipper_mcu
sudo systemctl restart klipper-mcu && sudo systemctl start klipper
```

Stop Klipper first (`curl -X POST http://<printer>/machine/services/stop?service=klipper`
works without sudo).

## Why this is safer than it looks

Katapult sits at `0x8000000` (Manta) / `0x10000000` (EBB) and the app is written at the
offsets above — **Katapult is never overwritten**. A failed app flash leaves the
bootloader intact and reachable, so you retry rather than recover. The Manta's SD-card
`firmware.bin` path is the fallback only if Katapult itself is damaged.

## Gotchas

- `canbus_query.py` and `flashtool.py -q` only list **uninitialised** nodes. Stopping
  Klipper is *not* enough — the MCUs keep their assigned CAN IDs until reset, so both
  report nothing. Use `flashtool.py -u <uuid> -r` to put a node into Katapult.
- Setting `CONFIG_MACH_RP2040=y` alone does nothing; the parent `CONFIG_MACH_RPXXXX=y`
  must be set too or `make olddefconfig` silently falls back to AVR and builds a `.hex`.
- RP2040 symbols are `RPXXXX_*`, not `RP2040_*`.
- **TAP:** the EBB carries the only Z endstop (`probe:z_virtual_endstop`). After
  flashing it, verify with `QUERY_PROBE` before any `G28 Z`.
