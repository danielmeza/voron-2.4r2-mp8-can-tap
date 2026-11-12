# 0020 — Cool the toolhead board instead of lowering the chamber

**Status:** Accepted · 2026-09-20 · fan fitted and config live 2026-09-22

## Context

The 8-hour ABS print of 2026-09-19 ("Orcaslicer project_ABS_7h59m") died at 00:31 after
2 h 17 min of printing with `MCU 'EBB' shutdown: Timer too close`. The user reports the
same shutdown on several earlier long prints (those logs have rotated out). What the
log of this one shows:

| Signal | Whole print | Last ~2 min |
|---|---|---|
| EBB `rx_error` | 0 | 0 → 4483 → **11 049** |
| EBB `tx_retries` | ~65/s, constant | unchanged |
| host → EBB `bytes_retransmit` | 0 | 729 → 1061 |
| MP8 (bridge) errors | 0 | 0 |
| host `can0` bus-errors / error-warn / bus-off (after) | 0 / 0 / 0 | 0 / 0 / 0 |
| bridge message | | `USB CANBUS bridge 'MP8' is discarding!` |
| host load / free memory | 0.36 / 550 MB | same |
| **EBB board thermistor** | climbing to **78.5 °C** | 77.8 °C |
| chamber (BME280) | 42 → 46.6 °C | 46.6 °C |

Two things fall out of that table. The steady 65 retries/s are *normal* for this bus
under motion traffic: the healthy cube print of 09-18 ran at 85–95/s. And the failure is
confined to the toolhead node: the host and bridge saw a clean bus throughout, only the
EBB failed to receive. The SB2209 carries an RP2040, which has no CAN peripheral; Klipper
bit-bangs CAN on its PIO blocks (can2040), so a chip near its 85 °C limit mis-samples
frames on its own receiver while everyone else on the bus is fine. That matches this
profile and does not match a cable fault, which would raise error counters on both ends.

The board is **32 °C above the chamber air**. That heat is its own: TMC2209 at 0.5 A,
RP2040, the 12 V and 5 V regulators, plus hotend radiation, all inside a Stealthburner
shell with no airflow over the PCB. The 20-minute cube on 09-18 peaked at 73 °C; a long
print settles at ~78 °C, which is where the failures cluster.

This is a different event from the 2026-07-30 incident in
[`EBB_SB2209_HARDWARE.md`](../EBB_SB2209_HARDWARE.md): that one was a hang while idle at
56.7 °C with the sequence counters frozen. This time the board kept answering after its
shutdown (sequence numbers advancing, thermistor reading falling from 78 to 67 °C).

## Decision

Fit a small **24 V fan on the SB2209's own IND/FAN header** (VIN / GND / GPIO6, jumper in
the FAN position — see the pinout in `docs/EBB RP2040 CAN/`). FAN1 and FAN2 on the SB0000
breakout are both taken (24 V part-cooling and hotend fans), and the 4-wire header cannot
switch power (hardware notes). GPIO6 is switched, so Klipper controls it:
`[temperature_fan EBB]` on the board thermistor, target 50 °C, watermark ±3 °C, off when
cool. This replaces `[temperature_sensor EBB]`; the reading keeps its name in the UI.

The chamber target stays where it is. Rejected alternatives:

- **Lower the chamber (exhaust target 50 → 45).** Trades one problem for warping on
  large ABS parts, and the board is 32 °C above the chamber anyway; the chamber is not
  the main heat source.
- **A software watchdog that pauses the print above ~75 °C.** Recoverable, but a pause
  on a long ABS part leaves a scar and the user judged the fan simpler. Can be added
  later if the fan is not enough.
- **Lower the extruder run current.** 0.5 A is already modest for the CW2 pancake; the
  gain would be small and it costs extrusion torque.

## Consequences

- The fan must be **24 V** (the header supplies VIN), 2-wire; a 3010 or 4010 at ~0.1 A.
  Polarity per the pinout: VIN on the outer pin, GND in the middle.
- `max_temp` on a `temperature_fan` is a shutdown threshold. It is set to 100 °C on
  purpose so the fan section can never become a new way to kill a print.
- Verify after fitting: `QUERY_PROBE` (TAP lives on this board), then watch the EBB
  reading on a long print. Target evidence: board ≤ 60 °C with the chamber at 50 °C.
  If it still climbs past 70 °C, the next step is a duct from the hotend fan exhaust or
  the pause watchdog above.
- Deploy waits for the fan: the config change is in the repo but not synced until the
  header is populated, so the change and the hardware land together.

## Revisions

### 2026-09-22 — fan fitted, config deployed; the open cover alone was not enough

Before the fan section went live, a 1 h 41 min temperature tower ran with the
Stealthburner cover **open** and the header unconfigured (GPIO6 low, fan off). The board
thermistor still peaked at **77.6 °C** (`measured_max_temp`), essentially the 78.5 °C of
the failed print. Opening the shell does not move the number; the board needs forced air.

`[temperature_fan EBB]` was then synced and Klipper restarted on the idle machine.
Verified live: pin `EBB:BOARD_FAN`, target 50, watermark ±3, max_temp 100, no config
warnings, `QUERY_PROBE` open. With the board at 52.4 °C the fan output went to 1.0;
lowering the target to 30 for 25 s changed the reading by only 0.07 °C, so the 25-second
check says nothing about airflow yet. The real evidence is the next long print: the
target in Consequences stands, board ≤ 60 °C with the chamber at 50 °C.

### 2026-11-12 — the "GPIO6" pin is the fan's switched negative; the tach goes to GPIO12 through a diode

*Correction to Consequences:* the line "VIN on the outer pin, GND in the middle" reads as
"wire the fan between VIN and GND", and a fan wired that way runs at 100 % whatever
Klipper does. Schematic page 2 ("Proximity Switch", P5/P6) shows the header as P5: pin 3
DC_IN, pin 2 GND, pin 1 signal. The FAN/IND jumper P6 connects pin 1 either to the
optocoupler input (IND) or to the drain of Q2, an ES3400 low-side MOSFET with D3 flyback
to DC_IN (FAN). Q2's gate is the GPIO6 net. So in FAN position the fan goes **+ to VIN,
− to the pin marked GPIO6**, and the middle GND pin is not used.

*What showed it:* the owner had fitted a 3-wire 24 V fan with red on VIN, black on GND and
the yellow tach on the GPIO6 pin. The fan ran all the time and hot. Q2 was switching
nothing but the tach line.

*Tach:* this header has only one MCU line, and in FAN position that line drives Q2. The
fan's yellow wire therefore goes to GPIO12, which is pin 3 (`4W_Speed`) of the SB0000's
4-wire header and the board's only tach input (hardware notes). It goes through a Schottky
diode with the band toward the fan. Because the fan is switched on its negative, its ground
floats toward 24 V while it is off, and so can its tach. The diode keeps that off the
RP2040, while a low tach still reads as about 0.2 V. Config: `tachometer_pin: ^EBB:TACH`,
`tachometer_ppr: 2`. The fan's model is unrecorded, and 2 is the common value. The reading
means something only while the fan is on, and the watermark controller runs it at 0 or
100 %. *Not yet verified:* the fan rewired as above, with an rpm reading at 100 %.

### 2026-11-12 (later) — the tach goes to GPIO21 on the Probe connector instead; GPIO12 stays with the 4010

*What changed:* the owner cannot use GPIO12. It sits on the SB0000 breakout, which is
nearly impossible to reach in the toolhead, and its tach input is about to serve the
4-wire 4010 fan. On the SB2209 itself the only free input left is **GPIO21**, pin 5
(`PROBE`) of the 5-pin Probe connector. It was freed when the never-fitted filament sensor
was removed in a9985af. TAP uses pin 3 (GPIO22) of the same connector, and the X endstop
holds GPIO24.

*Circuit (schematic page 1):* `PROBE` reaches GPIO21 through U13, a 74LVC2G17 Schmitt
buffer powered at 3.3 V, whose inputs tolerate 5 V. Nothing pulls `PROBE` up. A 10 kΩ
resistor therefore goes from pin 5 to pin 2 (+5 V). A `^` on the RP2040 pin would only
pull the buffer's output. The Schottky diode stays, band toward the fan, and now protects
the buffer instead of the RP2040.

*Config:* `tachometer_pin: EBB:BOARD_FAN_TACH`, through a new alias
`BOARD_FAN_TACH=PROBE1`, which replaces the dead `FILAMENT_PROBE` alias.

*Rule 5 applies:* the wiring work shares a connector with TAP. Run `QUERY_PROBE` and
trigger the probe by hand before any `G28 Z`.

### 2026-11-12 (night) — the board fan is dead; its section is commented out until the replacement arrives

The owner reports that the 3-wire fan smelled burnt and is damaged. They trace it to the
first time its yellow tach wire was connected to the header's GPIO6 pin. The cause is not
established beyond that report. The owner has ordered a replacement, a 2010 or 2007 24 V
fan, being sourced together with a 4-wire 4010 for the hotend.

*Change:* `[temperature_fan EBB]` is commented out, tach lines included, and the plain
`[temperature_sensor EBB]` on gpio28 is back so the board reading stays visible. It is the
same section this ADR replaced, with no min/max limits, so it adds no new shutdown path.

*Consequence while the fan is out:* the board is uncooled again. The evidence in Context
applies in full: 78.5 °C and `Timer too close` after 2 h 17 min of ABS, and a 77.6 °C peak
with the cover open. Long hot-chamber prints are at risk until the fan is back.
