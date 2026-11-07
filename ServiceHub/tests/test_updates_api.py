"""
tests.test_updates_api
======================
Jedinični i integracioni testovi za ServisHub API ažuriranja (GET /api/updates/status).
Pokriva:
- Ekstrakciju KB identifikatora iz naziva paketa (.msu i .cab)
- Skeniranje nepostojećeg foldera (available=False)
- Skeniranje praznog foldera (available=True, total_packages=0)
- Skeniranje foldera sa validnim paketima i filtriranje privremenih fajlova (.part, .tmp)
- FastAPI ruter i TestClient integraciju
- Standardni ThreadingHTTPServer i ServisHubHandler HTTP prenos
"""

import os
import shutil
import tempfile
import threading
import urllib.request
import json
import unittest

from servishub.config import HubConfig
from servishub.handlers import ServisHubHandler
from servishub.models.updates import UpdatePackageItem, UpdatesStatusResponse
from servishub.api.updates import extract_kb, scan_updates_directory, resolve_updates_directory
from servishub.main import app
from servishub.version import VERSION


class TestUpdatesApi(unittest.TestCase):

    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="servishub_test_updates_")

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_01_extract_kb(self):
        """Proverava preciznost ekstrakcije KB brojeva iz različitih formata datoteka."""
        self.assertEqual(extract_kb("Windows11.0-KB5043080-x64.msu"), "KB5043080")
        self.assertEqual(extract_kb("windows11.0-kb5129195-x64.msu"), "KB5129195")
        self.assertEqual(extract_kb("windows11.0-kb5129242-x64.msu"), "KB5129242")
        self.assertEqual(extract_kb("KB5129242-amd64.cab"), "KB5129242")
        self.assertEqual(extract_kb("windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu"), "KB5043080")
        self.assertEqual(extract_kb("Update_KB1234567_v2.msu"), "KB1234567")
        self.assertIsNone(extract_kb("custom-driver-pack.cab"))
        self.assertIsNone(extract_kb("readme.txt"))

    def test_02_missing_directory(self):
        """Proverava ponašanje kada konfigurisan direktorijum ne postoji na disku."""
        missing_path = os.path.join(self.test_dir, "non_existent_folder_123")
        res = scan_updates_directory(missing_path)

        self.assertTrue(res.enabled)
        self.assertFalse(res.available)
        self.assertEqual(res.directory, os.path.abspath(missing_path))
        self.assertEqual(res.total_packages, 0)
        self.assertEqual(res.total_size_bytes, 0)
        self.assertEqual(res.total_size_mb, 0.0)
        self.assertEqual(res.kb_list, [])
        self.assertEqual(res.packages, [])

    def test_03_empty_directory(self):
        """Proverava ponašanje kada direktorijum postoji ali je prazan."""
        empty_path = os.path.join(self.test_dir, "empty_updates")
        os.makedirs(empty_path, exist_ok=True)

        res = scan_updates_directory(empty_path)

        self.assertTrue(res.enabled)
        self.assertTrue(res.available)
        self.assertEqual(res.directory, os.path.abspath(empty_path))
        self.assertEqual(res.total_packages, 0)
        self.assertEqual(res.total_size_bytes, 0)
        self.assertEqual(res.total_size_mb, 0.0)
        self.assertEqual(res.kb_list, [])
        self.assertEqual(res.packages, [])

    def test_04_directory_with_packages_and_ignored_files(self):
        """Proverava parsiranje paketa i ignorisanje nekompletnih fajlova (.part, .tmp)."""
        updates_dir = os.path.join(self.test_dir, "Win11")
        os.makedirs(updates_dir, exist_ok=True)

        # 1. Validni .msu paket 1 (~2 MB)
        file1 = os.path.join(updates_dir, "Windows11.0-KB5129195-x64.msu")
        with open(file1, "wb") as f:
            f.write(b"M" * (2 * 1024 * 1024))

        # 2. Validni .msu paket 2 (~1 MB)
        file2 = os.path.join(updates_dir, "windows11.0-kb5129242-x64.msu")
        with open(file2, "wb") as f:
            f.write(b"N" * (1 * 1024 * 1024))

        # 3. Validni .cab paket (~512 KB)
        file3 = os.path.join(updates_dir, "KB5043080-x64.cab")
        with open(file3, "wb") as f:
            f.write(b"C" * (512 * 1024))

        # 4. Privremeni nekompletni download (MORA BITI IGNORISAN)
        part_file = os.path.join(updates_dir, "windows11.0-kb5043080-x64.msu.part")
        with open(part_file, "wb") as f:
            f.write(b"PARTIAL")

        # 5. Pomoćni tekstualni fajl (MORA BITI IGNORISAN)
        txt_file = os.path.join(updates_dir, "checksums.txt")
        with open(txt_file, "w") as f:
            f.write("SHA256 checksums")

        # 6. Podfolder (MORA BITI IGNORISAN)
        sub_dir = os.path.join(updates_dir, "Archive")
        os.makedirs(sub_dir, exist_ok=True)

        res = scan_updates_directory(updates_dir)

        self.assertTrue(res.available)
        self.assertEqual(res.total_packages, 3)
        self.assertEqual(res.kb_list, ["KB5043080", "KB5129195", "KB5129242"])

        expected_bytes = (2 * 1024 * 1024) + (1 * 1024 * 1024) + (512 * 1024)
        self.assertEqual(res.total_size_bytes, expected_bytes)
        self.assertEqual(res.total_size_mb, round(expected_bytes / (1024 * 1024), 2))

        filenames = [p.filename for p in res.packages]
        self.assertIn("Windows11.0-KB5129195-x64.msu", filenames)
        self.assertIn("windows11.0-kb5129242-x64.msu", filenames)
        self.assertIn("KB5043080-x64.cab", filenames)
        self.assertNotIn("windows11.0-kb5043080-x64.msu.part", filenames)
        self.assertNotIn("checksums.txt", filenames)

        for p in res.packages:
            self.assertIn(p.file_type, ["msu", "cab"])
            self.assertIsNotNone(p.modified_at)
            self.assertGreater(p.modified_timestamp, 0)
            self.assertGreater(p.size_bytes, 0)

    def test_fastapi_reports_canonical_component_version(self):
        if app is None:
            self.skipTest("FastAPI nije instaliran u ovom okruženju")
        from starlette.testclient import TestClient
        with TestClient(app) as client:
            for endpoint in ("/api/health", "/api/status"):
                with self.subTest(endpoint=endpoint):
                    response = client.get(endpoint)
                    self.assertEqual(response.status_code, 200)
                    self.assertEqual(response.json()["version"], VERSION)
            self.assertEqual(client.get("/openapi.json").json()["info"]["version"], VERSION)

    def test_05_fastapi_client(self):
        """Proverava rad FastAPI rute GET /api/updates/status preko TestClient-a."""
        if app is None:
            self.skipTest("FastAPI nije instaliran u ovom okruženju")

        from starlette.testclient import TestClient
        client = TestClient(app)

        # Kreiranje mock direktorijuma
        mock_dir = os.path.join(self.test_dir, "fastapi_updates")
        os.makedirs(mock_dir, exist_ok=True)
        test_pkg = os.path.join(mock_dir, "Windows11.0-KB5129195-x64.msu")
        with open(test_pkg, "wb") as f:
            f.write(b"FASTAPI_TEST_DATA" * 1000)

        # Poziv sa query parametrom directory
        resp = client.get(f"/api/updates/status?directory={mock_dir}")
        self.assertEqual(resp.status_code, 200)
        data = resp.json()
        self.assertTrue(data["available"])
        self.assertEqual(data["total_packages"], 1)
        self.assertEqual(data["kb_list"], ["KB5129195"])
        self.assertEqual(data["packages"][0]["filename"], "Windows11.0-KB5129195-x64.msu")
        self.assertEqual(data["packages"][0]["file_type"], "msu")

        # Poziv za nepostojeći direktorijum
        resp_missing = client.get(f"/api/updates/status?directory=/tmp/non_existent_fastapi_dir")
        self.assertEqual(resp_missing.status_code, 200)
        self.assertFalse(resp_missing.json()["available"])

    def test_06_threading_http_server_handler(self):
        """Proverava GET /api/updates/status preko standardnog ServisHub ThreadingHTTPServer-a."""
        from http.server import ThreadingHTTPServer

        mock_dir = os.path.join(self.test_dir, "http_updates")
        os.makedirs(mock_dir, exist_ok=True)
        test_pkg = os.path.join(mock_dir, "windows11.0-kb5129242-x64.msu")
        with open(test_pkg, "wb") as f:
            f.write(b"HTTP_TEST_PAYLOAD" * 500)

        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            updates_dir=mock_dir,
        )

        ServisHubHandler.config = cfg
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]

        srv_thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        srv_thread.start()

        try:
            url = f"http://127.0.0.1:{port}/api/updates/status"
            with urllib.request.urlopen(url, timeout=5) as resp:
                self.assertEqual(resp.status, 200)
                data = json.loads(resp.read().decode("utf-8"))
                self.assertTrue(data.get("available"))
                self.assertEqual(data.get("total_packages"), 1)
                self.assertIn("KB5129242", data.get("kb_list", []))
                self.assertEqual(data["packages"][0]["filename"], "windows11.0-kb5129242-x64.msu")
        finally:
            httpd.shutdown()
            httpd.server_close()


if __name__ == "__main__":
    unittest.main()
