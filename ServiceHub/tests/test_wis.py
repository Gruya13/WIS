"""
tests.test_wis
==============
Testovi za servishub.wis paket:
- list_removable_usb_devices i Safety Guardrails (is_safe, blocked_reason)
- WisUsbEngine (GPT FAT32 + NTFS particionisanje i formatiranje)
- WisIsoManager (detekcija ISO, keširan SHA-256 proračun, ekstrakcija na USB)
"""

import json
import os
import shutil
import tempfile
import time
import unittest
from unittest.mock import MagicMock

from servishub.wis import (
    WisIsoManager,
    WisUsbEngine,
    extract_iso_version,
    format_bytes,
    get_partition_device_path,
    get_wis_iso_info,
    list_removable_usb_devices,
)
from servishub.wis.usb import get_active_zpool_devices, inspect_device


class TestWisUsb(unittest.TestCase):

    def setUp(self):
        self.mock_lsblk_data = {
            "blockdevices": [
                {
                    "name": "nvme0n1",
                    "size": 256060514304,
                    "model": "SK hynix BC501",
                    "tran": "nvme",
                    "rm": False,
                    "hotplug": False,
                    "mountpoints": [],
                    "children": [
                        {
                            "name": "nvme0n1p1",
                            "size": 1073741824,
                            "tran": "nvme",
                            "rm": False,
                            "hotplug": False,
                            "mountpoints": ["/boot/efi"],
                        },
                        {
                            "name": "nvme0n1p2",
                            "size": 254985707008,
                            "tran": "nvme",
                            "rm": False,
                            "hotplug": False,
                            "mountpoints": ["/"],
                        },
                    ],
                },
                {
                    "name": "sdb",
                    "size": 32007028736,
                    "model": "SanDisk Ultra",
                    "tran": "usb",
                    "rm": True,
                    "hotplug": True,
                    "mountpoints": [],
                    "children": [
                        {
                            "name": "sdb1",
                            "size": 2097152000,
                            "tran": "usb",
                            "rm": True,
                            "hotplug": True,
                            "mountpoints": ["/media/gru/UEFI_BOOT"],
                        },
                        {
                            "name": "sdb2",
                            "size": 29909876736,
                            "tran": "usb",
                            "rm": True,
                            "hotplug": True,
                            "mountpoints": ["/media/gru/WIS_DATA"],
                        },
                    ],
                },
                {
                    "name": "sdc",
                    "size": 64000000000,
                    "model": "Kingston DataTraveler",
                    "tran": "usb",
                    "rm": True,
                    "hotplug": True,
                    "mountpoints": [],
                    "children": [
                        {
                            "name": "sdc1",
                            "size": 64000000000,
                            "tran": "usb",
                            "rm": True,
                            "hotplug": True,
                            "mountpoints": ["/home"],  # OVO JE SISTEMSKA PUTANJA!
                        }
                    ],
                },
                {
                    "name": "sdd",
                    "size": 16000000000,
                    "model": "ZFS USB Pool Member",
                    "tran": "usb",
                    "rm": True,
                    "hotplug": True,
                    "fstype": "zfs_member",
                    "mountpoints": [],
                },
            ]
        }
        self.mock_json_str = json.dumps(self.mock_lsblk_data)

    def test_format_bytes(self):
        self.assertEqual(format_bytes(0), "0 B")
        self.assertEqual(format_bytes(512), "512 B")
        self.assertEqual(format_bytes(1024 * 1024), "1.0 MiB")
        self.assertEqual(format_bytes(32007028736), "29.8 GiB")

    def test_get_partition_device_path(self):
        self.assertEqual(get_partition_device_path("/dev/sdb", 1), "/dev/sdb1")
        self.assertEqual(get_partition_device_path("/dev/sdb", 2), "/dev/sdb2")
        self.assertEqual(get_partition_device_path("/dev/nvme0n1", 1), "/dev/nvme0n1p1")
        self.assertEqual(get_partition_device_path("/dev/loop0", 2), "/dev/loop0p2")

    def test_list_removable_usb_devices_guardrails(self):
        devs = list_removable_usb_devices(mock_json=self.mock_json_str)
        # Treba da vrati samo USB diskove (sdb, sdc, sdd)
        names = [d["name"] for d in devs]
        self.assertIn("sdb", names)
        self.assertIn("sdc", names)
        self.assertIn("sdd", names)
        self.assertNotIn("nvme0n1", names)

        # Provera sdb: Bezbedan USB
        sdb = next(d for d in devs if d["name"] == "sdb")
        self.assertTrue(sdb["is_safe"])
        self.assertIsNone(sdb["blocked_reason"])
        self.assertEqual(sdb["device_path"], "/dev/sdb")
        self.assertEqual(sdb["transport"], "usb")
        self.assertTrue(sdb["removable"])
        self.assertEqual(sdb["size_formatted"], "29.8 GiB")

        # Provera sdc: Nebezbedan USB jer ima /home montiran
        sdc = next(d for d in devs if d["name"] == "sdc")
        self.assertFalse(sdc["is_safe"])
        self.assertEqual(sdc["blocked_reason"], "Sistemski disk / zpool")

        # Provera sdd: Nebezbedan jer ima zfs_member fstype
        sdd = next(d for d in devs if d["name"] == "sdd")
        self.assertFalse(sdd["is_safe"])
        self.assertEqual(sdd["blocked_reason"], "Sistemski disk / zpool")

    def test_inspect_device_system_nvme(self):
        dev = inspect_device("/dev/nvme0n1", mock_json=self.mock_json_str)
        self.assertIsNotNone(dev)
        self.assertFalse(dev["is_safe"])
        self.assertEqual(dev["blocked_reason"], "Sistemski disk / zpool")

    def test_wis_usb_engine_rejects_without_confirm_wipe(self):
        engine = WisUsbEngine(dry_run=True)
        with self.assertRaises(ValueError) as ctx:
            engine.format_uefi_usb("/dev/sdb", confirm_wipe=False, mock_json=self.mock_json_str)
        self.assertIn("confirm_wipe=True", str(ctx.exception))

    def test_wis_usb_engine_rejects_unsafe_system_device(self):
        engine = WisUsbEngine(dry_run=True)
        # Pokušaj formatiranja /dev/nvme0n1
        with self.assertRaises(RuntimeError) as ctx:
            engine.format_uefi_usb("/dev/nvme0n1", confirm_wipe=True, mock_json=self.mock_json_str)
        self.assertIn("blokirano", str(ctx.exception))

        # Pokušaj formatiranja USB diska koji montira /home (/dev/sdc)
        with self.assertRaises(RuntimeError) as ctx:
            engine.format_uefi_usb("/dev/sdc", confirm_wipe=True, mock_json=self.mock_json_str)
        self.assertIn("Sistemski disk / zpool", str(ctx.exception))

    def test_wis_usb_engine_successful_formatting_flow(self):
        engine = WisUsbEngine(dry_run=True)
        callbacks = []

        def on_progress(p):
            callbacks.append(p)

        res = engine.format_uefi_usb(
            "/dev/sdb",
            confirm_wipe=True,
            progress_callback=on_progress,
            fat32_size_mb=2048,
            mock_json=self.mock_json_str,
        )

        self.assertEqual(res["status"], "success")
        self.assertEqual(res["fat32_partition"], "/dev/sdb1")
        self.assertEqual(res["ntfs_partition"], "/dev/sdb2")
        self.assertEqual(res["fat32_label"], "UEFI_BOOT")
        self.assertEqual(res["ntfs_label"], "WIS_DATA")

        # Provera emitovanih faza
        phases = [c["phase"] for c in callbacks]
        self.assertIn("unmounting", phases)
        self.assertIn("wiping", phases)
        self.assertIn("partitioning", phases)
        self.assertIn("formatting_fat32", phases)
        self.assertIn("formatting_ntfs", phases)
        self.assertIn("completed", phases)
        self.assertEqual(callbacks[-1]["percent"], 100)


class TestWisIso(unittest.TestCase):

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.wis_dir = os.path.join(self.test_dir, "wis")
        os.makedirs(self.wis_dir, exist_ok=True)

        # Kreiranje testne dummy ISO datoteke
        self.dummy_iso_path = os.path.join(self.wis_dir, "wis-v2.4.0.iso")
        with open(self.dummy_iso_path, "wb") as f:
            f.write(b"CD001" + b"\x00" * 2048 + b"WIS INSTALLATION MEDIA SIMULATION")

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_extract_iso_version(self):
        self.assertEqual(extract_iso_version("wis-v2.4.0.iso"), "2.4.0")
        self.assertEqual(extract_iso_version("WIS_Win11_v1.5.iso"), "1.5")
        self.assertEqual(extract_iso_version("wis-2026-10-09.iso"), "2026-10-09")
        self.assertEqual(extract_iso_version("custom_installer.iso"), "1.0.0")

    def test_get_iso_info_with_caching(self):
        manager = WisIsoManager(wis_dir=self.wis_dir)
        info1 = manager.get_iso_info(self.dummy_iso_path)

        self.assertTrue(info1["available"])
        self.assertEqual(info1["filename"], "wis-v2.4.0.iso")
        self.assertEqual(info1["version"], "2.4.0")
        self.assertGreater(info1["size_bytes"], 0)
        self.assertIsNotNone(info1["sha256"])
        self.assertEqual(len(info1["sha256"]), 64)

        # Provera da je kreiran .sha256 companion fajl
        sha_file = f"{self.dummy_iso_path}.sha256"
        self.assertTrue(os.path.isfile(sha_file))
        with open(sha_file, "r") as f:
            content = f.read()
        self.assertIn(info1["sha256"], content)

        # Drugi poziv treba da koristi keš
        info2 = manager.get_iso_info(self.dummy_iso_path)
        self.assertEqual(info1["sha256"], info2["sha256"])

    def test_get_iso_info_auto_detection(self):
        manager = WisIsoManager(wis_dir=self.wis_dir)
        info = manager.get_iso_info()  # Bez eksplicitne putanje, nalazi wis-v2.4.0.iso

        self.assertTrue(info["available"])
        self.assertEqual(info["filename"], "wis-v2.4.0.iso")
        self.assertEqual(info["version"], "2.4.0")

    def test_get_iso_info_non_existent(self):
        manager = WisIsoManager(wis_dir=self.wis_dir)
        info = manager.get_iso_info("/non/existent/path.iso")

        self.assertFalse(info["available"])
        self.assertEqual(info["size_bytes"], 0)
        self.assertIsNone(info["sha256"])

    def test_extract_iso_to_usb_dry_run(self):
        manager = WisIsoManager(wis_dir=self.wis_dir, dry_run=True)
        callbacks = []

        def on_progress(p):
            callbacks.append(p)

        target_dir = os.path.join(self.test_dir, "usb_target")
        res = manager.extract_iso_to_usb(
            self.dummy_iso_path,
            target_dir,
            progress_callback=on_progress,
        )

        self.assertEqual(res["status"], "success")
        self.assertTrue(res.get("dry_run"))

        # Provera callback faza
        phases = [c["phase"] for c in callbacks]
        self.assertIn("preparing", phases)
        self.assertIn("extracting", phases)
        self.assertIn("syncing", phases)
        self.assertIn("completed", phases)
        self.assertEqual(callbacks[-1]["percent"], 100)

    def test_extract_iso_non_existent_file(self):
        manager = WisIsoManager(wis_dir=self.wis_dir, dry_run=True)
        with self.assertRaises(FileNotFoundError):
            manager.extract_iso_to_usb("/invalid/iso.iso", "/target/usb")

    def test_get_wis_iso_info_helper(self):
        info = get_wis_iso_info(self.dummy_iso_path)
        self.assertTrue(info["available"])
        self.assertEqual(info["filename"], "wis-v2.4.0.iso")


if __name__ == "__main__":
    unittest.main()
