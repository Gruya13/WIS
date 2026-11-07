#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Sinhronizuj-Usb.py - Kompletna, transakciona sinhronizacija WIS sistema na montirani USB fleš (v2.16.0).
Koristi jedinstveni kanonski manifest (`Skripte/deployment_manifest.json`), vrši pre-check svih obaveznih
izvora pre početka kopiranja, atomski upisuje fajlove preko privremenih .tmp datoteka i verifikuje
SHA-256 kontrolne sume svih komponenti na cilju (Nalaz 11).
"""

import os
import sys
import time
import fcntl
import shutil
import hashlib
import argparse
import subprocess
from pathlib import Path

_SCRIPT_DIR = Path(__file__).resolve().parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

from manifest_loader import (
    atomic_copy_file,
    sync_manifest_to_target,
    validate_manifest_sources,
)

CYAN = "\033[1;36m"
GREEN = "\033[1;32m"
YELLOW = "\033[1;33m"
RED = "\033[1;31m"
BOLD = "\033[1m"
RESET = "\033[0m"


def copy_tree_sync(src, dst):
    """Kopira dodatne direktorijume (npr. mrežni drajveri) atomski po fajlu, preskačući tajne."""
    os.makedirs(dst, exist_ok=True)
    count = 0
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        cur_dst = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(cur_dst, exist_ok=True)
        for f in files:
            sf = os.path.join(root, f)
            df = os.path.join(cur_dst, f)
            if (
                not os.path.exists(df)
                or os.path.getmtime(sf) > os.path.getmtime(df)
                or os.path.getsize(sf) != os.path.getsize(df)
            ):
                atomic_copy_file(sf, df)
                count += 1
    return count


def detect_mounted_usb():
    default_mount = Path("/run/media/gru/CCCOMA_X64FRE_EN-GB_DV9")
    if default_mount.exists() and (default_mount / "setup.exe").exists():
        return default_mount

    candidates = list(Path("/run/media/gru").glob("*")) + list(Path("/media/gru").glob("*"))
    for c in candidates:
        if c.is_dir() and (c / "setup.exe").exists():
            return c
    return None


def main(argv=None):
    parser = argparse.ArgumentParser(description="WIS Kanonska Sinhronizacija USB Medijuma (v2.16.0)")
    parser.add_argument("--target", help="Ciljna putanja montiranog USB medijuma (podrazumevano automatska detekcija)")
    parser.add_argument("--project-root", help="Putanja korena projekta (opciono)")
    parser.add_argument("--skip-drivers", action="store_true", help="Preskoči kopiranje glomaznih Drivers/Net stabala")
    args = parser.parse_args(argv)

    script_dir = Path(__file__).resolve().parent
    project_root = Path(args.project_root).resolve() if args.project_root else script_dir.parent

    usb_mount = Path(args.target).resolve() if args.target else detect_mounted_usb()
    if not usb_mount or not usb_mount.exists() or not usb_mount.is_dir():
        print(f"{RED}[GREŠKA] Nije pronađen montirani WIS USB fleš ({usb_mount})!{RESET}")
        return 1

    print(f"{CYAN}======================================================================{RESET}")
    print(f"{YELLOW}{BOLD}          WIS - KANONSKA SINHRONIZACIJA NA USB FLEŠ (v2.16.0)         {RESET}")
    print(f"{CYAN}======================================================================{RESET}")
    print(f"Izvorni projekat : {BOLD}{project_root}{RESET}")
    print(f"Ciljni USB medijum: {BOLD}{usb_mount}{RESET}")
    print(f"Vreme početka    : {time.strftime('%Y-%m-%d %H:%M:%S')}\n")

    start_time = time.time()

    real_target = os.path.realpath(str(usb_mount))
    target_hash = hashlib.sha256(real_target.encode("utf-8")).hexdigest()[:16]
    lock_file_path = Path("/tmp") / f"wis_usb_target_{target_hash}.lock"

    try:
        with open(lock_file_path, "a+", encoding="utf-8") as lf:
            try:
                fcntl.flock(lf.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                print(f"{RED}[GREŠKA] Ciljni USB medijum je zaključan od strane drugog procesa!{RESET}")
                return 1

            try:
                # 1. Pre-check svih obaveznih komponenti u manifestu
                print(f"{CYAN}[1/4] Provera kompletnosti izvornog WIS kompleta prema manifestu...{RESET}")
                plan = validate_manifest_sources(project_root=project_root)
                print(f"  {GREEN}✔{RESET} Svih {len(plan)} izvornih komponenti iz manifesta je prisutno i validno.")

                # 2. Atomska sinhronizacija i SHA-256 verifikacija svih komponenti iz manifesta
                print(f"\n{CYAN}[2/4] Atomska sinhronizacija i SHA-256 verifikacija manifest komponenti...{RESET}")

                def _cli_log(msg):
                    print(f"  {GREEN}✔{RESET} {msg}")

                sync_result = sync_manifest_to_target(
                    usb_root=usb_mount,
                    project_root=project_root,
                    log_fn=_cli_log,
                )

                # 3. Sinhronizacija dodatnih direktorijuma (mrežni drajveri i dokumentacija)
                print(f"\n{CYAN}[3/4] Sinhronizacija baze mrežnih drajvera i dokumentacije...{RESET}")
                if not args.skip_drivers:
                    directories_to_sync = [
                        ("sources/$OEM$/$1/setup/Drivers/Net", usb_mount / "sources" / "$OEM$" / "$1" / "setup" / "Drivers" / "Net"),
                        ("Programi/Drivers/Net", usb_mount / "Programi" / "Drivers" / "Net"),
                        ("wiki", usb_mount / "wiki"),
                    ]
                    for src_rel, dst_path in directories_to_sync:
                        src_path = project_root / src_rel
                        if src_path.exists() and src_path.is_dir():
                            print(f"  Prenos: {src_rel} -> {dst_path.relative_to(usb_mount)}...")
                            copied = copy_tree_sync(str(src_path), str(dst_path))
                            print(f"  {GREEN}✔{RESET} Uspešno sinhronizovano {copied} fajlova.")
                        else:
                            print(f"  {YELLOW}⚠{RESET} Opcioni direktorijum nije prisutan: {src_rel}")
                else:
                    print(f"  {YELLOW}ℹ{RESET} Preskočeno kopiranje glomaznih drajvera (--skip-drivers).")

                # 4. Pregled SHA-256 verifikacije ključnih ulaza i OS sync
                verified = sync_result["verified_files"]
                print(f"\n{CYAN}[4/4] Potvrda SHA-256 pariteta i fizički upis bafera (sync)...{RESET}")
                sample_keys = [
                    "autounattend.xml",
                    "SetupComplete.cmd",
                    "FirstLogon.cmd",
                    "FirstLogon.ps1",
                    "Skripte/Phases/Phase6-Finalize.ps1",
                    "Skripte/WIS.UI/WIS.UI.exe",
                    "WIS.Launcher.exe",
                ]
                for sk in sample_keys:
                    if sk in verified:
                        print(f"  {GREEN}✔{RESET} {sk:40s} | SHA-256: {verified[sk]['sha256'][:16]}... ({verified[sk]['size_bytes']} B)")

                subprocess.run(["sync"], check=True, timeout=30)
                print(f"  {GREEN}✔ Baferi su u potpunosti ispražnjeni na USB memoriju.{RESET}")

                elapsed = round(time.time() - start_time, 1)
                print(f"\n{CYAN}======================================================================{RESET}")
                print(f"{GREEN}{BOLD}  ✔ USB FLEŠ JE KOMPLETNO I TRANSAKCIONO AŽURIRAN ZA {elapsed} SEKUNDI!{RESET}")
                print(
                    f"  Verifikovano {sync_result['files_written']} ciljnih datoteka iz "
                    f"{sync_result['components_synced']} kanonskih komponenti (100% SHA-256 paritet)."
                )
                print(f"{CYAN}======================================================================{RESET}\n")
                return 0
            finally:
                try:
                    fcntl.flock(lf.fileno(), fcntl.LOCK_UN)
                except Exception:
                    pass

    except Exception as e:
        print(f"\n{RED}{BOLD}✖ GREŠKA TOKOM SINHRONIZACIJE: {e}{RESET}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
