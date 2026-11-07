# -*- coding: utf-8 -*-
"""
system_inspector.py - Inspekcija instalacionog medijuma i WIM metapodataka
Prepoznaje GWADT markere na USB flešu i nativno očitava Windows 11 Build broj.
"""

import os
import sys
import struct
import xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime

def read_wim_metadata(wim_path):
    """
    Pouzdano čitanje WIM/ESD zaglavlja i XML metapodataka.
    Prvo koristi wimlib-imagex ako je dostupan, uz podršku za nativni binarni fallback.
    Vraća listu edicija sa verzijama i podacima o build-u.
    """
    if not wim_path or not os.path.isfile(wim_path):
        return None

    # Metoda 1: wimlib-imagex info --xml (100% podrška za sve WIM/ESD formate i kompresije)
    try:
        proc = subprocess.run(["wimlib-imagex", "info", str(wim_path), "--xml"], capture_output=True, timeout=10)
        if proc.returncode == 0 and proc.stdout:
            raw = proc.stdout
            if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
                xml_str = raw.decode("utf-16", errors="ignore")
            else:
                xml_str = raw.decode("utf-8", errors="ignore")
            xml_str = xml_str.strip().lstrip("\ufeff")
            
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
            if editions:
                return editions
    except Exception:
        pass

    # Metoda 2: Nativno čitanje binarnog WIM zaglavlja
    try:
        with open(wim_path, "rb") as f:
            header = f.read(208)
            if len(header) < 208 or header[:8] != b"MSWIM\0\0\0":
                return None
            
            # Offset 0x48: XML data reshdr (flags_and_size, offset, original_size)
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
            
            xml_str = xml_str.strip().lstrip("\ufeff")
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
        print(f"[system_inspector] Greška pri čitanju WIM fajla '{wim_path}': {e}", file=sys.stderr)
        return None

def inspect_gwadt_system(target_dir):
    """
    Detaljno analizira direktorijum USB diska ili repozitorijuma
    i utvrđuje da li je na njemu prisutan GWADT sistem i koja je verzija.
    """
    if not target_dir:
        return {
            "is_gwadt": False,
            "status_text": "Nema definisane putanje",
            "markers": []
        }

    root = Path(target_dir)
    if not root.exists() or not root.is_dir():
        return {
            "is_gwadt": False,
            "status_text": "Putanja ne postoji",
            "markers": []
        }

    # Provera markera prisustva GWADT sistema
    markers = {}
    
    # 1. WIM/ESD slika
    wim_candidates = [
        root / "sources" / "install.wim",
        root / "sources" / "install.esd",
        root / "install.wim"
    ]
    wim_file = next((w for w in wim_candidates if w.exists() and w.is_file()), None)
    markers["wim"] = wim_file is not None

    # 2. autounattend.xml
    unattend = root / "autounattend.xml"
    markers["autounattend"] = unattend.exists() and unattend.is_file()

    # 3. FirstLogon.cmd
    firstlogon = root / "FirstLogon.cmd"
    markers["firstlogon"] = firstlogon.exists() and firstlogon.is_file()

    # 4. Deploy skripta Install-ServisApps.ps1
    script_candidates = [
        root / "Install-ServisApps.ps1",
        root / "Skripte" / "Install-ServisApps.ps1",
        root / "sources" / "$OEM$" / "$1" / "setup" / "Install-ServisApps.ps1"
    ]
    deploy_script = next((s for s in script_candidates if s.exists() and s.is_file()), None)
    markers["deploy_script"] = deploy_script is not None

    # 5. Programi i Zakrpe folderi
    markers["programi"] = (root / "Programi").is_dir()
    markers["zakrpe"] = (root / "Zakrpe").is_dir()

    # GWADT sistem se smatra prepoznatim ako ima WIM ili autounattend ili deploy skriptu
    is_gwadt = markers["wim"] or (markers["autounattend"] and markers["deploy_script"])

    # Detalji o WIM slici ako postoji
    wim_details = {
        "exists": False,
        "path": None,
        "size_bytes": 0,
        "size_mb": 0,
        "edition": "N/A",
        "build": 0,
        "spbuild": 0,
        "version_str": "N/A",
        "editions_count": 0
    }

    if wim_file:
        try:
            stat = wim_file.stat()
            size_bytes = stat.st_size
            size_mb = size_bytes // (1024 * 1024)
            editions = read_wim_metadata(str(wim_file))
            
            if editions:
                # Preferiramo Pro ediciju
                pro_ed = next((e for e in editions if "Pro" in e["name"] and "Workstation" not in e["name"]), editions[0])
                wim_details = {
                    "exists": True,
                    "path": str(wim_file),
                    "size_bytes": size_bytes,
                    "size_mb": size_mb,
                    "edition": pro_ed.get("name", "Windows 11"),
                    "build": pro_ed.get("build", 0),
                    "spbuild": pro_ed.get("spbuild", 0),
                    "version_str": pro_ed.get("version_str", "N/A"),
                    "editions_count": len(editions)
                }
            else:
                wim_details = {
                    "exists": True,
                    "path": str(wim_file),
                    "size_bytes": size_bytes,
                    "size_mb": size_mb,
                    "edition": "Windows 11 (Neparsirano)",
                    "build": 0,
                    "spbuild": 0,
                    "version_str": "Postoji WIM slika",
                    "editions_count": 1
                }
        except Exception as e:
            print(f"[system_inspector] Greška pri inspekciji WIM-a: {e}", file=sys.stderr)

    # Detalji o deploy skripti
    script_mtime_str = "N/A"
    if deploy_script:
        try:
            mtime = deploy_script.stat().st_mtime
            script_mtime_str = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M")
        except Exception:
            pass

    status_text = "GWADT Automatizovani Instalacioni Sistem" if is_gwadt else "Standardni Skladišni Medijum (Nije GWADT)"

    return {
        "is_gwadt": is_gwadt,
        "status_text": status_text,
        "target_path": str(root),
        "markers": markers,
        "wim": wim_details,
        "script": {
            "present": deploy_script is not None,
            "path": str(deploy_script) if deploy_script else None,
            "last_modified": script_mtime_str
        },
        "pipeline_integrity": inspect_pipeline_integrity(root),
        "has_wifi_config": (root / "Wifi-Servis.ini").exists()
    }


def inspect_pipeline_integrity(target_dir, project_root=None):
    """
    Poređenje integriteta automatizacionog koda (pajplajna) između USB-a i radnog projekta.
    Otkriva da li su skripte, moduli ili XML konfiguracije izmenjeni u projektu,
    a da još uvek nisu sinhronizovani na USB fleš medijum.
    """
    if project_root is None:
        project_root = Path(os.environ.get("GWADT_PROJECT_ROOT", str(Path(__file__).resolve().parent.parent.parent)))
    else:
        project_root = Path(project_root)

    target_root = Path(target_dir)
    project_version_file = project_root / "version.json"
    usb_version_file = target_root / "version.json"

    project_manifest = {}
    if project_version_file.exists():
        try:
            import json
            with open(project_version_file, "r", encoding="utf-8") as pvf:
                project_manifest = json.load(pvf)
        except Exception:
            pass

    usb_manifest = {}
    if usb_version_file.exists():
        try:
            import json
            with open(usb_version_file, "r", encoding="utf-8") as uvf:
                usb_manifest = json.load(uvf)
        except Exception:
            pass

    proj_hash = project_manifest.get("pipeline_hash", "")
    usb_hash = usb_manifest.get("pipeline_hash", "")
    proj_ver = project_manifest.get("pipeline_version", "2.5.0")
    usb_ver = usb_manifest.get("pipeline_version", "N/A")

    # Ako na USB-u nema version.json, računamo stvarni hash skripti sa USB-a
    diff_files = []
    if not usb_hash and target_root.exists():
        import hashlib
        proj_files = project_manifest.get("files", {})
        if proj_files:
            for fname, expected_hash in proj_files.items():
                f_candidate = target_root / fname
                if not f_candidate.exists():
                    f_candidate = target_root / "Skripte" / fname
                if f_candidate.exists() and f_candidate.is_file():
                    try:
                        with open(f_candidate, "rb") as cf:
                            actual_h = hashlib.sha256(cf.read()).hexdigest()
                            if actual_h != expected_hash:
                                diff_files.append(fname)
                    except Exception:
                        diff_files.append(fname)
                else:
                    diff_files.append(fname)

    is_synced = False
    if proj_hash and usb_hash:
        is_synced = (proj_hash == usb_hash)
        if not is_synced:
            diff_files = ["Skripte/Moduli/Konfiguracije (Razlika u verziji)"]
    elif not diff_files and (target_root / "Install-ServisApps.ps1").exists():
        is_synced = True

    if is_synced:
        status_badge = "Pajplajn Usklađen"
        badge_class = "badge-success"
        status_msg = f"Sve deploy skripte i moduli na USB-u su 100% usklađeni sa programom (v{proj_ver})."
    else:
        status_badge = "Pajplajn Zastareo"
        badge_class = "badge-warning"
        status_msg = "Skripte ili konfiguracija su menjani u programu. Preporučuje se brzo osvežavanje (1s)."

    return {
        "is_synced": is_synced,
        "project_version": proj_ver,
        "usb_version": usb_ver if usb_ver != "N/A" else ("v" + proj_ver if is_synced else "Starija verzija"),
        "project_hash": proj_hash[:16] + "..." if proj_hash else "N/A",
        "usb_hash": usb_hash[:16] + "..." if usb_hash else "N/A",
        "diff_files": diff_files,
        "status_badge": status_badge,
        "badge_class": badge_class,
        "status_message": status_msg
    }
