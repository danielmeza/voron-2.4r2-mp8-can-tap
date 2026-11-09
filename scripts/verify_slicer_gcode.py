#!/usr/bin/env python3
"""Check a sliced gcode file against docs/SLICER_SETUP.md section 7's checklist.

That checklist is currently done by eye. This automates the objective part: do
the required commands appear, are PRINT_START's placeholders resolved to real
numbers, does the file use exactly one retraction method (slicer G1 E- moves by
default, or firmware G10/G11 when tuning live -- ADR-0021), and does nothing heat
the machine before PRINT_START (which would defeat the soak-gate/QGL/mesh
sequencing PRINT_START owns).

Usage:    scripts/verify_slicer_gcode.py path/to/sliced.gcode
Selftest: scripts/verify_slicer_gcode.py --selftest
Exit code is 1 if any check fails, else 0.
"""

import re
import sys

REQUIRED_ANYWHERE = {
    "EXCLUDE_OBJECT_DEFINE": r"^\s*EXCLUDE_OBJECT_DEFINE\b",
}
# Retraction: the machine keeps BOTH mechanisms (ADR-0021). Slicer retraction
# (G1 E-... moves, default) or firmware retraction (G10/G11, for live tuning) --
# a file must use exactly one of them.
FW_RETRACT_RE = re.compile(r"^\s*G1[01]\b")
SLICER_RETRACT_RE = re.compile(r"^\s*G1\s+(?:F[\d.]+\s+)?E-")
# Either satisfies the "layer progress in the UI" requirement.
PROGRESS_RE = re.compile(r"^\s*(SET_PRINT_STATS_INFO|M73)\b")
# Per-feature acceleration. A Marlin-flavoured slicer emits M204; one set to
# klipper flavour emits Klipper's native SET_VELOCITY_LIMIT ACCEL= instead --
# which is the better output, not a missing feature. Accept either.
ACCEL_RE = re.compile(r"^\s*(M204\b|SET_VELOCITY_LIMIT\b.*\bACCEL=)")
PRINT_START_RE = re.compile(r"^\s*PRINT_START\b(.*)$")
HEATING_BEFORE_START_RE = re.compile(r"^\s*M(109|190)\b")
UNRESOLVED_PLACEHOLDER_RE = re.compile(r"[\[\]{}]")


def check(lines):
    """Return a list of failure strings; empty means the file passes."""
    failures = []

    found = {name: False for name in REQUIRED_ANYWHERE}
    patterns = {name: re.compile(p) for name, p in REQUIRED_ANYWHERE.items()}
    progress_found = False
    accel_found = False
    fw_retracts = 0
    slicer_retracts = 0
    print_start_line = None
    heating_before_start = []

    for i, line in enumerate(lines):
        if print_start_line is None and HEATING_BEFORE_START_RE.match(line):
            heating_before_start.append((i + 1, line.strip()))
        m = PRINT_START_RE.match(line)
        if m and print_start_line is None:
            print_start_line = (i + 1, m.group(1))
        for name, pat in patterns.items():
            if pat.match(line):
                found[name] = True
        if PROGRESS_RE.match(line):
            progress_found = True
        if FW_RETRACT_RE.match(line):
            fw_retracts += 1
        elif SLICER_RETRACT_RE.match(line):
            slicer_retracts += 1
        if ACCEL_RE.match(line):
            accel_found = True

    if print_start_line is None:
        failures.append("PRINT_START not found in file")
    else:
        lineno, params = print_start_line
        if "EXTRUDER=" not in params or "BED=" not in params:
            failures.append(
                f"line {lineno}: PRINT_START missing EXTRUDER= or BED= params"
            )
        elif UNRESOLVED_PLACEHOLDER_RE.search(params):
            failures.append(
                f"line {lineno}: PRINT_START params look unresolved: {params.strip()}"
            )

    if heating_before_start:
        lineno, text = heating_before_start[0]
        failures.append(
            f"line {lineno}: heating command before PRINT_START ({text}) -- "
            "breaks the soak-gate/QGL/mesh sequencing PRINT_START owns"
        )

    for name, ok in found.items():
        if not ok:
            failures.append(f"{name} never appears -- checklist requires it")
    if fw_retracts and slicer_retracts:
        failures.append(
            f"both retraction methods present ({fw_retracts} G10/G11 and "
            f"{slicer_retracts} G1 E- moves) -- a file must use exactly one (ADR-0021)"
        )
    elif not fw_retracts and not slicer_retracts:
        failures.append("no retraction at all (neither G10/G11 nor G1 E- moves)")
    if not progress_found:
        failures.append("neither SET_PRINT_STATS_INFO nor M73 appears")
    if not accel_found:
        failures.append(
            "no per-feature acceleration (neither M204 nor SET_VELOCITY_LIMIT ACCEL=)"
        )

    return failures


def selftest():
    good = [
        "M140 S0\n",
        "PRINT_START EXTRUDER=230 BED=100 CHAMBER=40\n",
        "EXCLUDE_OBJECT_DEFINE NAME=part1 CENTER=1,1 POLYGON=[[0,0]]\n",
        "M204 S5000\n",
        "G10\n",
        "G1 X10 Y10\n",
        "G11\n",
        "M73 P50\n",
        "PRINT_END\n",
    ]
    assert check(good) == [], check(good)

    # Same file but klipper gcode flavour: SET_VELOCITY_LIMIT instead of M204.
    # This is what OrcaSlicer actually emits for this printer, and requiring
    # M204 wrongly failed a perfectly good file.
    good_klipper = [ln for ln in good if not ln.startswith("M204")]
    good_klipper.insert(3, "SET_VELOCITY_LIMIT ACCEL=5700 ACCEL_TO_DECEL=2850\n")
    assert check(good_klipper) == [], check(good_klipper)

    no_accel = [ln for ln in good if not ln.startswith("M204")]
    failures = check(no_accel)
    assert any("per-feature acceleration" in f for f in failures), failures

    bad_placeholder = [
        "PRINT_START EXTRUDER=[nozzle_temperature_initial_layer] BED=100\n",
    ]
    failures = check(bad_placeholder)
    assert any("unresolved" in f for f in failures), failures

    bad_heat_order = [
        "M109 S230\n",
        "PRINT_START EXTRUDER=230 BED=100\n",
        "G10\nG11\nEXCLUDE_OBJECT_DEFINE\nM204\nM73\n",
    ]
    failures = check(bad_heat_order)
    assert any("before PRINT_START" in f for f in failures), failures

    # Slicer retraction (the default since ADR-0021): G1 E- moves instead of G10/G11.
    good_slicer = [ln for ln in good if ln.strip() not in ("G10", "G11")]
    good_slicer.insert(5, "G1 E-.75 F2100\n")
    good_slicer.insert(7, "G1 E.8 F2100\n")
    assert check(good_slicer) == [], check(good_slicer)

    both = good + ["G1 E-.75 F2100\n"]
    failures = check(both)
    assert any("both retraction methods" in f for f in failures), failures

    missing = ["PRINT_START EXTRUDER=230 BED=100\n"]
    failures = check(missing)
    assert any("no retraction at all" in f for f in failures), failures

    print("selftest ok")


def main(argv):
    if argv[1:2] == ["--selftest"]:
        selftest()
        return 0

    if len(argv) != 2:
        print(__doc__)
        return 1

    with open(argv[1]) as f:
        lines = f.readlines()

    failures = check(lines)
    if not failures:
        print(f"{argv[1]}: all checks passed")
        return 0

    print(f"{argv[1]}: {len(failures)} check(s) failed")
    for f in failures:
        print(f"  - {f}")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
