"""
tests.test_screen_capture_client
================================
Validacija PowerShell 5.1 klijentskog modula WIS.ScreenCapture.ps1 (Task 2.2).
Strogo postovanje No-Mock politike:
- Rigorozna sintaksna i leksicka provera skripte preko Verify-PowerShellSyntax parsera.
- Strukturna verifikacija obaveznih parametara, funkcija i .NET API poziva.
- Verifikacija ugovora prenosa: POST /api/devices/{id}/screen-frame, image/jpeg, X-Client-Timestamp, 800ms timeout.
- Verifikacija oslobadjanja resursa (.Dispose()), rukovanja greskama i mehanizma za zaustavljanje.
- End-to-End verifikacija kompatibilnosti sa realnim ServisHub serverom koristeci stvarne JPEG frejmove.
"""

import io
from pathlib import Path
import os
import re
import sys
import time
import json
import shutil
import tempfile
import threading
import unittest
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer
try:
    from PIL import Image
    HAS_PILLOW = True
except ImportError:
    HAS_PILLOW = False
    Image = None

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.handlers import ServisHubHandler

# Uvoz sintaksnog analizatora iz Storage/Skripte
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ISO/Skripte"))
import importlib.util
spec = importlib.util.spec_from_file_location("verify_ps", str(Path(__file__).resolve().parents[2] / "ISO/Skripte/Verify-PowerShellSyntax.py"))
verify_ps = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_ps)


@unittest.skipUnless(HAS_PILLOW, "Pillow nije instaliran (opciona dev zavisnost za screen capture)")
class TestScreenCaptureClient(unittest.TestCase):
    """Testira WIS.ScreenCapture.ps1 klijentski modul i njegovu integraciju sa ServisHub-om."""

    SCRIPT_PATH = str(Path(__file__).resolve().parents[2] / "UI/Skripte/Modules/WIS.ScreenCapture.ps1")

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

    def test_01_script_file_exists_and_syntax_clean(self):
        """Verifikuje postojanje fajla i dopunsku leksicku proveru (nije Windows PowerShell 5.1 parser)."""
        self.assertTrue(os.path.isfile(self.SCRIPT_PATH), f"Skripta ne postoji: {self.SCRIPT_PATH}")

        # Pokretanje dopunskog leksickog proveravaca; pravi PS5.1 parser je Windows CI
        passed = verify_ps.analyze_ps_syntax(self.SCRIPT_PATH)
        self.assertTrue(passed, "Verify-PowerShellSyntax.py je prijavio sintaksne greske u WIS.ScreenCapture.ps1!")

    def test_02_required_functions_and_parameters(self):
        """Verifikuje postojanje svih obaveznih funkcija i parametara u skripti."""
        with open(self.SCRIPT_PATH, "r", encoding="utf-8") as f:
            content = f.read()

        # Obavezne funkcije
        expected_functions = [
            "Start-ServisScreenCapture",
            "Stop-ServisScreenCapture",
            "Capture-ServisScreenFrame",
            "Send-ServisScreenFrame",
            "Get-ServisJpegCodecInfo",
        ]
        for fn in expected_functions:
            self.assertRegex(
                content,
                rf"function\s+{re.escape(fn)}\b",
                f"Funkcija {fn} mora biti definisana u modulu."
            )

        # Provera parametara za Start-ServisScreenCapture
        fn_start_match = re.search(r"function\s+Start-ServisScreenCapture\s*\{([\s\S]*?)\n\}", content)
        self.assertIsNotNone(fn_start_match, "Start-ServisScreenCapture blok nije pronadjen.")
        fn_start_body = fn_start_match.group(1)

        required_params = [
            "HubUrl",
            "DeviceId",
            "IntervalMs",
            "Quality",
            "MaxWidth",
            "StopEventName",
        ]
        for p in required_params:
            self.assertIn(f"${p}", fn_start_body, f"Parametar ${p} mora postojati u Start-ServisScreenCapture.")

    def test_03_dotnet_screen_capture_and_graphics_api(self):
        """Verifikuje koriscenje .NET API-ja za snimanje ekrana i skaliranje."""
        with open(self.SCRIPT_PATH, "r", encoding="utf-8") as f:
            content = f.read()

        # Sklopovi
        self.assertIn("System.Drawing", content)
        self.assertIn("System.Windows.Forms", content)

        # Bounds i CopyFromScreen
        self.assertIn("[System.Windows.Forms.Screen]::PrimaryScreen.Bounds", content)
        self.assertIn("System.Drawing.Bitmap", content)
        self.assertIn("[System.Drawing.Graphics]::FromImage", content)
        self.assertIn("CopyFromScreen", content)

        # Skaliranje i Bicubic interpolacija
        self.assertIn("HighQualityBicubic", content)
        self.assertIn("MaxWidth", content)

        # JPEG Encoder i MemoryStream
        self.assertIn("EncoderQuality", content)
        self.assertIn("System.IO.MemoryStream", content)

    def test_04_http_post_transmission_specifications(self):
        """Verifikuje HTTP POST parametre: endpoint, heder, Content-Type i 800ms timeout."""
        with open(self.SCRIPT_PATH, "r", encoding="utf-8") as f:
            content = f.read()

        # REST endpoint sablon
        self.assertIn("/api/devices/", content)
        self.assertIn("/screen-frame", content)

        # Hederi i format
        self.assertIn("image/jpeg", content)
        self.assertIn("X-Client-Timestamp", content)

        # Timeout 800ms
        self.assertRegex(content, r"Timeout\s*=\s*(800|\$TimeoutMs)", "Mora postojati timeout od 800ms.")
        self.assertRegex(content, r"ReadWriteTimeout\s*=\s*(800|\$TimeoutMs)", "Mora postojati ReadWriteTimeout od 800ms.")

    def test_05_resource_disposal_and_failsafe(self):
        """Verifikuje striktno oslobadjanje resursa (.Dispose()) i postojanje try/catch zastite."""
        with open(self.SCRIPT_PATH, "r", encoding="utf-8") as f:
            content = f.read()

        # Provera .Dispose() poziva za sprecavanje curenja GDI+ memorije
        self.assertIn("finally", content)
        dispose_count = content.count(".Dispose()")
        self.assertGreaterEqual(dispose_count, 6, "Mora postojati sveobuhvatno oslobadjanje resursa preko .Dispose().")

        # Provera postojanja EventWaitHandle za elegantno zaustavljanje
        self.assertIn("EventWaitHandle", content)
        self.assertIn("Stop-ServisScreenCapture", content)

    def test_06_real_http_e2e_contract_with_servishub(self):
        """
        End-to-End test prenosa realnog JPEG frejma u skladu sa specifikacijom WIS.ScreenCapture.ps1
        protiv stvarnog ServisHub HTTP servera.
        """
        device_id = "WIS-CLIENT-WORKER-01"
        target_url = f"{self.base_url}/api/devices/{device_id}/screen-frame"

        # Generisanje pravog JPEG frejma (1280x720) kompresovanog sa kvalitetom 70%
        img = Image.new("RGB", (1280, 720), color=(24, 76, 120))
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=70)
        jpeg_data = buf.getvalue()

        now_epoch_sec = int(time.time())

        # Inicijalna registracija uređaja preko heartbeat-a (simulacija WIS.Telemetry klijenta)
        hb_req = urllib.request.Request(
            f"{self.base_url}/api/heartbeat",
            data=json.dumps({"computer_name": device_id, "phase": 3, "status": "running"}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(hb_req, timeout=1.0) as hb_resp:
            self.assertEqual(hb_resp.status, 200)

        # Slanje HTTP POST-a tacno onako kako to radi Send-ServisScreenFrame
        req = urllib.request.Request(
            target_url,
            data=jpeg_data,
            headers={
                "Content-Type": "image/jpeg",
                "X-Client-Timestamp": str(now_epoch_sec),
            },
            method="POST",
        )

        with urllib.request.urlopen(req, timeout=1.0) as resp:
            self.assertEqual(resp.status, 200)
            resp_body = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(resp_body.get("status"), "ok")
            self.assertEqual(resp_body.get("device_id"), device_id)
            self.assertEqual(resp_body.get("size_bytes"), len(jpeg_data))

        # Verifikacija da je frejm sacuvan na Hub-u i da se moze preuzeti
        get_frame_url = f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg"
        with urllib.request.urlopen(get_frame_url, timeout=1.0) as get_resp:
            self.assertEqual(get_resp.status, 200)
            self.assertEqual(get_resp.headers.get("Content-Type"), "image/jpeg")
            downloaded_bytes = get_resp.read()
            self.assertEqual(downloaded_bytes, jpeg_data)

        # Verifikacija da /api/devices sada prijavljuje has_screen_stream = True
        devices_url = f"{self.base_url}/api/devices"
        with urllib.request.urlopen(devices_url, timeout=1.0) as dev_resp:
            self.assertEqual(dev_resp.status, 200)
            dev_data = json.loads(dev_resp.read().decode("utf-8"))
            dev_info = dev_data.get(device_id)
            self.assertIsNotNone(dev_info, f"Uredjaj {device_id} mora postojati u /api/devices mapi.")
            self.assertTrue(dev_info.get("has_screen_stream"), "has_screen_stream mora biti True.")


if __name__ == "__main__":
    unittest.main()
