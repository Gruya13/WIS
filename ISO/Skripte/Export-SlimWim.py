#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
================================================================================
GWADT - Export-SlimWim.py
Optimizacija i svođenje install.wim na Dual-Edition Slim WIM (Windows 11 Home + Windows 11 Pro)
GWADT-TASK-053 - Korak 2 [gwadt_module_architect]

Ova skripta:
1. Dinamički skenira 'sources/install.wim' i pronalazi tačne indekse za:
   - 'Windows 11 Home' (npr. Index 1)
   - 'Windows 11 Pro'  (npr. Index 6)
2. Izvozi 'Windows 11 Home' u privremeni fajl 'sources/install_slim.wim' uz --compress=maximum (LZX).
3. Izvozi 'Windows 11 Pro' i pridružuje ga u 'sources/install_slim.wim' uz automatsku deduplikaciju.
4. Premesti originalni 'sources/install.wim' u 'sources/install.wim.bak' (ako već ne postoji).
5. Postavlja 'sources/install_slim.wim' kao novi zvanični 'sources/install.wim'.
6. Verifikuje da nova slika ima tačno 2 indeksa (Index 1: Windows 11 Home, Index 2: Windows 11 Pro).
7. Meri uštedu prostora u poređenju sa originalnom 11-edicijskom slikom (~7.58 GB).
================================================================================
"""

import sys
import os
import shutil
import time
import subprocess
import argparse
from pathlib import Path

# ANSI boje za stilizovani TUI prikaz
CYAN = "\033[1;36m"
GREEN = "\033[1;32m"
YELLOW = "\033[1;33m"
RED = "\033[1;31m"
BOLD = "\033[1m"
MAGENTA = "\033[1;35m"
WHITE = "\033[1;37m"
RESET = "\033[0m"


def print_banner():
    print(f"{CYAN}================================================================================{RESET}")
    print(f"{BOLD}{WHITE}   GWADT - EXPORT DUAL-EDITION SLIM WIM TOOL                                    {RESET}")
    print(f"{MAGENTA}          Svođenje install.wim na Windows 11 Home + Windows 11 Pro              {RESET}")
    print(f"{CYAN}================================================================================{RESET}\n")


def format_bytes(byte_count: int) -> str:
    """Formatira broj bajtova u GB (dekadni) i GiB (binarni) radi preciznosti."""
    gb = byte_count / (1000 ** 3)
    gib = byte_count / (1024 ** 3)
    return f"{gb:.2f} GB ({gib:.2f} GiB / {byte_count:,} bajtova)"


def check_prerequisites():
    """Proverava dostupnost alata wimlib-imagex u sistemu."""
    tool = shutil.which("wimlib-imagex")
    if not tool:
        print(f"{RED}[GREŠKA] 'wimlib-imagex' nije pronađen u sistemu! Instalirajte paket wimtools.{RESET}")
        sys.exit(1)
    return tool


def get_wim_info(wim_path: Path):
    """
    Očitava metapodatke iz WIM fajla preko wimlib-imagex info.
    Vraća ukupan broj slika i rečnik sa nazivima indeksa.
    """
    if not wim_path.is_file():
        raise FileNotFoundError(f"WIM fajl ne postoji: {wim_path}")

    cmd = ["wimlib-imagex", "info", str(wim_path)]
    result = subprocess.run(cmd, capture_output=True, text=True, check=True)

    images = {}
    current_idx = None
    image_count = None

    for line in result.stdout.splitlines():
        stripped = line.strip()
        if stripped.startswith("Image Count:"):
            parts = stripped.split(":", 1)
            image_count = int(parts[1].strip())
        elif stripped.startswith("Index:"):
            parts = stripped.split(":", 1)
            current_idx = int(parts[1].strip())
        elif stripped.startswith("Name:") and current_idx is not None:
            parts = stripped.split(":", 1)
            images[current_idx] = parts[1].strip()
            current_idx = None

    return {
        "image_count": image_count,
        "images": images,
        "raw_info": result.stdout
    }


def find_edition_indices(images: dict):
    """Pronalazi indekse za 'Windows 11 Home' i 'Windows 11 Pro'."""
    home_idx = None
    pro_idx = None

    for idx, name in images.items():
        if name == "Windows 11 Home":
            home_idx = idx
        elif name == "Windows 11 Pro":
            pro_idx = idx

    return home_idx, pro_idx


def export_edition(source_wim: Path, index: int, edition_name: str, target_wim: Path, step_desc: str):
    """Izvozi pojedinačnu ediciju u ciljni WIM fajl uz maksimalnu kompresiju."""
    print(f"\n{BOLD}{CYAN}>>> {step_desc} (Index {index}: '{edition_name}'){RESET}")
    print(f"{WHITE}    Izvorni WIM: {source_wim}{RESET}")
    print(f"{WHITE}    Ciljni WIM:  {target_wim}{RESET}")
    print(f"{WHITE}    Kompresija:  maximum (LZX){RESET}\n")

    cmd = [
        "wimlib-imagex",
        "export",
        str(source_wim),
        str(index),
        str(target_wim),
        "--compress=maximum"
    ]

    start_time = time.time()
    proc = subprocess.Popen(cmd, stdout=sys.stdout, stderr=sys.stderr)
    retcode = proc.wait()
    duration = time.time() - start_time

    if retcode != 0:
        raise RuntimeError(f"Neuspešan izvoz edicije {edition_name} (Index {index})! Exit kod: {retcode}")

    print(f"{GREEN}[✓] Uspešno izvezen {edition_name} za {duration:.1f} sekundi.{RESET}")


def run_slim_export(source_wim: Path, temp_wim: Path, backup_wim: Path, force: bool = False):
    """Glavni tok izvoza i svođenja na Dual-Edition WIM."""
    start_total_time = time.time()
    check_prerequisites()

    if not source_wim.exists():
        # Provera da li već postoji backup i slim
        if backup_wim.exists() and source_wim.exists():
            pass
        else:
            raise FileNotFoundError(f"Izvorni fajl '{source_wim}' nije pronađen!")

    # Provera postojećeg stanja
    info = get_wim_info(source_wim)
    print(f"{WHITE}[*] Detektovan izvorni WIM: {source_wim}{RESET}")
    print(f"{WHITE}    Ukupno slika: {info['image_count']}{RESET}")
    for idx, name in info["images"].items():
        print(f"    - Index {idx:2d}: {name}")

    # Provera da li je već 2-edicijski WIM
    if info["image_count"] == 2 and not force:
        home_check, pro_check = find_edition_indices(info["images"])
        if home_check is not None and pro_check is not None:
            print(f"\n{YELLOW}[!] WIM '{source_wim}' već sadrži tačno 2 edicije (Home + Pro)!{RESET}")
            if backup_wim.exists():
                print(f"{WHITE}    Backup kopija postoji: {backup_wim}{RESET}")
            else:
                print(f"{YELLOW}    UPOZORENJE: Backup kopija '{backup_wim}' ne postoji!{RESET}")
            return

    # Ako je izvorni već slim a postoji backup sa više slika, prebaci izvor na backup
    actual_source = source_wim
    if info["image_count"] == 2 and backup_wim.exists():
        bak_info = get_wim_info(backup_wim)
        if bak_info["image_count"] and bak_info["image_count"] > 2:
            print(f"{YELLOW}[*] Koristim backup '{backup_wim}' sa {bak_info['image_count']} edicija kao izvor.{RESET}")
            actual_source = backup_wim
            info = bak_info

    home_idx, pro_idx = find_edition_indices(info["images"])

    if home_idx is None:
        raise ValueError("Edicija 'Windows 11 Home' nije pronađena u izvornoj slici!")
    if pro_idx is None:
        raise ValueError("Edicija 'Windows 11 Pro' nije pronađena u izvornoj slici!")

    print(f"\n{GREEN}[+] Pronađeni indeksi za izvoz:{RESET}")
    print(f"    - Windows 11 Home: {BOLD}Index {home_idx}{RESET}")
    print(f"    - Windows 11 Pro:  {BOLD}Index {pro_idx}{RESET}")

    # Beleženje originalne veličine
    original_size = actual_source.stat().st_size
    print(f"\n{WHITE}[*] Originalna veličina: {format_bytes(original_size)}{RESET}")

    # Čišćenje privremenog fajla ako je zaostao
    if temp_wim.exists():
        print(f"{YELLOW}[!] Uklanjam prethodni privremeni fajl: {temp_wim}{RESET}")
        temp_wim.unlink()

    # KORAK A: Izvoz Windows 11 Home (Index 1 u novom WIM-u)
    export_edition(
        source_wim=actual_source,
        index=home_idx,
        edition_name="Windows 11 Home",
        target_wim=temp_wim,
        step_desc="KORAK 1/2: Izvoz Windows 11 Home"
    )

    # KORAK B: Izvoz Windows 11 Pro (Index 2 u novom WIM-u)
    export_edition(
        source_wim=actual_source,
        index=pro_idx,
        edition_name="Windows 11 Pro",
        target_wim=temp_wim,
        step_desc="KORAK 2/2: Izvoz Windows 11 Pro (Deduplikacija i spajanje)"
    )

    # KORAK C: Pravljenje rezervne kopije originalnog install.wim ako ne postoji
    if not backup_wim.exists():
        print(f"\n{CYAN}[+] Arhiviram originalni WIM u: {backup_wim}{RESET}")
        shutil.move(str(source_wim), str(backup_wim))
    else:
        print(f"\n{YELLOW}[!] Rezervna kopija '{backup_wim}' već postoji. Original ostaje arhiviran.{RESET}")
        if source_wim.exists() and source_wim != actual_source:
            source_wim.unlink()

    # KORAK D: Premeštanje novog Slim WIM-a na install.wim
    print(f"{CYAN}[+] Postavljam novi Dual-Edition WIM na zvaničnu putanju: {source_wim}{RESET}")
    shutil.move(str(temp_wim), str(source_wim))

    # KORAK E: Verifikacija novog WIM fajla
    print(f"\n{BOLD}{CYAN}=== VERIFIKACIJA NOVE SLIKE (wimlib-imagex info) ==={RESET}")
    new_info = get_wim_info(source_wim)
    print(f"{WHITE}Ukupan broj slika (Image Count): {BOLD}{new_info['image_count']}{RESET}")
    for idx, name in new_info["images"].items():
        print(f"  * Index {idx}: {name}")

    if new_info["image_count"] != 2:
        raise RuntimeError(f"Greška verifikacije: Očekivano je tačno 2 edicije, a pronađeno {new_info['image_count']}!")

    if new_info["images"].get(1) != "Windows 11 Home":
        raise RuntimeError(f"Index 1 mora biti 'Windows 11 Home', a detektovan je: {new_info['images'].get(1)}")

    if new_info["images"].get(2) != "Windows 11 Pro":
        raise RuntimeError(f"Index 2 mora biti 'Windows 11 Pro', a detektovan je: {new_info['images'].get(2)}")

    print(f"{GREEN}[✓] Verifikacija strukture 100% USPEŠNA! (Index 1: Home, Index 2: Pro){RESET}")

    # KORAK F: Merenje uštede
    new_size = source_wim.stat().st_size
    diff_bytes = original_size - new_size
    diff_percent = (diff_bytes / original_size) * 100
    total_duration = time.time() - start_total_time

    print(f"\n{BOLD}{GREEN}================================================================================{RESET}")
    print(f"{BOLD}{GREEN}                      REZULTATI OPTIMIZACIJE I UŠTEDA                           {RESET}")
    print(f"{BOLD}{GREEN}================================================================================{RESET}")
    print(f"  * Stara veličina (11 edicija): {format_bytes(original_size)}")
    print(f"  * Nova veličina  (2 edicije):  {format_bytes(new_size)}")
    print(f"  * Ušteđeno prostora:           {format_bytes(diff_bytes)} ({diff_percent:.2f}% uštede)")
    print(f"  * Vreme izvršavanja:           {total_duration:.1f} sekundi")
    print(f"{BOLD}{GREEN}================================================================================{RESET}\n")


def main():
    print_banner()

    parser = argparse.ArgumentParser(
        description="GWADT Export-SlimWim - Svođenje install.wim na Dual-Edition (Home + Pro)"
    )
    project_root = Path(__file__).resolve().parent.parent
    default_source = project_root / "sources" / "install.wim"
    default_temp = project_root / "sources" / "install_slim.wim"
    default_backup = project_root / "sources" / "install.wim.bak"

    parser.add_argument("--source", type=Path, default=default_source, help="Putanja do izvornog install.wim")
    parser.add_argument("--temp", type=Path, default=default_temp, help="Putanja do privremenog install_slim.wim")
    parser.add_argument("--backup", type=Path, default=default_backup, help="Putanja do backup fajla install.wim.bak")
    parser.add_argument("--force", action="store_true", help="Forsiraj ponovni izvoz čak i ako već ima 2 edicije")

    args = parser.parse_args()

    try:
        run_slim_export(
            source_wim=args.source,
            temp_wim=args.temp,
            backup_wim=args.backup,
            force=args.force
        )
    except Exception as e:
        print(f"\n{RED}[FATALNA GREŠKA] {e}{RESET}")
        sys.exit(1)


if __name__ == "__main__":
    main()
