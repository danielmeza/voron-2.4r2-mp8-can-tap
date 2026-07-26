# Measurements

Raw data kept so later conclusions can be re-checked against what was actually observed.

| File | What | Conditions |
|---|---|---|
| `resonances_z_2026-07-26.csv` | Z-axis frequency response | `TEST_RESONANCES AXIS=Z ACCEL_PER_HZ=3.0`, toolhead at 175,175,20 |

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

- **A clean dominant Z mode near 53 Hz** — the gantry bouncing on its Z belts.
- **Only 1 % of the energy sits below 30 Hz.** A slack-belt gantry shows up as
  low-frequency compliance, so this corroborates the Belter readings
  ([ADR-0010](../adr/0010-belt-tension-method.md)): tension is not the problem.
- **Vertical excitation produces significant *Y* motion** (27 % of the Z peak, at
  essentially the same frequency). That is a coupled mode — driving the gantry up and
  down makes it move front-to-back, i.e. the gantry pitches rather than staying rigid
  in Y.

### How much to read into it

The measured bed mesh is a **Y-dominated saddle** (front `+0.014`, middle `−0.055`,
back `+0.033`, range 0.1487 mm). The Z↔Y coupling points at compliance along the **same
axis** as that saddle, which strengthens the case that the remaining Phase 2 suspects
are gantry-side (squaring / de-racking / bed mounting) rather than the bed itself —
consistent with Ellis' *"most bed mesh issues are caused by the gantry."*

**But these are different measurements.** The mesh saddle is a *static* deflection; this
is a *dynamic* mode. Agreement in axis is suggestive, not proof of a shared cause.

### Caveats

- **Excitation was 20 % of Klipper's intended energy.** `accel_per_hz_z` defaults to 15,
  which needs ~1500 mm/s²; this machine's `max_z_accel` is 350, so the run used 3.0.
  Peak *locations* are trustworthy; absolute magnitudes are not.
- **Ignore everything above 100 Hz.** The sweep stopped there, so the 136–149 Hz peaks
  were never excited — they are noise, not modes.
- `accel_chip_z` had to be added: `accel_chip` maps to the **xy** pair only, and
  `accel_chip_z` defaults to empty (Klipper allows a different, often bed-mounted sensor
  for Z, so it will not assume). Same physical ADXL. Diagnostic-only.
