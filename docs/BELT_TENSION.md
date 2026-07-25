# Belt tension — two methods, and how they relate

Two tools are in use here and **they do not agree out of the box**. Read the
reconciliation section before trusting either number.

---

## Method A — BTT Belter (direct force)

[Vendor page](https://global.bttwiki.com/belter.html) ·
[Repo](https://github.com/bigtreetech/Belter-belt-tension-Tool) ·
manual in [`docs/Belter User Manual.pdf`](Belter%20User%20Manual.pdf)

The Belter is a spring-loaded deflection gauge. You clamp it on the belt and read a
**displacement in mm**; a stiffer (tighter) belt pushes the probe further, so a larger
reading means more tension.

### The formula

Extracted from BTT's own `Calculation Tool/Belter_tension_calculator.xlsx`:

```
Tension_N = 7.673640167 × ABS(display_mm) − 33.7167364
```

**The `ABS()` is BTT's, not ours.** Negative readings are normal — the sign is just the
tool's direction convention, and only the magnitude matters. A display of `-9.5` is
treated identically to `9.5`.

### BTT's recommended windows

| Machine / belt | Tension (N) | Equivalent display reading (mm) |
|---|---|---|
| **All Voron A/B belts** | 7.8 – 15.0 | **5.41 – 6.35** |
| **Voron 2.4 Z belts** | 20.4 – 25.8 | **7.05 – 7.76** |
| VZ330/250 | 12.0 – 20.0 | 5.96 – 7.00 |

### This printer, measured

| Belt | Display | Tension | Spec | Verdict |
|---|---|---|---|---|
| A/B (both) | −6.0 mm | **12.33 N** | 7.8 – 15.0 N | ✅ in range, comfortably mid-window |
| Z | −9.5 mm | **39.18 N** | 20.4 – 25.8 N | ❌ **+52 % over max** |
| Z | −9.8 mm | **41.48 N** | 20.4 – 25.8 N | ❌ **+61 % over max** |

**The Z belts are substantially over-tensioned by BTT's own table.** To land in
spec the display should read **7.05 – 7.76 mm**, not 9.5 – 9.8.

Over-tensioned Z belts load the motors and bearings, and can bow the gantry — which
matters here, because the measured bed mesh is a **Y-dominated saddle with a 0.1487 mm
range** and Ellis notes that *"most bed mesh issues are caused by the gantry rather
than the bed itself."* This is a plausible contributor to the original
adhesion-away-from-centre complaint and is worth correcting before Phase 2 mechanical
work.

> If the spreadsheet reported "YES" for these Z values, check that the dropdown was set
> to **Voron 2.4 Z belts** and not *All Voron A/B Belts* (7.8–15 N) or *Custom Printer*.
> 39 N does not fall inside any of the built-in windows.

---

## Method B — Frequency / plucking

[Voron Belt Tuner](https://dehil.github.io/Voron-Belt-Tuner/) — plucks the belt and
reads the fundamental with the microphone.

- Target **140 Hz**, acceptable **130 – 150 Hz**
- Measured over a **150 mm** free span
- Channels for Z0–Z3 and A/B

Ellis' guide gives the same shape of advice: Z belts equal at **140 Hz over a 150 mm
span**, A/B at **110 Hz**.

---

## Reconciling the two — they disagree, and here is why

A plucked belt behaves like a string:

```
f = √(T/μ) / (2L)          T = μ · (2·L·f)²

  f = frequency (Hz)    T = tension (N)
  L = free span (m)     μ = belt linear mass density (kg/m)
```

**Tension is a property of the belt; frequency additionally depends on span and on μ.**
Converting between the two therefore needs μ, and that is where it falls apart:

| | |
|---|---|
| 140 Hz over 150 mm, μ = 5.6 g/m (typical GT2-6 mm) | **≈ 9.9 N** |
| BTT's Voron 2.4 Z window | **20.4 – 25.8 N** |
| BTT's Z window expressed as frequency (μ = 5.6 g/m) | **≈ 201 – 226 Hz** |

So BTT's Z target is roughly **2–2.6× the tension** implied by the 140 Hz rule. For the
two to agree, μ would have to be **11.6 – 14.6 g/m**, about double a typical GT2 6 mm
belt.

Sensitivity to μ, at 140 Hz / 150 mm:

| μ | Implied tension |
|---|---|
| 5.0 g/m | 8.8 N |
| 5.6 g/m | 9.9 N |
| 6.0 g/m | 10.6 N |
| 12.5 g/m | 22.1 N |

**Do not mix the two.** Pick one method and stay with it, or cross-calibrate (below).
Chasing both simultaneously will just move the belts back and forth.

### Cross-calibrating (you own both tools)

Because you have the Belter *and* the frequency app, you can measure μ directly instead
of trusting a book value:

1. Tension one belt to any convenient value.
2. Read it with the Belter → `T` (via the formula above).
3. Pluck a **carefully measured** 150 mm span of that same belt → `f`.
4. `μ = T / (2·L·f)²`

Repeat on two or three belts. Once μ is known for *your* belts, the two methods become
interchangeable and you can settle which target is right. Record the result here.

**Span accuracy matters a lot** — `f` scales as `1/L`, so a 10 % span error is a 10 %
frequency error and a **21 %** tension error. Measure the plucked span, don't eyeball it.

---

## Practical recommendation

1. **A/B belts: leave them.** 12.33 N sits mid-window on the Belter, and both belts read
   the same, which is the property that actually matters for CoreXY.
2. **Z belts: back them off.** They read 9.5 – 9.8 mm where BTT wants 7.05 – 7.76 mm.
   Equal tension across all four is more important than the absolute value.
3. Re-run `QUAD_GANTRY_LEVEL` and re-mesh afterwards, and compare the mesh range against
   the current **0.1487 mm**. If the saddle flattens, the Z belts were part of the
   adhesion problem.
4. Then cross-calibrate μ so future checks can use whichever tool is to hand.

Tracked in [`TUNING_PLAN.md`](TUNING_PLAN.md) Phase 2.
