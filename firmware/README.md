# MCU firmware build configs

Klipper's `~/klipper/.config` only ever holds the **last** board you built, so the
other boards' settings are lost the moment you build something else. These are the
three real, working configs captured from the machine — enough to rebuild any board's
firmware without guessing at `make menuconfig`.

Host Klipper is pinned to the same commit these were built from:
**`v0.13.0-827-g29d4ec2d6`** (since 2026-11-13; was `v0.13.0-708-g7046bd00e`). Firmware and host must match, or Klipper logs
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

## Katapult update — staged 2026-11-13, NOT applied

**Why:** the Manta's Katapult (v0.0.1-91, built with `CONFIG_INITIAL_PINS=""`) does not
drive PD14. Entering it drops `PS_ON`, and the relay cuts mains (ADR-0022, 2026-11-13).
The new Manta build is the *same config* with `INITIAL_PINS="PD14"`, on upstream Katapult
**v0.0.1-115-g3b973a4**. The EBB gets the same Katapult version for parity. Its config was
reconstructed, because no EBB Katapult config had been kept: RP2040, `boot2_w25q080` with
CLKDIV 2, CAN gpio4/gpio5 at 1 Mbit, app at `0x10004000`, double reset on, no LED.

| File | What |
|---|---|
| `katapult-manta-m8p-h723.config` | Manta Katapult (USB serial 1d50:6177, app 0x8020000, LED PC3, **INITIAL_PINS PD14**) |
| `katapult-ebb-sb2209-rp2040.config` | EBB Katapult (CAN 4/5 @ 1M, app 0x10004000) |

Staged on the CB1 in `~/fw-staging-2026-11-13/`, built from `~/katapult-2026` (upstream
clone; `~/katapult` is untouched) and `~/klipper` at v0.13.0-827-g29d4ec2d6:

```
d88ab631fd1dfb2c98b3e3d55aae73d54d397df3de393095438d137426962132  out-katapult-manta/deployer.bin
26fb792ee86c30b3148a98c221b21827a0ba657ffe4c7e90cf5cd3e7ff7fde4b  out-katapult-manta/katapult.bin
6dfeefa1ca5e6e4420c2abf6fee9040af0368069144b8095ac87b755b966477f  out-katapult-ebb/deployer.bin
492ed054c32235405176030efa06e79cd338967625fa4d49b4561bfeb7b7f7a7  out-katapult-ebb/katapult.bin
77fd931573150fbdbaa202f1d265af92529ffbbd429bd9c29081be6e9ef167b3  out-klipper-manta-m8p-h723/klipper.bin
0fdbb3e06a86cab45d597bdb03201cc275cb5f9e10d25d5a7fb17b9d61d5c694  out-klipper-ebb-sb2209-rp2040/klipper.bin
```

The deployer is flashed into the **application** area. It rewrites the bootloader and
leaves no Klipper behind, so each board's `klipper.bin` (827, staged above) goes back on
right after.

### Runbook (owner at the machine, PSU wired direct, relay out of the circuit)

Recovery if a bootloader write fails: the Manta has ROM DFU (BOOT0 + reset, `dfu-util`),
and the EBB has ROM BOOTSEL (BOOT button + USB-C). Neither board can be bricked.

```sh
FT=~/klippy-env/bin/python; K=~/katapult-2026/scripts/flashtool.py; S=~/fw-staging-2026-11-13
cd $S && sha256sum -c SHA256SUMS                      # all OK before anything
# stop Klipper (Moonraker service API, or as root: systemctl stop klipper)

# 1. EBB first -- it needs the bus the Manta provides
$FT $K -i can0 -u 19de38651b72 -r                     # into (old) Katapult
$FT $K -i can0 -u 19de38651b72 -f $S/out-katapult-ebb/deployer.bin
$FT $K -i can0 -q                                     # 19de38651b72 ... Application: Katapult
$FT $K -i can0 -u 19de38651b72 -f $S/out-klipper-ebb-sb2209-rp2040/klipper.bin
$FT $K -i can0 -q                                     # ... Application: Klipper

# 2. Manta LAST -- USB-CAN bridge
$FT $K -i can0 -u f24b112c272e -r
DEV=$(ls /dev/serial/by-id/usb-katapult_stm32h723xx_* | head -1)
$FT $K -d "$DEV" -f $S/out-katapult-manta/deployer.bin
sleep 3; DEV=$(ls /dev/serial/by-id/usb-katapult_stm32h723xx_* | head -1)   # new Katapult
$FT $K -d "$DEV" -f $S/out-klipper-manta-m8p-h723/klipper.bin
# start Klipper
```

Then: all three MCUs report `v0.13.0-827-g29d4ec2d6`, Klipper is `ready`, and
**`QUERY_PROBE` gives `probe: open`** (Rule 5: the EBB firmware changed).

**Prove the fix before trusting it.** With the PSU still direct, enter the Manta's Katapult
again (`-r`) and measure PS_ON at the relay's input with a meter: it must be high. Reflash
`klipper.bin` to leave it. Only then wire the PSU back through the relay. Power on, check
`PS_ON` = 1.0, and record the result in ADR-0022. Until then, keep `-RelayHeldByHand` the
only way the updater flashes the Manta.
