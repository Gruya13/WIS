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
import threading
from datetime import datetime, timedelta
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig, active_config, get_free_disk_gb
from servishub.telemetry import FleetStore, FLEET_STORE
from servishub.payload import PayloadManager
from servishub.releases import prune_old_releases
from servishub.drivers.cache import DriverCacheManager
from servishub.drivers.pool import DriverPoolManager
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.discovery import UdpDiscoveryServer
from servishub.handlers import ServisHubHandler
from servishub.predictive_cache import PredictiveCacheManager
from servishub.version import VERSION


def _seconds_until_next_local_time(target_hour: int = 3, target_minute: int = 0, now_dt: datetime = None) -> float:
    """Računa broj sekundi do sledećeg lokalnog termina (podrazumevano 03:00 CET)."""
    cur = now_dt if now_dt is not None else datetime.now()
    target = cur.replace(hour=int(target_hour), minute=int(target_minute), second=0, microsecond=0)
    if target <= cur:
        target = target + timedelta(days=1)
    return max(1.0, (target - cur).total_seconds())


def _run_oem_catalog_scheduler_step(pool_mgr, lock: threading.Lock, min_free_gb: float = 15.0) -> dict:
    """Izvršava jedno osvežavanje zastarelih OEM kataloga uz mutex i proveru slobodnog diska >= 15 GB."""
    if pool_mgr is None:
        return {"status": "skipped_no_pool"}
    if not lock.acquire(blocking=False):
        return {"status": "skipped_busy"}
    try:
        free_gb = get_free_disk_gb(getattr(pool_mgr, "pool_dir", None))
        if free_gb is not None and free_gb < float(min_free_gb):
            return {
                "status": "skipped_low_disk",
                "free_gb": round(free_gb, 2),
                "min_free_gb": float(min_free_gb),
            }
        return pool_mgr.refresh_stale_oem_catalogs()
    finally:
        lock.release()


def _run_nightly_gc_step(pool_mgr, cache_dir: str, lock: threading.Lock, keep_last: int = 3) -> dict:
    """Izvršava noćni Garbage Collector: prune_unused_packages() + prune_old_releases(keep_last=3)."""
    if not lock.acquire(blocking=False):
        return {"status": "skipped_busy"}
    try:
        pool_res = pool_mgr.prune_unused_packages() if pool_mgr is not None else None
        rel_res = prune_old_releases(cache_dir, keep_last=keep_last) if cache_dir else None
        return {"status": "ok", "pool": pool_res, "releases": rel_res}
    finally:
        lock.release()


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
    cleaned_tmp_dirs = DriverPoolManager.cleanup_stale_temp_dirs()
    if cleaned_tmp_dirs:
        print(f" [*] Očišćeno {cleaned_tmp_dirs} zaostalih privremenih wis-pool-extract-* foldera.")
    driver_pool_mgr = DriverPoolManager(
        pool_dir=cfg.driver_pool_dir,
        max_bundles_gb=cfg.max_bundles_gb,
        bundle_ttl_hours=cfg.bundle_ttl_hours,
        delete_ingested_archives=cfg.delete_ingested_archives,
        oem_catalog_stale_hours=cfg.oem_catalog_stale_hours,
    )

    def _on_driver_cab_cached(cab_path: str, saved_meta: dict):
        def _bg_ingest():
            try:
                driver_pool_mgr.ingest_archive(cab_path, source="ms_catalog")
            except Exception as exc:
                print(f"[WARN] Pozadinski Driver Pool ingest za .cab nije uspeo: {exc}", file=sys.stderr)
        threading.Thread(target=_bg_ingest, daemon=True).start()

    driver_cache_mgr = DriverCacheManager(cache_dir=cfg.drivers_cache_dir, on_driver_cached=_on_driver_cab_cached)
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
    ServisHubHandler.driver_pool_mgr = driver_pool_mgr
    ServisHubHandler.ms_catalog_scraper = ms_catalog_scraper
    ServisHubHandler.predictive_cache_mgr = PredictiveCacheManager(
        queue_file=os.path.join(cfg.storage_dir, "predictive_queue.json")
    )
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
    pool_stats = driver_pool_mgr.get_stats()
    print(f" [*] Payload Download URL:       http://localhost:{cfg.port}/api/payload/download")
    print(f" [*] Drivers Smart Cache:        {cfg.drivers_cache_dir} ({len(cached_drivers)} keširanih drajvera)")
    print(f" [*] Driver Pool (CAS):          {cfg.driver_pool_dir} ({pool_stats['unique_packages']} paketa / {pool_stats['models_count']} modela)")
    print(f" [*] MS Update Catalog Scraper:  Aktivan (OEM Driver Proxy)")
    updates_avail = "dostupan" if os.path.isdir(cfg.updates_dir) else "nedostupan"
    print(f" [*] Win11 Updates Cache:        {cfg.updates_dir} ({updates_avail})")
    print(f" [*] Arhiva logova:              {cfg.live_logs_dir}")
    print(f" [*] Arhiva izveštaja:          {cfg.reports_dir}")
    print("=" * 70)
    print(" Pritisnite Ctrl+C za prekid servera.\n")

    import threading as _wh_threading
    watcher_stop = _wh_threading.Event()

    def _harvest_pool_watcher():
        # Kratko čekanje pri startu, zatim skeniranje na svakih 300s
        if watcher_stop.wait(10.0):
            return
        while not watcher_stop.is_set():
            try:
                packs_dir = getattr(cfg, "model_packs_dir", None)
                if driver_pool_mgr is not None and packs_dir:
                    res = driver_pool_mgr.scan_and_ingest_archives(packs_dir)
                    if res.get("ingested", 0) > 0:
                        print(f"[DriverPool Watcher] Automatski ingestovano {res.get('ingested')} novih Harvest arhiva.")
            except Exception as ex:
                print(f"[DriverPool Watcher] Upozorenje pri skeniranju: {ex}")
            if watcher_stop.wait(300.0):
                break

    _wh_threading.Thread(target=_harvest_pool_watcher, name="wis-harvest-pool-watcher", daemon=True).start()

    oem_sched_lock = _wh_threading.Lock()
    nightly_gc_lock = _wh_threading.Lock()

    def _oem_catalog_scheduler():
        # 60s posle starta, zatim na svakih 12h uz mutex i disk guard >= 15 GB
        if watcher_stop.wait(60.0):
            return
        while not watcher_stop.is_set():
            try:
                res = _run_oem_catalog_scheduler_step(driver_pool_mgr, oem_sched_lock, min_free_gb=15.0)
                if res.get("status") == "skipped_low_disk":
                    print(
                        f"[OEM Scheduler] Preskačem osvežavanje kataloga: slobodno {res.get('free_gb')} GB < {res.get('min_free_gb')} GB.",
                        file=sys.stderr,
                    )
            except Exception as ex:
                print(f"[OEM Scheduler] Upozorenje pri osvežavanju OEM kataloga: {ex}", file=sys.stderr)
            if watcher_stop.wait(12.0 * 3600.0):
                break

    def _nightly_gc_worker():
        # Noćni Garbage Collector oko 03:00 lokalno (CET)
        while not watcher_stop.is_set():
            wait_s = _seconds_until_next_local_time(3, 0)
            if watcher_stop.wait(wait_s):
                break
            try:
                gc_res = _run_nightly_gc_step(driver_pool_mgr, cfg.cache_dir, nightly_gc_lock, keep_last=3)
                pool_r = gc_res.get("pool") or {}
                rel_r = gc_res.get("releases") or {}
                if pool_r.get("removed_packages") or pool_r.get("removed_bundles") or rel_r.get("removed"):
                    print(
                        f"[Nightly GC] Očišćeno: {pool_r.get('removed_packages', 0)} paketa, "
                        f"{pool_r.get('removed_bundles', 0)} bundlova, {len(rel_r.get('removed') or [])} starih izdanja."
                    )
            except Exception as ex:
                print(f"[Nightly GC] Upozorenje pri noćnom čišćenju: {ex}", file=sys.stderr)
            if watcher_stop.wait(60.0):
                break

    _wh_threading.Thread(target=_oem_catalog_scheduler, name="wis-oem-catalog-scheduler", daemon=True).start()
    _wh_threading.Thread(target=_nightly_gc_worker, name="wis-nightly-gc", daemon=True).start()

    def handle_shutdown(signum, frame):
        print("\n[!] Zaustavljam Servisni Hub...")
        watcher_stop.set()
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
