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
