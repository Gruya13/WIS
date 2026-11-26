"""
test_v355_api_reports_commands.py
=================================
Sveobuhvatni jedinični i integracioni testovi za WIS v3.5.5 / ServiceHub v3.7.2 (WIS-TASK-279):
- IMP-98 (handlers.py): uklonjen rani return pre asinhronog ingest_oem_pack na
  /api/drivers/resolve, /api/drivers/resolve_batch i /api/drivers/pool/bundle (FastNas)
  čak i kada #1 vrati postojeći harvest/driver_pool pogodak (uklj. PostRebootFinalize / Code 10/28/43).
- IMP-102: standardizovani pending_commands (cmd_<uuid>, ISO UTC, legacy normalizacija),
  stroga serverska allowlista (HTTP 400 za nepoznate/arbitrarne shell/PS komande i loše parametre),
  POST /api/fleet/command (fail-closed X-ServisHub-Admin-Token + filter_status/filter_phase/version_lt),
  POST /api/devices/<id>/command-ack (fail-closed client/admin token) + executed_commands u /api/heartbeat.
- IMP-107: POST /api/devices/<id>/report prvo pod fleet_lock obogaćuje payload živim existing_rec
  zapisom pa tek onda poziva save_device_report i upisuje spojeni izveštaj u existing_rec["latest_report"].
- IMP-112: image_type ("preinstalled" | "lean", default "lean") i wim_version u heartbeat-u,
  kartonu (JSON + Markdown) i /api/benchmark/summary (odvojeno poređenje Faze 4 i ukupnog vremena).
- IMP-114: kanonski autopilot objekat kroz /api/heartbeat, /api/devices/<id>/report i
  POST/GET /api/devices/<id>/autopilot (WinPE pre-format forenzika po SN, fail-closed client/admin token),
  nepregazivost jednom potvrđenog is_locked=True, Markdown/JSON sekcija bez lažnog zelenog za NIJE_PROVERENO
  i XSS-bezbedan prikaz u dashboard.js.
"""

import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import MagicMock

ROOT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ROOT_DIR not in sys.path:
    sys.path.insert(0, ROOT_DIR)

from servishub.benchmark import compute_fleet_benchmark_summary, extract_device_benchmark
from servishub.config import HubConfig
from servishub.drivers.pool import DriverPoolManager
from servishub.handlers import ServisHubHandler
from servishub.reports import (
    compute_handover_status,
    generate_markdown_report,
    merge_autopilot_status,
    normalize_autopilot_payload,
)
from servishub.telemetry import (
    ALLOWED_REMOTE_COMMAND_ACTIONS,
    FleetStore,
    normalize_command_record,
)


def _make_sample_inf_bytes(
    provider="Intel",
    ver="01/15/2024,31.0.101.5186",
    hwid=r"PCI\VEN_8086&DEV_51D0&SUBSYS_00011414",
    drv_class="System",
) -> bytes:
    txt = (
        "[Version]\n"
        'Signature="$WINDOWS NT$"\n'
        f"Class={drv_class}\n"
        "Provider=%Mfg%\n"
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
        'DevDesc="Intel Quick SPI Host Controller"\n'
    )
    return txt.encode("utf-8")


def _make_driver_tar(dest_tar: str, inf_name: str = "IntelQuickSPI.inf", hwid: str = r"PCI\VEN_8086&DEV_51D0&SUBSYS_00011414"):
    inf_bytes = _make_sample_inf_bytes(hwid=hwid)
    with tarfile.open(dest_tar, "w") as tf:
        ti = tarfile.TarInfo(name=f"drv/{inf_name}")
        ti.size = len(inf_bytes)
        tf.addfile(ti, io.BytesIO(inf_bytes))

        sys_bytes = b"SYS_PAYLOAD_V355"
        ts = tarfile.TarInfo(name="drv/dummy.sys")
        ts.size = len(sys_bytes)
        tf.addfile(ts, io.BytesIO(sys_bytes))

        cat_bytes = b"CAT_CERT_V355"
        tc = tarfile.TarInfo(name="drv/test.cat")
        tc.size = len(cat_bytes)
        tf.addfile(tc, io.BytesIO(cat_bytes))


class TestV355ApiReportsCommands(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v355_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def _start_server(self, cfg: HubConfig, store: FleetStore = None, pool_mgr=None):
        st = store or FleetStore(
            live_logs_dir=cfg.live_logs_dir,
            reports_dir=cfg.reports_dir,
        )

        class _BoundHandler(ServisHubHandler):
            pass

        _BoundHandler.config = cfg
        _BoundHandler.fleet_store = st
        _BoundHandler.driver_pool_mgr = pool_mgr
        _BoundHandler.driver_cache_mgr = None
        _BoundHandler.ms_catalog_scraper = None

        httpd = ThreadingHTTPServer(("127.0.0.1", 0), _BoundHandler)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        def _cleanup():
            httpd.shutdown()
            httpd.server_close()

        self.addCleanup(_cleanup)
        return httpd.server_address[1], st

    def _http_json(self, port: int, method: str, path: str, body=None, headers: dict = None):
        url = f"http://127.0.0.1:{port}{path}"
        hdrs = dict(headers or {})
        raw = None
        if body is not None:
            raw = json.dumps(body).encode("utf-8")
            hdrs.setdefault("Content-Type", "application/json")
        req = urllib.request.Request(url, data=raw, method=method, headers=hdrs)
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return resp.status, dict(resp.headers), json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as he:
            try:
                raw_err = he.read().decode("utf-8")
                try:
                    parsed_err = json.loads(raw_err)
                except Exception:
                    parsed_err = {"raw": raw_err}
                return he.code, dict(he.headers), parsed_err
            finally:
                he.close()

    # =========================================================================
    # 1. IMP-98: Uklonjen rani return pre asinhronog ingest_oem_pack
    # =========================================================================

    def test_01_imp98_resolve_triggers_bg_oem_ingest_even_on_harvest_pool_hit(self):
        """
        Kada #1 (resolve_device_hwids) vrati harvest pogodak a manifest nema oem_pack,
        /api/drivers/resolve i /api/drivers/resolve_batch moraju pokrenuti pozadinski ingest_oem_pack
        (posebno iz PostRebootFinalize/gate-a ili sa Code 10/28/43).
        """
        pool_dir = os.path.join(self.tmp_dir, "pool_imp98")
        pool_mgr = DriverPoolManager(pool_dir=pool_dir)
        tar_fp = os.path.join(self.tmp_dir, "sl5_harvest.tar")
        hwid_51d0 = r"PCI\VEN_8086&DEV_51D0&SUBSYS_00011414"
        _make_driver_tar(tar_fp, inf_name="IntelQuickSPI.inf", hwid=hwid_51d0)
        pool_mgr.ingest_archive(tar_fp, source="harvest", model="Surface Laptop 5")

        self.assertFalse(pool_mgr.has_oem_pack_in_manifest("Surface Laptop 5"))
        pool_mgr.catalog_mgr.find_driver_pack = MagicMock(return_value={
            "package_id": "ms_sl5_w11",
            "package_name": "SurfaceLaptop5_Win11.msi",
        })
        pool_mgr.start_dedup_background_job = MagicMock(return_value={"job_id": "dp_oem_sl5", "status": "running"})

        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
            driver_pool_dir=pool_dir,
            allow_unauthenticated_lan=True,
        )
        port, _ = self._start_server(cfg, pool_mgr=pool_mgr)

        # (a) /api/drivers/resolve sa harvest pogotkom i Code 10 iz PostRebootFinalize
        st, _, data = self._http_json(port, "POST", "/api/drivers/resolve", {
            "manufacturer": "Microsoft Corporation",
            "model": "Surface Laptop 5",
            "device_name": "Intel Quick SPI Host Controller - 51D0",
            "stage": "PostRebootFinalize",
            "error_code": 10,
            "hwids": [hwid_51d0],
        })
        self.assertEqual(st, 200)
        self.assertEqual(data["results"][0]["status"], "ready")
        self.assertEqual(data["results"][0]["source"], "driver_pool")
        self.assertTrue(data["results"][0].get("oem_ingest_triggered"))
        self.assertEqual(data["results"][0].get("oem_job_id"), "dp_oem_sl5")
        self.assertTrue(data.get("oem_ingest_triggered"))
        pool_mgr.start_dedup_background_job.assert_called_once()

        # (b) /api/drivers/resolve_batch sa devices listom i error_code=43 (čak i kad find_driver_pack vrati None)
        pool_mgr.start_dedup_background_job.reset_mock()
        pool_mgr.catalog_mgr.find_driver_pack = MagicMock(return_value=None)
        st_b, _, data_b = self._http_json(port, "POST", "/api/drivers/resolve_batch", {
            "manufacturer": "Microsoft Corporation",
            "model": "Surface Laptop 5",
            "stage": "PreAutoDriverGate",
            "devices": [
                {
                    "device_name": "Intel Quick SPI - 51D0",
                    "error_code": 43,
                    "hwids": [hwid_51d0],
                }
            ],
        })
        self.assertEqual(st_b, 200)
        self.assertEqual(data_b["resolved_count"], 1)
        self.assertTrue(data_b.get("oem_ingest_triggered"))
        pool_mgr.start_dedup_background_job.assert_called_once()

        # (c) FastNas /api/drivers/pool/bundle pokreće OEM prefetch i kad je zadat samo model (bez manufacturer)
        pool_mgr.start_dedup_background_job.reset_mock()
        st_f, _, data_f = self._http_json(port, "POST", "/api/drivers/pool/bundle", {
            "model": "Surface Laptop 5",
            "metadata_only": True,
        })
        self.assertEqual(st_f, 200)
        self.assertTrue(data_f.get("oem_ingest_triggered"))
        pool_mgr.start_dedup_background_job.assert_called_once()

        # (d) Ako manifest VEĆ ima oem_pack, ne pokreće novi ingest
        pool_mgr.start_dedup_background_job.reset_mock()
        pool_mgr.ingest_archive(tar_fp, source="oem_pack", model="Surface Laptop 5")
        self.assertTrue(pool_mgr.has_oem_pack_in_manifest("Surface Laptop 5"))
        st_ok, _, data_ok = self._http_json(port, "POST", "/api/drivers/resolve", {
            "manufacturer": "Microsoft Corporation",
            "model": "Surface Laptop 5",
            "stage": "PostRebootFinalize",
            "error_code": 10,
            "hwids": [hwid_51d0],
        })
        self.assertEqual(st_ok, 200)
        self.assertEqual(data_ok["results"][0]["source"], "oem_pack")
        self.assertFalse(data_ok.get("oem_ingest_triggered", False))
        pool_mgr.start_dedup_background_job.assert_not_called()

    # =========================================================================
    # 2. IMP-102: Allowlista komandi, standardizovani format, fleet broadcast, command-ack & heartbeat
    # =========================================================================

    def test_02_imp102_allowlist_standard_format_and_legacy_compat(self):
        """Proverava standardizovani command objekat, legacy normalizaciju i strogo odbijanje shell/PS akcija."""
        required_actions = {
            "unlock_driver_gate",
            "retry_phase",
            "jump_to_phase",
            "re_resolve_drivers",
            "reboot_and_resume",
            "apply_payload_update",
            "skip_step",
            "cancel",
            "abort",
            "reboot_pxe",
        }
        self.assertTrue(required_actions.issubset(ALLOWED_REMOTE_COMMAND_ACTIONS))

        # Legacy string i float created_at se normalizuju u kanonski ISO UTC objekat
        norm_str = normalize_command_record("reboot_pxe", device_id="DEV-LEG")
        self.assertIsNotNone(norm_str)
        self.assertTrue(norm_str["command_id"].startswith("cmd_"))
        self.assertEqual(norm_str["action"], "reboot_pxe")
        self.assertEqual(norm_str["params"], {})
        self.assertTrue(norm_str["created_at"].endswith("Z"))

        norm_float = normalize_command_record({
            "command_id": "cmd_legacy_1",
            "action": "skip_step",
            "params": {"step": "P4"},
            "created_at": 1764158400.0,
        }, device_id="DEV-LEG")
        self.assertEqual(norm_float["command_id"], "cmd_legacy_1")
        self.assertIsInstance(norm_float["created_at"], str)
        self.assertTrue(norm_float["created_at"].endswith("Z"))

        # Nedozvoljena akcija u legacy zapisu se odbacuje
        self.assertIsNone(normalize_command_record("powershell -enc AAAA"))

        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
            admin_token="adm-tok-v355",
            client_token="cli-tok-v355",
            allow_unauthenticated_lan=False,
        )
        port, _ = self._start_server(cfg)

        # Svaka akcija iz allowliste prolazi sa X-ServisHub-Admin-Token
        for act in sorted(required_actions):
            params = {}
            if act in ("retry_phase", "jump_to_phase"):
                params = {"phase": 4, "clear_step_guard": True}
            elif act == "apply_payload_update":
                params = {"mode": "scripts_only", "version": "3.5.5"}
            elif act == "unlock_driver_gate":
                params = {"reason": "Code 10 bypass"}
            st, _, resp = self._http_json(
                port,
                "POST",
                "/api/devices/TK-ALLOW-01/command",
                {"action": act, "params": params},
                headers={"X-ServisHub-Admin-Token": "adm-tok-v355"},
            )
            self.assertEqual(st, 200, f"Allowlist akcija '{act}' mora proći")
            self.assertTrue(resp["command"]["command_id"].startswith("cmd_"))
            self.assertTrue(resp["command"]["created_at"].endswith("Z"))

        # Arbitrarne shell/PS i nepoznate akcije -> strogi HTTP 400
        for evil_action in (
            "Invoke-Expression",
            "powershell -c whoami",
            "cmd /c dir",
            "exec_shell",
            "reboot_pxe; calc.exe",
            "download_and_run",
        ):
            st_e, _, err_resp = self._http_json(
                port,
                "POST",
                "/api/devices/TK-ALLOW-01/command",
                {"action": evil_action, "params": {}},
                headers={"X-ServisHub-Admin-Token": "adm-tok-v355"},
            )
            self.assertEqual(st_e, 400, f"Opasna akcija '{evil_action}' nije odbijena sa 400")
            self.assertIn("error", err_resp)

        # Nevalidni parametri za jump_to_phase (phase=99) i apply_payload_update (mode=arbitrary) -> 400
        st_p, _, _ = self._http_json(
            port,
            "POST",
            "/api/devices/TK-ALLOW-01/command",
            {"action": "jump_to_phase", "params": {"phase": 99}},
            headers={"X-ServisHub-Admin-Token": "adm-tok-v355"},
        )
        self.assertEqual(st_p, 400)

        st_m, _, _ = self._http_json(
            port,
            "POST",
            "/api/devices/TK-ALLOW-01/command",
            {"action": "apply_payload_update", "params": {"mode": "exec_raw"}},
            headers={"X-ServisHub-Admin-Token": "adm-tok-v355"},
        )
        self.assertEqual(st_m, 400)

    def test_03_imp102_fleet_broadcast_fail_closed_filters_and_command_ack(self):
        """Testira POST /api/fleet/command (fail-closed 503/401 + filteri), POST /command-ack i heartbeat executed_commands."""
        # 1. Fail-closed 503 kad nijedan token nije podešen
        cfg_closed = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_closed"),
            logs_dir=os.path.join(self.tmp_dir, "lg_closed"),
            admin_token="",
            client_token="",
            allow_unauthenticated_lan=False,
        )
        port_c, _ = self._start_server(cfg_closed)
        st_503_fl, _, _ = self._http_json(port_c, "POST", "/api/fleet/command", {"action": "apply_payload_update"})
        self.assertEqual(st_503_fl, 503)
        st_503_ack, _, _ = self._http_json(port_c, "POST", "/api/devices/DEV1/command-ack", {"command_id": "cmd_1"})
        self.assertEqual(st_503_ack, 503)

        # 2. Sa podešenim tokenima: bez/sa lošim tokenom -> 401
        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_open"),
            logs_dir=os.path.join(self.tmp_dir, "lg_open"),
            admin_token="adm-secret-355",
            client_token="cli-secret-355",
            allow_unauthenticated_lan=False,
        )
        port, store = self._start_server(cfg)

        # Client token NE SME pozvati /api/fleet/command -> 401
        st_401_cli, _, _ = self._http_json(
            port,
            "POST",
            "/api/fleet/command",
            {"action": "apply_payload_update"},
            headers={"X-ServisHub-Client-Token": "cli-secret-355"},
        )
        self.assertEqual(st_401_cli, 401)

        # Registruj 3 uređaja kroz heartbeat (različite faze, statusi i verzije)
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-FL01",
            "phase": 5,
            "status_label": "Aktivno",
            "wis_version": "3.5.4",
        })
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-FL02",
            "phase": 2,
            "status_label": "Aktivno",
            "wis_version": "3.5.4",
        })
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-FL03",
            "phase": 5,
            "status_label": "Aktivno",
            "wis_version": "3.5.5",
        })

        # Broadcast samo aktivnim uređajima u fazi 5 sa verzijom < 3.5.5 -> samo TK-LAPTOP-FL01!
        st_fl, _, fl_resp = self._http_json(
            port,
            "POST",
            "/api/fleet/command",
            {
                "action": "apply_payload_update",
                "params": {"mode": "scripts_only", "version": "3.5.5"},
                "filter_status": "Aktivno",
                "filter_phase": 5,
                "version_lt": "3.5.5",
            },
            headers={"X-ServisHub-Admin-Token": "adm-secret-355"},
        )
        self.assertEqual(st_fl, 200)
        self.assertEqual(fl_resp["count"], 1)
        self.assertEqual(fl_resp["matched_devices"], ["TK-LAPTOP-FL01"])
        self.assertEqual(len(fl_resp["command_ids"]), 1)
        cmd_id_fl01 = fl_resp["command_ids"][0]

        # Broadcast sa nepoznatom akcijom -> 400
        st_bad_fl, _, _ = self._http_json(
            port,
            "POST",
            "/api/fleet/command",
            {"action": "Invoke-Expression whoami"},
            headers={"X-ServisHub-Admin-Token": "adm-secret-355"},
        )
        self.assertEqual(st_bad_fl, 400)

        # POST /api/devices/TK-LAPTOP-FL01/command-ack bez tokena -> 401
        st_ack_401, _, _ = self._http_json(
            port,
            "POST",
            "/api/devices/TK-LAPTOP-FL01/command-ack",
            {"command_id": cmd_id_fl01, "action": "apply_payload_update", "status": "ok"},
        )
        self.assertEqual(st_ack_401, 401)

        # POST /command-ack sa klijentskim tokenom -> 200, čisti pending komandu i čuva istoriju
        st_ack_ok, _, ack_resp = self._http_json(
            port,
            "POST",
            "/api/devices/TK-LAPTOP-FL01/command-ack",
            {
                "command_id": cmd_id_fl01,
                "action": "apply_payload_update",
                "status": "applied",
                "detail": "Hotfix v3.5.5 scripts_only uspešno primenjen",
                "applied_at": "2026-11-26T12:20:00Z",
            },
            headers={"X-ServisHub-Client-Token": "cli-secret-355"},
        )
        self.assertEqual(st_ack_ok, 200)
        self.assertEqual(ack_resp["ack"]["command_id"], cmd_id_fl01)
        self.assertEqual(ack_resp["ack"]["status"], "applied")

        # GET /commands pokazuje prazan pending i zabeležen executed_commands
        st_gc, _, gc_resp = self._http_json(port, "GET", "/api/devices/TK-LAPTOP-FL01/commands")
        self.assertEqual(st_gc, 200)
        self.assertEqual(gc_resp["commands"], [])
        self.assertEqual(len(gc_resp["executed_commands"]), 1)
        self.assertEqual(gc_resp["executed_commands"][0]["command_id"], cmd_id_fl01)

        # Heartbeat sa executed_commands takođe beleži potvrdu izvršenja
        st_hb, _, _ = self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-FL02",
            "phase": 3,
            "executed_commands": [
                {
                    "command_id": "cmd_hb_ack_001",
                    "action": "unlock_driver_gate",
                    "status": "ok",
                    "detail": "Gate otključan daljinski",
                    "applied_at": "2026-11-26T12:25:00Z",
                }
            ],
        })
        self.assertEqual(st_hb, 200)
        hist_fl02 = store.get_command_history("TK-LAPTOP-FL02")
        self.assertEqual(len(hist_fl02), 1)
        self.assertEqual(hist_fl02[0]["command_id"], "cmd_hb_ack_001")
        self.assertEqual(store.fleet_devices["TK-LAPTOP-FL02"]["last_command_ack"]["action"], "unlock_driver_gate")

    # =========================================================================
    # 3. IMP-107: Obogaćivanje živim existing_rec PRE save_device_report i upis u latest_report
    # =========================================================================

    def test_04_imp107_report_enriches_from_live_memory_before_disk_save(self):
        """
        Kada na disku još NEMA prethodnog kartona, a existing_rec u RAM-u ima potvrđene
        apps i hw iz ranijih faza, nepotpuni POST /api/devices/<id>/report mora prvo biti
        obogaćen živim existing_rec zapisom pa tek onda snimljen na disk i u latest_report!
        """
        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_imp107"),
            logs_dir=os.path.join(self.tmp_dir, "lg_imp107"),
            allow_unauthenticated_lan=True,
        )
        port, store = self._start_server(cfg)

        # Živi heartbeat u Fazi 4 donosi pun HW i potvrđene OK aplikacije (0 fajlova na disku!)
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-MEM107",
            "serial_number": "SN107LIVE",
            "chassis": "Laptop",
            "phase": 4,
            "hw": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14 Gen 4",
                "cpu": "Intel Core i7-1355U",
                "ram_gb": 16,
                "serial": "SN107LIVE",
            },
            "apps": {
                "Chrome": "OK",
                "Office": "OK",
                "Acrobat": "OK",
                "WinRAR": "OK",
            },
        })
        self.assertEqual(os.listdir(cfg.reports_dir) if os.path.isdir(cfg.reports_dir) else [], [])

        # Prvi finalni report stiže osiromašen (all-False apps + cpu=None)
        st, _, resp = self._http_json(port, "POST", "/api/devices/TK-LAPTOP-MEM107/report", {
            "device_id": "TK-LAPTOP-MEM107",
            "computer_name": "TK-LAPTOP-MEM107",
            "serial_number": "SN107LIVE",
            "status": "COMPLETED",
            "duration_seconds": 410,
            "hw": {"cpu": None, "ram_gb": 0},
            "apps": {"Chrome": False, "Office": False},
        })
        self.assertEqual(st, 200)

        # 1. Provera fajla na disku (.json i .md)
        json_fp = os.path.join(cfg.reports_dir, resp["json_file"])
        md_fp = os.path.join(cfg.reports_dir, resp["md_file"])
        with open(json_fp, "r", encoding="utf-8") as jf:
            saved_disk = json.load(jf)
        with open(md_fp, "r", encoding="utf-8") as mf:
            saved_md = mf.read()

        self.assertEqual(saved_disk["hw"]["cpu"], "Intel Core i7-1355U")
        self.assertEqual(saved_disk["hw"]["ram_gb"], 16)
        self.assertEqual(saved_disk["apps"]["Chrome"], "OK")
        self.assertEqual(saved_disk["apps"]["Office"], "OK")
        self.assertEqual(saved_disk["apps"]["Acrobat"], "OK")
        self.assertIn("Intel Core i7-1355U", saved_md)
        self.assertIn("| Chrome | ✅ OK |", saved_md)

        # 2. Provera existing_rec["latest_report"] u memoriji (takođe mora biti spojeni izveštaj!)
        mem_latest = store.fleet_devices["TK-LAPTOP-MEM107"]["latest_report"]
        self.assertEqual(mem_latest["hw"]["cpu"], "Intel Core i7-1355U")
        self.assertEqual(mem_latest["apps"]["Chrome"], "OK")
        self.assertEqual(mem_latest["apps"]["Acrobat"], "OK")

    # =========================================================================
    # 4. IMP-112: image_type ("preinstalled" | "lean") i wim_version u heartbeat, report i benchmark
    # =========================================================================

    def test_05_imp112_image_type_wim_version_and_benchmark_comparison(self):
        """Verifikuje image_type i wim_version kroz heartbeat, karton i odvojenu benchmark statistiku."""
        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_imp112"),
            logs_dir=os.path.join(self.tmp_dir, "lg_imp112"),
            allow_unauthenticated_lan=True,
        )
        port, store = self._start_server(cfg)

        # 1. Heartbeat sa Golden preinstalled profilom
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-GOLD01",
            "serial_number": "GOLD01",
            "phase": 4,
            "image_type": "preinstalled",
            "wim_version": "26200.8037-heavy-v1",
        })
        self.assertEqual(store.fleet_devices["TK-LAPTOP-GOLD01"]["image_type"], "preinstalled")
        self.assertEqual(store.fleet_devices["TK-LAPTOP-GOLD01"]["wim_version"], "26200.8037-heavy-v1")

        # 2. Kasniji heartbeat bez image_type polja ne sme pregaziti preinstalled -> lean
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-GOLD01",
            "serial_number": "GOLD01",
            "phase": 5,
        })
        self.assertEqual(store.fleet_devices["TK-LAPTOP-GOLD01"]["image_type"], "preinstalled")

        # 3. Završni izveštaj za Golden preinstalled (Faza 4 = 25s, ukupno = 240s)
        st_g, _, resp_g = self._http_json(port, "POST", "/api/devices/TK-LAPTOP-GOLD01/report", {
            "device_id": "TK-LAPTOP-GOLD01",
            "computer_name": "TK-LAPTOP-GOLD01",
            "serial_number": "GOLD01",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 240,
            "benchmark": {
                "phase_durations_sec": {"Phase1": 10, "Phase2": 90, "Phase3": 25, "Phase4": 25, "Phase5": 70, "Phase6": 20},
                "total_phase_duration_sec": 240,
            },
        })
        self.assertEqual(st_g, 200)
        with open(os.path.join(cfg.reports_dir, resp_g["md_file"]), "r", encoding="utf-8") as mf:
            md_gold = mf.read()
        self.assertIn("preinstalled", md_gold)
        self.assertIn("26200.8037-heavy-v1", md_gold)

        # 4. Završni izveštaj bez image_type -> default "lean" (Faza 4 = 260s, ukupno = 600s)
        st_l, _, resp_l = self._http_json(port, "POST", "/api/devices/TK-LAPTOP-LEAN01/report", {
            "device_id": "TK-LAPTOP-LEAN01",
            "computer_name": "TK-LAPTOP-LEAN01",
            "serial_number": "LEAN01",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 600,
            "benchmark": {
                "phase_durations_sec": {"Phase1": 15, "Phase2": 180, "Phase3": 35, "Phase4": 260, "Phase5": 85, "Phase6": 25},
                "total_phase_duration_sec": 600,
            },
        })
        self.assertEqual(st_l, 200)
        with open(os.path.join(cfg.reports_dir, resp_l["json_file"]), "r", encoding="utf-8") as jf:
            lean_json = json.load(jf)
        self.assertEqual(lean_json["image_type"], "lean")

        # 5. GET /api/benchmark/summary -> odvojena statistika preinstalled vs lean
        st_bm, _, bm = self._http_json(port, "GET", "/api/benchmark/summary")
        self.assertEqual(st_bm, 200)
        self.assertIn("by_image_type", bm)
        self.assertIn("image_type_comparison", bm)

        pre = bm["by_image_type"]["preinstalled"]
        lean = bm["by_image_type"]["lean"]
        self.assertEqual(pre["total"], 1)
        self.assertEqual(pre["avg_phase4_seconds"], 25.0)
        self.assertEqual(pre["avg_duration_seconds"], 240.0)
        self.assertEqual(lean["total"], 1)
        self.assertEqual(lean["avg_phase4_seconds"], 260.0)
        self.assertEqual(lean["avg_duration_seconds"], 600.0)

        cmp_obj = bm["image_type_comparison"]
        self.assertEqual(cmp_obj["phase4_savings_seconds"], 235.0)
        self.assertEqual(cmp_obj["total_savings_seconds"], 360.0)
        self.assertGreater(cmp_obj["phase4_savings_pct"], 85.0)

        # extract_device_benchmark default "lean"
        bm_empty = extract_device_benchmark({})
        self.assertEqual(bm_empty["image_type"], "lean")
        self.assertEqual(bm_empty["wim_version"], "unknown")

    # =========================================================================
    # 5. IMP-114: Kanonski Autopilot, /api/devices/<id>/autopilot, is_locked zaštita, MD bez lažnog zelenog
    # =========================================================================

    def test_06_imp114_autopilot_endpoint_integrity_and_no_false_green(self):
        """
        Verifikuje:
        1) Fail-closed zaštitu POST /api/devices/<id>/autopilot (503 bez tokena, 401 sa pogrešnim, 200 sa client/admin).
        2) WinPE pre-format forenziku po SN (is_locked=True) koju kasniji prazan/UNVERIFIED heartbeat,
           STARTED lifecycle ili nepotpun report NIKADA ne sme pregaziti!
        3) Zabrana lažnog zelenog za UNVERIFIED/NIJE_PROVERENO u normalizaciji, handover statusu, Markdown-u i dashboard.js.
        """
        # 1. Fail-closed 503 bez konfigurisanih tokena
        cfg_closed = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_ap_closed"),
            logs_dir=os.path.join(self.tmp_dir, "lg_ap_closed"),
            admin_token="",
            client_token="",
            allow_unauthenticated_lan=False,
        )
        port_c, _ = self._start_server(cfg_closed)
        st_503, _, _ = self._http_json(port_c, "POST", "/api/devices/0F3QDCR25203P7/autopilot", {"is_locked": True})
        self.assertEqual(st_503, 503)

        # 2. Sa podešenim client/admin tokenom
        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "st_ap"),
            logs_dir=os.path.join(self.tmp_dir, "lg_ap"),
            admin_token="adm-ap-355",
            client_token="cli-ap-355",
            allow_unauthenticated_lan=False,
        )
        port, store = self._start_server(cfg)

        # Bez tokena -> 401
        st_401, _, _ = self._http_json(
            port,
            "POST",
            "/api/devices/0F3QDCR25203P7/autopilot",
            {"is_locked": True, "source": "winpe_preformat"},
        )
        self.assertEqual(st_401, 401)

        # WinPE pre-format forenzika po SN sa ClientToken -> 200
        st_wp, _, wp_resp = self._http_json(
            port,
            "POST",
            "/api/devices/0F3QDCR25203P7/autopilot",
            {
                "matrix_code": "LOCKED_TENANT",
                "is_locked": True,
                "confirmed_free": False,
                "tenant_domain": "corp.contoso.com",
                "tenant_id": "11111111-2222-3333-4444-555555555555",
                "assigned_device_name": "W-AAD-744311751",
                "query_mode": "winpe_offline_registry",
                "has_local_traces": True,
                "source": "winpe_preformat",
            },
            headers={"X-ServisHub-Client-Token": "cli-ap-355"},
        )
        self.assertEqual(st_wp, 200)
        ap_saved = wp_resp["autopilot"]
        self.assertTrue(ap_saved["is_locked"])
        self.assertFalse(ap_saved["confirmed_free"])
        self.assertEqual(ap_saved["binary_verdict"], "IMA_AUTOPILOT")
        self.assertEqual(ap_saved["status_level"], "CRITICAL")
        self.assertEqual(ap_saved["tenant_domain"], "corp.contoso.com")
        self.assertEqual(ap_saved["source"], "winpe_preformat")

        # Posle formata Windows startuje: lifecycle STARTED sa praznim autopilot={} NE SME obrisati is_locked!
        self._http_json(port, "POST", "/api/lifecycle", {
            "event": "STARTED",
            "computer_name": "W-AAD-744311751",
            "serial_number": "0F3QDCR25203P7",
            "autopilot": {},
        })
        # Kasniji heartbeat pod preimenovanim TK-LAPTOP-0F3QDCR25203P7 sa UNVERIFIED_CLEAN NE SME pregaziti is_locked!
        self._http_json(port, "POST", "/api/heartbeat", {
            "computer_name": "TK-LAPTOP-0F3QDCR25203P7",
            "previous_hostname": "W-AAD-744311751",
            "serial_number": "0F3QDCR25203P7",
            "phase": 5,
            "autopilot": {
                "matrix_code": "UNVERIFIED_CLEAN",
                "status_level": "INFO",
                "binary_verdict": "NIJE_PROVERENO",
                "is_locked": False,
                "confirmed_free": False,
                "query_mode": "offline",
                "has_local_traces": False,
                "source": "heartbeat",
            },
        })

        # GET /api/devices/0F3QDCR25203P7/autopilot i /api/devices/TK-LAPTOP-0F3QDCR25203P7/autopilot čuvaju is_locked=True!
        st_get, _, get_ap = self._http_json(port, "GET", "/api/devices/0F3QDCR25203P7/autopilot")
        self.assertEqual(st_get, 200)
        self.assertTrue(get_ap["autopilot"]["is_locked"])
        self.assertEqual(get_ap["autopilot"]["binary_verdict"], "IMA_AUTOPILOT")
        self.assertEqual(get_ap["autopilot"]["tenant_domain"], "corp.contoso.com")
        self.assertEqual(get_ap["autopilot"]["source"], "winpe_preformat")

        # Završni report koji šalje UNVERIFIED takođe čuva is_locked=True u JSON i MD!
        st_rep, _, rep_resp = self._http_json(port, "POST", "/api/devices/TK-LAPTOP-0F3QDCR25203P7/report", {
            "device_id": "TK-LAPTOP-0F3QDCR25203P7",
            "computer_name": "TK-LAPTOP-0F3QDCR25203P7",
            "serial_number": "0F3QDCR25203P7",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 350,
            "autopilot": {
                "matrix_code": "UNVERIFIED_CLEAN",
                "is_locked": False,
                "confirmed_free": False,
                "source": "report",
            },
        })
        self.assertEqual(st_rep, 200)
        with open(os.path.join(cfg.reports_dir, rep_resp["json_file"]), "r", encoding="utf-8") as jf:
            rep_json = json.load(jf)
        with open(os.path.join(cfg.reports_dir, rep_resp["md_file"]), "r", encoding="utf-8") as mf:
            rep_md = mf.read()

        self.assertTrue(rep_json["autopilot"]["is_locked"])
        self.assertEqual(rep_json["autopilot"]["binary_verdict"], "IMA_AUTOPILOT")
        self.assertEqual(compute_handover_status(rep_json), "NEEDS_SERVICE")
        self.assertIn("## 🛡️ Windows Autopilot Status", rep_md)
        self.assertIn("🔴 IMA AUTOPILOT", rep_md)
        self.assertIn("corp.contoso.com", rep_md)
        self.assertNotIn("🟢 NEMA AUTOPILOT", rep_md)

        # 3. Bez lažnog zelenog: UNVERIFIED_CLEAN sa pogrešnim INFO/NEMA_AUTOPILOT mora biti spušten na WARNING/NIJE_PROVERENO!
        unv_norm = normalize_autopilot_payload({
            "matrix_code": "UNVERIFIED_CLEAN",
            "status_level": "INFO",
            "binary_verdict": "NEMA_AUTOPILOT",
            "is_locked": False,
            "confirmed_free": False,
            "source": "autopilot_auditor",
        })
        self.assertEqual(unv_norm["binary_verdict"], "NIJE_PROVERENO")
        self.assertEqual(unv_norm["status_level"], "WARNING")
        self.assertFalse(unv_norm["confirmed_free"])

        md_unv = generate_markdown_report({
            "device_id": "TK-LAPTOP-UNV01",
            "computer_name": "TK-LAPTOP-UNV01",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "autopilot": unv_norm,
        })
        self.assertIn("## 🛡️ Windows Autopilot Status", md_unv)
        self.assertIn("⚠️ NIJE PROVERENO", md_unv)
        self.assertIn("`WARNING`", md_unv)
        self.assertNotIn("🟢 NEMA AUTOPILOT", md_unv)
        self.assertNotIn("Potvrđeno slobodan", md_unv)
        self.assertEqual(compute_handover_status({
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "autopilot": unv_norm,
        }), "READY_WITH_NOTE")

        # 4. Potvrđeno slobodan (Event 807 -> confirmed_free=True, is_locked=False) daje NEMA_AUTOPILOT / OK / zeleno
        free_norm = normalize_autopilot_payload({
            "matrix_code": "CONFIRMED_CLEAN",
            "is_locked": False,
            "confirmed_free": True,
            "query_mode": "oobe_ztd",
            "source": "oobe_ztd",
        })
        self.assertEqual(free_norm["binary_verdict"], "NEMA_AUTOPILOT")
        self.assertEqual(free_norm["status_level"], "OK")
        md_free = generate_markdown_report({
            "device_id": "TK-LAPTOP-FREE01",
            "computer_name": "TK-LAPTOP-FREE01",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "autopilot": free_norm,
        })
        self.assertIn("🟢 NEMA AUTOPILOT", md_free)
        self.assertEqual(compute_handover_status({
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "autopilot": free_norm,
        }), "READY")

        # 5. Provera XSS escaping-a u dashboard.js za Autopilot polja
        js_path = os.path.join(ROOT_DIR, "servishub", "web", "static", "dashboard.js")
        with open(js_path, "r", encoding="utf-8") as f:
            js_src = f.read()
        self.assertIn("IMA AUTOPILOT", js_src)
        self.assertIn("NEMA AUTOPILOT", js_src)
        self.assertIn("NIJE PROVERENO", js_src)
        self.assertIn("escapeHtml(apTenant", js_src)
        self.assertIn("escapeHtml(ap.assigned_device_name)", js_src)
        self.assertIn("escapeHtml(ap.matrix_code)", js_src)


if __name__ == "__main__":
    unittest.main()
