#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GWADT Linux WIM Auto-Refresh & Pre-Stage Tool (Direct Microsoft Refresh & Hotplug Sync)
100% autonomno osvežavanje Windows 11 WIM slike sa Linux radne stanice bez potrebe za Windows OS-om.

Mogućnosti:
1. Lokalni Pre-Stage Keš:
   - Računar unapred preuzima, ekstrahuje Pro ediciju i sabija sliku na lokalnom disku (/home/gru/Storage/gwadt_staging).
   - Fleš disk NIJE potreban u računaru tokom duge pripreme!
2. USB Hotplug Watcher:
   - Čeka ubacivanje fleša u računar, automatski prepoznaje USB i za samo ~60s kopira pripremljeni WIM uz zvučni signal.
3. Direktan uvoz i fabrička optimizacija iz zvaničnog ISO ili ESD fajla (--iso / --esd / --download-url):
   - Izvozi i maksimalno sabija ediciju 'Windows 11 Pro' (Compress=max).
   - Smanjuje WIM sa 7.5 GB na ~4.3 GB uz fabrički upečen najnoviji build.
4. Nativna provera Build broja sa Microsoft serverima (--check-only).
"""

import sys
import os
import uuid
import fcntl
import shutil
import struct
import hashlib
import json
import re
import time
import urllib.request
import urllib.parse
import subprocess
import argparse
import xml.etree.ElementTree as ET
from pathlib import Path

_SCRIPT_DIR = Path(__file__).resolve().parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

from manifest_loader import (
    calc_sha256,
    sync_manifest_to_target,
    validate_target_space_and_fs,
)

# ANSI boje za TUI prikaz
CYAN = "\033[1;36m"
GREEN = "\033[1;32m"
YELLOW = "\033[1;33m"
RED = "\033[1;31m"
BOLD = "\033[1m"
GRAY = "\033[0;90m"
RESET = "\033[0m"

REQUIRED_TOOLS = ["wimlib-imagex"]

def print_banner():
    print(f"{CYAN}================================================================================{RESET}")
    print(f"{YELLOW}   GWADT - LINUX DIRECT REFRESH, PRE-STAGE & HOTPLUG SYNC TOOL          {RESET}")
    print(f"{CYAN}          100% Autonomno osvežavanje bez zavisnosti od Windows mašine           {RESET}")
    print(f"{CYAN}================================================================================{RESET}\n")

def read_wim_metadata(wim_path):
    """
    Nativno čitanje WIM zaglavlja i XML metapodataka bez eksternih biblioteka.
    Vraća listu edicija sa verzijama i podacima o build-u.
    """
    if not os.path.isfile(wim_path):
        return None

    try:
        with open(wim_path, "rb") as f:
            header = f.read(208)
            if len(header) < 208 or header[:8] != b"MSWIM\0\0\0":
                return None
            
            rh_xml = header[0x48:0x48 + 24]
            flags_and_size, offset, orig_size = struct.unpack("<QQQ", rh_xml)
            size = flags_and_size & 0x00FFFFFFFFFFFFFF
            
            if size == 0 or offset == 0:
                return None
            
            f.seek(offset)
            xml_bytes = f.read(size)
            
            try:
                xml_str = xml_bytes.decode("utf-16le")
            except Exception:
                xml_str = xml_bytes.decode("utf-8", errors="ignore")
            
            root = ET.fromstring(xml_str)
            editions = []
            
            for img in root.findall("IMAGE"):
                idx = int(img.attrib.get("INDEX", "0"))
                name = img.findtext("NAME", "Unknown")
                ver = img.find("WINDOWS/VERSION")
                major = int(ver.findtext("MAJOR", "10")) if ver is not None else 10
                minor = int(ver.findtext("MINOR", "0")) if ver is not None else 0
                build = int(ver.findtext("BUILD", "0")) if ver is not None else 0
                spbuild = int(ver.findtext("SPBUILD", "0")) if ver is not None else 0
                
                editions.append({
                    "index": idx,
                    "name": name,
                    "major": major,
                    "minor": minor,
                    "build": build,
                    "spbuild": spbuild,
                    "version_str": f"{major}.{minor}.{build}.{spbuild}"
                })
            
            return editions
    except Exception as e:
        print(f"{RED}[GRESKA] Neuspešno očitavanje WIM fajla: {e}{RESET}")
        return None

def check_missing_tools(tools_list):
    """Proverava dostupnost sistemskih CLI alata."""
    return [tool for tool in tools_list if shutil.which(tool) is None]

def get_best_storage_dir():
    """Bira lokalni disk sa najviše slobodnog prostora (min. 20 GB)."""
    candidates = [
        Path("/home/gru/Storage/gwadt_refresh_work"),
        Path.home() / "gwadt_refresh_work",
        Path.home() / "Downloads" / "gwadt_refresh_work",
        Path("/tmp/gwadt_refresh_work")
    ]
    best_dir = None
    max_free = 0
    for cand in candidates:
        try:
            parent = cand.parent if not cand.exists() else cand
            if not parent.exists():
                parent = parent.parent
            usage = shutil.disk_usage(parent)
            free_gb = usage.free / (1024 ** 3)
            if free_gb > max_free:
                max_free = free_gb
                best_dir = cand
        except Exception:
            continue
    return best_dir, max_free

def get_staging_dir():
    """Direktorijum za lokalni pre-stage keš WIM slike na računaru."""
    storage_base = Path("/home/gru/Storage")
    if storage_base.exists():
        staging_path = storage_base / "gwadt_staging"
    else:
        staging_path = Path.home() / "gwadt_staging"
    staging_path.mkdir(parents=True, exist_ok=True)
    return staging_path

def get_staging_meta_file():
    return get_staging_dir() / "staging_meta.json"

def save_staging_metadata(meta):
    try:
        with open(get_staging_meta_file(), "w", encoding="utf-8") as f:
            json.dump(meta, f, indent=2, ensure_ascii=False)
    except Exception as e:
        print(f"{YELLOW}[UPOZORENJE] Nije moguće sačuvati staging metapodatke: {e}{RESET}")

def load_staging_metadata():
    mf = get_staging_meta_file()
    if mf.exists():
        try:
            with open(mf, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return None
    return None

def detect_usb_flash():
    """
    Traži aktivno montiran USB fleš medijum koji sadrži 'sources/install.wim'.
    Vraća Path do install.wim na flešu ili None.
    """
    # 1. Provera /run/media/gru/*
    media_root = Path("/run/media/gru")
    if media_root.exists():
        for m in media_root.iterdir():
            wim = m / "sources" / "install.wim"
            if wim.exists():
                return wim
    
    # 2. Provera /media/gru/*
    media_root_legacy = Path("/media/gru")
    if media_root_legacy.exists():
        for m in media_root_legacy.iterdir():
            wim = m / "sources" / "install.wim"
            if wim.exists():
                return wim

    # 3. Provera projektnog foldera ako je fleš montiran u koren projekta
    proj_wim = Path(__file__).resolve().parent.parent / "sources" / "install.wim"
    if proj_wim.exists() and (proj_wim.parent.parent / "autorun.inf").exists():
        return proj_wim

    return None

def mount_iso_udisks(iso_path):
    """
    Montira ISO bez sudo privilegija koristeći udisksctl loop-setup.
    Vraća (mount_dir, loop_dev).
    """
    cmd = ["udisksctl", "loop-setup", "-r", "-f", str(iso_path)]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError(f"Neuspešan loop-setup za ISO: {res.stderr}")
    
    m = re.search(r"(/dev/loop\d+)", res.stdout)
    if not m:
        raise RuntimeError(f"Nije prepoznat loop uređaj iz izlaza: {res.stdout}")
    loop_dev = m.group(1)
    
    mount_cmd = ["udisksctl", "mount", "-b", loop_dev]
    m_res = subprocess.run(mount_cmd, capture_output=True, text=True)
    if m_res.returncode != 0:
        subprocess.run(["udisksctl", "loop-delete", "-b", loop_dev], capture_output=True)
        raise RuntimeError(f"Neuspešno montiranje loop uređaja {loop_dev}: {m_res.stderr}")
    
    mm = re.search(r"at\s+(/run/media/\S+|/media/\S+)", m_res.stdout)
    if not mm:
        raise RuntimeError(f"Nije pronađena mount putanja iz: {m_res.stdout}")
    mount_dir = Path(mm.group(1).strip("."))
    return mount_dir, loop_dev

def unmount_iso_udisks(loop_dev):
    """Bezbedno demontira i oslobađa loop uređaj."""
    try:
        subprocess.run(["udisksctl", "unmount", "-b", loop_dev], capture_output=True)
    except Exception:
        pass
    try:
        subprocess.run(["udisksctl", "loop-delete", "-b", loop_dev], capture_output=True)
    except Exception:
        pass

def prepare_local_staging(source_image, edition_target="Windows 11 Pro"):
    """
    Ekstrahuje i maksimalno kompresuje Windows 11 Pro ediciju direktno
    u lokalni staging folder na disku računara (/home/gru/Storage/gwadt_staging)
    koristeći unikatni privremeni fajl pokušaja i međuprocesni lock pri promociji (Nalaz 12).
    """
    staging_dir = get_staging_dir()
    target_wim = staging_dir / "install.wim"
    attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
    tmp_wim = staging_dir / f"install_building_cli_{attempt_id}.wim"

    print(f"\n{CYAN}[PRE-STAGE KORAK 1/2]{RESET} Analiziram izvornu sliku: {BOLD}{source_image.name}{RESET}...")
    info_proc = subprocess.run(["wimlib-imagex", "info", str(source_image)], capture_output=True, text=True)
    if info_proc.returncode != 0:
        raise RuntimeError(f"wimlib-imagex info greška: {info_proc.stderr}")

    print(f"{GREEN}[OK]{RESET} Izvorna slika je validna.")
    print(f"\n{CYAN}[PRE-STAGE KORAK 2/2]{RESET} Izvozim i sabijam ediciju '{BOLD}{edition_target}{RESET}' u lokalni keš...")
    print(f"       Privremeni fajl: {tmp_wim.name}")
    print(f"       Odredište:       {target_wim}")
    print(f"       Kompresija:      Maximum (Solid/LZX optimalno)")
    print(f"{GRAY}Molimo sačekajte 1 do 3 minuta (rad se obavlja na brzom disku računara)...{RESET}")

    try:
        export_cmd = [
            "wimlib-imagex", "export",
            str(source_image), edition_target,
            str(tmp_wim),
            "--compress=maximum"
        ]
        exp_proc = subprocess.run(export_cmd)
        if exp_proc.returncode != 0:
            print(f"{YELLOW}[UPOZORENJE]{RESET} Izvoz po imenu nije prošao, pokušavam sa indeksom 1...")
            export_cmd[3] = "1"
            exp_proc = subprocess.run(export_cmd)
            if exp_proc.returncode != 0:
                raise RuntimeError(f"Greška pri izvozu WIM slike (exit code {exp_proc.returncode}).")

        if not tmp_wim.exists() or tmp_wim.stat().st_size == 0:
            raise RuntimeError("Privremeni izvezeni WIM fajl ne postoji ili je prazan!")

        editions = read_wim_metadata(str(tmp_wim))
        if not editions:
            raise RuntimeError("Privremeni izvezeni WIM nema validne WIM metapodatke!")

        pro_ed = editions[0]
        size_bytes = tmp_wim.stat().st_size
        size_mb = size_bytes // (1024 * 1024)
        wim_sha256 = calc_sha256(tmp_wim)

        meta = {
            "edition": pro_ed.get("name", edition_target),
            "build": pro_ed.get("build", 0),
            "spbuild": pro_ed.get("spbuild", 0),
            "version_str": pro_ed.get("version_str", "Unknown"),
            "size_bytes": size_bytes,
            "size_mb": size_mb,
            "sha256": wim_sha256,
            "attempt_id": attempt_id,
            "prepared_at": time.strftime("%Y-%m-%d %H:%M:%S")
        }

        lock_file = staging_dir / ".staging.lock"
        with open(lock_file, "a+", encoding="utf-8") as lf:
            fcntl.flock(lf.fileno(), fcntl.LOCK_EX)
            try:
                os.replace(tmp_wim, target_wim)
                tmp_wim = None
                save_staging_metadata(meta)
            finally:
                try:
                    fcntl.flock(lf.fileno(), fcntl.LOCK_UN)
                except Exception:
                    pass
    finally:
        if tmp_wim is not None and tmp_wim.exists():
            try:
                tmp_wim.unlink()
            except Exception:
                pass

    print(f"\n{GREEN}================================================================================{RESET}")
    print(f"{GREEN}  [PRE-STAGE SPREMAN] NAJNOVIJA SLIKA JE USPEŠNO PRIPREMLJENA NA RAČUNARU!       {RESET}")
    print(f"{GREEN}================================================================================{RESET}")
    print(f"  Verzija u kešu:   {BOLD}{meta['version_str']}{RESET} ({meta['edition']})")
    print(f"  Veličina:         {BOLD}{size_mb} MB{RESET} (SHA-256: {wim_sha256[:12]})")
    print(f"  Putanja:          {target_wim}")
    print(f"\n  {YELLOW}👉 Sledeći korak:{RESET} Čim ubacite USB fleš, skripta će za samo ~60 sekundi")
    print(f"     prebaciti sliku i fleš će biti 100% ažuran! Pokrenite: {BOLD}./osvezi-wim-linux.sh --watch{RESET}\n")
    return meta


def sync_to_usb(src_wim_path, usb_wim_path, force=False, fstype_override=None, sync_scripts=True):
    """
    Transakcioni prenos pripremljene WIM/ESD slike na USB fleš preko privremenog fajla
    uz proveru prostora, FAT32 limita, SHA-256 i WIM metapodataka, sa automatskim rollback-om (Nalaz 6).
    Nikada ne briše postojeću sliku na USB-u pre nego što je nova slika 100% potvrđena!
    """
    src_wim = Path(src_wim_path)
    usb_wim_path = Path(usb_wim_path)
    if not src_wim.exists() or not src_wim.is_file():
        print(f"{RED}[GRESKA] Izvorna slika ne postoji: {src_wim}{RESET}")
        return False

    src_eds = read_wim_metadata(str(src_wim))
    if not src_eds:
        print(f"{RED}[GRESKA] Izvorna slika nema validne WIM/ESD metapodatke: {src_wim}{RESET}")
        return False

    src_pro = next((e for e in src_eds if "Pro" in e.get("name", "")), src_eds[0])
    staging_ver = src_pro.get("version_str", "Nepoznato")
    src_size_bytes = src_wim.stat().st_size
    staging_size_mb = src_size_bytes // (1024 * 1024)

    print(f"\n{CYAN}================================================================================{RESET}")
    print(f"{CYAN}          TRANSAKCIONI PRENOS SLIKE NA USB FLEŠ (SAFE-STAGING SYNC)             {RESET}")
    print(f"{CYAN}================================================================================{RESET}")
    print(f"  Izvor (Računar):   {src_wim} ({staging_size_mb} MB, Build {staging_ver})")
    print(f"  Cilj (USB Fleš):   {usb_wim_path}")

    if usb_wim_path.exists():
        usb_eds = read_wim_metadata(str(usb_wim_path))
        if usb_eds:
            usb_pro = next((e for e in usb_eds if "Pro" in e.get("name", "")), usb_eds[0])
            usb_ver = usb_pro["version_str"]
            print(f"  Trenutno na USB-u: {BOLD}{usb_ver}{RESET}")
            if not force and (usb_pro["build"], usb_pro["spbuild"]) >= (src_pro["build"], src_pro["spbuild"]):
                print(f"\n{GREEN}[UP TO DATE] USB fleš već poseduje ovu ili noviju verziju ({usb_ver})!{RESET}")
                return True

    usb_dir = usb_wim_path.parent
    usb_dir.mkdir(parents=True, exist_ok=True)
    usb_root = usb_dir.parent if usb_dir.name.lower() == "sources" else usb_dir

    attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
    ext = usb_wim_path.suffix.lower() or ".wim"
    tmp_img = usb_dir / f".install{ext}.tmp.{attempt_id}"
    bak_img = None
    conflicting_img = usb_dir / ("install.esd" if ext == ".wim" else "install.wim")

    real_target = os.path.realpath(str(usb_root))
    target_hash = hashlib.sha256(real_target.encode("utf-8")).hexdigest()[:16]
    usb_lock_file = Path("/tmp") / f"wis_usb_target_{target_hash}.lock"

    try:
        with open(usb_lock_file, "a+", encoding="utf-8") as ulf:
            try:
                fcntl.flock(ulf.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise RuntimeError(f"Ciljni USB medijum ({usb_root}) je već zaključan drugom operacijom!")

            try:
                # 1. Provera slobodnog prostora i FAT32 limita BEZ brisanja postojeće slike!
                space_info = validate_target_space_and_fs(
                    usb_dir,
                    src_size_bytes,
                    reserve_mb=250,
                    fstype_override=fstype_override,
                )
                free_mb = space_info["free_bytes"] // (1024 * 1024)
                needed_mb = space_info["needed_bytes"] // (1024 * 1024)
                print(
                    f"\n{CYAN}[KORAK 1/3]{RESET} Provera prostora OK (FS: {space_info['fstype']}): "
                    f"{free_mb} MB slobodno, potrebno {needed_mb} MB (postojeća slika je zaštićena)."
                )

                # 2. Kopiranje u privremeni fajl pokušaja uz računanje SHA-256 i fsync
                print(f"\n{CYAN}[KORAK 2/3]{RESET} Prenosim sliku u privremeni staging fajl ({tmp_img.name})...")
                copied_bytes = 0
                start_time = time.time()
                last_print = 0
                src_hasher = hashlib.sha256()
                chunk_size = 16 * 1024 * 1024  # 16 MB

                with open(src_wim, "rb") as src, open(tmp_img, "wb") as dst:
                    while True:
                        chunk = src.read(chunk_size)
                        if not chunk:
                            break
                        src_hasher.update(chunk)
                        dst.write(chunk)
                        copied_bytes += len(chunk)
                        now = time.time()
                        if now - last_print >= 0.5 or copied_bytes == src_size_bytes:
                            last_print = now
                            elapsed = max(now - start_time, 0.001)
                            speed_mb_s = (copied_bytes / (1024 * 1024)) / elapsed
                            percent = (copied_bytes / max(1, src_size_bytes)) * 100
                            progress_bar = "#" * int(percent // 4) + "-" * (25 - int(percent // 4))
                            print(
                                f"\r  [{progress_bar}] {percent:5.1f}% | {copied_bytes // (1024*1024)}/{staging_size_mb} MB | {speed_mb_s:5.1f} MB/s",
                                end="",
                                flush=True,
                            )
                    dst.flush()
                    os.fsync(dst.fileno())

                print()
                expected_sha256 = src_hasher.hexdigest()

                # 3. Verifikacija privremenog fajla pre promocije
                print(f"{CYAN}[KORAK 3/3]{RESET} Verifikujem veličinu, SHA-256 i WIM metapodatke privremene slike...")
                if not tmp_img.exists() or tmp_img.stat().st_size != src_size_bytes:
                    raise RuntimeError("Veličina privremene slike na USB-u ne odgovara izvoru!")

                actual_sha256 = calc_sha256(tmp_img)
                if actual_sha256 != expected_sha256:
                    raise RuntimeError(
                        f"SHA-256 nepodudaranje privremene slike na USB-u "
                        f"({actual_sha256[:12]} != {expected_sha256[:12]})!"
                    )

                verified_eds = read_wim_metadata(str(tmp_img))
                if not verified_eds:
                    raise RuntimeError("Privremena slika na USB-u nema validne WIM/ESD metapodatke!")

                # 4. Preimenovanje stare slike u .bak.<attempt_id> i atomska promocija nove
                if usb_wim_path.exists():
                    bak_img = usb_dir / f"{usb_wim_path.name}.bak.{attempt_id}"
                    os.replace(usb_wim_path, bak_img)

                os.replace(tmp_img, usb_wim_path)
                tmp_img = None

                # 5. Sinhronizacija skripti po manifestu ako je u okviru USB strukture
                if sync_scripts and usb_dir.name.lower() == "sources":
                    project_root = _SCRIPT_DIR.parent
                    sync_manifest_to_target(usb_root=usb_root, project_root=project_root)

                # 6. Tek po potvrdi i slike i skripti brišemo .bak.<attempt_id> i konfliktni stari format
                if bak_img is not None and bak_img.exists():
                    try:
                        bak_img.unlink()
                    except Exception:
                        pass

                if conflicting_img.exists() and conflicting_img != usb_wim_path:
                    try:
                        conflicting_img.unlink()
                    except Exception:
                        pass

                print(f"{CYAN}[SYNC]{RESET} Sinhronizujem keš na USB fleš memoriju (sync)...")
                subprocess.run(["sync"], timeout=30)

                elapsed_total = int(time.time() - start_time)
                sys.stdout.write("\a")
                sys.stdout.flush()

                print(f"\n{GREEN}================================================================================{RESET}")
                print(f"{GREEN}  [USPEH] USB FLEŠ JE USPEŠNO AŽURIRAN NA NAJNOVIJU VERZIJU ({elapsed_total}s)!                {RESET}")
                print(f"{GREEN}================================================================================{RESET}")
                print(f"  Verzija:       {BOLD}{staging_ver}{RESET}")
                print(f"  SHA-256:       {BOLD}{expected_sha256[:16]}...{RESET}")
                print(f"  Lokacija:      {usb_wim_path}")
                print(f"  Status:        Sveže zapečeno i verifikovano! Možete bezbedno izvaditi fleš.\n")
                return True
            finally:
                try:
                    fcntl.flock(ulf.fileno(), fcntl.LOCK_UN)
                except Exception:
                    pass

    except Exception as e:
        print(f"\n{RED}[GRESKA] Prenos na USB nije uspeo: {e}{RESET}")
        if tmp_img is not None and tmp_img.exists():
            try:
                tmp_img.unlink()
            except Exception:
                pass
        if bak_img is not None and bak_img.exists():
            try:
                os.replace(bak_img, usb_wim_path)
                print(f"{YELLOW}[ROLLBACK]{RESET} Prethodna ispravna slika ({usb_wim_path.name}) je vraćena iz {bak_img.name}!")
            except Exception as rb_err:
                print(f"{RED}[KRITIČNO] Greška pri rollback-u {bak_img.name}: {rb_err}{RESET}")
        return False


def sync_staging_to_usb(usb_wim_path, force=False, fstype_override=None, sync_scripts=True):
    """
    Brzo kopira prethodno pripremljen lokalni WIM sa računara na USB fleš koristeći bezbedni sync_to_usb.
    """
    staging_wim = get_staging_dir() / "install.wim"
    if not staging_wim.exists():
        print(f"{RED}[GRESKA] Nema pripremljene lokalne slike u kešu!{RESET}")
        print(f"Prvo pripremite sliku pokretanjem: {BOLD}./osvezi-wim-linux.sh --prepare --iso <fajl>{RESET}")
        return False
    return sync_to_usb(staging_wim, usb_wim_path, force=force, fstype_override=fstype_override, sync_scripts=sync_scripts)


def watch_usb_hotplug():
    """
    Čeka u petlji dok korisnik ne ubaci USB fleš disk.
    Čim se fleš detektuje, automatski pokreće brzi prenos i završava.
    """
    staging_wim = get_staging_dir() / "install.wim"
    if not staging_wim.exists():
        print(f"{RED}[GRESKA] Nema pripremljene lokalne slike u kešu!{RESET}")
        print(f"Prvo pripremite sliku na računaru: {BOLD}./osvezi-wim-linux.sh --prepare --iso <fajl>{RESET}")
        return 1

    staging_meta = load_staging_metadata() or {}
    staging_ver = staging_meta.get("version_str", "Nepoznato")

    print(f"{CYAN}================================================================================{RESET}")
    print(f"{YELLOW}         GWADT USB HOTPLUG WATCHER - AUTOMATSKO AŽURIRANJE FLEŠA        {RESET}")
    print(f"{CYAN}================================================================================{RESET}")
    print(f"  Pripremljena verzija u kešu: {BOLD}{staging_ver}{RESET} ({staging_meta.get('size_mb', 0)} MB)")
    print(f"  Status: {GREEN}Čekam ubacivanje USB fleš diska...{RESET} (Pritisnite Ctrl+C za prekid)\n")

    spinner = ["|", "/", "-", "\\"]
    idx = 0
    try:
        while True:
            usb_wim = detect_usb_flash()
            if usb_wim:
                print(f"\n\n{GREEN}[DETEKTOVAN USB]{RESET} Pronađen fleš medijum: {BOLD}{usb_wim}{RESET}!")
                time.sleep(1)
                ok = sync_staging_to_usb(usb_wim)
                return 0 if ok else 1

            spin_char = spinner[idx % len(spinner)]
            print(f"\r  [{CYAN}{spin_char}{RESET}] Pratim USB portove... Ubacite fleš u bilo koji USB port računara...", end="", flush=True)
            idx += 1
            time.sleep(1.2)
    except KeyboardInterrupt:
        print(f"\n\n{YELLOW}[PREKID]{RESET} Praćenje portova zaustavljeno od strane korisnika.")
        return 0


def export_and_optimize_wim(source_image, target_usb_wim, edition_target="Windows 11 Pro", fstype_override=None):
    """
    Pomoću wimlib-imagex izvozi željenu ediciju (Windows 11 Pro) sa maksimalnom kompresijom
    direktno u novi izolovani WIM na lokalnom disku, i transakciono zamenjuje sliku na USB flešu.
    """
    work_dir, free_gb = get_best_storage_dir()
    if free_gb < 15:
        raise RuntimeError(f"Nema dovoljno slobodnog prostora na lokalnom disku ({free_gb:.1f} GB dostupno, potrebno 15 GB)!")

    attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
    work_dir.mkdir(parents=True, exist_ok=True)
    local_output_wim = work_dir / f"install_optimized_{attempt_id}.wim"

    print(f"\n{CYAN}[KORAK 1/3]{RESET} Inspekcija i očitavanje strukture izvorne slike...")
    info_proc = subprocess.run(["wimlib-imagex", "info", str(source_image)], capture_output=True, text=True)
    if info_proc.returncode != 0:
        raise RuntimeError(f"wimlib-imagex greška pri očitavanju: {info_proc.stderr}")

    print(f"{GREEN}[OK]{RESET} Izvorna slika: {BOLD}{source_image.name}{RESET}")
    print(f"\n{CYAN}[KORAK 2/3]{RESET} Izvozim i optimizujem ediciju '{BOLD}{edition_target}{RESET}'...")
    print(f"       Radna lokacija: {work_dir} ({free_gb:.1f} GB slobodno)")
    print(f"       Kompresija: Maximum (Solid/LZX optimalno)")
    print(f"{GRAY}Molimo sačekajte 1 do 3 minuta...{RESET}")

    try:
        export_cmd = [
            "wimlib-imagex", "export",
            str(source_image), edition_target,
            str(local_output_wim),
            "--compress=maximum"
        ]
        exp_proc = subprocess.run(export_cmd)
        if exp_proc.returncode != 0:
            print(f"{YELLOW}[UPOZORENJE]{RESET} Izvoz po imenu '{edition_target}' nije prošao, pokušavam po indeksu 1...")
            export_cmd[3] = "1"
            exp_proc = subprocess.run(export_cmd)
            if exp_proc.returncode != 0:
                raise RuntimeError(f"Greška pri izvozu WIM slike: exit code {exp_proc.returncode}")

        new_size_mb = local_output_wim.stat().st_size // (1024 * 1024)
        print(f"{GREEN}[OK]{RESET} Uspešno generisan optimizovani WIM: {new_size_mb} MB")

        ok = sync_to_usb(local_output_wim, target_usb_wim, force=True, fstype_override=fstype_override)
        if not ok:
            raise RuntimeError("Transakcioni prenos optimizovane WIM slike na USB fleš nije uspeo!")
    finally:
        if local_output_wim.exists():
            try:
                local_output_wim.unlink()
            except Exception:
                pass


def scan_for_local_images():
    """Traži postojeće Windows 11 ISO/ESD slike u Downloads, Storage i Home."""
    search_dirs = [
        Path.home() / "Downloads",
        Path("/home/gru/Storage"),
        Path("/home/gru/Storage/gwadt_refresh_work"),
        Path.home()
    ]
    found = []
    for d in search_dirs:
        try:
            if d.exists():
                for p in d.glob("*.iso"):
                    if "win" in p.name.lower() or "windows" in p.name.lower():
                        found.append(p)
                for p in d.glob("*.esd"):
                    if "win" in p.name.lower() or "install" in p.name.lower():
                        found.append(p)
        except Exception:
            continue
    return found

def download_and_process_url(url, target_usb_wim=None, is_staging=False):
    """
    Preuzima ISO ili ESD fajl sa zadatog URL-a pomoću aria2c u radni direktorijum
    i automatski pokreće proceduru izvoza u staging ili na USB fleš disk.
    """
    work_dir, free_gb = get_best_storage_dir()
    if free_gb < 20:
        raise RuntimeError(f"Nema dovoljno mesta za preuzimanje ISO fajla ({free_gb:.1f} GB dostupno, potrebno min 20 GB).")
    
    work_dir.mkdir(parents=True, exist_ok=True)
    
    parsed_path = urllib.parse.urlparse(url).path
    filename = os.path.basename(parsed_path)
    if not (filename.lower().endswith(".iso") or filename.lower().endswith(".esd")):
        filename = "Windows11_Ažuriran.iso"
    
    dest_path = work_dir / filename
    print(f"\n{CYAN}[PREUZIMANJE]{RESET} Pokrećem višenitno preuzimanje preko aria2c...")
    print(f"       Izvor:  {BOLD}{url[:80]}...{RESET}")
    print(f"       Cilj:   {dest_path}")
    print(f"       Radni disk: {work_dir} ({free_gb:.1f} GB slobodno)")
    
    cmd = [
        "aria2c",
        "-x", "8",
        "-s", "8",
        "-j", "8",
        "-k", "1M",
        "-c",
        "--summary-interval=5",
        "-d", str(work_dir),
        "-o", filename,
        url
    ]
    res = subprocess.run(cmd)
    if res.returncode != 0:
        raise RuntimeError(f"aria2c greška pri preuzimanju: izlazni kod {res.returncode}")
    
    size_mb = dest_path.stat().st_size // (1024 * 1024)
    print(f"\n{GREEN}[OK]{RESET} Preuzimanje uspešno završeno: {dest_path.name} ({size_mb} MB)")
    
    if is_staging or target_usb_wim is None:
        if filename.lower().endswith(".iso"):
            mount_dir, loop_dev = mount_iso_udisks(dest_path)
            try:
                source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                source_img = next((s for s in source_candidates if s.exists()), None)
                if not source_img:
                    raise RuntimeError("U preuzetom ISO fajlu nema install.wim ili install.esd!")
                prepare_local_staging(source_img)
            finally:
                unmount_iso_udisks(loop_dev)
        else:
            prepare_local_staging(dest_path)
    else:
        if filename.lower().endswith(".iso"):
            mount_dir, loop_dev = mount_iso_udisks(dest_path)
            try:
                source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                source_img = next((s for s in source_candidates if s.exists()), None)
                if not source_img:
                    raise RuntimeError("U preuzetom ISO fajlu nema install.wim ili install.esd!")
                export_and_optimize_wim(source_img, target_usb_wim, "Windows 11 Pro")
            finally:
                unmount_iso_udisks(loop_dev)
        else:
            export_and_optimize_wim(dest_path, target_usb_wim, "Windows 11 Pro")

def fetch_latest_online_build(branch_hint=None):
    """Poziva UUP Dump API za najnovije stabilno izdanje."""
    search_query = branch_hint if branch_hint else "Windows 11"
    url = f"https://api.uupdump.net/listid.php?search={urllib.parse.quote(str(search_query))}"
    headers = {"User-Agent": "GWADT-AutoUpdater/1.0"}
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        builds_dict = data.get("response", {}).get("builds", {})
        if not builds_dict:
            return None
        candidates = []
        for b in builds_dict.values():
            if b.get("arch") == "amd64" and "Windows 11" in b.get("title", ""):
                build_raw = str(b.get("build", "0"))
                parts = build_raw.split(".")
                b_num = int(parts[0]) if parts[0].isdigit() else 0
                sp_num = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
                candidates.append({
                    "title": b.get("title"),
                    "build_num": b_num,
                    "sp_num": sp_num,
                    "build_str": build_raw,
                    "uuid": b.get("uuid"),
                    "created": b.get("created", 0)
                })
        if not candidates:
            return None
        candidates.sort(key=lambda x: (x["build_num"], x["sp_num"], x["created"]), reverse=True)
        return candidates[0]
    except Exception as e:
        print(f"{RED}[GRESKA] Neuspešna veza sa API-jem: {e}{RESET}")
        return None

def main():
    parser = argparse.ArgumentParser(description="GWADT Linux Direct Refresh, Pre-Stage & Hotplug Sync Tool")
    parser.add_argument("--wim", help="Putanja do install.wim fajla na USB-u (podrazumevano automatska detekcija)")
    parser.add_argument("--iso", help="Putanja do zvaničnog Windows 11 ISO fajla")
    parser.add_argument("--esd", help="Putanja do install.esd fajla")
    parser.add_argument("--download-url", help="Direktan URL ka zvaničnom Microsoft Windows 11 ISO/ESD fajlu")
    parser.add_argument("--prepare", action="store_true", help="Pripremi WIM u lokalni keš računara (Pre-Stage) bez USB fleša")
    parser.add_argument("--watch", action="store_true", help="Hotplug Watcher: čeka ubacivanje fleša i automatski ga ažurira iz lokalnog keša")
    parser.add_argument("--sync-only", action="store_true", help="Odmah prebaci pripremljeni keš WIM na ubačeni USB fleš")
    parser.add_argument("--auto", action="store_true", help="Automatski skenira lokalne foldere za najnoviji ISO i obrađuje ga")
    parser.add_argument("--check-only", action="store_true", help="Samo proveri verziju bez izmena")
    args = parser.parse_args()

    print_banner()

    missing = check_missing_tools(REQUIRED_TOOLS)
    if missing:
        print(f"{RED}[GRESKA] Nedostaju sledeći alati na sistemu:{RESET} {', '.join(missing)}")
        print(f"Instalirajte ih sa: {CYAN}sudo apt install -y wimtools aria2{RESET}")
        return 1

    # REŽIM: Hotplug Watcher
    if args.watch:
        return watch_usb_hotplug()

    # Detektuj USB fleš
    target_usb_wim = Path(args.wim) if args.wim else detect_usb_flash()

    # REŽIM: Samo sinhronizuj lokalni staging na ubačeni fleš
    if args.sync_only:
        if not target_usb_wim:
            print(f"{RED}[GRESKA] USB fleš nije detektovan! Ubacite fleš ili pokrenite --watch.{RESET}")
            return 1
        return 0 if sync_staging_to_usb(target_usb_wim, force=True) else 1

    # REŽIM: Priprema lokalnog staging-a (Pre-Stage) na računaru
    if args.prepare:
        print(f"{CYAN}[REŽIM: PRE-STAGE PRIPREMA]{RESET} Pripremam sliku na disku računara...")
        if args.download_url:
            download_and_process_url(args.download_url, is_staging=True)
            return 0
        
        source_img = None
        loop_dev = None
        if args.iso:
            mount_dir, loop_dev = mount_iso_udisks(Path(args.iso).resolve())
            source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
            source_img = next((s for s in source_candidates if s.exists()), None)
        elif args.esd:
            source_img = Path(args.esd).resolve()
        else:
            images = scan_for_local_images()
            if images:
                images.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                best = images[0]
                print(f"Pronađen lokalni fajl: {best}")
                if best.suffix.lower() == ".iso":
                    mount_dir, loop_dev = mount_iso_udisks(best)
                    source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                    source_img = next((s for s in source_candidates if s.exists()), None)
                else:
                    source_img = best
        
        if not source_img:
            print(f"{RED}[GRESKA] Nije pronađen ISO ili ESD za pripremu! Navedite --iso ili --download-url.{RESET}")
            return 1
        
        try:
            prepare_local_staging(source_img)
            return 0
        finally:
            if loop_dev:
                unmount_iso_udisks(loop_dev)

    # REŽIM: Preuzimanje sa direktnog URL-a
    if args.download_url:
        download_and_process_url(args.download_url, target_usb_wim)
        return 0

    # REŽIM: Auto skeniranje
    if args.auto:
        local_images = scan_for_local_images()
        if not local_images:
            print(f"{YELLOW}[OBAVEŠTENJE]{RESET} Nije pronađen nijedan Windows 11 ISO/ESD fajl u ~/Downloads ili /home/gru/Storage.")
            return 1
        local_images.sort(key=lambda p: p.stat().st_mtime, reverse=True)
        best_img = local_images[0]
        print(f"{GREEN}[PRONAĐENO]{RESET} Najnovija slika: {BOLD}{best_img}{RESET}")
        if target_usb_wim:
            if best_img.suffix.lower() == ".iso":
                mount_dir, loop_dev = mount_iso_udisks(best_img)
                try:
                    source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                    source_img = next((s for s in source_candidates if s.exists()), None)
                    export_and_optimize_wim(source_img, target_usb_wim, "Windows 11 Pro")
                    return 0
                finally:
                    unmount_iso_udisks(loop_dev)
            else:
                export_and_optimize_wim(best_img, target_usb_wim, "Windows 11 Pro")
                return 0
        else:
            print(f"{CYAN}[INFO]{RESET} USB fleš nije detektovan. Pripremam u lokalni keš (Pre-stage)...")
            if best_img.suffix.lower() == ".iso":
                mount_dir, loop_dev = mount_iso_udisks(best_img)
                try:
                    source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                    source_img = next((s for s in source_candidates if s.exists()), None)
                    prepare_local_staging(source_img)
                    return 0
                finally:
                    unmount_iso_udisks(loop_dev)
            else:
                prepare_local_staging(best_img)
                return 0

    # REŽIM: Uvoz iz ISO fajla
    if args.iso:
        iso_path = Path(args.iso).resolve()
        if not iso_path.exists():
            print(f"{RED}[GRESKA] ISO fajl '{iso_path}' ne postoji!{RESET}")
            return 1
        mount_dir, loop_dev = mount_iso_udisks(iso_path)
        try:
            source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
            source_img = next((s for s in source_candidates if s.exists()), None)
            if not source_img:
                print(f"{RED}[GRESKA] U ISO fajlu nema install.wim ili install.esd!{RESET}")
                return 1
            if target_usb_wim:
                export_and_optimize_wim(source_img, target_usb_wim, "Windows 11 Pro")
            else:
                prepare_local_staging(source_img)
            return 0
        finally:
            unmount_iso_udisks(loop_dev)

    # REŽIM: Provera trenutnog stanja (ili interaktivni meni ako nema argumenata)
    staging_wim = get_staging_dir() / "install.wim"
    staging_meta = load_staging_metadata() or {}
    staging_ver = staging_meta.get("version_str", None)

    if not target_usb_wim:
        print(f"{YELLOW}[STATUS]{RESET} USB fleš disk trenutno {BOLD}nije detektovan{RESET} u računaru.")
        if staging_wim.exists() and staging_ver:
            print(f"  Pripremljena verzija u lokalnom kešu: {GREEN}{BOLD}Build {staging_ver}{RESET} ({staging_meta.get('size_mb', 0)} MB)")
            print(f"\n  {BOLD}Izaberite opciju:{RESET}")
            print(f"  {CYAN}1.{RESET} Pokreni Hotplug Watcher (čekaj ubacivanje fleša i automatski ažuriraj)")
            print(f"  {CYAN}2.{RESET} Pripremi noviju verziju u lokalni keš (Pre-stage)")
            print(f"  {CYAN}3.{RESET} Proveri najnoviju verziju na Microsoftu online")
            print(f"  {CYAN}4.{RESET} Izlaz")
        else:
            print(f"  Lokalni keš: {GRAY}Nema pripremljene slike na računaru.{RESET}")
            print(f"\n  {BOLD}Izaberite opciju:{RESET}")
            print(f"  {CYAN}1.{RESET} Pripremi Windows 11 sliku lokalno na računaru (Pre-stage)")
            print(f"  {CYAN}2.{RESET} Proveri najnoviju verziju na Microsoftu online")
            print(f"  {CYAN}3.{RESET} Izlaz")

        if args.check_only:
            print(f"\n{CYAN}[INFO]{RESET} Proveravam najnovija dostupna ažuriranja online...")
            latest_online = fetch_latest_online_build("Windows 11")
            if latest_online:
                print(f"       Najnovije izdanje: {BOLD}{latest_online['title']}{RESET} (Build {latest_online['build_str']})")
            return 0

        try:
            choice = input(f"\n{BOLD}Unesite broj opcije: {RESET}").strip()
        except (KeyboardInterrupt, EOFError):
            print()
            return 0

        if choice == "1" and staging_wim.exists() and staging_ver:
            return watch_usb_hotplug()
        elif (choice == "2" and staging_wim.exists() and staging_ver) or (choice == "1" and not staging_wim.exists()):
            images = scan_for_local_images()
            if images:
                images.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                best = images[0]
                print(f"Pronađen lokalni fajl: {best.name}. Pripremam...")
                if best.suffix.lower() == ".iso":
                    mount_dir, loop_dev = mount_iso_udisks(best)
                    try:
                        source_candidates = [mount_dir / "sources" / "install.wim", mount_dir / "sources" / "install.esd"]
                        source_img = next((s for s in source_candidates if s.exists()), None)
                        prepare_local_staging(source_img)
                        return 0
                    finally:
                        unmount_iso_udisks(loop_dev)
                else:
                    prepare_local_staging(best)
                    return 0
            else:
                print(f"Preuzmite ISO ili unesite URL: {BOLD}./osvezi-wim-linux.sh --prepare --download-url <URL>{RESET}")
                return 0
        elif (choice == "3" and staging_wim.exists()) or (choice == "2" and not staging_wim.exists()):
            latest_online = fetch_latest_online_build("Windows 11")
            if latest_online:
                print(f"\nNajnovije izdanje: {BOLD}{latest_online['title']}{RESET} (Build {latest_online['build_str']})")
            return 0
        return 0

    # Ako je USB fleš ubačen
    print(f"{CYAN}[INFO]{RESET} Detektovan USB instalacioni medijum: {BOLD}{target_usb_wim}{RESET}")
    editions = read_wim_metadata(str(target_usb_wim))
    if not editions:
        print(f"{RED}[GRESKA] Nije moguće pročitati metapodatke sa USB fleša.{RESET}")
        return 1

    pro_ed = next((e for e in editions if "Pro" in e["name"] and "Workstation" not in e["name"]), editions[0])
    local_build = pro_ed["build"]
    local_spbuild = pro_ed["spbuild"]
    local_ver_str = f"{local_build}.{local_spbuild}"

    print(f"{GREEN}[OK]{RESET} Prepoznata edicija: {BOLD}{pro_ed['name']}{RESET}")
    print(f"{GREEN}[OK]{RESET} Trenutni Build na USB-u: {BOLD}{local_ver_str}{RESET}")

    # Proveri da li u lokalnom kešu imamo noviji WIM spreman za prenos
    if staging_wim.exists() and staging_ver:
        s_build = staging_meta.get("build", 0)
        s_spbuild = staging_meta.get("spbuild", 0)
        print(f"{CYAN}[LOKALNI KEŠ]{RESET} Na računaru je spreman Build: {BOLD}{staging_ver}{RESET}")
        if (s_build, s_spbuild) > (local_build, local_spbuild):
            print(f"\n{YELLOW}================================================================================{RESET}")
            print(f"{YELLOW}  [NOVIJA SLIKA SPREMNA] NA RAČUNARU IMATE NOVIJI PRIPREMLJEN WIM!              {RESET}")
            print(f"{YELLOW}================================================================================{RESET}")
            print(f"  Na USB flešu:       {local_ver_str}")
            print(f"  Spremno u kešu:     {staging_ver}")
            print(f"\n  Prebacivanje traje samo ~60 sekundi jer je slika već sabijena i optimizovana!")
            if not args.check_only:
                try:
                    q = input(f"\nŽelite li da odmah ažurirate USB fleš? [D/n]: ").strip().lower()
                    if q in ["", "d", "da", "y", "yes"]:
                        sync_staging_to_usb(target_usb_wim, force=True)
                        return 0
                except (KeyboardInterrupt, EOFError):
                    print()
                    return 0

    print(f"{GRAY}--------------------------------------------------------------------------------{RESET}")
    print(f"{CYAN}[INFO]{RESET} Proveravam najnovija dostupna ažuriranja online...")
    latest_online = fetch_latest_online_build(str(local_build))
    if not latest_online:
        latest_online = fetch_latest_online_build("Windows 11")

    if latest_online:
        online_ver_str = latest_online["build_str"]
        print(f"       Najnovije izdanje: {BOLD}{latest_online['title']}{RESET} (Build {online_ver_str})")
        
        local_tuple = (local_build, local_spbuild)
        online_tuple = (latest_online["build_num"], latest_online["sp_num"])
        
        if local_tuple >= online_tuple:
            print(f"\n{GREEN}================================================================================{RESET}")
            print(f"{GREEN}  [UP TO DATE] VAŠ USB FLEŠ JE VEĆ NA NAJNOVIJOJ VERZIJI!                         {RESET}")
            print(f"{GREEN}================================================================================{RESET}")
            print(f"  Verzija na USB flešu:  {BOLD}{local_ver_str}{RESET}")
            print(f"  Nema potrebe za ažuriranjem. Sistem je spreman za rad!\n")
            return 0
        else:
            diff_revs = latest_online["sp_num"] - local_spbuild if latest_online["build_num"] == local_build else "Novo izdanje"
            print(f"\n{YELLOW}================================================================================{RESET}")
            print(f"{YELLOW}  [NOVA VERZIJA] DOSTUPNO JE NOVIJE IZDANJE!                                     {RESET}")
            print(f"{YELLOW}================================================================================{RESET}")
            print(f"  Trenutno na USB-u:     {BOLD}{local_ver_str}{RESET}")
            print(f"  Dostupno online:       {BOLD}{online_ver_str}{RESET} (Razlika: {diff_revs})")
            print(f"\n  {BOLD}Preporučeni tok rada:{RESET}")
            print(f"  1. Pripremite sliku unapred na računaru (čak i kad fleš nije priključen):")
            print(f"     {BOLD}./osvezi-wim-linux.sh --prepare --iso <fajl>{RESET}")
            print(f"  2. Čim ubacite fleš, pokrenite Watcher ili brzi prenos:")
            print(f"     {BOLD}./osvezi-wim-linux.sh --watch{RESET}\n")

    return 0

if __name__ == "__main__":
    sys.exit(main())
