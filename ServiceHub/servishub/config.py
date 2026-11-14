"""
servishub.config
================
Konfiguracija i upravljanje putanjama za ServisHub.
"""

import os

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
    "/mnt/Servis/Drivers/Harvested/Archives" if os.path.isdir("/mnt/Servis") else os.path.join(DRIVERS_CACHE_DIR, "model_packs"),
)

# Skladište zakrpi (Windows 11 updates keš)
DEFAULT_UPDATES_DIR = os.environ.get(
    "SERVIS_UPDATES_DIR",
    os.environ.get("SERVIS_HUB_UPDATES_DIR", "/home/gru/Storage/Updates/Win11")
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

        if model_packs_dir:
            self.model_packs_dir = os.path.abspath(model_packs_dir)
        elif os.environ.get("SERVIS_MODEL_PACKS_DIR"):
            self.model_packs_dir = os.path.abspath(os.environ["SERVIS_MODEL_PACKS_DIR"])
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
        elif os.path.isdir("/home/gru/Storage/Updates/Win11"):
            self.updates_dir = "/home/gru/Storage/Updates/Win11"
        elif os.path.isdir("/mnt/Servis/Updates/Win11"):
            self.updates_dir = "/mnt/Servis/Updates/Win11"
        elif os.path.isdir("/mnt/Servis"):
            self.updates_dir = "/mnt/Servis/Updates/Win11"
        elif os.path.isdir(os.path.join(self.base_dir, "Updates", "Win11")):
            self.updates_dir = os.path.abspath(os.path.join(self.base_dir, "Updates", "Win11"))
        else:
            self.updates_dir = os.path.abspath(DEFAULT_UPDATES_DIR)

        self.wis_dir = os.environ.get("SERVIS_HUB_WIS_DIR", os.path.join(self.storage_dir, "wis"))

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


# Globalna instanca konfiguracije
active_config = HubConfig()
