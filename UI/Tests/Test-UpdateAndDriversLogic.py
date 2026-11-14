#!/usr/bin/env python3
"""
Test-UpdateAndDriversLogic.py
=============================
Automatska verifikacija ugovora, regresionih invarijanti i konfiguracije
za WIS Windows Update i drajvere (WIS.Update.psm1, WIS.Hardware.psm1, Phase2-Drivers.ps1 i Wifi-Servis*.ini).
"""

import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
WIS_UPDATE_PATH = ROOT / "UI" / "Skripte" / "Modules" / "WIS.Update.psm1"
WIS_HARDWARE_PATH = ROOT / "UI" / "Skripte" / "Modules" / "WIS.Hardware.psm1"
PHASE2_DRIVERS_PATH = ROOT / "UI" / "Skripte" / "Phases" / "Phase2-Drivers.ps1"

WIFI_INI_PATHS = [
    ROOT / "Wifi-Servis.example.ini",
    ROOT / "ISO" / "Wifi-Servis.example.ini",
    ROOT / "ISO" / "Wifi-Servis.ini",
    ROOT / "ISO" / "Skripte" / "Wifi-Servis.ini",
    ROOT / "ISO" / "sources" / "$OEM$" / "$$" / "Setup" / "Scripts" / "Wifi-Servis.ini",
    ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Wifi-Servis.ini",
]


class TestUpdateAndDriversLogic(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.update_src = WIS_UPDATE_PATH.read_text(encoding="utf-8")
        cls.hw_src = WIS_HARDWARE_PATH.read_text(encoding="utf-8")
        cls.phase2_src = PHASE2_DRIVERS_PATH.read_text(encoding="utf-8")

    def test_01_ini_files_have_unified_nas_defaults(self):
        """Svih 6 Wifi-Servis*.ini fajlova moraju imati ShareName=Servis i DriversSubDir=Drivers\\Harvested\\Models."""
        for p in WIFI_INI_PATHS:
            self.assertTrue(p.exists(), f"INI fajl mora postojati: {p}")
            txt = p.read_text(encoding="utf-8")
            self.assertRegex(
                txt,
                r"(?mi)^ShareName\s*=\s*Servis\s*$",
                f"{p} mora imati ShareName=Servis",
            )
            self.assertRegex(
                txt,
                r"(?mi)^DriversSubDir\s*=\s*Drivers\\Harvested\\Models\s*$",
                f"{p} mora imati DriversSubDir=Drivers\\Harvested\\Models",
            )

    def test_02_nas_settings_fallback_defaults(self):
        """Get-ServisNasSettings u WIS.Update.psm1 mora podrazumevano koristiti ShareName='Servis' i DriversSubDir='Drivers\\Harvested\\Models'."""
        m = re.search(r"function Get-ServisNasSettings\s*\{([\s\S]*?)^\}", self.update_src, re.M)
        self.assertIsNotNone(m, "Get-ServisNasSettings mora postojati u WIS.Update.psm1")
        body = m.group(1)
        self.assertIn('ShareName         = "Servis"', body)
        self.assertIn('DriversSubDir     = "Drivers\\Harvested\\Models"', body)
        self.assertNotIn('ShareName         = "WIS"', body)
        self.assertNotIn('DriversSubDir     = "Drivers"', body)

    def test_03_model_pack_http_bidirectional_support(self):
        """Proverava HTTP <Model>.tar download/upload fallback i jedinstveni tar/pnputil helper u WIS.Update.psm1."""
        for sym in (
            "function Install-DriverTarArchive",
            "function Invoke-ServisHubBinaryUpload",
            "function Send-ModelDriverPackToServisHub",
        ):
            self.assertIn(sym, self.update_src, f"Nedostaje funkcija {sym}")

        self.assertIn("/api/drivers/model-pack/status", self.update_src)
        self.assertIn("/api/drivers/model-pack/download", self.update_src)
        self.assertIn("/api/drivers/model-pack/upload", self.update_src)
        # HTTP .tar download mora imati MinBytes >= 512 umesto defaulta za LCU pakete
        self.assertRegex(self.update_src, r"Receive-UpdateFileFromUrl[^\r\n]*-MinBytes\s+512")

    def test_04_os_build_compatibility_and_filtering(self):
        """Proverava OSBuild ekstrakciju/filtriranje u Invoke-NasUpdatePrePatch, Send-UpdatePackageToServisHub i Export-ServisWindowsUpdates."""
        self.assertIn("function Get-CurrentWindowsBuildNumber", self.update_src)
        self.assertIn("function Test-UpdatePackageBuildCompatible", self.update_src)
        self.assertIn("os_build=", self.update_src)
        self.assertIn("X-OS-Build", self.update_src)
        self.assertIn("os_build = $currBuild", self.update_src)
        self.assertIn("-OsBuild $currBuild", self.update_src)

    def test_05_module_exports_and_orchestrator_contract(self):
        """Proverava Export-ModuleMember i orkestraciju Phase2-Drivers.ps1 / WIS.Hardware.psm1."""
        export_match = re.search(r"Export-ModuleMember\s+-Function[\s\S]*?\Z", self.update_src)
        self.assertIsNotNone(export_match, "Export-ModuleMember mora postojati u WIS.Update.psm1")
        export_blk = export_match.group(0)
        for fn in (
            "Get-CurrentWindowsBuildNumber",
            "Test-UpdatePackageBuildCompatible",
            "Send-UpdatePackageToServisHub",
            "Send-ModelDriverPackToServisHub",
            "Invoke-NasUpdatePrePatch",
            "Invoke-ModelDriversPreInstall",
            "Export-ServisHardwareDrivers",
            "Export-ServisWindowsUpdates",
            "Invoke-Phase2Drivers",
        ):
            self.assertIn(fn, export_blk, f"{fn} mora biti eksportovan u WIS.Update.psm1")

        self.assertIn("Invoke-Phase2Drivers", self.phase2_src)
        self.assertIn("WIS.Update.psm1", self.phase2_src)
        self.assertIn("Resolve-MissingDriversFromHub", self.hw_src)
        self.assertIn("/api/drivers/resolve", self.hw_src)

    def test_06_wis_task_227_wu_completeness_hub_8080_and_apps_fixes(self):
        """Proverava completeness WU logiku (MU katalog, bez laznog COMPLETED, Phase4 restart radnika), 8080/status i Apps ispravke."""
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        tel_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Telemetry.psm1").read_text(encoding="utf-8")

        # 1. WU completeness
        self.assertIn("7971f918-a847-4430-9279-4a52d1efe18d", self.update_src)
        self.assertIn("wu-secondpass-done.flag", self.update_src)
        self.assertNotIn('Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Pozadinski WU: Zavrseno ($instCnt/$fndCnt)"', self.update_src)
        self.assertIn('Invoke-WisStep -Phase 2 -StepId "P2.WuDrivers" -Title "Hardverski drajveri preko Windows Update-a" -Test {', self.update_src)
        self.assertNotIn('-StepId "P2.WuDrivers" -Title "Hardverski drajveri preko Windows Update-a" -JournalTtlHours 6', self.update_src)
        self.assertIn("Start-BackgroundWindowsUpdateWorker", apps_src)

        # 2. Port 8080 + /api/status
        self.assertIn("$configuredPort = 8080", tel_src)
        self.assertIn('"$testUrl/api/status"', tel_src)

        # 3. Office Ohook + PdfDefault GPO + CurrentPhase=4
        self.assertIn("vfs\\System\\sppc.dll", apps_src)
        self.assertIn("/Ohook /S", apps_src)
        self.assertIn("AppAssoc_Acrobat.xml", apps_src)
        self.assertIn("$Global:CurrentPhase = 4", apps_src)

    def test_07_wis_task_229_wu_barrier_fast_exit_and_lifecycle_diagnostics(self):
        """Proverava brzo izlaženje WU barijere (wu-secondpass-done.flag, bez idle MoUsoCoreWorker) i očuvanje cooling_verified."""
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        ui_src = (ROOT / "UI" / "MainWindow.xaml.cs").read_text(encoding="utf-8")

        # 1. SecondPass i Wait-WindowsUpdateCompletionBarrier bez 15-min zastoja
        self.assertIn("while ($waitSw.Elapsed.TotalSeconds -lt 45)", self.update_src)
        self.assertNotIn("while ($waitSw.Elapsed.TotalSeconds -lt 180)", self.update_src)
        barrier_block = self.update_src.split("function Wait-WindowsUpdateCompletionBarrier", 1)[1]
        self.assertIn("[int]$TimeoutSeconds = 60", barrier_block)
        self.assertNotIn("[int]$TimeoutSeconds = 900", barrier_block)
        self.assertIn('$wuProcesses = @("tiworker", "trustedinstaller")', barrier_block)
        self.assertNotIn('"mousocoreworker", "tiworker", "trustedinstaller", "usoclient"', barrier_block)
        self.assertIn("TotalProcessorTime.TotalMilliseconds", barrier_block)
        self.assertIn("if (-not $bgStillActive)", barrier_block)

        # 2. Uklonjen dupli 900s poziv u Phase6 i Invoke-Phase6Finalize
        self.assertIn("Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 45", phase6_src)
        self.assertNotIn("Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 900", phase6_src)
        self.assertNotIn("Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 900", cleanup_src)
        self.assertIn("$spAlreadyDone =", cleanup_src)

        # 3. COMPLETED lifecycle čuva cooling_verified / diagnostics_status
        self.assertIn("Get-WisTelemetryDiagnostics -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir", cleanup_src)
        self.assertIn('{ "diagnostics_status", diagStatus }', ui_src)
        self.assertIn('{ "cooling_verified", coolingVerified }', ui_src)
        self.assertIn('{ "manual_check_required", !coolingVerified }', ui_src)
        self.assertIn('{ "max_cpu_temp_c", _lastStressMaxTemp }', ui_src)
        self.assertIn('{ "sensor_available", !isSensorUnavailable }', ui_src)


if __name__ == "__main__":
    unittest.main()
