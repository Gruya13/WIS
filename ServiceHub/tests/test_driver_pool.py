"""
tests.test_driver_pool
======================
Jedinični i integracioni testovi za WIS v3.2.0 / ServiceHub v3.5.0:
- Jedinstveni Content-Addressable (CAS) Driver Pool i 100% deduplikacija preko
  sva 3 izvora (harvest, ms_catalog, oem_pack)
- Pobednik verzije i WHQL .cat prioritet (sprečavanje degradacije starijom verzijom)
- Bezbednosni filteri (prnms009, nepotpisane skripte, path traversal i symlinks)
- Sklapanje minimalnog .tar bundle-a po modelu, missing_hwids (SUBSYS prioritet) i generičkim pločama
- Zvanični Dell/HP/Lenovo XML katalozi (XXE zaštita, HTTPS allowlist, SHA-256 verifikacija)
- HTTP API rute (/api/drivers/pool/*), model-pack fallback, auto-ingest i fail-closed autentifikacija
"""

import hashlib
import io
import json
import os
import shutil
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.drivers.oem.catalogs import (
    OemCatalogManager,
    detect_oem_vendor,
    parse_dell_catalog_xml,
    parse_hp_catalog_xml,
    parse_lenovo_catalog_xml,
    validate_oem_download_url,
)
from servishub.drivers.pool import DriverPoolManager, parse_inf_metadata
from servishub.handlers import ServisHubHandler
from servishub.telemetry import FleetStore


def _make_inf_bytes(
    ver: str = "10.0.1.5",
    date_str: str = "05/01/2025",
    hwid: str = r"PCI\VEN_8086&DEV_15F3&SUBSYS_00011028",
    cat_name: str = "testdrv.cat",
    drv_class: str = "Net",
    provider: str = "IG-Systems Lab",
) -> bytes:
    content = (
        "[Version]\r\n"
        'Signature="$WINDOWS NT$"\r\n'
        f"Class={drv_class}\r\n"
        f"Provider=%Mfg%\r\n"
        f"CatalogFile={cat_name}\r\n"
        f"DriverVer={date_str},{ver}\r\n\r\n"
        "[Manufacturer]\r\n"
        "%Mfg%=Models,NTamd64\r\n\r\n"
        "[Models.NTamd64]\r\n"
        f"%DevDesc%=InstallSec, {hwid}\r\n\r\n"
        "[Strings]\r\n"
        f'Mfg="{provider}"\r\n'
        'DevDesc="Mock Network Controller"\r\n'
    )
    return content.encode("utf-8")


def _write_driver_folder(
    base_dir: str,
    rel_folder: str,
    inf_name: str = "testdrv.inf",
    ver: str = "10.0.1.5",
    date_str: str = "05/01/2025",
    hwid: str = r"PCI\VEN_8086&DEV_15F3&SUBSYS_00011028",
    include_cat: bool = True,
    sys_payload: bytes = b"MZ_FAKE_SYS_BINARY_CONTENT_" * 30,
    extra_files: dict[str, bytes] | None = None,
) -> str:
    pkg_dir = os.path.join(base_dir, rel_folder)
    os.makedirs(pkg_dir, exist_ok=True)
    cat_name = os.path.splitext(inf_name)[0] + ".cat"
    inf_bytes = _make_inf_bytes(ver=ver, date_str=date_str, hwid=hwid, cat_name=cat_name)
    with open(os.path.join(pkg_dir, inf_name), "wb") as f:
        f.write(inf_bytes)
    with open(os.path.join(pkg_dir, os.path.splitext(inf_name)[0] + ".sys"), "wb") as f:
        f.write(sys_payload)
    if include_cat:
        with open(os.path.join(pkg_dir, cat_name), "wb") as f:
            f.write(b"FAKE_CAT_SIGNATURE_" * 20)
    if extra_files:
        for fn, data in extra_files.items():
            fp = os.path.join(pkg_dir, fn)
            os.makedirs(os.path.dirname(fp), exist_ok=True)
            with open(fp, "wb") as ef:
                ef.write(data)
    return pkg_dir


def _build_tar_from_dir(src_dir: str, tar_path: str) -> str:
    with tarfile.open(tar_path, "w") as tf:
        for rdir, _, files in os.walk(src_dir):
            for fn in sorted(files):
                full_f = os.path.join(rdir, fn)
                rel = os.path.relpath(full_f, src_dir).replace("\\", "/")
                tf.add(full_f, arcname=rel, recursive=False)
    return tar_path


def _build_zip_from_dir(src_dir: str, zip_path: str) -> str:
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for rdir, _, files in os.walk(src_dir):
            for fn in sorted(files):
                full_f = os.path.join(rdir, fn)
                rel = os.path.relpath(full_f, src_dir).replace("\\", "/")
                zf.write(full_f, arcname=rel)
    return zip_path


class TestDriverPoolAndOemCatalogs(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_pool_test_")
        self.pool_dir = os.path.join(self.tmp_dir, "pool")
        self.pool = DriverPoolManager(pool_dir=self.pool_dir)

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_01_cas_deduplication_across_all_three_sources(self):
        src1 = os.path.join(self.tmp_dir, "src_harvest")
        src2 = os.path.join(self.tmp_dir, "src_mscat")
        src3 = os.path.join(self.tmp_dir, "src_oem")

        # Namerno različiti omotači putanja za identičan drajver paket
        _write_driver_folder(src1, "DriverStore/FileRepository/net1.inf_amd64_aa11/net1", inf_name="net1.inf")
        _write_driver_folder(src2, "cab_root", inf_name="net1.inf")
        _write_driver_folder(src3, "win11/x64/Network/net1.inf_dir", inf_name="net1.inf")

        res1 = self.pool.ingest_directory(src1, source="harvest", model="ThinkPad_T14_Gen_3", manufacturer="Lenovo")
        res2 = self.pool.ingest_directory(src2, source="ms_catalog")
        res3 = self.pool.ingest_directory(src3, source="oem_pack", model="Latitude_5420", manufacturer="Dell")

        self.assertEqual(res1["packages_added"], 1)
        self.assertEqual(res1["dedup_hits"], 0)
        self.assertEqual(res2["packages_added"], 0)
        self.assertEqual(res2["dedup_hits"], 1)
        self.assertEqual(res3["packages_added"], 0)
        self.assertEqual(res3["dedup_hits"], 1)
        self.assertGreater(res3["dedup_saved_bytes"], 0)

        stats = self.pool.get_stats()
        self.assertEqual(stats["unique_packages"], 1)
        self.assertEqual(stats["models_count"], 2)
        self.assertEqual(stats["dedup_hits"], 2)

        idx = self.pool._ensure_index_loaded()
        only_pkg = next(iter(idx["packages"].values()))
        self.assertEqual(set(only_pkg["sources"]), {"harvest", "ms_catalog", "oem_pack"})

    def test_02_version_winner_and_whql_cat_priority(self):
        hwid = r"PCI\VEN_10EC&DEV_8168&SUBSYS_85701043"
        src_new = os.path.join(self.tmp_dir, "drv_new")
        src_old = os.path.join(self.tmp_dir, "drv_old")
        src_unsigned_newer = os.path.join(self.tmp_dir, "drv_unsigned")
        src_newer_signed = os.path.join(self.tmp_dir, "drv_v3")

        _write_driver_folder(
            src_new, "rtlan", inf_name="rtlan.inf",
            ver="10.60.1.0", date_str="06/15/2024", hwid=hwid, include_cat=True,
            sys_payload=b"RTL_V2_SYS" * 40,
        )
        _write_driver_folder(
            src_old, "rtlan", inf_name="rtlan.inf",
            ver="10.42.0.1", date_str="03/10/2021", hwid=hwid, include_cat=True,
            sys_payload=b"RTL_V1_SYS" * 40,
        )
        _write_driver_folder(
            src_unsigned_newer, "rtlan", inf_name="rtlan.inf",
            ver="11.0.0.1", date_str="01/01/2026", hwid=hwid, include_cat=False,
            sys_payload=b"RTL_UNSIGNED" * 40,
        )
        _write_driver_folder(
            src_newer_signed, "rtlan", inf_name="rtlan.inf",
            ver="11.2.0.5", date_str="05/01/2026", hwid=hwid, include_cat=True,
            sys_payload=b"RTL_V3_SYS" * 40,
        )

        # 1. Prvo v2 (signed), pa stariji v1 -> v1 mora biti preskočen, nema degradacije
        self.pool.ingest_directory(src_new, source="harvest", model="EliteBook_840_G8", manufacturer="HP")
        old_res = self.pool.ingest_directory(src_old, source="ms_catalog", model="EliteBook_840_G8", manufacturer="HP")
        self.assertGreaterEqual(old_res["older_skipped"], 1)

        # 2. Nepotpisani paket bez .cat ne sme pobediti WHQL paket ni kad ima veći broj verzije
        uns_res = self.pool.ingest_directory(src_unsigned_newer, source="harvest", model="EliteBook_840_G8", manufacturer="HP")
        self.assertGreaterEqual(uns_res["older_skipped"], 1)

        idx = self.pool._ensure_index_loaded()
        win_pid = idx["hwid_winners"][hwid.lower()]
        self.assertEqual(idx["packages"][win_pid]["driver_ver"], "10.60.1.0")

        # 3. Potpisan v3 pobeđuje v2 i ažurira hwid_winners + manifest
        self.pool.ingest_directory(src_newer_signed, source="oem_pack", model="EliteBook_840_G8", manufacturer="HP")
        idx2 = self.pool._ensure_index_loaded()
        win_pid2 = idx2["hwid_winners"][hwid.lower()]
        self.assertEqual(idx2["packages"][win_pid2]["driver_ver"], "11.2.0.5")

        manifest = self.pool._find_manifest_for_model("EliteBook_840_G8")
        self.assertEqual(manifest["hwid_map"][hwid.lower()], win_pid2)
        self.assertEqual(manifest["package_count"], 1)

        # Prune uklanja neiskorišćene pakete (nema starijih u pool-u, ali ostaje 1 aktivni)
        prune_res = self.pool.prune_unused_packages(max_bundle_age_sec=0)
        self.assertEqual(prune_res["status"], "ok")
        self.assertEqual(self.pool.get_stats()["unique_packages"], 1)

    def test_03_security_filters_prnms009_scripts_and_traversal(self):
        # 1. prnms009.inf se odbija u parse_inf_metadata
        inbox_dir = os.path.join(self.tmp_dir, "inbox_src")
        _write_driver_folder(inbox_dir, "prn", inf_name="prnms009.inf", hwid=r"PCI\VEN_1111&DEV_2222")
        self.assertIsNone(parse_inf_metadata(os.path.join(inbox_dir, "prn", "prnms009.inf")))

        # 2. Nepotpisan paket sa .ps1 se odbija (rejected_count >= 1),
        #    dok potpisan OEM paket sa .cat ulazi u Pool BEZ zabranjene .bat skripte
        sec_src = os.path.join(self.tmp_dir, "sec_src")
        _write_driver_folder(
            sec_src, "bad_unsigned", inf_name="bad.inf",
            hwid=r"PCI\VEN_AAAA&DEV_0001", include_cat=False,
            extra_files={"evil.ps1": b"Invoke-Expression evil"},
        )
        _write_driver_folder(
            sec_src, "good_oem_with_helper", inf_name="goodoem.inf",
            hwid=r"PCI\VEN_AAAA&DEV_0002", include_cat=True,
            extra_files={"install_helper.bat": b"@echo off\r\npnputil"},
        )
        res = self.pool.ingest_directory(sec_src, source="oem_pack", model="Latitude_5430", manufacturer="Dell")
        self.assertEqual(res["rejected_count"], 1)
        self.assertEqual(res["packages_added"], 1)

        idx = self.pool._ensure_index_loaded()
        self.assertNotIn(r"pci\ven_aaaa&dev_0001", idx["hwid_winners"])
        good_pid = idx["hwid_winners"][r"pci\ven_aaaa&dev_0002"]
        good_dir = os.path.join(self.pool.packages_dir, good_pid)
        extracted_names = [fn.lower() for _, _, files in os.walk(good_dir) for fn in files]
        self.assertIn("goodoem.inf", extracted_names)
        self.assertNotIn("install_helper.bat", extracted_names)

        # 3. Path traversal i symlink u .tar/.zip arhivama bacaju ValueError
        bad_tar = os.path.join(self.tmp_dir, "traversal.tar")
        with tarfile.open(bad_tar, "w") as tf:
            ti = tarfile.TarInfo(name="../../etc/evil.inf")
            data = _make_inf_bytes()
            ti.size = len(data)
            tf.addfile(ti, io.BytesIO(data))
        with self.assertRaises(ValueError):
            self.pool.ingest_archive(bad_tar, source="harvest")

        sym_tar = os.path.join(self.tmp_dir, "symlink.tar")
        with tarfile.open(sym_tar, "w") as tf:
            ti = tarfile.TarInfo(name="bad_link")
            ti.type = tarfile.SYMTYPE
            ti.linkname = "/etc/passwd"
            tf.addfile(ti)
        with self.assertRaises(ValueError):
            self.pool.ingest_archive(sym_tar, source="harvest")

        bad_zip = os.path.join(self.tmp_dir, "traversal.zip")
        with zipfile.ZipFile(bad_zip, "w") as zf:
            zf.writestr("../escape.inf", _make_inf_bytes())
        with self.assertRaises(ValueError):
            self.pool.ingest_archive(bad_zip, source="harvest")

    def test_04_bundle_tar_by_model_and_missing_hwids(self):
        src = os.path.join(self.tmp_dir, "bundle_src")
        _write_driver_folder(
            src, "audio_gen", inf_name="aud_gen.inf",
            hwid=r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256",
            sys_payload=b"AUDIO_GEN" * 60,
        )
        _write_driver_folder(
            src, "audio_subsys", inf_name="aud_lenovo.inf",
            hwid=r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_17AA3801",
            sys_payload=b"AUDIO_LENOVO" * 60,
        )
        _write_driver_folder(
            src, "wifi", inf_name="netwtw.inf",
            hwid=r"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086",
            sys_payload=b"WIFI_SYS" * 60,
        )
        self.pool.ingest_directory(src, source="oem_pack", model="ThinkPad_T14_Gen_3", manufacturer="Lenovo")

        # Ciljani upit po missing_hwids mora izabrati tačan SUBSYS pre generičkog VEN/DEV, i ne vući wifi
        bundle = self.pool.build_bundle_tar(
            model="System Product Name",  # Generička ploča dobija po HWID-u!
            missing_hwids=[r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_17AA3801&REV_1000"],
        )
        self.assertTrue(bundle["available"])
        self.assertEqual(bundle["package_count"], 1)
        self.assertEqual(bundle["packages"][0]["inf_name"], "aud_lenovo.inf")
        self.assertTrue(os.path.isfile(bundle["bundle_path"]))

        # Ceo model bundle daje sva 3 paketa bez duplikata
        full_bundle = self.pool.build_bundle_tar(model="ThinkPad_T14_Gen_3")
        self.assertTrue(full_bundle["available"])
        self.assertEqual(full_bundle["package_count"], 3)
        with tarfile.open(full_bundle["bundle_path"], "r") as tf:
            names = [m.name for m in tf.getmembers()]
        self.assertEqual(len([n for n in names if n.endswith(".inf")]), 3)

    def test_05_oem_catalog_parsers_security_and_mock_download(self):
        # 1. Dell XML
        dell_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<DriverPackManifest xmlns="openmanage/cm/dm" baseLocation="downloads.dell.com">
  <DriverPackage format="cab" hashMD5="11112222333344445555666677778888" size="123456"
                 dateTime="2025-09-10T00:00:00" dellVersion="A09" path="FOLDER123/1/5420-win11.CAB" releaseID="R123">
    <Name><Display lang="en">5420-win11.CAB</Display></Name>
    <SupportedOperatingSystems>
      <OperatingSystem osCode="Windows11" osArch="x64"><Display lang="en">Windows 11 x64</Display></OperatingSystem>
    </SupportedOperatingSystems>
    <SupportedSystems>
      <Brand key="9" prefix="LAT"><Display lang="en">Latitude</Display>
        <Model systemID="0A20" name="Latitude 5420"><Display lang="en">5420</Display></Model>
      </Brand>
    </SupportedSystems>
  </DriverPackage>
</DriverPackManifest>'''
        dell_entries = parse_dell_catalog_xml(dell_xml)
        self.assertEqual(len(dell_entries), 1)
        self.assertEqual(dell_entries[0]["download_url"], "https://downloads.dell.com/FOLDER123/1/5420-win11.CAB")
        self.assertEqual(dell_entries[0]["os"], "win11")

        # 2. HP XML (sa xs:schema + 22H2 vs 24H2 rangiranjem)
        hp_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<NewDataSet>
  <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="HP"/></xs:schema>
  <HPClientDriverPackCatalog>
    <SoftPaqList>
      <SoftPaq>
        <Id>sp150001</Id>
        <Name>HP EliteBook 840 G8 Win11 22H2</Name>
        <Version>1.0</Version>
        <DateReleased>1/15/2024 12:00:00 AM</DateReleased>
        <Url>https://ftp.hp.com/pub/softpaq/sp150001.exe</Url>
        <Size>100000</Size>
        <SHA256>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</SHA256>
      </SoftPaq>
      <SoftPaq>
        <Id>sp155555</Id>
        <Name>HP EliteBook 840 G8 Win11 24H2</Name>
        <Version>2.0</Version>
        <DateReleased>5/20/2025 12:00:00 AM</DateReleased>
        <Url>https://ftp.hp.com/pub/softpaq/sp155555.exe</Url>
        <Size>120000</Size>
        <SHA256>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</SHA256>
      </SoftPaq>
    </SoftPaqList>
    <ProductOSDriverPackList>
      <ProductOSDriverPack>
        <Architecture>64-bit</Architecture>
        <SystemId>880d</SystemId>
        <SystemName>HP EliteBook 840 G8 Notebook PC</SystemName>
        <OSName>Windows 11 64-bit, 22H2</OSName>
        <SoftPaqId>sp150001</SoftPaqId>
      </ProductOSDriverPack>
      <ProductOSDriverPack>
        <Architecture>64-bit</Architecture>
        <SystemId>880d</SystemId>
        <SystemName>HP EliteBook 840 G8 Notebook PC</SystemName>
        <OSName>Windows 11 64-bit, 24H2</OSName>
        <SoftPaqId>sp155555</SoftPaqId>
      </ProductOSDriverPack>
    </ProductOSDriverPackList>
  </HPClientDriverPackCatalog>
</NewDataSet>'''
        hp_entries = parse_hp_catalog_xml(hp_xml)
        self.assertEqual(len(hp_entries), 2)

        # 3. Lenovo XML sa UTF-8 BOM
        lenovo_xml_tmpl = (
            '\ufeff<ModelList version="1.0">'
            '<Model name="ThinkPad T14 Gen 3" arch="Intel">'
            '<Types><Type>21AH</Type></Types>'
            '<SCCM os="win11" version="24H2" date="2025-10-01" crc="{sha}">{pkg_url}</SCCM>'
            '</Model></ModelList>'
        )

        # XXE i URL allowlist sigurnosne provere
        with self.assertRaises(ValueError):
            parse_lenovo_catalog_xml(b'<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><ModelList/>')
        with self.assertRaises(ValueError):
            validate_oem_download_url("http://downloads.dell.com/foo.cab")
        with self.assertRaises(ValueError):
            validate_oem_download_url("https://evil.example/sp123.exe")
        self.assertEqual(detect_oem_vendor("", "ThinkPad T14 Gen 3"), "lenovo")

        # Priprema pravog .zip driver paketa i lokalnog 127.0.0.1 mock HTTP servera
        oem_src = os.path.join(self.tmp_dir, "mock_oem_zip_src")
        _write_driver_folder(
            oem_src, "chipset", inf_name="t14_chipset.inf",
            ver="10.1.99.1", date_str="09/01/2025",
            hwid=r"PCI\VEN_8086&DEV_51A4&SUBSYS_22E617AA",
        )
        zip_fp = _build_zip_from_dir(oem_src, os.path.join(self.tmp_dir, "tp_t14_gen3_w11.zip"))
        with open(zip_fp, "rb") as f:
            zip_bytes = f.read()
        zip_sha = hashlib.sha256(zip_bytes).hexdigest()

        class _MockOemHandler(BaseHTTPRequestHandler):
            xml_body = b""

            def log_message(self, fmt, *args):
                return

            def do_GET(self):
                if self.path == "/catalogv2.xml":
                    self.send_response(200)
                    self.send_header("Content-Type", "application/xml")
                    self.send_header("Content-Length", str(len(self.xml_body)))
                    self.end_headers()
                    self.wfile.write(self.xml_body)
                elif self.path == "/tp_t14_gen3_w11.zip":
                    self.send_response(200)
                    self.send_header("Content-Type", "application/zip")
                    self.send_header("Content-Length", str(len(zip_bytes)))
                    self.end_headers()
                    self.wfile.write(zip_bytes)
                else:
                    self.send_response(404)
                    self.end_headers()

        srv = ThreadingHTTPServer(("127.0.0.1", 0), _MockOemHandler)
        port = srv.server_address[1]
        _MockOemHandler.xml_body = lenovo_xml_tmpl.format(
            sha=zip_sha,
            pkg_url=f"http://127.0.0.1:{port}/tp_t14_gen3_w11.zip",
        ).encode("utf-8")
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(lambda: (srv.shutdown(), srv.server_close()))

        cat_mgr = OemCatalogManager(
            catalogs_dir=os.path.join(self.tmp_dir, "oem_cats"),
            catalog_urls={"lenovo": f"http://127.0.0.1:{port}/catalogv2.xml"},
            allowed_hosts=["127.0.0.1"],
        )
        cat_mgr.save_parsed_entries("dell", dell_entries)
        cat_mgr.save_parsed_entries("hp", hp_entries)
        sync_res = cat_mgr.sync_catalog("lenovo", force=True)
        self.assertEqual(sync_res["status"], "synced")
        self.assertEqual(sync_res["entries_count"], 1)

        # HP meč mora izabrati noviji 24H2 (sp155555) a ne mešati 840 G7!
        hp_hit = cat_mgr.find_driver_pack("HP", "HP EliteBook 840 G8 Notebook PC", os_ver="win11")
        self.assertIsNotNone(hp_hit)
        self.assertEqual(hp_hit["package_id"], "sp155555")
        self.assertIsNone(cat_mgr.find_driver_pack("HP", "HP EliteBook 840 G7", os_ver="win11"))

        # End-to-end download + ingest sa SHA-256 verifikacijom preko DriverPoolManager
        mock_pool = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "mock_oem_pool"), catalog_mgr=cat_mgr)
        oem_ingest = mock_pool.ingest_oem_pack("Lenovo", "21AH0012YA", os_ver="win11")
        self.assertEqual(oem_ingest["status"], "ok")
        self.assertEqual(oem_ingest["packages_added"], 1)
        self.assertTrue(mock_pool.has_model_manifest("21AH0012YA"))

        # Neispravan SHA-256 mora odbiti preuzimanje
        bad_entry = dict(cat_mgr.find_driver_pack("Lenovo", "ThinkPad T14 Gen 3"))
        bad_entry["sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            cat_mgr.download_driver_pack(bad_entry, os.path.join(self.tmp_dir, "bad_dl"))

    def test_06_http_api_auth_upload_autoingest_fallback_migrate_prune(self):
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
            model_packs_dir=os.path.join(self.tmp_dir, "archives"),
            driver_pool_dir=os.path.join(self.tmp_dir, "http_pool"),
            admin_token="",
            client_token="",
            allow_unauthenticated_lan=False,
        )
        os.makedirs(cfg.model_packs_dir, exist_ok=True)
        prev_cfg = getattr(ServisHubHandler, "config", None)
        prev_store = getattr(ServisHubHandler, "fleet_store", None)
        prev_pool = getattr(ServisHubHandler, "driver_pool_mgr", None)

        pool_mgr = DriverPoolManager(pool_dir=cfg.driver_pool_dir)
        store = FleetStore(live_logs_dir=cfg.live_logs_dir, reports_dir=cfg.reports_dir)
        ServisHubHandler.config = cfg
        ServisHubHandler.fleet_store = store
        ServisHubHandler.driver_pool_mgr = pool_mgr
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        def _cleanup():
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg
            ServisHubHandler.fleet_store = prev_store
            ServisHubHandler.driver_pool_mgr = prev_pool

        self.addCleanup(_cleanup)

        # 1. Fail-closed admin rute bez tokena moraju vratiti 503
        for adm_path in (
            "/api/drivers/pool/sync-catalogs",
            "/api/drivers/pool/ingest-oem",
            "/api/drivers/pool/migrate",
            "/api/drivers/pool/prune",
        ):
            body = b'{"manufacturer":"Dell","model":"Latitude 5420"}'
            req = urllib.request.Request(
                f"http://127.0.0.1:{port}{adm_path}",
                data=body,
                method="POST",
                headers={"Content-Type": "application/json", "Content-Length": str(len(body))},
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx:
                urllib.request.urlopen(req, timeout=5)
            self.assertEqual(ctx.exception.code, 503, f"Očekivan 503 na {adm_path}")
            ctx.exception.close()

        # Postavi tokene i proveri 401 za pogrešan token
        cfg.admin_token = "adm-pool-350"
        cfg.client_token = "cli-pool-350"

        bad_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/pool/prune",
            data=b"{}",
            method="POST",
            headers={"Content-Type": "application/json", "X-ServisHub-Token": "wrong"},
        )
        with self.assertRaises(urllib.error.HTTPError) as ctx401:
            urllib.request.urlopen(bad_req, timeout=5)
        self.assertEqual(ctx401.exception.code, 401)
        ctx401.exception.close()

        # 2. Pripremi legacy archive na disku pa migriraj preko HTTP /api/drivers/pool/migrate
        legacy_src = os.path.join(self.tmp_dir, "legacy_src")
        _write_driver_folder(
            legacy_src, "chip", inf_name="dell_chip.inf",
            hwid=r"PCI\VEN_8086&DEV_7A23&SUBSYS_0B111028",
        )
        _build_tar_from_dir(legacy_src, os.path.join(cfg.model_packs_dir, "Legacy_Model_5430.tar"))

        mig_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/pool/migrate",
            data=b'{"async_mode": false}',
            method="POST",
            headers={"Content-Type": "application/json", "X-ServisHub-Token": "adm-pool-350"},
        )
        with urllib.request.urlopen(mig_req, timeout=10) as resp:
            mig_json = json.loads(resp.read().decode("utf-8"))
        self.assertEqual(mig_json["status"], "ok")
        self.assertEqual(mig_json["migrated_tars"], 1)

        # 3. Direct pool ingest za model koji NEMA .tar u model_packs_dir -> model-pack/status/download fallback!
        pool_only_src = os.path.join(self.tmp_dir, "pool_only_src")
        _write_driver_folder(
            pool_only_src, "lan", inf_name="pool_only_lan.inf",
            hwid=r"PCI\VEN_8086&DEV_15BE&SUBSYS_09991028",
        )
        pool_mgr.ingest_directory(pool_only_src, source="oem_pack", model="Dell_Latitude_9440", manufacturer="Dell")

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/model-pack/status?model=Dell_Latitude_9440", timeout=5) as resp:
            st_json = json.loads(resp.read().decode("utf-8"))
        self.assertTrue(st_json["available"])
        self.assertEqual(st_json["source"], "driver_pool")

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/model-pack/download?model=Dell_Latitude_9440", timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers.get("X-Driver-Pool-Source"), "driver_pool")
            tar_bytes = resp.read()
        with tarfile.open(fileobj=io.BytesIO(tar_bytes), mode="r") as tf:
            self.assertTrue(any(m.name.endswith("pool_only_lan.inf") for m in tf.getmembers()))

        # 4. POST /api/drivers/pool/bundle (strimuje .tar i vraća 404 kad nema)
        bundle_body = json.dumps({
            "model": "Dell_Latitude_9440",
            "manufacturer": "Dell",
            "missing_hwids": [r"PCI\VEN_8086&DEV_15BE&SUBSYS_09991028&REV_11"],
        }).encode("utf-8")
        b_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/pool/bundle",
            data=bundle_body,
            method="POST",
            headers={"Content-Type": "application/json", "Content-Length": str(len(bundle_body))},
        )
        with urllib.request.urlopen(b_req, timeout=10) as b_resp:
            self.assertEqual(b_resp.status, 200)
            self.assertEqual(b_resp.headers.get("X-Driver-Pool-Count"), "1")
            b_bytes = b_resp.read()
        with tarfile.open(fileobj=io.BytesIO(b_bytes), mode="r") as tf:
            self.assertTrue(any(m.name.endswith("pool_only_lan.inf") for m in tf.getmembers()))

        miss_body = b'{"model":"NepostojeciModel_999","missing_hwids":["PCI\\\\VEN_FFFF&DEV_FFFF"]}'
        miss_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/pool/bundle",
            data=miss_body,
            method="POST",
            headers={"Content-Type": "application/json", "Content-Length": str(len(miss_body))},
        )
        with self.assertRaises(urllib.error.HTTPError) as ctx404:
            urllib.request.urlopen(miss_req, timeout=5)
        self.assertEqual(ctx404.exception.code, 404)
        ctx404.exception.close()

        # 5. Upload novog <Model>.tar preko /api/drivers/model-pack/upload automatski puni Driver Pool
        up_src = os.path.join(self.tmp_dir, "up_src")
        _write_driver_folder(up_src, "aud", inf_name="up_aud.inf", hwid=r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0285")
        up_tar_path = _build_tar_from_dir(up_src, os.path.join(self.tmp_dir, "Uploaded_T16.tar"))
        with open(up_tar_path, "rb") as uf:
            up_bytes = uf.read()
        up_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model=Uploaded_T16",
            data=up_bytes,
            method="POST",
            headers={
                "Content-Type": "application/octet-stream",
                "Content-Length": str(len(up_bytes)),
                "X-ServisHub-Client-Token": "cli-pool-350",
            },
        )
        with urllib.request.urlopen(up_req, timeout=10) as up_resp:
            self.assertEqual(up_resp.status, 200)

        # Pozadinska nit treba u roku od 3s da napravi manifest za Uploaded_T16
        deadline = time.time() + 4.0
        while time.time() < deadline and not pool_mgr.has_model_manifest("Uploaded_T16"):
            time.sleep(0.05)
        self.assertTrue(pool_mgr.has_model_manifest("Uploaded_T16"))

        # 6. GET /api/drivers/pool/stats, /manifests i POST /api/drivers/pool/prune
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/pool/stats", timeout=5) as s_resp:
            stats = json.loads(s_resp.read().decode("utf-8"))
        self.assertGreaterEqual(stats["unique_packages"], 3)
        self.assertGreaterEqual(stats["models_count"], 3)

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/drivers/pool/manifests", timeout=5) as m_resp:
            mans = json.loads(m_resp.read().decode("utf-8"))
        self.assertGreaterEqual(mans["count"], 3)

        prune_req = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/pool/prune",
            data=b'{"max_bundle_age_sec":0}',
            method="POST",
            headers={"Content-Type": "application/json", "X-ServisHub-Token": "adm-pool-350"},
        )
        with urllib.request.urlopen(prune_req, timeout=5) as p_resp:
            p_json = json.loads(p_resp.read().decode("utf-8"))
        self.assertEqual(p_json["status"], "ok")


if __name__ == "__main__":
    unittest.main()
