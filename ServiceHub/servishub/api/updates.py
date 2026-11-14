"""
servishub.api.updates
=====================
REST API ruter i logistika za inspekciju, preuzimanje, upload i keširanje skladišta Windows zakrpi (.msu / .cab).
"""

import hashlib
import io
import json
import os
import re
import time
import urllib.request
from datetime import datetime
from typing import Any, BinaryIO, Dict, List, Optional
from urllib.parse import urlparse

from servishub.models.updates import UpdatePackageItem, UpdatesStatusResponse


_VERSION_TO_BUILD = {
    "21H2": "22000",
    "22H2": "22621",
    "23H2": "22631",
    "24H2": "26100",
    "25H2": "26200",
}

_KNOWN_BUILDS = ("26200", "26100", "22631", "22621", "22000")


def extract_os_build(text: Optional[str]) -> Optional[str]:
    """
    Izvlači ciljni Windows 11 OS build broj (npr. '26100', '22631') iz naziva fajla,
    naslova zakrpe ili eksplicitnog parametra (podržava i oznake '22H2'..'25H2').
    """
    if not text:
        return None
    raw = str(text).strip()
    if not raw:
        return None

    if re.fullmatch(r'\d{5}', raw):
        return raw

    upper = raw.upper()
    if upper in _VERSION_TO_BUILD:
        return _VERSION_TO_BUILD[upper]

    m_ver = re.search(r'(?<![A-Z0-9])(21H2|22H2|23H2|24H2|25H2)(?![A-Z0-9])', upper)
    if m_ver:
        return _VERSION_TO_BUILD[m_ver.group(1)]

    m_bld = re.search(r'(?<!KB)(?<!\d)(26200|26100|22631|22621|22000)(?!\d)', upper)
    if m_bld:
        return m_bld.group(1)

    return None


def _index_file_path(target_dir: str) -> str:
    return os.path.join(target_dir, "index.json")


def _load_updates_index(target_dir: str) -> Dict[str, Any]:
    path = _index_file_path(target_dir)
    if not os.path.isfile(path):
        return {"packages": {}}
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict) and isinstance(data.get("packages"), dict):
            return data
    except Exception:
        pass
    return {"packages": {}}


def _save_updates_index(target_dir: str, index_data: Dict[str, Any]) -> None:
    try:
        os.makedirs(target_dir, exist_ok=True)
        path = _index_file_path(target_dir)
        tmp_path = f"{path}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(index_data, f, indent=2, ensure_ascii=False)
        os.replace(tmp_path, path)
    except OSError:
        pass


def extract_kb(filename: str) -> Optional[str]:
    """
    Ekstraktuje Microsoft KB broj (npr. 'KB5129195' ili 'KB5043080') iz naziva datoteke.
    Prepoznaje formate:
    - Windows11.0-KB5043080-x64.msu -> KB5043080
    - windows11.0-kb5129195-x64.msu -> KB5129195
    - KB5129242-amd64.cab -> KB5129242
    - windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu -> KB5043080
    """
    if not filename:
        return None
    m = re.search(r'(?i)(?:^|[^a-zA-Z0-9])(KB\d{5,8})(?:[^a-zA-Z0-9]|$)', filename)
    if m:
        return m.group(1).upper()
    return None


def _normalize_kb_input(kb_value: Optional[str]) -> Optional[str]:
    if not kb_value:
        return None
    raw = str(kb_value).strip()
    kb = extract_kb(raw)
    if kb:
        return kb
    if re.fullmatch(r'\d{5,8}', raw):
        return f"KB{raw}"
    return None


def resolve_updates_directory(custom_dir: Optional[str] = None) -> str:
    """
    Određuje putanju skladišta zakrpi u sledećem redosledu:
    1. Eksplicitno prosleđeni direktorijum
    2. SERVIS_UPDATES_DIR iz okruženja
    3. SERVIS_HUB_UPDATES_DIR iz okruženja
    4. active_config.updates_dir (ako je definisan)
    5. /home/gru/Storage/Updates/Win11 (ako postoji)
    6. /mnt/Servis/Updates/Win11 (ako postoji) ili /mnt/Servis/Updates/Win11 kada postoji /mnt/Servis
    7. ./Updates/Win11 (ako postoji)
    8. Fallback na /home/gru/Storage/Updates/Win11
    """
    if custom_dir:
        return os.path.abspath(custom_dir)

    env_dir = os.environ.get("SERVIS_UPDATES_DIR") or os.environ.get("SERVIS_HUB_UPDATES_DIR")
    if env_dir:
        return os.path.abspath(env_dir)

    try:
        from servishub.config import active_config
        if getattr(active_config, "updates_dir", None):
            return active_config.updates_dir
    except Exception:
        pass

    if os.path.isdir("/home/gru/Storage/Updates/Win11"):
        return "/home/gru/Storage/Updates/Win11"

    if os.path.isdir("/mnt/Servis/Updates/Win11"):
        return "/mnt/Servis/Updates/Win11"

    if os.path.isdir("/mnt/Servis"):
        return "/mnt/Servis/Updates/Win11"

    local_fallback = os.path.abspath(os.path.join(os.getcwd(), "Updates", "Win11"))
    if os.path.isdir(local_fallback):
        return local_fallback

    return "/home/gru/Storage/Updates/Win11"


def scan_updates_directory(
    directory: Optional[str] = None,
    os_build: Optional[str] = None,
) -> UpdatesStatusResponse:
    """
    Skenira skladište zakrpi i vraća strukturiran izveštaj o pronađenim .msu i .cab paketima.
    Ako je prosleđen os_build, filtrira pakete koji su eksplicitno označeni za drugi Windows build.
    """
    target_dir = resolve_updates_directory(directory)

    if not os.path.isdir(target_dir):
        return UpdatesStatusResponse(
            enabled=True,
            available=False,
            directory=target_dir,
            total_packages=0,
            total_size_bytes=0,
            total_size_mb=0.0,
            kb_list=[],
            packages=[],
        )

    norm_req_build = extract_os_build(os_build) or (str(os_build).strip() if os_build and str(os_build).strip().isdigit() else None)
    idx_data = _load_updates_index(target_dir)
    idx_packages = idx_data.get("packages", {})

    packages: List[UpdatePackageItem] = []
    ignored_extensions = {".part", ".tmp", ".crdownload", ".aria2"}

    try:
        entries = sorted(os.listdir(target_dir))
    except Exception:
        return UpdatesStatusResponse(
            enabled=True,
            available=False,
            directory=target_dir,
            total_packages=0,
            total_size_bytes=0,
            total_size_mb=0.0,
            kb_list=[],
            packages=[],
        )

    for entry in entries:
        lower_entry = entry.lower()
        # Ignoriši privremene/nezavršene datoteke
        if any(lower_entry.endswith(ext) for ext in ignored_extensions):
            continue

        if not (lower_entry.endswith(".msu") or lower_entry.endswith(".cab")):
            continue

        full_path = os.path.join(target_dir, entry)
        if not os.path.isfile(full_path):
            continue

        try:
            stat_res = os.stat(full_path)
            size_bytes = stat_res.st_size
            size_mb = round(size_bytes / (1024 * 1024), 2)
            mtime = stat_res.st_mtime
            modified_at = datetime.fromtimestamp(mtime).isoformat()
            file_type = "msu" if lower_entry.endswith(".msu") else "cab"
            meta = idx_packages.get(entry, {}) if isinstance(idx_packages.get(entry), dict) else {}
            kb = meta.get("kb") or extract_kb(entry)
            pkg_build = meta.get("os_build") or extract_os_build(entry) or extract_os_build(meta.get("title"))
            pkg_sha256 = meta.get("sha256")

            if norm_req_build and pkg_build and pkg_build != norm_req_build:
                continue

            pkg = UpdatePackageItem(
                filename=entry,
                kb=kb,
                file_type=file_type,
                size_bytes=size_bytes,
                size_mb=size_mb,
                modified_at=modified_at,
                modified_timestamp=mtime,
                path=full_path,
                os_build=pkg_build,
                sha256=pkg_sha256,
            )
            packages.append(pkg)
        except Exception:
            continue

    # Sortiraj pakete: najnoviji po mtime prvo, pa abecedno po nazivu
    packages.sort(key=lambda p: (p.modified_timestamp, p.filename), reverse=True)

    total_bytes = sum(p.size_bytes for p in packages)
    total_mb = round(total_bytes / (1024 * 1024), 2)
    kb_list = sorted(list({p.kb for p in packages if p.kb}))

    return UpdatesStatusResponse(
        enabled=True,
        available=True,
        directory=target_dir,
        total_packages=len(packages),
        total_size_bytes=total_bytes,
        total_size_mb=total_mb,
        kb_list=kb_list,
        packages=packages,
    )


def find_update_package(
    kb_or_filename: str,
    directory: Optional[str] = None,
) -> Optional[UpdatePackageItem]:
    """
    Pronalazi postojeći paket zakrpe (.msu ili .cab) po tačnom nazivu datoteke ili po KB broju.
    """
    if not kb_or_filename or not str(kb_or_filename).strip():
        return None

    query = str(kb_or_filename).strip()
    status = scan_updates_directory(directory)
    if not status.available or not status.packages:
        return None

    q_lower = os.path.basename(query).lower()
    for pkg in status.packages:
        if pkg.filename.lower() == q_lower:
            return pkg

    target_kb = _normalize_kb_input(query)
    if target_kb:
        kb_matches = [p for p in status.packages if p.kb and p.kb.upper() == target_kb]
        if kb_matches:
            # Preferiraj standalone .msu nad .cab, već sortirano od najnovijeg
            for p in kb_matches:
                if p.file_type == "msu":
                    return p
            return kb_matches[0]

    return None


def _build_safe_package_filename(filename: Optional[str], kb: Optional[str] = None) -> str:
    raw_name = os.path.basename((filename or "").replace("\\", "/")).strip()
    norm_kb = _normalize_kb_input(kb)

    if not raw_name:
        if norm_kb:
            return f"Windows11.0-{norm_kb}-x64.msu"
        raise ValueError("Nedostaje naziv datoteke (.msu ili .cab) za snimanje paketa.")

    # Sanitizacija imena fajla uz očuvanje tačke, crtice i donje crte
    safe_name = re.sub(r'[^a-zA-Z0-9._-]', '_', raw_name)
    base, ext = os.path.splitext(safe_name)
    ext_lower = ext.lower()
    if ext_lower not in (".msu", ".cab"):
        raise ValueError(f"Nepodržana ekstenzija paketa '{ext}'. Dozvoljeni su samo .msu i .cab fajlovi.")

    if extract_kb(safe_name) is None and norm_kb:
        return f"Windows11.0-{norm_kb}-x64{ext_lower}"

    return f"{base}{ext_lower}"


def save_uploaded_update_package_stream(
    filename: Optional[str],
    stream: BinaryIO,
    content_length: Optional[int] = None,
    directory: Optional[str] = None,
    kb: Optional[str] = None,
    os_build: Optional[str] = None,
    title: str = "",
) -> UpdatePackageItem:
    """
    Atomično upisuje binarni tok (.msu ili .cab) u skladište zakrpi preko privremenog .tmp fajla,
    računa SHA-256 kontrolnu sumu i beleži OSBuild metapodatke u index.json.
    """
    if content_length is not None and content_length <= 0:
        raise ValueError("Telo zahteva je prazno (Content-Length mora biti > 0).")

    final_name = _build_safe_package_filename(filename, kb=kb)
    target_dir = resolve_updates_directory(directory)
    os.makedirs(target_dir, exist_ok=True)

    dest_path = os.path.join(target_dir, final_name)
    tmp_path = f"{dest_path}.tmp"

    bytes_written = 0
    chunk_size = 65536
    hasher = hashlib.sha256()
    try:
        with open(tmp_path, "wb") as out_f:
            if content_length is not None:
                remaining = content_length
                while remaining > 0:
                    chunk = stream.read(min(chunk_size, remaining))
                    if not chunk:
                        break
                    out_f.write(chunk)
                    hasher.update(chunk)
                    bytes_written += len(chunk)
                    remaining -= len(chunk)
            else:
                while True:
                    chunk = stream.read(chunk_size)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    hasher.update(chunk)
                    bytes_written += len(chunk)

        if bytes_written <= 0:
            raise ValueError("Primljeni paket je prazan (0 bajtova).")

        if content_length is not None and bytes_written < content_length:
            raise ValueError(f"Nepotpun upload paketa: primljeno {bytes_written} od {content_length} bajtova.")

        os.replace(tmp_path, dest_path)
    except Exception:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass
        raise

    stat_res = os.stat(dest_path)
    size_bytes = stat_res.st_size
    size_mb = round(size_bytes / (1024 * 1024), 2)
    mtime = stat_res.st_mtime
    modified_at = datetime.fromtimestamp(mtime).isoformat()
    file_type = "msu" if final_name.lower().endswith(".msu") else "cab"
    resolved_kb = extract_kb(final_name) or _normalize_kb_input(kb)
    resolved_build = (
        extract_os_build(os_build)
        or (str(os_build).strip() if os_build and str(os_build).strip().isdigit() else None)
        or extract_os_build(final_name)
        or extract_os_build(title)
    )
    pkg_sha256 = hasher.hexdigest()

    idx_data = _load_updates_index(target_dir)
    idx_data.setdefault("packages", {})[final_name] = {
        "kb": resolved_kb,
        "os_build": resolved_build,
        "sha256": pkg_sha256,
        "title": title or "",
        "size_bytes": size_bytes,
        "updated_at": modified_at,
    }
    _save_updates_index(target_dir, idx_data)

    return UpdatePackageItem(
        filename=final_name,
        kb=resolved_kb,
        file_type=file_type,
        size_bytes=size_bytes,
        size_mb=size_mb,
        modified_at=modified_at,
        modified_timestamp=mtime,
        path=dest_path,
        os_build=resolved_build,
        sha256=pkg_sha256,
    )


def save_uploaded_update_package(
    filename: Optional[str],
    data: bytes,
    directory: Optional[str] = None,
    kb: Optional[str] = None,
    os_build: Optional[str] = None,
    title: str = "",
) -> UpdatePackageItem:
    """
    Validira i atomično upisuje .msu/.cab bajtove u skladište zakrpi.
    """
    if not data:
        raise ValueError("Podaci paketa su prazni.")
    return save_uploaded_update_package_stream(
        filename=filename,
        stream=io.BytesIO(data),
        content_length=len(data),
        directory=directory,
        kb=kb,
        os_build=os_build,
        title=title,
    )


def _select_catalog_update_entries(
    entries: list,
    scraper,
    os_build: Optional[str] = None,
) -> list:
    if not entries:
        return []

    norm_build = extract_os_build(os_build) or (
        str(os_build).strip() if os_build and str(os_build).strip().isdigit() else None
    )

    def _build_match_score(entry: dict) -> int:
        if not norm_build:
            return 0
        comb = f"{entry.get('title', '')} {entry.get('products', '')}"
        detected = extract_os_build(comb)
        return 1 if detected == norm_build else 0

    filtered = scraper.filter_for_win_x64(entries)
    if filtered:
        # Prioritizuj traženi Windows 11 OSBuild, pa Windows 11 x64
        filtered.sort(
            key=lambda e: (
                _build_match_score(e),
                1 if "windows 11" in (e.get("products", "") + " " + e.get("title", "")).lower() else 0,
                1 if "x64" in (e.get("products", "") + " " + e.get("title", "")).lower() else 0,
                e.get("date") or datetime.min,
                e.get("size_bytes", 0),
            ),
            reverse=True,
        )
        return filtered

    excluded = ("arm64", "x86", "32-bit", "32bit", "itanium", "ia64", "windows 7", "windows 8", "windows xp", "server 2008")
    fallback = []
    for e in entries:
        comb = (e.get("products", "") + " " + e.get("title", "")).lower()
        if any(tok in comb for tok in excluded):
            continue
        fallback.append(e)

    fallback.sort(
        key=lambda e: (
            _build_match_score(e),
            1 if "x64" in (e.get("products", "") + " " + e.get("title", "")).lower() else 0,
            e.get("date") or datetime.min,
            e.get("size_bytes", 0),
        ),
        reverse=True,
    )
    return fallback


def cache_kb_from_ms_catalog(
    kb: str,
    title: str = "",
    directory: Optional[str] = None,
    scraper: Any = None,
    timeout: int = 120,
    os_build: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Preuzima i trajno kešira standalone .msu ili .cab paket iz Microsoft Update Kataloga po KB broju.
    """
    norm_kb = _normalize_kb_input(kb)
    if not norm_kb:
        raise ValueError(f"Nevalidan KB identifikator: '{kb}'")

    norm_build = extract_os_build(os_build) or (
        str(os_build).strip() if os_build and str(os_build).strip().isdigit() else None
    )

    existing = find_update_package(norm_kb, directory=directory)
    if existing and not (norm_build and existing.os_build and existing.os_build != norm_build):
        return {
            "status": "cached",
            "already_cached": True,
            "kb": norm_kb,
            "os_build": existing.os_build or norm_build,
            "package": existing.to_dict(),
        }

    if scraper is None:
        from servishub.drivers.scraper import MsUpdateCatalogScraper
        scraper = MsUpdateCatalogScraper()

    entries = scraper.search(norm_kb, timeout=min(timeout, 30))
    candidates = _select_catalog_update_entries(entries, scraper, os_build=norm_build)
    if not candidates:
        return {
            "status": "not_found",
            "already_cached": False,
            "kb": norm_kb,
            "title": title,
            "error": f"Paket {norm_kb} nije pronađen u Microsoft Update Katalogu za Windows x64.",
        }

    user_agent = getattr(scraper, "user_agent", "WIS-UpdateCache/2.19")
    last_err = None

    for entry in candidates[:3]:
        urls = scraper.get_download_urls(entry.get("update_id", ""), timeout=min(timeout, 30))
        if not urls:
            continue

        # Sortiraj tako da .msu ima prednost nad .cab, a https nad http
        sorted_urls = sorted(
            urls,
            key=lambda u: (
                0 if ".msu" in u.lower() else (1 if ".cab" in u.lower() else 2),
                0 if u.lower().startswith("https://") else 1,
            ),
        )

        entry_title = title or entry.get("title", "")
        entry_build = norm_build or extract_os_build(entry_title) or extract_os_build(entry.get("products", ""))

        for dl_url in sorted_urls:
            try:
                url_path = urlparse(dl_url).path
                url_base = os.path.basename(url_path)
                _, url_ext = os.path.splitext(url_base)
                if url_ext.lower() not in (".msu", ".cab"):
                    url_base = f"Windows11.0-{norm_kb}-x64.msu"
                elif extract_kb(url_base) is None:
                    url_base = f"Windows11.0-{norm_kb}-x64{url_ext.lower()}"

                req = urllib.request.Request(dl_url, headers={"User-Agent": user_agent})
                with urllib.request.urlopen(req, timeout=timeout) as resp:
                    cl_hdr = resp.headers.get("Content-Length") if resp.headers else None
                    expected_len = int(cl_hdr) if cl_hdr and str(cl_hdr).isdigit() else None
                    saved_pkg = save_uploaded_update_package_stream(
                        filename=url_base,
                        stream=resp,
                        content_length=expected_len,
                        directory=directory,
                        kb=norm_kb,
                        os_build=entry_build,
                        title=entry_title,
                    )

                return {
                    "status": "cached",
                    "already_cached": False,
                    "kb": norm_kb,
                    "os_build": saved_pkg.os_build,
                    "title": entry_title,
                    "source_url": dl_url,
                    "package": saved_pkg.to_dict(),
                }
            except Exception as ex:
                last_err = str(ex)
                continue

    return {
        "status": "error" if last_err else "not_found",
        "already_cached": False,
        "kb": norm_kb,
        "title": title,
        "error": last_err or f"Nisu pronađeni download linkovi za {norm_kb}.",
    }


def prune_updates_cache(
    directory: Optional[str] = None,
    max_packages_per_build: int = 8,
    min_temp_age_seconds: int = 600,
) -> Dict[str, Any]:
    """
    Održavanje keša zakrpi:
    1. Briše zastarele privremene fajlove (.tmp, .part, .crdownload, .aria2).
    2. Čisti nepostojeće zapise iz index.json.
    3. Po potrebi zadržava najnovijih `max_packages_per_build` paketa po OSBuild grani.
    """
    target_dir = resolve_updates_directory(directory)
    if not os.path.isdir(target_dir):
        return {
            "status": "ok",
            "directory": target_dir,
            "removed_temps": [],
            "removed_packages": [],
            "removed_index_entries": [],
            "kept_packages": 0,
            "freed_bytes": 0,
        }

    temp_suffixes = (".tmp", ".part", ".crdownload", ".aria2")
    now = time.time()
    removed_temps: List[str] = []
    removed_packages: List[str] = []
    removed_index_entries: List[str] = []
    freed_bytes = 0

    try:
        for entry in sorted(os.listdir(target_dir)):
            lower_entry = entry.lower()
            if not any(lower_entry.endswith(sfx) for sfx in temp_suffixes):
                continue
            full_path = os.path.join(target_dir, entry)
            if not os.path.isfile(full_path):
                continue
            try:
                st = os.stat(full_path)
                if min_temp_age_seconds <= 0 or (now - st.st_mtime) >= min_temp_age_seconds:
                    size_b = st.st_size
                    os.remove(full_path)
                    removed_temps.append(entry)
                    freed_bytes += size_b
            except OSError:
                continue
    except OSError:
        pass

    scan = scan_updates_directory(target_dir)
    existing_filenames = {p.filename for p in scan.packages}
    idx_data = _load_updates_index(target_dir)
    idx_packages = idx_data.get("packages", {})
    idx_changed = False

    for fname in list(idx_packages.keys()):
        if fname not in existing_filenames:
            del idx_packages[fname]
            removed_index_entries.append(fname)
            idx_changed = True

    kept_count = len(scan.packages)
    if max_packages_per_build > 0 and scan.packages:
        by_build: Dict[str, List[UpdatePackageItem]] = {}
        for pkg in scan.packages:
            key = pkg.os_build or "unknown"
            by_build.setdefault(key, []).append(pkg)

        for build_key, pkgs in by_build.items():
            # scan.packages su već sortirani od najnovijeg ka najstarijem
            if len(pkgs) <= max_packages_per_build:
                continue
            for old_pkg in pkgs[max_packages_per_build:]:
                try:
                    if old_pkg.path and os.path.isfile(old_pkg.path):
                        os.remove(old_pkg.path)
                        freed_bytes += old_pkg.size_bytes
                    removed_packages.append(old_pkg.filename)
                    kept_count -= 1
                    if old_pkg.filename in idx_packages:
                        del idx_packages[old_pkg.filename]
                        idx_changed = True
                except OSError:
                    continue

    if idx_changed:
        idx_data["packages"] = idx_packages
        _save_updates_index(target_dir, idx_data)

    return {
        "status": "ok",
        "directory": target_dir,
        "removed_temps": removed_temps,
        "removed_packages": removed_packages,
        "removed_index_entries": removed_index_entries,
        "kept_packages": max(0, kept_count),
        "freed_bytes": freed_bytes,
    }


def sanitize_model_pack_name(model_name: Optional[str]) -> str:
    """
    Sanitizuje naziv modela računara tako da bude bezbedan za <Model>.tar datoteke.
    """
    if not model_name or not str(model_name).strip():
        raise ValueError("Naziv modela je prazan.")
    raw = str(model_name).strip()
    if raw.lower().endswith(".tar"):
        raw = raw[:-4]
    raw = os.path.basename(raw)
    clean = re.sub(r'[\\/:*?"<>|]+', '_', raw).strip(" .")
    if not clean:
        raise ValueError("Naziv modela ne sadrži validne karaktere.")
    return clean


def resolve_model_packs_directory(
    custom_dir: Optional[str] = None,
    active_config: Any = None,
) -> str:
    """
    Određuje direktorijum za skladištenje model-specifičnih <Model>.tar arhiva drajvera.
    """
    if custom_dir and str(custom_dir).strip():
        return os.path.abspath(str(custom_dir).strip())

    env_dir = os.environ.get("SERVIS_MODEL_PACKS_DIR")
    if env_dir and env_dir.strip():
        return os.path.abspath(env_dir.strip())

    cfg_dir = getattr(active_config, "model_packs_dir", None) if active_config is not None else None
    if cfg_dir and str(cfg_dir).strip():
        return os.path.abspath(str(cfg_dir).strip())

    candidates = [
        "/mnt/Servis/Drivers/Harvested/Archives",
        "/home/gru/Storage/Drivers/Harvested/Archives",
    ]
    for c in candidates:
        if os.path.isdir(c) or os.path.isdir(os.path.dirname(os.path.dirname(c))):
            return c

    return os.path.join(os.getcwd(), "Drivers", "Harvested", "Archives")


def _find_existing_model_pack_path(target_dir: str, clean_model: str) -> Optional[str]:
    exact = os.path.join(target_dir, f"{clean_model}.tar")
    if os.path.isfile(exact):
        return exact
    if not os.path.isdir(target_dir):
        return None
    target_lower = f"{clean_model}.tar".lower()
    try:
        for entry in os.listdir(target_dir):
            if entry.lower() == target_lower:
                candidate = os.path.join(target_dir, entry)
                if os.path.isfile(candidate):
                    return candidate
    except OSError:
        pass
    return None


def get_model_pack_status(
    model: Optional[str],
    directory: Optional[str] = None,
    active_config: Any = None,
) -> Dict[str, Any]:
    """
    Proverava da li postoji <Model>.tar arhiva drajvera na NAS/Hub skladištu.
    """
    clean_model = sanitize_model_pack_name(model)
    target_dir = resolve_model_packs_directory(directory, active_config=active_config)
    expected_name = f"{clean_model}.tar"
    found_path = _find_existing_model_pack_path(target_dir, clean_model)

    if not found_path:
        return {
            "available": False,
            "model": clean_model,
            "filename": expected_name,
            "directory": target_dir,
            "path": None,
            "size_bytes": 0,
            "size_mb": 0.0,
            "modified_at": None,
            "sha256": None,
        }

    st = os.stat(found_path)
    actual_name = os.path.basename(found_path)
    size_bytes = st.st_size
    if size_bytes < 512:
        return {
            "available": False,
            "model": clean_model,
            "filename": actual_name,
            "directory": target_dir,
            "path": None,
            "size_bytes": size_bytes,
            "size_mb": 0.0,
            "modified_at": datetime.fromtimestamp(st.st_mtime).isoformat(),
            "sha256": None,
        }

    idx_data = _load_updates_index(target_dir)
    meta = idx_data.get("packages", {}).get(actual_name, {})

    return {
        "available": True,
        "model": clean_model,
        "filename": actual_name,
        "directory": target_dir,
        "path": found_path,
        "size_bytes": size_bytes,
        "size_mb": round(size_bytes / (1024 * 1024), 2),
        "modified_at": datetime.fromtimestamp(st.st_mtime).isoformat(),
        "sha256": meta.get("sha256") if isinstance(meta, dict) else None,
    }


def save_uploaded_model_pack_stream(
    model: Optional[str],
    stream: BinaryIO,
    content_length: Optional[int] = None,
    directory: Optional[str] = None,
    filename: Optional[str] = None,
    active_config: Any = None,
) -> Dict[str, Any]:
    """
    Atomično snima <Model>.tar paket drajvera sa klijenta preko .tmp datoteke
    i računa SHA-256 kontrolnu sumu.
    """
    if content_length is not None and content_length < 512:
        raise ValueError("Primljeni .tar paket drajvera je premali (< 512 bajtova).")

    clean_model = sanitize_model_pack_name(model or filename)
    final_name = f"{clean_model}.tar"
    target_dir = resolve_model_packs_directory(directory, active_config=active_config)
    os.makedirs(target_dir, exist_ok=True)

    dest_path = os.path.join(target_dir, final_name)
    tmp_path = f"{dest_path}.tmp"

    bytes_written = 0
    chunk_size = 65536
    hasher = hashlib.sha256()
    try:
        with open(tmp_path, "wb") as out_f:
            if content_length is not None:
                remaining = content_length
                while remaining > 0:
                    chunk = stream.read(min(chunk_size, remaining))
                    if not chunk:
                        break
                    out_f.write(chunk)
                    hasher.update(chunk)
                    bytes_written += len(chunk)
                    remaining -= len(chunk)
            else:
                while True:
                    chunk = stream.read(chunk_size)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    hasher.update(chunk)
                    bytes_written += len(chunk)

        if bytes_written < 512:
            raise ValueError(f"Primljeni .tar paket drajvera je premali ({bytes_written} bajtova).")

        if content_length is not None and bytes_written < content_length:
            raise ValueError(f"Nepotpun upload model paketa: primljeno {bytes_written} od {content_length} bajtova.")

        os.replace(tmp_path, dest_path)
    except Exception:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass
        raise

    st = os.stat(dest_path)
    size_bytes = st.st_size
    size_mb = round(size_bytes / (1024 * 1024), 2)
    modified_at = datetime.fromtimestamp(st.st_mtime).isoformat()
    pkg_sha256 = hasher.hexdigest()

    idx_data = _load_updates_index(target_dir)
    idx_data.setdefault("packages", {})[final_name] = {
        "model": clean_model,
        "sha256": pkg_sha256,
        "size_bytes": size_bytes,
        "updated_at": modified_at,
    }
    _save_updates_index(target_dir, idx_data)

    return {
        "status": "ok",
        "model": clean_model,
        "filename": final_name,
        "directory": target_dir,
        "path": dest_path,
        "size_bytes": size_bytes,
        "size_mb": size_mb,
        "modified_at": modified_at,
        "sha256": pkg_sha256,
    }


# FastAPI ruter integracija
try:
    from fastapi import APIRouter, Body, Query, Request
    from fastapi.responses import FileResponse, JSONResponse

    router = APIRouter(prefix="/api/updates", tags=["updates"])

    @router.get("/status", response_model=UpdatesStatusResponse)
    def get_updates_status(
        directory: Optional[str] = Query(
            None,
            description="Opciona specifična putanja do direktorijuma sa zakrpama",
        ),
        os_build: Optional[str] = Query(
            None,
            description="Opcioni filter po Windows 11 OSBuild broju (npr. 26100 ili 24H2)",
        ),
    ) -> UpdatesStatusResponse:
        """
        Pregleda konfigurisanu putanju za skladište zakrpi (npr. SERVIS_UPDATES_DIR),
        parsira pronađene .msu i .cab fajlove, izvlači KB brojeve i metapodatke,
        i vraća status dostupnosti NAS keša zakrpi.
        """
        return scan_updates_directory(directory, os_build=os_build)

    @router.get("/download")
    def download_update_package(
        filename: Optional[str] = Query(None, description="Naziv paketa (.msu ili .cab)"),
        kb: Optional[str] = Query(None, description="KB broj zakrpe (npr. KB5043080)"),
        directory: Optional[str] = Query(None, description="Opciona putanja do skladišta zakrpi"),
    ):
        lookup = filename or kb
        if not lookup:
            return JSONResponse({"error": "Nedostaje parametar 'filename' ili 'kb'."}, status_code=400)
        pkg = find_update_package(lookup, directory=directory)
        if not pkg or not pkg.path or not os.path.isfile(pkg.path):
            return JSONResponse({"error": f"Paket '{lookup}' nije pronađen u skladištu."}, status_code=404)
        media_type = "application/vnd.ms-cab-compressed" if pkg.file_type == "cab" else "application/octet-stream"
        return FileResponse(pkg.path, media_type=media_type, filename=pkg.filename)

    @router.post("/upload")
    async def upload_update_package(
        request: Request,
        filename: Optional[str] = Query(None, description="Naziv paketa (.msu ili .cab)"),
        kb: Optional[str] = Query(None, description="KB broj zakrpe"),
        os_build: Optional[str] = Query(None, description="OSBuild ciljanog Windows sistema (npr. 26100)"),
        directory: Optional[str] = Query(None, description="Opciona putanja do skladišta zakrpi"),
    ):
        body = await request.body()
        if not body:
            return JSONResponse({"error": "Telo zahteva je prazno."}, status_code=400)
        try:
            saved = save_uploaded_update_package(
                filename=filename,
                data=body,
                directory=directory,
                kb=kb,
                os_build=os_build,
            )
            return {"status": "ok", "package": saved.to_dict()}
        except ValueError as ve:
            return JSONResponse({"error": str(ve)}, status_code=400)

    @router.post("/cache-kb")
    def cache_update_kb(
        payload: Dict[str, Any] = Body(...),
    ):
        kb_val = str(payload.get("kb", "")).strip()
        if not kb_val:
            return JSONResponse({"error": "Nedostaje obavezno polje 'kb'."}, status_code=400)
        title_val = str(payload.get("title", "")).strip()
        os_build_val = str(payload.get("os_build", "")).strip() or None
        dir_val = payload.get("directory")
        try:
            res = cache_kb_from_ms_catalog(
                kb=kb_val,
                title=title_val,
                directory=dir_val,
                os_build=os_build_val,
            )
            code = 404 if res.get("status") == "not_found" else (502 if res.get("status") == "error" else 200)
            return JSONResponse(res, status_code=code)
        except ValueError as ve:
            return JSONResponse({"error": str(ve)}, status_code=400)

    @router.post("/prune")
    def prune_updates_endpoint(
        payload: Optional[Dict[str, Any]] = Body(None),
    ):
        body = payload or {}
        dir_val = body.get("directory")
        try:
            max_per_build = int(body.get("max_packages_per_build", 8))
            min_temp_age = int(body.get("min_temp_age_seconds", 600))
        except (TypeError, ValueError):
            return JSONResponse({"error": "Nevalidni numerički parametri za prune."}, status_code=400)
        return prune_updates_cache(
            directory=dir_val,
            max_packages_per_build=max_per_build,
            min_temp_age_seconds=min_temp_age,
        )

except ImportError:
    router = None
