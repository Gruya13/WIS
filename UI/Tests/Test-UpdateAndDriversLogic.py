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
        """Praćeni .example.ini šabloni i lokalni Wifi-Servis*.ini fajlovi moraju imati ShareName=Servis i DriversSubDir=Drivers\\Harvested\\Models."""
        for p in WIFI_INI_PATHS:
            if not p.exists() and not p.name.endswith(".example.ini"):
                continue
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

        # 3. Legalna OA3x/OSPP/CustomLicensing aktivacija (bez spoljnog download-and-execute) + PdfDefault GPO + CurrentPhase=4
        self.assertIn("OA3xOriginalProductKey", apps_src)
        self.assertIn("OSPP.VBS", apps_src)
        self.assertIn("Invoke-CustomLicensing.ps1", apps_src)
        act_match = re.search(r"function Invoke-WindowsOfficeActivation\s*\{([\s\S]*?)^\}", apps_src, re.M)
        self.assertIsNotNone(act_match, "Invoke-WindowsOfficeActivation mora postojati u WIS.Apps.psm1")
        act_body = act_match.group(1)
        for forbidden_pattern in (r"\bInvoke-RestMethod\b", r"\bInvoke-WebRequest\b", r"\bInvoke-Expression\b", r"\birm\s+", r"\biex\s+"):
            self.assertNotRegex(act_body, re.compile(forbidden_pattern, re.IGNORECASE))
        self.assertIn("AppAssoc_Acrobat.xml", apps_src)
        self.assertIn("$Global:CurrentPhase = 4", apps_src)

    def test_07_wis_task_229_wu_barrier_fast_exit_and_lifecycle_diagnostics(self):
        """Proverava brzo izlaženje WU barijere (wu-secondpass-done.flag, bez idle MoUsoCoreWorker) i očuvanje cooling_verified."""
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        ui_src = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "UI").glob("MainWindow*.cs")))

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

    def test_08_wis_task_232_benchmark_and_model_dna_integration(self):
        """Proverava klijentski WIS Benchmark (Phase1..6, Common, Update, Hardware, Telemetry, Cleanup) i Model DNA ugovore."""
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        tel_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Telemetry.psm1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")

        # 1. WIS.Common.psm1 benchmark funkcije i eksport
        for fn in (
            "Start-WisPhaseBenchmark",
            "Complete-WisPhaseBenchmark",
            "Add-WisBenchmarkTransfer",
            "Get-WisBenchmarkSummary",
        ):
            self.assertIn(f"function {fn}", common_src, f"Nedostaje funkcija {fn} u WIS.Common.psm1")
        common_export = re.search(r"Export-ModuleMember\s+-Function[\s\S]*?\Z", common_src)
        self.assertIsNotNone(common_export)
        for fn in (
            "Start-WisPhaseBenchmark",
            "Complete-WisPhaseBenchmark",
            "Add-WisBenchmarkTransfer",
            "Get-WisBenchmarkSummary",
        ):
            self.assertIn(fn, common_export.group(0), f"{fn} mora biti eksportovan u WIS.Common.psm1")

        # 2. WIS.Hardware.psm1 Model DNA + benchmark transferi
        self.assertIn("function Send-WisModelDnaToHub", self.hw_src)
        self.assertIn("/api/model-profile/dna", self.hw_src)
        hw_export = re.search(r"Export-ModuleMember\s+-Function[\s\S]*?\Z", self.hw_src)
        self.assertIsNotNone(hw_export)
        self.assertIn("Send-WisModelDnaToHub", hw_export.group(0))
        self.assertIn("Add-WisBenchmarkTransfer", self.hw_src)

        # 3. WIS.Update.psm1 benchmark transferi + Model DNA poziv
        self.assertIn("Add-WisBenchmarkTransfer", self.update_src)
        self.assertIn("Send-WisModelDnaToHub", self.update_src)

        # 4. Telemetry i Cleanup šalju/upisuju benchmark objekat
        self.assertIn("Get-WisBenchmarkSummary", tel_src)
        self.assertIn('$payload["benchmark"]', tel_src)
        self.assertIn("Get-WisBenchmarkSummary", cleanup_src)
        self.assertIn('$kartonMap["benchmark"]', cleanup_src)

        # 5. Sve Phase1..Phase6 skripte mere početak i kraj faze
        phase_files = [
            "Phase1-Network.ps1",
            "Phase2-Drivers.ps1",
            "Phase3-Optimization.ps1",
            "Phase4-Apps.ps1",
            "Phase5-Stress.ps1",
            "Phase6-Finalize.ps1",
        ]
        for idx, pf in enumerate(phase_files, start=1):
            p_src = (ROOT / "UI" / "Skripte" / "Phases" / pf).read_text(encoding="utf-8")
            self.assertIn(f"Start-WisPhaseBenchmark -Phase {idx}", p_src, f"{pf} mora pozivati Start-WisPhaseBenchmark")
            self.assertIn(f"Complete-WisPhaseBenchmark -Phase {idx}", p_src, f"{pf} mora pozivati Complete-WisPhaseBenchmark")

    def test_09_wis_task_230_lean_usb_nas_programi_fallback_and_admin_normalization(self):
        """Proverava fallback na NAS kada je uboden Lean USB bez Office/Acrobat i normalizaciju Username=servis->admin."""
        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")

        # 1. Normalizacija korisnickog imena 'servis' -> 'admin' u oba Get-ServisNasSettings i Get-ServisProgramiRoot
        self.assertIn('-ieq "servis"', core_src)
        self.assertIn('-ieq "servis"', self.update_src)
        self.assertIn('-ieq "servis"', apps_src)

        # 2. Ensure-ProgramiAvailable obavezno poziva Sync-ProgramiFromNas -Force ako C:\setup\Programi nema Office/Acrobat (cak i sa ubodenim Lean USB-om)
        self.assertIn("function Test-ProgramiDirComplete", core_src)
        ensure_block = core_src.split("function Ensure-ProgramiAvailable", 1)[1].split("function Save-State", 1)[0]
        self.assertIn("Test-ProgramiDirComplete -DirPath $localProg", ensure_block)
        self.assertIn("Sync-ProgramiFromNas -DestinationDir $localProg -Force", ensure_block)
        self.assertNotIn("if (-not $foundDrive)", ensure_block)

        # 3. Get-ServisProgramiRoot zahteva Office+Acrobat (Test-ProgramiPackageComplete) i P4.Acrobat ima Sync-ProgramiFromNas -Force fallback
        self.assertIn("function Test-ProgramiPackageComplete", apps_src)
        prog_root_block = apps_src.split("function Get-ServisProgramiRoot", 1)[1].split("function Invoke-InstallerWithLocalFallback", 1)[0]
        self.assertIn("Test-ProgramiPackageComplete -DirPath $loc", prog_root_block)
        self.assertIn("Test-ProgramiPackageComplete -DirPath $uc", prog_root_block)
        self.assertIn("Lokalni Adobe Acrobat Pro paket nije pronadjen. Preuzimam sa servisnog NAS-a...", apps_src)

    def test_10_wis_task_235_t14_live_audit_fixes(self):
        """Proverava popravke sa ThinkPad T14 testa: Select-RelevantDriverInfFiles, DriverHarvest 1.5GB/dedup guard, model_aliases, DotNet UI i Phase5 benchmark fallback."""
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        tel_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Telemetry.psm1").read_text(encoding="utf-8")
        stepguard_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.StepGuard.psm1").read_text(encoding="utf-8")

        # 1. HWID filtriranje .inf paketa pre pnputil-a + model_aliases u Model DNA
        self.assertIn("function Select-RelevantDriverInfFiles", self.hw_src)
        self.assertIn("Select-RelevantDriverInfFiles -InfFiles $infFiles", self.hw_src)
        self.assertIn("-TargetHwids $hwids", self.hw_src)
        self.assertIn("model_aliases", self.hw_src)

        # 2. DriverHarvest deduplikacija po tar paketu, ciscenje tudjih HDX i limit 5.0 GB + StepGuard zastavice
        self.assertIn("maxHealthyTarBytes = [long]5368709120", self.update_src)
        self.assertIn("DriverHarvest deduplikacija", self.update_src)
        self.assertIn("local-pnp-done.flag", self.update_src)
        self.assertIn("wu-drivers-skipped.flag", self.update_src)

        # 3. DotNet appsStatusMap, PdfDefault Win11 i Debloat flag + bez curenja hashtable-a na stdout
        self.assertIn('$appsStatusMap["DotNet"]', apps_src)
        self.assertIn("($isGpoAcrobat -and $isClassAcrobat)", apps_src)
        self.assertIn("debloat-done.flag", branding_src)
        self.assertIn("$null = Write-WisStepJournal", stepguard_src)

        # 4. Phase5/Phase1 benchmark fallback & kompletan Send-FleetFinalReport
        self.assertIn("hwtest-result.json", common_src)
        self.assertIn("total_phase_duration_sec", tel_src)
        self.assertIn("Get-SystemHardwareInfo", tel_src)

    def test_11_wis_task_236_phase6_guard_generic_block_and_metadata_filter(self):
        """Proverava WIS-TASK-236 zaštite: Phase6 bez ponovnog <Model>.tar, blokada GenericPC i ignore AggregatedMetadata.cab."""
        # 1. Test-IsGenericComputerModel i blokada u preinstall/harvest funkcijama
        self.assertIn("function Test-IsGenericComputerModel", self.update_src)
        export_blk = re.search(r"Export-ModuleMember\s+-Function[\s\S]*?\Z", self.update_src).group(0)
        self.assertIn("Test-IsGenericComputerModel", export_blk)

        preinst_block = self.update_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]
        self.assertIn("model-drivers-preinstalled.flag", preinst_block)
        self.assertIn("Test-IsGenericComputerModel -CleanModel $cleanModel", preinst_block)

        harvest_block = self.update_src.split("function Export-ServisHardwareDrivers", 1)[1].split("function Export-ServisNetworkDrivers", 1)[0]
        self.assertIn("Test-IsGenericComputerModel -CleanModel $cleanModel", harvest_block)

        # 2. Invoke-NasUpdatePrePatch ima -SkipDriverPreInstall, AggregatedMetadata filter i nas-prepatch-done.flag
        prepatch_block = self.update_src.split("function Invoke-NasUpdatePrePatch", 1)[1].split("function Save-UpdatePackageFromMsCatalog", 1)[0]
        self.assertIn("[switch]$SkipDriverPreInstall", prepatch_block)
        self.assertIn("aggregatedmetadata", prepatch_block.lower())
        self.assertIn("if (-not $SkipDriverPreInstall)", prepatch_block)
        self.assertIn("nas-prepatch-done.flag", prepatch_block)

        # 3. Invoke-WindowsUpdateSecondPass ne sme ponovo pokretati model drajvere niti pre-patch kad flag postoji
        sp_block = self.update_src.split("function Invoke-WindowsUpdateSecondPass", 1)[1].split("function Wait-WindowsUpdateCompletionBarrier", 1)[0]
        self.assertIn("nas-prepatch-done.flag", sp_block)
        self.assertIn("Invoke-NasUpdatePrePatch -SkipDriverPreInstall", sp_block)

    def test_12_wis_task_247_field_monitoring_fixes(self):
        """Proverava WIS-TASK-247 ispravke sa terenskog pracenja 14+3 racunara."""
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        stepguard_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.StepGuard.psm1").read_text(encoding="utf-8")
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        tel_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Telemetry.psm1").read_text(encoding="utf-8")
        main_cs = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "UI").glob("MainWindow*.cs")))
        tel_cs = (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8")
        autodiag_cs = (ROOT / "UI" / "HwTest" / "Core" / "AutoHardwareDiagnostics.cs").read_text(encoding="utf-8")
        probe_cs = (ROOT / "UI" / "HwTest" / "Core" / "HardwareProbe.cs").read_text(encoding="utf-8")

        # 1. VCRedist: lokalni EXE + WaitForExit + taskkill /T /F za winget fallback i timeout faze u UI
        self.assertIn("Programi\\VCRedist", branding_src)
        self.assertIn("WaitForExit(180000)", branding_src)
        self.assertIn("taskkill.exe /PID $wp.Id /T /F", branding_src)
        self.assertIn("WaitForExit(120000)", apps_src)
        self.assertIn("taskkill.exe /PID $wp.Id /T /F", apps_src)
        self.assertIn("/PID {0} /T /F", main_cs)

        # 2. P4.Acrobat pracenje msiexec/acrobat_msi.log i retry
        self.assertIn("acrobat_msi.log", apps_src)
        self.assertIn("acrobat_msi_retry.log", apps_src)

        # 3. Select-RelevantDriverInfFiles bez comma-wrap i 90000 ms timeout za /api/drivers/resolve
        sel_block = self.hw_src.split("function Select-RelevantDriverInfFiles", 1)[1].split("function ", 1)[0]
        self.assertNotIn("return ,", sel_block)
        self.assertIn("$req.Timeout = 90000", self.hw_src)
        self.assertIn("ForEach-Object { $_ } | Where-Object { $_ -and $_.FullName }", self.hw_src)

        # 4. StepGuard AddStatement, DriverHarvest 5GB/.harvest.lock/KeepAlive/1200000 i NetworkRecovery
        self.assertIn("[void]$ps.AddStatement()", stepguard_src)
        self.assertIn("maxHealthyTarBytes = [long]5368709120", self.update_src)
        self.assertIn(".harvest.lock", self.update_src)
        self.assertIn("$req.KeepAlive = $false", self.update_src)
        self.assertIn("-TimeoutMs 1200000", self.update_src)
        self.assertIn("function Restore-ServisNetworkConnectivityIfNeeded", self.update_src)
        export_blk = re.search(r"Export-ModuleMember\s+-Function[\s\S]*?\Z", self.update_src).group(0)
        self.assertIn("Restore-ServisNetworkConnectivityIfNeeded", export_blk)

        # 5. Start-WisPhaseBenchmark reset duration_sec, C# throttling prag 85/80+25, pending-hub-commands & hwtest
        self.assertIn("prev_duration_sec = $prevDur", common_src)
        self.assertIn("maxT >= 85.0f || (maxThrot >= 25.0f && maxT >= 80.0f)", main_cs)
        self.assertIn("pending-hub-commands.json", tel_cs)
        self.assertIn("skip_step", tel_cs)
        self.assertIn("pending-hub-commands.json", tel_src)
        self.assertIn('"skip_step"', tel_src)
        self.assertIn("hwtest-result.json", tel_src)

        # 6. HwTest: neblokirajuci CPU stres i filter Facial Recognition / Hello Face Software Device
        self.assertIn("ThreadPool.QueueUserWorkItem", autodiag_cs)
        self.assertIn('FACIAL RECOGNITION', probe_cs)

    def test_13_wis_task_254_bug02_to_bug08_fixes(self):
        """Proverava WIS-TASK-254 (v3.1.1) sanaciju BUG-02..BUG-08 u PS modulima, Fazi 6 i WIS.UI."""
        main_cs = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "UI").glob("MainWindow*.cs")))
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")

        # BUG-02 & BUG-07 (PS klijent): network recovery retry + 2s PnP pauza + async_download polling
        res_block = self.hw_src.split("function Resolve-MissingDriversFromHub", 1)[1].split("function Send-WisModelDnaToHub", 1)[0]
        self.assertIn("Restore-ServisNetworkConnectivityIfNeeded -WaitSeconds 15", res_block)
        self.assertIn("async_download = $true", res_block)
        self.assertIn('$_.status -eq "downloading"', res_block)
        self.assertIn("Start-Sleep -Seconds 2", res_block)

        # BUG-03: Select-RelevantDriverInfFiles prag <= 12 + Extension/SoftwareComponent + /subdirs /install
        sel_block = self.hw_src.split("function Select-RelevantDriverInfFiles", 1)[1].split("function ", 1)[0]
        self.assertIn("$allInfs.Count -le 12", sel_block)
        self.assertIn("SOFTWARECOMPONENT", sel_block)
        self.assertIn("/subdirs /install", self.hw_src)

        # BUG-04: P2.WuDrivers ForceQuiet, ResultCode, retry i logovanje bez praznog catch {}
        p2_block = self.update_src.split("function Invoke-Phase2Drivers", 1)[1].split("function Write-BackgroundWuStatus", 1)[0]
        self.assertIn("$installer.ForceQuiet = $true", p2_block)
        self.assertIn("$resCode -in @(2, 3)", p2_block)
        self.assertIn("for ($drvTry = 1; $drvTry -le 2; $drvTry++)", p2_block)
        self.assertNotIn("installRes = $installer.Install()\n", p2_block)

        # BUG-05: Invoke-WindowsUpdateSecondPass bez (,$updateTitles) u Start-Job
        sp_block = self.update_src.split("function Invoke-WindowsUpdateSecondPass", 1)[1].split("function Wait-WindowsUpdateCompletionBarrier", 1)[0]
        self.assertNotIn("-ArgumentList (,$updateTitles)", sp_block)
        self.assertIn("$titlesJson = ConvertTo-Json -InputObject @($updateTitles) -Compress", sp_block)
        self.assertIn("ConvertFrom-Json -InputObject $titlesJson", sp_block)
        self.assertIn("GetUpdateResult($k)", sp_block)

        # BUG-06: BgWinUpdate ceka do 900s (300 ciklusa) i belezi WARN pri 0/N; Phase6 radi finalni Resolve
        bg_block = self.update_src.split("function Invoke-BackgroundWindowsUpdateWorker", 1)[1].split("function Invoke-WindowsUpdateSecondPass", 1)[0]
        self.assertIn("while ($waitCycles -lt 300)", bg_block)
        self.assertIn("$totalFoundAll -gt 0 -and $totalInstalledAll -eq 0", bg_block)
        self.assertIn('Write-BackgroundWuStatus -Status "WARN"', bg_block)
        self.assertIn("WIS.Hardware.psm1", phase6_src)
        self.assertIn("Resolve-MissingDriversFromHub", phase6_src)

        # BUG-07 (UI klijent): stvarni model/serial_number, Timeout = 120000 i "downloading" polling
        self.assertNotIn('reqDict["model"] = "";', main_cs)
        self.assertIn('reqDict["model"] = resolvedModel;', main_cs)
        self.assertIn('reqDict["serial_number"] = resolvedSerial;', main_cs)
        self.assertIn('reqDict["async_download"] = true;', main_cs)
        self.assertIn("httpReq.Timeout = 120000;", main_cs)
        self.assertIn('status.Equals("downloading"', main_cs)

        # BUG-08: Zabrana prljave berbe u Fazi 2 i odlaganje za kraj Faze 6
        self.assertIn("function Test-WisDeviceManagerReadyForHarvest", self.update_src)
        self.assertIn("driver-harvest-deferred.flag", p2_block)
        self.assertIn("driver-harvest-deferred.flag", phase6_src)


if __name__ == "__main__":
    unittest.main()
