# 0010 — BTT Belter is the belt-tension method of record

**Status:** Accepted · 2026-07-24

## Context

Two tools are available for setting belt tension, and **they disagree**:

- **BTT Belter** — a spring-loaded deflection gauge reading displacement in mm, with a
  vendor conversion to newtons and per-machine windows.
- **[Voron Belt Tuner](https://dehil.github.io/Voron-Belt-Tuner/)** — plucks the belt and
  reads the fundamental frequency. Targets **140 Hz** (130–150) over a **150 mm** span.

A plucked belt behaves like a string:

```
f = √(T/μ) / (2L)
```

Tension is a property of the belt; **frequency also depends on the span and on the belt's
linear mass density μ**. Converting between the two needs μ, and that is where they part
company. At a typical GT2-6 mm μ ≈ 5.6 g/m:

| | |
|---|---|
| 140 Hz over 150 mm | ≈ **9.9 N** |
| BTT's Voron 2.4 Z window | **20.4 – 25.8 N** |
| BTT's Z window as frequency | ≈ **201 – 226 Hz** |

A factor of **2–2.6×**. For them to agree, μ would have to be 11.6–14.6 g/m, roughly
double a standard GT2 6 mm belt. One of the two references is wrong, or is quoting a
different span, and neither publishes enough to tell which.

## Decision

**Use the Belter, and treat its table as authoritative.** Do not chase the 140 Hz target
at the same time.

Reasons:

- The Belter measures **force directly**. Frequency is an indirect measure that needs two
  extra quantities (span and μ), each of which is an error source — `f` scales as `1/L`,
  so a 10 % span error is a **21 %** tension error.
- BTT publishes explicit per-machine windows, including a Voron-2.4-specific Z window.
  The frequency tool applies one 140 Hz figure to both Z and A/B, which cannot be right
  for two belts under different loads.
- The formula is recoverable and auditable — extracted from BTT's own spreadsheet, which
  is vendored at [`docs/tools/`](../tools/) so it cannot go missing:

```
Tension_N = 7.673640167 × ABS(display_mm) − 33.7167364
```

  The `ABS()` is BTT's own, so the tool's negative readings are just a sign convention.

Measured on this machine — both in spec, no action taken:

| Belt | Display | Tension | Window | |
|---|---|---|---|---|
| A/B (both) | −6.0 mm | 12.33 N | 7.8 – 15.0 N | ✅ 63 % through |
| Z (all four) | −7.2 mm | 21.53 N | 20.4 – 25.8 N | ✅ 21 % through |

## Consequences

- **Belt tension is ruled out** as a cause of the 0.1487 mm bed-mesh saddle. The
  remaining Phase 2 suspects are gantry squaring / de-racking, debris under the spring
  steel, and bed mounting.
- Anyone cross-checking with the frequency app should expect roughly **207 Hz on Z** and
  **156 Hz on A/B** over a 150 mm span — *not* 140 Hz. That is this documented
  disagreement, not a fault.
- The disagreement is **not resolved**, only sidestepped. It can be settled empirically,
  since both tools are on hand: tension a belt, read `T` with the Belter, pluck a
  measured 150 mm span for `f`, then `μ = T / (2·L·f)²`. Until someone does that, the
  frequency route stays unused here.
- Equal tension across belts matters more than the absolute figure — a CoreXY cares about
  A/B matching, and QGL cares about the four Z belts matching.

Full working in [`docs/BELT_TENSION.md`](../BELT_TENSION.md).
