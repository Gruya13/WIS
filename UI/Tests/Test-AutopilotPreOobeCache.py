#!/usr/bin/env python3
"""
Test-AutopilotPreOobeCache.py
==============================
Automatizovana regresiona provera za Pre-OOBE hvatanje Autopilot ZTD izvestaja
(autopilot_report.json / AutopilotDDSZTDFile.preoobe.json), ispravku parsiranja
praznog CloudAssignedTenantId, citanje kesa i auto-retry na mrezu u WIS.UI.
"""

import hashlib
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent

PREOOBE_NET_PATH = ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "PreOOBE-Network.ps1"
SETUP_COMPLETE_PATH = ROOT / "ISO" / "SetupComplete.ps1"
CLOUD_ZTD_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "CloudZtdInspector.cs"
PROGRAM_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "Program.cs"
AUDITOR_MAIN_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "MainWindow.cs"
PROVERA_PS1_PATH = ROOT / "UI" / "Skripte" / "Provera_autopilota.ps1"
WIS_HW_PSM1_PATH = ROOT / "UI" / "Skripte" / "Modules" / "WIS.Hardware.psm1"
UI_MAIN_CS_PATH = ROOT / "UI" / "MainWindow.xaml.cs"

SYNC_GROUPS = [
    (
        ROOT / "Tools" / "AutopilotAuditor" / "WIS-AutopilotAuditor.exe",
        [
            ROOT / ".workspace" / "artifacts" / "tools" / "WIS-AutopilotAuditor.exe",
            ROOT / ".workspace" / "artifacts" / "windows" / "WIS-AutopilotAuditor.exe",
            ROOT / "ISO" / "WIS-AutopilotAuditor.exe",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "WIS-AutopilotAuditor.exe",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Skripte" / "WIS-AutopilotAuditor.exe",
            pathlib.Path("/mnt/Servis/WIS-AutopilotAuditor.exe"),
        ],
    ),
    (
        ROOT / "UI" / "WIS.UI.exe",
        [
            ROOT / ".workspace" / "artifacts" / "windows" / "WIS.UI.exe",
            ROOT / "ISO" / "WIS.UI.exe",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "WIS.UI.exe",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Skripte" / "WIS.UI" / "WIS.UI.exe",
            pathlib.Path("/mnt/Servis/WIS.UI.exe"),
            pathlib.Path("/mnt/Servis/wis-payload/WIS.UI.exe"),
            pathlib.Path("/mnt/Servis/wis-payload/WIS.UI/WIS.UI.exe"),
            pathlib.Path("/mnt/Servis/wis-payload/Skripte/WIS.UI/WIS.UI.exe"),
        ],
    ),
    (
        SETUP_COMPLETE_PATH,
        [
            ROOT / "ISO" / "Skripte" / "SetupComplete.ps1",
            ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "SetupComplete.ps1",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "SetupComplete.ps1",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "PreviewSetupScripts" / "SetupComplete.ps1",
            ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "SetupComplete.ps1",
            pathlib.Path("/mnt/Servis/wis-payload/SetupComplete.ps1"),
            pathlib.Path("/mnt/Servis/wis-payload/Skripte/SetupComplete.ps1"),
        ],
    ),
    (
        PREOOBE_NET_PATH,
        [
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "PreviewSetupScripts" / "PreOOBE-Network.ps1",
            ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "PreOOBE-Network.ps1",
        ],
    ),
    (
        PROVERA_PS1_PATH,
        [
            ROOT / "ISO" / "Provera_autopilota.ps1",
            ROOT / "ISO" / "Skripte" / "Provera_autopilota.ps1",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Provera_autopilota.ps1",
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Skripte" / "Provera_autopilota.ps1",
            pathlib.Path("/mnt/Servis/Provera_autopilota.ps1"),
            pathlib.Path("/mnt/Servis/wis-payload/Provera_autopilota.ps1"),
            pathlib.Path("/mnt/Servis/wis-payload/Skripte/Provera_autopilota.ps1"),
        ],
    ),
    (
        WIS_HW_PSM1_PATH,
        [
            ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Skripte" / "Modules" / "WIS.Hardware.psm1",
            pathlib.Path("/mnt/Servis/Skripte/Modules/WIS.Hardware.psm1"),
            pathlib.Path("/mnt/Servis/wis-payload/Modules/WIS.Hardware.psm1"),
            pathlib.Path("/mnt/Servis/wis-payload/Skripte/Modules/WIS.Hardware.psm1"),
        ],
    ),
]


def sha256_file(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class TestAutopilotPreOobeCache(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.preoobe_src = PREOOBE_NET_PATH.read_text(encoding="utf-8")
        cls.setup_complete_src = SETUP_COMPLETE_PATH.read_text(encoding="utf-8")
        cls.cloud_ztd_src = CLOUD_ZTD_CS_PATH.read_text(encoding="utf-8")
        cls.program_src = PROGRAM_CS_PATH.read_text(encoding="utf-8")
        cls.auditor_main_src = AUDITOR_MAIN_CS_PATH.read_text(encoding="utf-8")
        cls.provera_src = PROVERA_PS1_PATH.read_text(encoding="utf-8")
        cls.hw_src = WIS_HW_PSM1_PATH.read_text(encoding="utf-8")
        cls.ui_main_src = UI_MAIN_CS_PATH.read_text(encoding="utf-8")

    def test_01_preoobe_scripts_stabilize_network_and_save_report(self):
        """PreOOBE-Network.ps1 i SetupComplete.ps1 moraju stabilizovati mrezu i sacuvati autopilot_report.json."""
        self.assertIn("Get-NetIPAddress", self.preoobe_src)
        self.assertIn("tbs", self.preoobe_src)
        self.assertIn("\\Microsoft\\Windows\\Management\\Provisioning\\Autopilot", self.preoobe_src)

        self.assertIn("AutopilotPreOOBE", self.setup_complete_src)
        self.assertIn("SkipNetworkSetup", self.setup_complete_src)
        self.assertIn("DisableOOBEConnection", self.setup_complete_src)
        self.assertIn("ztd.dds.microsoft.com", self.setup_complete_src)
        self.assertIn("autopilot_report.json", self.setup_complete_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.setup_complete_src)
        self.assertIn("WIS-AutopilotAuditor.exe", self.setup_complete_src)

    def test_02_csharp_auditor_caches_and_parses_empty_ztd_profile(self):
        """CloudZtdInspector.cs mora imati TryParseZtdJsonContent (prazan CloudAssignedTenantId -> ConfirmedFree), TryLoadCachedReport i SaveCachedReport."""
        self.assertIn("public static bool TryParseZtdJsonContent", self.cloud_ztd_src)
        self.assertIn("public static bool TryLoadCachedReport", self.cloud_ztd_src)
        self.assertIn("public static void SaveCachedReport", self.cloud_ztd_src)
        self.assertIn("if (TryLoadCachedReport(targetSerial, report))", self.cloud_ztd_src)
        self.assertIn("807 - ZtdDeviceIsNotRegistered (ZTD File)", self.cloud_ztd_src)
        self.assertIn("PREOOBE_CACHED_REPORT", self.cloud_ztd_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.cloud_ztd_src)
        self.assertIn("IsReportContentConfirmed", self.cloud_ztd_src)

        self.assertIn("CloudZtdInspector.SaveCachedReport(result)", self.program_src)
        self.assertIn("CloudZtdInspector.SaveCachedReport(evaluated)", self.auditor_main_src)

    def test_03_powershell_auditor_and_hardware_module_use_cache(self):
        """Provera_autopilota.ps1 i WIS.Hardware.psm1 moraju koristiti sacuvani Pre-OOBE izvestaj bez degradacije."""
        self.assertFalse(PROVERA_PS1_PATH.read_bytes().startswith(b"\xef\xbb\xbf"), "Provera_autopilota.ps1 ne sme imati UTF-8 BOM")
        self.assertIn("function Resolve-ZtdJsonContent", self.provera_src)
        self.assertIn("function Test-SavedAutopilotReportConfirmed", self.provera_src)
        self.assertIn("PREOOBE_CACHED_REPORT", self.provera_src)
        self.assertIn("807 - ZtdDeviceIsNotRegistered (ZTD File)", self.provera_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.provera_src)
        self.assertIn("autopilot_report.json", self.provera_src)

        self.assertIn("autopilot_report.json", self.hw_src)
        self.assertRegex(self.hw_src, r"LOCKED_TENANT.*HIDDEN_LOCK")

    def test_04_wis_ui_uses_cached_report_and_retries_on_network_ready(self):
        """UI/MainWindow.xaml.cs mora odmah ucitati potvrdjen kes, stititi od degradacije i ponoviti sken po aktivaciji mreze."""
        self.assertIn("_autopilotRetriedAfterNetwork", self.ui_main_src)
        self.assertIn("IsAutopilotMatrixConfirmed", self.ui_main_src)
        self.assertIn("TryReadCachedAutopilotReport", self.ui_main_src)
        self.assertIn("TryRetryAutopilotOnNetworkReady", self.ui_main_src)
        self.assertIn(r"C:\Windows\Logs\WIS\autopilot_report.json", self.ui_main_src)
        self.assertIn('stepId.StartsWith("P1."', self.ui_main_src)

    def test_05_all_synced_copies_have_identical_sha256(self):
        """Sve sinhronizovane kopije skripti i binara u ISO/, artifacts/ i /mnt/Servis/ moraju imati identican SHA-256."""
        for src, targets in SYNC_GROUPS:
            self.assertTrue(src.is_file(), f"Izvorni fajl ne postoji: {src}")
            expected = sha256_file(src)
            for dst in targets:
                self.assertTrue(dst.is_file(), f"Odredisni fajl ne postoji: {dst}")
                self.assertEqual(sha256_file(dst), expected, f"SHA-256 neuskladjen za {dst}")


if __name__ == "__main__":
    unittest.main()
