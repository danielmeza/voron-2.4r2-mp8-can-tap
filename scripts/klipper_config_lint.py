#!/usr/bin/env python3
"""Offline sanity checker for this printer's modular Klipper config.

Klipper spreads one logical section (eg. [extruder]) across several files and
merges them with RawConfigParser(strict=False) -- duplicates are silently
accepted and the LAST loaded file wins.  That makes it easy to have two files
disagree about the same option and never notice.  This script replicates
Klipper's own load semantics and reports the disagreements.

Semantics mirrored from klippy/configfile.py:
  * '[include <glob>]' is resolved relative to the including file's directory
  * glob.glob() is NOT recursive, so '**' behaves exactly like '*'
  * matched filenames are sorted() before being read
  * everything from '#' to end-of-line is stripped before parsing
  * a missing wildcard match is silently ignored (only literal paths error)
  * the trailing SAVE_CONFIG block overrides everything above it

Usage:  scripts/klipper_config_lint.py [config/klippy.conf]
Exit code is 1 if any ERROR-level finding is present, else 0.
"""

import glob
import os
import re
import sys
from collections import defaultdict

AUTOSAVE_HEADER = "#*# <---------------------- SAVE_CONFIG ---------------------->"
SECT_RE = re.compile(r"\[(?P<header>[^]]+)\]\s*$")
OPT_RE = re.compile(r"(?P<key>[^:=]+?)\s*[:=]\s*(?P<value>.*)$")
# a pin token may carry pull-up/invert modifiers and an "MCU:" prefix
PIN_TOKEN_RE = re.compile(r"^[\^~!]*\s*(?:(?P<mcu>\w+)\s*:\s*)?(?P<pin>[\w.]+)$")

ERROR, WARN, INFO = "ERROR", "WARN", "INFO"


class Finding:
    def __init__(self, level, kind, message, locations=()):
        self.level, self.kind = level, kind
        self.message, self.locations = message, list(locations)


def strip_comments(line):
    """Klipper truncates each line at the first '#'; ';' is an inline comment."""
    pos = line.find("#")
    if pos >= 0:
        line = line[:pos]
    pos = line.find(";")
    if pos >= 0:
        line = line[:pos]
    return line


class ConfigLoader:
    """Walks the include tree recording where every option was assigned."""

    def __init__(self, root):
        self.root = os.path.abspath(root)
        # (section, option) -> list of (value, file, lineno)
        self.assignments = defaultdict(list)
        self.section_files = defaultdict(list)   # section -> [files]
        self.findings = []
        self.visited = set()
        self.file_order = []

    def load(self):
        self._parse_file(self.root)
        return self

    def _parse_file(self, path):
        path = os.path.abspath(path)
        if path in self.visited:
            self.findings.append(Finding(
                ERROR, "recursive-include",
                "recursive include of %s" % self._rel(path)))
            return
        self.visited.add(path)
        self.file_order.append(path)
        try:
            with open(path, encoding="utf-8", errors="replace") as fh:
                raw = fh.read().replace("\r\n", "\n")
        except OSError as exc:
            self.findings.append(Finding(
                ERROR, "unreadable", "cannot read %s: %s" % (self._rel(path), exc)))
            return
        # The SAVE_CONFIG block is parsed by Klipper as an overriding layer.
        body = raw.split(AUTOSAVE_HEADER)[0]
        self._parse_lines(body.split("\n"), path)

    def _parse_lines(self, lines, path):
        section = None
        last_key = None
        for lineno, raw_line in enumerate(lines, 1):
            line = strip_comments(raw_line)
            if not line.strip():
                # configparser defaults to empty_lines_in_values=True, so a
                # blank line (or a comment-only line, which Klipper blanks out)
                # does NOT terminate a multi-line value such as a macro body.
                continue
            if line[:1].isspace():
                # continuation of the previous option's value
                if section and last_key:
                    prev = self.assignments[(section, last_key)][-1]
                    self.assignments[(section, last_key)][-1] = (
                        prev[0] + "\n" + line.strip(), prev[1], prev[2])
                continue
            match = SECT_RE.match(line.strip())
            if match:
                header = " ".join(match.group("header").split())
                last_key = None
                if header.startswith("include "):
                    self._do_include(header[len("include "):].strip(), path, lineno)
                    section = None
                    continue
                section = header
                self.section_files[section].append((self._rel(path), lineno))
                continue
            opt = OPT_RE.match(line.strip())
            if opt and section is not None:
                key = opt.group("key").strip().lower()
                value = opt.group("value").strip()
                self.assignments[(section, key)].append(
                    (value, self._rel(path), lineno))
                last_key = key

    def _do_include(self, spec, source_path, lineno):
        include_glob = os.path.join(os.path.dirname(source_path), spec)
        matches = glob.glob(include_glob)          # deliberately NOT recursive
        if not matches:
            has_magic = glob.has_magic(include_glob)
            self.findings.append(Finding(
                ERROR if not has_magic else WARN,
                "dead-include",
                "[include %s] matches no files%s" % (
                    spec, "" if not has_magic else " (wildcard, so Klipper stays silent)"),
                [("%s:%d" % (self._rel(source_path), lineno))]))
            return
        for filename in sorted(matches):
            self._parse_file(filename)

    def _rel(self, path):
        base = os.path.dirname(os.path.dirname(self.root))
        try:
            return os.path.relpath(path, base)
        except ValueError:
            return path


def check_conflicts(loader):
    """Same option assigned more than once -> last-loaded silently wins."""
    for (section, option), places in sorted(loader.assignments.items()):
        if len(places) < 2:
            continue
        values = {p[0] for p in places}
        locs = ["%s:%d = %s" % (f, ln, v) for v, f, ln in places]
        if len(values) > 1:
            loader.findings.append(Finding(
                ERROR, "conflicting-duplicate",
                "[%s] %s defined %d times with DIFFERENT values; "
                "Klipper keeps the last one (%s)" % (
                    section, option, len(places), places[-1][0]),
                locs))
        else:
            loader.findings.append(Finding(
                INFO, "redundant-duplicate",
                "[%s] %s repeated %d times with the same value" % (
                    section, option, len(places)),
                locs))


def build_alias_maps(loader):
    """mcu name -> {ALIAS: value} from every [board_pins ...] section."""
    maps = {}
    for (section, option), places in loader.assignments.items():
        if not section.startswith("board_pins"):
            continue
        if not option.startswith("aliases"):
            continue
        mcu = "mcu"
        board = section.split(None, 1)[1] if " " in section else ""
        mcu_opt = loader.assignments.get((section, "mcu"))
        if mcu_opt:
            mcu = mcu_opt[-1][0]
        elif board:
            mcu = board
        entry = maps.setdefault(mcu, {})
        for value, filename, lineno in places:
            for chunk in value.replace("\n", ",").split(","):
                chunk = chunk.strip()
                if not chunk or "=" not in chunk:
                    continue
                name, _, target = chunk.partition("=")
                entry[name.strip()] = (target.strip(), filename, lineno)
    return maps


def check_aliases(loader, alias_maps):
    """An alias whose target is neither a real pin nor another alias."""
    for mcu, entries in sorted(alias_maps.items()):
        for name, (target, filename, lineno) in sorted(entries.items()):
            if not target:
                continue
            if re.match(r"^(gpio\d+|P[A-Z]\d+|PIN_\d+|\d+)$", target):
                continue          # looks like a raw pin
            if re.match(r"^<.*>$", target):
                continue          # <GND>/<5V>/<NC>/<RST> header placeholders
            if target in entries:
                continue          # points at a sibling alias
            loader.findings.append(Finding(
                ERROR, "unresolved-alias",
                "[board_pins %s] alias %s=%s -- '%s' is not a raw pin and not a "
                "defined alias on this board" % (mcu, name, target, target),
                ["%s:%d" % (filename, lineno)]))


def check_pin_reuse(loader, alias_maps):
    """Two sections driving the same physical pin."""
    usage = defaultdict(list)
    for (section, option), places in loader.assignments.items():
        if section.startswith("board_pins"):
            continue
        if not (option == "pin" or option.endswith("_pin")):
            continue
        # shared buses are legitimately reused
        if option.endswith(("uart_pin", "sclk_pin", "mosi_pin", "miso_pin")):
            continue
        value, filename, lineno = places[-1]
        if not value or ":" in value and value.split(":")[0].strip() in ("probe", "virtual"):
            continue
        token = PIN_TOKEN_RE.match(value.strip())
        if not token:
            continue
        mcu = token.group("mcu") or "mcu"
        pin = token.group("pin")
        resolved, seen = pin, set()
        entries = alias_maps.get(mcu, {})
        while resolved in entries and resolved not in seen:
            seen.add(resolved)
            resolved = entries[resolved][0]
        usage[(mcu, resolved)].append((section, option, filename, lineno))
    for (mcu, pin), users in sorted(usage.items()):
        sections = {u[0] for u in users}
        if len(sections) > 1:
            loader.findings.append(Finding(
                INFO, "pin-reuse",
                "%s pin %s referenced by %d different sections" % (mcu, pin, len(sections)),
                ["%s:%d [%s] %s" % (f, ln, s, o) for s, o, f, ln in users]))


def main(argv):
    root = argv[1] if len(argv) > 1 else os.path.join("config", "klippy.conf")
    if not os.path.exists(root):
        print("config root not found: %s" % root, file=sys.stderr)
        return 2
    loader = ConfigLoader(root).load()
    alias_maps = build_alias_maps(loader)
    check_conflicts(loader)
    check_aliases(loader, alias_maps)
    check_pin_reuse(loader, alias_maps)

    order = {ERROR: 0, WARN: 1, INFO: 2}
    findings = sorted(loader.findings, key=lambda f: (order[f.level], f.kind, f.message))
    counts = defaultdict(int)
    for finding in findings:
        counts[finding.level] += 1

    print("parsed %d config files from %s\n" % (len(loader.file_order), root))
    for finding in findings:
        print("%-5s %-22s %s" % (finding.level, finding.kind, finding.message))
        for loc in finding.locations:
            print("          %s" % loc)
        print()
    print("%d error(s), %d warning(s), %d info" % (
        counts[ERROR], counts[WARN], counts[INFO]))
    return 1 if counts[ERROR] else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
