#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Sync-FirstLogon.py - Sinhronizacija FirstLogon.cmd i FirstLogon.ps1 na svim lokacijama
Garantuje 100% binarni paritet (CRLF, UTF-8, MD5 / SHA-256) u projektu i na Storage NAS-u.
"""

import hashlib
import os
import sys
from pathlib import Path

def sync_pair(source_path: Path, target_paths: list):
    if not source_path.exists():
        print(f"[ERROR] Kanonski izvor ne postoji: {source_path}")
        return False

    raw_text = source_path.read_text(encoding="utf-8")
    # Garantovan CRLF
    crlf_text = raw_text.replace("\r\n", "\n").replace("\n", "\r\n")
    data = crlf_text.encode("utf-8")
    src_md5 = hashlib.md5(data).hexdigest()
    src_sha256 = hashlib.sha256(data).hexdigest()

    print(f"\nKanonski izvor: {source_path}")
    print(f"  Veličina: {len(data)} B | MD5: {src_md5} | SHA256: {src_sha256[:16]}...")

    success = True
    for target in target_paths:
        # Provera da li je putanja unutar read-only montiranog ISO-a (npr. win11 ili sources symlink na NAS-u)
        target_str = str(target)
        if "/Storage/sources/" in target_str or "/Storage/win11/" in target_str:
            # Provera da li je fajl writable pre pokušaja
            try:
                if not os.access(target, os.W_OK):
                    print(f"  [INFO] Preskoceno {target} (montiran Read-Only ISO share na NAS-u)")
                    continue
            except Exception:
                continue

        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            t_md5 = hashlib.md5(target.read_bytes()).hexdigest()
            if t_md5 == src_md5:
                print(f"  [OK] {target} -> 100% paritet (MD5={t_md5})")
            else:
                print(f"  [FAIL] {target} -> Heš se ne poklapa!")
                success = False
        except Exception as e:
            if "/Storage/sources/" in target_str or "/Storage/win11/" in target_str:
                print(f"  [INFO] Preskoceno {target} (montiran Read-Only ISO: {e})")
            else:
                print(f"  [WARN] Greska za {target}: {e}")
                success = False
    return success

def main():
    proj_root = Path(__file__).resolve().parent.parent
    storage_root = Path("/home/gru/Storage")

    cmd_source = proj_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.cmd"
    ps1_source = proj_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.ps1"

    cmd_targets = [
        proj_root / "FirstLogon.cmd",
        proj_root / "Skripte" / "FirstLogon.cmd",
        proj_root / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "FirstLogon.cmd",
        proj_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.cmd",
    ]

    ps1_targets = [
        proj_root / "FirstLogon.ps1",
        proj_root / "Skripte" / "FirstLogon.ps1",
        proj_root / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "FirstLogon.ps1",
        proj_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.ps1",
    ]

    if storage_root.exists() and os.access(storage_root, os.W_OK):
        cmd_targets.extend([
            storage_root / "FirstLogon.cmd",
            storage_root / "Skripte" / "FirstLogon.cmd",
            storage_root / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "FirstLogon.cmd",
            storage_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.cmd",
        ])
        ps1_targets.extend([
            storage_root / "FirstLogon.ps1",
            storage_root / "Skripte" / "FirstLogon.ps1",
            storage_root / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "FirstLogon.ps1",
            storage_root / "sources" / "$OEM$" / "$1" / "setup" / "FirstLogon.ps1",
        ])

    ok1 = sync_pair(cmd_source, cmd_targets)
    ok2 = sync_pair(ps1_source, ps1_targets)

    if ok1 and ok2:
        print("\n[USPEH] Svi FirstLogon fajlovi su uspešno sinhronizovani uz 100% binarni paritet!")
        return 0
    else:
        print("\n[GREŠKA] Došlo je do problema tokom sinhronizacije.")
        return 1

if __name__ == "__main__":
    sys.exit(main())

