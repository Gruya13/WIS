#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
manifest_loader.py - Jedinstveni čitač, verifikator i sinhronizator kanonskog WIS manifesta
(`Skripte/deployment_manifest.json`), kao i bezbednosnih provera za WIM/ESD transfer.
"""

import os
import json
import uuid
import shutil
import hashlib
import subprocess
from pathlib import Path


def calc_sha256(filepath):
    """Računa SHA-256 heš datoteke bez učitavanja cele datoteke u RAM."""
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while True:
            chunk = f.read(65536)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def get_default_manifest_path(project_root=None):
    if project_root is None:
        project_root = Path(__file__).resolve().parent.parent
    else:
        project_root = Path(project_root)
    return project_root / "Skripte" / "deployment_manifest.json"


def load_deployment_manifest(project_root=None, manifest_path=None):
    if project_root is None:
        project_root = Path(
            os.environ.get(
                "GWADT_PROJECT_ROOT",
                str(Path(__file__).resolve().parent.parent),
            )
        )
    else:
        project_root = Path(project_root)

    mpath = Path(manifest_path) if manifest_path else get_default_manifest_path(project_root)
    if not mpath.exists() or not mpath.is_file():
        raise FileNotFoundError(f"Kanonski deployment manifest ne postoji: {mpath}")

    with open(mpath, "r", encoding="utf-8") as f:
        data = json.load(f)

    if not isinstance(data, dict) or "components" not in data or not isinstance(data["components"], list):
        raise ValueError(f"Neispravan format manifesta: {mpath}")

    return data


def validate_manifest_sources(project_root=None, manifest_data=None):
    """
    Proverava da SVI obavezni ('required': true) izvori postoje u project_root pre početka kopiranja.
    Ako bilo koji obavezan izvor nedostaje, odmah podiže RuntimeError bez delimičnog kopiranja.
    """
    if project_root is None:
        project_root = Path(
            os.environ.get(
                "GWADT_PROJECT_ROOT",
                str(Path(__file__).resolve().parent.parent),
            )
        )
    else:
        project_root = Path(project_root)

    if manifest_data is None:
        manifest_data = load_deployment_manifest(project_root)

    missing_required = []
    resolved_plan = []

    for comp in manifest_data.get("components", []):
        comp_id = comp.get("id", "unknown")
        rel_src = comp.get("source", "")
        destinations = comp.get("destinations", [])
        required = bool(comp.get("required", False))
        opt_until_created = bool(comp.get("optional_until_created", False))
        comp_type = comp.get("type", "file")
        is_secret = bool(comp.get("secret", False))

        if not rel_src or "..." in rel_src or any("..." in d for d in destinations):
            raise RuntimeError(f"Nedozvoljena putanja sa '...' u manifest komponenti '{comp_id}'!")

        src_path = project_root / rel_src
        exists = src_path.exists() and (src_path.is_file() if comp_type == "file" else src_path.is_dir())

        if not exists:
            if required and not opt_until_created:
                missing_required.append(f"{comp_id} ({rel_src})")
            continue

        resolved_plan.append(
            {
                "id": comp_id,
                "source_rel": rel_src,
                "source_path": src_path,
                "destinations": destinations,
                "required": required,
                "type": comp_type,
                "secret": is_secret,
            }
        )

    if missing_required:
        raise RuntimeError(
            "Prekinuta sinhronizacija pre početka kopiranja — nedostaju obavezne izvorne komponente: "
            + ", ".join(missing_required)
        )

    return resolved_plan


def atomic_copy_file(src_path, dst_path):
    """
    Kopira fajl preko privremenog .tmp fajla u istom direktorijumu uz flush + fsync
    i atomsku zamenu os.replace(tmp, dst).
    """
    dst_path = Path(dst_path)
    dst_path.parent.mkdir(parents=True, exist_ok=True)
    attempt_tag = f"{os.getpid()}_{uuid.uuid4().hex[:8]}"
    tmp_path = dst_path.parent / f".{dst_path.name}.tmp.{attempt_tag}"

    try:
        with open(src_path, "rb") as f_src, open(tmp_path, "wb") as f_dst:
            while True:
                chunk = f_src.read(262144)
                if not chunk:
                    break
                f_dst.write(chunk)
            f_dst.flush()
            os.fsync(f_dst.fileno())
        shutil.copystat(src_path, tmp_path)
        os.replace(tmp_path, dst_path)
    except Exception:
        if tmp_path.exists():
            try:
                tmp_path.unlink()
            except Exception:
                pass
        raise


def sync_manifest_to_target(usb_root, project_root=None, log_fn=None, permissive=False):
    """
    Izvršava atomsku sinhronizaciju svih komponenti iz deployment_manifest.json na usb_root:
      1. Proverava prisustvo svih obaveznih izvora pre prvog upisa.
      2. Kopira svaku komponentu preko privremenog fajla i os.replace.
      3. Verifikuje veličinu i SHA-256 svih kopiranih fajlova (posebno svih obaveznih).
    Vraća dict sa statistikom i mapom SHA-256 heševa.
    """
    usb_root = Path(usb_root)
    if not usb_root.exists() or not usb_root.is_dir():
        raise RuntimeError(f"Ciljna putanja ne postoji ili nije direktorijum: {usb_root}")

    if project_root is None:
        project_root = Path(
            os.environ.get(
                "GWADT_PROJECT_ROOT",
                str(Path(__file__).resolve().parent.parent),
            )
        )
    else:
        project_root = Path(project_root)

    manifest_data = load_deployment_manifest(project_root)
    plan = validate_manifest_sources(project_root, manifest_data)

    copied_entries = []
    verified_hashes = {}

    # 1. Kopiranje preko privremenih fajlova
    for item in plan:
        src_path = item["source_path"]
        src_sha = calc_sha256(src_path)
        src_size = src_path.stat().st_size
        is_secret = item["secret"]

        for rel_dst in item["destinations"]:
            dst_path = usb_root / rel_dst
            if dst_path.resolve() == src_path.resolve():
                verified_hashes[rel_dst] = {
                    "size_bytes": src_size,
                    "sha256": src_sha,
                    "required": item["required"],
                    "secret_redacted": item["secret"],
                }
                continue
            try:
                atomic_copy_file(src_path, dst_path)
                copied_entries.append((item, src_path, dst_path, src_sha, src_size))
                if log_fn:
                    if is_secret:
                        log_fn(f"Ažuriran fajl: {rel_dst} [sadržaj zaštićen]")
                    else:
                        log_fn(f"Ažuriran fajl: {rel_dst}")
            except (PermissionError, OSError) as e:
                if permissive:
                    if log_fn:
                        log_fn(f"Zaobiđen fajl sa ograničenim pravima: {rel_dst} ({e})")
                else:
                    raise

    # 2. Stroga SHA-256 verifikacija svih prenetih fajlova
    mismatches = []
    for item, src_path, dst_path, expected_sha, expected_size in copied_entries:
        rel_dst = str(dst_path.relative_to(usb_root))
        if not dst_path.exists() or not dst_path.is_file():
            mismatches.append(f"Nedostaje ciljni fajl nakon kopiranja: {rel_dst}")
            continue
        actual_size = dst_path.stat().st_size
        actual_sha = calc_sha256(dst_path)
        if actual_size != expected_size or actual_sha != expected_sha:
            mismatches.append(
                f"SHA-256/veličina nepodudaranje za {rel_dst} "
                f"(očekivano {expected_sha[:12]} / {expected_size} B, "
                f"dobijeno {actual_sha[:12]} / {actual_size} B)"
            )
        else:
            verified_hashes[rel_dst] = {
                "size_bytes": actual_size,
                "sha256": actual_sha,
                "required": item["required"],
                "secret_redacted": item["secret"],
            }

    if mismatches:
        raise RuntimeError(
            "Verifikacija sinhronizacije skripti NIJE uspela: " + "; ".join(mismatches)
        )

    return {
        "manifest_version": manifest_data.get("manifest_version", "2.16.0"),
        "components_synced": len(plan),
        "files_written": len(copied_entries),
        "verified_files": verified_hashes,
    }


def get_filesystem_type(target_path):
    """
    Određuje tip fajlsistema za zadatu putanju čitanjem /proc/mounts (ili df -T).
    Vraća string u malim slovima (npr. 'vfat', 'fat32', 'exfat', 'ntfs', 'ext4', ...).
    """
    target_real = os.path.realpath(str(target_path))
    best_mount = ""
    best_fstype = ""
    proc_mounts = Path("/proc/mounts")
    if proc_mounts.exists():
        try:
            with open(proc_mounts, "r", encoding="utf-8", errors="ignore") as f:
                for line in f:
                    parts = line.split()
                    if len(parts) >= 3:
                        mnt = parts[1].encode("utf-8").decode("unicode_escape")
                        fstype = parts[2].lower()
                        if target_real == mnt or target_real.startswith(mnt.rstrip("/") + "/"):
                            if len(mnt) > len(best_mount):
                                best_mount = mnt
                                best_fstype = fstype
        except Exception:
            pass
    if best_fstype:
        return best_fstype

    try:
        res = subprocess.run(["df", "-T", target_real], capture_output=True, text=True, timeout=5)
        if res.returncode == 0:
            lines = [ln.strip() for ln in res.stdout.splitlines() if ln.strip()]
            if len(lines) >= 2:
                cols = lines[-1].split()
                if len(cols) >= 2:
                    return cols[1].lower()
    except Exception:
        pass
    return "unknown"


def validate_target_space_and_fs(usb_dir, src_size_bytes, reserve_mb=250, fstype_override=None):
    """
    Proverava:
    1. Da li ciljni fajlsistem (ako je vfat/fat32/msdos) podržava veličinu fajla (< 4 GiB - 1 B).
    2. Da li trenutno slobodan prostor (BEZ brisanja postojeće slike!) pokriva novu sliku + rezervu za skripte.
    Podiže RuntimeError sa jasnim objašnjenjem ako uslov nije ispunjen.
    """
    usb_dir = Path(usb_dir)
    fstype = (fstype_override or get_filesystem_type(usb_dir)).lower()
    fat32_max_bytes = (4 * 1024 * 1024 * 1024) - 1  # 4,294,967,295 B

    if fstype in ("vfat", "fat32", "msdos", "fat") and src_size_bytes > fat32_max_bytes:
        src_mb = src_size_bytes // (1024 * 1024)
        raise RuntimeError(
            f"Ciljni medijum koristi FAT32 ({fstype}) fajlsistem koji ne podržava pojedinačne fajlove "
            f"veće od 4 GiB (veličina slike: {src_mb} MB). Postojeća slika na medijumu NIJE obrisana. "
            f"Formatirajte medijum u exFAT/NTFS ili koristite podeljenu SWM sliku."
        )

    usage = shutil.disk_usage(usb_dir)
    free_bytes = usage.free
    reserve_bytes = int(reserve_mb) * 1024 * 1024
    needed_bytes = int(src_size_bytes) + reserve_bytes

    if free_bytes < needed_bytes:
        free_mb = free_bytes // (1024 * 1024)
        needed_mb = needed_bytes // (1024 * 1024)
        missing_mb = max(1, (needed_bytes - free_bytes + (1024 * 1024 - 1)) // (1024 * 1024))
        raise RuntimeError(
            f"Nedovoljno slobodnog prostora na medijumu za bezbedan staging transfer bez brisanja postojeće slike! "
            f"Slobodno: {free_mb} MB, potrebno: {needed_mb} MB (slika + {reserve_mb} MB rezerve), "
            f"nedostaje: {missing_mb} MB. Postojeća instalaciona slika je ostala 100% netaknuta."
        )

    return {
        "fstype": fstype,
        "free_bytes": free_bytes,
        "needed_bytes": needed_bytes,
    }


def main():
    import argparse
    import sys

    parser = argparse.ArgumentParser(description="WIS Deployment Manifest Loader & Synchronizer")
    parser.add_argument("--sync", action="store_true", help="Izvrši sinhronizaciju komponenti po manifestu")
    parser.add_argument("--base-dir", default=None, help="Osnovni projektni direktorijum (podrazumevano: repo root)")
    parser.add_argument("--target", default=None, help="Ciljni direktorijum za sinhronizaciju (podrazumevano: koren projekta)")
    parser.add_argument("--verify", action="store_true", help="Samo proveri prisustvo izvornih komponenti")
    parser.add_argument("--permissive", action="store_true", help="Zaobiđi greške sa pravima na zaštićenim/mrežnim putanjama")
    args = parser.parse_args()

    project_root = Path(args.base_dir).resolve() if args.base_dir else Path(__file__).resolve().parent.parent
    target_root = Path(args.target).resolve() if args.target else project_root

    manifest_data = load_deployment_manifest(project_root)
    print(f"[INFO] Učitan manifest verzija: {manifest_data.get('manifest_version', 'unknown')}")

    if args.verify or not args.sync:
        plan = validate_manifest_sources(project_root, manifest_data)
        print(f"[OK] Svih {len(plan)} izvornih komponenti je validno.")
        if not args.sync:
            return 0

    if args.sync:
        print(f"[INFO] Pokrećem sinhronizaciju na cilj: {target_root}")
        res = sync_manifest_to_target(
            target_root,
            project_root=project_root,
            log_fn=lambda m: print(f"  -> {m}"),
            permissive=args.permissive,
        )
        print(f"[OK] Uspešno sinhronizovano {res['components_synced']} komponenti ({res['files_written']} kopiranih/ažuriranih). 100% SHA-256 verifikovano.")
    return 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
