#!/usr/bin/env python3
"""
wis_nas_space_guard.py
======================
Host skripta za TrueNAS storage higijenu i zaštitu slobodnog prostora (WIS v3.5.4 / ServiceHub v3.7.1).

Funkcionalnosti:
1. U `NORMAL` režimu (bez čekanja da slobodan prostor padne ispod 12 GB!) čisti siroče
   `.tmp`, `.part`, `.dedup.tmp`, `.harvest.lock` i `.tar.tmp.<pid>` fajlove starije od
   30 minuta (`1800s`) u svim radnim direktorijumima (`TMP_SCAN_DIRS`).
2. Pre svakog brisanja izvršava host `lsof` proveru kako bi osigurala da `smbd`, `python3`
   ili drugi aktivni proces ne drži fajl otvorenim.
3. U `LOW_DISK` režimu (`free_gb < 12.0`) dodatno izvršava LRU evikciju najstarijih
   keširanih `.tar` (i pratećih `.json`) bundle-ova u `BUNDLES_DIR`.
4. Strogo odbija praćenje simboličkih linkova i path traversal.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
from datetime import datetime
from typing import Dict, Iterable, List, Optional, Sequence, Set, Tuple

BUNDLES_DIR = "/mnt/TK_NAS/WIS_Cache/bundles"

TMP_SCAN_DIRS: Tuple[str, ...] = (
    "/mnt/TK_NAS/WIS_Cache/bundles",
    "/mnt/TK_NAS/WIS_Cache/drivers_cache",
    "/mnt/TK_NAS/WIS/shared/Drivers/Harvested/Archives",
    "/mnt/TK_NAS/WIS/shared/Drivers/Harvested",
    "/mnt/TK_NAS/ServiceHub/data",
)

ORPHAN_MAX_AGE_SEC = 1800  # 30 minuta
LOW_DISK_THRESHOLD_GB = 12.0
TARGET_FREE_GB = 16.0

ORPHAN_SUFFIXES: Tuple[str, ...] = (
    ".tmp",
    ".part",
    ".dedup.tmp",
    ".harvest.lock",
)
ACTIVE_LOCK_PROCESS_PREFIXES: Tuple[str, ...] = ("smbd", "python")


def is_orphan_temp_or_lock_name(filename: str) -> bool:
    """
    Vraća True ako naziv fajla odgovara privremenom ili lock fajlu
    (`.tmp`, `.part`, `.dedup.tmp`, `.harvest.lock` ili `.tar.tmp.<pid>`).
    """
    low = (filename or "").strip().lower()
    if not low or low in (".", ".."):
        return False
    if low.endswith(ORPHAN_SUFFIXES):
        return True
    if ".tar.tmp." in low:
        return True
    return False


def get_open_files_via_lsof(
    candidate_paths: Sequence[str],
    timeout: float = 15.0,
) -> Tuple[Dict[str, str], bool]:
    """
    Proverava preko host `lsof -F pcn -- <paths>` da li neki proces (naročito `smbd` ili `python3`)
    drži otvorenim bilo koji od kandidata za brisanje.

    :return: `(open_map, lsof_ok)` gde je `open_map` `{realpath: process_name}`,
             a `lsof_ok` False ako je `lsof` nedostupan ili je vratio neočekivanu grešku.
    """
    clean_paths = [os.path.abspath(p) for p in candidate_paths if p]
    if not clean_paths:
        return {}, True

    lsof_bin = shutil.which("lsof")
    if not lsof_bin:
        return {}, False

    open_map: Dict[str, str] = {}
    batch_size = 100

    for idx in range(0, len(clean_paths), batch_size):
        batch = clean_paths[idx : idx + batch_size]
        try:
            proc = subprocess.run(
                [lsof_bin, "-F", "pcn", "--", *batch],
                capture_output=True,
                text=True,
                timeout=timeout,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            return open_map, False

        # lsof vraća 0 ako ima otvorenih fajlova, 1 ako nijedan nije otvoren
        if proc.returncode not in (0, 1):
            return open_map, False

        cur_cmd = ""
        for line in (proc.stdout or "").splitlines():
            if not line:
                continue
            tag, val = line[0], line[1:]
            if tag == "c":
                cur_cmd = val.strip()
            elif tag == "n":
                rp = os.path.realpath(val)
                open_map[rp] = cur_cmd or "unknown"

    return open_map, True


def _collect_orphan_candidates(
    scan_dirs: Iterable[str],
    max_age_sec: float,
    now: float,
) -> List[Tuple[str, str, int, float]]:
    """
    Prikuplja kandidate `(base_dir, real_file_path, size_bytes, mtime)` starije od `max_age_sec`
    bez praćenja symlinkova i bez izlaska van `base_dir`.
    """
    candidates: List[Tuple[str, str, int, float]] = []
    seen_real: Set[str] = set()
    recent_tmp_stems: Set[str] = set()

    # Prvi prolaz: zabeleži sve aktivne/sveže .tar.tmp* stemove kako ne bismo obrisali njihov .harvest.lock
    for base_dir in scan_dirs:
        if not base_dir or os.path.islink(base_dir) or not os.path.isdir(base_dir):
            continue
        real_base = os.path.realpath(base_dir)
        for cur_dir, dirnames, filenames in os.walk(real_base, followlinks=False):
            dirnames[:] = [
                d for d in sorted(dirnames)
                if not os.path.islink(os.path.join(cur_dir, d))
            ]
            for fn in filenames:
                low_fn = fn.lower()
                if ".tar.tmp" not in low_fn and not low_fn.endswith((".tmp", ".part", ".dedup.tmp")):
                    continue
                fp = os.path.join(cur_dir, fn)
                if os.path.islink(fp):
                    continue
                try:
                    st = os.lstat(fp)
                    age = now - float(st.st_mtime)
                    if age < max_age_sec:
                        stem = low_fn.split(".tar.tmp")[0]
                        recent_tmp_stems.add(os.path.join(cur_dir, stem))
                except OSError:
                    continue

    # Drugi prolaz: prikupi siročiće starije od max_age_sec
    for base_dir in scan_dirs:
        if not base_dir or os.path.islink(base_dir) or not os.path.isdir(base_dir):
            continue
        real_base = os.path.realpath(base_dir)
        for cur_dir, dirnames, filenames in os.walk(real_base, followlinks=False):
            dirnames[:] = [
                d for d in sorted(dirnames)
                if not os.path.islink(os.path.join(cur_dir, d))
            ]
            for fn in sorted(filenames):
                if not is_orphan_temp_or_lock_name(fn):
                    continue
                fp = os.path.join(cur_dir, fn)
                if os.path.islink(fp):
                    continue
                real_fp = os.path.realpath(fp)
                if os.path.commonpath([real_base, real_fp]) != real_base:
                    continue
                if real_fp in seen_real:
                    continue
                try:
                    st = os.lstat(real_fp)
                except OSError:
                    continue
                if not os.path.isfile(real_fp):
                    continue
                age = now - float(st.st_mtime)
                if age < max_age_sec:
                    continue

                low_fn = fn.lower()
                if low_fn.endswith(".harvest.lock"):
                    lock_stem = low_fn[: -len(".harvest.lock")]
                    if os.path.join(cur_dir, lock_stem) in recent_tmp_stems:
                        continue

                seen_real.add(real_fp)
                candidates.append((real_base, real_fp, int(st.st_size), float(st.st_mtime)))

    return candidates


def cleanup_orphan_temp_files(
    scan_dirs: Sequence[str] = TMP_SCAN_DIRS,
    max_age_sec: float = ORPHAN_MAX_AGE_SEC,
    dry_run: bool = False,
    now: Optional[float] = None,
    open_files_fn=get_open_files_via_lsof,
) -> Dict[str, object]:
    """
    Briše siroče `.tmp`, `.part`, `.dedup.tmp` i `.harvest.lock` fajlove starije od
    `max_age_sec` (podrazumevano 1800s = 30 min) u `NORMAL` i `LOW_DISK` režimima,
    nakon provere preko host `lsof` da fajl nije otvoren.
    """
    cur_ts = float(now) if now is not None else time.time()
    candidates = _collect_orphan_candidates(scan_dirs, max_age_sec=max_age_sec, now=cur_ts)

    removed = 0
    freed_bytes = 0
    skipped_open = 0
    errors: List[str] = []

    if not candidates:
        return {
            "candidates": 0,
            "removed": 0,
            "freed_bytes": 0,
            "skipped_open": 0,
            "lsof_ok": True,
            "errors": [],
        }

    open_map, lsof_ok = open_files_fn([c[1] for c in candidates])
    if not lsof_ok:
        # Fail-safe: ako lsof ne radi, ne brišemo kandidate da ne bismo prekinuli aktivan SMB/Python upis
        return {
            "candidates": len(candidates),
            "removed": 0,
            "freed_bytes": 0,
            "skipped_open": len(candidates),
            "lsof_ok": False,
            "errors": ["lsof provera nije uspela; brisanje preskočeno iz bezbednosnih razloga"],
        }

    for _, real_fp, sz, _ in candidates:
        holder = open_map.get(real_fp)
        if holder is not None:
            skipped_open += 1
            continue
        if dry_run:
            removed += 1
            freed_bytes += sz
            continue
        try:
            os.remove(real_fp)
            removed += 1
            freed_bytes += sz
        except OSError as ex:
            errors.append(f"{real_fp}: {ex}")

    return {
        "candidates": len(candidates),
        "removed": removed,
        "freed_bytes": freed_bytes,
        "skipped_open": skipped_open,
        "lsof_ok": True,
        "errors": errors,
    }


def _get_dir_size_bytes(directory: str) -> int:
    if not directory or os.path.islink(directory) or not os.path.isdir(directory):
        return 0
    total = 0
    real_base = os.path.realpath(directory)
    try:
        with os.scandir(real_base) as it:
            for entry in it:
                if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
                    continue
                try:
                    total += int(entry.stat(follow_symlinks=False).st_size)
                except OSError:
                    continue
    except OSError:
        pass
    return total


def _evict_lru_bundles(
    bundles_dir: str,
    bytes_to_free: int,
    dry_run: bool = False,
    open_files_fn=get_open_files_via_lsof,
) -> Dict[str, int]:
    """U LOW_DISK režimu briše najstarije `.tar` (i parne `.json`) bundle fajlove koji nisu otvoreni."""
    if bytes_to_free <= 0 or not bundles_dir or os.path.islink(bundles_dir) or not os.path.isdir(bundles_dir):
        return {"removed": 0, "freed_bytes": 0, "skipped_open": 0}

    real_base = os.path.realpath(bundles_dir)
    tar_entries: List[Tuple[float, str, int]] = []
    try:
        with os.scandir(real_base) as it:
            for entry in it:
                if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
                    continue
                if not entry.name.lower().endswith(".tar"):
                    continue
                real_fp = os.path.realpath(entry.path)
                if os.path.commonpath([real_base, real_fp]) != real_base:
                    continue
                try:
                    st = entry.stat(follow_symlinks=False)
                    tar_entries.append((float(st.st_mtime), real_fp, int(st.st_size)))
                except OSError:
                    continue
    except OSError:
        return {"removed": 0, "freed_bytes": 0, "skipped_open": 0}

    tar_entries.sort(key=lambda x: x[0])
    open_map, lsof_ok = open_files_fn([x[1] for x in tar_entries])
    if not lsof_ok:
        return {"removed": 0, "freed_bytes": 0, "skipped_open": len(tar_entries)}

    removed = 0
    freed = 0
    skipped_open = 0

    for _, real_fp, sz in tar_entries:
        if freed >= bytes_to_free:
            break
        if real_fp in open_map:
            skipped_open += 1
            continue
        meta_fp = f"{real_fp[:-4]}.json"
        if not dry_run:
            try:
                os.remove(real_fp)
                removed += 1
                freed += sz
                if os.path.isfile(meta_fp) and not os.path.islink(meta_fp):
                    os.remove(meta_fp)
            except OSError:
                continue
        else:
            removed += 1
            freed += sz

    return {"removed": removed, "freed_bytes": freed, "skipped_open": skipped_open}


def run_space_guard(
    bundles_dir: str = BUNDLES_DIR,
    tmp_scan_dirs: Sequence[str] = TMP_SCAN_DIRS,
    low_disk_threshold_gb: float = LOW_DISK_THRESHOLD_GB,
    target_free_gb: float = TARGET_FREE_GB,
    orphan_max_age_sec: float = ORPHAN_MAX_AGE_SEC,
    dry_run: bool = False,
    now: Optional[float] = None,
    open_files_fn=get_open_files_via_lsof,
    disk_usage_fn=shutil.disk_usage,
) -> Dict[str, object]:
    """
    Glavni prolaz Space Guard-a:
    - Uvek (i u NORMAL režimu) čisti siroče temp/lock fajlove starije od 30 min (`1800s`).
    - Ako je slobodan prostor ispod `low_disk_threshold_gb` (`12 GB`), prelazi u `LOW_DISK`
      režim i radi LRU evikciju najstarijih bundle fajlova.
    """
    probe_dir = bundles_dir
    while probe_dir and not os.path.exists(probe_dir):
        parent = os.path.dirname(probe_dir)
        if parent == probe_dir:
            break
        probe_dir = parent

    free_gb = 0.0
    try:
        du = disk_usage_fn(probe_dir or "/")
        free_gb = float(du.free) / (1024.0 ** 3)
    except OSError:
        free_gb = 0.0

    mode = "LOW_DISK" if free_gb < float(low_disk_threshold_gb) else "NORMAL"
    bundles_before_gb = round(_get_dir_size_bytes(bundles_dir) / (1024.0 ** 3), 2)

    orphan_res = cleanup_orphan_temp_files(
        scan_dirs=tmp_scan_dirs,
        max_age_sec=orphan_max_age_sec,
        dry_run=dry_run,
        now=now,
        open_files_fn=open_files_fn,
    )

    bundles_evicted = 0
    bundles_freed_bytes = 0
    skipped_open = int(orphan_res["skipped_open"])

    if mode == "LOW_DISK":
        need_bytes = int(max(0.0, float(target_free_gb) - free_gb) * (1024.0 ** 3))
        evict_res = _evict_lru_bundles(
            bundles_dir=bundles_dir,
            bytes_to_free=need_bytes,
            dry_run=dry_run,
            open_files_fn=open_files_fn,
        )
        bundles_evicted = evict_res["removed"]
        bundles_freed_bytes = evict_res["freed_bytes"]
        skipped_open += evict_res["skipped_open"]

    total_removed = int(orphan_res["removed"]) + bundles_evicted
    total_freed = int(orphan_res["freed_bytes"]) + bundles_freed_bytes

    return {
        "timestamp": datetime.now().isoformat(),
        "mode": mode,
        "free_gb": round(free_gb, 2),
        "bundles_before_gb": bundles_before_gb,
        "removed": total_removed,
        "orphan_tmp_removed": int(orphan_res["removed"]),
        "bundles_removed": bundles_evicted,
        "freed_bytes": total_freed,
        "skipped_open": skipped_open,
        "lsof_ok": bool(orphan_res["lsof_ok"]),
        "dry_run": dry_run,
    }


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="WIS NAS Space Guard (v3.5.4)")
    parser.add_argument("--bundles-dir", default=BUNDLES_DIR)
    parser.add_argument("--min-free-gb", type=float, default=LOW_DISK_THRESHOLD_GB)
    parser.add_argument("--target-free-gb", type=float, default=TARGET_FREE_GB)
    parser.add_argument("--orphan-age-sec", type=int, default=ORPHAN_MAX_AGE_SEC)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    res = run_space_guard(
        bundles_dir=args.bundles_dir,
        tmp_scan_dirs=TMP_SCAN_DIRS,
        low_disk_threshold_gb=args.min_free_gb,
        target_free_gb=args.target_free_gb,
        orphan_max_age_sec=args.orphan_age_sec,
        dry_run=args.dry_run,
    )
    print(
        f"[SPACE_GUARD] mode={res['mode']}, free={res['free_gb']}GB, "
        f"bundles_before={res['bundles_before_gb']}GB, removed={res['removed']}, "
        f"orphan_tmp={res['orphan_tmp_removed']}, skipped_open={res['skipped_open']}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
