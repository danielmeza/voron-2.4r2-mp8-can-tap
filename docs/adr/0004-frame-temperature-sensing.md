# 0004 — Frame temperature sensing via bolted NTCs, not I2C

**Status:** Accepted · 2026-07-24 · *hardware not yet fitted*

## Context

[0003](0003-temperature-gated-soak.md) gates on chamber air, which warms much faster
than the frame — so it does not fully solve thermal drift. What actually moves Z on a
2.4 is the **vertical Z extrusions** growing as they heat. Ellis lists Klipper's
`z_thermal_adjust` as a legitimate mitigation, and it needs a frame sensor.

The existing chamber sensor is a **BME280** — it measures *air*, and moving it to the
frame would not change that.

Options considered:

| Option | Wires | Notes |
|---|---|---|
| NTC 100K 3950, M3 ring lug | **2** | Bolts to a T-nut, metal-to-metal contact |
| LM75 on the existing I2C | **4** (VCC/GND/SDA/SCL) | Reads its own PCB die temperature |
| MCP9808 | — | **No Klipper driver.** Not viable |
| DS18B20 | 3 | 1-Wire, chainable, but needs `serial_no` + firmware support |

The I2C option was initially attractive for "chain onto the existing bus, no new
wiring". That argument does not survive contact with the physical layout: the sensors
go on **four separate Z corner pillars**, which cannot be usefully chained, so each
still needs its own 4-conductor run. NTCs need 2 conductors each — and the MP8 has
**four free thermistor ports** (TH0–TH3; only THB is used).

Mount point also matters: the sensors go on **static** vertical extrusions, not the
moving gantry, so there is no drag chain and the wiring is trivial either way.

## Decision

Use 2-wire **NTC 100K 3950 thermistors with M3 ring lugs**, bolted into T-nuts on the
vertical Z corner pillars, wired to the MP8's free TH ports.

Roll out in stages:

1. One sensor on TH1 → enough to configure `z_thermal_adjust`.
2. All four → feed a virtual sensor via `sensor_type: temperature_combined`.

`temperature_combined` is registered with `add_sensor_factory`, so it is a
**sensor_type used inside a `[temperature_sensor]` block**, not a section of its own.
`z_thermal_adjust` defines its own sensor inline (it calls `heaters.setup_sensor` on
its own config), so it can take the combined type directly.

`maximum_deviation` on the combined sensor makes Klipper **raise an error** when the
pillars disagree — turning it into an active detector for *uneven* frame heating,
which tilts the gantry and is a direct suspect for the original symptom.

Config template lives commented-out in `printer/aux_temperature_sensors.cfg`.

## Consequences

- Four 2-wire runs instead of four 4-wire runs; no I2C address management.
- Absolute accuracy barely matters — `temp_coeff` is calibrated against whatever the
  sensor reads, so stability and repeatability are what count. A cheap 3950 is fine.
- **`temp_coeff` must be measured, not guessed.** 0.02 mm/°C is a starting ballpark
  for aluminium only. Wrong sign makes drift worse, so `max_z_adjustment` starts at
  0.2 mm as a safety net.
- Fitting only one sensor gives Z compensation but no uneven-heating detection.
- The BME280 keeps doing its real job (chamber air for the exhaust fan).
