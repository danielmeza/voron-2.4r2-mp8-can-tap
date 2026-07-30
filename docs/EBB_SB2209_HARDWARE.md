# EBB SB2209 CAN (RP2040) — hardware notes

Extracted from `docs/EBB RP2040 CAN/BIGTREETECH EBB SB2209 CAN(RP2040) V1.0-SCH.pdf`
and the `-Pin.png` pinout. Written because the fan power architecture is not documented
on BTT's wiki and it constrains what fans can be fitted.

## SB0000 breakout header (J1, 2×7, 2.0 mm)

| Net | GPIO | Net | GPIO |
|---|---|---|---|
| `V_FAN` | — (power) | `4W_FAN_PWM` | GPIO15 |
| `V_4W_FAN` | — (power) | `4W_Speed` (tach) | GPIO12 |
| `5V` | — | `RGB` | GPIO16 |
| `CAN_P` | — | `FAN1_PWM` | GPIO14 |
| `GND` | — | `FAN2_PWM` | GPIO13 |
| | | `CAN_N`, `GND` | — |

Board-side aliases in `printer/mcu/btt_ebb_sb2209_rp2040.cfg`:
`HOTEND_FAN=gpio14`, `PARTS_FAN=gpio13`, `TACH=gpio12`, `PWM=gpio15`, `RGB=gpio16`.

## Fan power architecture — the constraint

Two **independent** voltage jumpers, labelled in the schematic as
*"Power for FAN1/FAN2"* and *"Power for 4 WAY FAN"*:

| Jumper | Feeds | Options |
|---|---|---|
| `V_FAN` | **FAN1 _and_ FAN2 together** | VIN / 12 V / 5 V |
| `V_4W_FAN` | 4-wire fan header only | VIN / 12 V / 5 V |

**FAN1 and FAN2 share one rail.** They cannot run at different voltages.

Onboard buck: **24 V in → 12 V @ 1.5 A**.

## Tach is only on the 4-wire header

`4W_Speed` → GPIO12 is part of the 4-wire fan circuit. **FAN1/FAN2 have no tach pins.**
A fan needs to be on the 4-wire connector to report RPM through the intended wiring.

## The 4-wire header does not switch power

`4W_FAN_PWM` (GPIO15) drives **U14, a 74LVC1G level shifter** powered from 5 V. Its
output `4W_FAN` is a **5 V logic signal** intended for a 4-wire fan's PWM input pin —
it is *not* a power switch.

Consequence: **`V_4W_FAN` is constant.** A **3-wire** fan on this header runs at 100 %
whenever the board is powered. Only a true 4-wire fan can be speed-controlled here.

## Fitting a 12 V hotend fan alongside a 24 V parts fan

The case this note exists for. Since `V_FAN` is shared, a 12 V and a 24 V fan cannot
both live on FAN1/FAN2.

| Option | `V_FAN` | `V_4W_FAN` | Outcome |
|---|---|---|---|
| **A** | VIN (24 V) — parts fan on FAN1/FAN2 | **12 V** — hotend fan | RPM works. Hotend fan **always on**. No parts needed. |
| B | 12 V — both fans | — | Full thermostatic control, but needs a **12 V parts fan**. |

**Do not use a series resistor to drop 24 V → 12 V.** Fan current swings with load and
inrush, so the dropped voltage swings with it and the fan may not start; and a 12 V
@ 0.15 A fan means dissipating ~1.8 W inside the toolhead. The board already has a
regulated 12 V rail — use it.

### Option A wiring

```
hotend fan +     -> V_4W_FAN   (jumper set to 12V)
hotend fan -     -> GND
hotend fan tach  -> 4W_Speed   (GPIO12)
parts fan        -> FAN1 or FAN2, V_FAN jumper on VIN
```

Klipper side — `pin` becomes vestigial (a 3-wire fan ignores the PWM signal), but the
tach reports real RPM:

```ini
[heater_fan hotend_fan]
pin: EBB:PWM
tachometer_pin: ^EBB:TACH
# tachometer_ppr: 2      # default; set if RPM reads 2x or 0.5x
```

Klipper will still report the fan as on/off per `heater_temp`, while the fan physically
runs continuously. Worth knowing when reading the UI.

## ⚠️ Before connecting any tach wire

GPIO12 is **3.3 V**. The fan's tach output must be **open-collector** (switches to
ground; the `^` pull-up in the config provides the high level). Most fans are.

A push-pull tach output at fan voltage will **destroy GPIO12**. Check the fan datasheet,
or spin the fan on bench power and measure tach-to-ground — it should read near 0 V, not
12 V or 24 V.

---

## Incident 2026-07-30 — EBB dropped off the bus while idle

**Root cause not established.** Recording the evidence so a recurrence can be compared
against it rather than re-diagnosed from scratch.

```
Timeout with MCU 'EBB' (eventtime=66984.778546)
Transition to shutdown state: Lost communication with MCU 'EBB'
```

State at the time, from `klippy.log` stats:

| Signal | Value | Reading |
|---|---|---|
| `canstat_EBB` | `bus_state=unknown  tx_retries=2334` | host retransmitting, nothing coming back |
| EBB `send_seq` / `receive_seq` | frozen at 2792 | node stopped responding |
| `canstat_MP8` | `bus_state=active  tx_retries=0` | bridge unaffected |
| EBB temp | 56.7 °C (frozen at last reading) | not thermal runaway |
| `print_time` | frozen at 65756 | **~1200 s of idle before the drop** |
| bed / extruder | target 0, cooling | nothing was moving or heating |

`ip -details link show can0` immediately after, **all zero**:

```
can state ERROR-ACTIVE  restart-ms 0
re-started bus-errors arbit-lost error-warn error-pass bus-off
0          0          0          0          0          0
RX/TX errors 0  dropped 0
```

**What this rules out.** A broken/marginal CAN wire, bad termination, or bus contention
would show up as `bus-errors`, `error-warn`, `error-pass` or `bus-off` counts. All were
zero, and the link stayed `ERROR-ACTIVE`. The bus was electrically healthy; the EBB node
simply stopped answering on it. That also distinguishes this from the earlier
bandwidth-related shutdown during resonance testing
([ADR-0012](adr/0012-slicer-integration-for-tuning.md) revisions), which was a
`Timer too close` on **MP8** under ADXL streaming load — a different board and a
different mechanism.

**What it does not tell us.** Whether the RP2040 hung, browned out, or reset. Nothing
was moving, so cable flex during motion is *not* the obvious explanation it would be
during a print.

**If it recurs, capture before restarting** — a power cycle destroys the evidence:

```bash
ip -details -statistics link show can0        # bus counters: still all zero?
sudo dmesg | grep -iE 'can0|gs_usb|bus-off'   # USB/CAN adapter level events
grep -n 'Timeout with MCU' ~/printer_data/logs/klippy.log
~/klippy-env/bin/python ~/klipper/scripts/canbus_query.py can0   # needs klipper stopped
```

Then check whether the EBB reappears on `FIRMWARE_RESTART` alone (node recovered) or
needs a power cycle (board was hung/dead).

**Outcome this time:** recovered after a host restart/power cycle — all three MCUs came
back at `v0.13.0-708-g7046bd00e` and `QUERY_PROBE` returned `probe: open`. So the board
was **hung, not damaged**. A `FIRMWARE_RESTART` alone was attempted first but the host
went down before it could be confirmed, so which of the two was sufficient is unknown —
try `FIRMWARE_RESTART` first next time, it is the cheaper test and distinguishes the two
cases.

**Not yet recurring.** One event. If it happens again, that changes it from an anomaly
to a pattern worth chasing (marginal toolhead CAN connector, 24 V supply sag, or an
RP2040 firmware hang are the candidates, in that order of likelihood on a Voron).

⚠️ **TAP is on this board and is the only Z endstop.** After any EBB recovery,
`QUERY_PROBE` before any `G28 Z` — see `CLAUDE.md` Rule 5.
