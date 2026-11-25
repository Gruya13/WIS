"""
tests.test_v354_oem_and_guard
=============================
Jedinični i integracioni testovi za WIS v3.5.4 / ServiceHub v3.7.1:
- Celina C: Dockerfile (`msitools`), 6 GiB limit, prošireni `ALLOWED_OEM_HOST_SUFFIXES`,
  `TIER1_OEM_VENDORS` / `TIER2_DROPIN_VENDORS`, `detect_oem_vendor`, `MicrosoftOEMProvider`,
  `extract_msi_drivers`, `FujitsuOEMProvider` (XXE + HTTPS allowlist), `DropInOEMProvider`
  (path traversal + symlinks + Tier 2 + Tier 1 fallback + lokalno kopiranje bez HTTP-a).
- `wis_nas_space_guard.py`: `BUNDLES_DIR`, `TMP_SCAN_DIRS`, čišćenje siročadi
  (`.tmp`, `.part`, `.dedup.tmp`, `.harvest.lock`, `.tar.tmp.<pid>`) starijih od 30 min
  u `NORMAL` režimu uz host `lsof` zaštitu za `smbd` / `python3`.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import SimpleNamespace
from unittest.mock import patch

from servishub.drivers.oem import (
    BaseOEMProvider,
    DropInOEMProvider,
    FujitsuOEMProvider,
    MicrosoftOEMProvider,
    OemCatalogManager,
    TIER1_OEM_VENDORS,
    TIER2_DROPIN_VENDORS,
    detect_oem_vendor,
    extract_msi_drivers,
    get_oem_provider,
    parse_fujitsu_catalog_xml,
)
from servishub.drivers.oem.catalogs import (
    ALLOWED_OEM_HOST_SUFFIXES,
    MAX_OEM_PACKAGE_BYTES,
    validate_oem_download_url,
)

SCRIPTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "scripts"))
if SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, SCRIPTS_DIR)

import wis_nas_space_guard  # noqa: E402


class TestV354OemConstantsAndDetection(unittest.TestCase):
    """Testovi za konstante, Dockerfile, URL allowlist i detekciju vendora."""

    def test_constants_and_dockerfile(self):
        self.assertEqual(MAX_OEM_PACKAGE_BYTES, 6 * 1024 * 1024 * 1024)
        self.assertEqual(TIER1_OEM_VENDORS, ("dell", "hp", "lenovo", "microsoft", "fujitsu"))
        self.assertEqual(TIER2_DROPIN_VENDORS, ("asus", "acer", "msi", "dynabook"))
        for host in ("download.microsoft.com", ".microsoft.com", "support.ts.fujitsu.com"):
            self.assertIn(host, ALLOWED_OEM_HOST_SUFFIXES)

        dockerfile_path = os.path.abspath(
            os.path.join(os.path.dirname(__file__), "..", "Dockerfile")
        )
        with open(dockerfile_path, "r", encoding="utf-8") as f:
            df = f.read()
        self.assertIn("msitools", df)

    def test_validate_oem_download_url_new_domains(self):
        self.assertEqual(
            validate_oem_download_url("https://download.microsoft.com/download/a/b/SurfacePro9.msi"),
            "https://download.microsoft.com/download/a/b/SurfacePro9.msi",
        )
        self.assertEqual(
            validate_oem_download_url("https://surface.microsoft.com/pack.msi"),
            "https://surface.microsoft.com/pack.msi",
        )
        self.assertEqual(
            validate_oem_download_url("https://support.ts.fujitsu.com/DeskUpdate/pack.zip"),
            "https://support.ts.fujitsu.com/DeskUpdate/pack.zip",
        )
        with self.assertRaises(ValueError):
            validate_oem_download_url("http://download.microsoft.com/SurfacePro9.msi")
        with self.assertRaises(ValueError):
            validate_oem_download_url("https://evil.example.com/SurfacePro9.msi")

    def test_detect_oem_vendor_all_tiers_and_boundaries(self):
        # Postojeći Tier 1
        self.assertEqual(detect_oem_vendor("Dell Inc.", "Latitude 5420"), "dell")
        self.assertEqual(detect_oem_vendor("HP", "EliteBook 840 G8"), "hp")
        self.assertEqual(detect_oem_vendor("Lenovo", "ThinkPad T14"), "lenovo")

        # Microsoft Surface vs VM
        self.assertEqual(detect_oem_vendor("Microsoft Corporation", "Surface Pro 9"), "microsoft")
        self.assertEqual(detect_oem_vendor("", "Surface Laptop 5"), "microsoft")
        self.assertEqual(detect_oem_vendor("Microsoft Corporation", "Virtual Machine"), "")

        # MSI vs Microsoft (bez mešanja MICRO-STAR i lažnih MSI podstringova)
        self.assertEqual(detect_oem_vendor("MICRO-STAR INTERNATIONAL CO., LTD", "GF63 Thin"), "msi")
        self.assertEqual(detect_oem_vendor("MSI", "Prestige 14"), "msi")
        self.assertEqual(detect_oem_vendor("Generic", "MSIX_Installer_Device"), "")

        # Fujitsu
        self.assertEqual(detect_oem_vendor("FUJITSU", "LIFEBOOK E5411"), "fujitsu")
        self.assertEqual(detect_oem_vendor("", "ESPRIMO D7011"), "fujitsu")
        self.assertEqual(detect_oem_vendor("", "CELSIUS H7510"), "fujitsu")

        # ASUS (bez lažnog pogotka na 'ROG' u 'PROGRAM')
        self.assertEqual(detect_oem_vendor("ASUSTeK COMPUTER INC.", "ZenBook UX425"), "asus")
        self.assertEqual(detect_oem_vendor("", "ExpertBook B9"), "asus")
        self.assertEqual(detect_oem_vendor("", "TUF Gaming F15"), "asus")
        self.assertEqual(detect_oem_vendor("", "ROG Zephyrus G14"), "asus")
        self.assertEqual(detect_oem_vendor("Generic", "PROGRAMMER_BOARD"), "")

        # Acer (bez lažnog pogotka u 'TRACER')
        self.assertEqual(detect_oem_vendor("Acer", "TravelMate P215"), "acer")
        self.assertEqual(detect_oem_vendor("", "Aspire 5"), "acer")
        self.assertEqual(detect_oem_vendor("", "Nitro 5"), "acer")
        self.assertEqual(detect_oem_vendor("Generic", "TRACER_PC"), "")

        # Dynabook / Toshiba
        self.assertEqual(detect_oem_vendor("Dynabook Inc.", "TECRA A50-J"), "dynabook")
        self.assertEqual(detect_oem_vendor("TOSHIBA", "PORTEGE Z30"), "dynabook")

    def test_get_oem_provider_factory(self):
        self.assertIsInstance(get_oem_provider("Microsoft Surface"), MicrosoftOEMProvider)
        self.assertIsInstance(get_oem_provider("Microsoft Corporation", model="Surface Pro 9"), MicrosoftOEMProvider)
        self.assertIsNone(get_oem_provider("Microsoft Corporation"))
        self.assertIsInstance(get_oem_provider("FUJITSU"), FujitsuOEMProvider)
        self.assertIsInstance(get_oem_provider("Fujitsu"), BaseOEMProvider)
        # Tier 2 ostaje None u factory funkciji (ide preko OemCatalogManager / DropInOEMProvider)
        for t2 in ("ASUS", "Acer", "MSI", "Toshiba", "Dynabook"):
            self.assertIsNone(get_oem_provider(t2))


class TestMicrosoftSurfaceAndMsiExtract(unittest.TestCase):
    """Testovi za MicrosoftOEMProvider, SystemSKU mapiranje i extract_msi_drivers."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_ms_test_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_extract_msi_drivers_whitelist_and_finally_cleanup(self):
        msi_path = os.path.join(self.tmp_dir, "SurfacePro9.msi")
        with open(msi_path, "wb") as f:
            f.write(b"MOCK_MSI_BINARY")
        dest_dir = os.path.join(self.tmp_dir, "out_drivers")

        created_tmp_dirs = []

        def fake_run(cmd, capture_output=True, text=True, timeout=600, check=False):
            self.assertEqual(os.path.basename(cmd[0]), "msiextract")
            self.assertEqual(cmd[1], "-C")
            tmp_out = cmd[2]
            created_tmp_dirs.append(tmp_out)
            drv_dir = os.path.join(tmp_out, "SourceDir", "SurfaceUpdate", "Audio")
            os.makedirs(drv_dir, exist_ok=True)
            # Dozvoljene ekstenzije
            for ext in (".inf", ".sys", ".cat", ".dll", ".bin", ".dat", ".fw", ".icm", ".efi", ".cap", ".rom", ".man", ".ini"):
                with open(os.path.join(drv_dir, f"drv{ext}"), "wb") as df:
                    df.write(b"DRIVER_DATA")
            # Zabranjene izvršne i skript ekstenzije
            for bad_ext in (".exe", ".msi", ".ps1", ".bat", ".cmd", ".vbs", ".js"):
                with open(os.path.join(drv_dir, f"payload{bad_ext}"), "wb") as bf:
                    bf.write(b"EVIL_CODE")
            return SimpleNamespace(returncode=0, stdout="ok", stderr="")

        with patch("shutil.which", return_value="/usr/bin/msiextract"), \
             patch("subprocess.run", side_effect=fake_run):
            extracted = extract_msi_drivers(msi_path, dest_dir)

        self.assertEqual(len(extracted), 13)
        self.assertEqual(len(created_tmp_dirs), 1)
        self.assertFalse(os.path.exists(created_tmp_dirs[0]), "Temp direktorijum mora biti obrisan u finally!")

        extracted_names = {os.path.basename(p) for p in extracted}
        self.assertIn("drv.inf", extracted_names)
        self.assertIn("drv.sys", extracted_names)
        self.assertIn("drv.cat", extracted_names)
        for bad_ext in (".exe", ".msi", ".ps1", ".bat", ".cmd", ".vbs", ".js"):
            self.assertNotIn(f"payload{bad_ext}", extracted_names)

        # Čak i kada msiextract padne, temp dir se briše u finally
        def failing_run(cmd, **kwargs):
            created_tmp_dirs.append(cmd[2])
            return SimpleNamespace(returncode=1, stdout="", stderr="msiextract failed")

        with patch("shutil.which", return_value="/usr/bin/msiextract"), \
             patch("subprocess.run", side_effect=failing_run):
            with self.assertRaises(RuntimeError):
                extract_msi_drivers(msi_path, dest_dir)
        self.assertFalse(os.path.exists(created_tmp_dirs[-1]))

    def test_surface_system_sku_and_os_build_distinction(self):
        cat_mgr = OemCatalogManager(catalogs_dir=os.path.join(self.tmp_dir, "cats"))
        sync_res = cat_mgr.sync_catalog("microsoft", force=True)
        self.assertEqual(sync_res["status"], "synced")
        self.assertGreaterEqual(sync_res["entries_count"], 10)

        # Intel vs ARM64 po SystemSKU
        intel_hit = cat_mgr.find_driver_pack(
            manufacturer="Microsoft Corporation",
            model="Surface Pro 11th Edition",
            os_ver="win11-24h2",
            system_sku="Surface_Pro_11th_Edition_for_Business_2110",
        )
        arm_hit = cat_mgr.find_driver_pack(
            manufacturer="Microsoft Corporation",
            model="Surface Pro 11th Edition",
            os_ver="win11-24h2",
            system_sku="Surface_Pro_11th_Edition_2076",
        )
        self.assertIsNotNone(intel_hit)
        self.assertIsNotNone(arm_hit)
        self.assertEqual(intel_hit["arch"], "x64")
        self.assertEqual(arm_hit["arch"], "arm64")

        # OS build selekcija za Surface Pro 9 (22621 vs 26100)
        sp9_24h2 = cat_mgr.find_driver_pack(
            manufacturer="Microsoft Corporation",
            model="Surface Pro 9",
            os_ver="26100",
            system_sku="Surface_Pro_9_2038",
        )
        sp9_22h2 = cat_mgr.find_driver_pack(
            manufacturer="Microsoft Corporation",
            model="Surface Pro 9",
            os_ver="22621",
            system_sku="Surface_Pro_9_2038",
        )
        self.assertIsNotNone(sp9_24h2)
        self.assertIsNotNone(sp9_22h2)
        self.assertEqual(sp9_24h2["os_build"], "26100")
        self.assertEqual(sp9_22h2["os_build"], "22621")

    def test_provider_contracts(self):
        p = MicrosoftOEMProvider(cache_dir=os.path.join(self.tmp_dir, "ms_cache"))
        res = p.resolve_driver_pack("Surface Laptop 5", os_ver="win11-24h2", system_sku="Surface_Laptop_5_1950")
        self.assertEqual(res["status"], "ok")
        self.assertTrue(res["package_name"].endswith(".msi"))
        self.assertTrue(res["download_url"].startswith("https://download.microsoft.com/"))
        bios = p.check_bios_update("Surface Laptop 5", current_bios="1.0.0")
        self.assertTrue(bios["update_available"])


class TestFujitsuAndDropIn(unittest.TestCase):
    """Testovi za FujitsuOEMProvider (XXE + allowlist) i DropInOEMProvider (path traversal + lokalno kopiranje)."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_fj_dropin_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_fujitsu_xxe_and_url_allowlist(self):
        xxe_xml = b'<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><DeskUpdateCatalog/>'
        with self.assertRaises(ValueError):
            parse_fujitsu_catalog_xml(xxe_xml)

        bad_http_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<DeskUpdateCatalog>
  <DriverPackage id="FTS1" version="1.0">
    <Url>http://support.ts.fujitsu.com/bad.zip</Url>
    <Models><Model name="LIFEBOOK E5411"/></Models>
  </DriverPackage>
</DeskUpdateCatalog>'''
        with self.assertRaises(ValueError):
            parse_fujitsu_catalog_xml(bad_http_xml)

        bad_host_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<DeskUpdateCatalog>
  <DriverPackage id="FTS2" version="1.0">
    <Url>https://evil.example.com/bad.zip</Url>
    <Models><Model name="LIFEBOOK E5411"/></Models>
  </DriverPackage>
</DeskUpdateCatalog>'''
        with self.assertRaises(ValueError):
            parse_fujitsu_catalog_xml(bad_host_xml)

        good_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<DeskUpdateCatalog>
  <DriverPackage id="FTS_E5411" version="2.1.0" releaseDate="2025-02-01" size="1234567">
    <PackageName>FTS_LIFEBOOK_E5411_W11.zip</PackageName>
    <DownloadUrl>https://support.ts.fujitsu.com/DriverPacks/FTS_LIFEBOOK_E5411_W11.zip</DownloadUrl>
    <SupportedOperatingSystems>
      <OperatingSystem name="Windows 11 24H2" arch="x64"/>
    </SupportedOperatingSystems>
    <SupportedSystems>
      <Model name="FUJITSU LIFEBOOK E5411" systemId="FJNB2E1"/>
    </SupportedSystems>
  </DriverPackage>
</DeskUpdateCatalog>'''
        entries = parse_fujitsu_catalog_xml(good_xml)
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["vendor"], "fujitsu")
        self.assertEqual(entries[0]["os"], "win11")
        self.assertEqual(entries[0]["os_feature_rank"], 242)

        # HTTP sync test preko lokalnog servera
        class _FjHandler(BaseHTTPRequestHandler):
            def log_message(self, fmt, *args):
                return

            def do_GET(self):
                if self.path == "/deskupdate.xml":
                    body = good_xml.replace(
                        b"https://support.ts.fujitsu.com/DriverPacks/FTS_LIFEBOOK_E5411_W11.zip",
                        f"http://127.0.0.1:{self.server.server_address[1]}/FTS_LIFEBOOK_E5411_W11.zip".encode("utf-8"),
                    )
                    self.send_response(200)
                    self.send_header("Content-Type", "application/xml")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:
                    self.send_response(404)
                    self.end_headers()

        srv = ThreadingHTTPServer(("127.0.0.1", 0), _FjHandler)
        port = srv.server_address[1]
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(lambda: (srv.shutdown(), srv.server_close()))

        cat_mgr = OemCatalogManager(
            catalogs_dir=os.path.join(self.tmp_dir, "cats"),
            catalog_urls={"fujitsu": f"http://127.0.0.1:{port}/deskupdate.xml"},
            allowed_hosts=["127.0.0.1"],
        )
        sync_res = cat_mgr.sync_catalog("fujitsu", force=True)
        self.assertEqual(sync_res["status"], "synced")
        self.assertEqual(sync_res["entries_count"], 1)
        hit = cat_mgr.find_driver_pack("FUJITSU", "LIFEBOOK E5411", os_ver="win11")
        self.assertIsNotNone(hit)
        self.assertEqual(hit["package_id"], "FTS_E5411")

    def test_dropin_path_traversal_symlinks_and_local_copy(self):
        dropin_root = os.path.join(self.tmp_dir, "oem_dropin")
        asus_dir = os.path.join(dropin_root, "asus", "ZenBook_UX425")
        os.makedirs(asus_dir, exist_ok=True)
        pkg_file = os.path.join(asus_dir, "ZenBook_UX425_Win11.zip")
        with open(pkg_file, "wb") as f:
            f.write(b"PK\x03\x04_FAKE_ASUS_ZIP_" * 30)

        # Symlink napad unutar drop-in foldera mora biti ignorisan
        outside_secret = os.path.join(self.tmp_dir, "secret.zip")
        with open(outside_secret, "wb") as f:
            f.write(b"SECRET")
        sym_link = os.path.join(asus_dir, "evil_symlink.zip")
        os.symlink(outside_secret, sym_link)

        provider = DropInOEMProvider(vendor="asus", dropin_dir=dropin_root)

        # Path traversal u resolve_safe_subdir mora baciti ValueError
        for bad_path in ("../../etc", "../secret", "/etc/passwd", "foo/../../bar"):
            with self.assertRaises(ValueError):
                provider.resolve_safe_subdir("asus", bad_path)
            bad_res = provider.resolve_driver_pack(bad_path)
            self.assertEqual(bad_res["status"], "not_found")
            self.assertEqual(bad_res["local_path"], "")

        # Validno pronalaženje i kopiranje preko OemCatalogManager bez HTTP zahteva
        cat_mgr = OemCatalogManager(
            catalogs_dir=os.path.join(self.tmp_dir, "cats"),
            dropin_dir=dropin_root,
        )
        sync_t2 = cat_mgr.sync_catalog("asus", force=True)
        self.assertEqual(sync_t2["status"], "synced")
        self.assertEqual(sync_t2["entries_count"], 1)

        found = cat_mgr.find_driver_pack("ASUSTeK COMPUTER INC.", "ZenBook UX425", os_ver="win11")
        self.assertIsNotNone(found)
        self.assertEqual(found["package_name"], "ZenBook_UX425_Win11.zip")
        self.assertEqual(found["local_path"], os.path.realpath(pkg_file))

        dl_dest = os.path.join(self.tmp_dir, "dl_dest")
        with patch("urllib.request.urlopen") as mock_urlopen:
            copied_path = cat_mgr.download_driver_pack(found, dl_dest)
            mock_urlopen.assert_not_called()

        self.assertTrue(os.path.isfile(copied_path))
        self.assertEqual(os.path.basename(copied_path), "ZenBook_UX425_Win11.zip")


class TestWisNasSpaceGuard(unittest.TestCase):
    """Testovi za ServiceHub/scripts/wis_nas_space_guard.py."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_space_guard_")
        self.bundles_dir = os.path.join(self.tmp_dir, "bundles")
        self.archives_dir = os.path.join(self.tmp_dir, "Archives")
        self.drivers_cache_dir = os.path.join(self.tmp_dir, "drivers_cache")
        for d in (self.bundles_dir, self.archives_dir, self.drivers_cache_dir):
            os.makedirs(d, exist_ok=True)

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_default_paths_constants(self):
        self.assertEqual(wis_nas_space_guard.BUNDLES_DIR, "/mnt/TK_NAS/WIS_Cache/bundles")
        for expected in (
            "/mnt/TK_NAS/WIS_Cache/bundles",
            "/mnt/TK_NAS/WIS_Cache/drivers_cache",
            "/mnt/TK_NAS/WIS/shared/Drivers/Harvested/Archives",
            "/mnt/TK_NAS/WIS/shared/Drivers/Harvested",
            "/mnt/TK_NAS/ServiceHub/data",
        ):
            self.assertIn(expected, wis_nas_space_guard.TMP_SCAN_DIRS)

    def test_normal_mode_orphan_cleanup_and_lsof_protection(self):
        now = time.time()
        old_ts = now - 2400  # 40 minuta (> 1800s)
        fresh_ts = now - 300  # 5 minuta (< 1800s)

        # Stari siročići koji treba da budu obrisani u NORMAL režimu
        old_tmp = os.path.join(self.bundles_dir, "abc123.tar.521f90.tmp")
        old_part = os.path.join(self.drivers_cache_dir, "drv.cab.part")
        old_dedup = os.path.join(self.drivers_cache_dir, "pool_index.json.dedup.tmp")
        old_lock = os.path.join(self.archives_dir, "HP_ZBook.harvest.lock")
        old_tar_tmp = os.path.join(self.archives_dir, "HP_ZBook.tar.tmp.5044")

        # Stari siročad koji je OTVOREN od smbd (simulirano preko lsof) -> NE SME biti obrisan
        smbd_open_tmp = os.path.join(self.archives_dir, "Latitude_5430.tar.tmp.9999")
        # Sveži tmp fajl (< 30 min) -> NE SME biti obrisan
        fresh_tmp = os.path.join(self.bundles_dir, "fresh.tar.tmp")
        # Regularni bundle .tar u NORMAL režimu -> NE SME biti obrisan
        regular_tar = os.path.join(self.bundles_dir, "valid_bundle.tar")

        for fp in (old_tmp, old_part, old_dedup, old_lock, old_tar_tmp, smbd_open_tmp, fresh_tmp, regular_tar):
            with open(fp, "wb") as f:
                f.write(b"DATA_CONTENT" * 10)

        for fp in (old_tmp, old_part, old_dedup, old_lock, old_tar_tmp, smbd_open_tmp, regular_tar):
            os.utime(fp, (old_ts, old_ts))
        os.utime(fresh_tmp, (fresh_ts, fresh_ts))

        def mock_disk_usage(_path):
            return SimpleNamespace(total=500 * (1024 ** 3), used=260 * (1024 ** 3), free=240 * (1024 ** 3))

        def mock_lsof(paths):
            return {os.path.realpath(smbd_open_tmp): "smbd"}, True

        res = wis_nas_space_guard.run_space_guard(
            bundles_dir=self.bundles_dir,
            tmp_scan_dirs=(self.bundles_dir, self.archives_dir, self.drivers_cache_dir),
            now=now,
            open_files_fn=mock_lsof,
            disk_usage_fn=mock_disk_usage,
        )

        self.assertEqual(res["mode"], "NORMAL")
        self.assertEqual(res["orphan_tmp_removed"], 5)
        self.assertEqual(res["skipped_open"], 1)
        for deleted_fp in (old_tmp, old_part, old_dedup, old_lock, old_tar_tmp):
            self.assertFalse(os.path.exists(deleted_fp), f"Trebalo je obrisati: {deleted_fp}")
        self.assertTrue(os.path.exists(smbd_open_tmp), "Fajl otvoren od smbd ne sme biti obrisan!")
        self.assertTrue(os.path.exists(fresh_tmp), "Svež .tmp fajl (< 30 min) ne sme biti obrisan!")
        self.assertTrue(os.path.exists(regular_tar), "Regularni .tar u NORMAL režimu ne sme biti obrisan!")

    def test_real_lsof_protects_open_python_file(self):
        if not shutil.which("lsof"):
            self.skipTest("lsof nije dostupan u okruženju")
        now = time.time()
        old_ts = now - 3600
        open_tmp = os.path.join(self.bundles_dir, "active_python.tmp")
        with open(open_tmp, "wb") as f:
            f.write(b"ACTIVE_WRITE")
            f.flush()
            os.utime(open_tmp, (old_ts, old_ts))
            res_open = wis_nas_space_guard.cleanup_orphan_temp_files(
                scan_dirs=(self.bundles_dir,),
                now=now,
            )
            self.assertTrue(res_open["lsof_ok"])
            self.assertEqual(res_open["removed"], 0)
            self.assertEqual(res_open["skipped_open"], 1)
            self.assertTrue(os.path.exists(open_tmp))

        # Nakon zatvaranja fajla, sledeći prolaz ga bezbedno briše
        res_closed = wis_nas_space_guard.cleanup_orphan_temp_files(
            scan_dirs=(self.bundles_dir,),
            now=now,
        )
        self.assertEqual(res_closed["removed"], 1)
        self.assertFalse(os.path.exists(open_tmp))


if __name__ == "__main__":
    unittest.main()
