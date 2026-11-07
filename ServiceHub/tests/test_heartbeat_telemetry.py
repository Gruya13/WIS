"""
tests.test_heartbeat_telemetry
==============================
Testovi za proširenu senzorsku telemetriju (CPU, RAM, Termika, SMART)
u FleetStore-u i REST API /api/heartbeat i /api/devices rutama.
Striktna No-Mock politika: koristi stvarne instance i realan HTTP server.
"""

import os
import json
import shutil
import tempfile
import threading
import urllib.request
import unittest
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.handlers import ServisHubHandler


class TestHeartbeatTelemetry(unittest.TestCase):
    """Testovi senzorske telemetrije u FleetStore-u i HTTP integracije."""

    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp()
        cls.config = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=cls.test_dir,
            storage_dir=os.path.join(cls.test_dir, "storage"),
            logs_dir=os.path.join(cls.test_dir, "logs"),
        )
        cls.fleet_store = FleetStore(
            live_logs_dir=cls.config.live_logs_dir,
            reports_dir=cls.config.reports_dir,
        )

        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.config = cls.config

        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        cls.port = cls.httpd.server_address[1]
        cls.base_url = f"http://127.0.0.1:{cls.port}"

        cls.server_thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def _http_request(self, method: str, path: str, data: dict = None):
        url = f"{self.base_url}{path}"
        body = json.dumps(data).encode("utf-8") if data is not None else None
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                raw = resp.read()
                return resp.status, json.loads(raw.decode("utf-8"))
        except urllib.error.HTTPError as err:
            raw = err.read()
            try:
                return err.code, json.loads(raw.decode("utf-8"))
            except Exception:
                return err.code, raw.decode("utf-8")

    def test_01_fleet_store_sensor_parsing_and_types(self):
        """Provera direktnog parsiranja i tipova senzorskih polja u FleetStore."""
        fs = FleetStore()
        payload = {
            "computer_name": "TK-SENZOR-01",
            "cpu_percent": 34.5,
            "cpu_temp": 58,
            "ram_used": "5.4 GB",
            "ram_total": "16.0 GB",
            "ram_percent": 33,
            "smart": {
                "SsdHealth": 98,
                "Category": "ČISTO",
                "Temperature": 41,
            },
        }

        dev_id = fs.update_fleet_heartbeat(payload, "192.168.1.100")
        self.assertEqual(dev_id, "TK-SENZOR-01")

        summary = fs.get_devices_summary()
        self.assertIn("TK-SENZOR-01", summary)
        dev = summary["TK-SENZOR-01"]

        self.assertEqual(dev["cpu_percent"], 34.5)
        self.assertEqual(dev["cpu_temp"], 58)
        self.assertEqual(dev["ram_used"], "5.4 GB")
        self.assertEqual(dev["ram_total"], "16.0 GB")
        self.assertEqual(dev["ram_percent"], 33)
        self.assertIsInstance(dev["smart"], dict)
        self.assertEqual(dev["smart"]["SsdHealth"], 98)
        self.assertEqual(dev["smart"]["ssd_health"], 98)
        self.assertEqual(dev["smart"]["Category"], "ČISTO")
        self.assertEqual(dev["smart"]["Temperature"], 41)

    def test_02_sensor_string_and_nested_normalization(self):
        """Provera parsiranja tekstualnih procenata/temperatura i ugnježdenih polja."""
        fs = FleetStore()
        payload = {
            "computer_name": "TK-SENZOR-02",
            "sensors": {
                "cpu_percent": "45%",
                "cpu_temp": "65 °C",
                "ram_percent": "70%",
                "smart": {
                    "ssd_health": 88,
                    "category": "WARNING",
                    "temperature": 48,
                },
            },
            "ram_used": 7.2,
            "ram_total": 16.0,
        }

        fs.record_heartbeat(payload, "192.168.1.101")
        summary = fs.get_devices_summary()
        dev = summary["TK-SENZOR-02"]

        self.assertEqual(dev["cpu_percent"], 45)
        self.assertEqual(dev["cpu_temp"], 65)
        self.assertEqual(dev["ram_percent"], 70)
        self.assertEqual(dev["ram_used"], 7.2)
        self.assertEqual(dev["ram_total"], 16.0)
        self.assertEqual(dev["smart"]["SsdHealth"], 88)
        self.assertEqual(dev["smart"]["Category"], "WARNING")
        self.assertEqual(dev["smart"]["Temperature"], 48)

    def test_03_safe_handling_missing_and_none_fields(self):
        """Provera otpornosti: None ili nedostajuća polja ne gaze postojeće podatke niti dižu izuzetak."""
        fs = FleetStore()
        # 1. Početni puls sa temperaturom
        fs.record_heartbeat({
            "computer_name": "TK-SENZOR-03",
            "cpu_temp": 52,
            "cpu_percent": 20,
        }, "192.168.1.102")

        # 2. Sledeći puls šalje cpu_temp=None i ram_percent=None
        fs.record_heartbeat({
            "computer_name": "TK-SENZOR-03",
            "cpu_temp": None,
            "cpu_percent": 25,
            "ram_percent": None,
        }, "192.168.1.102")

        summary = fs.get_devices_summary()
        dev = summary["TK-SENZOR-03"]

        # cpu_temp ostaje sačuvan (nije pregažen sa None)
        self.assertEqual(dev["cpu_temp"], 52)
        # cpu_percent je ažuriran na 25
        self.assertEqual(dev["cpu_percent"], 25)
        # ram_percent nikada nije postojao, pa nije prisutan u summary rečniku
        self.assertNotIn("ram_percent", dev)

    def test_04_http_heartbeat_and_devices_integration(self):
        """End-to-end HTTP test: POST /api/heartbeat i GET /api/devices."""
        hb_payload = {
            "computer_name": "TK-RIG-01",
            "phase": 3,
            "worker_progress": 60,
            "cpu_percent": 42.1,
            "cpu_temp": 54,
            "ram_used": "6.1 GB",
            "ram_total": "32.0 GB",
            "ram_percent": 19,
            "smart": {
                "SsdHealth": 100,
                "Category": "ČISTO",
                "Temperature": 35,
            },
        }

        # 1. Slanje pulsa
        status, resp = self._http_request("POST", "/api/heartbeat", hb_payload)
        self.assertEqual(status, 200)
        self.assertEqual(resp.get("status"), "ok")
        self.assertEqual(resp.get("device_id"), "TK-RIG-01")

        # 2. Preuzimanje sa /api/devices
        status, devs = self._http_request("GET", "/api/devices")
        self.assertEqual(status, 200)
        self.assertIn("TK-RIG-01", devs)

        rig = devs["TK-RIG-01"]
        self.assertEqual(rig["computer_name"], "TK-RIG-01")
        self.assertEqual(rig["cpu_percent"], 42.1)
        self.assertEqual(rig["cpu_temp"], 54)
        self.assertEqual(rig["ram_used"], "6.1 GB")
        self.assertEqual(rig["ram_total"], "32.0 GB")
        self.assertEqual(rig["ram_percent"], 19)
        self.assertEqual(rig["smart"]["SsdHealth"], 100)
        self.assertEqual(rig["smart"]["Category"], "ČISTO")
        self.assertEqual(rig["smart"]["Temperature"], 35)

    def test_05_http_heartbeat_invalid_body_handling(self):
        """Provera da nevalidno telo na /api/heartbeat vraća 400."""
        url = f"{self.base_url}/api/heartbeat"
        req = urllib.request.Request(url, data=b"[1, 2, 3]", method="POST")
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                self.assertEqual(resp.status, 400)
        except urllib.error.HTTPError as err:
            self.assertEqual(err.code, 400)

    def test_06_extreme_sensor_values(self):
        """Granične i ekstremne vrednosti (negativne temp, >100°C, 0% i 100% CPU/RAM)."""
        fs = FleetStore()

        # Uređaj sa sub-zero temperaturom i 0% CPU
        dev_subzero = fs.record_heartbeat({
            "computer_name": "TK-COLD-RIG",
            "cpu_temp": "-15°C",
            "cpu_percent": "0%",
            "ram_percent": 0,
            "ram_used": "0.8 GB",
            "ram_total": "16 GB",
        }, "10.0.0.1")
        self.assertEqual(dev_subzero, "TK-COLD-RIG")

        # Uređaj sa ekstremno visokom temperaturom (>100°C) i 100% CPU/RAM
        dev_hot = fs.record_heartbeat({
            "computer_name": "TK-HOT-RIG",
            "cpu_temp": "115.5 °c",
            "cpu_percent": 100.0,
            "ram_percent": "100%",
            "ram_used": 64.0,
            "ram_total": 64.0,
        }, "10.0.0.2")
        self.assertEqual(dev_hot, "TK-HOT-RIG")

        summary = fs.get_devices_summary()
        cold = summary["TK-COLD-RIG"]
        self.assertEqual(cold["cpu_temp"], -15)
        self.assertEqual(cold["cpu_percent"], 0)
        self.assertEqual(cold["ram_percent"], 0)
        self.assertEqual(cold["ram_used"], "0.8 GB")

        hot = summary["TK-HOT-RIG"]
        self.assertEqual(hot["cpu_temp"], 115.5)
        self.assertEqual(hot["cpu_percent"], 100)
        self.assertEqual(hot["ram_percent"], 100)
        self.assertEqual(hot["ram_used"], 64.0)

    def test_07_malformed_and_unexpected_types_resilience(self):
        """Otpornost na neočekivane tipove: boolean, prazne/whitespace stringove, corrupt objekte."""
        fs = FleetStore()

        # Početni validni heartbeat
        fs.record_heartbeat({
            "computer_name": "TK-ROBUST-01",
            "cpu_temp": 48,
            "cpu_percent": 15,
            "ram_used": "4 GB",
            "ram_total": "16 GB",
            "ram_percent": 25,
            "smart": {"SsdHealth": 99, "Category": "ČISTO", "Temperature": 36},
        }, "192.168.1.110")

        # Sledeći heartbeat sa malformed/neočekivanim tipovima
        fs.record_heartbeat({
            "computer_name": "TK-ROBUST-01",
            "cpu_temp": True,           # Boolean umesto broja - mora biti odbačen
            "cpu_percent": False,        # Boolean umesto broja - mora biti odbačen
            "ram_used": "   ",          # Prazan/whitespace string - ne sme pregaziti "4 GB"
            "ram_total": "",            # Prazan string - ne sme pregaziti "16 GB"
            "ram_percent": "N/A",       # Nevalidan string procenat - ne sme pregaziti 25
            "smart": "SVE_OK",          # String umesto dict-a - ne sme pregaziti smart dict
        }, "192.168.1.110")

        summary = fs.get_devices_summary()
        dev = summary["TK-ROBUST-01"]

        # Validni podaci moraju ostati očuvani i netaknuti
        self.assertEqual(dev["cpu_temp"], 48)
        self.assertEqual(dev["cpu_percent"], 15)
        self.assertEqual(dev["ram_used"], "4 GB")
        self.assertEqual(dev["ram_total"], "16 GB")
        self.assertEqual(dev["ram_percent"], 25)
        self.assertIsInstance(dev["smart"], dict)
        self.assertEqual(dev["smart"]["SsdHealth"], 99)

    def test_08_incremental_heartbeat_sensor_preservation(self):
        """Očuvanje postojećih senzorskih podataka kada heartbeat pošalje samo delimična polja."""
        fs = FleetStore()

        # Korak 1: Pun početni puls sa kompletnom telemetrijom
        fs.record_heartbeat({
            "computer_name": "TK-PERSIST-01",
            "phase": 2,
            "worker_progress": 20,
            "cpu_temp": 58,
            "cpu_percent": 30,
            "ram_used": "6.5 GB",
            "ram_total": "16.0 GB",
            "ram_percent": 40,
            "smart": {"SsdHealth": 97, "Category": "ČISTO", "Temperature": 40},
        }, "192.168.1.120")

        # Korak 2: Klijent šalje heartbeat samo sa progresom i fazom (nema senzora)
        fs.record_heartbeat({
            "computer_name": "TK-PERSIST-01",
            "phase": 3,
            "worker_progress": 55,
        }, "192.168.1.120")

        summary = fs.get_devices_summary()
        dev = summary["TK-PERSIST-01"]
        self.assertEqual(dev["phase"], 3)
        self.assertEqual(dev["worker_progress"], 55)
        self.assertEqual(dev["cpu_temp"], 58)
        self.assertEqual(dev["cpu_percent"], 30)
        self.assertEqual(dev["ram_used"], "6.5 GB")
        self.assertEqual(dev["ram_total"], "16.0 GB")
        self.assertEqual(dev["ram_percent"], 40)
        self.assertEqual(dev["smart"]["SsdHealth"], 97)

        # Korak 3: Klijent ažurira samo cpu_percent i worker_progress (ostalo ne šalje)
        fs.record_heartbeat({
            "computer_name": "TK-PERSIST-01",
            "worker_progress": 70,
            "cpu_percent": 65,
        }, "192.168.1.120")

        summary = fs.get_devices_summary()
        dev = summary["TK-PERSIST-01"]
        self.assertEqual(dev["worker_progress"], 70)
        self.assertEqual(dev["cpu_percent"], 65)  # Ažurirano na novu vrednost
        self.assertEqual(dev["cpu_temp"], 58)     # Očuvana prethodna temperatura
        self.assertEqual(dev["ram_used"], "6.5 GB")
        self.assertEqual(dev["ram_total"], "16.0 GB")
        self.assertEqual(dev["ram_percent"], 40)

        # Korak 4: Klijent šalje eksplicitne null vrednosti za senzore
        fs.record_heartbeat({
            "computer_name": "TK-PERSIST-01",
            "worker_progress": 85,
            "cpu_temp": None,
            "ram_percent": None,
        }, "192.168.1.120")

        summary = fs.get_devices_summary()
        dev = summary["TK-PERSIST-01"]
        self.assertEqual(dev["worker_progress"], 85)
        self.assertEqual(dev["cpu_temp"], 58)     # Nije pregaženo sa None
        self.assertEqual(dev["ram_percent"], 40)  # Nije pregaženo sa None

    def test_09_http_end_to_end_extreme_and_persistence(self):
        """End-to-end HTTP test: granične vrednosti i očuvanje stanja preko REST poziva."""
        # 1. Početni HTTP puls sa ekstremnim i specifičnim vrednostima
        payload_1 = {
            "computer_name": "TK-HTTP-RIG-99",
            "phase": 1,
            "worker_progress": 10,
            "cpu_temp": "-8°C",
            "cpu_percent": "0%",
            "ram_used": "2.1 GB",
            "ram_total": "32.0 GB",
            "ram_percent": 6,
            "smart": {"SsdHealth": 100, "Category": "ČISTO", "Temperature": 28},
        }
        status, resp = self._http_request("POST", "/api/heartbeat", payload_1)
        self.assertEqual(status, 200)
        self.assertEqual(resp.get("status"), "ok")

        # Provera na GET /api/devices
        status, devs = self._http_request("GET", "/api/devices")
        self.assertEqual(status, 200)
        rig = devs["TK-HTTP-RIG-99"]
        self.assertEqual(rig["cpu_temp"], -8)
        self.assertEqual(rig["cpu_percent"], 0)
        self.assertEqual(rig["ram_percent"], 6)
        self.assertEqual(rig["ram_used"], "2.1 GB")
        self.assertEqual(rig["smart"]["Temperature"], 28)

        # 2. Sledeći HTTP puls šalje samo radni progres (bez senzorskih polja)
        payload_2 = {
            "computer_name": "TK-HTTP-RIG-99",
            "phase": 3,
            "worker_progress": 65,
        }
        status, resp = self._http_request("POST", "/api/heartbeat", payload_2)
        self.assertEqual(status, 200)

        # Provera da su senzori preko HTTP-a i dalje sačuvani
        status, devs = self._http_request("GET", "/api/devices")
        self.assertEqual(status, 200)
        rig = devs["TK-HTTP-RIG-99"]
        self.assertEqual(rig["phase"], 3)
        self.assertEqual(rig["worker_progress"], 65)
        self.assertEqual(rig["cpu_temp"], -8)
        self.assertEqual(rig["cpu_percent"], 0)
        self.assertEqual(rig["ram_used"], "2.1 GB")
        self.assertEqual(rig["smart"]["Temperature"], 28)

        # 3. HTTP puls sa praznim i null poljima ne narušava stanje
        payload_3 = {
            "computer_name": "TK-HTTP-RIG-99",
            "worker_progress": 80,
            "cpu_temp": "",
            "ram_used": None,
        }
        status, resp = self._http_request("POST", "/api/heartbeat", payload_3)
        self.assertEqual(status, 200)

        status, devs = self._http_request("GET", "/api/devices")
        self.assertEqual(status, 200)
        rig = devs["TK-HTTP-RIG-99"]
        self.assertEqual(rig["worker_progress"], 80)
        self.assertEqual(rig["cpu_temp"], -8)
        self.assertEqual(rig["ram_used"], "2.1 GB")

    def test_10_historical_reports_and_agy_session_telemetry(self):
        """Učitavanje i normalizacija telemetrije iz istorijskih izveštaja na disku."""
        hist_dir = tempfile.mkdtemp()
        try:
            # 1. Kreiranje istorijskog report.json sa formatiranim senzorima
            report_data = {
                "computer_name": "TK-HIST-DEV-01",
                "timestamp": "2026-03-01T12:00:00",
                "status": "COMPLETED",
                "cpu_percent": "35%",
                "cpu_temp": "59 °C",
                "ram_used": "7.5 GB",
                "ram_total": "16 GB",
                "ram_percent": "46%",
                "smart": {
                    "SsdHealth": 94,
                    "Category": "ČISTO",
                    "Temperature": 41,
                },
            }
            with open(os.path.join(hist_dir, "report_TK-HIST-DEV-01_20260301.json"), "w", encoding="utf-8") as f:
                json.dump(report_data, f)

            # 2. Kreiranje poddirektorijuma sa DEVICE_INFO.json (AGY sesija)
            agy_subdir = os.path.join(hist_dir, "TK-HIST-DEV-02")
            os.makedirs(agy_subdir, exist_ok=True)
            agy_data = {
                "computer_name": "TK-HIST-DEV-02",
                "status_label": "Servisiran (AGY)",
                "sensors": {
                    "cpu_percent": 22.4,
                    "cpu_temp": 51,
                    "ram_percent": 30,
                },
                "ram_used": "4.8 GB",
                "ram_total": "16 GB",
            }
            with open(os.path.join(agy_subdir, "DEVICE_INFO.json"), "w", encoding="utf-8") as f:
                json.dump(agy_data, f)

            fs = FleetStore(reports_dir=hist_dir)
            fs.load_historical_devices()

            summary = fs.get_devices_summary()
            self.assertIn("TK-HIST-DEV-01", summary)
            dev1 = summary["TK-HIST-DEV-01"]
            self.assertEqual(dev1["cpu_percent"], 35)
            self.assertEqual(dev1["cpu_temp"], 59)
            self.assertEqual(dev1["ram_percent"], 46)
            self.assertEqual(dev1["ram_used"], "7.5 GB")
            self.assertEqual(dev1["smart"]["SsdHealth"], 94)

            self.assertIn("TK-HIST-DEV-02", summary)
            dev2 = summary["TK-HIST-DEV-02"]
            self.assertEqual(dev2["cpu_percent"], 22.4)
            self.assertEqual(dev2["cpu_temp"], 51)
            self.assertEqual(dev2["ram_percent"], 30)
            self.assertEqual(dev2["ram_used"], "4.8 GB")
        finally:
            shutil.rmtree(hist_dir, ignore_errors=True)

    def test_11_session_state_and_lifecycle_sensor_extraction(self):
        """Telemetrija u record_session_state, lifecycle STARTED i lifecycle COMPLETED."""
        fs = FleetStore()

        # 1. record_session_state sa telemetrijom u tab_data
        sess_payload = {
            "computer_name": "TK-SESS-01",
            "phase": 2,
            "progress_percent": 35,
            "tab_data": {
                "cpu_percent": 44,
                "cpu_temp": 56,
                "ram_used": "5.1 GB",
                "ram_total": "16 GB",
                "ram_percent": 32,
                "smart": {"SsdHealth": 98, "Category": "ČISTO", "Temperature": 38},
            },
        }
        fs.record_session_state("sess-xyz", sess_payload, "192.168.1.130")

        summary = fs.get_devices_summary()
        self.assertIn("TK-SESS-01", summary)
        dev = summary["TK-SESS-01"]
        self.assertEqual(dev["cpu_percent"], 44)
        self.assertEqual(dev["cpu_temp"], 56)
        self.assertEqual(dev["ram_used"], "5.1 GB")
        self.assertEqual(dev["ram_percent"], 32)
        self.assertEqual(dev["smart"]["SsdHealth"], 98)

        # 2. Lifecycle STARTED sa senzorima
        fs.record_lifecycle_start({
            "computer_name": "TK-LIFECYCLE-01",
            "cpu_temp": 49,
            "cpu_percent": 18,
            "ram_used": "3.2 GB",
        }, "192.168.1.131")

        summary = fs.get_devices_summary()
        dev_start = summary["TK-LIFECYCLE-01"]
        self.assertEqual(dev_start["status_label"], "Aktivno")
        self.assertEqual(dev_start["cpu_temp"], 49)
        self.assertEqual(dev_start["cpu_percent"], 18)
        self.assertEqual(dev_start["ram_used"], "3.2 GB")

        # 3. Lifecycle COMPLETED sa ažuriranim senzorima
        fs.record_lifecycle_complete({
            "computer_name": "TK-LIFECYCLE-01",
            "cpu_temp": 61,
            "cpu_percent": 8,
            "ram_used": "4.0 GB",
        }, "192.168.1.131")

        summary = fs.get_devices_summary()
        dev_comp = summary["TK-LIFECYCLE-01"]
        self.assertEqual(dev_comp["status_label"], "Zavrseno")
        self.assertEqual(dev_comp["cpu_temp"], 61)
        self.assertEqual(dev_comp["cpu_percent"], 8)
        self.assertEqual(dev_comp["ram_used"], "4.0 GB")


if __name__ == "__main__":
    unittest.main()

