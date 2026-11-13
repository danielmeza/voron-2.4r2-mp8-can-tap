#!/usr/bin/env python3
"""Generate the ABS layer-time dwell ladder (ADR-0027).

A gcode test print that measures the shortest minimum layer time ABS can take in
this printer's chamber. Every layer prints at the same speed. Each layer's total
time is set by parking the head away from the part and dwelling, never by slowing
the extrusion down, so the band edges isolate layer time from speed.

Usage:  python3 -I scripts/gen_layer_time_tower.py [--out PATH] [options]
        Default output is stdout; the summary goes to stderr.
        Exit code 1 if a self-check fails (nothing is written then).
"""

import argparse
import math
import os
import re
import sys
from collections import namedtuple

CORNER_S = 0.03          # estimated cost of each corner or reversal (s)
LIFT_MM = 2.0            # Z lift before the park move
FILAMENT_AREA = math.pi * 0.875 ** 2   # pi * (1.75 / 2)^2, mm^2
EDGE_MIN, EDGE_MAX = 5.0, 345.0
MAX_E_MOVE = 10.0
HEAT_RE = re.compile(r"M(104|109|140|190)\b")

Layer = namedtuple("Layer", "n z w h speed loops fill corners")


def num(v, nd=3):
    """Format a number without trailing zeros."""
    s = f"{v:.{nd}f}".rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


def parse_pair(text):
    x, y = (float(v) for v in text.split(","))
    return x, y


def parse_args(argv):
    p = argparse.ArgumentParser(description="Generate the ABS layer-time dwell ladder (ADR-0027).")
    p.add_argument("--out", default=None, help="output path (default: stdout)")
    p.add_argument("--extruder", type=float, default=235)
    p.add_argument("--extruder-after", type=float, default=230)
    p.add_argument("--bed", type=float, default=80)
    p.add_argument("--chamber", type=float, default=50)
    p.add_argument("--fan", type=float, default=70, help="percent, from layer 1 on")
    p.add_argument("--times", default="12,10,8,6,0",
                   help="target seconds per layer, one band each; 0 = no dwell (the layer's natural time)")
    p.add_argument("--band-layers", type=int, default=20)
    p.add_argument("--size", type=float, default=10, help="square side, mm")
    p.add_argument("--center", type=parse_pair, default=(175.0, 175.0))
    p.add_argument("--speed", type=float, default=60, help="mm/s, all extrusion after layer 0")
    p.add_argument("--first-speed", type=float, default=30, help="mm/s, layer 0 extrusion")
    p.add_argument("--travel", type=float, default=200, help="mm/s")
    p.add_argument("--flow", type=float, default=0.912)
    p.add_argument("--accel", type=float, default=3000)
    p.add_argument("--width", type=float, default=0.45, help="extrusion width, mm")
    p.add_argument("--first-height", type=float, default=0.25)
    p.add_argument("--height", type=float, default=0.2)
    p.add_argument("--retract", type=float, default=0.5, help="mm")
    p.add_argument("--retract-speed", type=float, default=35, help="mm/s")
    p.add_argument("--park-offset", type=float, default=25, help="mm in +X from the part's right edge")
    a = p.parse_args(argv)
    try:
        a.times = [float(v) for v in a.times.split(",") if v.strip()]
    except ValueError:
        p.error("--times must be a comma-separated list of numbers")
    if not a.times or a.band_layers < 1:
        p.error("need at least one band and --band-layers >= 1")
    return a


def spread(lo, hi, count):
    if count == 1:
        return [(lo + hi) / 2]
    return [lo + (hi - lo) * k / (count - 1) for k in range(count)]


def layer_geometry(n, a):
    """Perimeters (innermost first), zigzag fill and corner count for layer n."""
    first = n == 0
    w = a.width * 1.2 if first else a.width          # layer 0: 120 % width
    h = a.first_height if first else a.height
    z = a.first_height + n * a.height
    speed = a.first_speed if first else a.speed
    cx, cy = a.center
    half = a.size / 2

    loops = []
    for i in (2, 1, 0):                              # perimeter i, 0 = outermost
        d = w / 2 + i * w
        xl, xh = cx - half + d, cx + half - d
        yl, yh = cy - half + d, cy + half - d
        # Closed loop, starting and ending at the back-left corner (seam aligned).
        loops.append([(xl, yh), (xl, yl), (xh, yl), (xh, yh), (xl, yh)])

    ax0, ax1 = cx - half + 3 * w, cx + half - 3 * w  # fill area, inset 3 * width
    ay0, ay1 = cy - half + 3 * w, cy + half - 3 * w
    span = ax1 - ax0
    count = round((span - w) / w) + 1 if span > w else 0
    fill = []
    if count and n % 2 == 0:                         # even layers: lines along X
        for k, y in enumerate(spread(ay0 + w / 2, ay1 - w / 2, count)):
            ends = (ax0, ax1) if k % 2 == 0 else (ax1, ax0)
            fill += [(ends[0], y), (ends[1], y)]
    elif count:                                      # odd layers: lines along Y
        for k, x in enumerate(spread(ax0 + w / 2, ax1 - w / 2, count)):
            ends = (ay0, ay1) if k % 2 == 0 else (ay1, ay0)
            fill += [(x, ends[0]), (x, ends[1])]

    corners = 4 * len(loops) + max(0, 2 * count - 2)
    return Layer(n, z, w, h, speed, loops, fill, corners)


class Emitter:
    """Writes G1 lines and keeps the time estimate and filament count."""

    def __init__(self, travel, retract_speed):
        self.lines = []
        self.pos = [None, None, None]
        self.travel = travel
        self.retract_speed = retract_speed
        self.time = 0.0
        self.extruded = 0.0
        self.max_e = 0.0

    def add_time(self, seconds):
        self.time += seconds

    def move(self, x=None, y=None, z=None, e=None, feed=None):
        feed = self.travel if feed is None else feed
        changed = False
        dist_sq = 0.0
        for i, v in enumerate((x, y, z)):
            if v is None:
                continue
            if self.pos[i] is None:
                changed = True
            elif v != self.pos[i]:
                changed = True
                dist_sq += (v - self.pos[i]) ** 2
            self.pos[i] = v
        if not changed and e is None:
            return
        dist = math.sqrt(dist_sq) if dist_sq else abs(e or 0.0)
        self.time += dist / feed
        if dist_sq and e:
            self.extruded += e
        words = ["G1"]
        for letter, v in (("X", x), ("Y", y), ("Z", z)):
            if v is not None:
                words.append(f"{letter}{num(v)}")
        if e is not None:
            words.append(f"E{num(e, 5)}")
            self.max_e = max(self.max_e, abs(e))
        words.append(f"F{round(feed * 60)}")
        self.lines.append(" ".join(words))


def build(a):
    total = 1 + len(a.times) * a.band_layers
    band_of = [max(0, (n - 1) // a.band_layers) for n in range(total)]
    geoms = [layer_geometry(n, a) for n in range(total)]
    cx, cy = a.center
    half = a.size / 2
    x0, x1 = cx - half, cx + half
    y0, y1 = cy - half, cy + half
    park_x = x1 + a.park_offset
    fil_k = a.flow / FILAMENT_AREA

    em = Emitter(a.travel, a.retract_speed)
    em.lines += [
        f"EXCLUDE_OBJECT_DEFINE NAME=layer_time_tower CENTER={num(cx)},{num(cy)} "
        f"POLYGON=[[{num(x0)},{num(y0)}],[{num(x1)},{num(y0)}],"
        f"[{num(x1)},{num(y1)}],[{num(x0)},{num(y1)}]]",
        f"PRINT_START EXTRUDER={num(a.extruder)} BED={num(a.bed)} CHAMBER={num(a.chamber)}",
        "G90",
        "M83",
        f"M204 S{num(a.accel)}",
        "M107",
    ]

    g0 = geoms[0]
    sx, sy = g0.loops[0][0]
    em.move(x=sx, y=sy, z=g0.z + LIFT_MM)
    em.move(z=g0.z)

    dwell_ms = [0] * total
    t_layer = [0.0] * total
    for n, g in enumerate(geoms):
        band = band_of[n]
        target = a.times[band]
        em.lines.append(f";LAYER:{n}")
        if n == 0 or band_of[n - 1] != band:
            ## The console timestamps of these markers give the MEASURED layer time:
            ## (next marker - this marker) / band layers. The estimate below ignores
            ## acceleration, so the measured value is the one to use (ADR-0027).
            label = "natural" if target == 0 else f"{num(target)}s"
            em.lines.append(f"M117 Band {label}")
            em.lines.append(f'RESPOND MSG="layer time band {label} from z {g.z:.2f}"')
        em.lines.append("EXCLUDE_OBJECT_START NAME=layer_time_tower")
        t0 = em.time
        epm = g.w * g.h * fil_k
        for poly in [*g.loops, g.fill]:
            if not poly:
                continue
            em.move(x=poly[0][0], y=poly[0][1])
            for qx, qy in poly[1:]:
                seg = math.hypot(qx - em.pos[0], qy - em.pos[1])
                em.move(x=qx, y=qy, e=seg * epm, feed=g.speed)
        em.lines.append("EXCLUDE_OBJECT_END NAME=layer_time_tower")
        em.add_time(g.corners * CORNER_S)

        em.move(e=-a.retract, feed=a.retract_speed)
        em.move(z=g.z + LIFT_MM)
        em.move(x=park_x, y=cy)
        slot = len(em.lines)
        em.lines.append("")                          # dwell, filled in below
        if n + 1 < len(geoms):
            nxt = geoms[n + 1]
            nx, ny = nxt.loops[0][0]
            em.move(x=nx, y=ny)
            em.move(z=nxt.z)
            em.move(e=a.retract, feed=a.retract_speed)

        t_layer[n] = em.time - t0
        dwell_ms[n] = max(0, round((target - t_layer[n]) * 1000))
        em.lines[slot] = f"G4 P{dwell_ms[n]}"
        if n == 0:
            em.lines.append(f"M104 S{num(a.extruder_after)}")
            em.lines.append(f"M106 S{round(a.fan * 255 / 100)}")

    em.lines += [
        "",
        "; end",
        'RESPOND MSG="layer time ladder end"',          # closes the last band's timing
        "M107",
        "PRINT_END",
    ]
    return total, band_of, geoms, em, dwell_ms, t_layer


def header(a, total, band_of, geoms, dwell_ms, em):
    bands = sorted(set(band_of))
    cx, cy = a.center
    out = [
        "; layer_time_tower: ABS layer-time dwell ladder (ADR-0027)",
        "; Generated by scripts/gen_layer_time_tower.py. Regenerate instead of editing.",
        "; Measures the shortest layer time this chamber and fan allow. Every layer prints",
        "; at one speed; each layer's total time is set by a park-and-dwell, never by",
        "; slowing the extrusion. Bands run from the longest target (bottom) to the",
        "; shortest (top), so a failing short band cannot spoil the others.",
        ";",
        "; Parameters:",
        f";   extruder {num(a.extruder)} C (layer 0), extruder_after {num(a.extruder_after)} C, "
        f"bed {num(a.bed)} C, chamber {num(a.chamber)} C",
        f";   fan {num(a.fan)} % from layer 1 (layer 0 fan off), times {','.join(num(t) for t in a.times)} s, "
        f"band_layers {a.band_layers}",
        f";   size {num(a.size)} mm, center {num(cx)},{num(cy)}, speed {num(a.speed)} mm/s, "
        f"first_speed {num(a.first_speed)} mm/s, travel {num(a.travel)} mm/s",
        f";   flow {num(a.flow)}, accel {num(a.accel)}, width {num(a.width)} mm (layer 0: "
        f"{num(geoms[0].w)} mm), first_height {num(a.first_height)}, height {num(a.height)}",
        f";   retract {num(a.retract)} mm at {num(a.retract_speed)} mm/s, "
        f"park_offset {num(a.park_offset)} mm (+X from the part's right edge)",
        f"; Layers: {total}, z {geoms[0].z:.2f} to {geoms[-1].z:.2f} mm",
        ";",
        "; Band table:",
        ";  band  target_s  layers      z_mm            dwell_s_per_layer",
    ]
    for b in bands:
        layers = [n for n in range(total) if band_of[n] == b]
        first, last = layers[0], layers[-1]
        dwells = [dwell_ms[n] / 1000 for n in layers]
        out.append(
            f";  {b:>4}  {num(a.times[b]):>8}  {first:>3}-{last:<3}  "
            f"{geoms[first].z:.2f}-{geoms[last].z:.2f}  "
            f"{min(dwells):.2f}-{max(dwells):.2f}"
        )
    est_min = (em.time + sum(dwell_ms) / 1000) / 60
    out += [
        f"; Estimated total time: {est_min:.1f} min (excluding PRINT_START). "
        "Layer 0 is slower (first_speed); its dwell differs from the rest of band 0.",
        "; Judge: the shortest band whose corners stay sharp and walls straight (no bulge, gloss or rounding), and whose layers do not crack when flexed. That value is cool_min_layer_time. See docs/adr/0027.",
        "",
    ]
    return out


def self_check(lines):
    errs = []
    ps = next((i for i, ln in enumerate(lines) if ln.split(";", 1)[0].strip().startswith("PRINT_START")), None)
    if ps is None:
        errs.append("no PRINT_START line")
    for ln in lines[: ps or 0]:
        cmd = ln.split(";", 1)[0].strip()
        if HEAT_RE.match(cmd):
            errs.append(f"heating command before PRINT_START: {cmd}")
    starts = sum(1 for ln in lines if ln.startswith("EXCLUDE_OBJECT_START"))
    ends = sum(1 for ln in lines if ln.startswith("EXCLUDE_OBJECT_END"))
    if starts != ends:
        errs.append(f"EXCLUDE_OBJECT_START {starts} != EXCLUDE_OBJECT_END {ends}")
    for ln in lines:
        cmd = ln.split(";", 1)[0].strip()
        if not cmd.startswith("G1"):
            continue
        mx = re.search(r"\bX(-?[\d.]+)", cmd)
        my = re.search(r"\bY(-?[\d.]+)", cmd)
        for label, m in (("X", mx), ("Y", my)):
            if m and not EDGE_MIN <= float(m.group(1)) <= EDGE_MAX:
                errs.append(f"{label} out of {EDGE_MIN:g}..{EDGE_MAX:g}: {cmd}")
        me = re.search(r"\bE(-?[\d.]+)", cmd)
        if me and abs(float(me.group(1))) > MAX_E_MOVE:
            errs.append(f"E move above {MAX_E_MOVE:g} mm: {cmd}")
    return errs


def main(argv):
    a = parse_args(argv)
    total, band_of, geoms, em, dwell_ms, t_layer = build(a)
    head = header(a, total, band_of, geoms, dwell_ms, em)
    lines = head + em.lines

    errs = self_check(lines)
    if errs:
        for e in errs[:20]:
            print(f"self-check failed: {e}", file=sys.stderr)
        return 1

    for b in sorted(set(band_of)):
        layers = [n for n in range(total) if band_of[n] == b]
        target = a.times[b]
        worst = max(layers, key=lambda n: t_layer[n])
        if target > 0 and t_layer[worst] > target:
            print(
                f"warning: band {num(target)} s: layer {worst} needs {t_layer[worst]:.2f} s, "
                f"{t_layer[worst] - target:.2f} s over the target; no dwell can reach it",
                file=sys.stderr,
            )

    text = "\n".join(lines) + "\n"
    if a.out:
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        with open(a.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    else:
        sys.stdout.write(text)

    total_s = em.time + sum(dwell_ms) / 1000
    print(f"layers: {total}", file=sys.stderr)
    print(f"height: {geoms[-1].z:.2f} mm (first layer {geoms[0].z:.2f})", file=sys.stderr)
    print(f"estimated time: {total_s / 60:.1f} min (excluding PRINT_START)", file=sys.stderr)
    print(f"filament: {em.extruded:.0f} mm extruded", file=sys.stderr)
    for b in sorted(set(band_of)):
        ds = [dwell_ms[n] / 1000 for n in range(total) if band_of[n] == b]
        print(f"band {num(a.times[b])} s: dwell {min(ds):.2f}-{max(ds):.2f} s per layer", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
