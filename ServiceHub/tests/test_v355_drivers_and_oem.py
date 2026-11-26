"""
tests.test_v355_drivers_and_oem
===============================
Jedinični i integracioni testovi za WIS v3.5.5 / ServiceHub v3.7.2 (WIS-TASK-279):
- IMP-97: filtriranje manifest["packages"] po all_hwids / hwid_winners u build_bundle_tar,
  deduplikacija INF-ova i fajlova, migraciona sanacija manifests/*.json (`compact_manifests`).
- IMP-98: uklanjanje sintetičkih Microsoft Surface .msi URL-ova i 404 default katalog URL-a,
  dinamički Download Center HTML parser + HTTP HEAD verifikacija + DropInOEMProvider fallback.
- IMP-106: per-package `try/except ValueError` u `parse_fujitsu_catalog_xml`, prošireni catch
  u `sync_catalog("fujitsu")`, uklanjanje lažnih `SoftwareGUID=lifebook-...` URL-ova.
- IMP-108: dopuna `detect_oem_vendor` serija (ASUS, HP, Acer, MSI) bez lažnih pogodaka.
- IMP-110: proširenje `prune_old_releases(keep_last=3)` uz `current.json`, symlink i `lsof`
  zaštitu na sve konfigurisane/sekundarne `releases/` korene u `server.py` i `wis_nas_space_guard.py`.
- IMP-113: zabrana `Class=Firmware` i firmware/CFU INF obrazaca iz harvest izvora pri ingestu,
  delta-checku i `build_bundle_tar`; DCH Extension/SoftwareComponent uparivanje u `clean_missing`
  modu bez nesrodnih paketa; OS-build (`26200` vs `26100`) izolacija; Surface SKU `2107`.
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tarfile
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

from servishub.drivers.oem.catalogs import (
    CATALOG_URLS,
    OemCatalogManager,
    detect_oem_vendor,
)
from servishub.drivers.oem.fujitsu import FujitsuOEMProvider, parse_fujitsu_catalog_xml
from servishub.drivers.oem.microsoft_surface import (
    SURFACE_CATALOG_SPECS,
    MicrosoftOEMProvider,
    discover_surface_msi_entries,
    parse_microsoft_download_center_html,
)
from servishub.drivers.pool import (
    DriverPoolManager,
    is_blocked_firmware_inf,
    parse_inf_metadata,
)
from servishub.server import _run_nightly_gc_step

SCRIPTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "scripts"))
if SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, SCRIPTS_DIR)

import wis_nas_space_guard  # noqa: E402


def _write_inf_pkg(
    base_dir: str,
    rel_folder: str,
    inf_name: str,
    hwid: str,
    drv_class: str = "Net",
    ver: str = "10.0.1.5",
    date_str: str = "05/01/2025",
    provider: str = "IG-Systems Lab",
    component_ids: list[str] | None = None,
    include_cat: bool = True,
    sys_payload: bytes = b"MZ_FAKE_SYS_DATA_" * 20,
) -> str:
    pkg_dir = os.path.join(base_dir, rel_folder)
    os.makedirs(pkg_dir, exist_ok=True)
    cat_name = os.path.splitext(inf_name)[0] + ".cat"
    comp_section = ""
    if component_ids:
        c_lines = "\r\n".join(f"ComponentIDs={cid}" for cid in component_ids)
        comp_section = f"\r\n[InstallSec.Components]\r\n{c_lines}\r\n"

    inf_text = (
        "[Version]\r\n"
        'Signature="$WINDOWS NT$"\r\n'
        f"Class={drv_class}\r\n"
        "Provider=%Mfg%\r\n"
        f"CatalogFile={cat_name}\r\n"
        f"DriverVer={date_str},{ver}\r\n\r\n"
        "[Manufacturer]\r\n"
        "%Mfg%=Models,NTamd64\r\n\r\n"
        "[Models.NTamd64]\r\n"
        f"%DevDesc%=InstallSec, {hwid}\r\n"
        f"{comp_section}\r\n"
        "[Strings]\r\n"
        f'Mfg="{provider}"\r\n'
        'DevDesc="Mock Device"\r\n'
    )
    with open(os.path.join(pkg_dir, inf_name), "wb") as f:
        f.write(inf_text.encode("utf-8"))
    with open(os.path.join(pkg_dir, os.path.splitext(inf_name)[0] + ".sys"), "wb") as f:
        f.write(sys_payload)
    if include_cat:
        with open(os.path.join(pkg_dir, cat_name), "wb") as f:
            f.write(b"FAKE_CAT_SIG_" * 15)
    return pkg_dir


class TestIMP97BundleFilteringAndManifestCompaction(unittest.TestCase):
    """Testovi za IMP-97: filtriranje po all_hwids/hwid_winners i sanacija manifests/*.json."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v355_imp97_")
        self.pool = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_build_bundle_tar_filters_manifest_by_all_hwids_instead_of_full_bloat(self):
        src = os.path.join(self.tmp_dir, "sl6_src")
        hw_wifi = r"PCI\VEN_8086&DEV_51F1&SUBSYS_00948086"
        hw_audio = r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0289&SUBSYS_14140066"
        hw_unused_1 = r"PCI\VEN_10DE&DEV_28A0&SUBSYS_14149999"
        hw_unused_2 = r"PCI\VEN_10EC&DEV_8168&SUBSYS_14148888"

        _write_inf_pkg(src, "wifi", "netwtw14.inf", hw_wifi, drv_class="Net", sys_payload=b"WIFI" * 100)
        _write_inf_pkg(src, "audio", "rtkvhd64.inf", hw_audio, drv_class="MEDIA", sys_payload=b"AUD" * 100)
        _write_inf_pkg(src, "unused_gpu", "nv_disp.inf", hw_unused_1, drv_class="Display", sys_payload=b"GPU" * 500)
        _write_inf_pkg(src, "unused_lan", "rt640x64.inf", hw_unused_2, drv_class="Net", sys_payload=b"LAN" * 300)

        self.pool.ingest_directory(src, source="harvest", model="Surface_Laptop_6_for_Business", manufacturer="Microsoft")

        # Klijent šalje samo svoja 2 aktivna HWID-a u all_hwids -> ne sme dobiti svih 4 iz manifesta!
        bundle = self.pool.build_bundle_tar(
            model="Surface_Laptop_6_for_Business",
            all_hwids=[hw_wifi, hw_audio],
        )
        self.assertTrue(bundle["available"])
        self.assertEqual(bundle["package_count"], 2)
        infs = {p["inf_name"] for p in bundle["packages"]}
        self.assertEqual(infs, {"netwtw14.inf", "rtkvhd64.inf"})

        with tarfile.open(bundle["bundle_path"], "r") as tf:
            members = [m.name for m in tf.getmembers()]
        self.assertEqual(len(members), len(set(m.lower() for m in members)))
        self.assertFalse(any("nv_disp.inf" in m for m in members))

    def test_compact_manifests_sanitizes_bloated_and_duplicate_inf_manifests(self):
        # 1. Ingestujemo stariju i noviju verziju istog Intel GPU INF-a sa delimično preklapajućim HWID-ovima
        src_old = os.path.join(self.tmp_dir, "gpu_old")
        src_new = os.path.join(self.tmp_dir, "gpu_new")
        hw_active = r"PCI\VEN_8086&DEV_7D55&SUBSYS_14140066"
        hw_legacy_only = r"PCI\VEN_8086&DEV_7D40&SUBSYS_14140001"

        # Stariji paket ima 2 HWID-a
        old_dir = os.path.join(src_old, "igfx_old")
        os.makedirs(old_dir, exist_ok=True)
        old_inf = (
            "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Display\r\nProvider=%Mfg%\r\n"
            "CatalogFile=iigd_dch.cat\r\nDriverVer=01/10/2024,31.0.101.5000\r\n\r\n"
            "[Manufacturer]\r\n%Mfg%=Models,NTamd64\r\n\r\n[Models.NTamd64]\r\n"
            f"%D1%=Inst, {hw_active}\r\n%D2%=Inst, {hw_legacy_only}\r\n\r\n"
            "[Strings]\r\nMfg=\"Intel\"\r\nD1=\"GPU1\"\r\nD2=\"GPU2\"\r\n"
        ).encode("utf-8")
        with open(os.path.join(old_dir, "iigd_dch.inf"), "wb") as f:
            f.write(old_inf)
        with open(os.path.join(old_dir, "iigd_dch.cat"), "wb") as f:
            f.write(b"CAT_OLD")
        with open(os.path.join(old_dir, "igfx.sys"), "wb") as f:
            f.write(b"OLD_GPU_BLOAT_" * 500)

        # Noviji paket ima samo aktivni HWID
        _write_inf_pkg(
            src_new, "igfx_new", "iigd_dch.inf", hw_active,
            drv_class="Display", ver="32.0.101.6500", date_str="04/01/2025",
            provider="Intel", sys_payload=b"NEW_GPU_" * 100,
        )

        self.pool.ingest_directory(src_old, source="harvest", model="Surface_Laptop_6_for_Business")
        self.pool.ingest_directory(src_new, source="harvest", model="Surface_Laptop_6_for_Business")

        # Simuliramo zagađeni postojeći manifest sa diska (kakav je zatečen u produkciji)
        m_path = self.pool._manifest_path("Surface_Laptop_6_for_Business")
        with open(m_path, "r", encoding="utf-8") as f:
            m_data = json.load(f)
        idx = self.pool._ensure_index_loaded()
        old_pids = [pid for pid, pm in idx["packages"].items() if pm["driver_ver"] == "31.0.101.5000"]
        new_pids = [pid for pid, pm in idx["packages"].items() if pm["driver_ver"] == "32.0.101.6500"]
        self.assertEqual(len(old_pids), 1)
        self.assertEqual(len(new_pids), 1)
        old_pid, new_pid = old_pids[0], new_pids[0]

        # Ubaci i lažni legacy harvest SurfaceUEFI paket direktno u index/packages/manifest
        fw_pid = "f" * 64
        fw_dir = os.path.join(self.pool.packages_dir, fw_pid)
        os.makedirs(fw_dir, exist_ok=True)
        with open(os.path.join(fw_dir, "SurfaceUEFI.inf"), "wb") as f:
            f.write(b"[Version]\nClass=Firmware\n")
        idx["packages"][fw_pid] = {
            "pkg_id": fw_pid,
            "inf_name": "SurfaceUEFI.inf",
            "inf_stem": "SurfaceUEFI",
            "driver_class": "Firmware",
            "provider": "Microsoft",
            "driver_ver": "12.0.0.1",
            "driver_date": "2025-01-01",
            "has_cat": True,
            "hwids": [r"ACPI\MSHW9999"],
            "size_bytes": 500000,
            "sources": ["harvest"],
        }
        idx["hwid_winners"][r"acpi\mshw9999"] = fw_pid
        self.pool._save_index_locked(idx)

        m_data["hwid_map"][hw_legacy_only.lower()] = old_pid
        m_data["hwid_map"][r"acpi\mshw9999"] = fw_pid
        m_data["packages"] = [old_pid, new_pid, fw_pid, "nonexistent_" + ("0" * 52)]
        m_data["package_count"] = len(m_data["packages"])
        m_data["total_bytes"] = 9999999
        with open(m_path, "w", encoding="utf-8") as f:
            json.dump(m_data, f)

        # Symlink manifest ne sme biti dirnut
        outside_json = os.path.join(self.tmp_dir, "outside.json")
        with open(outside_json, "w", encoding="utf-8") as f:
            f.write('{"packages": ["should_stay"]}')
        os.symlink(outside_json, os.path.join(self.pool.manifests_dir, "evil_link.json"))

        res = self.pool.compact_manifests(prune_orphans=True)
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["manifests_compacted"], 1)
        self.assertGreaterEqual(res["firmware_removed"], 1)

        with open(m_path, "r", encoding="utf-8") as f:
            cleaned = json.load(f)
        self.assertEqual(cleaned["packages"], [new_pid])
        self.assertEqual(cleaned["package_count"], 1)
        self.assertNotIn(r"acpi\mshw9999", cleaned["hwid_map"])
        self.assertFalse(os.path.exists(fw_dir))

        # Symlink cilj ostao netaknut
        with open(outside_json, "r", encoding="utf-8") as f:
            self.assertEqual(json.load(f)["packages"], ["should_stay"])


class TestIMP113FirmwareBlacklistDchPairingAndOsBuild(unittest.TestCase):
    """Testovi za IMP-113: Firmware/CFU blokada, DCH uparivanje i OS-build izolacija."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v355_imp113_")
        self.pool = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"))

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_firmware_and_cfu_blocked_at_harvest_ingest_delta_check_and_bundle(self):
        src = os.path.join(self.tmp_dir, "fw_harvest")
        blocked_cases = [
            ("fw_cls", "CustomSystemBios.inf", "Firmware", r"ACPI\MSHW0001"),
            ("uefi", "SurfaceUEFI_2107.inf", "System", r"ACPI\MSHW0002"),
            ("sam", "SurfaceSAM.inf", "System", r"ACPI\MSHW0003"),
            ("me", "SurfaceME_LNL.inf", "System", r"ACPI\MSHW0004"),
            ("tppu", "SurfaceTouchPadProcessorUpdate.inf", "HIDClass", r"HID\VEN_MSHW&DEV_0005"),
            ("cfu", "SurfaceCFUOverHid.inf", "HIDClass", r"HID\VEN_MSHW&DEV_0006"),
            ("sfwu", "SurfaceStorageFwUpdate.inf", "DiskDrive", r"NVME\VEN_1414&DEV_0007"),
            ("genfw", "IntelMeFwUpdate.inf", "System", r"PCI\VEN_8086&DEV_7E70"),
        ]
        for folder, inf_n, cls_n, hw in blocked_cases:
            self.assertTrue(is_blocked_firmware_inf(inf_n, cls_n))
            _write_inf_pkg(src, folder, inf_n, hw, drv_class=cls_n)

        # Jedan potpuno ispravan HID drajver mora proći
        _write_inf_pkg(src, "ok_hid", "surface_hid_mini.inf", r"ACPI\MSHW0100", drv_class="HIDClass")

        res = self.pool.ingest_directory(src, source="harvest", model="Surface_Laptop_7_Intel", os_build="26200")
        self.assertEqual(res["packages_added"], 1)
        self.assertEqual(res["rejected_count"], len(blocked_cases))

        # delta_check ne sme tražiti blokirane firmware INF-ove za harvest
        dc = self.pool.delta_check(
            model="Surface_Laptop_7_Intel",
            packages=[
                {"inf_name": "SurfaceUEFI.inf", "driver_class": "Firmware", "hwids": [r"ACPI\MSHW0002"]},
                {"inf_name": "SurfaceTouchPadProcessorUpdate.inf", "driver_class": "HIDClass", "hwids": [r"HID\VEN_MSHW&DEV_0005"]},
                {"inf_name": "CustomBios.inf", "driver_class": "Firmware", "hwids": [r"ACPI\MSHW0001"]},
            ],
            os_build="26200",
        )
        self.assertEqual(dc["missing_infs"], [])
        self.assertEqual(len(dc["blocked_infs"]), 3)
        self.assertFalse(dc["harvest_needed"])

        # Zvanični oem_pack izvor nije blokiran harvest blacklistom
        oem_src = os.path.join(self.tmp_dir, "oem_fw")
        _write_inf_pkg(oem_src, "sam_oem", "SurfaceSAM.inf", r"ACPI\MSHW0003", drv_class="Firmware")
        res_oem = self.pool.ingest_directory(oem_src, source="oem_pack", model="Surface_Laptop_7_Intel")
        self.assertEqual(res_oem["packages_added"], 1)

    def test_legacy_harvest_firmware_blocked_in_build_bundle_tar_and_resolve(self):
        # Simulacija postojećih (već ubranih u v3.5.4) harvest CFU/firmware paketa u Pool-u
        idx = self.pool._ensure_index_loaded()
        cfu_pid = "c" * 64
        tppu_pid = "d" * 64
        for pid, inf_n, hw in (
            (cfu_pid, "SurfaceCFUOverHid.inf", r"HID\VEN_MSHW&DEV_0099"),
            (tppu_pid, "SurfaceTouchPadProcessorUpdate.inf", r"HID\VEN_MSHW&DEV_0098"),
        ):
            pdir = os.path.join(self.pool.packages_dir, pid)
            os.makedirs(pdir, exist_ok=True)
            with open(os.path.join(pdir, inf_n), "wb") as f:
                f.write(b"[Version]\nSignature=\"$WINDOWS NT$\"\nClass=HIDClass\n")
            idx["packages"][pid] = {
                "pkg_id": pid,
                "inf_name": inf_n,
                "inf_stem": os.path.splitext(inf_n)[0],
                "driver_class": "HIDClass",
                "provider": "Microsoft",
                "driver_ver": "1.0.0.1",
                "driver_date": "2025-01-01",
                "has_cat": True,
                "hwids": [hw],
                "size_bytes": 1024,
                "sources": ["harvest"],
            }
            idx["hwid_winners"][hw.lower()] = pid
        self.pool._save_index_locked(idx)

        m_path = self.pool._manifest_path("SL7_Legacy")
        with open(m_path, "w", encoding="utf-8") as mf:
            json.dump({
                "model": "SL7_Legacy",
                "hwid_map": {
                    r"hid\ven_mshw&dev_0099": cfu_pid,
                    r"hid\ven_mshw&dev_0098": tppu_pid,
                },
                "packages": [cfu_pid, tppu_pid],
                "sources": ["harvest"],
                "total_bytes": 2048,
            }, mf)

        # Sva 3 moda build_bundle_tar + resolve_device_hwids moraju blokirati ove pakete!
        b_miss = self.pool.build_bundle_tar(model="SL7_Legacy", missing_hwids=[r"HID\VEN_MSHW&DEV_0099"])
        b_all = self.pool.build_bundle_tar(model="SL7_Legacy", all_hwids=[r"HID\VEN_MSHW&DEV_0098"])
        b_full = self.pool.build_bundle_tar(model="SL7_Legacy")
        self.assertFalse(b_miss["available"])
        self.assertFalse(b_all["available"])
        self.assertFalse(b_full["available"])

        r_res = self.pool.resolve_device_hwids([r"HID\VEN_MSHW&DEV_0099", r"HID\VEN_MSHW&DEV_0098"], model="SL7_Legacy")
        self.assertEqual(r_res["matches"], [])
        self.assertEqual(len(r_res["unmatched"]), 2)

    def test_clean_missing_pairs_base_with_dch_extension_and_software_component_only(self):
        src = os.path.join(self.tmp_dir, "dch_src")
        hw_audio = r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0289&SUBSYS_14140095"
        hw_wifi = r"PCI\VEN_8086&DEV_A840&SUBSYS_00948086"

        # 1. Base audio INF sa ComponentIDs
        _write_inf_pkg(
            src, "audio_base", "rtkvhd64.inf", hw_audio,
            drv_class="MEDIA", ver="6.0.9700.1", date_str="02/10/2025",
            component_ids=[r"VEN_10EC&AUD_BASE_APO"],
            sys_payload=b"BASE_AUDIO" * 30,
        )
        # 2. DCH Extension INF za isti HWID (sa starijim datumom — NE sme biti odbačen zbog Base INF-a!)
        _write_inf_pkg(
            src, "audio_ext", "SurfaceAudioExt.inf", hw_audio,
            drv_class="Extension", ver="1.2.3.4", date_str="01/05/2024",
            component_ids=[r"SWC\VEN_10EC&SURF_EXT_APO"],
            sys_payload=b"EXT_AUDIO" * 30,
        )
        # 3. SoftwareComponent za Base ComponentID
        _write_inf_pkg(
            src, "audio_apo_base", "RealtekAPO.inf", r"SWC\VEN_10EC&AUD_BASE_APO",
            drv_class="SoftwareComponent", ver="13.0.6000.1", date_str="02/01/2025",
            sys_payload=b"APO_BASE" * 30,
        )
        # 4. SoftwareComponent za Extension ComponentID
        _write_inf_pkg(
            src, "audio_apo_ext", "SurfaceApoExt.inf", r"SWC\VEN_10EC&SURF_EXT_APO",
            drv_class="SoftwareComponent", ver="2.0.1.0", date_str="02/01/2025",
            sys_payload=b"APO_EXT" * 30,
        )
        # 5. Nesrodni Wi-Fi paket modela koji NE SME da uđe u clean_missing audio bundle
        _write_inf_pkg(
            src, "wifi", "netwtw14.inf", hw_wifi,
            drv_class="Net", ver="23.100.0.4", date_str="03/01/2025",
            sys_payload=b"WIFI_UNRELATED" * 30,
        )

        # Proveri parse_inf_metadata za component_ids i dch_role
        meta_ext = parse_inf_metadata(os.path.join(src, "audio_ext", "SurfaceAudioExt.inf"))
        self.assertEqual(meta_ext["dch_role"], "extension")
        self.assertEqual(meta_ext["component_ids"], [r"SWC\VEN_10EC&SURF_EXT_APO"])

        ingest_res = self.pool.ingest_directory(
            src, source="harvest", model="Surface_Laptop_7_Intel", os_build="26100"
        )
        self.assertEqual(ingest_res["packages_added"], 5)

        bundle = self.pool.build_bundle_tar(
            model="Surface_Laptop_7_Intel",
            missing_hwids=[hw_audio + "&REV_1001"],
            os_build="26100",
        )
        self.assertTrue(bundle["available"])
        infs = {p["inf_name"] for p in bundle["packages"]}
        self.assertEqual(
            infs,
            {"rtkvhd64.inf", "SurfaceAudioExt.inf", "RealtekAPO.inf", "SurfaceApoExt.inf"},
        )
        self.assertNotIn("netwtw14.inf", infs)

    def test_os_build_isolation_between_26100_and_26200_harvest_packages(self):
        hw = r"PCI\VEN_8086&DEV_7E78&SUBSYS_14142107"
        src_26100 = os.path.join(self.tmp_dir, "b26100")
        src_26200 = os.path.join(self.tmp_dir, "b26200")

        _write_inf_pkg(
            src_26100, "spi_26100", "IntelQuickSPI_26100.inf", hw,
            drv_class="System", ver="30.100.2400.1", date_str="01/15/2025",
            sys_payload=b"SPI_26100" * 40,
        )
        _write_inf_pkg(
            src_26200, "spi_26200", "IntelQuickSPI_26200.inf", hw,
            drv_class="System", ver="30.100.2500.1", date_str="06/15/2025",
            sys_payload=b"SPI_26200" * 40,
        )

        # Ingestujemo samo 26200 najpre
        r262 = self.pool.ingest_directory(src_26200, source="harvest", model="SL7_Intel", os_build="26200.8037")
        self.assertEqual(r262["os_build"], "26200")
        self.assertEqual(r262["packages_added"], 1)

        # 26100 klijent pita bundle -> ne sme slepo dobiti 26200 harvest paket!
        b_miss = self.pool.build_bundle_tar(model="SL7_Intel", missing_hwids=[hw], os_build="26100")
        self.assertFalse(b_miss["available"])

        # delta_check za 26100 kaže da 26100 drajver fali (ne meša se sa 26200!)
        dc = self.pool.delta_check(
            model="SL7_Intel",
            packages=[{"inf_name": "IntelQuickSPI_26100.inf", "driver_ver": "30.100.2400.1", "hwids": [hw]}],
            os_build="26100",
        )
        self.assertEqual(dc["missing_infs"], ["IntelQuickSPI_26100.inf"])

        # Ingestujemo i 26100 (čak i sa starijom verzijom, ne sme biti odbačen zbog 26200 pobednika!)
        r261 = self.pool.ingest_directory(src_26100, source="harvest", model="SL7_Intel", os_build="26100")
        self.assertEqual(r261["packages_added"], 1)

        b_26100 = self.pool.build_bundle_tar(model="SL7_Intel", missing_hwids=[hw], os_build="26100")
        b_26200 = self.pool.build_bundle_tar(model="SL7_Intel", missing_hwids=[hw], os_build="26200")
        self.assertTrue(b_26100["available"])
        self.assertTrue(b_26200["available"])
        self.assertEqual(b_26100["packages"][0]["inf_name"], "IntelQuickSPI_26100.inf")
        self.assertEqual(b_26200["packages"][0]["inf_name"], "IntelQuickSPI_26200.inf")


class TestIMP98AndIMP106OemCatalogsAndSurfaceSku(unittest.TestCase):
    """Testovi za IMP-98, IMP-106 i IMP-113 (Surface SKU 2107, dinamički fetch, DropIn, Fujitsu)."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v355_oem_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_no_synthetic_surface_urls_no_404_default_and_sku_2107_present(self):
        self.assertNotIn("microsoft", CATALOG_URLS)
        for spec in SURFACE_CATALOG_SPECS:
            dl = str(spec.get("download_url") or "")
            self.assertEqual(dl, "", f"Sintetički download_url mora biti uklonjen iz {spec['package_id']}")
            self.assertTrue(
                str(spec.get("download_page_url") or "").startswith("https://www.microsoft.com/en-us/download/details.aspx?id=")
            )

        # IMP-113: SystemSKU Surface_Laptop_7th_Edition_With_Intel_For_Business_2107
        spec_2107 = MicrosoftOEMProvider.find_surface_spec(
            model="Surface Laptop for Business 7th Edition with Intel",
            os_ver="win11-24h2",
            system_sku="Surface_Laptop_7th_Edition_With_Intel_For_Business_2107",
        )
        self.assertIsNotNone(spec_2107)
        self.assertEqual(spec_2107["package_id"], "SurfaceLaptop7_Intel_Win11_26100")
        self.assertEqual(spec_2107["arch"], "x64")
        self.assertEqual(spec_2107["download_center_id"], "108014")
        self.assertIn("id=108014", spec_2107["download_page_url"])

    def test_surface_dynamic_html_discovery_head_verification_and_dropin_fallback(self):
        class _MsMockHandler(BaseHTTPRequestHandler):
            def log_message(self, fmt, *args):
                return

            def do_GET(self):
                port = self.server.server_address[1]
                if self.path == "/details?id=108014":
                    html = (
                        "<html><body>"
                        f'<a href="http://127.0.0.1:{port}/SurfaceLaptop7thEditionforBusiness_Win11_26100_25.041.20950.0.msi">OK</a>'
                        f'<a href="http://127.0.0.1:{port}/Missing_Win11_26100.msi">404</a>'
                        '<a href="https://evil.example.com/SurfaceEvil.msi">EVIL</a>'
                        "</body></html>"
                    ).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(html)))
                    self.end_headers()
                    self.wfile.write(html)
                else:
                    self.send_response(404)
                    self.end_headers()

            def do_HEAD(self):
                if self.path.endswith("SurfaceLaptop7thEditionforBusiness_Win11_26100_25.041.20950.0.msi"):
                    self.send_response(200)
                    self.send_header("Content-Type", "application/octet-stream")
                    self.send_header("Content-Length", "1620000000")
                    self.end_headers()
                else:
                    self.send_response(404)
                    self.end_headers()

        srv = ThreadingHTTPServer(("127.0.0.1", 0), _MsMockHandler)
        port = srv.server_address[1]
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(lambda: (srv.shutdown(), srv.server_close()))

        spec = {
            "download_page_url": f"http://127.0.0.1:{port}/details?id=108014",
            "download_center_id": "108014",
            "models": ["Surface Laptop 7th Edition for Business"],
            "system_skus": ["Surface_Laptop_7th_Edition_With_Intel_For_Business_2107"],
            "arch": "x64",
        }
        discovered = discover_surface_msi_entries(
            spec,
            timeout=5.0,
            verify_head=True,
            allowed_hosts=["127.0.0.1"],
        )
        self.assertEqual(len(discovered), 1)
        self.assertEqual(discovered[0]["package_name"], "SurfaceLaptop7thEditionforBusiness_Win11_26100_25.041.20950.0.msi")
        self.assertEqual(discovered[0]["size_bytes"], 1620000000)
        self.assertTrue(discovered[0]["head_verified"])
        self.assertEqual(discovered[0]["os_build"], "26100")

        # Bez Drop-in paketa: čist dropin_required bez izmišljenog URL-a
        dropin_dir = os.path.join(self.tmp_dir, "dropin")
        ms_prov = MicrosoftOEMProvider(cache_dir=os.path.join(self.tmp_dir, "ms_cache"), dropin_dir=dropin_dir)
        no_di = ms_prov.resolve_driver_pack(
            "Surface Laptop 5",
            os_ver="win11-24h2",
            system_sku="Surface_Laptop_5_1950",
        )
        self.assertEqual(no_di["status"], "not_found")
        self.assertEqual(no_di["download_url"], "")
        self.assertTrue(no_di["dropin_required"])
        self.assertIn("details.aspx?id=104679", no_di["download_page_url"])

        # Sa Drop-in .msi paketom: odmah status="ok", source="dropin", 0 HTTP poziva
        sl5_dir = os.path.join(dropin_dir, "microsoft", "Surface_Laptop_5")
        os.makedirs(sl5_dir, exist_ok=True)
        msi_fp = os.path.join(sl5_dir, "SurfaceLaptop5_Win11_26100_25.020.19500.0.msi")
        with open(msi_fp, "wb") as f:
            f.write(b"MSI_LOCAL_DROPIN_CONTENT" * 40)

        with_di = ms_prov.resolve_driver_pack("Surface Laptop 5", os_ver="win11-24h2")
        self.assertEqual(with_di["status"], "ok")
        self.assertEqual(with_di["source"], "dropin")
        self.assertEqual(with_di["local_path"], os.path.realpath(msi_fp))

        # OemCatalogManager.find_driver_pack preferira lokalni Drop-in umesto indeksnog zapisa bez download_url
        cat_mgr = OemCatalogManager(catalogs_dir=os.path.join(self.tmp_dir, "cats"), dropin_dir=dropin_dir)
        cat_mgr.sync_catalog("microsoft", force=True)
        hit = cat_mgr.find_driver_pack("Microsoft Corporation", "Surface Laptop 5", os_ver="win11-24h2")
        self.assertIsNotNone(hit)
        self.assertEqual(hit["source"], "dropin")
        self.assertEqual(hit["local_path"], os.path.realpath(msi_fp))

        # Za model bez Drop-in fajla, ingest_oem_pack vraća čist dropin_required (bez lažnog 404 downloada)
        pool = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool"), catalog_mgr=cat_mgr)
        with patch("servishub.drivers.pool.discover_surface_msi_entries", return_value=[]):
            res_req = pool.ingest_oem_pack(
                "Microsoft Corporation",
                "Surface Laptop 6 for Business",
                system_sku="Surface_Laptop_6_for_Business_2081",
            )
        self.assertEqual(res_req["status"], "dropin_required")
        self.assertIn("id=105946", res_req["download_page_url"])

    def test_fujitsu_xml_skips_bad_urls_removes_fake_guids_and_catches_sync_errors(self):
        # 1. Mixed XML: 1 HTTP, 1 evil domen, 1 ispravan HTTPS -> vraća 1 ispravan (ne baca ValueError!)
        mixed_xml = b'''<?xml version="1.0" encoding="utf-8"?>
<DeskUpdateCatalog>
  <DriverPackage id="BAD_HTTP" version="1.0">
    <Url>http://support.ts.fujitsu.com/old.zip</Url>
    <Models><Model name="LIFEBOOK E5411"/></Models>
  </DriverPackage>
  <DriverPackage id="BAD_HOST" version="1.0">
    <Url>https://evil.example.com/malware.zip</Url>
    <Models><Model name="LIFEBOOK E5411"/></Models>
  </DriverPackage>
  <DriverPackage id="GOOD_HTTPS" version="2.3.0" releaseDate="2025-03-10">
    <DownloadUrl>https://support.ts.fujitsu.com/DriverPacks/LIFEBOOK_E5411_W11.zip</DownloadUrl>
    <SupportedOperatingSystems><OperatingSystem name="Windows 11 24H2" arch="x64"/></SupportedOperatingSystems>
    <SupportedSystems><Model name="FUJITSU LIFEBOOK E5411"/></SupportedSystems>
  </DriverPackage>
</DeskUpdateCatalog>'''
        entries = parse_fujitsu_catalog_xml(mixed_xml)
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["package_id"], "GOOD_HTTPS")

        # XXE i dalje baca ValueError
        with self.assertRaises(ValueError):
            parse_fujitsu_catalog_xml(b'<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><DeskUpdateCatalog/>')

        # 2. Nijedan KNOWN_MODELS zapis nema lažni SoftwareGUID=lifebook-... URL
        for km in FujitsuOEMProvider.KNOWN_MODELS.values():
            self.assertNotIn("SoftwareGUID=lifebook-", str(km.get("download_url") or ""))
            self.assertNotIn("SoftwareGUID=esprimo-", str(km.get("download_url") or ""))
            self.assertNotIn("SoftwareGUID=celsius-", str(km.get("download_url") or ""))
            self.assertEqual(km.get("download_url"), "")
            self.assertEqual(km.get("bios_url"), "")
        self.assertEqual(FujitsuOEMProvider.get_builtin_catalog_entries(), [])

        # 3. sync_catalog("fujitsu") hvata i ValueError / ET.ParseError i radi čist DropIn fallback
        dropin_dir = os.path.join(self.tmp_dir, "fj_dropin")
        fj_model_dir = os.path.join(dropin_dir, "fujitsu", "LIFEBOOK_E5411")
        os.makedirs(fj_model_dir, exist_ok=True)
        fj_zip = os.path.join(fj_model_dir, "FTS_LIFEBOOK_E5411_W11.zip")
        with open(fj_zip, "wb") as f:
            f.write(b"PK\x03\x04_FUJITSU_DROPIN_" * 40)

        cat_mgr = OemCatalogManager(catalogs_dir=os.path.join(self.tmp_dir, "fj_cats"), dropin_dir=dropin_dir)
        with patch.object(cat_mgr, "_http_fetch_bytes", side_effect=ValueError("corrupt xml")):
            sync_res = cat_mgr.sync_catalog("fujitsu", force=True)
        self.assertEqual(sync_res["status"], "synced")
        self.assertEqual(sync_res["source_url"], "dropin://fujitsu")
        self.assertEqual(sync_res["entries_count"], 1)


class TestIMP108DetectOemVendorExpansion(unittest.TestCase):
    """Testovi za IMP-108: proširene serije laptopova i zaštita od lažnih poklapanja."""

    def test_expanded_vendor_series_and_collision_guards(self):
        # ASUS
        self.assertEqual(detect_oem_vendor("", "Vivobook_Go_E1504FA"), "asus")
        self.assertEqual(detect_oem_vendor("", "VivoBook 15 OLED"), "asus")
        self.assertEqual(detect_oem_vendor("", "ProArt P16"), "asus")
        # HP
        self.assertEqual(detect_oem_vendor("", "Pavilion 15-eg2000"), "hp")
        self.assertEqual(detect_oem_vendor("", "Envy x360 14"), "hp")
        self.assertEqual(detect_oem_vendor("", "OMEN 16"), "hp")
        self.assertEqual(detect_oem_vendor("", "Victus 15"), "hp")
        self.assertEqual(detect_oem_vendor("", "OmniBook Ultra Flip"), "hp")
        # Acer
        self.assertEqual(detect_oem_vendor("", "Swift Go 14"), "acer")
        self.assertEqual(detect_oem_vendor("", "Extensa 15"), "acer")
        self.assertEqual(detect_oem_vendor("", "Veriton X2690G"), "acer")
        self.assertEqual(detect_oem_vendor("", "Predator Helios Neo 16"), "acer")
        # MSI (kada Manufacturer nije popunjen)
        self.assertEqual(detect_oem_vendor("", "Prestige 14 Evo"), "msi")
        self.assertEqual(detect_oem_vendor("", "Modern 15"), "msi")
        self.assertEqual(detect_oem_vendor("", "Katana 15 B13V"), "msi")
        self.assertEqual(detect_oem_vendor("", "Stealth 16 Studio"), "msi")
        self.assertEqual(detect_oem_vendor("", "Creator Z16"), "msi")

        # Anti-kolizije (word boundary & eksplicitni brend prioritet)
        self.assertEqual(detect_oem_vendor("Generic", "COMPONENT_BOARD"), "")
        self.assertEqual(detect_oem_vendor("Generic", "ENVIRONMENTAL_PC"), "")
        self.assertEqual(detect_oem_vendor("ASUS", "ZenBook Pro Creator"), "asus")
        self.assertEqual(detect_oem_vendor("Microsoft Corporation", "Virtual Machine"), "")


class TestIMP110MultiRootReleasesPruning(unittest.TestCase):
    """Testovi za IMP-110: multi-root prune_old_releases u server.py i wis_nas_space_guard.py."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v355_imp110_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    @staticmethod
    def _seed_release_root(root_dir: str, versions: list[str], active_id: str) -> str:
        rel_dir = os.path.join(root_dir, "releases")
        os.makedirs(rel_dir, exist_ok=True)
        for i, v in enumerate(versions):
            v_dir = os.path.join(rel_dir, v)
            os.makedirs(v_dir, exist_ok=True)
            with open(os.path.join(v_dir, "wis-payload.zip"), "wb") as f:
                f.write(b"ZIP_DATA_" * 50)
            ts = 10000 + i * 100
            os.utime(v_dir, (ts, ts))
        with open(os.path.join(root_dir, "current.json"), "w", encoding="utf-8") as cf:
            json.dump({"release_id": active_id}, cf)
        return rel_dir

    def test_nightly_gc_step_prunes_primary_and_secondary_roots_with_current_json_protection(self):
        primary_root = os.path.join(self.tmp_dir, "primary_cache")
        secondary_root = os.path.join(self.tmp_dir, "nas_shared_root")

        p_rel = self._seed_release_root(primary_root, ["v1", "v2", "v3", "v4", "v5"], active_id="v1")
        s_rel = self._seed_release_root(secondary_root, ["s1", "s2", "s3", "s4", "s5"], active_id="s1")

        # Symlink release u sekundarnom korenu ne sme biti dirnut
        outside_dir = os.path.join(self.tmp_dir, "outside_keep")
        os.makedirs(outside_dir, exist_ok=True)
        os.symlink(outside_dir, os.path.join(s_rel, "s0_symlink"))

        pool_mgr = MagicMock()
        pool_mgr.prune_unused_packages.return_value = {"removed_packages": 0, "removed_bundles": 0}
        lock = threading.Lock()

        gc_res = _run_nightly_gc_step(
            pool_mgr,
            primary_root,
            lock,
            keep_last=3,
            extra_release_roots=[secondary_root],
        )
        self.assertEqual(gc_res["status"], "ok")
        self.assertEqual(sorted(gc_res["releases"]["removed"]), ["s2", "v2"])
        self.assertTrue(os.path.isdir(os.path.join(p_rel, "v1")))
        self.assertFalse(os.path.isdir(os.path.join(p_rel, "v2")))
        self.assertTrue(os.path.isdir(os.path.join(s_rel, "s1")))
        self.assertFalse(os.path.isdir(os.path.join(s_rel, "s2")))
        self.assertTrue(os.path.isdir(outside_dir))

    def test_nas_space_guard_prunes_secondary_releases_with_lsof_and_corrupt_current_json_guard(self):
        bundles_dir = os.path.join(self.tmp_dir, "bundles")
        os.makedirs(bundles_dir, exist_ok=True)

        nas_root_1 = os.path.join(self.tmp_dir, "nas_root_1")
        nas_root_2 = os.path.join(self.tmp_dir, "nas_root_2")
        nas_root_corrupt = os.path.join(self.tmp_dir, "nas_root_corrupt")

        r1 = self._seed_release_root(nas_root_1, ["r1", "r2", "r3", "r4", "r5"], active_id="r1")
        r2 = self._seed_release_root(nas_root_2, ["a1", "a2", "a3", "a4", "a5"], active_id="a5")
        rc = self._seed_release_root(nas_root_corrupt, ["c1", "c2", "c3", "c4", "c5"], active_id="c5")
        # Ošteti current.json u nas_root_corrupt -> fail-safe, ništa ne sme biti obrisano!
        with open(os.path.join(nas_root_corrupt, "current.json"), "w", encoding="utf-8") as cf:
            cf.write("{BROKEN_JSON")

        # Simuliramo da je a1/wis-payload.zip otvoren od strane smbd -> a1 ostaje, a2 se briše!
        open_file = os.path.realpath(os.path.join(r2, "a1", "wis-payload.zip"))

        def mock_disk_usage(_path):
            return SimpleNamespace(total=500 * (1024 ** 3), used=200 * (1024 ** 3), free=300 * (1024 ** 3))

        def mock_lsof(_paths):
            return {open_file: "smbd"}, True

        res = wis_nas_space_guard.run_space_guard(
            bundles_dir=bundles_dir,
            tmp_scan_dirs=(bundles_dir,),
            now=time.time(),
            open_files_fn=mock_lsof,
            disk_usage_fn=mock_disk_usage,
            release_roots=(nas_root_1, nas_root_2, nas_root_corrupt),
            keep_last_releases=3,
        )
        self.assertEqual(res["mode"], "NORMAL")
        # r2 obrisan iz nas_root_1, a2 obrisan iz nas_root_2 (a1 preskočen zbog lsof), 0 iz corrupt
        self.assertEqual(res["releases_removed"], 2)
        self.assertEqual(res["skipped_open"], 1)
        self.assertTrue(os.path.isdir(os.path.join(r1, "r1")))
        self.assertFalse(os.path.isdir(os.path.join(r1, "r2")))
        self.assertTrue(os.path.isdir(os.path.join(r2, "a1")))
        self.assertFalse(os.path.isdir(os.path.join(r2, "a2")))
        for c_ver in ("c1", "c2", "c3", "c4", "c5"):
            self.assertTrue(os.path.isdir(os.path.join(rc, c_ver)))

    def test_server_env_release_roots_and_space_guard_fail_safe_guards(self):
        env_root = os.path.join(self.tmp_dir, "env_rel_root")
        e_rel = self._seed_release_root(env_root, ["e1", "e2", "e3", "e4", "e5"], active_id="e1")
        primary = os.path.join(self.tmp_dir, "empty_primary")
        os.makedirs(primary, exist_ok=True)

        pool_mgr = MagicMock()
        pool_mgr.prune_unused_packages.return_value = {"removed_packages": 0, "removed_bundles": 0}
        with patch.dict(os.environ, {"SERVIS_RELEASE_ROOTS": env_root}):
            gc_res = _run_nightly_gc_step(pool_mgr, primary, threading.Lock(), keep_last=3)
        self.assertEqual(gc_res["releases"]["removed"], ["e2"])
        self.assertTrue(os.path.isdir(os.path.join(e_rel, "e1")))
        self.assertFalse(os.path.isdir(os.path.join(e_rel, "e2")))

        # Symlink current.json i lsof_ok=False moraju fail-safe blokirati brisanje
        sym_root = os.path.join(self.tmp_dir, "sym_current_root")
        s_rel = self._seed_release_root(sym_root, ["k1", "k2", "k3", "k4", "k5"], active_id="k5")
        os.remove(os.path.join(sym_root, "current.json"))
        ext_cur = os.path.join(self.tmp_dir, "ext_current.json")
        with open(ext_cur, "w", encoding="utf-8") as f:
            json.dump({"release_id": "k5"}, f)
        os.symlink(ext_cur, os.path.join(sym_root, "current.json"))

        res_sym = wis_nas_space_guard.prune_old_releases_dir(sym_root, keep_last=3, open_files_fn=lambda _: ({}, True))
        self.assertEqual(res_sym["status"], "skipped_symlink_current")
        self.assertEqual(res_sym["removed"], [])
        self.assertTrue(os.path.isdir(os.path.join(s_rel, "k1")))

        lsof_fail_root = os.path.join(self.tmp_dir, "lsof_fail_root")
        lf_rel = self._seed_release_root(lsof_fail_root, ["l1", "l2", "l3", "l4", "l5"], active_id="l5")
        res_lf = wis_nas_space_guard.prune_old_releases_dir(lsof_fail_root, keep_last=3, open_files_fn=lambda _: ({}, False))
        self.assertFalse(res_lf["lsof_ok"])
        self.assertEqual(res_lf["removed"], [])
        self.assertTrue(os.path.isdir(os.path.join(lf_rel, "l1")))


class TestSec05AndSec06Hardening(unittest.TestCase):
    """Regresioni testovi za SEC-05 (3xx SSRF Open Redirect zaštita) i SEC-06 (Firmware ClassGuid/CatalogFile paritet)."""

    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_sec0506_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_sec05_302_redirect_outside_allowlist_blocked_without_following(self):
        from http.server import BaseHTTPRequestHandler, HTTPServer
        from servishub.drivers.oem.catalogs import OemCatalogManager, open_safe_oem_url
        from servishub.drivers.oem.microsoft_surface import (
            discover_surface_msi_entries,
            verify_surface_msi_url,
        )

        trap_hits = []

        class TrapHandler(BaseHTTPRequestHandler):
            def do_HEAD(self):
                trap_hits.append(("HEAD", self.path))
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Length", "1024")
                self.end_headers()

            def do_GET(self):
                trap_hits.append(("GET", self.path))
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.end_headers()
                self.wfile.write(b"SECRET_INTERNAL_PAYLOAD" * 64)

            def log_message(self, fmt, *args):
                pass

        trap_srv = HTTPServer(("127.0.0.1", 0), TrapHandler)
        trap_port = trap_srv.server_address[1]
        trap_thr = threading.Thread(target=trap_srv.serve_forever, daemon=True)
        trap_thr.start()

        class RedirectHandler(BaseHTTPRequestHandler):
            def _send_redirect(self, loc):
                self.send_response(302)
                self.send_header("Location", loc)
                self.end_headers()

            def do_HEAD(self):
                if self.path == "/redir-evil":
                    self._send_redirect(f"http://127.0.0.1:{trap_port}/secret.msi")
                elif self.path == "/redir-external":
                    self._send_redirect("https://evil.example/payload.msi")
                elif self.path == "/redir-allowed":
                    self._send_redirect("/ok.msi")
                elif self.path == "/redir-loop":
                    self._send_redirect("/redir-loop")
                elif self.path == "/ok.msi":
                    self.send_response(200)
                    self.send_header("Content-Type", "application/octet-stream")
                    self.send_header("Content-Length", "2048")
                    self.end_headers()
                else:
                    self.send_response(404)
                    self.end_headers()

            def do_GET(self):
                if self.path == "/redir-evil":
                    self._send_redirect(f"http://127.0.0.1:{trap_port}/secret.msi")
                elif self.path == "/redir-external":
                    self._send_redirect("https://evil.example/payload.msi")
                elif self.path == "/redir-allowed":
                    self._send_redirect("/ok.msi")
                elif self.path == "/redir-loop":
                    self._send_redirect("/redir-loop")
                elif self.path == "/ok.msi":
                    self.send_response(200)
                    self.send_header("Content-Type", "application/octet-stream")
                    body = b"VALID_MSI_DATA_" * 100
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:
                    self.send_response(404)
                    self.end_headers()

            def log_message(self, fmt, *args):
                pass

        # Slušamo na localhost, a allowlista sadrži isključivo 'localhost' (ne '127.0.0.1' niti 'evil.example')
        redir_srv = HTTPServer(("127.0.0.1", 0), RedirectHandler)
        redir_port = redir_srv.server_address[1]
        redir_thr = threading.Thread(target=redir_srv.serve_forever, daemon=True)
        redir_thr.start()

        try:
            allowed = ["localhost"]
            # 1. verify_surface_msi_url odbija 302 ka 127.0.0.1 i ka evil.example bez kontakta sa trap serverom
            ok_evil, sz_evil = verify_surface_msi_url(
                f"http://localhost:{redir_port}/redir-evil",
                timeout=3.0,
                allowed_hosts=allowed,
            )
            self.assertFalse(ok_evil)
            self.assertEqual(sz_evil, 0)
            self.assertEqual(trap_hits, [], "302 redirekcija ka 127.0.0.1 ne sme uopšte biti ispraćena!")

            ok_ext, _ = verify_surface_msi_url(
                f"http://localhost:{redir_port}/redir-external",
                timeout=3.0,
                allowed_hosts=allowed,
            )
            self.assertFalse(ok_ext)

            # 2. Dozvoljena relativna 302 redirekcija na istom allowlist hostu prolazi
            ok_rel, sz_rel = verify_surface_msi_url(
                f"http://localhost:{redir_port}/redir-allowed",
                timeout=3.0,
                allowed_hosts=allowed,
            )
            self.assertTrue(ok_rel)
            self.assertEqual(sz_rel, 2048)

            # 3. Prekoračenje limita redirekcija (loop > 5 skokova) biva odbijeno
            ok_loop, _ = verify_surface_msi_url(
                f"http://localhost:{redir_port}/redir-loop",
                timeout=3.0,
                allowed_hosts=allowed,
            )
            self.assertFalse(ok_loop)

            # 4. discover_surface_msi_entries odbija 302 van allowliste bez praćenja
            entries = discover_surface_msi_entries(
                spec={"download_page_url": f"http://localhost:{redir_port}/redir-evil"},
                timeout=3.0,
                verify_head=False,
                allowed_hosts=allowed,
            )
            self.assertEqual(entries, [])
            self.assertEqual(trap_hits, [])

            # 5. OemCatalogManager._http_fetch_bytes i download_driver_pack bacaju ValueError na 302 van allowliste
            mgr = OemCatalogManager(
                catalogs_dir=os.path.join(self.tmp_dir, "cats"),
                allowed_hosts=allowed,
            )
            with self.assertRaises(ValueError):
                mgr._http_fetch_bytes(f"http://localhost:{redir_port}/redir-evil", timeout=3.0)
            with self.assertRaises(ValueError):
                mgr.download_driver_pack(
                    entry={
                        "package_name": "test.msi",
                        "download_url": f"http://localhost:{redir_port}/redir-evil",
                    },
                    dest_dir=os.path.join(self.tmp_dir, "dl"),
                    timeout=3.0,
                )
            self.assertEqual(trap_hits, [])

            # 6. Direktna provera open_safe_oem_url za dozvoljeni redirect
            import urllib.request as _ur
            with open_safe_oem_url(
                _ur.Request(f"http://localhost:{redir_port}/redir-allowed"),
                timeout=3.0,
                allowed_hosts=allowed,
            ) as resp:
                self.assertTrue(resp.geturl().endswith("/ok.msi"))
                self.assertIn(b"VALID_MSI_DATA_", resp.read())
        finally:
            redir_srv.shutdown()
            redir_srv.server_close()
            trap_srv.shutdown()
            trap_srv.server_close()

    def test_sec06_firmware_class_guid_and_catalog_file_parity(self):
        from servishub.drivers.pool import is_blocked_harvest_firmware_package

        pool = DriverPoolManager(os.path.join(self.tmp_dir, "pool_sec06"))
        src = os.path.join(self.tmp_dir, "fw_guid_src")

        # 1. INF sa neutralnim imenom i Class=System, ali Windows Firmware ClassGuid
        guid_dir = os.path.join(src, "fw_by_guid")
        os.makedirs(guid_dir, exist_ok=True)
        guid_inf = os.path.join(guid_dir, "NeutralComponent.inf")
        with open(guid_inf, "w", encoding="utf-8") as f:
            f.write(
                "[Version]\n"
                'Signature="$WINDOWS NT$"\n'
                "Class=System\n"
                "ClassGuid={f2e7dd72-6468-4e36-b6f1-6488f42c1b52}\n"
                'Provider="Vendor"\n'
                "DriverVer=03/10/2025,1.2.3.4\n"
                "CatalogFile=neutral.cat\n"
                "[Models]\n"
                "Dev = Inst, ACPI\\MSHW7771\n"
            )
        with open(os.path.join(guid_dir, "neutral.cat"), "wb") as f:
            f.write(b"CAT" * 8)

        # 2. INF sa neutralnim imenom i neutralnom klasom/GUID-om, ali CatalogFile za firmware/CFU
        cat_dir = os.path.join(src, "fw_by_cat")
        os.makedirs(cat_dir, exist_ok=True)
        cat_inf = os.path.join(cat_dir, "NeutralHelper.inf")
        with open(cat_inf, "w", encoding="utf-8") as f:
            f.write(
                "[Version]\n"
                'Signature="$WINDOWS NT$"\n'
                "Class=HIDClass\n"
                "ClassGuid={745a17a0-74d3-11d0-b6fe-00a0c90f57da}\n"
                'Provider="Vendor"\n'
                "DriverVer=03/10/2025,1.2.3.4\n"
                "CatalogFile=SurfaceCFUOverHid_x64.cat\n"
                "[Models]\n"
                "Dev = Inst, HID\\VEN_MSHW&DEV_7772\n"
            )
        with open(os.path.join(cat_dir, "SurfaceCFUOverHid_x64.cat"), "wb") as f:
            f.write(b"CAT" * 8)

        meta_guid = parse_inf_metadata(guid_inf)
        self.assertEqual(meta_guid["class_guid"], "f2e7dd72-6468-4e36-b6f1-6488f42c1b52")
        self.assertTrue(is_blocked_firmware_inf(meta_guid["inf_name"], meta_guid["driver_class"], class_guid=meta_guid["class_guid"]))

        meta_cat = parse_inf_metadata(cat_inf)
        self.assertTrue(is_blocked_firmware_inf(meta_cat["inf_name"], meta_cat["driver_class"], catalog_files=meta_cat["catalog_files"]))

        # Ingest iz harvest izvora odbija oba
        res = pool.ingest_directory(src, source="harvest", model="Surface_Test_Sec06", os_build="26200")
        self.assertEqual(res["packages_added"], 0)
        self.assertEqual(res["rejected_count"], 2)

        # delta_check takođe odbija oba (po class_guid i po catalog_files)
        dc = pool.delta_check(
            model="Surface_Test_Sec06",
            packages=[
                {"inf_name": "NeutralComponent.inf", "driver_class": "System", "class_guid": "{F2E7DD72-6468-4E36-B6F1-6488F42C1B52}", "hwids": [r"ACPI\MSHW7771"]},
                {"inf_name": "NeutralHelper.inf", "driver_class": "HIDClass", "catalog_files": ["CustomFwUpdate.cat"], "hwids": [r"HID\VEN_MSHW&DEV_7772"]},
            ],
            os_build="26200",
        )
        self.assertEqual(dc["missing_infs"], [])
        self.assertEqual(len(dc["blocked_infs"]), 2)

        # Legacy paket na disku bez class_guid u index.json biva uhvaćen preko pkg_dir provere
        self.assertTrue(
            is_blocked_harvest_firmware_package(
                {"inf_name": "NeutralComponent.inf", "driver_class": "System", "sources": ["harvest"]},
                pkg_dir=guid_dir,
            )
        )
        self.assertTrue(
            is_blocked_harvest_firmware_package(
                {"inf_name": "NeutralHelper.inf", "driver_class": "HIDClass", "sources": ["harvest"]},
                pkg_dir=cat_dir,
            )
        )


if __name__ == "__main__":
    unittest.main()
