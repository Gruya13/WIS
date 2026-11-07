# -*- coding: utf-8 -*-
"""
patch_engine.py - Provera verzija zakrpa online i u lokalnom programu
Povezuje se na Microsoft / UUP baze podataka i analizira lokalni Pre-Stage keš i folder Zakrpe.
"""

import os
import sys
import json
import time
import urllib.request
import urllib.parse
from pathlib import Path
from datetime import datetime

# Globalni keš za online proveru da ne bismo preopteretili API pri polling-u
_ONLINE_CACHE = {
    "data": None,
    "last_checked": 0,
    "ttl_seconds": 600 # 10 minuta
}

def get_staging_dir():
    """Vraća primarni direktorijum za staging keš na disku radne stanice."""
    env_staging = os.environ.get("GWADT_STAGING_DIR")
    if env_staging:
        try:
            p = Path(env_staging)
            p.mkdir(parents=True, exist_ok=True)
            return p
        except Exception:
            pass

    candidates = [
        Path("/home/gru/Storage/gwadt_staging"),
        Path.home() / "gwadt_staging",
        Path.home() / "Downloads" / "gwadt_staging",
        Path("/tmp/gwadt_staging")
    ]
    for cand in candidates:
        try:
            parent = cand.parent
            if parent.exists() and os.access(str(parent), os.W_OK):
                cand.mkdir(parents=True, exist_ok=True)
                return cand
            elif cand.exists() and os.access(str(cand), os.W_OK):
                return cand
        except Exception:
            continue
    fallback = Path.home() / "gwadt_staging"
    try:
        fallback.mkdir(parents=True, exist_ok=True)
    except Exception:
        fallback = Path("/tmp/gwadt_staging")
        fallback.mkdir(parents=True, exist_ok=True)
    return fallback

def get_staging_metadata():
    """Očitava metapodatke o pripremljenoj WIM slici u lokalnom programu."""
    staging_dir = get_staging_dir()
    staging_wim = staging_dir / "install.wim"
    meta_file = staging_dir / "staging_meta.json"
    
    # 1. Ako postoji u staging kešu
    if staging_wim.exists():
        size_mb = staging_wim.stat().st_size // (1024 * 1024)
        from .system_inspector import read_wim_metadata
        editions = read_wim_metadata(str(staging_wim))
        pro_ed = next((e for e in editions if "Pro" in e["name"] and "Workstation" not in e["name"]), editions[0]) if editions else {}

        meta = {}
        if meta_file.exists():
            try:
                with open(meta_file, "r", encoding="utf-8") as f:
                    meta = json.load(f)
            except Exception:
                pass

        # Prioritet imaju STVARNO očitani podaci iz WIM fajla
        build = pro_ed.get("build") or meta.get("build") or 26200
        spbuild = pro_ed.get("spbuild") or meta.get("spbuild") or 8037
        version_str = pro_ed.get("version_str") or f"10.0.{build}.{spbuild}"
        edition_name = pro_ed.get("name") or meta.get("edition") or "Windows 11 Pro"

        # Ažuriranje meta fajla ako je bio neusklađen
        if meta.get("build") != build or meta.get("spbuild") != spbuild:
            try:
                meta.update({
                    "edition": edition_name,
                    "build": build,
                    "spbuild": spbuild,
                    "version_str": version_str,
                    "size_mb": size_mb
                })
                with open(meta_file, "w", encoding="utf-8") as f:
                    json.dump(meta, f, indent=2, ensure_ascii=False)
            except Exception:
                pass

        return {
            "has_staging": True,
            "staging_path": str(staging_wim),
            "source_type": "Lokalni Pre-Stage Keš",
            "edition": edition_name,
            "version_str": version_str,
            "build": build,
            "spbuild": spbuild,
            "size_mb": size_mb,
            "prepared_at": meta.get("prepared_at", "Prethodno sačuvano")
        }

    # 2. Fallback na projektni sources/install.wim ako staging nije generisan
    project_root = Path(os.environ.get("GWADT_PROJECT_ROOT", str(Path(__file__).resolve().parent.parent.parent)))
    proj_wim = project_root / "sources" / "install.wim"
    if proj_wim.exists():
        from .system_inspector import read_wim_metadata
        size_mb = proj_wim.stat().st_size // (1024 * 1024)
        editions = read_wim_metadata(str(proj_wim))
        pro_ed = next((e for e in editions if "Pro" in e["name"] and "Workstation" not in e["name"]), editions[0]) if editions else {}
        build = pro_ed.get("build", 26200)
        spbuild = pro_ed.get("spbuild", 8037)
        ver_str = pro_ed.get("version_str", f"10.0.{build}.{spbuild}")
        mtime = proj_wim.stat().st_mtime
        mtime_str = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M")

        return {
            "has_staging": True,
            "staging_path": str(proj_wim),
            "source_type": "Programski Repozitorijum (sources/install.wim)",
            "edition": pro_ed.get("name", "Windows 11 Pro"),
            "version_str": ver_str,
            "build": build,
            "spbuild": spbuild,
            "size_mb": size_mb,
            "prepared_at": mtime_str
        }

    return {
        "has_staging": False,
        "staging_path": None,
        "source_type": "Nema slike",
        "version_str": "Nema pripremljene slike",
        "build": 0,
        "spbuild": 0,
        "size_mb": 0,
        "prepared_at": "N/A"
    }

def get_local_patches_list(project_root=None):
    """
    Skenira direktorijum 'Zakrpe/' i vraća listu preuzetih .msu i .cab datoteka.
    """
    if project_root is None:
        project_root = Path(os.environ.get("GWADT_PROJECT_ROOT", str(Path(__file__).resolve().parent.parent.parent)))
    else:
        project_root = Path(project_root)

    zakrpe_dir = project_root / "Zakrpe"
    if not zakrpe_dir.exists() or not zakrpe_dir.is_dir():
        return []

    patches = []
    for f in zakrpe_dir.iterdir():
        if f.is_file() and f.suffix.lower() in [".msu", ".cab"]:
            stat = f.stat()
            size_mb = round(stat.st_size / (1024 * 1024), 1)
            
            # Ekstrakcija KB broja
            kb_num = "Nepoznato"
            name_lower = f.name.lower()
            if "kb" in name_lower:
                part = name_lower.split("kb")[1]
                kb_clean = "".join(c for c in part if c.isdigit())
                if kb_clean:
                    kb_num = f"KB{kb_clean[:7]}"

            patch_type = "Kumulativno Ažuriranje (LCU)"
            if "ndp" in name_lower or "net" in name_lower:
                patch_type = ".NET Framework Zakrpa"
            elif f.suffix.lower() == ".cab" and size_mb < 5:
                patch_type = "Servicing Stack (SSU)"

            patches.append({
                "filename": f.name,
                "kb": kb_num,
                "type": patch_type,
                "size_mb": size_mb,
                "size_bytes": stat.st_size,
                "path": str(f)
            })

    patches.sort(key=lambda x: x["size_bytes"], reverse=True)
    return patches

def fetch_latest_online_build(force_refresh=False):
    """
    Poziva UUP Dump / Microsoft API i vraća podatke o najnovijem zvaničnom Windows 11 Build-u.
    Koristi keširanje radi brzog odziva u GUI-ju.
    """
    now = time.time()
    if not force_refresh and _ONLINE_CACHE["data"] and (now - _ONLINE_CACHE["last_checked"] < _ONLINE_CACHE["ttl_seconds"]):
        return _ONLINE_CACHE["data"]

    url = "https://api.uupdump.net/listid.php?search=Windows%2011"
    headers = {"User-Agent": "GWADT-USBTool/1.0"}
    req = urllib.request.Request(url, headers=headers)
    
    try:
        with urllib.request.urlopen(req, timeout=8) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        
        builds_dict = data.get("response", {}).get("builds", {})
        if not builds_dict:
            return _fallback_online_data()

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
                    "version_str": f"10.0.{build_raw}",
                    "uuid": b.get("uuid"),
                    "created": b.get("created", 0)
                })

        if candidates:
            candidates.sort(key=lambda x: (x["build_num"], x["sp_num"], x["created"]), reverse=True)
            top = candidates[0]
            result = {
                "success": True,
                "title": top["title"],
                "build_num": top["build_num"],
                "sp_num": top["sp_num"],
                "version_str": top["version_str"],
                "uuid": top.get("uuid", ""),
                "checked_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            }
            _ONLINE_CACHE["data"] = result
            _ONLINE_CACHE["last_checked"] = now
            return result
        else:
            return _fallback_online_data()

    except Exception as e:
        print(f"[patch_engine] Online provera nije uspela: {e}", file=sys.stderr)
        if _ONLINE_CACHE["data"]:
            return _ONLINE_CACHE["data"]
        return _fallback_online_data()


def fetch_uup_download_package(uuid, dest_dir, edition="professional", lang="en-us"):
    """
    Šalje POST zahtev na UUP Dump i preuzima ZIP paket koji sadrži aria2 skripte
    i alate za konverziju u WIM/ISO, te ga raspakuje u dest_dir.
    """
    import zipfile
    import io
    
    url = f"https://uupdump.net/get.php?id={uuid}&pack={lang}&edition={edition}"
    data = urllib.parse.urlencode({
        "autodl": "2",
        "updates": "1",
        "cleanup": "1"
    }).encode("utf-8")
    
    req = urllib.request.Request(
        url,
        data=data,
        headers={"User-Agent": "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0"}
    )
    
    with urllib.request.urlopen(req, timeout=30) as resp:
        content_type = resp.info().get_content_type()
        if "zip" not in content_type and "octet-stream" not in content_type:
            body_start = resp.read(500).decode("utf-8", errors="ignore")
            raise RuntimeError(f"Server nije vratio ZIP arhivu (Tip: {content_type}): {body_start}")
        
        zip_bytes = resp.read()
    
    dest_p = Path(dest_dir)
    dest_p.mkdir(parents=True, exist_ok=True)
    
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as z:
        z.extractall(dest_p)
    
    for sh in dest_p.glob("*.sh"):
        sh.chmod(0o755)
    
    converter = dest_p / "files" / "converter_multi"
    if converter.exists():
        converter.chmod(0o755)
        
    return dest_p

def _fallback_online_data():
    """Rezervni podaci ukoliko računar trenutno nema pristup internetu."""
    return {
        "success": False,
        "title": "Windows 11 Najnovije Kumulativno Izdanje (Offline procena)",
        "build_num": 26200,
        "sp_num": 8037,
        "version_str": "10.0.26200.8037",
        "checked_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S") + " (Offline)"
    }

def compare_versions(usb_wim_info, staging_info, online_info):
    """
    Poređenje tri ključna stanja:
    1. Verzija na ubačenom USB-u
    2. Verzija pripremljena u programu (staging)
    3. Najnovija verzija online kod Microsofta

    Vraća statusnu matricu i preporučenu akciju.
    """
    usb_build = usb_wim_info.get("build", 0) if usb_wim_info else 0
    usb_spbuild = usb_wim_info.get("spbuild", 0) if usb_wim_info else 0

    staging_build = staging_info.get("build", 0) if staging_info else 0
    staging_spbuild = staging_info.get("spbuild", 0) if staging_info else 0

    online_build = online_info.get("build_num", 0) if online_info else 0
    online_spbuild = online_info.get("sp_num", 0) if online_info else 0

    usb_tuple = (usb_build, usb_spbuild)
    staging_tuple = (staging_build, staging_spbuild)
    online_tuple = (online_build, online_spbuild)

    # 1. Provera da li na USB-u može da se uradi upgrade iz programa
    usb_upgrade_available = False
    if staging_info.get("has_staging"):
        if staging_tuple > usb_tuple:
            usb_upgrade_available = True

    # 2. Provera da li je u programu dostupna novija verzija online
    program_update_available = False
    if online_tuple > staging_tuple and online_build > 0:
        program_update_available = True

    # Status kôd i tekst
    if usb_build == 0:
        status_code = "NO_WIM_ON_USB"
        status_badge = "Nema WIM-a"
        action_text = "Potrebna instalacija GWADT sistema na USB"
    elif usb_upgrade_available:
        status_code = "USB_UPGRADE_AVAILABLE"
        status_badge = "Dostupna Nadogradnja USB-a"
        action_text = f"U programu je spremna novija verzija (Build {staging_info.get('version_str')}). Kliknite 'Nadogradi USB'."
    elif usb_tuple >= online_tuple and usb_build > 0:
        status_code = "UP_TO_DATE"
        status_badge = "USB je Ažuran (Up-to-Date)"
        action_text = "USB fleš disk ima najnoviju verziju sistema. Spreman za rad!"
    elif program_update_available:
        status_code = "PROGRAM_UPDATE_AVAILABLE"
        status_badge = "Dostupne Nove Zakrpe Online"
        action_text = f"Microsoft je objavio novije ažuriranje (Build {online_info.get('version_str')}). Možete osvežiti sistem u programu."
    else:
        status_code = "STABLE"
        status_badge = "Sistem je Usklađen"
        action_text = "Verzija na USB-u odgovara verziji u programu."

    return {
        "status_code": status_code,
        "status_badge": status_badge,
        "action_text": action_text,
        "usb_upgrade_available": usb_upgrade_available,
        "program_update_available": program_update_available,
        "usb_version": f"{usb_build}.{usb_spbuild}" if usb_build > 0 else "Nema",
        "staging_version": staging_info.get("version_str", "Nema"),
        "online_version": online_info.get("version_str", "N/A")
    }
