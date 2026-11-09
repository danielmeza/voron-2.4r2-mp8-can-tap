# Measurements

Raw data kept so later conclusions can be re-checked against what was actually observed.

| File | What | Conditions |
|---|---|---|
| `resonances_z_2026-07-26.csv` | Z-axis frequency response | `TEST_RESONANCES AXIS=Z ACCEL_PER_HZ=3.0`, toolhead at 175,175,20 |
| `bed_mesh_cold_vs_hot_2026-07-26.csv` | Full 11×11 bed mesh, cold and hot | QGL + `G28 Z` before each; cold at 34 °C, hot at 100 °C after a 10 min soak |

## Z resonance, 2026-07-26 — gantry diagnostic

Run as a **diagnostic**, not to configure a Z input shaper (this printer has none and
should not — Z moves are slow and short, and surface artifacts come from X/Y).

**Within the excited band (the sweep ran 5–100 Hz):**

| | Peak | Note |
|---|---|---|
| `psd_z` | **53.4 Hz** (3.21e4) | dominant Z mode, shoulders at 50.2 and 56.5 Hz |
| `psd_y` | **55.0 Hz** (8.51e3) | **27 % of the Z peak — cross-axis coupling** |
| `psd_x` | 136.6 Hz (1.55e3) | outside the excited band, treat as noise |

**Energy distribution:** 1 % below 30 Hz · 50 % in 30–60 Hz · 49 % above 60 Hz.

### What it says

**Solid:**

- **A clean dominant Z mode at ~53 Hz** — the gantry bouncing on its Z belts. Sharp and
  well defined: `psd_z` climbs from 7.5 % of peak at 45.5 Hz to 100 % at 53.4 Hz and back
  to 25 % by 58 Hz. This is a genuine structural resonance and a useful **baseline
  number** — re-measure after any gantry work and see whether it moves.
- **Only 1 % of the energy sits below 30 Hz.** A slack-belt gantry shows up as
  low-frequency compliance, and there is essentially none. Third independent confirmation
  that belt tension is not the problem (after the Belter readings,
  [ADR-0010](../adr/0010-belt-tension-method.md)).
- **X coupling is negligible** — `psd_x/psd_z` stays at 0.00–0.05 through the mode.

**Retracted — the Y cross-coupling is not real.**

An earlier reading of this data claimed 27 % Z→Y coupling and took it as evidence that
the gantry pitches front-to-back. Checking the ratio *across frequency* rather than
peak-to-peak shows otherwise:

| `psd_y/psd_z` at the 53 Hz mode (±4 Hz) | away from it |
|---|---|
| 0.28 | **0.36** |

The coupling is **higher away from the mode than at it**, and the ratio climbs past 1.0
around 59–61 Hz exactly where `psd_z` has decayed to ~13 % of peak. That is a noise
floor — roughly constant Y noise divided by a falling Z signal — not a coupled mode.
Comparing peak to peak manufactured a correlation that is not in the data.

The low excitation (20 % of Klipper's intended energy, below) is what makes the
cross-axis channels unusable: the Z peak is far above the noise, the Y channel is not.

### What it does not tell us

It does **not** diagnose the 0.1487 mm Y-dominated mesh saddle. A resonance sweep
measures *dynamic* behaviour; the saddle is a *static* deflection. Diagnosing that needs
static measurement — re-mesh after gantry squaring, or a dial indicator on the gantry.

### Caveats

- **Excitation was 20 % of Klipper's intended energy.** `accel_per_hz_z` defaults to 15,
  which needs ~1500 mm/s²; this machine's `max_z_accel` is 350, so the run used 3.0.
  Peak *locations* are trustworthy; absolute magnitudes are not.
- **Ignore everything above 100 Hz.** The sweep stopped there, so the 136–149 Hz peaks
  were never excited — they are noise, not modes.
- `accel_chip_z` had to be added: `accel_chip` maps to the **xy** pair only, and
  `accel_chip_z` defaults to empty (Klipper allows a different, often bed-mounted sensor
  for Z, so it will not assume). Same physical ADXL. Diagnostic-only.


---

## Bed mesh, cold vs hot — 2026-07-26

Two full 11×11 meshes under controlled conditions, each preceded by its own QGL and
`G28 Z` so the Z reference was re-established both times.

| | Range |
|---|---|
| **Cold** (bed 34 °C) | **0.3100 mm** |
| **Hot** (bed 100 °C, 10 min soak) | **0.1231 mm** |

**The bed is ~60 % flatter at printing temperature.**

Per-row Y averages, front → back:

| Y | cold | hot | Δ |
|---|---|---|---|
| 30 | −0.2685 | −0.0151 | +0.2534 |
| 117 | −0.3105 | −0.0148 | +0.2956 |
| 204 | −0.3237 | −0.0524 | +0.2712 |
| 320 | −0.2525 | −0.0203 | +0.2322 |

Point-by-point change: mean **+0.2687 mm**, span **0.3213 mm**.

### What this means

- **Never mesh a cold bed.** At 34 °C the plate is dished ~0.3 mm relative to the
  Z-home point; at 100 °C it flattens to ~0.12 mm. A cold mesh describes a shape the
  printer never prints on. `PRINT_START` already meshes after `M190`, which is correct —
  this measurement is why that ordering matters.
- **The mean +0.27 mm is a near-uniform Z shift** — thermal expansion of the bed and
  frame. It is re-zeroed here because `G28 Z` ran at each temperature. During a print,
  drift *after* homing is exactly what
  [`z_thermal_adjust`](../adr/0004-frame-temperature-sensing.md) compensates, and this
  gives a sense of the magnitude involved.
- **At print temperature the bed is good.** 0.1231 mm across a 350 mm plate, against
  0.1487 mm for the previously saved profile — about 0.026 mm apart, taken at different
  times. That is reasonable repeatability, and the adaptive mesh compensates it every
  print.
- **The saddle is not a defect to chase.** With belts in spec, the gantry squared, and
  the hot shape both modest and repeatable, the remaining deviation is the plate itself
  behaving normally under heat.
