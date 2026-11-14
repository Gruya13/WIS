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

import hashlib
import io
import os
import shutil
import tarfile
import tempfile
import threading
import urllib.error
import urllib.request
import json
import unittest
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.drivers.cache import DriverCacheManager
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.handlers import ServisHubHandler
from servishub.models.updates import UpdatePackageItem, UpdatesStatusResponse
from servishub.api.updates import (
    extract_kb,
    extract_os_build,
    scan_updates_directory,
    resolve_updates_directory,
    find_update_package,
    save_uploaded_update_package,
    cache_kb_from_ms_catalog,
    prune_updates_cache,
)
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

    def test_07_find_and_save_uploaded_update_package(self):
        """Proverava validaciju, čuvanje i pretragu uploadovanih .msu i .cab paketa."""
        mock_dir = os.path.join(self.test_dir, "upload_unit")
        os.makedirs(mock_dir, exist_ok=True)

        msu_bytes = b"MSU_UNIT_PAYLOAD" * 200
        cab_bytes = b"CAB_UNIT_PAYLOAD" * 150

        saved_msu = save_uploaded_update_package(
            filename="Windows11.0-KB5053598-x64.msu",
            data=msu_bytes,
            directory=mock_dir,
        )
        self.assertEqual(saved_msu.filename, "Windows11.0-KB5053598-x64.msu")
        self.assertEqual(saved_msu.kb, "KB5053598")
        self.assertEqual(saved_msu.file_type, "msu")
        self.assertEqual(saved_msu.size_bytes, len(msu_bytes))
        self.assertTrue(os.path.isfile(saved_msu.path))

        saved_cab = save_uploaded_update_package(
            filename="package-without-kb.cab",
            data=cab_bytes,
            directory=mock_dir,
            kb="5053599",
        )
        self.assertEqual(saved_cab.file_type, "cab")
        self.assertEqual(saved_cab.kb, "KB5053599")
        self.assertIn("KB5053599", saved_cab.filename)

        found_by_name = find_update_package("Windows11.0-KB5053598-x64.msu", directory=mock_dir)
        self.assertIsNotNone(found_by_name)
        self.assertEqual(found_by_name.kb, "KB5053598")

        found_by_kb = find_update_package("kb5053598", directory=mock_dir)
        self.assertIsNotNone(found_by_kb)
        self.assertEqual(found_by_kb.filename, "Windows11.0-KB5053598-x64.msu")

        found_cab = find_update_package("KB5053599", directory=mock_dir)
        self.assertIsNotNone(found_cab)
        self.assertEqual(found_cab.file_type, "cab")

        self.assertIsNone(find_update_package("KB9999999", directory=mock_dir))

        with self.assertRaises(ValueError):
            save_uploaded_update_package("bad_installer.exe", msu_bytes, directory=mock_dir)

        with self.assertRaises(ValueError):
            save_uploaded_update_package("Windows11.0-KB5053598-x64.msu", b"", directory=mock_dir)

    def test_08_cache_kb_from_ms_catalog_mocked(self):
        """Proverava keširanje zakrpe iz mock Microsoft Catalog-a preko lokalnog HTTP servera."""
        mock_dir = os.path.join(self.test_dir, "catalog_cache")
        os.makedirs(mock_dir, exist_ok=True)
        payload = b"CATALOG_MSU_CONTENT" * 500

        class MockCdnHandler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format, *args):
                pass

        cdn_srv = ThreadingHTTPServer(("127.0.0.1", 0), MockCdnHandler)
        cdn_port = cdn_srv.server_address[1]
        cdn_thread = threading.Thread(target=cdn_srv.serve_forever, daemon=True)
        cdn_thread.start()

        class FakeScraper(MsUpdateCatalogScraper):
            def search(self, query, timeout=30):
                return [
                    {
                        "update_id": "bad-arm64",
                        "title": f"2025-03 Cumulative Update for Windows 11 ARM64 ({query})",
                        "products": "Windows 11",
                        "date": datetime(2025, 3, 11),
                        "version_parsed": (24, 2),
                        "size_bytes": 1000,
                    },
                    {
                        "update_id": "good-win11-x64",
                        "title": f"2025-03 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems ({query})",
                        "products": "Windows 11",
                        "date": datetime(2025, 3, 11),
                        "version_parsed": (24, 2),
                        "size_bytes": len(payload),
                    },
                ]

            def get_download_urls(self, update_id, timeout=30):
                if update_id == "good-win11-x64":
                    return [f"http://127.0.0.1:{cdn_port}/windows11.0-kb5053598-x64.msu"]
                return []

        try:
            scraper = FakeScraper()
            res = cache_kb_from_ms_catalog(
                kb="KB5053598",
                title="2025-03 Cumulative Update",
                directory=mock_dir,
                scraper=scraper,
                timeout=10,
            )
            self.assertEqual(res["status"], "cached")
            self.assertFalse(res["already_cached"])
            self.assertEqual(res["kb"], "KB5053598")
            self.assertEqual(res["package"]["size_bytes"], len(payload))

            found = find_update_package("KB5053598", directory=mock_dir)
            self.assertIsNotNone(found)
            with open(found.path, "rb") as f:
                self.assertEqual(f.read(), payload)

            res_cached = cache_kb_from_ms_catalog(
                kb="5053598",
                directory=mock_dir,
                scraper=scraper,
            )
            self.assertEqual(res_cached["status"], "cached")
            self.assertTrue(res_cached["already_cached"])
        finally:
            cdn_srv.shutdown()
            cdn_srv.server_close()

    def test_09_http_download_upload_and_cache_kb(self):
        """Proverava POST /upload, GET /download i POST /cache-kb na ServisHubHandler-u."""
        mock_dir = os.path.join(self.test_dir, "http_bidir")
        os.makedirs(mock_dir, exist_ok=True)
        catalog_payload = b"HUB_CATALOG_LCU_BYTES" * 400
        upload_payload = b"CLIENT_UPLOADED_MSU_BYTES" * 300

        class MockCdnHandler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", str(len(catalog_payload)))
                self.end_headers()
                self.wfile.write(catalog_payload)

            def log_message(self, format, *args):
                pass

        cdn_srv = ThreadingHTTPServer(("127.0.0.1", 0), MockCdnHandler)
        cdn_port = cdn_srv.server_address[1]
        threading.Thread(target=cdn_srv.serve_forever, daemon=True).start()

        class FakeHubScraper(MsUpdateCatalogScraper):
            def search(self, query, timeout=30):
                return [
                    {
                        "update_id": "hub-x64-id",
                        "title": f"Cumulative Update for Windows 11 x64 ({query})",
                        "products": "Windows 11",
                        "date": datetime(2025, 3, 12),
                        "version_parsed": (24, 2),
                        "size_bytes": len(catalog_payload),
                    }
                ]

            def get_download_urls(self, update_id, timeout=30):
                return [f"http://127.0.0.1:{cdn_port}/windows11.0-kb5052222-x64.msu"]

        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            updates_dir=mock_dir,
        )

        prev_cfg = getattr(ServisHubHandler, "config", None)
        prev_scraper = getattr(ServisHubHandler, "ms_catalog_scraper", None)
        ServisHubHandler.config = cfg
        ServisHubHandler.ms_catalog_scraper = FakeHubScraper()

        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        try:
            # 1. Binarni POST /api/updates/upload
            up_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/updates/upload?filename=Windows11.0-KB5051111-x64.msu&kb=KB5051111",
                data=upload_payload,
                method="POST",
                headers={
                    "Content-Type": "application/octet-stream",
                    "Content-Length": str(len(upload_payload)),
                },
            )
            with urllib.request.urlopen(up_req, timeout=10) as resp:
                self.assertEqual(resp.status, 200)
                up_json = json.loads(resp.read().decode("utf-8"))
                self.assertEqual(up_json.get("status"), "ok")
                self.assertEqual(up_json["package"]["kb"], "KB5051111")
                self.assertEqual(up_json["package"]["size_bytes"], len(upload_payload))

            # 2. Odbijanje nevalidne ekstenzije pri upload-u
            bad_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/updates/upload?filename=bad.exe",
                data=b"MZ123",
                method="POST",
                headers={"Content-Type": "application/octet-stream", "Content-Length": "5"},
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx:
                urllib.request.urlopen(bad_req, timeout=10)
            self.assertEqual(ctx.exception.code, 400)
            ctx.exception.close()

            # 3. GET /api/updates/download po imenu fajla i po KB broju
            with urllib.request.urlopen(
                f"http://127.0.0.1:{port}/api/updates/download?filename=Windows11.0-KB5051111-x64.msu",
                timeout=10,
            ) as dl_resp:
                self.assertEqual(dl_resp.status, 200)
                self.assertEqual(dl_resp.read(), upload_payload)

            with urllib.request.urlopen(
                f"http://127.0.0.1:{port}/api/updates/download?kb=kb5051111",
                timeout=10,
            ) as dl_kb_resp:
                self.assertEqual(dl_kb_resp.status, 200)
                self.assertEqual(dl_kb_resp.read(), upload_payload)

            # 4. GET /api/updates/download za nepostojeći paket vraća 404
            with self.assertRaises(urllib.error.HTTPError) as ctx404:
                urllib.request.urlopen(
                    f"http://127.0.0.1:{port}/api/updates/download?kb=KB9999999",
                    timeout=10,
                )
            self.assertEqual(ctx404.exception.code, 404)
            ctx404.exception.close()

            # 5. JSON POST /api/updates/cache-kb + naknadni GET /download
            cache_body = json.dumps({"kb": "KB5052222", "title": "Windows 11 24H2"}).encode("utf-8")
            cache_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/updates/cache-kb",
                data=cache_body,
                method="POST",
                headers={
                    "Content-Type": "application/json",
                    "Content-Length": str(len(cache_body)),
                },
            )
            with urllib.request.urlopen(cache_req, timeout=10) as c_resp:
                self.assertEqual(c_resp.status, 200)
                c_json = json.loads(c_resp.read().decode("utf-8"))
                self.assertEqual(c_json.get("status"), "cached")
                self.assertEqual(c_json.get("kb"), "KB5052222")

            with urllib.request.urlopen(
                f"http://127.0.0.1:{port}/api/updates/download?kb=KB5052222",
                timeout=10,
            ) as dl_cat:
                self.assertEqual(dl_cat.status, 200)
                self.assertEqual(dl_cat.read(), catalog_payload)
        finally:
            httpd.shutdown()
            httpd.server_close()
            cdn_srv.shutdown()
            cdn_srv.server_close()
            ServisHubHandler.config = prev_cfg
            ServisHubHandler.ms_catalog_scraper = prev_scraper

    def test_10_os_build_extraction_and_filtering(self):
        """Proverava ekstrakciju OSBuild broja, SHA-256 index.json, filtriranje po os_build i prune održavanje."""
        self.assertEqual(extract_os_build("Cumulative Update for Windows 11 24H2 x64"), "26100")
        self.assertEqual(extract_os_build("23H2"), "22631")
        self.assertEqual(extract_os_build("22621"), "22621")
        self.assertEqual(extract_os_build("Windows 11 25H2 (26200)"), "26200")
        self.assertIsNone(extract_os_build("Windows11.0-KB2610012-x64.msu"))

        upd_dir = os.path.join(self.test_dir, "build_filter_updates")
        os.makedirs(upd_dir, exist_ok=True)

        p24_a = save_uploaded_update_package(
            "Windows11.0-KB5053331-x64.msu",
            b"WIN11_24H2_PKG_A" * 64,
            directory=upd_dir,
            kb="KB5053331",
            os_build="24H2",
            title="2025-04 Cumulative Update for Windows 11 Version 24H2",
        )
        p24_b = save_uploaded_update_package(
            "Windows11.0-KB5053339-x64.msu",
            b"WIN11_24H2_PKG_B" * 64,
            directory=upd_dir,
            kb="KB5053339",
            os_build="26100",
        )
        p23 = save_uploaded_update_package(
            "Windows11.0-KB5053332-23H2-x64.msu",
            b"WIN11_23H2_PKG" * 64,
            directory=upd_dir,
            kb="KB5053332",
        )
        p_neutral = save_uploaded_update_package(
            "Windows11.0-KB5053333-x64.cab",
            b"NEUTRAL_CAB" * 64,
            directory=upd_dir,
            kb="KB5053333",
        )

        self.assertEqual(p24_a.os_build, "26100")
        self.assertEqual(p24_a.sha256, hashlib.sha256(b"WIN11_24H2_PKG_A" * 64).hexdigest())
        self.assertEqual(p23.os_build, "22631")
        self.assertIsNone(p_neutral.os_build)
        self.assertTrue(os.path.isfile(os.path.join(upd_dir, "index.json")))

        # Filtriranje na 26100 vraća p24_a, p24_b i p_neutral (bez p23)
        scan_26100 = scan_updates_directory(upd_dir, os_build="26100")
        names_26100 = {p.filename for p in scan_26100.packages}
        self.assertIn(p24_a.filename, names_26100)
        self.assertIn(p24_b.filename, names_26100)
        self.assertIn(p_neutral.filename, names_26100)
        self.assertNotIn(p23.filename, names_26100)

        # Dodavanje zaostalog .tmp fajla i testiranje HTTP /status?os_build= + POST /prune
        tmp_abandoned = os.path.join(upd_dir, "abandoned.msu.tmp")
        with open(tmp_abandoned, "wb") as f:
            f.write(b"OLD_TMP")

        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            updates_dir=upd_dir,
        )
        prev_cfg = getattr(ServisHubHandler, "config", None)
        ServisHubHandler.config = cfg
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/updates/status?os_build=26100", timeout=10) as resp:
                self.assertEqual(resp.status, 200)
                st_json = json.loads(resp.read().decode("utf-8"))
                http_names = {p["filename"] for p in st_json["packages"]}
                self.assertEqual(http_names, names_26100)

            prune_body = json.dumps({"max_packages_per_build": 1, "min_temp_age_seconds": 0}).encode("utf-8")
            prune_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/updates/prune",
                data=prune_body,
                method="POST",
                headers={"Content-Type": "application/json", "Content-Length": str(len(prune_body))},
            )
            with urllib.request.urlopen(prune_req, timeout=10) as presp:
                self.assertEqual(presp.status, 200)
                pjson = json.loads(presp.read().decode("utf-8"))
                self.assertIn("abandoned.msu.tmp", pjson["removed_temps"])
                self.assertEqual(len(pjson["removed_packages"]), 1)
                self.assertFalse(os.path.exists(tmp_abandoned))
        finally:
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg

    def test_11_model_pack_status_upload_and_download(self):
        """Proverava dvosmerni HTTP model-pack API (/api/drivers/model-pack/status, upload, download)."""
        packs_dir = os.path.join(self.test_dir, "model_packs")
        os.makedirs(packs_dir, exist_ok=True)

        tar_buf = io.BytesIO()
        inf_bytes = b"[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Net\r\n" * 20
        with tarfile.open(fileobj=tar_buf, mode="w") as tf:
            ti = tarfile.TarInfo(name="netadapter.inf")
            ti.size = len(inf_bytes)
            tf.addfile(ti, io.BytesIO(inf_bytes))
        tar_payload = tar_buf.getvalue()
        expected_sha = hashlib.sha256(tar_payload).hexdigest()

        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            model_packs_dir=packs_dir,
        )
        prev_cfg = getattr(ServisHubHandler, "config", None)
        ServisHubHandler.config = cfg
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        try:
            q_model = urllib.request.quote("ThinkPad T14 Gen 3")
            # 1. Status pre upload-a -> available=False
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/model-pack/status?model={q_model}", timeout=10) as resp:
                self.assertEqual(resp.status, 200)
                st0 = json.loads(resp.read().decode("utf-8"))
                self.assertFalse(st0["available"])

            # 2. Premali upload (< 512 B) mora biti odbijen sa 400
            bad_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model={q_model}",
                data=b"TINY_TAR",
                method="POST",
                headers={"Content-Type": "application/octet-stream", "Content-Length": "8"},
            )
            with self.assertRaises(urllib.error.HTTPError) as bad_ctx:
                urllib.request.urlopen(bad_req, timeout=10)
            self.assertEqual(bad_ctx.exception.code, 400)
            bad_ctx.exception.close()

            # 3. Validan binarni upload <Model>.tar
            up_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model={q_model}",
                data=tar_payload,
                method="POST",
                headers={"Content-Type": "application/octet-stream", "Content-Length": str(len(tar_payload))},
            )
            with urllib.request.urlopen(up_req, timeout=10) as up_resp:
                self.assertEqual(up_resp.status, 200)
                up_json = json.loads(up_resp.read().decode("utf-8"))
                self.assertEqual(up_json["status"], "ok")
                self.assertEqual(up_json["sha256"], expected_sha)
                self.assertEqual(up_json["size_bytes"], len(tar_payload))

            # 4. Status posle upload-a -> available=True
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/model-pack/status?model={q_model}", timeout=10) as resp2:
                st1 = json.loads(resp2.read().decode("utf-8"))
                self.assertTrue(st1["available"])
                self.assertEqual(st1["sha256"], expected_sha)

            # 5. Preuzimanje model paketa preko HTTP GET
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/model-pack/download?model={q_model}", timeout=10) as dl_resp:
                self.assertEqual(dl_resp.status, 200)
                self.assertEqual(dl_resp.read(), tar_payload)
        finally:
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg

    def test_12_driver_resolve_early_stop_and_batch(self):
        """Proverava early-stop, cache-first i paralelnu 'devices' batch rezoluciju na /api/drivers/resolve."""
        cache_dir = os.path.join(self.test_dir, "drivers_cache")
        cache_mgr = DriverCacheManager(cache_dir)
        resolve_calls = []
        calls_lock = threading.Lock()

        class FakeResolveScraper(MsUpdateCatalogScraper):
            def resolve_driver(self, hwid):
                with calls_lock:
                    resolve_calls.append(hwid)
                return {
                    "title": f"Driver for {hwid}",
                    "version": "10.0.1.5",
                    "date_str": "04/01/2025",
                    "device_class": "Net",
                    "download_url": f"http://mock.local/{hwid.replace(chr(92), '_')}.cab",
                    "matched_query": hwid,
                }

            def download_cab(self, url):
                return b"MSCF" + (b"CAB_DATA" * 80)

        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
        )
        prev_cfg = getattr(ServisHubHandler, "config", None)
        prev_cache = getattr(ServisHubHandler, "driver_cache_mgr", None)
        prev_scraper = getattr(ServisHubHandler, "ms_catalog_scraper", None)
        ServisHubHandler.config = cfg
        ServisHubHandler.driver_cache_mgr = cache_mgr
        ServisHubHandler.ms_catalog_scraper = FakeResolveScraper()

        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        try:
            # 1. Single device sa 2 HWID-a -> mora stati na prvom uspešnom HWID-u (early-stop)
            body1 = json.dumps({
                "device_name": "Realtek LAN",
                "hwids": ["PCI\\VEN_10EC&DEV_8168", "PCI\\VEN_10EC&CC_0200"],
            }).encode("utf-8")
            req1 = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/resolve",
                data=body1,
                method="POST",
                headers={"Content-Type": "application/json", "Content-Length": str(len(body1))},
            )
            with urllib.request.urlopen(req1, timeout=10) as r1:
                j1 = json.loads(r1.read().decode("utf-8"))
                self.assertEqual(j1["status"], "ok")
                self.assertEqual(len(j1["results"]), 1)
                self.assertEqual(j1["results"][0]["source"], "catalog_download")
                self.assertEqual(resolve_calls, ["PCI\\VEN_10EC&DEV_8168"])

            # 2. Ponovni upit za alias HWID -> cache hit bez dodatnih resolve_driver poziva
            body2 = json.dumps({
                "device_name": "Realtek LAN",
                "hwids": ["PCI\\VEN_10EC&CC_0200"],
            }).encode("utf-8")
            req2 = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/resolve",
                data=body2,
                method="POST",
                headers={"Content-Type": "application/json", "Content-Length": str(len(body2))},
            )
            with urllib.request.urlopen(req2, timeout=10) as r2:
                j2 = json.loads(r2.read().decode("utf-8"))
                self.assertEqual(j2["results"][0]["source"], "cache")
                self.assertEqual(len(resolve_calls), 1)

            # 3. Batch 'devices' zahtev za više uređaja odjednom
            batch_body = json.dumps({
                "devices": [
                    {"device_name": "Audio", "hwids": ["HDAUDIO\\FUNC_01", "HDAUDIO\\GENERIC"]},
                    {"device_name": "WiFi", "hwids": ["PCI\\VEN_8086&DEV_51F0", "PCI\\VEN_8086&CC_0280"]},
                ]
            }).encode("utf-8")
            req3 = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/resolve",
                data=batch_body,
                method="POST",
                headers={"Content-Type": "application/json", "Content-Length": str(len(batch_body))},
            )
            with urllib.request.urlopen(req3, timeout=10) as r3:
                j3 = json.loads(r3.read().decode("utf-8"))
                self.assertEqual(j3["resolved_count"], 2)
                self.assertEqual(len(j3["devices"]), 2)
                self.assertEqual({d["device_name"] for d in j3["devices"]}, {"Audio", "WiFi"})
                # Svaki novi uređaj stao je na svom prvom HWID-u -> ukupno 3 resolve poziva (1 + 2)
                self.assertEqual(len(resolve_calls), 3)
        finally:
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg
            ServisHubHandler.driver_cache_mgr = prev_cache
            ServisHubHandler.ms_catalog_scraper = prev_scraper


if __name__ == "__main__":
    unittest.main()
