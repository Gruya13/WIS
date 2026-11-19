"""
servishub.config
================
Konfiguracija i upravljanje putanjama za ServisHub.
"""

import os
import tempfile

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
