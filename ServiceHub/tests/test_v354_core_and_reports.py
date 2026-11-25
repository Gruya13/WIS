"""
test_v354_core_and_reports.py
=============================
Sveobuhvatni testovi za WIS v3.5.4 / ServiceHub v3.7.1 (WIS-TASK-278):
- Celina B: daemon scheduler + 15 GB guard, noćni GC (03:00), TIER1_OEM_VENDORS,
  rani P2 OEM prefetch sa deduplikacijom, /api/drivers/resolve (bez warranty po defaultu,
  batch + 3-stepeni redosled + downloading), oem_pack > ms_catalog > harvest, .msi ekstrakcija.
- Celina D: IMP-89 clean_all bez generičkog bloat-a, pametni LRU keš (hits >= 2 & <= max_gb)
  vs on-the-fly .tar streaming (0 B u bundles/, bez X-Model-SHA256), post-ingest cleanup
  uz .harvest.lock zaštitu, prune_old_releases(keep_last=3) uz zaštitu current.json.
- Celina E: IMP-94 boolean apps + zaštita od all-False/cpu=None pregaza,
  IMP-94b parcijalni HwTest (phase5_hwtest / is_partial_hwtest),
  IMP-72 spajanje W-AAD-* -> TK-LAPTOP-<SN> po serial_number + previous_hostname.
"""

import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from datetime import datetime
from http.server import ThreadingHTTPServer
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

ROOT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ROOT_DIR not in sys.path:
    sys.path.insert(0, ROOT_DIR)

from servishub.drivers.pool import DriverPoolManager
from servishub.handlers import ServisHubHandler
from servishub.releases import prune_old_releases
from servishub.reports import (
    generate_markdown_report,
    is_partial_hwtest_payload,
    merge_app_statuses,
    save_device_report,
)
from servishub.server import (
    _run_nightly_gc_step,
    _run_oem_catalog_scheduler_step,
    _seconds_until_next_local_time,
)
from servishub.telemetry import FleetStore


def _make_sample_inf_bytes(
    provider="Intel",
    ver="01/15/2024,31.0.101.5186",
    hwid=r"PCI\VEN_8086&DEV_9A49&SUBSYS_0A381028",
    drv_class="Display",
) -> bytes:
    txt = (
        "[Version]\n"
        'Signature="$WINDOWS NT$"\n'
        f"Class={drv_class}\n"
        f"Provider=%Mfg%\n"
        f"DriverVer={ver}\n"
        "CatalogFile=test.cat\n\n"
        "[Manufacturer]\n"
        "%Mfg%=Models,NTamd64\n\n"
        "[Models.NTamd64]\n"
        f"%DevDesc%=InstallSec, {hwid}\n\n"
        "[InstallSec]\n"
        "CopyFiles=FilesSec\n\n"
        "[FilesSec]\n"
        "dummy.sys\n\n"
        "[Strings]\n"
        f'Mfg="{provider}"\n'
        'DevDesc="Test Device"\n'
    )
    return txt.encode("utf-8")


def _make_driver_tar(
    dest_tar: str,
    inf_name: str = "test.inf",
    ver: str = "01/15/2024,31.0.101.5186",
    hwid: str = r"PCI\VEN_8086&DEV_9A49&SUBSYS_0A381028",
    with_cat: bool = True,
    extra_size: int = 0,
):
    inf_bytes = _make_sample_inf_bytes(ver=ver, hwid=hwid)
    with tarfile.open(dest_tar, "w") as tf:
        ti = tarfile.TarInfo(name=f"drv/{inf_name}")
        ti.size = len(inf_bytes)
        tf.addfile(ti, io.BytesIO(inf_bytes))

        sys_bytes = b"SYS_PAYLOAD_" + (b"A" * max(0, extra_size))
        ts = tarfile.TarInfo(name="drv/dummy.sys")
        ts.size = len(sys_bytes)
        tf.addfile(ts, io.BytesIO(sys_bytes))

        if with_cat:
            cat_bytes = b"CAT_CERT"
            tc = tarfile.TarInfo(name="drv/test.cat")
            tc.size = len(cat_bytes)
            tf.addfile(tc, io.BytesIO(cat_bytes))


class _TestHandler(ServisHubHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)


class TestV354CoreAndReports(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v354_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    # ------------------------------------------------------------------
    # Celina B: Daemon scheduler, noćni GC i TIER1_OEM_VENDORS
    # ------------------------------------------------------------------

    def test_01_oem_scheduler_disk_guard_and_nightly_gc(self):
        pool_mgr = MagicMock()
        pool_mgr.pool_dir = self.tmp_dir
        pool_mgr.refresh_stale_oem_catalogs.return_value = {"status": "ok"}
        pool_mgr.prune_unused_packages.return_value = {"removed_count": 1}

        lock = threading.Lock()
        with patch("servishub.server.get_free_disk_gb", return_value=10.5):
            res_low = _run_oem_catalog_scheduler_step(pool_mgr, lock, min_free_gb=15.0)
        self.assertEqual(res_low["status"], "skipped_low_disk")
        pool_mgr.refresh_stale_oem_catalogs.assert_not_called()

        with patch("servishub.server.get_free_disk_gb", return_value=42.0):
            res_ok = _run_oem_catalog_scheduler_step(pool_mgr, lock, min_free_gb=15.0)
        self.assertEqual(res_ok["status"], "ok")
        pool_mgr.refresh_stale_oem_catalogs.assert_called_once()

        # Noćni GC poziva prune_unused_packages + prune_old_releases(cache_dir, keep_last=3)
        cache_dir = os.path.join(self.tmp_dir, "rel_cache")
        rel_dir = os.path.join(cache_dir, "releases")
        os.makedirs(rel_dir, exist_ok=True)
        for idx in range(5):
            d = os.path.join(rel_dir, f"v3.5.{idx}")
            os.makedirs(d, exist_ok=True)
            os.utime(d, (1000 + idx * 10, 1000 + idx * 10))
        gc_res = _run_nightly_gc_step(pool_mgr, cache_dir, lock, keep_last=3)
        pool_mgr.prune_unused_packages.assert_called_once()
        self.assertEqual(len(gc_res["releases"]["removed"]), 2)

        sec = _seconds_until_next_local_time(3, 0, now_dt=datetime(2026, 11, 25, 2, 45, 0))
        self.assertAlmostEqual(sec, 900.0, delta=2.0)

    def test_02_refresh_stale_oem_catalogs_uses_tier1_vendors(self):
        pool_mgr = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))
        pool_mgr.catalog_mgr.get_catalog_status = MagicMock(return_value={
            "dell": {"synced": True, "age_hours": 100.0},
            "hp": {"synced": True, "age_hours": 1.0},
            "lenovo": {"synced": False, "age_hours": None},
            "microsoft": {"synced": True, "age_hours": 100.0},
            "fujitsu": {"synced": False, "age_hours": None},
        })
        pool_mgr.catalog_mgr.sync_catalog = MagicMock(return_value={"status": "ok"})
        res = pool_mgr.refresh_stale_oem_catalogs()
        called_vendors = [c.args[0] for c in pool_mgr.catalog_mgr.sync_catalog.call_args_list]
        self.assertEqual(called_vendors, ["dell", "lenovo", "microsoft", "fujitsu"])
        self.assertEqual(res["vendors"]["hp"]["status"], "fresh")
        for v in ("dell", "lenovo", "microsoft", "fujitsu"):
            self.assertEqual(res["vendors"][v]["status"], "refreshed")

    # ------------------------------------------------------------------
    # Celina B: Prioritet oem_pack > ms_catalog > harvest i .msi podrška
    # ------------------------------------------------------------------

    def test_03_signed_oem_pack_beats_newer_ms_catalog_and_harvest(self):
        pool_mgr = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))
        oem_older = {
            "sources": ["oem_pack"],
            "has_cat": True,
            "driver_date": "2023-01-01",
            "driver_ver_tuple": (10, 0, 1, 1),
        }
        ms_newer = {
            "sources": ["ms_catalog"],
            "has_cat": True,
            "driver_date": "2026-01-01",
            "driver_ver_tuple": (99, 0, 1, 1),
        }
        harvest_newer = {
            "sources": ["harvest"],
            "has_cat": True,
            "driver_date": "2026-05-01",
            "driver_ver_tuple": (99, 9, 9, 9),
        }
        self.assertTrue(pool_mgr._is_package_better(oem_older, ms_newer))
        self.assertFalse(pool_mgr._is_package_better(ms_newer, oem_older))
        self.assertTrue(pool_mgr._is_package_better(oem_older, harvest_newer))
        self.assertFalse(pool_mgr._is_package_better(harvest_newer, oem_older))

        oem_newer = {
            "sources": ["oem_pack"],
            "has_cat": True,
            "driver_date": "2025-06-01",
            "driver_ver_tuple": (11, 0, 1, 1),
        }
        self.assertTrue(pool_mgr._is_package_better(oem_newer, oem_older))

    def test_04_msi_archive_extraction_via_microsoft_surface(self):
        pool_mgr = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))
        msi_path = os.path.join(self.tmp_dir, "SurfacePro9.msi")
        with open(msi_path, "wb") as f:
            f.write(b"FAKE_MSI")

        def _fake_extract_msi(src_msi: str, dest_dir: str):
            os.makedirs(dest_dir, exist_ok=True)
            inf_p = os.path.join(dest_dir, "surface_hid.inf")
            with open(inf_p, "wb") as f:
                f.write(_make_sample_inf_bytes(hwid=r"ACPI\MSHW0101"))
            with open(os.path.join(dest_dir, "dummy.sys"), "wb") as f:
                f.write(b"SURFACE_SYS")
            with open(os.path.join(dest_dir, "test.cat"), "wb") as f:
                f.write(b"SURFACE_CAT")
            return [inf_p]

        with patch("servishub.drivers.pool.extract_msi_drivers", side_effect=_fake_extract_msi) as mock_ext:
            res = pool_mgr.ingest_archive(msi_path, source="oem_pack", model="Surface Pro 9")
        mock_ext.assert_called_once()
        self.assertEqual(res["packages_added"], 1)
        self.assertTrue(pool_mgr.has_oem_pack_in_manifest("Surface Pro 9"))

    # ------------------------------------------------------------------
    # Celina D: IMP-89, Pametni LRU keš / Streaming, Post-ingest cleanup, Releases
    # ------------------------------------------------------------------

    def test_05_imp89_clean_all_blocks_generic_global_fallback_when_manifest_exists(self):
        pool_mgr = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))
        t1 = os.path.join(self.tmp_dir, "model_drv.tar")
        t2 = os.path.join(self.tmp_dir, "other_generic.tar")
        _make_driver_tar(t1, inf_name="lat.inf", hwid=r"PCI\VEN_8086&DEV_1111&SUBSYS_00011028")
        _make_driver_tar(t2, inf_name="other.inf", hwid=r"PCI\VEN_10EC&DEV_8168")

        pool_mgr.ingest_archive(t1, source="oem_pack", model="Latitude_5420")
        pool_mgr.ingest_archive(t2, source="harvest", model=None)  # globalni generički bez SUBSYS-a

        bundle = pool_mgr.build_bundle_tar(
            model="Latitude_5420",
            all_hwids=[r"PCI\VEN_8086&DEV_1111&SUBSYS_00011028", r"PCI\VEN_10EC&DEV_8168"],
        )
        self.assertTrue(bundle["available"])
        self.assertEqual(bundle["package_count"], 1)
        self.assertIn(r"PCI\VEN_10EC&DEV_8168", bundle["missing_unmatched_hwids"])

        # Za clean_missing (ciljana popravka) globalni fallback ostaje dozvoljen
        bundle_missing = pool_mgr.build_bundle_tar(
            model="Latitude_5420",
            missing_hwids=[r"PCI\VEN_10EC&DEV_8168"],
        )
        self.assertTrue(bundle_missing["available"])
        self.assertEqual(bundle_missing["package_count"], 1)

    def test_06_post_ingest_cleanup_respects_harvest_lock_and_size_stability(self):
        pool_dir = os.path.join(self.tmp_dir, "pool")
        arch_dir = os.path.join(self.tmp_dir, "Archives")
        os.makedirs(arch_dir, exist_ok=True)

        locked_tar = os.path.join(arch_dir, "ModelA.tar")
        ready_tar = os.path.join(arch_dir, "ModelB.tar")
        _make_driver_tar(locked_tar, inf_name="a.inf", hwid=r"PCI\VEN_8086&DEV_A001")
        _make_driver_tar(ready_tar, inf_name="b.inf", hwid=r"PCI\VEN_8086&DEV_B001")
        with open(os.path.join(arch_dir, "ModelA.harvest.lock"), "w", encoding="utf-8") as lf:
            lf.write("busy")

        pool_mgr = DriverPoolManager(pool_dir=pool_dir, delete_ingested_archives=True)
        res = pool_mgr.scan_and_ingest_archives(arch_dir)
        self.assertEqual(res["ingested"], 2)
        self.assertEqual(res["deleted_archives"], 1)
        self.assertTrue(os.path.isfile(locked_tar))
        self.assertFalse(os.path.isfile(ready_tar))

    def test_07_prune_old_releases_keeps_last_3_and_active_current_json(self):
        cache_dir = os.path.join(self.tmp_dir, "rel_cache_p")
        rel_dir = os.path.join(cache_dir, "releases")
        os.makedirs(rel_dir, exist_ok=True)
        versions = ["v3.1.0", "v3.2.0", "v3.3.0", "v3.4.0", "v3.5.0"]
        for i, v in enumerate(versions):
            p = os.path.join(rel_dir, v)
            os.makedirs(p, exist_ok=True)
            os.utime(p, (2000 + i * 100, 2000 + i * 100))

        # Najstariji release je aktivan u current.json -> ne sme biti obrisan!
        with open(os.path.join(cache_dir, "current.json"), "w", encoding="utf-8") as cf:
            json.dump({"release_id": "v3.1.0"}, cf)

        res = prune_old_releases(cache_dir, keep_last=3)
        self.assertEqual(res["status"], "ok")
        self.assertTrue(os.path.isdir(os.path.join(rel_dir, "v3.1.0")))
        self.assertFalse(os.path.isdir(os.path.join(rel_dir, "v3.2.0")))
        for kept in ("v3.3.0", "v3.4.0", "v3.5.0"):
            self.assertTrue(os.path.isdir(os.path.join(rel_dir, kept)))

    # ------------------------------------------------------------------
    # Celina B + D HTTP integracija: prefetch, resolve, bundle streaming vs LRU
    # ------------------------------------------------------------------

    def test_08_http_bundle_streaming_lru_prefetch_and_resolve(self):
        pool_dir = os.path.join(self.tmp_dir, "http_pool")
        bundles_dir = os.path.join(self.tmp_dir, "http_bundles")
        reports_dir = os.path.join(self.tmp_dir, "reports")
        os.makedirs(reports_dir, exist_ok=True)

        pool_mgr = DriverPoolManager(
            pool_dir=pool_dir,
            bundles_dir=bundles_dir,
            bundle_cache_min_hits=2,
            bundle_cache_max_gb=4.0,
        )
        t_path = os.path.join(self.tmp_dir, "hp_drv.tar")
        _make_driver_tar(t_path, inf_name="hp_net.inf", hwid=r"PCI\VEN_8086&DEV_15F3&SUBSYS_8711103C")
        pool_mgr.ingest_archive(t_path, source="oem_pack", model="HP_EliteBook_840_G8")

        cfg = SimpleNamespace(
            reports_dir=reports_dir,
            storage_dir=self.tmp_dir,
            driver_pool_dir=pool_dir,
            allow_unauthenticated_lan=True,
            max_content_length=16 * 1024 * 1024,
        )
        fleet_store = FleetStore(reports_dir=reports_dir)
        ms_scraper = MagicMock()
        ms_scraper.resolve_driver.return_value = None

        class _BoundHandler(_TestHandler):
            pass

        _BoundHandler.config = cfg
        _BoundHandler.fleet_store = fleet_store
        _BoundHandler.driver_pool_mgr = pool_mgr
        _BoundHandler.ms_catalog_scraper = ms_scraper
        _BoundHandler.driver_cache_mgr = None

        httpd = ThreadingHTTPServer(("127.0.0.1", 0), _BoundHandler)
        port = httpd.server_address[1]
        t = threading.Thread(target=httpd.serve_forever, daemon=True)
        t.start()
        try:
            def _post(path, body):
                req = urllib.request.Request(
                    f"http://127.0.0.1:{port}{path}",
                    data=json.dumps(body).encode("utf-8"),
                    method="POST",
                    headers={"Content-Type": "application/json"},
                )
                with urllib.request.urlopen(req, timeout=10) as resp:
                    return resp.status, dict(resp.headers), resp.read()

            # 1. Prvi bundle zahtev (hits=1 < 2): on-the-fly streaming, 0 B u bundles/, bez X-Model-SHA256
            st1, hdr1, raw1 = _post("/api/drivers/pool/bundle", {"model": "HP_EliteBook_840_G8"})
            self.assertEqual(st1, 200)
            self.assertEqual(hdr1.get("X-Driver-Pool-Cache-Hit"), "false")
            self.assertEqual(hdr1.get("X-Driver-Pool-Stream"), "true")
            self.assertIsNone(hdr1.get("X-Model-SHA256"))
            self.assertEqual(os.listdir(bundles_dir) if os.path.isdir(bundles_dir) else [], [])
            with tarfile.open(fileobj=io.BytesIO(raw1), mode="r:*") as tf:
                self.assertTrue(any(m.name.endswith("hp_net.inf") for m in tf.getmembers()))

            # 2. Drugi bundle zahtev (hits=2 >= 2): kešira u bundles/
            st2, hdr2, _ = _post("/api/drivers/pool/bundle", {"model": "HP_EliteBook_840_G8"})
            self.assertEqual(st2, 200)
            self.assertEqual(hdr2.get("X-Driver-Pool-Cache-Hit"), "false")
            self.assertTrue(hdr2.get("X-Model-SHA256"))
            self.assertTrue(any(fn.endswith(".tar") for fn in os.listdir(bundles_dir)))

            # 3. Treći bundle zahtev (hits=3): cache_hit=true
            st3, hdr3, _ = _post("/api/drivers/pool/bundle", {"model": "HP_EliteBook_840_G8"})
            self.assertEqual(st3, 200)
            self.assertEqual(hdr3.get("X-Driver-Pool-Cache-Hit"), "true")

            # 4. /api/drivers/resolve bez include_warranty -> nema warranty u odgovoru, pogađa lokalni oem_pack
            with patch("servishub.handlers.get_oem_provider") as mock_prov_fn:
                mock_prov = MagicMock()
                mock_prov.resolve_driver_pack.return_value = {"status": "ok", "package_name": "sp12345.exe"}
                mock_prov_fn.return_value = mock_prov
                st_r, _, raw_r = _post("/api/drivers/resolve", {
                    "manufacturer": "HP",
                    "model": "HP EliteBook 840 G8",
                    "serial_number": "5CG0001111",
                    "async_download": True,
                    "devices": [{
                        "device_name": "Intel NIC",
                        "hwids": [r"PCI\VEN_8086&DEV_15F3&SUBSYS_8711103C"],
                    }],
                })
                self.assertEqual(st_r, 200)
                mock_prov.get_warranty_info.assert_not_called()
                r_json = json.loads(raw_r.decode("utf-8"))
                self.assertNotIn("warranty", r_json)
                self.assertEqual(r_json["resolved_count"], 1)
                dl_url = r_json["results"][0]["download_url"]
                self.assertTrue(dl_url.startswith("/api/drivers/pool/package?id="))

                # Provera GET /api/drivers/pool/package?id=...
                with urllib.request.urlopen(f"http://127.0.0.1:{port}{dl_url}", timeout=10) as p_resp:
                    self.assertEqual(p_resp.status, 200)
                    with tarfile.open(fileobj=io.BytesIO(p_resp.read()), mode="r:*") as p_tf:
                        self.assertTrue(any(m.name.endswith("hp_net.inf") for m in p_tf.getmembers()))

            # 5. /api/drivers/resolve za drugi model gde je OEM paket dostupan u katalogu -> status="downloading"
            pool_mgr.catalog_mgr.find_driver_pack = MagicMock(return_value={
                "package_id": "dell_lat_5530",
                "package_name": "Latitude-5530.cab",
            })
            pool_mgr.ingest_oem_pack = MagicMock(return_value={"status": "ok"})
            st_dl, _, raw_dl = _post("/api/drivers/resolve", {
                "manufacturer": "Dell Inc.",
                "model": "Latitude 5530",
                "system_sku": "0B23",
                "async_download": True,
                "hwids": [r"PCI\VEN_10EC&DEV_525A"],
            })
            self.assertEqual(st_dl, 200)
            dl_json = json.loads(raw_dl.decode("utf-8"))
            self.assertEqual(dl_json["results"][0]["status"], "downloading")
            self.assertEqual(dl_json["results"][0]["source"], "oem_pack_async")
            ms_scraper.resolve_driver.assert_not_called()

            # 6. Rani P2 OEM prefetch sa deduplikacijom
            pool_mgr.start_dedup_background_job = MagicMock(return_value={"job_id": "dp_1", "status": "running"})
            try:
                _post("/api/drivers/pool/bundle", {
                    "manufacturer": "Dell Inc.",
                    "model": "Latitude 5530",
                    "system_sku": "0B23",
                })
            except urllib.error.HTTPError as he:
                self.assertEqual(he.code, 404)
                he.close()
            pool_mgr.start_dedup_background_job.assert_called_once()
        finally:
            httpd.shutdown()
            httpd.server_close()

    # ------------------------------------------------------------------
    # Celina E: IMP-94, IMP-94b, IMP-72
    # ------------------------------------------------------------------

    def test_09_imp94_boolean_apps_and_merge_protection(self):
        md = generate_markdown_report({
            "device_id": "TK-LAPTOP-ABC1",
            "computer_name": "TK-LAPTOP-ABC1",
            "apps": {"Chrome": True, "7-Zip": False, "Office": "OK"},
        })
        self.assertIn("| Chrome | ✅ OK |", md)
        self.assertIn("| 7-Zip | ❌ Nedostaje |", md)
        self.assertNotIn("⏳ False", md)

        merged_apps = merge_app_statuses(
            {"Chrome": "✅ OK", "7-Zip": True},
            {"Chrome": False, "7-Zip": False, "Edge": True, "VLC": False},
        )
        self.assertEqual(merged_apps["Chrome"], "✅ OK")
        self.assertIs(merged_apps["7-Zip"], True)
        self.assertIs(merged_apps["Edge"], True)
        self.assertNotIn("VLC", merged_apps)

        reports_dir = os.path.join(self.tmp_dir, "rep_imp94")
        os.makedirs(reports_dir, exist_ok=True)
        save_device_report("TK-LAPTOP-ABC1", {
            "device_id": "TK-LAPTOP-ABC1",
            "computer_name": "TK-LAPTOP-ABC1",
            "serial_number": "ABC1",
            "duration_seconds": 320,
            "duration_formatted": "5m 20s",
            "apps": {"Chrome": True, "7-Zip": True},
            "hw": {"cpu": "Intel Core i5-1235U", "ram": "16 GB"},
        }, reports_dir)

        # Sekundarni payload sa all-False i cpu=None ne sme pregaziti potvrđene vrednosti
        json_fp, _, _, _ = save_device_report("TK-LAPTOP-ABC1", {
            "device_id": "TK-LAPTOP-ABC1",
            "computer_name": "TK-LAPTOP-ABC1",
            "serial_number": "ABC1",
            "duration_seconds": 0,
            "apps": {"Chrome": False, "7-Zip": False},
            "hw": {"cpu": None, "ram": None},
        }, reports_dir)
        with open(json_fp, "r", encoding="utf-8") as f:
            saved = json.load(f)
        self.assertIs(saved["apps"]["Chrome"], True)
        self.assertEqual(saved["hw"]["cpu"], "Intel Core i5-1235U")
        self.assertEqual(saved["duration_seconds"], 320)

    def test_10_imp94b_partial_hwtest_does_not_create_or_overwrite_final_report(self):
        self.assertTrue(is_partial_hwtest_payload({"is_partial_hwtest": True}))
        self.assertTrue(is_partial_hwtest_payload({"report_stage": "phase5_hwtest"}))

        reports_dir = os.path.join(self.tmp_dir, "rep_imp94b")
        os.makedirs(reports_dir, exist_ok=True)
        res_tuple = save_device_report("TK-LAPTOP-HW1", {
            "device_id": "TK-LAPTOP-HW1",
            "is_partial_hwtest": True,
            "report_stage": "phase5_hwtest",
            "duration_seconds": 0,
        }, reports_dir)
        self.assertEqual(res_tuple, (None, None, None, None))
        self.assertEqual(os.listdir(reports_dir), [])

        store = FleetStore(reports_dir=reports_dir)
        dev_id = store.record_lifecycle_complete({
            "computer_name": "TK-LAPTOP-HW1",
            "serial_number": "HW1",
            "report_stage": "phase5_hwtest",
            "is_partial_hwtest": True,
            "hwtest": {"status": "PASS"},
        }, "10.0.0.5")
        self.assertEqual(dev_id, "TK-LAPTOP-HW1")
        self.assertEqual(os.listdir(reports_dir), [])

    def test_11_imp72_waad_to_tk_laptop_merges_session_and_report(self):
        reports_dir = os.path.join(self.tmp_dir, "rep_imp72")
        os.makedirs(reports_dir, exist_ok=True)
        store = FleetStore(reports_dir=reports_dir)

        k1 = store.record_heartbeat({
            "computer_name": "W-AAD-9001",
            "serial_number": "SN778899",
            "phase": 2,
            "worker_progress": 30,
        }, "10.0.0.20")
        store.record_logs("W-AAD-9001", ["Faza 2 OK"], serial_number="SN778899")

        k2 = store.record_heartbeat({
            "computer_name": "TK-LAPTOP-SN778899",
            "previous_hostname": "W-AAD-9001",
            "serial_number": "SN778899",
            "phase": 5,
            "worker_progress": 80,
        }, "10.0.0.20")
        self.assertEqual(k2, "TK-LAPTOP-SN778899")
        self.assertEqual(len(store.fleet_devices), 1)
        self.assertEqual(store.fleet_devices[k2]["computer_name"], "TK-LAPTOP-SN778899")

        # Karton: prvi snimljen pod W-AAD-9001, drugi pod TK-LAPTOP-SN778899 sa previous_hostname
        save_device_report("W-AAD-9001", {
            "device_id": "W-AAD-9001",
            "computer_name": "W-AAD-9001",
            "serial_number": "SN778899",
            "apps": {"Chrome": True},
            "hw": {"cpu": "AMD Ryzen 5"},
        }, reports_dir)
        json_fp, _, _, _ = save_device_report("TK-LAPTOP-SN778899", {
            "device_id": "TK-LAPTOP-SN778899",
            "computer_name": "TK-LAPTOP-SN778899",
            "previous_hostname": "W-AAD-9001",
            "serial_number": "SN778899",
            "apps": {"7-Zip": True},
        }, reports_dir)
        json_files = [fn for fn in os.listdir(reports_dir) if fn.endswith(".json") and not fn.startswith(".")]
        self.assertEqual(len(json_files), 1)
        self.assertIn("TK-LAPTOP-SN778899", os.path.basename(json_fp))
        with open(json_fp, "r", encoding="utf-8") as f:
            merged_rep = json.load(f)
        self.assertIs(merged_rep["apps"]["Chrome"], True)
        self.assertIs(merged_rep["apps"]["7-Zip"], True)
        self.assertEqual(merged_rep["hw"]["cpu"], "AMD Ryzen 5")


if __name__ == "__main__":
    unittest.main()
