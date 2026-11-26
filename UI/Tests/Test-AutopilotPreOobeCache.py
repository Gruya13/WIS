#!/usr/bin/env python3
"""
Test-AutopilotPreOobeCache.py
==============================
Automatizovana regresiona provera za IMP-112 (Golden ISO marker + WinPE zastita)
i IMP-114 (Besplatna Opcija 1 $0: Sloj A Auditor/Skripte sa wmansvc primarnom
putanjom, popravljenom lokalnom forenzikom i binarnim modelom; Sloj B WinPE
pre-format forenzika; Sloj C pravi OOBE ZTD presretac).
"""

import ast
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent

PREOOBE_NET_CANONICAL = ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "PreOOBE-Network.ps1"
PREOOBE_NET_OEM = ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "PreOOBE-Network.ps1"
PREOOBE_NET_PATH = PREOOBE_NET_OEM if PREOOBE_NET_OEM.is_file() else PREOOBE_NET_CANONICAL
PREOOBE_CMD_CANONICAL = ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "PreOOBE-Network.cmd"
PREOOBE_CMD_OEM = ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "PreOOBE-Network.cmd"
SETUP_COMPLETE_PATH = ROOT / "ISO" / "SetupComplete.ps1"
AUTOUNATTEND_PATH = ROOT / "ISO" / "autounattend.xml"
BUILD_ISO_PY_PATH = ROOT / "ISO" / "Skripte" / "Build-Iso.py"
PATCH_BOOTWIM_PY_PATH = ROOT / "ISO" / "Skripte" / "Patch-BootWim.py"
WINPE_FORENSICS_PS1_PATH = ROOT / "ISO" / "Skripte" / "Invoke-WinPeAutopilotForensics.ps1"
ACCEL_PE_PATH = ROOT / "ISO" / "WIS-AcceleratePE.cmd"

CLOUD_ZTD_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "CloudZtdInspector.cs"
LOCAL_FORENSICS_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "LocalForensicsInspector.cs"
DECISION_MATRIX_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "DecisionMatrix.cs"
MODEL_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "Model.cs"
REPORT_EXPORTER_CS_PATH = ROOT / "Tools" / "AutopilotAuditor" / "ReportExporter.cs"
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
        ACCEL_PE_PATH,
        [
            ROOT / "ISO" / "Skripte" / "WIS-AcceleratePE.cmd",
            ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "WIS-AcceleratePE.cmd",
            ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "WIS-AcceleratePE.cmd",
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
        PREOOBE_CMD_CANONICAL,
        [
            PREOOBE_CMD_OEM,
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


def load_build_iso_module():
    spec = importlib.util.spec_from_file_location("wis_build_iso_test", str(BUILD_ISO_PY_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class TestAutopilotPreOobeCache(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.preoobe_src = PREOOBE_NET_PATH.read_text(encoding="utf-8")
        cls.setup_complete_src = SETUP_COMPLETE_PATH.read_text(encoding="utf-8")
        cls.autounattend_src = AUTOUNATTEND_PATH.read_text(encoding="utf-8")
        cls.accel_pe_src = ACCEL_PE_PATH.read_text(encoding="utf-8")
        cls.patch_bootwim_src = PATCH_BOOTWIM_PY_PATH.read_text(encoding="utf-8")
        cls.winpe_forensics_src = WINPE_FORENSICS_PS1_PATH.read_text(encoding="utf-8")
        cls.cloud_ztd_src = CLOUD_ZTD_CS_PATH.read_text(encoding="utf-8")
        cls.local_forensics_src = LOCAL_FORENSICS_CS_PATH.read_text(encoding="utf-8")
        cls.decision_matrix_src = DECISION_MATRIX_CS_PATH.read_text(encoding="utf-8")
        cls.model_cs_src = MODEL_CS_PATH.read_text(encoding="utf-8")
        cls.report_exporter_src = REPORT_EXPORTER_CS_PATH.read_text(encoding="utf-8")
        cls.program_src = PROGRAM_CS_PATH.read_text(encoding="utf-8")
        cls.auditor_main_src = AUDITOR_MAIN_CS_PATH.read_text(encoding="utf-8")
        cls.provera_src = PROVERA_PS1_PATH.read_text(encoding="utf-8")
        cls.hw_src = WIS_HW_PSM1_PATH.read_text(encoding="utf-8")
        cls.ui_main_src = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "UI").glob("MainWindow*.cs")))

    def test_01_preoobe_scripts_stabilize_network_and_use_wmansvc_primary_path(self):
        """PreOOBE-Network.ps1 i SetupComplete.ps1 moraju stabilizovati mrezu, koristiti primarnu wmansvc putanju i sacuvati autopilot_report.json."""
        self.assertIn("Get-NetIPAddress", self.preoobe_src)
        self.assertIn("tbs", self.preoobe_src)
        self.assertIn("wmansvc", self.preoobe_src)
        self.assertIn("\\Microsoft\\Windows\\Management\\Provisioning\\Autopilot", self.preoobe_src)
        self.assertIn("WIS_OOBE_ZTD_Watcher", self.preoobe_src)
        self.assertIn("-OobeWatcherMode", self.preoobe_src)

        self.assertIn("AutopilotPreOOBE", self.setup_complete_src)
        self.assertIn("SkipNetworkSetup", self.setup_complete_src)
        self.assertIn("DisableOOBEConnection", self.setup_complete_src)
        self.assertIn("ztd.dds.microsoft.com", self.setup_complete_src)
        self.assertIn("autopilot_report.json", self.setup_complete_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.setup_complete_src)
        self.assertIn("WIS-AutopilotAuditor.exe", self.setup_complete_src)

        # IMP-114: primarna Windows 11 putanja u Get-PrimaryZtdJsonFiles mora biti ServiceState\wmansvc\AutopilotDDSZTDFile.json ispred ServiceState\Autopilot
        ztd_fn_setup = self.setup_complete_src.split("function Get-PrimaryZtdJsonFiles", 1)[1].split("function Copy-PrimaryZtdBackup", 1)[0]
        idx_wmansvc = ztd_fn_setup.find(r"ServiceState\wmansvc\AutopilotDDSZTDFile.json")
        idx_prov = ztd_fn_setup.find(r"Provisioning\Autopilot")
        idx_legacy = ztd_fn_setup.find(r"ServiceState\Autopilot\AutopilotDDSZTDFile.json")
        self.assertGreater(idx_wmansvc, 0, "SetupComplete.ps1 mora proveravati ServiceState\\wmansvc\\AutopilotDDSZTDFile.json")
        self.assertGreater(idx_prov, idx_wmansvc, "Provisioning\\Autopilot mora biti iza primarnog wmansvc")
        self.assertGreater(idx_legacy, idx_prov, "Sekundarna ServiceState\\Autopilot putanja mora biti fallback")

    def test_02_csharp_auditor_wmansvc_primary_no_false_confirmed_free_and_full_event_log(self):
        """IMP-114 Sloj A: CloudZtdInspector.cs koristi wmansvc primarnu putanju, zabranjuje lazni ConfirmedFree, podrzava 100/110/170/171 i ne pali OnlineChecked na TCP."""
        self.assertIn("public static bool TryParseZtdJsonContent", self.cloud_ztd_src)
        self.assertIn("public static bool TryLoadCachedReport", self.cloud_ztd_src)
        self.assertIn("public static void SaveCachedReport", self.cloud_ztd_src)
        self.assertIn("if (TryLoadCachedReport(targetSerial, report))", self.cloud_ztd_src)
        self.assertIn("PREOOBE_CACHED_REPORT", self.cloud_ztd_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.cloud_ztd_src)
        self.assertIn("IsReportContentConfirmed", self.cloud_ztd_src)

        # Primarna putanja wmansvc + Provisioning\Autopilot ispred ServiceState\Autopilot
        idx_wmansvc = self.cloud_ztd_src.find(r"ServiceState\wmansvc\AutopilotDDSZTDFile.json")
        idx_prov = self.cloud_ztd_src.find(r"Provisioning\Autopilot")
        idx_legacy = self.cloud_ztd_src.find(r"ServiceState\Autopilot\AutopilotDDSZTDFile.json")
        self.assertGreater(idx_wmansvc, 0)
        self.assertGreater(idx_prov, idx_wmansvc)
        self.assertGreater(idx_legacy, idx_prov)

        # Zabrana laznog ConfirmedFree = true za puko ProfileState=NotAssigned ili prazan CloudAssignedTenantId
        self.assertNotIn("807 - ZtdDeviceIsNotRegistered (ZTD File)", self.cloud_ztd_src)
        self.assertNotRegex(
            self.cloud_ztd_src,
            r'ProfileState\\s\*=\\s\*NotAssigned[\s\S]{0,180}ConfirmedFree\s*=\s*true',
            "ProfileState=NotAssigned bez Event 807 ne sme postavljati ConfirmedFree = true",
        )

        # OnlineChecked se NE sme paliti na puki TCP connect ka login.live.com
        self.assertNotIn("login.live.com", self.cloud_ztd_src)
        self.assertIn("report.NetworkReachable = canReachZtd;", self.cloud_ztd_src)

        # Pun Event Log bez ogranicenja na zadnjih 50 zapisa, sa podrskom za 24H2/25H2 (100/110/170/171) i kljucna polja
        self.assertNotIn("checkedCount < 50", self.cloud_ztd_src)
        for token in (
            "eventId == 100 || eventId == 110 || eventId == 170 || eventId == 171",
            "CloudAssignedDeviceName",
            "CloudAssignedTenantDomain",
            "CloudAssignedTenantId",
            "ZtdRegistrationState",
            "ZtdDeviceIsNotRegistered",
        ):
            self.assertIn(token, self.cloud_ztd_src)

        self.assertIn("CloudZtdInspector.SaveCachedReport(result)", self.program_src)
        self.assertIn("CloudZtdInspector.SaveCachedReport(evaluated)", self.auditor_main_src)

    def test_03_local_forensics_and_powershell_parity(self):
        """IMP-114 Sloj A: LocalForensicsInspector.cs i Provera_autopilota.ps1 ispravno citaju podkljuceve, PolicyJsonCache, AutopilotSettings i W-AAD-*."""
        # C# LocalForensicsInspector.cs
        self.assertIn('key.OpenSubKey("EstablishedCorrelations")', self.local_forensics_src)
        self.assertIn(r'OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache")', self.local_forensics_src)
        self.assertIn('cacheKey.GetValue("PolicyJsonCache")', self.local_forensics_src)
        self.assertIn(r'SOFTWARE\Microsoft\Provisioning\AutopilotSettings', self.local_forensics_src)
        self.assertIn(r'SOFTWARE\Microsoft\Provisioning\EnterpriseAssignedAccess', self.local_forensics_src)
        self.assertIn("EnrollmentStatusTracking", self.local_forensics_src)
        self.assertIn(r"W-AAD-\d{9}", self.local_forensics_src)
        self.assertIn("CloudAssignedDeviceName", self.local_forensics_src)

        # PowerShell Provera_autopilota.ps1 paritet (bez UTF-8 BOM-a)
        self.assertFalse(PROVERA_PS1_PATH.read_bytes().startswith(b"\xef\xbb\xbf"), "Provera_autopilota.ps1 ne sme imati UTF-8 BOM")
        self.assertIn("function Resolve-ZtdJsonContent", self.provera_src)
        self.assertIn("function Test-SavedAutopilotReportConfirmed", self.provera_src)
        self.assertIn("PREOOBE_CACHED_REPORT", self.provera_src)
        self.assertIn("AutopilotDDSZTDFile.preoobe.json", self.provera_src)
        self.assertIn("autopilot_report.json", self.provera_src)
        self.assertNotIn("807 - ZtdDeviceIsNotRegistered (ZTD File)", self.provera_src)
        self.assertNotIn("-MaxEvents 50", self.provera_src)
        self.assertNotIn("USLOVNO CISTO", self.provera_src.upper())

        ztd_fn_ps = self.provera_src.split("function Get-PrimaryZtdFileCandidates", 1)[1].split("Clear-Host", 1)[0]
        idx_ps_wman = ztd_fn_ps.find(r"ServiceState\wmansvc\AutopilotDDSZTDFile.json")
        idx_ps_prov = ztd_fn_ps.find(r"Provisioning\Autopilot")
        idx_ps_leg = ztd_fn_ps.find(r"ServiceState\Autopilot\AutopilotDDSZTDFile.json")
        self.assertGreater(idx_ps_wman, 0)
        self.assertGreater(idx_ps_prov, idx_ps_wman)
        self.assertGreater(idx_ps_leg, idx_ps_prov)

        for token in (
            r"Diagnostics\AutoPilot\EstablishedCorrelations",
            r"Provisioning\AutopilotPolicyCache",
            "PolicyJsonCache",
            r"Provisioning\AutopilotSettings",
            r"Provisioning\EnterpriseAssignedAccess",
            "EnrollmentStatusTracking",
            r"W-AAD-\d{9}",
            "IMA_AUTOPILOT",
            "NEMA_AUTOPILOT",
            "NIJE_PROVERENO",
        ):
            self.assertIn(token, self.provera_src)

        self.assertIn("autopilot_report.json", self.hw_src)
        self.assertRegex(self.hw_src, r"LOCKED_TENANT.*HIDDEN_LOCK")

    def test_04_decision_matrix_binary_model_and_wis_ui_cache_hooks(self):
        """IMP-114 Sloj A: DecisionMatrix koristi binarni model (WARNING za UNVERIFIED/OFFLINE, nikad INFO/uslovno cisto) i cuva UI kes hookove."""
        self.assertIn('res.BinaryVerdict = "IMA_AUTOPILOT";', self.decision_matrix_src)
        self.assertIn('res.BinaryVerdict = "NEMA_AUTOPILOT";', self.decision_matrix_src)
        self.assertIn('res.BinaryVerdict = "NIJE_PROVERENO";', self.decision_matrix_src)
        self.assertIn("public string BinaryVerdict { get; set; }", self.model_cs_src)
        self.assertIn('\\"binary_verdict\\"', self.model_cs_src)
        self.assertNotIn("StatusSeverity.INFO", self.decision_matrix_src)
        self.assertNotIn("USLOVNO ČISTO", self.decision_matrix_src.upper())
        self.assertNotIn("USLOVNO ČISTO", self.report_exporter_src.upper())
        self.assertNotIn("u normalnom stanju", self.decision_matrix_src.lower())

        # Postojeci UI ugovor za kesirani izvestaj i retry po mrezi
        self.assertIn("_autopilotRetriedAfterNetwork", self.ui_main_src)
        self.assertIn("IsAutopilotMatrixConfirmed", self.ui_main_src)
        self.assertIn("TryReadCachedAutopilotReport", self.ui_main_src)
        self.assertIn("TryRetryAutopilotOnNetworkReady", self.ui_main_src)
        self.assertIn(r"C:\Windows\Logs\WIS\autopilot_report.json", self.ui_main_src)
        self.assertIn('stepId.StartsWith("P1."', self.ui_main_src)

    def test_05_all_synced_copies_have_identical_sha256(self):
        """Sve lokalne sinhronizovane kopije skripti i binara u ISO/, OEM i artifacts/ moraju imati identican SHA-256 (NAS opt-in preko WIS_CHECK_NAS_SYNC=1)."""
        check_nas = os.environ.get("WIS_CHECK_NAS_SYNC") == "1"
        for src, targets in SYNC_GROUPS:
            if not src.is_file() and src.suffix.lower() == ".exe":
                continue
            self.assertTrue(src.is_file(), f"Izvorni fajl ne postoji: {src}")
            expected = sha256_file(src)
            for dst in targets:
                dst_str = str(dst).replace("\\", "/")
                if "/mnt/Servis" in dst_str and not check_nas:
                    continue
                if not dst.is_file() and (
                    "/mnt/Servis" in dst_str or ".workspace" in dst_str or "ISO/sources" in dst_str
                ):
                    continue
                self.assertTrue(dst.is_file(), f"Odredisni fajl ne postoji: {dst}")
                self.assertEqual(sha256_file(dst), expected, f"SHA-256 neuskladjen za {dst}")

    def test_06_v344_04_silent_background_execution(self):
        """V344-04: AutopilotAuditor ne sme alocirati konzolni prozor u --json/--silent/redirected rezimu i svi podprocesi moraju biti skriveni."""
        self.assertIn('"--silent"', self.program_src)
        self.assertIn('"--no-console"', self.program_src)
        self.assertIn("consoleAttachedOrAllocated", self.program_src)
        self.assertIn("isJsonOutput || isSilent || Console.IsOutputRedirected", self.program_src)
        self.assertIn("Console.OutputEncoding = Encoding.UTF8", self.program_src)
        self.assertRegex(
            self.program_src,
            r"if\s*\(\s*!suppressConsoleWindow\s*\)\s*\{\s*consoleAttachedOrAllocated\s*=\s*TryAttachOrAllocConsole\(\);",
        )
        self.assertRegex(
            self.program_src,
            r"finally\s*\{\s*if\s*\(\s*consoleAttachedOrAllocated\s*\)\s*\{\s*TryFreeConsole\(\);",
        )

        self.assertIn("WindowStyle = ProcessWindowStyle.Hidden", self.local_forensics_src)
        self.assertIn("CreateNoWindow = true", self.local_forensics_src)
        self.assertIn("UseShellExecute = false", self.local_forensics_src)

        self.assertGreaterEqual(
            self.cloud_ztd_src.count("WindowStyle = ProcessWindowStyle.Hidden"),
            4,
            "CloudZtdInspector.cs mora imati ProcessWindowStyle.Hidden za sve ProcessStartInfo pozive",
        )
        self.assertGreaterEqual(
            self.cloud_ztd_src.count("CreateNoWindow = true"),
            4,
            "CloudZtdInspector.cs mora imati CreateNoWindow = true za sve ProcessStartInfo pozive",
        )
        self.assertGreaterEqual(
            self.cloud_ztd_src.count("RedirectStandardOutput = true"),
            4,
            "CloudZtdInspector.cs mora preusmeriti StandardOutput za sve ProcessStartInfo pozive",
        )
        self.assertGreaterEqual(
            self.cloud_ztd_src.count("RedirectStandardError = true"),
            4,
            "CloudZtdInspector.cs mora preusmeriti StandardError za sve ProcessStartInfo pozive",
        )

        self.assertIn('Arguments = "--json --silent"', self.ui_main_src)
        self.assertIn("-WindowStyle Hidden", self.ui_main_src)
        self.assertGreaterEqual(
            self.ui_main_src.count("WindowStyle = ProcessWindowStyle.Hidden"),
            2,
            "MainWindow.xaml.cs mora sakriti i EXE i PowerShell fallback prozor za Autopilot skeniranje",
        )

    def test_07_imp112_golden_iso_profile_markers_and_winpe_guard(self):
        """IMP-112: Build-Iso.py upisuje \\sources\\wis-image-profile.json i \\WIS_IMAGE_PROFILE.tag, a WIS-AcceleratePE.cmd ih poredi pre prebacivanja na Z:\\win11."""
        mod = load_build_iso_module()

        # 1. Sanitizacija tag vrednosti protiv cmd.exe injekcije
        sanitized = mod.sanitize_profile_tag_value('preinstalled&calc|echo>x"%')
        self.assertEqual(sanitized, "preinstalledcalcechox")

        # 2. Stvarno formiranje staging stabla za Golden Preinstalled profil
        with tempfile.TemporaryDirectory() as tmp_root, tempfile.TemporaryDirectory() as tmp_staging:
            mock_root = pathlib.Path(tmp_root)
            staging_dir = pathlib.Path(tmp_staging)
            (mock_root / "sources" / "$OEM$" / "$$" / "Logs" / "WIS").mkdir(parents=True, exist_ok=True)
            (mock_root / "sources" / "install.wim").write_text("mock-wim", encoding="utf-8")
            (mock_root / "sources" / "boot.wim").write_text("mock-boot", encoding="utf-8")
            (mock_root / "version.json").write_text('{"os_build_target": "10.0.26200.8037"}', encoding="utf-8")
            (mock_root / "sources" / "$OEM$" / "$$" / "Logs" / "WIS" / "image-profile.json").write_text(
                json.dumps({
                    "image_type": "preinstalled",
                    "wim_version": "26200.8037-heavy-v1",
                    "preinstalled_apps": ["VCRedist", "DotNet8", "DotNet9", "Office2024", "AcrobatPro2024"],
                }),
                encoding="utf-8",
            )

            builder = mod.IsoBuilder(project_root=mock_root, profile="lean", image_format="wim")
            builder.populate_staging(staging_dir)

            json_marker = staging_dir / "sources" / "wis-image-profile.json"
            tag_marker = staging_dir / "WIS_IMAGE_PROFILE.tag"
            self.assertTrue(json_marker.is_file(), "Mora postojati \\sources\\wis-image-profile.json u staging-u")
            self.assertTrue(tag_marker.is_file(), "Mora postojati \\WIS_IMAGE_PROFILE.tag u korenu ISO staging-a")

            parsed = json.loads(json_marker.read_text(encoding="utf-8"))
            self.assertEqual(parsed["image_type"], "preinstalled")
            self.assertEqual(parsed["wim_version"], "26200.8037-heavy-v1")
            self.assertIn("Office2024", parsed["preinstalled_apps"])

            tag_raw = tag_marker.read_bytes()
            self.assertIn(b"\r\n", tag_raw, "WIS_IMAGE_PROFILE.tag mora koristiti CRLF za WinPE cmd.exe")
            tag_txt = tag_raw.decode("ascii")
            self.assertIn("IMAGE_TYPE=preinstalled", tag_txt)
            self.assertIn("WIM_VERSION=26200.8037-heavy-v1", tag_txt)

        # 3. WIS-AcceleratePE.cmd i Patch-BootWim.py moraju porediti Y:\WIS_IMAGE_PROFILE.tag i Z:\win11\WIS_IMAGE_PROFILE.tag PRE detach/remap
        for label, src in (("WIS-AcceleratePE.cmd", self.accel_pe_src), ("Patch-BootWim.py", self.patch_bootwim_src)):
            idx_y_tag = src.find(r'Y:\WIS_IMAGE_PROFILE.tag')
            idx_z_tag = src.find(r'Z:\win11\WIS_IMAGE_PROFILE.tag')
            idx_detach = src.find(r'if exist "T:\iventoy.iso"')
            self.assertGreater(idx_y_tag, 0, f"{label} mora citati Y:\\WIS_IMAGE_PROFILE.tag")
            self.assertGreater(idx_z_tag, idx_y_tag, f"{label} mora porediti Z:\\win11\\WIS_IMAGE_PROFILE.tag")
            self.assertGreater(idx_detach, idx_z_tag, f"{label} mora izvrsiti poredjenje taga PRE detach-a T:\\iventoy.iso")
            self.assertIn('ALLOW_Z_SWITCH', src)
            self.assertIn('WIS_PROFILE_MISMATCH.tag', src)
            self.assertIn('net.exe use Z: /delete /y', src)

    def test_08_imp114_sloj_b_winpe_preformat_forensics_security_and_integration(self):
        """IMP-114 Sloj B: Invoke-WinPeAutopilotForensics.ps1 bezbedno radi reg load/unload u finally, stiti tajne i salje na /api/devices/<sn>/autopilot."""
        self.assertTrue(WINPE_FORENSICS_PS1_PATH.is_file())
        self.assertFalse(WINPE_FORENSICS_PS1_PATH.read_bytes().startswith(b"\xef\xbb\xbf"), "Invoke-WinPeAutopilotForensics.ps1 ne sme imati UTF-8 BOM")
        self.assertNotIn("Invoke-Expression", self.winpe_forensics_src)
        self.assertNotRegex(self.winpe_forensics_src, r"(?i)\biex\b")

        for token in (
            r"ServiceState\wmansvc\AutopilotDDSZTDFile.json",
            r"Provisioning\Autopilot",
            r"System32\config\SOFTWARE",
            r"System32\config\SYSTEM",
            "reg.exe load",
            "reg.exe unload",
            "[GC]::Collect()",
            "[GC]::WaitForPendingFinalizers()",
            "finally {",
            "EstablishedCorrelations",
            "AutopilotPolicyCache",
            "AutopilotSettings",
            "EnterpriseAssignedAccess",
            "EnrollmentStatusTracking",
            "CloudDomainJoin",
            r"W-AAD-\d{9}",
            "/api/devices/{1}/autopilot",
            "X-ServisHub-Client-Token",
            "***MASKIRANO***",
            "winpe_preformat",
        ):
            self.assertIn(token, self.winpe_forensics_src)

        # Integracija u WIS-AcceleratePE.cmd i Patch-BootWim.py
        self.assertIn("Invoke-WinPeAutopilotForensics.ps1", self.accel_pe_src)
        self.assertIn("WIS_WinPE_Forensics_Done.tag", self.accel_pe_src)
        self.assertIn("Invoke-WinPeAutopilotForensics.ps1", self.patch_bootwim_src)
        self.assertIn("/Windows/System32/Invoke-WinPeAutopilotForensics.ps1", self.patch_bootwim_src)

        # SEC-07: Eksplicitno oslobadjanje svih RegistryKey rucki (.Dispose() + $null) pre reg.exe unload
        self.assertIn("$ch.Dispose()", self.winpe_forensics_src)
        self.assertIn("$es.Dispose()", self.winpe_forensics_src)
        self.assertIn("$csItem.Dispose()", self.winpe_forensics_src)
        self.assertIn("$eSubs = $null", self.winpe_forensics_src)
        self.assertIn("$cdjSubs = $null", self.winpe_forensics_src)
        self.assertIn("$selProps = $null", self.winpe_forensics_src)
        self.assertIn("$cnProps = $null", self.winpe_forensics_src)

        # SEC-07: WIS-AcceleratePE.cmd :ReadProfileTag bez findstr, sa set /p, resetom _CLEAN_V i strogom allowlistom
        for label, src in (("WIS-AcceleratePE.cmd", self.accel_pe_src), ("Patch-BootWim.py", self.patch_bootwim_src)):
            read_sec = src.split(":ReadProfileTag", 1)[1].split(":RunWinPeForensics", 1)[0]
            self.assertNotIn("findstr", read_sec.lower(), f"{label} :ReadProfileTag ne sme koristiti findstr.exe (nije u WinPE)")
            self.assertIn('set /p "_L1="', read_sec)
            self.assertIn('set "_CLEAN_V="', read_sec)
            self.assertIn('"preinstalled"', read_sec)
            self.assertIn('"lean"', read_sec)
            self.assertIn("delims=0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-", read_sec)

    def test_09_imp114_sloj_c_oobe_ztd_interceptor_and_valid_unattend_xml(self):
        """IMP-114 Sloj C: autounattend.xml je validan XML bez preranog BypassNRO/SkipNetworkSetup, a SetupComplete.ps1 ima SYSTEM OOBE ZTD watcher sa fallbackom."""
        # 1. Validacija XML strukture ISO/autounattend.xml
        tree = ET.parse(str(AUTOUNATTEND_PATH))
        root = tree.getroot()
        ns = {"u": "urn:schemas-microsoft-com:unattend"}
        passes = {elem.attrib.get("pass"): elem for elem in root.findall("u:settings", ns)}
        self.assertIn("windowsPE", passes)
        self.assertIn("specialize", passes)
        self.assertIn("oobeSystem", passes)

        # windowsPE zadrzava LabConfig 1-6 + Order 7 load_vmd.cmd + Order 8 WIS-AcceleratePE.cmd
        winpe_cmds = [c.text or "" for c in passes["windowsPE"].findall(".//u:Path", ns)]
        self.assertTrue(any("load_vmd.cmd" in p for p in winpe_cmds))
        self.assertTrue(any("WIS-AcceleratePE.cmd" in p for p in winpe_cmds))

        # specialize NE sme prerano dodavati BypassNRO=1 ili SkipNetworkSetup=1
        spec_cmds = [c.text or "" for c in passes["specialize"].findall(".//u:Path", ns)]
        for cmd in spec_cmds:
            self.assertFalse(
                re.search(r"(?i)reg(\.exe)?\s+add\b.*(BypassNRO|SkipNetworkSetup|HideOnlineAccountScreens)", cmd),
                f"specialize ne sme prerano blokirati OOBE mrezu: {cmd}",
            )
        self.assertTrue(any("PreOOBE-Network.cmd" in p for p in spec_cmds))

        # oobeSystem OOBE ne blokira online/wireless ZTD razmenu i zadrzava AutoLogon Korisnik
        oobe_node = passes["oobeSystem"].find(".//u:OOBE", ns)
        self.assertIsNotNone(oobe_node)
        self.assertEqual((oobe_node.findtext("u:HideOnlineAccountScreens", default="", namespaces=ns)).lower(), "false")
        self.assertEqual((oobe_node.findtext("u:HideWirelessSetupInOOBE", default="", namespaces=ns)).lower(), "false")
        self.assertEqual(passes["oobeSystem"].findtext(".//u:AutoLogon/u:Username", default="", namespaces=ns), "Korisnik")

        # 2. SetupComplete.ps1 ima -OobeWatcherMode bez Invoke-Expression i sa bezbednim timeout/offline fallbackom
        self.assertNotIn("Invoke-Expression", self.setup_complete_src)
        self.assertNotIn("Invoke-Expression", self.preoobe_src)
        for token in (
            "[switch]$OobeWatcherMode",
            "function Invoke-OobeZtdWatcher",
            "function Inspect-OobeZtdRealtimeSnapshot",
            "function Enable-SafeLocalServiceTransition",
            "function Send-AutopilotVerdictToHub",
            "IMA_AUTOPILOT",
            "NEMA_AUTOPILOT",
            "NIJE_PROVERENO",
            "OOBE_ZTD_TIMEOUT",
            "OOBE_ZTD_OFFLINE",
            "WIS_OOBE_ZTD_Watcher",
            "X-ServisHub-Client-Token",
        ):
            self.assertIn(token, self.setup_complete_src)

        # SEC-07: PreOOBE-Network.ps1 sme registrovati WIS_OOBE_ZTD_Watcher ISKLJUCIVO na admin-only C:\Windows\Setup\Scripts\SetupComplete.ps1
        self.assertIn(r"$watcherScript = 'C:\Windows\Setup\Scripts\SetupComplete.ps1'", self.preoobe_src)
        self.assertNotIn(r"'C:\setup\SetupComplete.ps1'", self.preoobe_src)
        self.assertNotIn(r"'C:\setup\Skripte\SetupComplete.ps1'", self.preoobe_src)

        # SEC-07: Bezuslovno uklanjanje WIS_OOBE_ZTD_Watcher u normalnom SetupComplete.ps1 i u Fazi 6 WIS.Cleanup.psm1
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8-sig")
        self.assertIn('/Delete /TN "WIS_OOBE_ZTD_Watcher" /F', cleanup_src)
        self.assertIn('/Delete /TN "WIS_OOBE_ZTD_Watcher" /F', self.setup_complete_src)

    def test_10_winpe_batch_scripts_no_unescaped_parens_in_echo_blocks(self):
        """WinPE batch skripte i Patch-BootWim.py ne smeju imati ne-eskejpovane '(' ili ')' u echo linijama (uzrok 0x800700FF - 0x40030) i moraju imati balansiran depth == 0."""
        tree = ast.parse(self.patch_bootwim_src, filename=str(PATCH_BOOTWIM_PY_PATH))
        embedded_batches = {}
        for node in tree.body:
            if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id in (
                        "VENTOY_AUTORUN_BAT_CONTENT",
                        "STARTNET_CMD_CONTENT",
                        "ACCELERATE_PE_CMD_CONTENT",
                    ):
                        embedded_batches[target.id] = node.value.value

        self.assertEqual(
            set(embedded_batches.keys()),
            {"VENTOY_AUTORUN_BAT_CONTENT", "STARTNET_CMD_CONTENT", "ACCELERATE_PE_CMD_CONTENT"},
            "Patch-BootWim.py mora definisati sva 3 WinPE batch bloka",
        )

        targets = [
            ("ISO/WIS-AcceleratePE.cmd", self.accel_pe_src),
            ("ISO/Skripte/WIS-ConnectNAS.cmd", (ROOT / "ISO" / "Skripte" / "WIS-ConnectNAS.cmd").read_text(encoding="utf-8")),
            ("ISO/load_vmd.cmd", (ROOT / "ISO" / "load_vmd.cmd").read_text(encoding="utf-8")),
            ("Patch-BootWim.py::VENTOY_AUTORUN_BAT_CONTENT", embedded_batches["VENTOY_AUTORUN_BAT_CONTENT"]),
            ("Patch-BootWim.py::STARTNET_CMD_CONTENT", embedded_batches["STARTNET_CMD_CONTENT"]),
            ("Patch-BootWim.py::ACCELERATE_PE_CMD_CONTENT", embedded_batches["ACCELERATE_PE_CMD_CONTENT"]),
        ]

        for label, content in targets:
            depth = 0
            violations = []
            for lineno, raw in enumerate(content.splitlines(), 1):
                line = raw.strip()
                if not line or line.startswith("::") or re.match(r"(?i)^rem(\s|$)", line):
                    continue
                m_echo = re.match(r"(?i)^@?echo(?:\s+|$|\.)(.*)", line)
                if m_echo:
                    rest = re.sub(r"\^[()]", "", m_echo.group(1))
                    if "(" in rest or ")" in rest:
                        violations.append(f"L{lineno} (depth={depth}): {line}")
                    continue
                no_quotes = re.sub(r'"[^"]*"', '""', line)
                no_quotes = re.sub(r"\^[()]", "", no_quotes)
                depth += no_quotes.count("(") - no_quotes.count(")")
                self.assertGreaterEqual(
                    depth,
                    0,
                    f"{label} L{lineno}: negativan balans zagrada (depth={depth}): {line}",
                )

            self.assertEqual(
                violations,
                [],
                f"{label} sadrzi ne-eskejpovane zagrade u echo linijama:\n" + "\n".join(violations),
            )
            self.assertEqual(depth, 0, f"{label} ima nebalansirane zagrade na kraju (final_depth={depth})")

    def test_11_unattend_path_max_length_and_preoobe_zero_exit(self):
        """specialize hotfix: svaki <Path>/<CommandLine> u ISO/autounattend.xml <= 255 znakova, PreOOBE-Network.cmd ima '& ver >nul' i 'exit /b 0', a PreOOBE-Network.ps1 'exit 0'."""
        tree = ET.parse(str(AUTOUNATTEND_PATH))
        root = tree.getroot()
        ns = {"u": "urn:schemas-microsoft-com:unattend"}

        all_cmds = []
        for tag in ("Path", "CommandLine"):
            for elem in root.findall(f".//u:{tag}", ns):
                txt = elem.text or ""
                all_cmds.append((tag, txt))
                self.assertLessEqual(
                    len(txt),
                    255,
                    f"ISO/autounattend.xml <{tag}> prelazi 255 znakova ({len(txt)}): {txt}",
                )
        self.assertGreaterEqual(len(all_cmds), 16, "Ocekivano najmanje 16 <Path>/<CommandLine> unosa u autounattend.xml")

        specialize = root.find("u:settings[@pass='specialize']", ns)
        self.assertIsNotNone(specialize, "specialize pass mora postojati u autounattend.xml")
        spec_cmds = [
            (
                int(cmd.findtext("u:Order", default="0", namespaces=ns)),
                cmd.findtext("u:Path", default="", namespaces=ns),
            )
            for cmd in specialize.findall(".//u:RunSynchronousCommand", ns)
        ]
        spec_cmds.sort(key=lambda item: item[0])
        self.assertEqual([order for order, _ in spec_cmds], list(range(1, len(spec_cmds) + 1)))

        preoobe_calls = [path for _, path in spec_cmds if "PreOOBE-Network.cmd" in path]
        self.assertEqual(len(preoobe_calls), 1, "specialize mora imati tacno jedan poziv PreOOBE-Network.cmd")
        self.assertRegex(
            preoobe_calls[0].strip(),
            r"&\s*ver\s*>nul\"?$",
            f"PreOOBE-Network.cmd poziv u specialize mora zavrsavati sa '& ver >nul': {preoobe_calls[0]}",
        )

        # Svaka reg delete OOBE stavka u specialize mora biti odvojena i zasticena sa '& ver >nul'
        for key_name in ("SkipNetworkSetup", "BypassNRO", "DisableOOBEConnection"):
            matches = [path for _, path in spec_cmds if f"/v {key_name}" in path]
            self.assertEqual(len(matches), 1, f"specialize mora imati zaseban reg delete za {key_name}")
            self.assertRegex(
                matches[0].strip(),
                r"&\s*ver\s*>nul\"?$",
                f"reg delete {key_name} mora zavrsavati sa '& ver >nul': {matches[0]}",
            )

        # PreOOBE-Network.cmd mora zavrsavati sa 'exit /b 0'
        for cmd_path in (PREOOBE_CMD_CANONICAL, PREOOBE_CMD_OEM):
            if cmd_path == PREOOBE_CMD_OEM and not cmd_path.is_file():
                continue
            self.assertTrue(cmd_path.is_file(), f"Nedostaje {cmd_path}")
            non_empty = [ln.strip() for ln in cmd_path.read_text(encoding="utf-8").splitlines() if ln.strip()]
            self.assertTrue(non_empty, f"Prazan fajl: {cmd_path}")
            self.assertEqual(
                non_empty[-1].lower(),
                "exit /b 0",
                f"{cmd_path} mora zavrsavati sa 'exit /b 0', a zavrsava sa: {non_empty[-1]!r}",
            )

        # PreOOBE-Network.ps1 mora zavrsavati sa 'exit 0'
        for ps_path in (PREOOBE_NET_CANONICAL, PREOOBE_NET_OEM):
            if ps_path == PREOOBE_NET_OEM and not ps_path.is_file():
                continue
            self.assertTrue(ps_path.is_file(), f"Nedostaje {ps_path}")
            non_empty = [ln.strip() for ln in ps_path.read_text(encoding="utf-8").splitlines() if ln.strip()]
            self.assertTrue(non_empty, f"Prazan fajl: {ps_path}")
            self.assertEqual(
                non_empty[-1].lower(),
                "exit 0",
                f"{ps_path} mora zavrsavati sa 'exit 0', a zavrsava sa: {non_empty[-1]!r}",
            )


if __name__ == "__main__":
    unittest.main()
