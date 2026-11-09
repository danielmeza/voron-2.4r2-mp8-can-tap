#!/usr/bin/env python3
"""Build (or check, or extract) the OrcaSlicer preset bundle for this printer.

    python3 scripts/build_orca_bundle.py            # rebuild the bundle from src/
    python3 scripts/build_orca_bundle.py --check    # exit 1 if the bundle is stale vs src/
    python3 scripts/build_orca_bundle.py --extract BUNDLE DIR
                                                     # dump any .orca_printer/.orca_filament
                                                     # into DIR (to diff an Orca export vs src/)

Sources live in slicer_profiles/orcaslicer/src/{printer,filament,process}/<name>.json,
one preset per file, file stem == preset "name". All three are deltas with "inherits",
exactly the shape OrcaSlicer stores in its own user/ directory (a full 158-key export
worked through the importer, which turned it into a delta, but installed as-is it made
Orca error when the printer was selected -- 2026-09-24). The bundle is a zip with
bundle_structure.json listing printer_config / filament_config / process_config, the
layout OrcaSlicer's own "Export Preset Bundle" produces. The zip is written with fixed
timestamps so a rebuild with unchanged sources is byte-identical (no git churn).

See slicer_profiles/README.md and docs/adr/0012 (revision 2026-09-19).
"""
import io
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "slicer_profiles", "orcaslicer", "src")
BUNDLE = os.path.join(ROOT, "slicer_profiles", "orcaslicer", "voron_2.4r2_350_can.orca_printer")
KINDS = ("printer", "filament", "process")
FIXED_TIME = (2026, 1, 1, 0, 0, 0)


def load_sources():
    """Return {kind: [(name, dict)]}, validating the invariants the importer relies on."""
    out = {}
    errors = []
    for kind in KINDS:
        d = os.path.join(SRC, kind)
        items = []
        for fn in sorted(os.listdir(d)) if os.path.isdir(d) else []:
            if not fn.endswith(".json"):
                continue
            stem = fn[:-5]
            with open(os.path.join(d, fn), encoding="utf-8") as f:
                cfg = json.load(f)
            if cfg.get("name") != stem:
                errors.append(f"{kind}/{fn}: 'name' is {cfg.get('name')!r}, must equal the file stem")
            # OrcaSlicer's importer ignores folders and bundle_structure lists: it routes each
            # JSON by which *_settings_id key it carries (PresetBundle::import_json_presets),
            # so the key must exist and must match the preset name.
            idkey = {"printer": "printer_settings_id", "filament": "filament_settings_id",
                     "process": "print_settings_id"}[kind]
            idval = cfg.get(idkey)
            if isinstance(idval, list):
                idval = idval[0] if idval else None
            if idval != stem:
                errors.append(f"{kind}/{fn}: {idkey} is {idval!r}, must equal the preset name {stem!r}")
            if kind != "printer" and not cfg.get("inherits"):
                errors.append(f"{kind}/{fn}: delta preset needs an 'inherits' parent")
            items.append((stem, cfg))
        out[kind] = items
    if len(out["printer"]) != 1:
        errors.append(f"expected exactly one printer preset, found {len(out['printer'])}")
    else:
        pname, pcfg = out["printer"][0]
        for k in ("machine_start_gcode", "machine_end_gcode", "use_firmware_retraction"):
            if k not in pcfg:
                errors.append(f"printer/{pname}.json: missing {k}")
        sg = pcfg.get("machine_start_gcode", "")
        if any(t in sg for t in ("M190", "M109", "M104", "M140")):
            errors.append("printer start gcode heats before PRINT_START (docs/SLICER_SETUP.md section 6)")
        fw = str(pcfg.get("use_firmware_retraction")) in ("1", "['1']")
        wipe_on = str(pcfg.get("wipe")) in ("['1']", "1")
        if fw and wipe_on:
            errors.append("printer 'wipe' must be 0 while use_firmware_retraction is 1 (OrcaSlicer refuses the pair)")
        if not fw and not wipe_on:
            errors.append("printer: slicer retraction without wipe -- ADR-0021 chose slicer retraction *for* the wipe; set wipe 1 or explain")
        # Compatibility, copied from the user presets that demonstrably worked in the GUI
        # (2026-07 .. 09): a PROCESS delta declares no compatible_printers at all and
        # inherits its parent's list; a FILAMENT delta lists only the SYSTEM printer the
        # repo printer inherits from. Listing the user printer's own name hid the presets
        # in the GUI (2026-09-24) and did not fix the importer either.
        parent = pcfg.get("inherits", "")
        for name, cfg in out["process"]:
            if cfg.get("compatible_printers"):
                errors.append(f"process/{name}.json: drop compatible_printers; inherit the parent's list")
        for name, cfg in out["filament"]:
            comp = cfg.get("compatible_printers") or []
            if comp != [parent]:
                errors.append(f"filament/{name}.json: compatible_printers must be exactly [{parent!r}]")
    if errors:
        for e in errors:
            print("ERROR:", e)
        sys.exit(1)
    return out


def render(sources):
    """Return {path_in_zip: bytes} for the bundle."""
    pname = sources["printer"][0][0]
    structure = {
        "bundle_id": f"_{pname}_repo",
        "bundle_type": "printer config bundle",
        "printer_preset_name": pname,
        "printer_config": [],
        "filament_config": [],
        "process_config": [],
        "version": "00.00.00.00",
    }
    files = {}
    for kind in KINDS:
        for name, cfg in sources[kind]:
            path = f"{kind}/{name}.json"
            structure[f"{kind}_config"].append(path)
            files[path] = (json.dumps(cfg, indent="\t", ensure_ascii=False) + "\n").encode("utf-8")
    files["bundle_structure.json"] = json.dumps(structure, separators=(",", ":")).encode("utf-8")
    return files


def write_zip(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for path in ["bundle_structure.json"] + sorted(p for p in files if p != "bundle_structure.json"):
            info = zipfile.ZipInfo(path, date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, files[path])
    return buf.getvalue()


def read_zip(path):
    with zipfile.ZipFile(path) as z:
        return {n: z.read(n) for n in z.namelist()}


def cmd_build():
    data = write_zip(render(load_sources()))
    with open(BUNDLE, "wb") as f:
        f.write(data)
    print(f"wrote {os.path.relpath(BUNDLE, ROOT)} ({len(data)} bytes)")
    for line in describe(read_zip(BUNDLE)):
        print(" ", line)


def cmd_check():
    want = render(load_sources())
    if not os.path.exists(BUNDLE):
        print("ERROR: bundle missing; run scripts/build_orca_bundle.py")
        sys.exit(1)
    have = read_zip(BUNDLE)
    stale = [p for p in set(want) | set(have) if want.get(p) != have.get(p)]
    if stale:
        print("ERROR: bundle is stale for:", ", ".join(sorted(stale)))
        print("       run: python3 scripts/build_orca_bundle.py")
        sys.exit(1)
    print("bundle matches src/")


def describe(files):
    s = json.loads(files["bundle_structure.json"])
    yield f"printer : {s['printer_preset_name']}"
    for kind in ("filament", "process"):
        for p in s.get(f"{kind}_config", []):
            yield f"{kind:8}: {os.path.basename(p)[:-5]}"


def cmd_extract(bundle, outdir):
    files = read_zip(bundle)
    for path, data in files.items():
        dst = os.path.join(outdir, path)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, "wb") as f:
            f.write(data)
        print("extracted", dst)


def main(argv):
    if argv[1:] == ["--check"]:
        cmd_check()
    elif argv[1:2] == ["--extract"] and len(argv) == 4:
        cmd_extract(argv[2], argv[3])
    elif argv[1:] == []:
        cmd_build()
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main(sys.argv)
