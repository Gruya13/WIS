"""
tests.test_remote_commands
==========================
Integracioni testovi za serverski komandni red u FleetStore-u i REST API
rute za daljinsko upravljanje (Remote BIOS Reboot Backend).

Striktna No-Mock politika:
- Koristi realan ThreadingHTTPServer na dinamičkom portu.
- Koristi realan FleetStore i ServisHubHandler.
- Testira stvarne HTTP zahteve preko urllib.request.
"""

from pathlib import Path
import os
import json
import time
import shutil
import tempfile
import threading
import concurrent.futures
import importlib.util
import urllib.request
import urllib.parse
import urllib.error
import unittest
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.handlers import ServisHubHandler


class TestRemoteCommands(unittest.TestCase):
    """Testovi za komandni red i REST API rute daljinskog upravljanja."""

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

    def setUp(self):
        # Očisti komandni red pre svakog testa
        with self.fleet_store.command_lock:
            self.fleet_store.fleet_commands.clear()

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

    def _raw_http_request(self, method: str, path: str, raw_body: bytes):
        url = f"{self.base_url}{path}"
        req = urllib.request.Request(url, data=raw_body, method=method)
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

    # =========================================================================
    # 1. FleetStore Unit nivo (in-memory komandni red)
    # =========================================================================

    def test_01_fleet_store_enqueue_and_pop(self):
        """Provera direktnih metoda enqueue_command, get_pending_commands i pop_pending_commands."""
        fs = FleetStore()
        cmd = fs.enqueue_command("DEV-01", "reboot_uefi", {"reason": "firmware"})

        self.assertTrue(cmd["command_id"].startswith("cmd_"))
        self.assertEqual(cmd["device_id"], "DEV-01")
        self.assertEqual(cmd["action"], "reboot_uefi")
        self.assertEqual(cmd["params"], {"reason": "firmware"})
        self.assertIsInstance(cmd["created_at"], float)

        # Pregled bez pražnjenja
        pending = fs.get_pending_commands("DEV-01")
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["command_id"], cmd["command_id"])

        # Preuzimanje sa pražnjenjem
        popped = fs.pop_pending_commands("DEV-01")
        self.assertEqual(len(popped), 1)
        self.assertEqual(popped[0]["command_id"], cmd["command_id"])

        # Naredno preuzimanje mora biti prazno
        self.assertEqual(fs.pop_pending_commands("DEV-01"), [])
        self.assertEqual(fs.get_pending_commands("DEV-01"), [])

    def test_02_fleet_store_fifo_ordering(self):
        """Provera FIFO redosleda komandi."""
        fs = FleetStore()
        c1 = fs.enqueue_command("DEV-FIFO", "action_1", {"seq": 1})
        c2 = fs.enqueue_command("DEV-FIFO", "action_2", {"seq": 2})
        c3 = fs.enqueue_command("DEV-FIFO", "action_3", {"seq": 3})

        popped = fs.pop_pending_commands("DEV-FIFO")
        self.assertEqual(len(popped), 3)
        self.assertEqual(popped[0]["action"], "action_1")
        self.assertEqual(popped[1]["action"], "action_2")
        self.assertEqual(popped[2]["action"], "action_3")

    def test_03_fleet_store_max_capacity_protection(self):
        """Provera ograničenja na maksimalno 20 komandi po uređaju."""
        fs = FleetStore()
        for i in range(25):
            fs.enqueue_command("DEV-OVERFLOW", f"action_{i}", {"index": i})

        pending = fs.get_pending_commands("DEV-OVERFLOW")
        self.assertEqual(len(pending), 20)
        # Najstarijih 5 je odbačeno (0..4), ostale su 5..24
        self.assertEqual(pending[0]["action"], "action_5")
        self.assertEqual(pending[-1]["action"], "action_24")

    def test_04_fleet_store_clear_commands(self):
        """Provera pražnjenja reda preko clear_commands."""
        fs = FleetStore()
        fs.enqueue_command("DEV-CLEAR", "action_a")
        fs.enqueue_command("DEV-CLEAR", "action_b")
        self.assertEqual(len(fs.get_pending_commands("DEV-CLEAR")), 2)

        fs.clear_commands("DEV-CLEAR")
        self.assertEqual(len(fs.get_pending_commands("DEV-CLEAR")), 0)
        self.assertEqual(len(fs.pop_pending_commands("DEV-CLEAR")), 0)

    # =========================================================================
    # 2. HTTP REST API integracija (Realan server, bez mock-ova)
    # =========================================================================

    def test_05_http_enqueue_and_get_commands(self):
        """Slanje komande na POST /api/devices/{id}/command i provera GET /api/devices/{id}/commands."""
        post_data = {
            "action": "reboot_uefi",
            "params": {"timeout_sec": 10}
        }
        status, resp = self._http_request("POST", "/api/devices/RIG-ALPHA/command", post_data)
        self.assertEqual(status, 200)
        self.assertEqual(resp.get("status"), "ok")
        self.assertEqual(resp.get("device_id"), "RIG-ALPHA")
        cmd = resp.get("command")
        self.assertIsNotNone(cmd)
        self.assertTrue(cmd["command_id"].startswith("cmd_"))
        self.assertEqual(cmd["action"], "reboot_uefi")
        self.assertEqual(cmd["params"], {"timeout_sec": 10})

        # Provera preko GET /api/devices/RIG-ALPHA/commands
        status, get_resp = self._http_request("GET", "/api/devices/RIG-ALPHA/commands")
        self.assertEqual(status, 200)
        self.assertEqual(get_resp.get("status"), "ok")
        self.assertEqual(get_resp.get("device_id"), "RIG-ALPHA")
        cmds = get_resp.get("commands", [])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0]["command_id"], cmd["command_id"])

    def test_06_http_url_encoded_device_id(self):
        """Provera podrške za URL-enkodovan device_id u putanji."""
        post_data = {"action": "reboot_pxe"}
        status, resp = self._http_request("POST", "/api/devices/TK%20BENCH%2001/command", post_data)
        self.assertEqual(status, 200)
        self.assertEqual(resp.get("status"), "ok")
        self.assertEqual(resp.get("device_id"), "TK BENCH 01")

        status, get_resp = self._http_request("GET", "/api/devices/TK%20BENCH%2001/commands")
        self.assertEqual(status, 200)
        self.assertEqual(get_resp.get("device_id"), "TK BENCH 01")
        self.assertEqual(len(get_resp.get("commands", [])), 1)

    def test_07_http_heartbeat_retrieves_and_clears_command(self):
        """End-to-End: Slanje komande, preuzimanje kroz /api/heartbeat i provera pražnjenja."""
        device_id = "RIG-BETA-02"

        # 1. Enqueue komandu
        cmd_payload = {"action": "reboot_uefi", "params": {"force": True}}
        status, resp = self._http_request("POST", f"/api/devices/{device_id}/command", cmd_payload)
        self.assertEqual(status, 200)
        expected_cmd_id = resp["command"]["command_id"]

        # 2. Prvi heartbeat od radnika: mora dobiti beklogovanu komandu
        hb_payload = {
            "computer_name": device_id,
            "device_id": device_id,
            "phase": 2,
            "worker_progress": 45,
        }
        status, hb_resp = self._http_request("POST", "/api/heartbeat", hb_payload)
        self.assertEqual(status, 200)
        self.assertEqual(hb_resp.get("status"), "ok")
        self.assertIn("timestamp", hb_resp)
        self.assertIn("pending_commands", hb_resp)

        pending = hb_resp["pending_commands"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["command_id"], expected_cmd_id)
        self.assertEqual(pending[0]["action"], "reboot_uefi")
        self.assertEqual(pending[0]["params"], {"force": True})

        # 3. Drugi heartbeat od istog radnika: red mora biti prazan
        status, hb_resp_2 = self._http_request("POST", "/api/heartbeat", hb_payload)
        self.assertEqual(status, 200)
        self.assertEqual(hb_resp_2.get("status"), "ok")
        self.assertEqual(hb_resp_2.get("pending_commands"), [])

        # 4. GET /api/devices/{id}/commands takođe mora vratiti praznu listu
        status, get_resp = self._http_request("GET", f"/api/devices/{device_id}/commands")
        self.assertEqual(status, 200)
        self.assertEqual(get_resp.get("commands"), [])

    def test_08_http_heartbeat_multiple_commands_fifo(self):
        """Provera da klijent kroz heartbeat preuzima više komandi u FIFO redosledu."""
        device_id = "RIG-MULTI-03"

        self._http_request("POST", f"/api/devices/{device_id}/command", {"action": "action_first"})
        self._http_request("POST", f"/api/devices/{device_id}/command", {"action": "action_second"})

        hb_payload = {"computer_name": device_id}
        status, hb_resp = self._http_request("POST", "/api/heartbeat", hb_payload)
        self.assertEqual(status, 200)

        cmds = hb_resp.get("pending_commands", [])
        self.assertEqual(len(cmds), 2)
        self.assertEqual(cmds[0]["action"], "action_first")
        self.assertEqual(cmds[1]["action"], "action_second")

    # =========================================================================
    # 3. Validacija i zaštita od nevalidnih payload-a (HTTP 400)
    # =========================================================================

    def test_09_command_missing_or_invalid_action(self):
        """Provera da nedostajuća ili nevalidna akcija vraća HTTP 400."""
        # Nedostaje 'action'
        status, resp = self._http_request("POST", "/api/devices/DEV-ERR/command", {"params": {}})
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

        # 'action' je prazan string
        status, resp = self._http_request("POST", "/api/devices/DEV-ERR/command", {"action": "   "})
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

        # 'action' nije string (npr. integer ili boolean)
        status, resp = self._http_request("POST", "/api/devices/DEV-ERR/command", {"action": 12345})
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

    def test_10_command_invalid_params_type(self):
        """Provera da nevalidan tip za 'params' vraća HTTP 400."""
        status, resp = self._http_request("POST", "/api/devices/DEV-ERR/command", {
            "action": "reboot_uefi",
            "params": "not-a-dict"
        })
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

    def test_11_command_malformed_json_body(self):
        """Provera da neispravan JSON u telu zahteva vraća HTTP 400."""
        status, resp = self._raw_http_request(
            "POST",
            "/api/devices/DEV-ERR/command",
            b"{ malformed json string, "
        )
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

    def test_12_heartbeat_malformed_json_body(self):
        """Provera da neispravan JSON ili ne-dict na /api/heartbeat vraća HTTP 400."""
        status, resp = self._raw_http_request(
            "POST",
            "/api/heartbeat",
            b"not a valid json"
        )
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

        status, resp = self._raw_http_request(
            "POST",
            "/api/heartbeat",
            b'["array", "not", "dict"]'
        )
        self.assertEqual(status, 400)
        self.assertIn("error", resp)

    # =========================================================================
    # 4. Paralelizam, izolacija redova i granični slučajevi (Faza 3 QA Gate)
    # =========================================================================

    def test_13_concurrent_commands_multi_device_isolation_and_fifo(self):
        """
        Više paralelnih komandi za različite uređaje:
        - Proverava potpunu izolaciju redova između uređaja.
        - Proverava očuvanje striktnog FIFO redosleda unutar svakog uređaja.
        - Izvršava se konkurentno preko ThreadPoolExecutor-a sa realnim HTTP pozivima.
        """
        devices = ["DEV-ALPHA", "DEV-BETA", "DEV-GAMMA", "DEV-DELTA", "DEV-EPSILON"]
        cmds_per_device = 6

        def _send_device_stream(dev_id: str):
            for s in range(cmds_per_device):
                payload = {
                    "action": f"action_seq_{s}",
                    "params": {"device": dev_id, "sequence": s}
                }
                status, resp = self._http_request("POST", f"/api/devices/{dev_id}/command", payload)
                self.assertEqual(status, 200)
                self.assertEqual(resp.get("status"), "ok")
                self.assertEqual(resp.get("device_id"), dev_id)
            return dev_id

        # Pokreni paralelne radnike za svih 5 uređaja konkurentno
        futures = []
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as executor:
            for dev in devices:
                futures.append(executor.submit(_send_device_stream, dev))

            for fut in concurrent.futures.as_completed(futures):
                dev_id = fut.result()
                self.assertIn(dev_id, devices)

        # Verifikuj izolaciju i FIFO redosled za svaki uređaj
        for dev in devices:
            status, get_resp = self._http_request("GET", f"/api/devices/{dev}/commands")
            self.assertEqual(status, 200)
            commands = get_resp.get("commands", [])
            self.assertEqual(len(commands), cmds_per_device)

            # Provera da su sve komande striktno za ovaj uređaj i u FIFO redosledu
            for idx, cmd in enumerate(commands):
                self.assertEqual(cmd["device_id"], dev)
                self.assertEqual(cmd["action"], f"action_seq_{idx}")
                self.assertEqual(cmd["params"]["sequence"], idx)
                self.assertEqual(cmd["params"]["device"], dev)

        # Heartbeat pražnjenje jednog uređaja ne sme uticati na ostale
        hb_status, hb_resp = self._http_request("POST", "/api/heartbeat", {"computer_name": "DEV-ALPHA"})
        self.assertEqual(hb_status, 200)
        self.assertEqual(len(hb_resp.get("pending_commands", [])), cmds_per_device)

        # DEV-ALPHA je sada prazan
        status, get_alpha = self._http_request("GET", "/api/devices/DEV-ALPHA/commands")
        self.assertEqual(len(get_alpha.get("commands", [])), 0)

        # Svi ostali uređaji i dalje moraju imati svojih 6 komandi netaknutih
        for dev in devices[1:]:
            status, get_other = self._http_request("GET", f"/api/devices/{dev}/commands")
            self.assertEqual(len(get_other.get("commands", [])), cmds_per_device)

    def test_14_concurrent_enqueue_and_heartbeat_pop_same_device(self):
        """
        Konkurentno slanje komandi i preuzimanje kroz heartbeat za isti uređaj:
        - Thread-safety provera komandnog reda pod paralelnim opterećenjem.
        - Osigurava da nijedna komanda ne biva izgubljena ili duplirana.
        """
        device_id = "DEV-CONCURRENT-SYNC"
        num_commands = 12
        enqueued_ids = set()
        received_ids = []
        received_lock = threading.Lock()

        def _producer(seq: int):
            payload = {"action": f"stress_action_{seq}", "params": {"n": seq}}
            status, resp = self._http_request("POST", f"/api/devices/{device_id}/command", payload)
            if status == 200:
                cid = resp["command"]["command_id"]
                return cid
            return None

        def _consumer():
            for _ in range(5):
                time.sleep(0.02)
                hb_payload = {"computer_name": device_id}
                status, resp = self._http_request("POST", "/api/heartbeat", hb_payload)
                if status == 200:
                    with received_lock:
                        for c in resp.get("pending_commands", []):
                            received_ids.append(c["command_id"])

        # Pokreni paralelne producere i potrošača
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
            consumer_fut = executor.submit(_consumer)
            prod_futures = [executor.submit(_producer, i) for i in range(num_commands)]

            for fut in concurrent.futures.as_completed(prod_futures):
                cid = fut.result()
                self.assertIsNotNone(cid)
                enqueued_ids.add(cid)

            consumer_fut.result()

        # Pokupi sve preostale komande koje potrošač nije stigao da pokupi
        hb_status, hb_final = self._http_request("POST", "/api/heartbeat", {"computer_name": device_id})
        self.assertEqual(hb_status, 200)
        with received_lock:
            for c in hb_final.get("pending_commands", []):
                received_ids.append(c["command_id"])

        # Sve komande moraju biti primljene tačno jednom (bez gubitaka i bez duplikata)
        self.assertEqual(len(received_ids), num_commands)
        self.assertEqual(set(received_ids), enqueued_ids)
        self.assertEqual(len(set(received_ids)), num_commands)

    def test_15_action_validation_supported_and_unknown_actions(self):
        """
        Validacija tipova akcija:
        - Podržane akcije: reboot_uefi, reboot_pxe, cancel, abort.
        - Nepoznate/prilagođene akcije (prosledjuju se transparentno bez pada servera).
        - Sanitizacija praznog prostora (whitespace trim).
        - Opcioni params (null/None se konvertuje u prazan rečnik {}).
        """
        device_id = "DEV-ACTION-TEST"

        test_cases = [
            ("reboot_uefi", {"reason": "firmware_update"}, "reboot_uefi", {"reason": "firmware_update"}),
            ("reboot_pxe", {"network_interface": "eth0"}, "reboot_pxe", {"network_interface": "eth0"}),
            ("cancel", {"task": "chkdsk"}, "cancel", {"task": "chkdsk"}),
            ("abort", {}, "abort", {}),
            ("custom_telemetry_probe", {"interval": 5}, "custom_telemetry_probe", {"interval": 5}),
            ("  reboot_uefi  ", None, "reboot_uefi", {}),  # trim + None params
        ]

        for req_action, req_params, exp_action, exp_params in test_cases:
            payload = {"action": req_action}
            if req_params is not None:
                payload["params"] = req_params
            else:
                payload["params"] = None

            status, resp = self._http_request("POST", f"/api/devices/{device_id}/command", payload)
            self.assertEqual(status, 200)
            self.assertEqual(resp.get("status"), "ok")
            cmd = resp.get("command", {})
            self.assertEqual(cmd.get("action"), exp_action)
            self.assertEqual(cmd.get("params"), exp_params)

        # Provera da klijent kroz heartbeat preuzima sve akcije tačno u redosledu slanja
        status, hb_resp = self._http_request("POST", "/api/heartbeat", {"computer_name": device_id})
        self.assertEqual(status, 200)
        cmds = hb_resp.get("pending_commands", [])
        self.assertEqual(len(cmds), len(test_cases))
        for idx, (req_action, req_params, exp_action, exp_params) in enumerate(test_cases):
            self.assertEqual(cmds[idx]["action"], exp_action)
            self.assertEqual(cmds[idx]["params"], exp_params)

    def test_16_http_overflow_queue_limit_20_commands(self):
        """
        Testiranje ponašanja kada klijent ne preuzme komande neko vreme:
        - Slanje 28 komandi preko HTTP REST interfejsa.
        - Verifikacija limita od 20 komandi (najstarijih 8 se odbacuje po FIFO principu).
        - Heartbeat preuzima tačno 20 najnovijih komandi.
        - Sledeći heartbeat i GET potvrđuju da je red potpuno ispražnjen.
        """
        device_id = "DEV-OVERFLOW-HTTP"

        for i in range(28):
            payload = {"action": f"cmd_{i}", "params": {"index": i}}
            status, resp = self._http_request("POST", f"/api/devices/{device_id}/command", payload)
            self.assertEqual(status, 200)

        # Provera preko GET /api/devices/{id}/commands
        status, get_resp = self._http_request("GET", f"/api/devices/{device_id}/commands")
        self.assertEqual(status, 200)
        cmds = get_resp.get("commands", [])
        self.assertEqual(len(cmds), 20)
        # Odbaceno 0..7, ostalo 8..27
        self.assertEqual(cmds[0]["action"], "cmd_8")
        self.assertEqual(cmds[0]["params"]["index"], 8)
        self.assertEqual(cmds[-1]["action"], "cmd_27")
        self.assertEqual(cmds[-1]["params"]["index"], 27)

        # Klijentski heartbeat mora primiti tačno ovih 20 komandi
        status, hb_resp = self._http_request("POST", "/api/heartbeat", {"computer_name": device_id})
        self.assertEqual(status, 200)
        hb_cmds = hb_resp.get("pending_commands", [])
        self.assertEqual(len(hb_cmds), 20)
        self.assertEqual(hb_cmds[0]["action"], "cmd_8")
        self.assertEqual(hb_cmds[-1]["action"], "cmd_27")

        # Sledeći heartbeat mora biti prazan
        status, hb_resp_2 = self._http_request("POST", "/api/heartbeat", {"computer_name": device_id})
        self.assertEqual(status, 200)
        self.assertEqual(hb_resp_2.get("pending_commands"), [])

        # GET takođe mora biti prazan
        status, get_resp_2 = self._http_request("GET", f"/api/devices/{device_id}/commands")
        self.assertEqual(status, 200)
        self.assertEqual(get_resp_2.get("commands"), [])

    def test_17_heartbeat_pop_once_semantics_and_alias_matching(self):
        """
        Verifikacija jednokratnog pražnjenja (pop-once) i asocijacije alijasa:
        - Komanda poslata na sanitizovani ili puni naziv.
        - Heartbeat je preuzima tačno jednom.
        - Naredni heartbeat pozivi (bilo sa computer_name ili device_id) ne smeju ponoviti komandu.
        """
        dev_orig = "TK-STATION #05"
        cmd_payload = {"action": "reboot_uefi", "params": {"delay": 5}}

        # Postavi komandu
        status, resp = self._http_request("POST", f"/api/devices/{urllib.parse.quote(dev_orig)}/command", cmd_payload)
        self.assertEqual(status, 200)
        cmd_id = resp["command"]["command_id"]

        # Prvi heartbeat sa originalnim imenom preuzima komandu
        status, hb1 = self._http_request("POST", "/api/heartbeat", {
            "computer_name": dev_orig,
            "device_id": "tk_station_05"
        })
        self.assertEqual(status, 200)
        self.assertEqual(len(hb1.get("pending_commands", [])), 1)
        self.assertEqual(hb1["pending_commands"][0]["command_id"], cmd_id)

        # Drugi heartbeat sa computer_name - komanda se NE SME ponoviti
        status, hb2 = self._http_request("POST", "/api/heartbeat", {
            "computer_name": dev_orig
        })
        self.assertEqual(status, 200)
        self.assertEqual(hb2.get("pending_commands"), [])

        # Treći heartbeat sa alternativnim device_id - takođe prazno
        status, hb3 = self._http_request("POST", "/api/heartbeat", {
            "device_id": "tk_station_05"
        })
        self.assertEqual(status, 200)
        self.assertEqual(hb3.get("pending_commands"), [])

    def test_18_idempotent_get_does_not_drain_queue(self):
        """
        Provera da GET /api/devices/{id}/commands ne prazni red (idempotentan je),
        dok tek POST /api/heartbeat vrši pražnjenje.
        """
        device_id = "DEV-IDEMPOTENT"
        self._http_request("POST", f"/api/devices/{device_id}/command", {"action": "test_idem"})

        # Pozovi GET 3 puta zaredom
        for _ in range(3):
            status, get_resp = self._http_request("GET", f"/api/devices/{device_id}/commands")
            self.assertEqual(status, 200)
            self.assertEqual(len(get_resp.get("commands", [])), 1)
            self.assertEqual(get_resp["commands"][0]["action"], "test_idem")

        # Tek heartbeat vrši pražnjenje
        status, hb_resp = self._http_request("POST", "/api/heartbeat", {"computer_name": device_id})
        self.assertEqual(status, 200)
        self.assertEqual(len(hb_resp.get("pending_commands", [])), 1)

        # Nakon heartbeat-a, GET mora vratiti prazan red
        status, get_resp_after = self._http_request("GET", f"/api/devices/{device_id}/commands")
        self.assertEqual(status, 200)
        self.assertEqual(get_resp_after.get("commands"), [])

    def test_19_powershell_client_contract_and_ast_verification(self):
        """
        Validacija klijentskog ugovora u WIS.Telemetry.psm1 za daljinsko upravljanje:
        - Statička leksička i sintaksna analiza koda preko Verify-PowerShellSyntax parsera.
        - Verifikacija funkcije Invoke-ServisRemoteCommand i njene strukture.
        - Verifikacija obrade 'reboot_uefi' sa shutdown.exe /r /fw /t 3 i fallback-om.
        - Verifikacija obrade 'reboot_pxe', 'cancel', 'abort' i default grane.
        - Verifikacija da Send-ServisHeartbeat preuzima pending_commands i delegira ih.
        - Verifikacija izvoza funkcije u Export-ModuleMember.
        """
        psm1_path = str(Path(__file__).resolve().parents[2] / "UI/Skripte/Modules/WIS.Telemetry.psm1")
        self.assertTrue(os.path.isfile(psm1_path), f"Modul ne postoji: {psm1_path}")

        # 1. Rigorozna sintaksna i leksička verifikacija
        spec = importlib.util.spec_from_file_location("verify_ps", str(Path(__file__).resolve().parents[2] / "ISO/Skripte/Verify-PowerShellSyntax.py"))
        verify_ps = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(verify_ps)
        is_syntax_valid = verify_ps.analyze_ps_syntax(psm1_path)
        self.assertTrue(is_syntax_valid, "PowerShell sintaksa u WIS.Telemetry.psm1 nije 100% validna!")

        # 2. Učitavanje i provera sadržaja ugovora
        with open(psm1_path, "r", encoding="utf-8") as f:
            ps_code = f.read()

        # Funkcija Invoke-ServisRemoteCommand
        self.assertIn("function Invoke-ServisRemoteCommand", ps_code)
        self.assertIn("[psobject]$Command", ps_code)

        # Obrada akcija
        self.assertIn('"reboot_uefi"', ps_code)
        self.assertIn("shutdown.exe /r /fw /t 3", ps_code)
        self.assertIn("shutdown.exe /r /t 3", ps_code)  # Fallback na standardni restart za Legacy BIOS
        self.assertIn('"reboot_pxe"', ps_code)
        self.assertIn('"cancel"', ps_code)
        self.assertIn('"abort"', ps_code)
        self.assertIn("default {", ps_code)

        # Heartbeat integracija: obrada pending_commands
        self.assertIn("$respObj.pending_commands", ps_code)
        self.assertIn("Invoke-ServisRemoteCommand -Command $cmd", ps_code)

        # Izvoz funkcije
        self.assertIn("Invoke-ServisRemoteCommand", ps_code)
        self.assertIn("Export-ModuleMember", ps_code)


if __name__ == "__main__":
    unittest.main()

