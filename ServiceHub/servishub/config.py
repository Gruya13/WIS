"""
servishub.config
================
Konfiguracija i upravljanje putanjama za ServisHub.
"""

import os
import shutil
import tempfile
import time
from typing import Any, Dict, Optional, Tuple

# Podrazumevane mrežne konstante
DEFAULT_HOST = os.environ.get("SERVIS_HUB_HOST", "0.0.0.0")
DEFAULT_HTTP_PORT = int(os.environ.get("SERVIS_HUB_PORT", os.environ.get("PORT", "8080")))
DEFAULT_UDP_DISCOVERY_PORT = int(os.environ.get("SERVIS_HUB_UDP_PORT", "8888"))

# Koreni direktorijum projekta
BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))

# Skladište i keš
STORAGE_DIR = os.environ.get("SERVIS_HUB_STORAGE_DIR", os.path.join(BASE_DIR, "storage"))
CACHE_DIR = os.path.join(STORAGE_DIR, "cache")
DRIVERS_CACHE_DIR = os.environ.get("GWADT_DRIVERS_CACHE_DIR", os.path.join(STORAGE_DIR, "drivers_cache"))
WIS_DIR = os.environ.get("SERVIS_HUB_WIS_DIR", os.path.join(STORAGE_DIR, "wis"))

# Logovi i izveštaji
LOGS_DIR = os.environ.get("SERVIS_HUB_LOGS_DIR", os.path.join(BASE_DIR, "Logs"))
LIVE_LOGS_DIR = os.path.join(LOGS_DIR, "live")
REPORTS_DIR = os.path.join(LOGS_DIR, "reports")

# Izvor za payload
PAYLOAD_SOURCE_DIR = os.environ.get("SERVIS_HUB_PAYLOAD_DIR", os.path.join(BASE_DIR, "payload_src"))

# Skladište paketa drajvera po modelu (<Model>.tar)
DEFAULT_MODEL_PACKS_DIR = os.environ.get(
    "SERVIS_MODEL_PACKS_DIR",
    "/app/harvested/Archives" if os.path.isdir("/app/harvested/Archives") else (
        "/mnt/Servis/Drivers/Harvested/Archives" if os.path.isdir("/mnt/Servis") else os.path.join(DRIVERS_CACHE_DIR, "model_packs")
    ),
)

# Skladište zakrpi (Windows 11 updates keš)
DEFAULT_UPDATES_DIR = os.environ.get(
    "SERVIS_UPDATES_DIR",
    os.environ.get("SERVIS_HUB_UPDATES_DIR", os.path.join(STORAGE_DIR, "updates", "Win11"))
)

# IMP-63 & IMP-68 & v3.5.4: LRU kvote za Driver Pool bundles, drivers_cache i disk guard za ms_catalog
DEFAULT_MAX_BUNDLES_GB = float(os.environ.get("SERVIS_POOL_MAX_BUNDLES_GB", "16.0"))
DEFAULT_BUNDLE_TTL_HOURS = float(os.environ.get("SERVIS_POOL_BUNDLE_TTL_HOURS", "24.0"))
DEFAULT_BUNDLE_CACHE_MIN_HITS = int(os.environ.get("SERVIS_POOL_BUNDLE_MIN_HITS", "2"))
DEFAULT_BUNDLE_CACHE_MAX_GB = float(os.environ.get("SERVIS_POOL_BUNDLE_MAX_GB", "4.0"))
DEFAULT_MAX_DRIVERS_CACHE_GB = float(os.environ.get("SERVIS_MAX_DRIVERS_CACHE_GB", "25.0"))
DEFAULT_MS_CATALOG_MIN_FREE_GB = float(os.environ.get("SERVIS_MS_CATALOG_MIN_FREE_GB", "20.0"))
DEFAULT_OEM_CATALOG_STALE_HOURS = float(os.environ.get("SERVIS_OEM_CATALOG_STALE_HOURS", "72.0"))
DEFAULT_DELETE_INGESTED_ARCHIVES = os.environ.get("SERVIS_POOL_DELETE_INGESTED_ARCHIVES", "1").strip().lower() not in (
    "0", "false", "no", "off"
)


def _is_active_temp_name(filename: str) -> bool:
    """Proverava da li je fajl aktivni privremeni zapis koji se nikada ne sme brisati pri LRU evikciji."""
    low = (filename or "").lower()
    return low.endswith((".tmp", ".part", ".lock", ".crdownload", ".aria2")) or ".tmp." in low


def get_free_disk_gb(path: str) -> Optional[float]:
    """Vraća slobodan prostor na disku u GB za postojeću putanju (ili najbliži postojeći roditelj)."""
    if not path:
        return None
    probe = os.path.abspath(path)
    while probe and not os.path.exists(probe):
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    try:
        du = shutil.disk_usage(probe)
        return float(du.free) / (1024.0 ** 3)
    except OSError:
        return None


def is_low_disk_for_ms_catalog(
    path: str,
    min_free_gb: Optional[float] = None,
) -> Tuple[bool, Optional[float], float]:
    """
    Proverava da li je slobodan prostor na disku ispod praga za ms_catalog preuzimanje (< 20 GB).
    Ako min_free_gb nije eksplicitno prosleđen ni preko env-a, a fajlsistem ukupno ima < min_free_gb
    (npr. mali tmpfs u testovima), ne blokira automatski.
    Vraća (is_low, free_gb, effective_threshold_gb).
    """
    explicit = min_free_gb is not None or bool(os.environ.get("SERVIS_MS_CATALOG_MIN_FREE_GB", "").strip())
    threshold = float(min_free_gb) if min_free_gb is not None else DEFAULT_MS_CATALOG_MIN_FREE_GB
    if threshold <= 0.0:
        return False, get_free_disk_gb(path), 0.0

    probe = os.path.abspath(path or STORAGE_DIR)
    while probe and not os.path.exists(probe):
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    try:
        du = shutil.disk_usage(probe)
        free_gb = float(du.free) / (1024.0 ** 3)
        total_gb = float(du.total) / (1024.0 ** 3)
    except OSError:
        return False, None, threshold

    if not explicit and total_gb < threshold:
        return False, free_gb, threshold

    return free_gb < threshold, free_gb, threshold


def enforce_directory_lru_quota(
    directory: str,
    max_bytes: int,
    ttl_seconds: Optional[float] = None,
    extensions: Optional[Tuple[str, ...]] = None,
    ignore_names: Tuple[str, ...] = ("pool_index.json", "index.json", "driver_cache.json"),
    paired_extensions: Optional[Dict[str, Tuple[str, ...]]] = None,
    recursive: bool = False,
    now: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Bezbedna LRU + TTL evikcija fajlova u direktorijumu:
    - Strogo odbija symlinkove (`os.path.islink`, `entry.is_symlink()`) i path traversal (`commonpath`).
    - Nikada ne briše aktivne `.tmp` / `.part` / `.lock` fajlove ni zaštićene indeks fajlove.
    - Prvo uklanja fajlove starije od `ttl_seconds`, zatim najstarije po mtime dok ukupan volumen ne padne <= `max_bytes`.
    """
    result = {"removed_files": 0, "freed_bytes": 0, "remaining_bytes": 0}
    if not directory or os.path.islink(directory) or not os.path.isdir(directory):
        return result

    cur_ts = float(now) if now is not None else time.time()
    real_base = os.path.realpath(directory)
    ignore_set = {x.lower() for x in (ignore_names or ())}
    ext_tuple = tuple(e.lower() for e in extensions) if extensions else None
    candidates = []

    def _collect_dir(cur_dir: str) -> None:
        if os.path.islink(cur_dir):
            return
        try:
            with os.scandir(cur_dir) as it:
                entries = list(it)
        except OSError:
            return
        for entry in entries:
            try:
                name = entry.name
                if ".." in name or "/" in name or "\\" in name:
                    continue
                if entry.is_symlink() or os.path.islink(entry.path):
                    continue
                real_target = os.path.realpath(entry.path)
                if os.path.commonpath([real_base, real_target]) != real_base:
                    continue
                if entry.is_dir(follow_symlinks=False):
                    if recursive:
                        _collect_dir(entry.path)
                    continue
                if not entry.is_file(follow_symlinks=False):
                    continue
                low_name = name.lower()
                if low_name in ignore_set or _is_active_temp_name(low_name):
                    continue
                if ext_tuple and not low_name.endswith(ext_tuple):
                    continue
                st = entry.stat(follow_symlinks=False)
                candidates.append((float(st.st_mtime), int(st.st_size), entry.path, low_name))
            except (OSError, ValueError):
                continue

    _collect_dir(real_base)
    if not candidates:
        return result

    removed_paths = set()

    def _safe_remove(fpath: str, fsize: int) -> None:
        if fpath in removed_paths:
            return
        try:
            if os.path.islink(fpath):
                return
            real_target = os.path.realpath(fpath)
            if os.path.commonpath([real_base, real_target]) != real_base:
                return
            if _is_active_temp_name(os.path.basename(fpath)):
                return
            os.remove(fpath)
            removed_paths.add(fpath)
            result["removed_files"] += 1
            result["freed_bytes"] += int(fsize)
        except (OSError, ValueError):
            return

        if paired_extensions:
            stem, ext = os.path.splitext(fpath)
            for p_ext in paired_extensions.get(ext.lower(), ()):
                pf = f"{stem}{p_ext}"
                if pf in removed_paths or not os.path.exists(pf) or os.path.islink(pf):
                    continue
                try:
                    real_pf = os.path.realpath(pf)
                    if os.path.commonpath([real_base, real_pf]) != real_base:
                        continue
                    if os.path.isfile(pf):
                        psz = os.path.getsize(pf)
                        os.remove(pf)
                        removed_paths.add(pf)
                        result["removed_files"] += 1
                        result["freed_bytes"] += int(psz)
                except (OSError, ValueError):
                    pass

    # 1. TTL evikcija
    surviving = []
    if ttl_seconds is not None and float(ttl_seconds) > 0.0:
        ttl_s = float(ttl_seconds)
        for mtime, sz, fp, low_n in candidates:
            if (cur_ts - mtime) >= ttl_s:
                _safe_remove(fp, sz)
            else:
                surviving.append((mtime, sz, fp, low_n))
    else:
        surviving = list(candidates)

    # 2. LRU evikcija po veličini (najstariji mtime prvi)
    total_bytes = sum(sz for _, sz, fp, _ in surviving if fp not in removed_paths)
    if max_bytes is not None and int(max_bytes) >= 0 and total_bytes > int(max_bytes):
        surviving.sort(key=lambda item: item[0])
        for mtime, sz, fp, _ in surviving:
            if total_bytes <= int(max_bytes):
                break
            if fp in removed_paths:
                continue
            before_freed = result["freed_bytes"]
            _safe_remove(fp, sz)
            if result["freed_bytes"] > before_freed:
                total_bytes = max(0, total_bytes - (result["freed_bytes"] - before_freed))

    result["remaining_bytes"] = max(0, total_bytes)
    return result


class HubConfig:
    """Klasa za dinamičko prilagođavanje konfiguracije runtime parametrima."""

    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_HTTP_PORT,
        udp_port: int = DEFAULT_UDP_DISCOVERY_PORT,
        base_dir: str = BASE_DIR,
        storage_dir: str = STORAGE_DIR,
        logs_dir: str = LOGS_DIR,
        payload_source_dir: str = PAYLOAD_SOURCE_DIR,
        updates_dir: str = None,
        model_packs_dir: str = None,
        admin_token: str = None,
        client_token: str = None,
        allow_unauthenticated_lan: bool = None,
        cors_origins: list = None,
        max_screen_frames_bytes: int = 256 * 1024 * 1024,
        screen_frame_ttl_sec: float = 60.0,
        live_log_retention_days: int = 30,
        max_sse_subscribers_total: int = 64,
        max_sse_subscribers_per_device: int = 8,
        driver_pool_dir: str = None,
        max_bundles_gb: float = None,
        bundle_ttl_hours: float = None,
        max_drivers_cache_gb: float = None,
        ms_catalog_min_free_gb: float = None,
        oem_catalog_stale_hours: float = None,
        delete_ingested_archives: bool = None,
    ):
        self.host = host
        self.port = port
        self.udp_port = udp_port
        self.base_dir = os.path.abspath(base_dir)
        self.storage_dir = os.path.abspath(storage_dir)
        self.cache_dir = os.path.join(self.storage_dir, "cache")
        self.drivers_cache_dir = os.environ.get("GWADT_DRIVERS_CACHE_DIR", os.path.join(self.storage_dir, "drivers_cache"))
        self.logs_dir = os.path.abspath(logs_dir)
        self.live_logs_dir = os.path.join(self.logs_dir, "live")
        self.reports_dir = os.path.join(self.logs_dir, "reports")
        self.payload_source_dir = os.path.abspath(payload_source_dir)
        self.admin_token = (
            admin_token.strip()
            if isinstance(admin_token, str)
            else os.environ.get("SERVIS_HUB_ADMIN_TOKEN", "").strip()
        )
        self.client_token = (
            client_token.strip()
            if isinstance(client_token, str)
            else os.environ.get("SERVIS_HUB_CLIENT_TOKEN", "").strip()
        )
        if allow_unauthenticated_lan is not None:
            self.allow_unauthenticated_lan = bool(allow_unauthenticated_lan)
        else:
            self.allow_unauthenticated_lan = os.environ.get("SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN", "").strip().lower() in ("1", "true", "yes")
        if cors_origins is not None:
            self.cors_origins = [str(x).strip() for x in cors_origins if str(x).strip()]
        else:
            raw_cors = os.environ.get("SERVIS_HUB_CORS_ORIGINS", "")
            self.cors_origins = [x.strip() for x in raw_cors.split(",") if x.strip()]
        self.max_screen_frames_bytes = int(max_screen_frames_bytes)
        self.screen_frame_ttl_sec = float(screen_frame_ttl_sec)
        self.live_log_retention_days = int(live_log_retention_days)
        self.max_sse_subscribers_total = int(max_sse_subscribers_total)
        self.max_sse_subscribers_per_device = int(max_sse_subscribers_per_device)
        self.max_bundles_gb = float(max_bundles_gb) if max_bundles_gb is not None else DEFAULT_MAX_BUNDLES_GB
        self.bundle_ttl_hours = float(bundle_ttl_hours) if bundle_ttl_hours is not None else DEFAULT_BUNDLE_TTL_HOURS
        self.max_drivers_cache_gb = float(max_drivers_cache_gb) if max_drivers_cache_gb is not None else DEFAULT_MAX_DRIVERS_CACHE_GB
        self._explicit_ms_catalog_min_free_gb = (
            ms_catalog_min_free_gb is not None
            or bool(os.environ.get("SERVIS_MS_CATALOG_MIN_FREE_GB", "").strip())
        )
        self.ms_catalog_min_free_gb = float(ms_catalog_min_free_gb) if ms_catalog_min_free_gb is not None else DEFAULT_MS_CATALOG_MIN_FREE_GB
        self.oem_catalog_stale_hours = float(oem_catalog_stale_hours) if oem_catalog_stale_hours is not None else DEFAULT_OEM_CATALOG_STALE_HOURS
        self.delete_ingested_archives = (
            bool(delete_ingested_archives)
            if delete_ingested_archives is not None
            else DEFAULT_DELETE_INGESTED_ARCHIVES
        )

        if model_packs_dir:
            self.model_packs_dir = os.path.abspath(model_packs_dir)
        elif os.environ.get("SERVIS_MODEL_PACKS_DIR"):
            self.model_packs_dir = os.path.abspath(os.environ["SERVIS_MODEL_PACKS_DIR"])
        elif os.path.isdir("/app/harvested/Archives") or os.path.isdir("/app/harvested"):
            self.model_packs_dir = "/app/harvested/Archives"
        elif os.path.isdir("/mnt/Servis/Drivers/Harvested/Archives") or os.path.isdir("/mnt/Servis"):
            self.model_packs_dir = "/mnt/Servis/Drivers/Harvested/Archives"
        else:
            self.model_packs_dir = os.path.join(self.drivers_cache_dir, "model_packs")

        # Određivanje putanje za skladište Windows 11 zakrpi
        if updates_dir:
            self.updates_dir = os.path.abspath(updates_dir)
        elif os.environ.get("SERVIS_UPDATES_DIR"):
            self.updates_dir = os.path.abspath(os.environ["SERVIS_UPDATES_DIR"])
        elif os.environ.get("SERVIS_HUB_UPDATES_DIR"):
            self.updates_dir = os.path.abspath(os.environ["SERVIS_HUB_UPDATES_DIR"])
        elif os.path.isdir("/app/storage/updates/Win11"):
            self.updates_dir = "/app/storage/updates/Win11"
        elif os.path.isdir("/mnt/Servis/Updates/Win11"):
            self.updates_dir = "/mnt/Servis/Updates/Win11"
        elif os.path.isdir("/mnt/Servis"):
            self.updates_dir = "/mnt/Servis/Updates/Win11"
        elif os.path.isdir(os.path.join(self.base_dir, "Updates", "Win11")):
            self.updates_dir = os.path.abspath(os.path.join(self.base_dir, "Updates", "Win11"))
        else:
            self.updates_dir = os.path.abspath(os.path.join(self.storage_dir, "updates", "Win11"))

        self.wis_dir = os.environ.get("SERVIS_HUB_WIS_DIR", os.path.join(self.storage_dir, "wis"))

        tmp_root = os.path.abspath(tempfile.gettempdir()).rstrip(os.sep) + os.sep
        is_temp_storage = (
            os.path.abspath(self.storage_dir).startswith(tmp_root)
            or os.path.abspath(self.base_dir).startswith(tmp_root)
        )
        if driver_pool_dir:
            self.driver_pool_dir = os.path.abspath(driver_pool_dir)
        elif os.environ.get("SERVIS_DRIVER_POOL_DIR"):
            self.driver_pool_dir = os.path.abspath(os.environ["SERVIS_DRIVER_POOL_DIR"])
        elif not is_temp_storage and os.path.isdir("/app/harvested") and os.access("/app/harvested", os.W_OK):
            self.driver_pool_dir = "/app/harvested/Pool"
        elif not is_temp_storage and os.path.isdir("/mnt/Servis/Drivers/Harvested") and os.access("/mnt/Servis/Drivers/Harvested", os.W_OK):
            self.driver_pool_dir = "/mnt/Servis/Drivers/Harvested/Pool"
        else:
            self.driver_pool_dir = os.path.join(self.storage_dir, "driver_pool")

        self.ensure_directories()

    def ensure_directories(self):
        """Osigurava postojanje svih radnih direktorijuma."""
        os.makedirs(self.storage_dir, exist_ok=True)
        os.makedirs(self.cache_dir, exist_ok=True)
        os.makedirs(self.drivers_cache_dir, exist_ok=True)
        os.makedirs(self.wis_dir, exist_ok=True)
        os.makedirs(self.logs_dir, exist_ok=True)
        os.makedirs(self.live_logs_dir, exist_ok=True)
        os.makedirs(self.reports_dir, exist_ok=True)
        try:
            os.makedirs(self.model_packs_dir, exist_ok=True)
        except OSError:
            pass
        try:
            os.makedirs(self.driver_pool_dir, exist_ok=True)
        except OSError:
            pass


# Globalna instanca konfiguracije
active_config = HubConfig()
