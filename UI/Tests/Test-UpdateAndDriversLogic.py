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

    def test_14_wis_task_257_live_v311_hotfixes(self):
        """Proverava WIS-TASK-257 live v3.1.1 hotfixeve (Stavke 1, 2 i BAG-10..BAG-16)."""
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        stepguard_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.StepGuard.psm1").read_text(encoding="utf-8")
        main_cs = (ROOT / "UI" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        loc_cs = (ROOT / "UI" / "LocalizationService.cs").read_text(encoding="utf-8")
        sync_py = (ROOT / "ISO" / "Skripte" / "Sync-Versions.py").read_text(encoding="utf-8")

        # BAG-10: GUID JSON HWID bez pokvarenog regex-a + InnerException.Response u Invoke-HubDriverResolvePost
        res_block = self.hw_src.split("function Resolve-MissingDriversFromHub", 1)[1].split("function Send-WisModelDnaToHub", 1)[0]
        self.assertIn("__WIS_HWIDS_PLACEHOLDER__", res_block)
        self.assertNotIn(r'[^,\}]+', res_block)
        self.assertIn("$exWalk.InnerException", res_block)

        # BAG-12: $problemCount umesto unrollovanog (@($dmCheck.ProblemDevices)).Count iz if izraza
        p2_block = self.update_src.split("function Invoke-Phase2Drivers", 1)[1].split("function Write-BackgroundWuStatus", 1)[0]
        self.assertIn("$problemCount = @($problemDevices).Count", p2_block)
        self.assertIn("Detektovano $problemCount problema sa drajverima", p2_block)

        # BAG-13: WHQL/Authenticode .cat provera u Install-DriverTarArchive + skip HTTP duplog preuzimanja + prnms009 ciscenje
        tar_block = self.update_src.split("function Install-DriverTarArchive", 1)[1].split("function Invoke-ModelDriversPreInstall", 1)[0]
        self.assertIn("Get-AuthenticodeSignature", tar_block)
        self.assertIn("nedozvoljene skripte", tar_block)
        pre_block = self.update_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]
        self.assertIn("$skipHttpModelFallback", pre_block)
        self.assertIn("prnms009", self.update_src)

        # BAG-14: Kod 14 (Pending Restart) ne sme blokirati Device Manager proveru ni Resolve-MissingDriversFromHub
        dm_block = self.hw_src.split("function Test-WisDeviceManagerClean", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("-notin @(0, 14, 22, 45)", res_block)
        self.assertIn("$code -ne 14", dm_block)
        self.assertIn("-notin @(14, 22, 45)", tar_block)

        # BAG-15: 0x80240016 circuit breaker u P2.WuDrivers
        self.assertIn("$consecutiveNotAllowed", p2_block)
        self.assertIn("0x80240016", p2_block)
        self.assertIn("$consecutiveNotAllowed -ge 3", p2_block)

        # Stavka 1: Legalni [Licensing] (OfficeKmsHost/OfficeProductKey) i $hasOffice u Get-WisAppPresence
        self.assertIn("$inLicensingSection", apps_src)
        self.assertIn("OfficeKmsHost", apps_src)
        self.assertIn("OfficeProductKey", apps_src)
        self.assertIn("$result.OfficeInstalled  = $hasOffice", apps_src)

        # Stavka 2: P3.Debloat (rani debloat-done.flag + 1x Get-Appx*), P3.Telemetry bez Get-CimInstance, MTA StepGuard, MainWindow ExitCode 2 oporavak
        p3_block = branding_src.split("function Invoke-Phase3Optimization", 1)[1].split("Export-ModuleMember", 1)[0]
        tel_test_block = p3_block.split('-StepId "P3.Telemetry"', 1)[1].split('-Action {', 1)[0]
        self.assertNotIn("Get-CimInstance", tel_test_block)
        self.assertIn(r"HKLM:\SYSTEM\CurrentControlSet\Services\$sName", tel_test_block)
        debloat_test_head = p3_block.split('-StepId "P3.Debloat"', 1)[1].split("$foundBloat", 1)[0]
        self.assertIn("debloat-done.flag", debloat_test_head)
        self.assertIn("ApartmentState]::MTA", stepguard_src)
        self.assertIn("_phase3AutoRetryCount", main_cs)
        self.assertIn("try { proc.WaitForExit(); } catch { }", main_cs)

        # BAG-16: AppTitle 3.4.3 i Sync-Versions.py podrska
        self.assertIn('"WIS v3.4.3 - Windows Installation Assistant"', loc_cs)
        self.assertIn('"WIS v3.4.3 - 视窗系统自动化安装助手"', loc_cs)
        self.assertIn("UI/LocalizationService.cs", sync_py)

    def test_15_wis_task_258_driver_pool_client_integration(self):
        """Proverava WIS-TASK-258 (v3.2.0) integraciju Jedinstvenog Driver Pool-a u WIS.Update i WIS.Hardware."""
        pool_block = self.update_src.split("function Invoke-DriverPoolBundleInstall", 1)[1].split("function Invoke-ModelDriversPreInstall", 1)[0]
        self.assertIn("/api/drivers/pool/bundle", pool_block)
        self.assertIn("Install-DriverTarArchive", pool_block)
        self.assertIn('Category "driver_pool"', pool_block)
        self.assertIn("WIS-DriverPool/3.2", pool_block)

        pre_block = self.update_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]
        self.assertIn("Invoke-DriverPoolBundleInstall", pre_block)
        self.assertIn("$poolTargetModel", pre_block)
        self.assertIn("Export-ModuleMember", self.update_src)
        export_tail = self.update_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Invoke-DriverPoolBundleInstall", export_tail)

        res_block = self.hw_src.split("function Resolve-MissingDriversFromHub", 1)[1].split("function Send-WisModelDnaToHub", 1)[0]
        self.assertIn("Invoke-DriverPoolBundleInstall", res_block)
        self.assertIn("$batchMissingHwids", res_block)
        self.assertIn("WIS-DriverEngine/3.2", res_block)

    def test_16_wis_task_260_stepguard_deadlock_and_winrar_resilience(self):
        """Proverava WIS-TASK-260 (v3.2.1) popravku deadlocka u StepGuard-u, otpornost P4.WinRAR i Build-WIS.ps1 CI integraciju."""
        stepguard_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.StepGuard.psm1").read_text(encoding="utf-8")
        self.assertIn("PSThreadOptions]::UseNewThread", stepguard_src)
        self.assertIn("ApartmentState]::MTA", stepguard_src)
        self.assertNotIn("ReuseThread", stepguard_src)
        self.assertIn("BeginStop", stepguard_src)
        self.assertIn("BeginClose", stepguard_src)
        self.assertIn("WisTelemetryConfig|WisBloatPatterns", stepguard_src)
        self.assertIn("ExportedCommands", stepguard_src)

        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        p4_wr = apps_src.split("# 2. WINRAR - P4.WinRAR", 1)[1].split("# 3. GOOGLE CHROME - P4.Chrome", 1)[0]
        self.assertIn("@(Get-ChildItem", p4_wr)
        self.assertIn('Get-WisAppPresence -AppId "WinRAR"', p4_wr)
        self.assertIn('"/S"', p4_wr)
        self.assertIn("Send-IpcLog", p4_wr)

        build_ps1 = (ROOT / "ISO" / "Skripte" / "Build-WIS.ps1").read_text(encoding="utf-8")
        self.assertIn("Test-StepGuard.ps1", build_ps1)

    def test_17_v330_live_audit_improvements_imp02_to_imp13(self):
        """Proverava implementaciju svih PS5.1/ini unapredjenja (IMP-02..IMP-09, IMP-11..IMP-13)."""
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        ini_root = (ROOT / "Wifi-Servis.example.ini").read_text(encoding="utf-8")
        ini_iso = (ROOT / "ISO" / "Wifi-Servis.example.ini").read_text(encoding="utf-8")

        # IMP-04: Send-IpcAppUpdate definisana i izvezena
        self.assertIn("function Send-IpcAppUpdate", common_src)
        self.assertIn("Send-IpcAppUpdate,", common_src.split("Export-ModuleMember", 1)[1])

        # IMP-06: DPI scaling flag i LogPixels=96 za 100% Sub-FHD
        self.assertIn(r"C:\setup\dpi-scaling-done.flag", branding_src)
        scale_block = branding_src.split("function Set-OptimalDisplayScaling", 1)[1].split("function ", 1)[0]
        self.assertIn('"LogPixels" -Value 96', scale_block)
        p3_scale_test = branding_src.split('-StepId "P3.Scaling"', 1)[1].split('-Action {', 1)[0]
        self.assertIn("dpi-scaling-done.flag", p3_scale_test)

        # IMP-05: P3.VcRedist prvo trazi NAS Programi\VCRedist pre winget fallbacka (sa lazy importom WIS.Update i ispravnim Disconnect)
        p3_vc = branding_src.split('-StepId "P3.VcRedist"', 1)[1].split('# 6. OSLOBA', 1)[0]
        self.assertIn("WIS.Update.psm1", p3_vc)
        self.assertIn("Disconnect-ServisNasUpdatesShare -UncShare $nasVcCtx.UncShare", p3_vc)
        nas_idx = p3_vc.find("Connect-ServisNasUpdatesShare")
        wg_idx = p3_vc.find("winget.exe")
        self.assertGreater(nas_idx, 0)
        self.assertGreater(wg_idx, nas_idx)

        # IMP-15: Invoke-ServisInterimReboot emituje Send-IpcStatus za Fazu 3 Restart u toku
        reboot_block = branding_src.split("function Invoke-ServisInterimReboot", 1)[1].split("function Invoke-Phase3Optimization", 1)[0]
        self.assertIn('Send-IpcStatus -Phase 3 -SubPhase "Restart u toku"', reboot_block)

        # IMP-09: Realna procena kopiranja u WIS.Core i Mode=DriversOnly uz ocuvan HarvestMode=All
        self.assertIn("obicno 1-5 min", core_src)
        self.assertIn("Mode=DriversOnly", ini_root)
        self.assertIn("HarvestMode=All", ini_root)
        self.assertIn("Mode=DriversOnly", ini_iso)
        self.assertIn("HarvestMode=All", ini_iso)

        # IMP-09: Install-LocalUpdatePackageFile ne blokira slepo na -Wait i salje heartbeat
        inst_pkg = self.update_src.split("function Install-LocalUpdatePackageFile", 1)[1].split("function Receive-UpdateFileFromUrl", 1)[0]
        self.assertNotIn("-Wait -PassThru", inst_pkg)
        self.assertIn("WaitForExit(1000)", inst_pkg)
        self.assertIn("Send-IpcStatus", inst_pkg)
        self.assertIn("2400", inst_pkg)

        # IMP-02, IMP-12, IMP-13: Harvest heartbeat kopiranje, notify-harvest, TTL cleanup za lock i .tar.tmp.*
        self.assertIn("function Send-ServisHubHarvestNotify", self.update_src)
        self.assertIn("function Copy-FileWithHeartbeat", self.update_src)
        self.assertIn("/api/drivers/pool/notify-harvest", self.update_src)
        export_tail = self.update_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Send-ServisHubHarvestNotify", export_tail)
        self.assertIn("Copy-FileWithHeartbeat", export_tail)
        self.assertIn("Connect-ServisNasUpdatesShare", export_tail)
        self.assertIn("Disconnect-ServisNasUpdatesShare", export_tail)
        harvest_fn = self.update_src.split("function Export-ServisHardwareDrivers", 1)[1].split("function Export-ServisNetworkDrivers", 1)[0]
        self.assertIn("Copy-FileWithHeartbeat", harvest_fn)
        self.assertIn("Send-ServisHubHarvestNotify", harvest_fn)
        self.assertIn(".tar.tmp.*", harvest_fn)
        self.assertIn("TotalMinutes -lt 30", harvest_fn)

        # IMP-03: Uklonjen $onlyGenericGpu bypass i dodata provera DM pre wu-drivers-skipped.flag
        p2_block = self.update_src.split("function Invoke-Phase2Drivers", 1)[1].split("function Write-BackgroundWuStatus", 1)[0]
        self.assertNotIn("$onlyGenericGpu", p2_block)
        self.assertIn("$dmSkipVerify = if (Get-Command Test-WisDeviceManagerClean", p2_block)

        # IMP-07: Instant break za 0x80248007/0x80070103 i 12s za 0x80240016
        self.assertIn('"0x80248007", "0x80070103"', p2_block)
        self.assertIn('{ 12 } else { 3 }', p2_block)

        # IMP-11: Asinhroni BeginInvoke cleanup u Invoke-WindowsUpdateSecondPass
        sp_block = self.update_src.split("function Invoke-WindowsUpdateSecondPass", 1)[1].split("function Wait-WindowsUpdateCompletionBarrier", 1)[0]
        self.assertIn("BeginInvoke()", sp_block)

    def test_18_v322_overnight_fixes_imp16_imp17_imp18(self):
        """Proverava IMP-16 (SecondPass Search timeout 120s), IMP-17 (Basic Display u Resolve) i IMP-18 (WinRAR/Chrome NAS retry)."""
        # IMP-16: Asinhroni Search() sa 120s watchdog-om u Invoke-WindowsUpdateSecondPass
        sp_block = self.update_src.split("function Invoke-WindowsUpdateSecondPass", 1)[1].split("function Wait-WindowsUpdateCompletionBarrier", 1)[0]
        self.assertIn("$searchTimeoutSec = 120", sp_block)
        self.assertIn("$searchJob = Start-Job", sp_block)
        self.assertIn("SEARCH_TIMEOUT", sp_block)
        self.assertIn("$cleanupSearchPs.BeginInvoke()", sp_block)

        # IMP-17: Microsoft Basic Display Adapter / Standard VGA ukljucen u Resolve-MissingDriversFromHub
        res_block = self.hw_src.split("function Resolve-MissingDriversFromHub", 1)[1].split("function Send-WisModelDnaToHub", 1)[0]
        self.assertIn("function Get-WisProblematicPnpDevices", res_block)
        self.assertIn("Microsoft Basic Display Adapter|Standard VGA", res_block)
        self.assertIn("Get-WisProblematicPnpDevices", res_block)

        # IMP-18: WinRAR i Chrome rade Sync-ProgramiFromNas retry pre winget fallbacka
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        p4_wr = apps_src.split("# 2. WINRAR - P4.WinRAR", 1)[1].split("# 3. GOOGLE CHROME - P4.Chrome", 1)[0]
        p4_ch = apps_src.split("# 3. GOOGLE CHROME - P4.Chrome", 1)[1].split("# 4. ADOBE ACROBAT PRO - P4.Acrobat", 1)[0]
        self.assertIn("Sync-ProgramiFromNas", p4_wr)
        self.assertIn("Sync-ProgramiFromNas", p4_ch)

    def test_19_v341_office_activation_diagnostics_and_licensing_hooks(self):
        """Proverava WIS-TASK-264 (LIC-01..LIC-06) i Backlog B1/B3 ugovore za WIS v3.4.1."""
        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        tel_cs = (ROOT / "UI" / "TelemetryService.cs").read_text(encoding="utf-8")
        ini_root = (ROOT / "Wifi-Servis.example.ini").read_text(encoding="utf-8")
        ini_iso = (ROOT / "ISO" / "Wifi-Servis.example.ini").read_text(encoding="utf-8")

        # 1. WIS.Core.psm1: Get-ServisLicensingSettings, Sync-ServisLicensingFromNas i bezbednosni helperi
        for sym in (
            "function Get-ServisLicensingSettings",
            "function Invoke-WisCoreNetUse",
            "function Test-WisSafeLicensingItem",
            "function Test-WisSafeLicensingDirHasFiles",
            "function Copy-WisSafeLicensingTree",
            "function Sync-ServisLicensingFromNas",
        ):
            self.assertIn(sym, core_src, f"Nedostaje {sym} u WIS.Core.psm1")

        lic_cfg_block = core_src.split("function Get-ServisLicensingSettings", 1)[1].split("function ", 1)[0]
        for tok in ("OfficeActivationMode", "OfficeKmsHost", "OfficeKmsPort", "OfficeProductKey", '"externalhook"', '"skip"'):
            self.assertIn(tok, lic_cfg_block)

        net_use_block = core_src.split("function Invoke-WisCoreNetUse", 1)[1].split("function Test-WisSafeLicensingItem", 1)[0]
        self.assertIn("System.Diagnostics.ProcessStartInfo", net_use_block)
        self.assertIn("$psi.FileName = $netExe", net_use_block)
        self.assertNotIn("cmd.exe /c", net_use_block.lower())

        safe_item_block = core_src.split("function Test-WisSafeLicensingItem", 1)[1].split("function Test-WisSafeLicensingDirHasFiles", 1)[0]
        self.assertIn("FileAttributes]::ReparsePoint", safe_item_block)
        self.assertIn("FileAttributes]::Hidden", safe_item_block)
        self.assertIn("FileAttributes]::System", safe_item_block)

        sync_lic_block = core_src.split("function Sync-ServisLicensingFromNas", 1)[1].split("function Test-ProgramiDirComplete", 1)[0]
        self.assertIn(r'C:\setup\Cache\Licensing', sync_lic_block)
        self.assertIn('Invoke-WisCoreNetUse -Action "Connect"', sync_lic_block)
        self.assertIn('Invoke-WisCoreNetUse -Action "DisconnectAll"', sync_lic_block)
        self.assertIn("Copy-WisSafeLicensingTree", sync_lic_block)
        self.assertNotIn("cmd.exe /c", sync_lic_block.lower())
        self.assertNotIn("cmd /c", sync_lic_block.lower())

        copy_core_tree = core_src.split("function Copy-WisSafeLicensingTree", 1)[1].split("function Sync-ServisLicensingFromNas", 1)[0]
        self.assertIn("[System.IO.Path]::GetFullPath", copy_core_tree)
        self.assertIn("Unblock-File -LiteralPath", copy_core_tree)
        self.assertIn(":Zone.Identifier", copy_core_tree)

        core_export = core_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Get-ServisLicensingSettings", core_export)
        self.assertIn("Sync-ServisLicensingFromNas", core_export)

        # 2. WIS.Apps.psm1: LIC-01..LIC-05 helperi, sppsvc, timeouti, uklonjen duplikat WINWORD.EXE i lokalno kesiranje
        for sym in (
            "function Test-OfficeInstalled",
            "function Protect-WisLicenseLogText",
            "function Format-Win32CommandLineArgument",
            "function Copy-WisSafeDirectoryTree",
            "function Invoke-ProcessStartInfoWithTimeout",
            "function Invoke-CscriptWithTimeout",
            "function Get-OfficeLicenseStatus",
            "function Wait-OfficeInstallationReady",
            "function Invoke-ServisExternalLicensingHook",
        ):
            self.assertIn(sym, apps_src, f"Nedostaje {sym} u WIS.Apps.psm1")

        office_inst_block = apps_src.split("function Test-OfficeInstalled", 1)[1].split("function Protect-WisLicenseLogText", 1)[0]
        for p in (
            r"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
            r"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
            r"C:\Program Files\Microsoft Office\Office16\WINWORD.EXE",
            r"C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE",
            r"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
            r"C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
            r"C:\Program Files\Microsoft Office\Office16\EXCEL.EXE",
            r"C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE",
        ):
            self.assertIn(p, office_inst_block)

        office_presence_blk = apps_src.split("'(?i)^(Office|Microsoft Office|MS Office)$'", 1)[1].split("'(?i)^BatteryBar$'", 1)[0]
        dup_word_path = r'"C:\Program Files\Microsoft Office\Office16\WINWORD.EXE"'
        self.assertEqual(office_presence_blk.count(dup_word_path), 1, "Duplirana WINWORD.EXE putanja mora ostati uklonjena u Get-WisAppPresence")
        self.assertIn(r'"C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE"', office_presence_blk)

        protect_blk = apps_src.split("function Protect-WisLicenseLogText", 1)[1].split("function Format-Win32CommandLineArgument", 1)[0]
        self.assertIn("*****-*****-*****-*****-$2", protect_blk)

        safe_dir_blk = apps_src.split("function Copy-WisSafeDirectoryTree", 1)[1].split("function Invoke-ProcessStartInfoWithTimeout", 1)[0]
        self.assertIn("FileAttributes]::ReparsePoint", safe_dir_blk)
        self.assertIn("-LiteralPath", safe_dir_blk)

        psi_blk = apps_src.split("function Invoke-ProcessStartInfoWithTimeout", 1)[1].split("function Invoke-CscriptWithTimeout", 1)[0]
        self.assertIn("ReadToEndAsync()", psi_blk)
        self.assertIn("taskkill.exe", psi_blk)
        self.assertIn("Protect-WisLicenseLogText", psi_blk)

        cscript_blk = apps_src.split("function Invoke-CscriptWithTimeout", 1)[1].split("function Get-OfficeLicenseStatus", 1)[0]
        self.assertIn("[int]$TimeoutSec = 25", cscript_blk)
        self.assertIn("Format-Win32CommandLineArgument", cscript_blk)
        self.assertIn("Invoke-ProcessStartInfoWithTimeout", cscript_blk)

        lic_status_blk = apps_src.split("function Get-OfficeLicenseStatus", 1)[1].split("function Test-OfficeActivated", 1)[0]
        for tok in ("VFS_SPP", "WMI_SPP", 'Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/dstatus") -TimeoutSec 20', "LastErrorCode", "LicenseStatusText"):
            self.assertIn(tok, lic_status_blk)

        hook_blk = apps_src.split("function Invoke-ServisExternalLicensingHook", 1)[1].split("function Invoke-WindowsOfficeActivation", 1)[0]
        for tok in (
            "[System.IO.Path]::GetFullPath",
            "FileAttributes]::ReparsePoint",
            '@(".ps1", ".cmd", ".bat")',
            r"C:\setup\Cache\Licensing",
            "Copy-WisSafeDirectoryTree",
            "Unblock-File -LiteralPath",
            ":Zone.Identifier",
            "Format-Win32CommandLineArgument",
            "/d /c",
            "[int]$TimeoutSec = 180",
            'EnvironmentVariables["WIS_PROGRAMI_DIR"]',
            'EnvironmentVariables["WIS_OFFICE_INSTALLED"]',
            'EnvironmentVariables["WIS_WIN_ACTIVATED"]',
            'EnvironmentVariables["WIS_OFF_ACTIVATED"]',
        ):
            self.assertIn(tok, hook_blk)

        act_blk = apps_src.split("function Invoke-WindowsOfficeActivation", 1)[1].split("function Invoke-AppsPhase", 1)[0]
        for tok in (
            "Test-OfficeInstalled",
            "Wait-OfficeInstallationReady -TimeoutSec 45",
            'Get-Service -Name "sppsvc"',
            'Start-Service -Name "sppsvc"',
            "Invoke-CscriptWithTimeout",
            "^[A-Za-z0-9._:-]+$",
            "^[A-Za-z0-9-]+$",
            'Sync-ServisLicensingFromNas -DestinationDir "C:\\setup\\Cache\\Licensing"',
            "Invoke-ServisExternalLicensingHook",
            "Write-WisLicenseStatusFile",
            'Write-DiagnosticEvent -Event "LICENSE_CHECK_RESULT"',
        ):
            self.assertIn(tok, act_blk)
        self.assertNotIn("OSPP.VBS`\" /act >$null 2>&1", act_blk)

        apps_export = apps_src.split("Export-ModuleMember", 1)[1]
        for fn in (
            "Test-OfficeInstalled",
            "Protect-WisLicenseLogText",
            "Invoke-CscriptWithTimeout",
            "Get-OfficeLicenseStatus",
            "Invoke-ServisExternalLicensingHook",
        ):
            self.assertIn(fn, apps_export)

        p4_act_blk = apps_src.split("# 9. PROVERA OEM I SISTEMSKIH LICENCI - P4.Activation", 1)[1].split("# Uklanjanje flag datoteke", 1)[0]
        self.assertIn("Protect-WisLicenseLogText -Text ([string]$stepActivation.Reason)", p4_act_blk)
        self.assertIn("$okDetail = Protect-WisLicenseLogText", p4_act_blk)
        self.assertIn("$failDetail = Protect-WisLicenseLogText", p4_act_blk)

        # 3. Zakomentarisani [Licensing] blok u oba Wifi-Servis.example.ini fajla (bez stvarnih kljuceva)
        self.assertEqual(ini_root, ini_iso, "Root i ISO Wifi-Servis.example.ini moraju biti identicni")
        for ini_txt in (ini_root, ini_iso):
            self.assertIn("ShopName=IG-Systems", ini_txt)
            self.assertIn("; [Licensing]", ini_txt)
            self.assertIn("; OfficeActivationMode=Auto", ini_txt)
            self.assertIn("; OfficeKmsHost=", ini_txt)
            self.assertIn("; OfficeKmsPort=1688", ini_txt)
            self.assertIn("; OfficeProductKey=", ini_txt)
            self.assertNotRegex(ini_txt, re.compile(r"(?m)^\[Licensing\]"))
            self.assertNotRegex(ini_txt, re.compile(r"\b(?:[A-Z0-9]{5}-){4}[A-Z0-9]{5}\b", re.IGNORECASE))

        # 4. LIC-06 & ciscenje Cache\Licensing (WIS.Apps, WIS.Cleanup, Phase6-Finalize, TelemetryService.cs)
        write_lic_blk = apps_src.split("function Write-WisLicenseStatusFile", 1)[1].split("function Test-BatteryPresent", 1)[0]
        self.assertIn("license-status.json", write_lic_blk)
        self.assertIn("System.Text.UTF8Encoding($false)", write_lic_blk)

        self.assertIn("function Get-WisLicenseStatusFromFile", cleanup_src)
        self.assertIn("license-status.json", cleanup_src)
        self.assertIn("STATUS SISTEMSKIH LICENCI", cleanup_src)
        self.assertIn("### [LICENCE] Status Licenci", cleanup_src)
        for field in ("windows_activated", "office_installed", "office_activated", "office_activation_mode", "office_license_method", "office_license_detail"):
            self.assertIn(field, cleanup_src)
        cleanup_cache_blk = cleanup_src.split("function Start-FinalCleanup", 1)[1].split("function Invoke-ServisCleanup", 1)[0]
        self.assertIn(r'(Join-Path $LocalSetupDir "Cache\Licensing")', cleanup_cache_blk)
        self.assertIn(r'"C:\setup\Cache\Licensing"', cleanup_cache_blk)

        self.assertIn("license-status.json", phase6_src)
        self.assertIn('$finalTelemetryData["windows_activated"]', phase6_src)
        self.assertIn('$finalTelemetryData["licenses"]', phase6_src)
        self.assertIn(r'(Join-Path $LocalSetupDir "Cache\Licensing")', phase6_src)
        self.assertIn(r'"C:\setup\Cache\Licensing"', phase6_src)

        for tok in (
            "EnrichLicenseTelemetryPayload(payload);",
            "MaskLicenseKeyText(",
            r'@"C:\setup\license-status.json"',
            r'@"C:\setup\servisni_karton.json"',
            '"windows_activated"',
            '"office_installed"',
            '"office_activated"',
            '"office_license_method"',
            '"office_license_detail"',
        ):
            self.assertIn(tok, tel_cs)

        # 5. Backlog B1 (velicina paketa + Send-IpcProgress heartbeat na 20s) i B3 (Get-WUConfigMode -> DriversOnly)
        inst_pkg_blk = self.update_src.split("function Install-LocalUpdatePackageFile", 1)[1].split("function Receive-UpdateFileFromUrl", 1)[0]
        self.assertIn("$pkgSizeLabel", inst_pkg_blk)
        self.assertIn("($elapsedSec - $lastIpcSec) -ge 20", inst_pkg_blk)
        self.assertIn("Send-IpcProgress -Progress $safeProg", inst_pkg_blk)
        self.assertIn("[Math]::Max(0, [Math]::Min(99, $parsedProg))", inst_pkg_blk)
        self.assertIn("($elapsedSec - $lastLogSec) -ge 60", inst_pkg_blk)

        wu_mode_blk = self.update_src.split("function Get-WUConfigMode", 1)[1].split("function Test-IsDriverUpdate", 1)[0]
        self.assertIn('return "DriversOnly"', wu_mode_blk)
        self.assertNotIn('return "All"', wu_mode_blk)

        # 6. Ocuvanje zabrana iz test_06 / test_14 u WIS.Apps.psm1 i WIS.Core.psm1
        forbidden_patterns = (
            r"\bInvoke-RestMethod\b",
            r"\bInvoke-WebRequest\b",
            r"\bInvoke-Expression\b",
            r"\birm\b",
            r"\biex\b",
            r"\b" + "mas_aio" + r"\b",
            r"\bo" + "hook\b",
            r"\bhw" + "id\b",
            r"activated\." + "win",
            r"\bkms" + "38\b",
            r"\bts" + "forge\b",
            r"\b(?:[A-Z0-9]{5}-){4}[A-Z0-9]{5}\b",
        )
        for src_name, src_body in (("WIS.Apps.psm1", apps_src), ("WIS.Core.psm1", core_src)):
            for pat in forbidden_patterns:
                self.assertNotRegex(src_body, re.compile(pat, re.IGNORECASE), f"Zabranjen obrazac {pat} pronađen u {src_name}")

    def test_20_v342_imp34_bug01_location_and_lfsvc_not_blocked_in_phase3(self):
        """IMP-34 / BUG-01: WIS.Branding.psm1 ne sme postavljati DisableLocation=1 niti gasiti lfsvc pre Faze 5."""
        branding_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_text(encoding="utf-8")
        tel_cfg_blk = branding_src.split("$Global:WisTelemetryConfig = @{", 1)[1].split("function Disable-LocationPrivacyPrompts", 1)[0]
        self.assertNotIn("DisableLocation", tel_cfg_blk)
        self.assertNotIn('"lfsvc"', tel_cfg_blk)
        self.assertIn('"DiagTrack"', tel_cfg_blk)
        self.assertIn('"dmwappushservice"', tel_cfg_blk)
        self.assertIn("ShowGlobalPrompts", tel_cfg_blk)

        brand_fn = branding_src.split("function Invoke-SystemOptimizationAndBranding", 1)[1].split("function Set-OptimalDisplayScaling", 1)[0]
        self.assertNotIn("DisableLocation", brand_fn)
        self.assertNotIn("lfsvc", brand_fn)
        self.assertIn("Disable-LocationPrivacyPrompts", brand_fn)
        self.assertIn("AllowTelemetry", brand_fn)
        self.assertIn("DiagTrack", brand_fn)
        self.assertIn("dmwappushservice", brand_fn)
        self.assertIn("AdvertisingInfo", brand_fn)
        self.assertIn("BingSearchEnabled", brand_fn)
        self.assertIn("CortanaConsent", brand_fn)

    def test_21_v342_imp21_imp29_imp28_imp33_update_and_core_heartbeat(self):
        """IMP-21, IMP-29, IMP-28, IMP-33 i IMP-22 ugovori u WIS.Common, WIS.Update, WIS.Core, WIS.Cleanup i Phase6."""
        common_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Common.psm1").read_text(encoding="utf-8")
        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")

        # IMP-21: ValidateSet sadrzi driver_pool i izolovan try/catch oko Add-WisBenchmarkTransfer
        bench_fn = common_src.split("function Add-WisBenchmarkTransfer", 1)[1].split("function Record-WisStepBenchmark", 1)[0]
        self.assertIn('[ValidateSet("driver", "update", "model_pack", "oem_pack", "driver_pool")]', bench_fn)
        pool_fn = self.update_src.split("function Invoke-DriverPoolBundleInstall", 1)[1].split("function Invoke-ModelDriversPreInstall", 1)[0]
        self.assertIn("try {", pool_fn.split("Add-WisBenchmarkTransfer", 1)[0].splitlines()[-1] + "\n" + pool_fn.split("Add-WisBenchmarkTransfer", 1)[0])
        self.assertIn('-Level "WARN" -Component "NAS"', pool_fn)

        # IMP-29: fail-fast /api/drivers/pool/bundle Timeout=15000 + ReadWriteTimeout=300000 & 4 MiB FileStream <Model>.tar kopiranje
        self.assertIn("$req.Timeout = 15000", pool_fn)
        self.assertIn("$req.ReadWriteTimeout = 300000", pool_fn)
        pre_fn = self.update_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]
        self.assertNotIn("Copy-Item -Path $tarArchive -Destination $localTar", pre_fn)
        self.assertIn("New-Object byte[] 4194304", pre_fn)
        self.assertIn("[System.IO.File]::OpenRead($tarArchive)", pre_fn)
        self.assertIn("[System.IO.File]::Create($localTar)", pre_fn)
        self.assertIn("-Phase 2", pre_fn)
        self.assertIn("Mbps", pre_fn)
        self.assertIn("Remove-Item -LiteralPath $localTar", pre_fn)

        # IMP-28: prepoznavanje -2145116147 (0x8024200d / WU_E_UH_NEEDANOTHERDOWNLOAD)
        inst_pkg = self.update_src.split("function Install-LocalUpdatePackageFile", 1)[1].split("function Receive-UpdateFileFromUrl", 1)[0]
        self.assertIn("-2145116147", inst_pkg)
        self.assertIn("0x8024200d", inst_pkg)
        self.assertIn("WU_E_UH_NEEDANOTHERDOWNLOAD", inst_pkg)
        self.assertIn("SSU", inst_pkg)

        # IMP-33: Wait-WindowsUpdateCompletionBarrier salje -Phase 6 i ne duplira se u Invoke-Phase6Finalize
        barrier_blk = self.update_src.split("function Wait-WindowsUpdateCompletionBarrier", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertNotIn("-Phase 5", barrier_blk)
        self.assertIn('-Phase 6 -PhaseName "Završetak"', barrier_blk)
        self.assertIn("Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 45", phase6_src)
        self.assertIn("$Global:WisWuBarrierCompletedInPhase6 = $true", phase6_src)
        p6_fin_fn = cleanup_src.split("function Invoke-Phase6Finalize", 1)[1].split("function Invoke-Phase5Finalize", 1)[0]
        self.assertIn("WisWuBarrierCompletedInPhase6", p6_fin_fn)

        # IMP-22: Sync-ProgramiFromNas asinhroni WaitForExit(15000) sa Send-IpcProgress i Write-ServisLog
        sync_prog_fn = core_src.split("function Sync-ProgramiFromNas", 1)[1].split("function Format-WisCoreProcessArg", 1)[0]
        self.assertNotIn("-Wait -PassThru", sync_prog_fn)
        self.assertIn("WaitForExit(15000)", sync_prog_fn)
        self.assertIn("Send-IpcProgress", sync_prog_fn)
        self.assertIn("Write-ServisLog", sync_prog_fn)
        self.assertIn("$proc.ExitCode", sync_prog_fn)

    def test_22_v342_imp24_imp30_apps_retry_and_preserve_phase(self):
        """IMP-24 i IMP-30: License single-app retry WARN/exit 0 i PreserveCurrentPhase u Phase4-Apps / WIS.Apps."""
        phase4_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase4-Apps.ps1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")

        self.assertIn("[switch]$PreserveCurrentPhase", phase4_src)
        self.assertIn("-PreserveCurrentPhase:$PreserveCurrentPhase", phase4_src)

        inv_p4 = apps_src.split("function Invoke-Phase4Apps", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("[switch]$PreserveCurrentPhase", inv_p4)
        self.assertIn("$preservePhase = [bool]($PreserveCurrentPhase -or $isSingleAppRetry)", inv_p4)
        self.assertNotRegex(inv_p4, re.compile(r"if\s*\(-not\s*\$isSingleAppRetry\b[^\r\n]*Send-IpcProgress"))

        retry_blk = inv_p4.split("# Režim pojedinačnog Retry-a", 1)[1].split("# Uklanjanje flag datoteke", 1)[0]
        self.assertIn("$isLicenseBestEffortWarn", retry_blk)
        self.assertIn('Send-IpcAppUpdate -AppId "License" -Status "WARN"', retry_blk)
        self.assertIn("$global:LASTEXITCODE = 0", retry_blk)
        self.assertNotIn("Send-IpcProgress", retry_blk)

    def test_23_v342_imp32_imp23_phase6_antileak_order_and_telemetry_retry(self):
        """IMP-32 i IMP-23: Clear-ServisWifiSecrets u finally posle izvestaja, 10s/3x retry, initial_start_time i 1x COMPLETED."""
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        cleanup_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Cleanup.psm1").read_text(encoding="utf-8")
        tel_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Telemetry.psm1").read_text(encoding="utf-8")

        # IMP-32: u Phase6-Finalize.ps1 slanje izvestaja dolazi pre Clear-ServisWifiSecrets i Clear-ServisWifiSecrets je u finally
        idx_invoke_p6 = phase6_src.find("Invoke-Phase6Finalize -LocalSetupDir")
        idx_send_rep = phase6_src.find("Send-FleetFinalReport")
        idx_finally = phase6_src.find("} finally {")
        idx_clear_wifi = phase6_src.find("Clear-ServisWifiSecrets -LocalSetupDir")
        self.assertGreater(idx_invoke_p6, 0)
        self.assertGreater(idx_send_rep, 0)
        self.assertGreater(idx_finally, idx_invoke_p6)
        self.assertGreater(idx_clear_wifi, idx_finally, "Clear-ServisWifiSecrets mora biti iskljucivo u finally bloku posle slanja izvestaja")
        self.assertIn("WIS_FORCE_ANTILEAK_FAIL", phase6_src)
        self.assertIn("exit 2", phase6_src)
        self.assertNotIn("DeleteSetupDir = $true", phase6_src)

        # IMP-23 jednokratno slanje i duration_seconds u Send-FleetFinalReport
        send_fn = tel_src.split("function Send-FleetFinalReport", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("WisFinalReportSentStatus", send_fn)
        self.assertIn("$req.Timeout = 10000", send_fn)
        self.assertIn("$req.ReadWriteTimeout = 10000", send_fn)
        self.assertIn("X-ServisHub-Client-Token", send_fn)
        self.assertIn("@(0, 1, 2)", send_fn)
        idx_init_time = send_fn.find("stObj.initial_start_time")
        idx_bench_dur = send_fn.find("benchSummary.total_phase_duration_sec")
        idx_script_dur = send_fn.find("Global:ScriptStartTime")
        self.assertGreater(idx_init_time, 0)
        self.assertGreater(idx_bench_dur, idx_init_time)
        self.assertGreater(idx_script_dur, idx_bench_dur)

        # Fallback HTTP u WIS.Cleanup.psm1 (10s timeout, 3x retry, token, WisFinalReportSentStatus)
        p6_fin_fn = cleanup_src.split("function Invoke-Phase6Finalize", 1)[1].split("function Invoke-Phase5Finalize", 1)[0]
        self.assertIn("$req.Timeout = 10000", p6_fin_fn)
        self.assertIn("$req.ReadWriteTimeout = 10000", p6_fin_fn)
        self.assertIn("X-ServisHub-Client-Token", p6_fin_fn)
        self.assertIn("$retryDelaysClean = @(0, 1, 2)", p6_fin_fn)
        self.assertIn('$Global:WisFinalReportSentStatus = "COMPLETED"', p6_fin_fn)
        self.assertIn('$Global:WisFinalReportSentStatus -ne "COMPLETED"', phase6_src)

    def test_24_v343_01_location_allow_and_sync_parity(self):
        """V343-01: Location ConsentStore=Allow (HKLM/HKCU/HKU/DefaultUser), uklonjen DisableLocation=1, lfsvc aktivan i paritet WIS.Branding/SetupComplete."""
        setup_canon = (ROOT / "ISO" / "SetupComplete.ps1").read_bytes()
        setup_skripte = (ROOT / "ISO" / "Skripte" / "SetupComplete.ps1").read_bytes()
        setup_media = (ROOT / "ISO" / "media" / "oem" / "Setup" / "Scripts" / "SetupComplete.ps1").read_bytes()
        self.assertEqual(setup_canon, setup_skripte, "ISO/Skripte/SetupComplete.ps1 mora biti identičan sa ISO/SetupComplete.ps1")
        self.assertEqual(setup_canon, setup_media, "ISO/media/oem/.../SetupComplete.ps1 mora biti identičan sa ISO/SetupComplete.ps1")

        setup_txt = setup_canon.decode("utf-8")
        self.assertIn(r"ConsentStore\location", setup_txt)
        self.assertIn("NonPackaged", setup_txt)
        self.assertIn("'Allow'", setup_txt)
        self.assertIn("ShowGlobalPrompts", setup_txt)
        self.assertIn("DisableLocation", setup_txt)
        self.assertIn("lfsvc", setup_txt)

        branding_ui = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_bytes()
        branding_oem = (ROOT / "ISO" / "sources" / "$OEM$" / "$1" / "setup" / "Skripte" / "Modules" / "WIS.Branding.psm1").read_bytes()
        self.assertEqual(branding_ui, branding_oem, "ISO $OEM$ WIS.Branding.psm1 mora biti 100% identičan sa UI/Skripte/Modules/WIS.Branding.psm1")

        branding_src = branding_ui.decode("utf-8-sig")
        loc_fn = branding_src.split("function Disable-LocationPrivacyPrompts", 1)[1].split("function Set-OptimalDisplayScaling", 1)[0]
        self.assertIn('"Allow"', loc_fn)
        self.assertIn("NonPackaged", loc_fn)
        self.assertIn("ShowGlobalPrompts", loc_fn)
        self.assertIn("DisableLocation", loc_fn)
        self.assertIn("lfsvc", loc_fn)
        self.assertIn("function Test-WisLocationAccessEnabled", loc_fn)

        p3_tel = branding_src.split('-StepId "P3.Telemetry"', 1)[1].split('-StepId "P3.Oem"', 1)[0]
        self.assertIn("Test-WisLocationAccessEnabled", p3_tel)
        self.assertIn("Disable-LocationPrivacyPrompts", p3_tel)

        export_blk = branding_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Test-WisLocationAccessEnabled", export_blk)

        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        self.assertNotIn('ConsentStore\\location', phase6_src)

    def test_25_v343_02_windows_licensing_diagnostics_and_activation_flow(self):
        """V343-02: Get-WindowsLicenseStatus, Windows [Licensing] parametri, slmgr /ato bez OA3x i prošireni license-status.json."""
        core_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Core.psm1").read_text(encoding="utf-8")
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        ini_root = (ROOT / "Wifi-Servis.example.ini").read_text(encoding="utf-8")
        ini_iso = (ROOT / "ISO" / "Wifi-Servis.example.ini").read_text(encoding="utf-8")

        self.assertEqual(ini_root, ini_iso)
        for ini_txt in (ini_root, ini_iso):
            for tok in ("; WindowsActivationMode=Auto", "; WindowsKmsHost=", "; WindowsKmsPort=1688", "; WindowsProductKey="):
                self.assertIn(tok, ini_txt)

        core_lic_blk = core_src.split("function Get-ServisLicensingSettings", 1)[1].split("function Test-NasHostOnline", 1)[0]
        for tok in ("WindowsActivationMode", "WindowsKmsHost", "WindowsKmsPort", "WindowsProductKey"):
            self.assertIn(tok, core_lic_blk)

        local_lic_blk = apps_src.split("function Get-WisLicensingSettingsLocal", 1)[1].split("function Write-WisLicenseStatusFile", 1)[0]
        for tok in ("WindowsActivationMode", "WindowsKmsHost", "WindowsKmsPort", "WindowsProductKey"):
            self.assertIn(tok, local_lic_blk)

        self.assertIn("function Get-WindowsLicenseStatus", apps_src)
        win_stat_blk = apps_src.split("function Get-WindowsLicenseStatus", 1)[1].split("function Test-WindowsActivated", 1)[0]
        self.assertIn("55c92734-d682-4d71-98e3-d6ec3f16059f", win_stat_blk)
        self.assertIn("SoftwareLicensingProduct", win_stat_blk)
        self.assertIn("LicenseStatusReason", win_stat_blk)
        self.assertIn("Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @(\"/xpr\") -TimeoutSec 25", win_stat_blk)

        test_win_blk = apps_src.split("function Test-WindowsActivated", 1)[1].split("function Test-OfficeInstalled", 1)[0]
        self.assertIn("Get-WindowsLicenseStatus", test_win_blk)
        self.assertNotIn("cscript //nologo", test_win_blk)

        act_blk = apps_src.split("function Invoke-WindowsOfficeActivation", 1)[1].split("function Invoke-AppsPhase", 1)[0]
        self.assertIn("OA3xOriginalProductKeyDescription", act_blk)
        self.assertIn("$winEditionMismatch", act_blk)
        self.assertIn("Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @(\"/ato\") -TimeoutSec 25", act_blk)
        self.assertIn("WindowsKmsHost", act_blk)
        self.assertIn("WindowsProductKey", act_blk)
        self.assertIn("$winNeedsHook", act_blk)
        self.assertIn("windows_oa3_present", act_blk)
        self.assertIn("windows_edition", act_blk)
        self.assertIn("windows_license_method", act_blk)
        self.assertIn("windows_license_detail", act_blk)

        write_lic_blk = apps_src.split("function Write-WisLicenseStatusFile", 1)[1].split("function Test-BatteryPresent", 1)[0]
        for field in ("windows_oa3_present", "windows_edition", "windows_license_method", "windows_license_detail"):
            self.assertIn(field, write_lic_blk)
        self.assertIn("Protect-WisLicenseLogText -Text $WindowsLicenseDetail", write_lic_blk)

        export_blk = apps_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Get-WindowsLicenseStatus", export_blk)

        for field in ("windows_oa3_present", "windows_edition", "windows_license_method", "windows_license_detail"):
            self.assertIn(field, phase6_src)

    def test_26_v343_03_phase6_hwtest_hard_gate(self):
        """V343-03: Phase6-Finalize.ps1 blokira DONE/COMPLETED kada hwtest-result.json ima preskočene/nezavršene interaktivne testove."""
        phase6_src = (ROOT / "UI" / "Skripte" / "Phases" / "Phase6-Finalize.ps1").read_text(encoding="utf-8")
        self.assertIn("hwtest-result.json", phase6_src)
        self.assertIn("$unfinishedInteractiveTests", phase6_src)
        self.assertIn('"SKIP", "PENDING"', phase6_src)
        self.assertIn("$hwTestIncomplete = $true", phase6_src)
        self.assertIn("$blockHandover = $true", phase6_src)
        self.assertIn("Send-FleetFinalReport -Status \"NEEDS_ATTENTION\"", phase6_src)
        idx_finally = phase6_src.find("} finally {")
        idx_clear = phase6_src.find("Clear-ServisWifiSecrets -LocalSetupDir")
        self.assertGreater(idx_clear, idx_finally)

    def test_27_v343_04_hardware_backlit_kb_no_false_positives(self):
        """V343-04: WIS.Hardware.psm1 uklanja lažne PnP/WMI/BIOS detektore i daje prioritet hwtest-result.json (backlight_passed)."""
        hw_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Hardware.psm1").read_text(encoding="utf-8")
        diag_blk = hw_src.split("function Get-HardwareDiagnosticsInfo", 1)[1].split("function Test-BatteryPresent", 1)[0]
        for forbidden_tok in (
            "LEN0071",
            "DELLABCE",
            "Lenovo_SetKeyboardBacklightStatus",
            "HP_BIOSSetting",
            "DCIM_BiosEnumeration",
        ):
            self.assertNotIn(forbidden_tok, diag_blk, f"Lažni detektor {forbidden_tok} ne sme ostati u WIS.Hardware.psm1")

        self.assertIn("hwtest-result.json", diag_blk)
        self.assertIn("backlight_passed", diag_blk)
        self.assertIn('"no_hardware"', diag_blk)
        self.assertIn('"Da (Pozadinsko osvetljenje)"', diag_blk)
        self.assertIn('"Standardna (Bez osvetljenja)"', diag_blk)
        self.assertIn("Nije potvrđeno (provera u Fazi 5)", diag_blk)

    def test_28_v343_05_07_models_and_peripherals_contract(self):
        """V343-05 (Models.cs) i V343-07 (Models.cs + MainWindow.Hardware.cs): sklopljene faze po difoltu, bez Web kamera/TB bedževa, BT verzija, 4G/5G i eSIM."""
        models_src = (ROOT / "UI" / "Models.cs").read_text(encoding="utf-8")
        hw_cs_src = (ROOT / "UI" / "MainWindow.Hardware.cs").read_text(encoding="utf-8")

        # V343-05 u Models.cs
        self.assertIn("private bool _showStepsInOverview = false;", models_src)
        self.assertIn("private bool _isPhasesExpanded = false;", models_src)
        self.assertIn("Prikaži korake", models_src)
        self.assertIn("Sakrij korake", models_src)

        # V343-07 u RebuildOverviewPeripherals
        rebuild_blk = models_src.split("public void RebuildOverviewPeripherals(", 1)[1].split("private void AddPeripheralBadge(", 1)[0]
        self.assertNotIn("Web kamera", rebuild_blk)
        self.assertNotIn("Thunderbolt / USB4", rebuild_blk)
        self.assertIn("IR kamera (Hello)", rebuild_blk)
        self.assertIn('AddPeripheralBadge("eSIM"', rebuild_blk)
        self.assertIn("_bluetoothVersionLabel", rebuild_blk)
        self.assertIn("_wwanInfo", rebuild_blk)

        # V343-07 u MainWindow.Hardware.cs
        self.assertIn("public void RefreshPeripheralsAfterDrivers()", hw_cs_src)
        self.assertIn("A92F26CA-EDA7-4B1D-9DB2-27B68AA5A2EB", hw_cs_src)
        self.assertIn("private static void DetectEsimSupport(", hw_cs_src)
        self.assertIn("mbn show interfaces", hw_cs_src)
        self.assertIn("WwanSvc", hw_cs_src)

    def test_29_v343_02_licensing_antileak_no_plaintext_kms_or_raw_output(self):
        """V343-02 Anti-Leak: WIS.Apps.psm1 ne sme logovati plain-text KMS hostove niti nezaštićene slmgr/ospp izlaze."""
        apps_src = (ROOT / "UI" / "Skripte" / "Modules" / "WIS.Apps.psm1").read_text(encoding="utf-8")
        protect_blk = apps_src.split("function Protect-WisLicenseLogText", 1)[1].split("function Format-Win32CommandLineArgument", 1)[0]
        self.assertIn("[string[]]$SensitiveHosts = @()", protect_blk)
        self.assertIn("***KMS_HOST_MASKIRANO***", protect_blk)

        act_blk = apps_src.split("function Invoke-WindowsOfficeActivation", 1)[1].split("function Invoke-AppsPhase", 1)[0]
        self.assertNotIn("($skmsTarget)", act_blk)
        self.assertNotIn("(${officeKmsHost}:$officeKmsPort)", act_blk)
        self.assertNotIn("${winKmsHost}:$winKmsPort", act_blk)
        self.assertIn("$shMsg = Protect-WisLicenseLogText", act_blk)
        self.assertIn("$spMsg = Protect-WisLicenseLogText", act_blk)
        self.assertIn("$inpMsg = Protect-WisLicenseLogText", act_blk)
        self.assertIn("$cleanAct = Protect-WisLicenseLogText", act_blk)


if __name__ == "__main__":
    unittest.main()
