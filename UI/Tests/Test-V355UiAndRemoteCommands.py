#!/usr/bin/env python3
"""
Test-V355UiAndRemoteCommands.py
===============================
Verifikacija WIS-TASK-279 (WIS v3.5.5 / ServiceHub v3.7.2) C# WPF UI, HwTest i Remote Commands izmena:
- IMP-99  : Aktivni ograničeni auto-oporavak u PreAutoDriverGate (Code 10 / pnputil / re-resolve) i uklanjanje 60s ERROR spama
- IMP-102 : Strukturisane daljinske komande, trajna deduplikacija (executed-hub-commands.json), command-ack,
            stroga allowlista, 5 bezbednih UI akcija + live RSA-2048 / SHA-256 / Zip-Slip potpisani hotfix
- IMP-103 : ApplyDeferredRename pre SuspendBitLockerBeforeReboot / HandleInterimReboot pri ulazu u Fazu 5
- IMP-108 : Proširena lista laptop serija u ResolveIsLaptop i zabrana trajnog keširanja false pri WMI timeoutu
- IMP-112 : Čitanje C:\\Windows\\Logs\\WIS\\image-profile.json i slanje image_type + wim_version
- IMP-114 : Binarni Autopilot UI (IMA AUTOPILOT / NEMA AUTOPILOT / NIJE PROVERENO), zabrana lažnog zelenog,
            100% SR/EN/ZH lokalizacioni paritet i pun kanonski autopilot objekat u telemetriji

Uključuje i pravi runtime test nad produkcionim C# kodom (UI/TelemetryService.cs + UI/JsonHelper.cs) pod Mono-om!
"""

import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
UI_DIR = ROOT / "UI"
HWTEST_DIR = UI_DIR / "HwTest"
LAUNCHER_DIR = ROOT / "Launcher"


def _extract_const_string(src: str, const_name: str) -> str:
    m = re.search(rf'public\s+const\s+string\s+{re.escape(const_name)}\s*=\s*"([^"]+)"\s*;', src)
    return m.group(1) if m else ""


class TestV355UiAndRemoteCommandsContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        cls.main_cs = (UI_DIR / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        cls.hwt_cs = (UI_DIR / "MainWindow.HwTest.cs").read_text(encoding="utf-8")
        cls.models_cs = (UI_DIR / "Models.cs").read_text(encoding="utf-8")
        cls.loc_cs = (UI_DIR / "LocalizationService.cs").read_text(encoding="utf-8")
        cls.tel_cs = (UI_DIR / "TelemetryService.cs").read_text(encoding="utf-8")
        cls.launcher_pm_cs = (LAUNCHER_DIR / "PayloadManager.cs").read_text(encoding="utf-8")

    def test_01_imp99_preauto_driver_gate_recovery_and_no_error_spam(self):
        """IMP-99: PreAutoDriverGate ima 1x ograničeni auto-oporavak za Code 10 i ne spamuje ERROR na 60s."""
        wf_blk = self.shell_src.split("public void StartPhase5FullWorkflow(", 1)[1].split("EmitPhase5Step(\"P5.AutoDiag\"", 1)[0]
        self.assertIn("const int maxAutoRecoveryAttempts = 1;", wf_blk)
        self.assertIn("const double autoRecoveryCooldownSeconds = 60.0;", wf_blk)
        self.assertIn("const double infoHeartbeatIntervalSeconds = 300.0;", wf_blk)
        self.assertIn("TryRecoverCode10DevicesViaPnpUtil()", wf_blk)
        self.assertIn("_driverGateRecoveryHandler", wf_blk)
        self.assertIn("signatureChanged", wf_blk)
        self.assertNotIn("TotalSeconds >= 60.0", wf_blk)
        self.assertIn('statusCb("INFO", liveMsg, heartbeatMsg)', wf_blk)

        # Bezbedna validacija PnP Instance ID-a bez shell interpolacije
        self.assertIn("SafePnpInstanceIdRegex", self.shell_src)
        self.assertIn("IsSafePnpInstanceId(", self.shell_src)
        self.assertIn('"/restart-device \\"{0}\\""', self.shell_src)
        self.assertIn("UseShellExecute = false", self.shell_src)

        # Host callback povezan u MainWindow.HwTest.cs
        self.assertIn("SetDriverGateRecoveryHandler(TriggerDriverGateAutoRecoveryFromHwTest)", self.hwt_cs)
        self.assertIn('RunPhase2DriversStage("PostRebootFinalize", 6)', self.hwt_cs)

    def test_02_imp102_rsa_key_parity_allowlist_and_security_guards(self):
        """IMP-102: Identičan RSA-2048 javni ključ kao u Launcheru, stroga allowlista, bez arbitrarnog PS/CMD izvršavanja."""
        ui_key = _extract_const_string(self.tel_cs, "TrustedPublicKeyXml")
        launcher_key = _extract_const_string(self.launcher_pm_cs, "TrustedPublicKeyXml")
        self.assertTrue(ui_key.startswith("<RSAKeyValue><Modulus>"), "UI TrustedPublicKeyXml mora postojati")
        self.assertEqual(ui_key, launcher_key, "TrustedPublicKeyXml u TelemetryService.cs mora biti 1:1 identičan Launcher/PayloadManager.cs")

        # Allowlista i command-ack / deduplikacija u TelemetryService.cs
        for tok in (
            '"unlock_driver_gate"',
            '"retry_phase"',
            '"jump_to_phase"',
            '"re_resolve_drivers"',
            '"reboot_and_resume"',
            '"apply_payload_update"',
            "executed-hub-commands.json",
            "/command-ack",
            '"executed_commands"',
            "X-ServisHub-Client-Token",
            "IsActionAllowlisted(",
            "ParsePendingCommands(",
            "ValidateZipEntriesAgainstZipSlip(",
            "IsScriptsOnlyEligibleRelativePath(",
            "ApplyScriptsOnlyFromStaging(",
            "ContainsReparsePointsRecursive(",
        ):
            self.assertIn(tok, self.tel_cs)

        # Dispečer i bezbednosne kontrole u MainWindow.xaml.cs
        remote_blk = self.main_cs.split("#region Daljinske Komande i Live RSA-2048 Potpisani Hotfix (IMP-102)", 1)[1].split("#endregion", 1)[0]
        self.assertNotIn("Invoke-Expression", remote_blk)
        self.assertNotIn("cmd.exe", remote_blk)
        self.assertNotIn("-Command", remote_blk)
        for tok in (
            "HandleRemoteUnlockDriverGate",
            "HandleRemotePhaseTransition",
            "HandleRemoteReResolveDrivers",
            "HandleRemoteRebootAndResume",
            "HandleRemoteApplyPayloadUpdate",
            "targetPhase < 2 || targetPhase > 6",
            "ClearStepGuardEntriesForPhase",
            "StopActivePhaseExecutionCleanly",
            'FileName = "shutdown.exe"',
            'Arguments = "/r /t 5"',
            'Global\\WIS_Launcher_Update_Mutex',
            "--apply-pending-update",
            '"hotfix_applied"',
            "ApplyRemoteEmergencyDriverGateUnlock",
            "EnsureInstallSessionIdPersisted",
        ):
            self.assertIn(tok, remote_blk)

        # Povezivanje handlera u konstruktoru
        self.assertIn("_telemetryService.SetRemoteCommandHandler(ExecuteHubRemoteCommand);", self.main_cs)

    def test_03_imp103_apply_deferred_rename_before_phase5_reboot(self):
        """IMP-103: Na ulazu u Fazu 5, kada je hasPendingRenameRebootFlag == true, ApplyDeferredRename se poziva PRE BitLocker suspenda i HandleInterimReboot."""
        p5_gate_blk = self.main_cs.split("if (hasPendingRenameRebootFlag || hasCode14)", 1)[1].split("if (postRebootFinalizeExecuted)", 1)[0]
        idx_if = p5_gate_blk.find("if (hasPendingRenameRebootFlag)")
        idx_rename = p5_gate_blk.find('RunPhase2DriversStage("ApplyDeferredRename", 1);')
        idx_bitlocker = p5_gate_blk.find("SuspendBitLockerBeforeReboot();")
        idx_reboot = p5_gate_blk.find("HandleInterimReboot();")

        self.assertGreater(idx_if, 0)
        self.assertGreater(idx_rename, idx_if)
        self.assertGreater(idx_bitlocker, idx_rename, "ApplyDeferredRename mora ići neposredno PRE SuspendBitLockerBeforeReboot")
        self.assertGreater(idx_reboot, idx_bitlocker, "SuspendBitLockerBeforeReboot mora ići PRE HandleInterimReboot")

    def test_04_imp108_resolve_is_laptop_extended_series_and_timeout_cache_guard(self):
        """IMP-108: ResolveIsLaptop pokriva sve 22 dodatne serije i ne kešira trajno false posle WMI timeout-a."""
        required_series = (
            "VivoBook", "ProArt", "IdeaPad", "ThinkBook", "Yoga", "LifeBook",
            "Aspire", "TravelMate", "Swift", "Pavilion", "Envy", "Omen", "Victus", "OmniBook",
            "XPS", "Vostro", "Inspiron", "Prestige", "Modern", "Katana", "Stealth", "Creator",
        )
        for series in required_series:
            self.assertIn(f'"{series}"', self.tel_cs, f"Serija '{series}' mora biti u LaptopModelKeywords")

        res_blk = self.tel_cs.split("private bool ResolveIsLaptop()", 1)[1].split("private string GetCanonicalDeviceId(", 1)[0]
        self.assertIn("bool wmiSucceeded = false;", res_blk)
        self.assertNotIn("_cachedEnclosureIsLaptop = detected;", res_blk)
        self.assertIn("else if (wmiSucceeded && (wmiHadChassisData || modelPopulated))", res_blk)

    def test_05_imp112_image_profile_read_and_telemetry(self):
        """IMP-112: TelemetryService čita image-profile.json i šalje image_type + wim_version u heartbeat i finalni report."""
        for tok in (
            r'C:\Windows\Logs\WIS\image-profile.json',
            'imageType = "lean";',
            'wimVersion = "unknown";',
            'data["image_type"] = imgType;',
            'data["wim_version"] = wimVer;',
            'reportPayload["image_type"] = finalImgType;',
            'reportPayload["wim_version"] = finalWimVer;',
        ):
            self.assertIn(tok, self.tel_cs)

    def test_06_imp114_binary_autopilot_ui_localization_and_canonical_telemetry(self):
        """IMP-114: Bez lažnog zelenog 'Slobodan' i bez 'USLOVNO ČISTO'/INFO za UNVERIFIED/OFFLINE; 100% SR/EN/ZH paritet; kanonski autopilot objekat."""
        # Zabrana "USLOVNO ČISTO" u UI prikazima i lokalizaciji
        ap_blk = self.main_cs.split("private void UpdateAutopilotFromData(JsonNode root)", 1)[1].split("#region Asinhrona Detekcija Hardvera", 1)[0]
        switch_blk = ap_blk.split("switch (matrixCode)", 1)[1].split('if (root.ContainsKey("status_title")', 1)[0]
        self.assertNotIn("USLOVNO ČISTO", switch_blk)
        self.assertNotIn("USLOVNO ČISTO", self.models_cs)
        self.assertNotIn("USLOVNO ČISTO", self.loc_cs)
        self.assertIn("isLegacySoftClean", ap_blk)

        # MainWindow.xaml.cs mora proveravati confirmed_free pre zelenog statusa
        self.assertIn("confirmed_free", ap_blk)
        self.assertIn("else if (confirmedFree)", ap_blk)
        self.assertIn('ViewModel.Autopilot.BinaryVerdict = "IMA_AUTOPILOT";', ap_blk)
        self.assertIn('ViewModel.Autopilot.BinaryVerdict = "NEMA_AUTOPILOT";', ap_blk)
        self.assertIn('ViewModel.Autopilot.BinaryVerdict = "NIJE_PROVERENO";', ap_blk)
        self.assertNotIn('ViewModel.Autopilot.StatusLevel = "INFO";', ap_blk)

        # Models.cs: ComputeBinaryVerdict, ConfirmedFree, UpdateAlertState blokira i za UNVERIFIED/OFFLINE
        self.assertIn("public static string ComputeBinaryVerdict(", self.models_cs)
        self.assertIn("public bool ConfirmedFree", self.models_cs)
        self.assertIn("MdlAlertApUnverifiedTitle", self.models_cs)
        self.assertIn("MdlAlertApUnverifiedDetail", self.models_cs)

        # 100% SR/EN/ZH paritet za sve nove/izmenjene Autopilot ključeve
        for key in (
            "ApMatrixTitle_LockedTenant",
            "ApMatrixTitle_HiddenLock",
            "ApMatrixTitle_Clean",
            "ApMatrixTitle_UnverifiedClean",
            "ApMatrixTitle_UnverifiedLocalTraces",
            "ApMatrixTitle_OfflineLocalTraces",
            "ApMatrixTitle_OfflineCleanLocal",
            "ApCloudNotConfirmedShort",
            "ApCloudRegistered",
            "ApCloudFree",
            "ApCloudNotVerified",
            "MdlAlertApLockedTitle",
            "MdlAlertApUnverifiedTitle",
            "MdlAlertApUnverifiedDetail",
        ):
            self.assertEqual(self.loc_cs.count(f'"{key}"'), 3, f"Ključ {key} mora postojati u sva 3 rečnika (_srDict, _enDict, _zhDict)")

        # Kanonski autopilot objekat u TelemetryService.cs (svih 11 polja)
        ap_tel_blk = self.tel_cs.split("public Dictionary<string, object> BuildAutopilotPayload()", 1)[1].split("private static void ApplyHotfixMetadata(", 1)[0]
        for field in (
            '"matrix_code"',
            '"status_level"',
            '"binary_verdict"',
            '"is_locked"',
            '"confirmed_free"',
            '"tenant_domain"',
            '"tenant_id"',
            '"assigned_device_name"',
            '"query_mode"',
            '"has_local_traces"',
            '"source"',
        ):
            self.assertIn(field, ap_tel_blk, f"BuildAutopilotPayload mora sadržati polje {field}")
        self.assertIn('data["autopilot"] = BuildAutopilotPayload();', self.tel_cs)
        self.assertIn('reportPayload["autopilot"] = apFinalPayload;', self.tel_cs)


class TestV355RuntimeMonoBehavior(unittest.TestCase):
    """Kompajlira i izvršava stvarni produkcioni UI/TelemetryService.cs + UI/JsonHelper.cs u Mono-u!"""

    def test_runtime_remote_commands_rsa_zipslip_laptop_image_profile_and_autopilot(self):
        mcs = shutil.which("mcs")
        mono = shutil.which("mono")
        if not mcs or not mono:
            self.skipTest("mcs ili mono nije dostupan u okruženju")

        with tempfile.TemporaryDirectory(prefix="wis-v355-ui-tests-") as tmp_str:
            tmp = pathlib.Path(tmp_str)

            # Pripremi test ZIP-ove: (1) validan paket, (2) Zip-Slip paket sa ../evil.ps1
            valid_zip = tmp / "valid-payload.zip"
            mod_bytes = b"# v3.5.5 hotfix module\nfunction Test-Ok { return $true }\n"
            phase_bytes = b"# v3.5.5 hotfix phase\nWrite-Output 'ok'\n"
            exe_bytes = b"MZ-DUMMY-UI-BINARY"
            with zipfile.ZipFile(valid_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
                zf.writestr("Skripte/Modules/WIS.Core.psm1", mod_bytes)
                zf.writestr("Skripte/Phases/Phase2-Drivers.ps1", phase_bytes)
                zf.writestr("Skripte/WIS.UI/WIS.UI.exe", exe_bytes)

            zipslip_zip = tmp / "zipslip-payload.zip"
            with zipfile.ZipFile(zipslip_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
                zf.writestr("../escaped.ps1", b"evil")

            stub_cs = tmp / "WpfTestStubs.cs"
            stub_cs.write_text(
                """
using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace System.Windows
{
    public class Application
    {
        public static Application Current { get { return null; } }
        public Dispatcher Dispatcher { get { return null; } }
    }
}

namespace WIS.UI
{
    public class BenchmarkBaseline
    {
        public double FleetP50TotalSeconds { get; set; }
        public Dictionary<int, double> PhaseAveragesSeconds { get; set; }
        public Dictionary<string, double> ModelAveragesSeconds { get; set; }
        public string SourceLabel { get; set; }
        public BenchmarkBaseline()
        {
            PhaseAveragesSeconds = new Dictionary<int, double>();
            ModelAveragesSeconds = new Dictionary<string, double>();
        }
    }

    public class HardwareInfoViewModel
    {
        public string SerialNumber { get; set; }
        public string MotherboardModel { get; set; }
        public string OverviewResolution { get; set; }
    }

    public class AutopilotViewModel
    {
        public string MatrixCode { get; set; }
        public string StatusLevel { get; set; }
        public bool IsLocked { get; set; }
        public bool IsRegistered { get; set; }
        public bool ConfirmedFree { get; set; }
        public string TenantDomain { get; set; }
        public string ProfileTenantDomain { get; set; }
        public string TenantId { get; set; }
        public string ProfileTenantId { get; set; }
        public string AssignedDeviceName { get; set; }
        public string QueryMode { get; set; }
        public bool HasLocalTraces { get; set; }
        public string Source { get; set; }
        public string HwManufacturer { get; set; }
        public string HwModel { get; set; }
        public string HwSerialNumber { get; set; }

        public static string ComputeBinaryVerdict(string matrixCode, bool isLocked, bool isRegistered, bool confirmedFree)
        {
            string code = (matrixCode ?? "").ToUpperInvariant().Trim();
            if (isLocked || isRegistered || code == "LOCKED_TENANT" || code == "HIDDEN_LOCK")
                return "IMA_AUTOPILOT";
            if (confirmedFree && !isRegistered && !isLocked &&
                (code == "CONFIRMED_CLEAN" || code == "CLEAN" || code == "RELEASED_LOCAL_TRACES"))
                return "NEMA_AUTOPILOT";
            return "NIJE_PROVERENO";
        }
    }

    public class MainViewModel
    {
        public string SerialNumber { get; set; }
        public string ComputerModel { get; set; }
        public bool IsLaptop { get; set; }
        public int CurrentPhase { get; set; }
        public string PhaseName { get; set; }
        public string LiveStatus { get; set; }
        public int Progress { get; set; }
        public string CpuInfo { get; set; }
        public string RamInfo { get; set; }
        public string DiskInfo { get; set; }
        public string BatteryStatus { get; set; }
        public string WifiSsid { get; set; }
        public string IpAddress { get; set; }
        public string ConnectionType { get; set; }
        public string NasLatency { get; set; }
        public string NasStatus { get; set; }
        public string DownloadedMb { get; set; }
        public int DiskWear { get; set; }
        public string SmartStatus { get; set; }
        public int BatteryHealth { get; set; }
        public string CpuUsage { get; set; }
        public string RamUsage { get; set; }
        public AutopilotViewModel Autopilot { get; set; }
        public HardwareInfoViewModel HardwareInfo { get; set; }

        public MainViewModel()
        {
            Autopilot = new AutopilotViewModel();
            HardwareInfo = new HardwareInfoViewModel();
        }

        public Dictionary<string, string> GetAppsStatusSnapshot() { return new Dictionary<string, string>(); }
        public void UpdateServiceHubConnection(string url, string ver, bool ok) { }
        public void ApplyBenchmarkBaseline(BenchmarkBaseline b) { }
    }
}
""",
                encoding="utf-8",
            )

            runner_cs = tmp / "Runner.cs"
            runner_cs.write_text(
                """
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WIS.UI;

class Program
{
    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception("ASSERT FAILED: " + msg);
    }

    static string SignString(RSACryptoServiceProvider rsa, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        object halg = CryptoConfig.MapNameToOID("SHA256");
        if (halg == null) halg = "SHA256";
        byte[] sig = rsa.SignData(data, halg);
        return Convert.ToBase64String(sig);
    }

    static string BuildSignedManifestJson(RSACryptoServiceProvider rsa, string version, string releaseId, string zipPath, Dictionary<string, string> reqFiles, bool tamperSignature)
    {
        FileInfo fi = new FileInfo(zipPath);
        string zipSha = PayloadHotfixVerifier.CalculateSha256(zipPath);
        string reqSha = PayloadHotfixVerifier.ComputeRequiredFilesCanonicalSha256(reqFiles);
        string signedPayload = string.Format(CultureInfo.InvariantCulture,
            "WIS|v2|{0}|{1}|{2}|{3}|{4}|{5}",
            version, releaseId, fi.Length, zipSha, "3.5.0", reqSha);
        string sig = SignString(rsa, tamperSignature ? (signedPayload + "_TAMPERED") : signedPayload);

        var doc = new Dictionary<string, object>
        {
            { "product", "WIS" },
            { "schema_version", 2 },
            { "version", version },
            { "release_id", releaseId },
            { "min_launcher_version", "3.5.0" },
            { "sha256", zipSha },
            { "size", fi.Length },
            { "built_at", "2026-11-26T12:00:00Z" },
            { "required_files", reqFiles },
            { "signed_payload", signedPayload },
            { "signature", sig }
        };
        return JsonHelper.Serialize(doc);
    }

    static int Main(string[] args)
    {
        string tmpDir = args[0];
        string validZip = args[1];
        string zipSlipZip = args[2];

        // 1. IMP-102: Stroga allowlista akcija
        Assert(TelemetryService.IsActionAllowlisted("unlock_driver_gate"), "unlock_driver_gate must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("retry_phase"), "retry_phase must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("jump_to_phase"), "jump_to_phase must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("re_resolve_drivers"), "re_resolve_drivers must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("reboot_and_resume"), "reboot_and_resume must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("apply_payload_update"), "apply_payload_update must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("apply_hotfix"), "apply_hotfix alias must be allowed");
        Assert(TelemetryService.IsActionAllowlisted("skip_step"), "skip_step must be allowed");
        Assert(!TelemetryService.IsActionAllowlisted("exec"), "exec must be rejected");
        Assert(!TelemetryService.IsActionAllowlisted("powershell"), "powershell must be rejected");
        Assert(!TelemetryService.IsActionAllowlisted("cmd"), "cmd must be rejected");
        Assert(!TelemetryService.IsActionAllowlisted("Invoke-Expression"), "IEX must be rejected");
        Assert(!TelemetryService.IsActionAllowlisted("run_script"), "run_script must be rejected");

        // 2. IMP-102: Parsiranje strukturisanih + legacy komandi + trajna deduplikacija u executed-hub-commands.json
        string execPath = Path.Combine(tmpDir, "executed-hub-commands.json");
        string pendPath = Path.Combine(tmpDir, "pending-hub-commands.json");
        string skipPath = Path.Combine(tmpDir, ".skip");

        var vm = new MainViewModel();
        var tel1 = new TelemetryService(vm, null);
        tel1.ExecutedCommandsFilePath = execPath;
        tel1.PendingCommandsFilePath = pendPath;
        tel1.SkipMarkerFilePath = skipPath;
        tel1.AsyncCommandDispatch = false;

        int handledCount = 0;
        string lastReason = "";
        bool postAckRan = false;
        tel1.SetRemoteCommandHandler(delegate(HubRemoteCommand cmd)
        {
            handledCount++;
            lastReason = cmd.GetParamString("reason", "");
            return new HubCommandExecutionResult
            {
                CommandId = cmd.CommandId,
                Action = cmd.Action,
                Status = "APPLIED",
                Detail = "OK:" + lastReason,
                PostAckAction = delegate { postAckRan = true; }
            };
        });

        string respJson = "{\\"pending_commands\\": [" +
            "{\\"command_id\\": \\"cmd-001\\", \\"action\\": \\"unlock_driver_gate\\", \\"params\\": {\\"reason\\": \\"SL5 51D0 bypass\\"}}," +
            "{\\"command_id\\": \\"cmd-evil\\", \\"action\\": \\"powershell\\", \\"params\\": {\\"script\\": \\"whoami\\"}}," +
            "\\"skip_step\\"" +
            "]}";
        tel1.ProcessHeartbeatCommands(respJson);

        Assert(handledCount == 1, "Only unlock_driver_gate should invoke UI handler, got " + handledCount);
        Assert(lastReason == "SL5 51D0 bypass", "Reason param mismatch");
        Assert(postAckRan, "PostAckAction must run");
        Assert(File.Exists(execPath), "executed-hub-commands.json must be persisted");
        Assert(File.Exists(skipPath), ".skip must be created for skip_step");

        // Nova instanca TelemetryService (simulacija restarta procesa): isti command_id NE sme ponovo da se izvrši!
        var tel2 = new TelemetryService(vm, null);
        tel2.ExecutedCommandsFilePath = execPath;
        tel2.PendingCommandsFilePath = pendPath;
        tel2.SkipMarkerFilePath = skipPath;
        tel2.AsyncCommandDispatch = false;
        int secondHandledCount = 0;
        tel2.SetRemoteCommandHandler(delegate(HubRemoteCommand cmd)
        {
            secondHandledCount++;
            return new HubCommandExecutionResult { Status = "APPLIED" };
        });
        tel2.ProcessHeartbeatCommands(respJson);
        Assert(secondHandledCount == 0, "Deduplication failed across restart: secondHandledCount=" + secondHandledCount);
        List<object> recentExec = tel2.GetRecentExecutedCommandsForHeartbeat();
        Assert(recentExec.Count >= 2, "Heartbeat executed_commands must contain persisted results");

        // 3. IMP-102: RSA-2048 manifest verifikacija (validan vs nevalidan potpis, anti-downgrade, WinRing0 blokada)
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
        {
            string pubXml = rsa.ToXmlString(false);
            string stageDir = Path.Combine(tmpDir, "stage_check");
            Directory.CreateDirectory(stageDir);
            string modSrc = Path.Combine(stageDir, "Skripte", "Modules", "WIS.Core.psm1");
            string phSrc = Path.Combine(stageDir, "Skripte", "Phases", "Phase2-Drivers.ps1");
            string uiSrc = Path.Combine(stageDir, "Skripte", "WIS.UI", "WIS.UI.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(modSrc));
            Directory.CreateDirectory(Path.GetDirectoryName(phSrc));
            Directory.CreateDirectory(Path.GetDirectoryName(uiSrc));
            File.WriteAllText(modSrc, "# v3.5.5 hotfix module\\nfunction Test-Ok { return $true }\\n", new UTF8Encoding(false));
            File.WriteAllText(phSrc, "# v3.5.5 hotfix phase\\nWrite-Output 'ok'\\n", new UTF8Encoding(false));
            File.WriteAllText(uiSrc, "MZ-DUMMY-UI-BINARY", new UTF8Encoding(false));

            var reqFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Skripte/Modules/WIS.Core.psm1", PayloadHotfixVerifier.CalculateSha256(modSrc) },
                { "Skripte/Phases/Phase2-Drivers.ps1", PayloadHotfixVerifier.CalculateSha256(phSrc) },
                { "Skripte/WIS.UI/WIS.UI.exe", PayloadHotfixVerifier.CalculateSha256(uiSrc) }
            };

            string validManifestJson = BuildSignedManifestJson(rsa, "3.5.5", "rel-20261126", validZip, reqFiles, false);
            VerifiedHotfixManifest parsedOk;
            string err;
            Assert(PayloadHotfixVerifier.ParseAndVerifyManifest(validManifestJson, out parsedOk, out err, pubXml),
                "Valid RSA-2048 manifest must pass: " + err);
            Assert(parsedOk != null && parsedOk.IsSignatureValid && parsedOk.Version == "3.5.5", "Manifest fields mismatch");

            // Sa produkcionim ključem (bez override-a) efemerno potpisani manifest MORA biti odbijen!
            VerifiedHotfixManifest parsedWrongKey;
            Assert(!PayloadHotfixVerifier.ParseAndVerifyManifest(validManifestJson, out parsedWrongKey, out err),
                "Untrusted key must be rejected by production Root of Trust");

            // Tamperovani potpis MORA biti odbijen!
            string tamperedJson = BuildSignedManifestJson(rsa, "3.5.5", "rel-20261126", validZip, reqFiles, true);
            VerifiedHotfixManifest parsedTampered;
            Assert(!PayloadHotfixVerifier.ParseAndVerifyManifest(tamperedJson, out parsedTampered, out err, pubXml),
                "Tampered RSA-2048 signature must be rejected");

            // Anti-downgrade (< 3.1.0) MORA biti odbijen!
            string oldJson = BuildSignedManifestJson(rsa, "2.9.0", "rel-old", validZip, reqFiles, false);
            VerifiedHotfixManifest parsedOld;
            Assert(!PayloadHotfixVerifier.ParseAndVerifyManifest(oldJson, out parsedOld, out err, pubXml),
                "Downgrade < 3.1.0 must be rejected");

            // Zip-Slip validacija: validan ZIP prolazi, ../escaped.ps1 MORA biti odbijen!
            List<string> entries;
            Assert(PayloadHotfixVerifier.ValidateZipEntriesAgainstZipSlip(validZip, stageDir, out entries, out err),
                "Valid ZIP must pass Zip-Slip check: " + err);
            Assert(!PayloadHotfixVerifier.ValidateZipEntriesAgainstZipSlip(zipSlipZip, stageDir, out entries, out err),
                "Zip-Slip archive with ../ MUST be rejected!");

            // scripts_only filter i atomska zamena: samo Modules/*.psm1 i Phases/*.ps1, NIKADA WIS.UI.exe!
            Assert(PayloadHotfixVerifier.IsScriptsOnlyEligibleRelativePath("Skripte/Modules/WIS.Core.psm1"), "psm1 eligible");
            Assert(PayloadHotfixVerifier.IsScriptsOnlyEligibleRelativePath("Skripte/Phases/Phase2-Drivers.ps1"), "ps1 eligible");
            Assert(!PayloadHotfixVerifier.IsScriptsOnlyEligibleRelativePath("Skripte/WIS.UI/WIS.UI.exe"), "exe NOT eligible");
            Assert(!PayloadHotfixVerifier.IsScriptsOnlyEligibleRelativePath("WIS.Launcher.exe"), "launcher NOT eligible");
            Assert(!PayloadHotfixVerifier.IsScriptsOnlyEligibleRelativePath("Skripte/Modules/evil.exe"), "exe in Modules NOT eligible");

            string setupSim = Path.Combine(tmpDir, "setup_sim");
            string existingExe = Path.Combine(setupSim, "Skripte", "WIS.UI", "WIS.UI.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(existingExe));
            File.WriteAllText(existingExe, "ORIGINAL-RUNNING-UI", new UTF8Encoding(false));

            int updatedCount;
            Assert(PayloadHotfixVerifier.ApplyScriptsOnlyFromStaging(stageDir, setupSim, parsedOk, out updatedCount, out err),
                "ApplyScriptsOnlyFromStaging failed: " + err);
            Assert(updatedCount == 2, "Expected 2 updated script files, got " + updatedCount);
            Assert(File.ReadAllText(existingExe) == "ORIGINAL-RUNNING-UI", "WIS.UI.exe must remain untouched in scripts_only mode!");
            Assert(File.Exists(Path.Combine(setupSim, "Skripte", "Modules", "WIS.Core.psm1")), "Module must be replaced");
            Assert(File.Exists(Path.Combine(setupSim, "Skripte", "Phases", "Phase2-Drivers.ps1")), "Phase script must be replaced");
        }

        // 4. IMP-108: Proširena lista laptop serija
        string[] laptopModels = new string[] {
            "ASUS VivoBook 15", "ProArt StudioBook", "Lenovo IdeaPad 5", "ThinkBook 14", "Yoga Slim 7",
            "FUJITSU LifeBook E5411", "Acer Aspire 5", "TravelMate P2", "Swift Go 14", "HP Pavilion 15",
            "HP Envy x360", "OMEN 16", "Victus 15", "HP OmniBook Ultra", "Dell XPS 13", "Vostro 3520",
            "Inspiron 15", "MSI Prestige 14", "Modern 15", "Katana 15", "Stealth 16", "Creator Z16"
        };
        for (int i = 0; i < laptopModels.Length; i++)
        {
            Assert(TelemetryService.IsKnownLaptopModelName(laptopModels[i]), "Laptop model not recognized: " + laptopModels[i]);
        }
        Assert(!TelemetryService.IsKnownLaptopModelName("Dell OptiPlex 7010"), "Desktop model falsely classified as laptop");

        // 5. IMP-112: Čitanje image-profile.json + fallback lean/unknown
        string imgProfPath = Path.Combine(tmpDir, "image-profile.json");
        string imgType, wimVer;
        TelemetryService.ReadImageProfile(imgProfPath, out imgType, out wimVer);
        Assert(imgType == "lean" && wimVer == "unknown", "Missing image-profile must fallback to lean/unknown");

        File.WriteAllText(imgProfPath, "{\\"image_type\\": \\"preinstalled\\", \\"wim_version\\": \\"26200.8037-heavy-v1\\"}", new UTF8Encoding(false));
        TelemetryService.ReadImageProfile(imgProfPath, out imgType, out wimVer);
        Assert(imgType == "preinstalled" && wimVer == "26200.8037-heavy-v1", "Preinstalled image-profile parse failed");

        // 6. IMP-114: Kanonski autopilot objekat bez lažnog zelenog
        vm.Autopilot.MatrixCode = "UNVERIFIED_CLEAN";
        vm.Autopilot.StatusLevel = "INFO"; // čak i da neko prosledi stari INFO, BuildAutopilotPayload mora podići na WARNING!
        vm.Autopilot.IsLocked = false;
        vm.Autopilot.IsRegistered = false;
        vm.Autopilot.ConfirmedFree = false;
        Dictionary<string, object> apUnver = tel1.BuildAutopilotPayload();
        Assert((string)apUnver["binary_verdict"] == "NIJE_PROVERENO", "UNVERIFIED_CLEAN must be NIJE_PROVERENO");
        Assert((string)apUnver["status_level"] == "WARNING", "UNVERIFIED_CLEAN must be WARNING, never INFO/OK");
        Assert((bool)apUnver["confirmed_free"] == false, "UNVERIFIED_CLEAN must have confirmed_free=false");

        // Lažni CLEAN bez confirmed_free == true NE SME postati NEMA_AUTOPILOT!
        vm.Autopilot.MatrixCode = "CLEAN";
        vm.Autopilot.ConfirmedFree = false;
        Dictionary<string, object> apFakeClean = tel1.BuildAutopilotPayload();
        Assert((string)apFakeClean["binary_verdict"] == "NIJE_PROVERENO", "CLEAN with confirmed_free=false must be NIJE_PROVERENO");
        Assert((string)apFakeClean["status_level"] == "WARNING", "CLEAN with confirmed_free=false must be WARNING");

        // Pravi CONFIRMED_CLEAN sa confirmed_free == true
        vm.Autopilot.MatrixCode = "CONFIRMED_CLEAN";
        vm.Autopilot.ConfirmedFree = true;
        vm.Autopilot.StatusLevel = "OK";
        Dictionary<string, object> apClean = tel1.BuildAutopilotPayload();
        Assert((string)apClean["binary_verdict"] == "NEMA_AUTOPILOT", "CONFIRMED_CLEAN must be NEMA_AUTOPILOT");
        Assert((string)apClean["status_level"] == "OK", "CONFIRMED_CLEAN must be OK");
        Assert((bool)apClean["confirmed_free"] == true, "CONFIRMED_CLEAN must have confirmed_free=true");

        // LOCKED_TENANT
        vm.Autopilot.MatrixCode = "LOCKED_TENANT";
        vm.Autopilot.IsLocked = true;
        vm.Autopilot.IsRegistered = true;
        vm.Autopilot.ConfirmedFree = false;
        vm.Autopilot.TenantDomain = "contoso.com";
        vm.Autopilot.AssignedDeviceName = "W-AAD-744311751";
        Dictionary<string, object> apLocked = tel1.BuildAutopilotPayload();
        Assert((string)apLocked["binary_verdict"] == "IMA_AUTOPILOT", "LOCKED_TENANT must be IMA_AUTOPILOT");
        Assert((string)apLocked["status_level"] == "CRITICAL", "LOCKED_TENANT must be CRITICAL");
        Assert((bool)apLocked["is_locked"] == true, "LOCKED_TENANT must have is_locked=true");
        Assert((string)apLocked["tenant_domain"] == "contoso.com", "tenant_domain mismatch");
        Assert((string)apLocked["assigned_device_name"] == "W-AAD-744311751", "assigned_device_name mismatch");

        Console.WriteLine("ALL_V355_MONO_RUNTIME_TESTS_PASSED");
        return 0;
    }
}
""",
                encoding="utf-8",
            )

            out_exe = tmp / "V355RuntimeTests.exe"
            compile_cmd = [
                mcs,
                "-sdk:4.5",
                "-langversion:5",
                "-r:System.Management.dll",
                "-r:WindowsBase.dll",
                f"-out:{out_exe}",
                str(UI_DIR / "JsonHelper.cs"),
                str(UI_DIR / "TelemetryService.cs"),
                str(stub_cs),
                str(runner_cs),
            ]
            c_res = subprocess.run(compile_cmd, capture_output=True, text=True)
            self.assertEqual(
                c_res.returncode,
                0,
                f"Mono mcs kompilacija runtime testa nije uspela:\nSTDOUT:\n{c_res.stdout}\nSTDERR:\n{c_res.stderr}",
            )

            r_res = subprocess.run(
                [mono, str(out_exe), str(tmp), str(valid_zip), str(zipslip_zip)],
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(
                r_res.returncode,
                0,
                f"Mono runtime testovi su pali:\nSTDOUT:\n{r_res.stdout}\nSTDERR:\n{r_res.stderr}",
            )
            self.assertIn("ALL_V355_MONO_RUNTIME_TESTS_PASSED", r_res.stdout)


if __name__ == "__main__":
    unittest.main()
