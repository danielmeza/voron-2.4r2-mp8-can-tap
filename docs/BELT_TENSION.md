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
| A/B (both) | −6.0 mm | **12.33 N** | 7.8 – 15.0 N | ✅ in range (63 % through the window) |
| Z (all four) | −7.2 mm | **21.53 N** | 20.4 – 25.8 N | ✅ in range (21 % through the window) |

**Both belt sets are in spec.** The spreadsheet was right.

Z sits in the lower fifth of its window, which is a fine place to be — Ellis'
guidance is that equal tension across all four Z belts matters more than the exact
figure, and slightly-loose beats over-tight for motor and bearing load.

Target display readings for reference: **A/B 5.41 – 6.35 mm**, **Z 7.05 – 7.76 mm**.

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
| This printer's actual Z (21.53 N) as frequency | **≈ 207 Hz** |
| This printer's actual A/B (12.33 N) as frequency | **≈ 156 Hz** |

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

1. **Leave both belt sets alone.** A/B at 12.33 N and Z at 21.53 N are both inside BTT's
   windows, and equality across belts — the property that actually matters — holds.
2. **Belt tension is therefore ruled out** as a cause of the 0.1487 mm mesh saddle. If
   Phase 2 mechanical work is needed, the remaining suspects are gantry squaring /
   de-racking, debris under the spring steel, and bed mounting — not tension.
3. If you ever want to cross-check with the frequency app, expect roughly **207 Hz** on Z
   and **156 Hz** on A/B over a 150 mm span — *not* 140 Hz. That is the method
   disagreement above, not a fault.
4. Cross-calibrate μ if you want the two tools to agree.

Tracked in [`TUNING_PLAN.md`](TUNING_PLAN.md) Phase 2.
