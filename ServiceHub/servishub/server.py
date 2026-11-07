"""
servishub.server
================
Inicijalizacija, konfiguracija i pokretanje glavnog HTTP i UDP servera.
"""

import os
import sys
import time
import socket
import subprocess
import signal
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig, active_config
from servishub.telemetry import FleetStore, FLEET_STORE
from servishub.payload import PayloadManager
from servishub.drivers.cache import DriverCacheManager
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.discovery import UdpDiscoveryServer
from servishub.handlers import ServisHubHandler
from servishub.version import VERSION


def run_hub_server(
    host: str = None,
    port: int = None,
    udp_port: int = None,
    base_dir: str = None,
    storage_dir: str = None,
    logs_dir: str = None,
    payload_source_dir: str = None,
    updates_dir: str = None,
):
    """
    Glavna funkcija za pokretanje ServisHub servera.
    """
    cfg = HubConfig(
        host=host or active_config.host,
        port=port or active_config.port,
        udp_port=udp_port or active_config.udp_port,
        base_dir=base_dir or active_config.base_dir,
        storage_dir=storage_dir or active_config.storage_dir,
        logs_dir=logs_dir or active_config.logs_dir,
        payload_source_dir=payload_source_dir or active_config.payload_source_dir,
        updates_dir=updates_dir or active_config.updates_dir,
    )

    # Inicijalizacija servisa
    fleet_store = FleetStore(live_logs_dir=cfg.live_logs_dir, reports_dir=cfg.reports_dir)
    print(f" [*] Učitano {len(fleet_store.fleet_devices)} uređaja iz istorijske arhive.")
    driver_cache_mgr = DriverCacheManager(cache_dir=cfg.drivers_cache_dir)
    ms_catalog_scraper = MsUpdateCatalogScraper()
    payload_mgr = PayloadManager(base_dir=cfg.base_dir, cache_dir=cfg.cache_dir, source_dir=cfg.payload_source_dir)

    # Inicijalna provera / priprema payload paketa
    try:
        payload_mgr.ensure_payload()
    except Exception as e:
        print(f"[WARN] Inicijalna provera payload paketa nije uspela: {e}", file=sys.stderr)

    # Injektovanje zavisnosti u handler
    ServisHubHandler.fleet_store = fleet_store
    ServisHubHandler.config = cfg
    ServisHubHandler.payload_mgr = payload_mgr
    ServisHubHandler.driver_cache_mgr = driver_cache_mgr
    ServisHubHandler.ms_catalog_scraper = ms_catalog_scraper
    ServisHubHandler.start_time = time.time()

    # 1. Pokretanje UDP discovery respondera
    udp_srv = UdpDiscoveryServer(http_port=cfg.port, udp_port=cfg.udp_port)
    udp_srv.start()

    # 2. Pokretanje višenitnog HTTP servera
    server_address = (cfg.host, cfg.port)
    httpd = ThreadingHTTPServer(server_address, ServisHubHandler)

    print("=" * 70)
    print(f" 🚀 WIS SERVICE CENTRALNI SERVISNI HUB (ServisHub v{VERSION})")
    print("=" * 70)
    print(f" [*] Web Dashboard dostupan na:  http://localhost:{cfg.port}")

    # Prikaz svih detektovanih adresa po podmrežama
    printed_any = False
    try:
        out = subprocess.check_output(["ip", "-4", "-br", "addr", "show"], text=True)
        for line in out.splitlines():
            parts = line.split()
            if len(parts) >= 3 and parts[0] != "lo":
                iface = parts[0]
                for ip_cidr in parts[2:]:
                    ip_only = ip_cidr.split("/")[0]
                    sub_desc = "LAN Podmreža " if "192.168.88." in ip_only else ("WLAN Podmreža" if "192.168.180." in ip_only else "Servisna Mreža")
                    print(f" [*] {sub_desc} ({iface:<4}):  http://{ip_only}:{cfg.port}")
                    printed_any = True
    except Exception:
        pass

    if not printed_any:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            primary_ip = s.getsockname()[0]
            s.close()
            print(f" [*] Pristup iz lokalne mreže:   http://{primary_ip}:{cfg.port}")
        except Exception:
            pass

    try:
        meta = payload_mgr.get_metadata()
    except Exception:
        meta = {}
    cached_drivers = driver_cache_mgr.list_cached()
    print(f" [*] UDP Auto-Discovery:         Port {cfg.udp_port} (aktivan)")
    if meta:
        print(f" [*] Potpisano WIS izdanje:  v{meta.get('version')} ({meta.get('size_bytes', 0)} B, SHA256: {meta.get('sha256', '')[:10]}...)")
    print(f" [*] Payload Download URL:       http://localhost:{cfg.port}/api/payload/download")
    print(f" [*] Drivers Smart Cache:        {cfg.drivers_cache_dir} ({len(cached_drivers)} keširanih drajvera)")
    print(f" [*] MS Update Catalog Scraper:  Aktivan (OEM Driver Proxy)")
    updates_avail = "dostupan" if os.path.isdir(cfg.updates_dir) else "nedostupan"
    print(f" [*] Win11 Updates Cache:        {cfg.updates_dir} ({updates_avail})")
    print(f" [*] Arhiva logova:              {cfg.live_logs_dir}")
    print(f" [*] Arhiva izveštaja:          {cfg.reports_dir}")
    print("=" * 70)
    print(" Pritisnite Ctrl+C za prekid servera.\n")

    def handle_shutdown(signum, frame):
        print("\n[!] Zaustavljam Servisni Hub...")
        udp_srv.running = False
        httpd.server_close()
        print("[OK] Server je ugašen.")
        sys.exit(0)

    signal.signal(signal.SIGINT, handle_shutdown)
    signal.signal(signal.SIGTERM, handle_shutdown)

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        handle_shutdown(None, None)
