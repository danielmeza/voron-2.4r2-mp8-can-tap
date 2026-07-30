# 0013 — Probe samples: 3 → 2

**Status:** Accepted · 2026-07-25

## Context

Adaptive meshing ([0006](0006-adaptive-bed-mesh.md)) cut the mesh to 9 points, but
`[probe] samples: 3` still means every one of those points is probed 3 times —
27 probe cycles for a 9-point mesh, ~15 s of the mesh's total time.

`PROBE_ACCURACY` on this machine measures **std dev 0.001858 mm**, against Ellis'
bar of ≤ 0.004 mm — a ~2× margin even before touching `samples`.

## Decision

`samples: 3 → 2`, keeping `samples_result: median` and `samples_tolerance_retries: 3`
as the safety net for a bad individual probe.

The standard error of a 2-sample mean vs. a 3-sample mean, given the measured
0.001858 mm std dev:

- 3 samples: σ/√3 ≈ 0.0011 mm
- 2 samples: σ/√2 ≈ 0.0013 mm

A **0.0002 mm** change in expected noise, against a first-layer sensitivity of roughly
0.02 mm. Not measurable in print output.

Rejected alternative: **leave at 3**. Costs ~15 s per print for precision that is
already 2× inside spec — the wrong trade on a machine tuned for 5–10 min parts.

## Consequences

- Two probes is the Klipper-recommended floor for `samples_result: median` — going to
  1 would remove the median's outlier rejection entirely, so 2 is the stopping point,
  not an arbitrary stop on the way to 1.
- `samples_tolerance_retries: 3` still catches a probe pair that disagrees beyond
  `samples_tolerance: 0.005` and re-probes, so a single bad TAP trigger does not
  silently corrupt the mesh.
- If `PROBE_ACCURACY` is ever re-measured and comes back worse than **0.004 mm** std
  dev (Ellis' ceiling), revisit this first — it is the cheapest lever to reverse.
