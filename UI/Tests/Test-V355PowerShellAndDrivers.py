#!/usr/bin/env python3
"""
Test-V355PowerShellAndDrivers.py
================================
Sveobuhvatna verifikacija WIS v3.5.5 PowerShell 5.1 modula za WIS-TASK-279:
- IMP-113: Need-Only instalacija drajvera, zastita oem*.inf, cist aktivni Delta Harvest, blokada Firmware/CFU
- IMP-98:  Rani P2 OEM prefetch poziv ka ServiceHub-u (uklj. Surface Laptop 6/7 SystemSKU)
- IMP-105: SUBSYS-bezbedno poklapanje u Test-WisDeviceCoveredByStagedHub i HWID WUA filtriranje bez CoveredClasses
- IMP-111: Retry do 3 uzastopne prolazne HTTP greske u Invoke-HubBatchDriverResolveAndStage
- IMP-112: Golden Preinstalled ISO Opcija B (image-profile.json, delta NAS sync -SubFolders, 0 MB skip, run_meta)
- IMP-75:  Kanonski prioritet 'Servis' ispred default 'Programi' deljenja
- IMP-100: Strogo monotoni brojac preuzetih MB u Sync-ProgramiFromNas
- IMP-104: Desktop batterybar=SKIPPED i realna detekcija laptopa u fallback bloku Export-ServisSummaryReport
- IMP-109: Get-WisPreviousHostname nikad ne upisuje TK-* u initial-hostname.txt
- IMP-114: Kanonski autopilot objekat u servisni_karton.json i payload-u za ServiceHub bez laznog zelenog
- Bezbednost i stroga Windows PowerShell 5.1 kompatibilnost.
"""

import pathlib
import re
import subprocess
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
MOD_DIR = ROOT / "UI" / "Skripte" / "Modules"

MOD_UPDATE = MOD_DIR / "WIS.Update.psm1"
MOD_APPS = MOD_DIR / "WIS.Apps.psm1"
MOD_CORE = MOD_DIR / "WIS.Core.psm1"
MOD_CLEANUP = MOD_DIR / "WIS.Cleanup.psm1"
MOD_TELEMETRY = MOD_DIR / "WIS.Telemetry.psm1"

OWNED_MODULES = [
    MOD_UPDATE,
    MOD_APPS,
    MOD_CORE,
    MOD_CLEANUP,
    MOD_TELEMETRY,
]


def _strip_ps_comments_and_strings(text: str) -> str:
    """Grubo uklanja linijske komentare radi detekcije PS 7+ operatora u kodu."""
    out = []
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("#"):
            continue
        out.append(line)
    return "\n".join(out)


class TestV355PowerShellAndDrivers(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.upd_src = MOD_UPDATE.read_text(encoding="utf-8-sig")
        cls.apps_src = MOD_APPS.read_text(encoding="utf-8-sig")
        cls.core_src = MOD_CORE.read_text(encoding="utf-8-sig")
        cls.clean_src = MOD_CLEANUP.read_text(encoding="utf-8-sig")
        cls.tel_src = MOD_TELEMETRY.read_text(encoding="utf-8-sig")

    def test_01_ps51_strict_compatibility_bom_and_syntax_tools(self):
        """Svih 5 izmenjenih PS modula moraju imati UTF-8 BOM, 0 PS 7+ operatora i proci sintaksne provere."""
        for p in OWNED_MODULES:
            raw = p.read_bytes()
            self.assertTrue(raw.startswith(b"\xef\xbb\xbf"), f"Nedostaje UTF-8 BOM u {p.name}")
            code = _strip_ps_comments_and_strings(raw.decode("utf-8-sig"))

            # Zabranjeni PS 7+ operatori: ??, ??=, -Parallel, -SkipHttpErrorCheck
            self.assertNotRegex(code, r"\?\?=?|\s-Parallel\b|\s-SkipHttpErrorCheck\b", f"Pronadjen PS 7+ operator u {p.name}")
            # Zabranjen ternarni operator '? ... :' van regexa i URL query parametara
            for idx, ln in enumerate(code.splitlines(), start=1):
                # Izbegni regex obrasce (?i), (?mi), (?:...) i URL parametre ?model=
                clean_ln = re.sub(r'"[^"]*"|\'[^\']*\'', '""', ln)
                self.assertNotRegex(
                    clean_ln,
                    r"\)\s*\?\s+[^:]+\s+:\s+",
                    f"Potencijalni PS 7+ ternarni operator (?:) na liniji {idx} u {p.name}: {ln}",
                )
            # Stroga bezbednosna zabrana: nema Invoke-Expression / iex
            self.assertNotRegex(code, r"(?i)\bInvoke-Expression\b|\biex\s+", f"Zabranjen Invoke-Expression u {p.name}")

        # Pokreni zvanicne skripte sintaksne i hashtable kontrole
        r1 = subprocess.run([sys.executable, str(ROOT / "ISO" / "Skripte" / "Verify-PowerShellSyntax.py")], capture_output=True, text=True)
        self.assertEqual(r1.returncode, 0, f"Verify-PowerShellSyntax.py pao:\n{r1.stdout}\n{r1.stderr}")

        r2 = subprocess.run([sys.executable, str(ROOT / "ISO" / "Skripte" / "Check-DuplicateHashtableKeys.py")], capture_output=True, text=True)
        self.assertEqual(r2.returncode, 0, f"Check-DuplicateHashtableKeys.py pao:\n{r2.stdout}\n{r2.stderr}")

    def test_02_imp113_get_devices_needing_oem_drivers_and_golden_rule(self):
        """IMP-113: Get-WisDevicesNeedingOemDrivers stiti Code==0 + oem*.inf, hvata greske i genericke inbox klase."""
        self.assertIn("function Get-WisDevicesNeedingOemDrivers", self.upd_src)
        blk = self.upd_src.split("function Get-WisDevicesNeedingOemDrivers", 1)[1].split("function Invoke-WisEarlyOemPrefetch", 1)[0]

        # Ignorisane klase i virtuelni prefiksi
        self.assertIn("^(?i)(ROOT|SWD)\\\\", blk)
        for cls_name in ("'Printer'", "'PrintQueue'", "'SoftwareDevice'"):
            self.assertIn(cls_name, blk)

        # Zlatno pravilo zastite ispravnog oem*.inf drajvera
        self.assertIn("^oem\\d+\\.inf$", blk)
        self.assertIn("if ($errCode -eq 0 -and $hasActiveOemInf)", blk)
        self.assertIn("continue", blk)

        # (a) PnP greske van @(0, 14, 22, 45)
        self.assertIn("$errCode -notin @(0, 14, 22, 45)", blk)

        # (b) Kljucne klase na generickom Microsoft inbox drajveru
        for tok in (
            '"Display"',
            '"MEDIA"',
            '"Net"',
            '"Bluetooth"',
            '"Biometric"',
            '"Camera"',
            '"Image"',
            '"HIDClass"',
            '"Mouse"',
            "Touchpad",
            "SerialHub",
            "I2C",
            "SPI",
            "SST",
        ):
            self.assertIn(tok, blk, f"Nedostaje kljucna klasa/obrazac {tok} u Get-WisDevicesNeedingOemDrivers")

        # Eksportovana iz modula
        export_blk = self.upd_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Get-WisDevicesNeedingOemDrivers", export_blk)

    def test_03_imp113_need_only_preinstall_zero_transfer_and_cascade_pass2(self):
        """IMP-113: Invoke-ModelDriversPreInstall preskace transfer kad je 0 uredjaja, a salje -MissingHwids + 2. prolaz posle 3s."""
        pre_blk = self.upd_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]

        # 0 uredjaja -> 0 MB transfera, 0 pnputil poziva
        self.assertIn("$neededDevices = @(Get-WisDevicesNeedingOemDrivers)", pre_blk)
        self.assertIn("if ($neededDevices.Count -eq 0)", pre_blk)
        self.assertIn("0 MB transfera, 0 pnputil poziva", pre_blk)
        self.assertIn("SKIPPED_CLEAN", pre_blk)

        # Ciljani -MissingHwids umesto -AllHwids
        self.assertIn("-MissingHwids $missingHwidsPass1", pre_blk)
        self.assertNotIn("-AllHwids $allPoolHwids", pre_blk)

        # 2. prolaz za kaskadne child uredjaje posle 3s pauze
        self.assertIn("Start-Sleep -Seconds 3", pre_blk)
        self.assertIn("$neededPass2 = @(Get-WisDevicesNeedingOemDrivers)", pre_blk)
        self.assertIn("-MissingHwids $newChildHwids", pre_blk)

        # SMB/HTTP fallback takodje prosledjuje -TargetHwids da ne gazi postojece drajvere
        self.assertIn('-SourceLabel "NAS SMB" -TargetHwids $missingHwidsPass1', pre_blk)
        self.assertIn('-SourceLabel "ServiceHub HTTP" -TargetHwids $missingHwidsPass1', pre_blk)

    def test_04_imp113_firmware_cfu_blocklist_and_active_delta_harvest(self):
        """IMP-113: Blokada Class=Firmware/CFU u berbi i instalaciji + cist aktivni Delta Harvest sa os_build."""
        fw_blk = self.upd_src.split("function Test-WisFirmwareOrCfuInf", 1)[1].split("function Invoke-SafePnpUtilAddDriver", 1)[0]
        for pat in (
            "Firmware",
            "SurfaceUEFI",
            "SurfaceSAM",
            "SurfaceME",
            "SurfaceTouchPadProcessorUpdate",
            "SurfaceCFUOverHid",
            "SurfaceStorageFwUpdate",
            "FwUpdate",
            "f2e7dd72-6468-4e36-b6f1-6488f42c1b52",
        ):
            self.assertIn(pat, fw_blk, f"Firmware/CFU obrazac {pat} mora biti blokiran u Test-WisFirmwareOrCfuInf")

        # Defense-in-depth u Invoke-SafePnpUtilAddDriver i Install-DriverTarArchive
        pnp_blk = self.upd_src.split("function Invoke-SafePnpUtilAddDriver", 1)[1].split("function Test-IsDriverUpdate", 1)[0]
        self.assertIn("Test-WisFirmwareOrCfuInf", pnp_blk)
        self.assertIn("[FIRMWARE_BLOCK]", pnp_blk)

        tar_blk = self.upd_src.split("function Install-DriverTarArchive", 1)[1].split("function Get-WisDevicesNeedingOemDrivers", 1)[0]
        self.assertIn("Test-WisFirmwareOrCfuInf", tar_blk)
        self.assertIn("[string[]]$TargetHwids = @()", tar_blk)

        # Aktivni OEM paketi + DCH Extension/SoftwareComponent
        act_blk = self.upd_src.split("function Get-WisActiveOemDriverPackages", 1)[1].split("function Export-ServisHardwareDrivers", 1)[0]
        self.assertIn("Win32_PnPSignedDriver", act_blk)
        self.assertIn("^oem\\d+\\.inf$", act_blk)
        self.assertIn("Extension", act_blk)
        self.assertIn("SoftwareComponent", act_blk)
        self.assertIn("Test-WisFirmwareOrCfuInf", act_blk)
        self.assertIn("os_build", act_blk)

        # Export-ServisHardwareDrivers bez celog dism DriverStore eksporta
        exp_blk = self.upd_src.split("function Export-ServisHardwareDrivers", 1)[1].split("function Export-ServisNetworkDrivers", 1)[0]
        self.assertNotIn("/online /export-driver", exp_blk)
        self.assertIn("Get-WisActiveOemDriverPackages", exp_blk)
        self.assertIn('pnputil.exe /export-driver "$oemInfName"', exp_blk)
        self.assertIn("Test-WisFirmwareOrCfuInf", exp_blk)
        self.assertIn("-OsBuild $currHarvestBuild", exp_blk)

        # Invoke-DeltaDriverHarvest: os_build u delta-check, validacija ^oem\d+\.inf$, blokada Firmware/CFU
        delta_blk = self.upd_src.split("function Invoke-DeltaDriverHarvest", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("Get-WisActiveOemDriverPackages", delta_blk)
        self.assertIn("os_build = $currBuildDelta", delta_blk)
        self.assertIn("^oem\\d+\\.inf$", delta_blk)
        self.assertIn("Test-WisFirmwareOrCfuInf", delta_blk)
        self.assertIn("-OsBuild $currBuildDelta", delta_blk)

        export_blk = self.upd_src.split("Export-ModuleMember", 1)[1]
        for fn in ("Test-WisFirmwareOrCfuInf", "Get-WisActiveOemDriverPackages"):
            self.assertIn(fn, export_blk)

    def test_05_imp98_early_p2_oem_prefetch_and_system_sku(self):
        """IMP-98: Rani, vidljivo logovan P2 OEM prefetch poziv ka ServiceHub-u za podrzane modele (uklj. Surface 6/7)."""
        pref_blk = self.upd_src.split("function Invoke-WisEarlyOemPrefetch", 1)[1].split("function Invoke-DriverPoolBundleInstall", 1)[0]
        self.assertIn("/api/drivers/pool/bundle", pref_blk)
        self.assertIn("metadata_only = $true", pref_blk)
        self.assertIn("system_sku    = $SystemSku", pref_blk)
        self.assertIn("SystemSKUNumber", pref_blk)
        self.assertIn("Surface", pref_blk)
        self.assertIn("[OEM_PREFETCH]", pref_blk)

        # Poziva se u P2.Identify i u Invoke-ModelDriversPreInstall
        p2_blk = self.upd_src.split("function Invoke-Phase2Drivers", 1)[1].split("function Write-BackgroundWuStatus", 1)[0]
        self.assertIn("Invoke-WisEarlyOemPrefetch", p2_blk)
        pre_blk = self.upd_src.split("function Invoke-ModelDriversPreInstall", 1)[1].split("function Get-WisUpdateHubEndpoint", 1)[0]
        self.assertIn("Invoke-WisEarlyOemPrefetch", pre_blk)

        # Invoke-DriverPoolBundleInstall prosledjuje system_sku
        pool_blk = self.upd_src.split("function Invoke-DriverPoolBundleInstall", 1)[1].split("function Invoke-ModelDriversPreInstall", 1)[0]
        self.assertIn("[string]$SystemSku = \"\"", pool_blk)
        self.assertIn("system_sku    = $effectiveSku", pool_blk)

        export_blk = self.upd_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Invoke-WisEarlyOemPrefetch", export_blk)

    def test_06_imp105_subsys_safe_matching_and_hwid_wua_filtering(self):
        """IMP-105: StartsWith($venDev) fallback dozvoljen samo bez SUBSYS_ u .inf-u; bez CoveredClasses WUA odbacivanja."""
        cov_blk = self.upd_src.split("function Test-WisDeviceCoveredByStagedHub", 1)[1].split("function Test-WisWuaDriverCoveredByStagedHub", 1)[0]
        self.assertIn("&SUBSYS_[0-9A-F]{8}", cov_blk)
        self.assertIn("$infSubsys.Equals($devSubsys, [System.StringComparison]::OrdinalIgnoreCase)", cov_blk)

        wua_helper = self.upd_src.split("function Test-WisWuaDriverCoveredByStagedHub", 1)[1].split("function Invoke-HubBatchDriverResolveAndStage", 1)[0]
        self.assertIn("DriverHardwareID", wua_helper)
        self.assertIn("Test-WisDeviceCoveredByStagedHub", wua_helper)

        bg_blk = self.upd_src.split("function Invoke-BackgroundDriverDownloadWorker", 1)[1].split("function Start-BackgroundDriverDownloadWorker", 1)[0]
        self.assertNotIn("CoveredClasses.Contains", bg_blk)
        self.assertIn("Test-WisWuaDriverCoveredByStagedHub", bg_blk)

        post_blk = self.upd_src.split("function Invoke-PostRebootDriverFinalize", 1)[1].split("function Invoke-DeltaDriverHarvest", 1)[0]
        self.assertNotIn("CoveredClasses.Contains", post_blk)
        self.assertIn("Test-WisWuaDriverCoveredByStagedHub", post_blk)

        export_blk = self.upd_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Test-WisWuaDriverCoveredByStagedHub", export_blk)

    def test_07_imp111_batch_resolve_transient_http_error_retries(self):
        """IMP-111: Invoke-HubBatchDriverResolveAndStage dozvoljava do 3 uzastopne prolazne HTTP greske pre break-a."""
        batch_blk = self.upd_src.split("function Invoke-HubBatchDriverResolveAndStage", 1)[1].split("function Invoke-BackgroundDriverDownloadWorker", 1)[0]
        self.assertIn("$consecutiveHttpErrors = 0", batch_blk)
        self.assertIn("$maxConsecutiveHttpErrors = 3", batch_blk)
        self.assertIn("$consecutiveHttpErrors++", batch_blk)
        self.assertIn("if ($consecutiveHttpErrors -lt $maxConsecutiveHttpErrors)", batch_blk)
        self.assertIn("Start-Sleep -Seconds $PollIntervalSeconds", batch_blk)
        self.assertIn("continue", batch_blk)
        self.assertIn("$pollSw.Elapsed.TotalSeconds -ge $PollBudgetSeconds", batch_blk)
        self.assertIn("Test-ServisSkipRequested", batch_blk)

    def test_08_imp112_golden_preinstalled_iso_delta_sync_and_telemetry(self):
        """IMP-112: Golden Preinstalled ISO Opcija B (image-profile.json, delta -SubFolders, 0 MB skip, Shortcuts/PdfDefault/Activation, run_meta)."""
        # 1. Get-WisImageProfile u WIS.Core i WIS.Telemetry (fallback lean / unknown)
        for mod_name, src in (("WIS.Core.psm1", self.core_src), ("WIS.Telemetry.psm1", self.tel_src)):
            self.assertIn("function Get-WisImageProfile", src, f"Nedostaje Get-WisImageProfile u {mod_name}")
            self.assertIn("image-profile.json", src)
            self.assertIn('image_type        = "lean"', src)
            self.assertIn('wim_version       = "unknown"', src)
            self.assertIn("Get-WisImageProfile", src.split("Export-ModuleMember", 1)[1])

        # 2. Ensure-ProgramiAvailable i Test-ProgramiDirComplete u WIS.Core.psm1
        td_blk = self.core_src.split("function Test-ProgramiDirComplete", 1)[1].split("function Ensure-ProgramiAvailable", 1)[0]
        self.assertIn("[string[]]$RequiredApps = @()", td_blk)
        self.assertIn("MissingInstallers", td_blk)

        ens_blk = self.core_src.split("function Ensure-ProgramiAvailable", 1)[1].split("function Save-State", 1)[0]
        self.assertIn("[string[]]$MissingApps = $null", ens_blk)
        self.assertIn("Get-WisImageProfile", ens_blk)
        self.assertIn("if ($effectiveMissing.Count -eq 0)", ens_blk)
        self.assertIn("Sync-ProgramiFromNas -DestinationDir $localProg -SubFolders $missingArr -Force", ens_blk)
        self.assertIn("Test-ProgramiDirComplete -DirPath $localProg -RequiredApps $missingArr", ens_blk)

        # Sync-ProgramiFromNas mapira Chrome/WinRAR/BatteryBar i iz -SubFolders na root filtere
        sync_blk = self.core_src.split("function Sync-ProgramiFromNas", 1)[1].split("function Format-WisCoreProcessArg", 1)[0]
        self.assertIn("$addTargetSpec", sync_blk)
        self.assertIn("GoogleChromeStandaloneEnterprise64.msi", sync_blk)
        self.assertIn("winrar-x64-701.exe", sync_blk)

        # 3. WIS.Apps.psm1: VCRedist prisustvo + ulazna provera u Invoke-Phase4Apps + [PREINSTALLED] + obavezni Shortcuts/PdfDefault/Activation
        self.assertIn("'(?i)^VCRedist$'", self.apps_src)
        self.assertIn("vcruntime140.dll", self.apps_src)
        p4_blk = self.apps_src.split("function Invoke-Phase4Apps", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("image-profile.json", p4_blk)
        self.assertIn("$missingApps.Count -eq 0", p4_blk)
        self.assertIn("-MissingApps $missingApps", p4_blk)
        self.assertIn("DONE ([PREINSTALLED])", p4_blk)
        self.assertIn("P4.Shortcuts", p4_blk)
        self.assertIn("P4.PdfDefault", p4_blk)
        self.assertIn("P4.Activation", p4_blk)

        # 4. Telemetrija i servisni karton ukljucuju image_type, wim_version i run_meta
        for src_name, src in (("WIS.Telemetry.psm1", self.tel_src), ("WIS.Cleanup.psm1", self.clean_src)):
            self.assertIn("image_type", src, f"Nedostaje image_type u {src_name}")
            self.assertIn("wim_version", src, f"Nedostaje wim_version u {src_name}")
            self.assertIn("run_meta", src, f"Nedostaje run_meta u {src_name}")

    def test_09_imp75_servis_share_canonical_priority(self):
        """IMP-75 dopuna: default ProgramiShare='Programi' ne sme pregaziti kanonski prioritet 'Servis'."""
        root_blk = self.apps_src.split("function Get-ServisProgramiRoot", 1)[1].split("function Invoke-InstallerWithLocalFallback", 1)[0]
        self.assertIn('$nasShares = @("Servis", "Programi", "WIS")', root_blk)
        self.assertIn('$cfgProgShare -ine "Programi"', root_blk)

        sync_blk = self.core_src.split("function Sync-ProgramiFromNas", 1)[1].split("function Format-WisCoreProcessArg", 1)[0]
        self.assertIn('$cfgProgShare -ine "Programi"', sync_blk)
        idx_servis = sync_blk.find('$shareCandidates += "Servis"')
        idx_prog = sync_blk.find('$shareCandidates += "Programi"')
        self.assertGreater(idx_servis, 0)
        self.assertGreater(idx_prog, idx_servis, "U Sync-ProgramiFromNas 'Servis' mora imati prioritet ispred 'Programi'")

    def test_10_imp100_monotonic_mb_counter_in_sync_programi(self):
        """IMP-100: Brojac preuzetih MB u Sync-ProgramiFromNas mora biti strogo monoton (ne pada unazad)."""
        sync_blk = self.core_src.split("function Sync-ProgramiFromNas", 1)[1].split("function Format-WisCoreProcessArg", 1)[0]
        self.assertIn("$maxCopiedMb = 0.0", sync_blk)
        self.assertIn("if ($candidateMb -gt $maxCopiedMb)", sync_blk)
        self.assertIn("$maxCopiedMb = $candidateMb", sync_blk)
        self.assertIn("$copiedMb = $maxCopiedMb", sync_blk)

    def test_11_imp104_desktop_batterybar_skipped_and_laptop_fallback_detection(self):
        """IMP-104: batterybar='SKIPPED' na Desktopu i stvarna detekcija laptopa u fallback bloku Export-ServisSummaryReport."""
        exp_blk = self.clean_src.split("function Export-ServisSummaryReport", 1)[1].split("function Refresh-ExplorerShell", 1)[0]

        # Nema vise hardkodovanog IsLaptop = $false
        self.assertNotIn("IsLaptop = $false;", exp_blk)
        self.assertIn("Test-DeviceIsPortable", exp_blk)
        self.assertIn("Win32_Battery", exp_blk)
        self.assertIn("Win32_SystemEnclosure", exp_blk)

        # Desktop dobija SKIPPED u JSON-u i [PRESKOČENO] Nije primenjivo (Desktop) u .txt/.md
        self.assertIn('if (-not $isLaptop) { "SKIPPED" }', exp_blk)
        self.assertIn("[PRESKOČENO] Nije primenjivo (Desktop)", exp_blk)

    def test_12_imp109_previous_hostname_never_writes_tk_prefix(self):
        """IMP-109: Get-WisPreviousHostname nikad ne upisuje TK-* hostname u initial-hostname.txt."""
        prev_blk = self.tel_src.split("function Get-WisPreviousHostname", 1)[1].split("function Send-ServisHeartbeat", 1)[0]
        self.assertIn('StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)', prev_blk)
        idx_tk_guard = prev_blk.find('if ($curHost -and $curHost.StartsWith("TK-"')
        idx_write = prev_blk.find("initial-hostname.txt")
        self.assertGreater(idx_tk_guard, 0)
        # Poslednji upis initial-hostname.txt dolazi tek POSLE TK- provere
        last_write_idx = prev_blk.rfind("initial-hostname.txt")
        self.assertGreater(last_write_idx, idx_tk_guard, "Upis u initial-hostname.txt mora biti zasticen TK- proverom")

    def test_13_imp114_canonical_autopilot_object_in_karton_and_hub_payload(self):
        """IMP-114: Pun kanonski autopilot objekat u servisni_karton.json i payload-u za ServiceHub bez laznog zelenog."""
        self.assertIn("function Get-WisAutopilotStatusFromFile", self.clean_src)
        ap_blk = self.clean_src.split("function Get-WisAutopilotStatusFromFile", 1)[1].split("function Export-ServisSummaryReport", 1)[0]

        self.assertIn("autopilot-status.json", ap_blk)
        self.assertIn("autopilot_report.json", ap_blk)
        for field in (
            "matrix_code",
            "status_level",
            "binary_verdict",
            "is_locked",
            "confirmed_free",
            "tenant_domain",
            "tenant_id",
            "assigned_device_name",
            "query_mode",
            "has_local_traces",
            "source",
        ):
            self.assertIn(field, ap_blk, f"Nedostaje kanonsko autopilot polje '{field}' u Get-WisAutopilotStatusFromFile")

        for verdict in ('"IMA_AUTOPILOT"', '"NEMA_AUTOPILOT"', '"NIJE_PROVERENO"'):
            self.assertIn(verdict, ap_blk)
        # Nikada INFO za UNVERIFIED / NIJE_PROVERENO
        self.assertNotIn('"INFO"', ap_blk)
        self.assertIn('"WARNING"', ap_blk)

        # Ukljucen u servisni_karton.json i u Invoke-Phase6Finalize payload
        exp_blk = self.clean_src.split("function Export-ServisSummaryReport", 1)[1].split("function Refresh-ExplorerShell", 1)[0]
        self.assertIn("Get-WisAutopilotStatusFromFile", exp_blk)
        self.assertIn("autopilot         = $autopilotObj", exp_blk)

        p6_blk = self.clean_src.split("function Invoke-Phase6Finalize", 1)[1].split("function Invoke-Phase5Finalize", 1)[0]
        self.assertIn('$diagCustom["autopilot"] = $p6Autopilot', p6_blk)
        self.assertIn("ConvertTo-Json -InputObject $repPayload -Compress -Depth 6", p6_blk)

        export_blk = self.clean_src.split("Export-ModuleMember", 1)[1]
        self.assertIn("Get-WisAutopilotStatusFromFile", export_blk)

        # Send-FleetFinalReport takodje podrzava fallback ucitavanje autopilot objekta
        send_blk = self.tel_src.split("function Send-FleetFinalReport", 1)[1].split("Export-ModuleMember", 1)[0]
        self.assertIn("Get-WisAutopilotStatusFromFile", send_blk)
        self.assertIn('$payload["autopilot"]', send_blk)

    def test_14_security_atomic_writes_literalpath_and_no_secret_logging(self):
        """Bezbednosne invarijante: atomican upis fajlova, -LiteralPath, bez logovanja lozinki/tokena."""
        # Atomican upis u WIS.Cleanup i WIS.Telemetry
        self.assertIn("function Write-WisTextAtomic", self.clean_src)
        self.assertIn("Move-Item -LiteralPath $tmpPath -Destination $Path -Force", self.clean_src)
        self.assertIn("Move-Item -LiteralPath $tmpHostFile -Destination $destHostFile -Force", self.tel_src)

        # Nijedna log linija ne sme stampati ClientToken niti lozinke
        for mod_name, src in (
            ("WIS.Update.psm1", self.upd_src),
            ("WIS.Apps.psm1", self.apps_src),
            ("WIS.Core.psm1", self.core_src),
            ("WIS.Cleanup.psm1", self.clean_src),
            ("WIS.Telemetry.psm1", self.tel_src),
        ):
            for idx, ln in enumerate(src.splitlines(), start=1):
                if "Write-ServisLog" in ln or "Send-IpcLog" in ln:
                    for secret_var in ("$uPass", "$nasPass", "$clientTok", "$poolClientTok", "$clientTokClean"):
                        self.assertNotIn(secret_var, ln, f"Curenje tajne {secret_var} u logu ({mod_name}:{idx})")


if __name__ == "__main__":
    unittest.main()
