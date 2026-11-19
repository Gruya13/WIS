"""
tests.test_benchmark_and_predictive
===================================
Jedinični i integracioni testovi za:
- WIS Benchmark (`servishub.benchmark`, `GET /api/benchmark/summary`, Markdown izveštaj)
- Model DNA (`update_model_dna`, `POST /api/model-profile/dna`, `GET /api/model-profiles`, pasivni `/api/drivers/resolve`)
- Predictive Cache (`servishub.predictive_cache`, `POST /api/predictive/prewarm`, `GET /api/predictive/status`)
"""

import json
import os
import shutil
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

from servishub.benchmark import compute_fleet_benchmark_summary, extract_device_benchmark
from servishub.config import HubConfig, active_config
from servishub.drivers.cache import DriverCacheManager
from servishub.handlers import ServisHubHandler
from servishub.model_profiles import list_model_profiles, load_model_profile, update_model_dna
from servishub.predictive_cache import PredictiveCacheManager
from servishub.reports import generate_markdown_report
from servishub.telemetry import FleetStore


class _StubCatalogScraper:
    """Offline stub za MS Update Catalog scraper radi determinističkog testiranja pre-warming-a."""

    def __init__(self):
        self.resolved_queries = []

    def resolve_driver(self, hwid: str):
        self.resolved_queries.append(hwid)
        if "9999" in hwid:
            return {
                "title": "Stub Intel Chipset Prewarm Driver",
                "version": "10.1.19600.8418",
                "date_str": "01/15/2026",
                "device_class": "System",
                "products": "Windows 11",
                "matched_query": hwid,
                "download_url": "https://stub.local/driver.cab",
            }
        return None

    def download_cab(self, url: str) -> bytes:
        return b"MSCF" + b"\x00" * 1020


class TestBenchmarkAndPredictiveCache(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp(prefix="wis_bench_pred_")
        cls.updates_dir = os.path.join(cls.test_dir, "updates_win11")
        cls.model_packs_dir = os.path.join(cls.test_dir, "model_packs")
        os.makedirs(cls.updates_dir, exist_ok=True)
        os.makedirs(cls.model_packs_dir, exist_ok=True)

        # Kreiraj lažni 24H2 MSU paket da proverimo Windows Update spremnost
        with open(os.path.join(cls.updates_dir, "Windows11.0-KB5051987-24H2-x64.msu"), "wb") as f:
            f.write(b"MSU_TEST" * 1024)

        cls.orig_base = active_config.base_dir
        active_config.base_dir = cls.test_dir

        cls.config = HubConfig(
            base_dir=cls.test_dir,
            storage_dir=os.path.join(cls.test_dir, "storage"),
            logs_dir=os.path.join(cls.test_dir, "Logs"),
            updates_dir=cls.updates_dir,
            model_packs_dir=cls.model_packs_dir,
        )
        cls.fleet_store = FleetStore(
            live_logs_dir=cls.config.live_logs_dir,
            reports_dir=cls.config.reports_dir,
        )
        cls.driver_cache = DriverCacheManager(cls.config.drivers_cache_dir)
        cls.stub_scraper = _StubCatalogScraper()
        cls.predictive_mgr = PredictiveCacheManager(
            queue_file=os.path.join(cls.config.storage_dir, "predictive_queue.json")
        )

        # Sačuvaj prethodno stanje ServisHubHandler klase
        cls._prev_handler_attrs = {
            "config": ServisHubHandler.config,
            "fleet_store": ServisHubHandler.fleet_store,
            "driver_cache_mgr": ServisHubHandler.driver_cache_mgr,
            "ms_catalog_scraper": ServisHubHandler.ms_catalog_scraper,
            "predictive_cache_mgr": getattr(ServisHubHandler, "predictive_cache_mgr", None),
            "payload_mgr": ServisHubHandler.payload_mgr,
        }

        ServisHubHandler.config = cls.config
        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.driver_cache_mgr = cls.driver_cache
        ServisHubHandler.ms_catalog_scraper = cls.stub_scraper
        ServisHubHandler.predictive_cache_mgr = cls.predictive_mgr
        ServisHubHandler.payload_mgr = None

        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        cls.port = cls.httpd.server_address[1]
        cls.base_url = f"http://127.0.0.1:{cls.port}"

        cls.server_thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        active_config.base_dir = cls.orig_base
        for k, v in cls._prev_handler_attrs.items():
            setattr(ServisHubHandler, k, v)
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def _http_json(self, path: str, payload: dict = None, method: str = None):
        url = f"{self.base_url}{path}"
        data = None
        headers = {}
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
            method = method or "POST"
        else:
            method = method or "GET"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                return resp.status, json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as he:
            try:
                return he.code, json.loads(he.read().decode("utf-8"))
            finally:
                he.close()

    def test_01_model_dna_api_and_deduplication(self):
        # Greška 400 kada fale polja
        code_err, _ = self._http_json("/api/model-profile/dna", {"model": "ThinkPad T14 Gen 3"})
        self.assertEqual(code_err, 400)

        code, resp = self._http_json("/api/model-profile/dna", {
            "model": "ThinkPad T14 Gen 3",
            "manufacturer": "LENOVO",
            "os_build": "26100",
            "hwids": [
                r"PCI\VEN_8086&DEV_51C8",
                r"pci\\ven_8086&dev_51c8",  # duplikat po normalizaciji
                r"USB\VID_04F2&PID_B751",
                "INVALID_NO_SLASH",
            ],
        })
        self.assertEqual(code, 200)
        self.assertEqual(resp["status"], "ok")
        prof = resp["profile"]
        self.assertEqual(prof["manufacturer"], "LENOVO")
        self.assertEqual(prof["preferred_os_build"], "26100")
        self.assertEqual(len(prof["known_hwids"]), 2)

        # GET /api/model-profiles mora vratiti naučeni model
        code_list, list_data = self._http_json("/api/model-profiles")
        self.assertEqual(code_list, 200)
        models = [p["model"] for p in list_data["profiles"]]
        self.assertIn("ThinkPad T14 Gen 3", models)

    def test_02_passive_dna_learning_during_driver_resolve(self):
        cached_hwid = r"PCI\VEN_10EC&DEV_8168"
        self.driver_cache.save_cache(
            cached_hwid,
            b"MSCF" + b"\x01" * 512,
            {"title": "Realtek PCIe GbE", "version": "1168.10", "date_str": "01/01/2026"},
        )

        code, resp = self._http_json("/api/drivers/resolve", {
            "device_name": "Realtek Ethernet",
            "manufacturer": "HP",
            "model": "HP EliteBook 840 G8",
            "hwids": [cached_hwid],
        })
        self.assertEqual(code, 200)
        self.assertEqual(resp["results"][0]["status"], "ready")

        prof = load_model_profile("HP EliteBook 840 G8")
        self.assertIn(cached_hwid, prof["known_hwids"])

    def test_03_predictive_prewarm_and_status(self):
        # Validacija praznog zahteva -> 400
        code_err, _ = self._http_json("/api/predictive/prewarm", {"manufacturer": "Dell"})
        self.assertEqual(code_err, 400)

        # Priprema: jedan HWID već u kešu, drugi nije u kešu (stub scraper ga preuzima za model DNA)
        pre_cached_hwid = r"PCI\VEN_8086&DEV_46A6"
        missing_hwid = r"PCI\VEN_8086&DEV_9999"
        self.driver_cache.save_cache(
            pre_cached_hwid,
            b"MSCF" + b"\x02" * 512,
            {"title": "Intel Iris Xe Graphics", "version": "31.0.101.5186", "date_str": "02/01/2026"},
        )
        update_model_dna("Dell Latitude 5430", [pre_cached_hwid, missing_hwid], manufacturer="Dell", os_build="26100")

        # J3K9L22 je poznat Dell Service Tag u OFFLINE_WARRANTY_CATALOG
        code, resp = self._http_json("/api/predictive/prewarm", {
            "manufacturer": "Dell",
            "model": "Dell Latitude 5430",
            "serial_number": "J3K9L22",
            "os_build": "24H2",
        })
        self.assertEqual(code, 200)
        self.assertEqual(resp["status"], "ok")
        self.assertEqual(resp["os_build"], "26100")
        self.assertTrue(resp["warranty_cached"])
        self.assertTrue(resp["bom_cached"])
        self.assertTrue(resp["updates_ready"])
        self.assertGreaterEqual(resp["updates_available_count"], 1)
        self.assertEqual(resp["drivers_total_hwids"], 2)
        self.assertEqual(resp["drivers_cached_hits"], 1)
        self.assertEqual(resp["drivers_downloaded"], 1)
        self.assertEqual(resp["drivers_missing"], 0)
        self.assertGreaterEqual(resp["readiness_score_pct"], 80)

        # Proveri da je missing_hwid sada zaista u lokalnom kešu
        self.assertIsNotNone(self.driver_cache.get_cached(missing_hwid))

        # GET /api/predictive/status
        code_st, st_resp = self._http_json("/api/predictive/status")
        self.assertEqual(code_st, 200)
        self.assertGreaterEqual(st_resp["total_jobs"], 1)
        self.assertTrue(st_resp["updates_cache"]["available"])

    def test_04_benchmark_report_ingestion_markdown_and_fleet_summary(self):
        # 1. Pošalji uspešan izveštaj sa benchmark objektom
        report_ok = {
            "device_id": "TK-LAPTOP-BENCH01",
            "computer_name": "LAPTOP-BENCH01",
            "serial_number": "BENCH01",
            "chassis": "Laptop",
            "status": "COMPLETED",
            "handover_ready": True,
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 620,
            "hw": {"manufacturer": "Lenovo", "model": "ThinkPad T14 Gen 3"},
            "apps": {"Chrome": "OK", "Office": "OK"},
            "benchmark": {
                "schema": 1,
                "phase_durations_sec": {
                    "Phase1": 15,
                    "Phase2": 220,
                    "Phase3": 45,
                    "Phase4": 280,
                    "Phase5": 35,
                    "Phase6": 25,
                },
                "total_phase_duration_sec": 620,
                "cache_hits": 9,
                "cache_misses": 1,
                "driver_hits": 6,
                "driver_misses": 1,
                "update_hits": 3,
                "update_misses": 0,
                "cache_hit_ratio_pct": 90.0,
                "byte_hit_ratio_pct": 95.0,
                "lan_bytes": 950 * 1024 * 1024,
                "wan_bytes": 50 * 1024 * 1024,
                "lan_throughput_mbps": 820.5,
                "wan_throughput_mbps": 95.0,
            },
        }
        code1, _ = self._http_json("/api/devices/TK-LAPTOP-BENCH01/report", report_ok)
        self.assertEqual(code1, 200)

        # Proveri Markdown izveštaj
        md_text = generate_markdown_report(report_ok)
        self.assertIn("WIS Benchmark & Performanse", md_text)
        self.assertIn("90.0%", md_text)
        self.assertIn("820.5 Mbps", md_text)
        self.assertIn("`Phase4` | 280s", md_text)

        # 2. Pošalji izveštaj sa NEEDS_ATTENTION (termalno pregrevanje -> nije uspešan first-pass)
        report_fail = {
            "device_id": "TK-DESKTOP-BENCH02",
            "computer_name": "DESKTOP-BENCH02",
            "serial_number": "BENCH02",
            "chassis": "Desktop",
            "status": "NEEDS_ATTENTION",
            "handover_ready": False,
            "diagnostics_status": "THERMAL_CRITICAL",
            "max_cpu_temp_c": 97.5,
            "cooling_verified": False,
            "manual_check_required": True,
            "duration_seconds": 380,
            "hw": {"manufacturer": "HP", "model": "EliteDesk 800 G6"},
            "benchmark": {
                "schema": 1,
                "phase_durations_sec": {
                    "Phase1": 10,
                    "Phase2": 140,
                    "Phase3": 30,
                    "Phase4": 150,
                    "Phase5": 30,
                    "Phase6": 20,
                },
                "total_phase_duration_sec": 380,
                "cache_hits": 5,
                "cache_misses": 0,
                "lan_bytes": 400 * 1024 * 1024,
                "wan_bytes": 0,
                "lan_throughput_mbps": 900.0,
                "wan_throughput_mbps": 0.0,
            },
        }
        code2, _ = self._http_json("/api/devices/TK-DESKTOP-BENCH02/report", report_fail)
        self.assertEqual(code2, 200)

        # 3. GET /api/benchmark/summary
        code_bm, bm_summary = self._http_json("/api/benchmark/summary")
        self.assertEqual(code_bm, 200)
        self.assertEqual(bm_summary["status"], "ok")
        self.assertEqual(bm_summary["total_installations"], 2)
        self.assertEqual(bm_summary["successful_installations"], 1)
        self.assertEqual(bm_summary["success_rate_pct"], 50.0)
        self.assertEqual(bm_summary["avg_duration_seconds"], 500.0)
        self.assertEqual(bm_summary["p50_duration_seconds"], 500.0)
        self.assertEqual(bm_summary["bottleneck_phase"], "Phase4")
        self.assertEqual(bm_summary["phase_averages_sec"]["Phase4"], 215.0)

        cm = bm_summary["cache_metrics"]
        self.assertEqual(cm["total_hits"], 14)
        self.assertEqual(cm["total_misses"], 1)
        self.assertAlmostEqual(cm["cache_hit_ratio_pct"], 93.3, places=1)
        self.assertGreater(cm["saved_wan_mb"], 1300.0)
        self.assertEqual(cm["avg_lan_throughput_mbps"], 860.25)

        # Alias ruta /api/benchmark/stats
        code_stats, stats_summary = self._http_json("/api/benchmark/stats")
        self.assertEqual(code_stats, 200)
        self.assertEqual(stats_summary["total_installations"], 2)

        # Prazan izveštaj za extract_device_benchmark ne sme pasti
        empty_bm = extract_device_benchmark({})
        self.assertFalse(empty_bm["has_benchmark"])
        self.assertEqual(empty_bm["total_duration_sec"], 0)

    def test_05_wis_task_235_model_aliases_and_duration_fallback(self):
        # 1. Spajanje MTM i komercijalnog naziva preko model_aliases
        code, resp = self._http_json("/api/model-profile/dna", {
            "model": "20W0009HMH",
            "manufacturer": "LENOVO",
            "os_build": "26200",
            "model_aliases": ["20W0009HMH", "Lenovo ThinkPad T14 Gen 2i", "ThinkPad T14 Gen 2i"],
            "hwids": [
                r"INTELAUDIO\FUNC_01&VEN_10EC&DEV_0257&SUBSYS_17AA22C9",
                r"PCI\VEN_8086&DEV_9A49&SUBSYS_22C917AA",
            ],
        })
        self.assertEqual(code, 200)
        self.assertEqual(len(resp["profile"]["known_hwids"]), 2)

        mkt_prof = load_model_profile("Lenovo ThinkPad T14 Gen 2i")
        self.assertEqual(mkt_prof["manufacturer"], "LENOVO")
        self.assertEqual(len(mkt_prof["known_hwids"]), 2)
        self.assertIn("20W0009HMH", mkt_prof.get("aliases", []))

        # 2. Report sa duration_seconds=0 koristi benchmark.total_phase_duration_sec fallback
        code_rep, _ = self._http_json("/api/devices/TK-LAPTOP-ALIAS01/report", {
            "device_id": "TK-LAPTOP-ALIAS01",
            "computer_name": "DESKTOP-ALIAS01",
            "serial_number": "ALIAS01",
            "chassis": "Laptop",
            "status": "COMPLETED",
            "duration_seconds": 0,
            "duration_formatted": "0m 00s",
            "hw": {"manufacturer": "LENOVO", "model": "Lenovo ThinkPad T14 Gen 2i"},
            "benchmark": {
                "phase_durations_sec": {"Phase1": 5, "Phase2": 120, "Phase3": 30, "Phase4": 180, "Phase5": 60, "Phase6": 15},
                "total_phase_duration_sec": 410,
            },
        })
        self.assertEqual(code_rep, 200)
        rep_files = [fn for fn in os.listdir(self.config.reports_dir) if "TK-LAPTOP-ALIAS01" in fn and fn.endswith(".json")]
        self.assertEqual(len(rep_files), 1)
        with open(os.path.join(self.config.reports_dir, rep_files[0]), "r", encoding="utf-8") as rf:
            rep_saved = json.load(rf)
        self.assertEqual(rep_saved["duration_seconds"], 410)
        self.assertEqual(rep_saved["duration_formatted"], "6m 50s")

    def test_06_wis_task_236_report_merge_and_duration_str_and_mtm_normalization(self):
        # Parcijalni report_DESKTOP-*.json sa kasnijim timestampom ne sme prebrisati hw/benchmark iz TK-* izveštaja
        isolated_reports = os.path.join(self.test_dir, "isolated_reports_236")
        os.makedirs(isolated_reports, exist_ok=True)

        full_rec = {
            "device_id": "TK-LAPTOP-MERGE01",
            "computer_name": "DESKTOP-MERGE01",
            "serial_number": "MERGE01",
            "status": "COMPLETED",
            "duration_seconds": 0,
            "hw": {"manufacturer": "HP", "model": "HP EliteBook 840 14 inch G10 Notebook PC"},
            "apps": {"Chrome": "OK"},
            "benchmark": {
                "phase_durations_sec": {"Phase1": 5, "Phase2": 300, "Phase3": 20, "Phase4": 250, "Phase5": 30, "Phase6": 25},
                "total_phase_duration_sec": 630,
                "cache_hits": 2,
                "cache_misses": 0,
            },
            "timestamp": "2026-11-16T11:52:46+01:00",
        }
        partial_newer = {
            "event": "COMPLETED",
            "device_id": "TK-LAPTOP-MERGE01",
            "computer_name": "DESKTOP-MERGE01",
            "serial": "MERGE01",
            "status": "COMPLETED",
            "stress_status": "PASS",
            "cooling_verified": True,
            "timestamp": "2026-11-16T12:52:46+01:00",
        }
        legacy_no_did = {
            "event": "COMPLETED",
            "computer_name": "DESKTOP-LEGACY02",
            "status": "SUCCESS",
            "duration": "00:11:19",
            "timestamp": "2026-11-09T18:21:15+01:00",
        }
        mtm_rec = {
            "device_id": "TK-LAPTOP-MTM03",
            "computer_name": "DESKTOP-MTM03",
            "status": "COMPLETED",
            "duration_seconds": 500,
            "hw": {"manufacturer": "LENOVO", "model": "21BYS0211G"},
            "timestamp": "2026-11-16T17:45:00+01:00",
        }
        with open(os.path.join(isolated_reports, "20261116_115246_TK-LAPTOP-MERGE01.json"), "w", encoding="utf-8") as f:
            json.dump(full_rec, f)
        with open(os.path.join(isolated_reports, "report_DESKTOP-MERGE01_20261116_125246.json"), "w", encoding="utf-8") as f:
            json.dump(partial_newer, f)
        with open(os.path.join(isolated_reports, "report_DESKTOP-LEGACY02_20261109_182115.json"), "w", encoding="utf-8") as f:
            json.dump(legacy_no_did, f)
        with open(os.path.join(isolated_reports, "20261116_174500_TK-LAPTOP-MTM03.json"), "w", encoding="utf-8") as f:
            json.dump(mtm_rec, f)

        summary = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=isolated_reports)
        self.assertEqual(summary["total_installations"], 3)
        by_did = {r["device_id"]: r for r in summary["recent_runs"]}
        # Merge sačuvao model i trajanje uprkos kasnijem parcijalnom reportu + uklonio duplo HP HP
        self.assertEqual(by_did["TK-LAPTOP-MERGE01"]["model"], "HP EliteBook 840 14 inch G10 Notebook PC")
        self.assertEqual(by_did["TK-LAPTOP-MERGE01"]["duration_seconds"], 630)
        # Legacy HH:MM:SS parsiran u 679s
        self.assertEqual(by_did["DESKTOP-LEGACY02"]["duration_seconds"], 679)
        # MTM 21BYS0211G normalizovan u LENOVO ThinkPad X13s Gen 1
        self.assertEqual(by_did["TK-LAPTOP-MTM03"]["model"], "LENOVO ThinkPad X13s Gen 1")

    def test_07_wis_task_247_telemetry_and_benchmark_fixes(self):
        """Proverava WIS-TASK-247 ispravke: kineski apps_summary, hwtest merge, NEEDS_ATTENTION status i cleanup DESKTOP-* duplikata."""
        from servishub.benchmark import _merge_report_records
        from servishub.telemetry import _normalize_apps_summary

        # 1. Normalizacija kineskog apps_summary stringa u srpski zapis
        cn = "已安装 8 / 8 • 进行中 0 • 已跳过 0 • 错误 0"
        self.assertEqual(
            _normalize_apps_summary(cn),
            "8 od 8 instalirano • 0 u toku • 0 preskočeno • 0 grešaka",
        )
        self.assertEqual(
            _normalize_apps_summary("8 od 8 instalirano • 0 u toku • 0 preskočeno • 0 grešaka"),
            "8 od 8 instalirano • 0 u toku • 0 preskočeno • 0 grešaka",
        )

        # 2. _merge_report_records cuva hwtest ako noviji zapis ima prazan ili None hwtest
        _, merged = _merge_report_records(
            (1000.0, {"device_id": "TK-LAPTOP-HW01", "hwtest": {"passed": True, "score": 98}, "timestamp": "2026-11-18T10:00:00"}),
            1005.0,
            {"device_id": "TK-LAPTOP-HW01", "hwtest": None, "timestamp": "2026-11-18T10:05:00"},
        )
        self.assertEqual(merged.get("hwtest"), {"passed": True, "score": 98})

        # 3. FleetStore.enrich_report_payload cuva hwtest/benchmark iz memorije i record_lifecycle_complete cisti DESKTOP-* duplikat
        isolated_store = FleetStore(reports_dir=os.path.join(self.test_dir, "reports_247"))
        isolated_store.fleet_devices["DESKTOP-PREOEM1"] = {
            "computer_name": "DESKTOP-PREOEM1",
            "serial_number": "SN-DUP-247",
            "started_at": "2026-11-18T09:00:00+01:00",
            "autopilot": {"status": "OK"},
            "hwtest": {"passed": True, "cpu_temp_max": 72},
            "benchmark": {"phase_durations_sec": {"Phase2": 120}, "total_phase_duration_sec": 300},
        }
        isolated_store.fleet_devices["TK-LAPTOP-KEEP"] = {
            "computer_name": "TK-LAPTOP-KEEP",
            "serial_number": "SN-OTHER",
        }
        canon_id = isolated_store.record_lifecycle_complete(
            {
                "computer_name": "TK-LAPTOP-DUP247",
                "serial_number": "SN-DUP-247",
                "status": "COMPLETED",
                "apps_summary": cn,
                "duration_seconds": 300,
                "hwtest": {"passed": True, "cpu_temp_max": 72},
                "benchmark": {"total_phase_duration_sec": 300},
            },
            "192.168.170.99",
        )
        self.assertEqual(canon_id, "TK-LAPTOP-DUP247")
        self.assertNotIn("DESKTOP-PREOEM1", isolated_store.fleet_devices)
        self.assertIn("TK-LAPTOP-KEEP", isolated_store.fleet_devices)
        canon_rec = isolated_store.fleet_devices["TK-LAPTOP-DUP247"]
        self.assertEqual(canon_rec.get("started_at"), "2026-11-18T09:00:00+01:00")
        self.assertEqual(canon_rec.get("autopilot"), {"status": "OK"})
        self.assertIn("8 od 8 instalirano", canon_rec.get("latest_report", {}).get("apps_summary", ""))

        # 4. recent_runs.status se uskladjuje na NEEDS_ATTENTION kada success == False a status glasi COMPLETED
        isolated_reports = os.path.join(self.test_dir, "isolated_reports_247")
        os.makedirs(isolated_reports, exist_ok=True)
        with open(os.path.join(isolated_reports, "report_TK-FAIL-247.json"), "w", encoding="utf-8") as f:
            json.dump(
                {
                    "device_id": "TK-FAIL-247",
                    "computer_name": "TK-FAIL-247",
                    "status": "COMPLETED",
                    "deployment_status": "NEEDS_ATTENTION",
                    "handover_ready": False,
                    "duration_seconds": 400,
                    "timestamp": "2026-11-18T11:00:00+01:00",
                },
                f,
            )
        summary = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=isolated_reports)
        run_map = {r["device_id"]: r for r in summary["recent_runs"]}
        self.assertFalse(run_map["TK-FAIL-247"]["success"])
        self.assertEqual(run_map["TK-FAIL-247"]["status"], "NEEDS_ATTENTION")


if __name__ == "__main__":
    unittest.main()
