#!/usr/bin/env python3
"""Install the repo's slicer presets straight into OrcaSlicer's and Cura's user folders.

    python3 scripts/install_slicer_presets.py            # Orca + Cura
    python3 scripts/install_slicer_presets.py --orca     # only OrcaSlicer
    python3 scripts/install_slicer_presets.py --cura     # only Cura
    python3 scripts/install_slicer_presets.py --dry-run  # show what would change

Why this exists: OrcaSlicer 2.4.2's "Import Configs" silently dropped the process preset
from the bundle three times (2026-09-19), while its own user presets are plain files
under user/default/{machine,filament,process}/. Writing those files directly is what
the GUI does when you click "save preset", so it is the reliable path. Cura's materials
are plain .xml.fdm_material files in its materials/ folder.

Both apps must be CLOSED: they hold presets in memory and rewrite the folder on exit.
Existing files with the same name are backed up next to the user folder before being
replaced. Sources: slicer_profiles/orcaslicer/src/ and slicer_profiles/cura/*.xml.fdm_material.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORCA_SRC = os.path.join(ROOT, "slicer_profiles", "orcaslicer", "src")
CURA_SRC = os.path.join(ROOT, "slicer_profiles", "cura")
HOME = os.path.expanduser("~")
ORCA_USER = os.path.join(HOME, ".var/app/com.orcaslicer.OrcaSlicer/config/OrcaSlicer/user/default")
CURA_DATA = os.path.join(HOME, ".var/app/com.ultimaker.cura/data/cura")
# OrcaSlicer stores a .info beside each user preset; base_id is the parent's setting_id.
BASE_IDS = {"Generic ABS @System": "W0LP8Ah5MHMzogvV", "0.20mm Standard @Voron": "woKQ9fsFs3B9vQCS"}
KIND_DIR = {"printer": "machine", "filament": "filament", "process": "process"}


def running(names):
    for n in names:
        if subprocess.run(["pgrep", "-x", n], capture_output=True).returncode == 0:
            return n
    return None


def backup(path, stamp, backup_root):
    """Copy an existing file to <backup_root>/backup-install-<stamp>/<kind>/ before replacing it."""
    if os.path.exists(path):
        dst = os.path.join(backup_root, f"backup-install-{stamp}", os.path.basename(os.path.dirname(path)))
        os.makedirs(dst, exist_ok=True)
        shutil.copy2(path, os.path.join(dst, os.path.basename(path)))


def install_orca(dry):
    if not os.path.isdir(ORCA_USER):
        print("OrcaSlicer user folder not found:", ORCA_USER)
        return 1
    if (n := running(["orca-slicer"])):
        print(f"ERROR: {n} is running; close OrcaSlicer first")
        return 1
    stamp = time.strftime("%Y%m%d-%H%M%S")
    for kind, sub in KIND_DIR.items():
        d = os.path.join(ORCA_SRC, kind)
        for fn in sorted(os.listdir(d)) if os.path.isdir(d) else []:
            if not fn.endswith(".json"):
                continue
            cfg = json.load(open(os.path.join(d, fn), encoding="utf-8"))
            cfg["from"] = "User"
            dst_dir = os.path.join(ORCA_USER, sub)
            dst = os.path.join(dst_dir, fn)
            info = dst[:-5] + ".info"
            base = BASE_IDS.get(cfg.get("inherits", ""), "")
            print(f"{'would write' if dry else 'write'} {os.path.relpath(dst, HOME)}")
            if dry:
                continue
            os.makedirs(dst_dir, exist_ok=True)
            backup(dst, stamp, os.path.dirname(ORCA_USER))  # .../user/backup-install-*
            with open(dst, "w", encoding="utf-8") as f:
                json.dump(cfg, f, indent="\t", ensure_ascii=False)
            with open(info, "w") as f:
                f.write(f"sync_info = update\nuser_id = \nsetting_id = \nbase_id = {base}\nupdated_time = 0\n")
    return 0


def install_cura(dry):
    # Only numeric version folders (5.13, 5.14 ...); backups and other folders live here too.
    versions = sorted((v for v in os.listdir(CURA_DATA) if re.fullmatch(r"\d+\.\d+", v) and os.path.isdir(os.path.join(CURA_DATA, v))),
                      key=lambda v: tuple(int(x) for x in v.split("."))) if os.path.isdir(CURA_DATA) else []
    if not versions:
        print("Cura data folder not found:", CURA_DATA)
        return 1
    if (n := running(["cura", "UltiMaker-Cura", "cura_app.py"])):
        print(f"ERROR: {n} is running; close Cura first")
        return 1
    mats = os.path.join(CURA_DATA, versions[-1], "materials")
    stamp = time.strftime("%Y%m%d-%H%M%S")
    for fn in sorted(os.listdir(CURA_SRC)):
        if not fn.endswith(".xml.fdm_material"):
            continue
        dst = os.path.join(mats, fn)
        print(f"{'would write' if dry else 'write'} {os.path.relpath(dst, HOME)}")
        if dry:
            continue
        os.makedirs(mats, exist_ok=True)
        backup(dst, stamp, os.path.join(CURA_DATA, versions[-1]))  # .../cura/<version>/backup-install-*
        shutil.copy2(os.path.join(CURA_SRC, fn), dst)
    return 0


def main(argv):
    dry = "--dry-run" in argv
    do_orca = "--cura" not in argv
    do_cura = "--orca" not in argv
    rc = 0
    if do_orca:
        rc |= install_orca(dry)
    if do_cura:
        rc |= install_cura(dry)
    sys.exit(rc)


if __name__ == "__main__":
    main(sys.argv[1:])
