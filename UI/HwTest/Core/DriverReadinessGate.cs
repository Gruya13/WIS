using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace WIS.HwTest.Core
{
    /// <summary>
    /// Stanje provere spremnosti drajvera za otkljucavanje interaktivnih perifernih testova (V344-01).
    /// Strogo kompatibilan sa C# 5.0 (/langversion:5).
    /// </summary>
    public class DriverGateState
    {
        public bool IsUnlocked { get; set; }
        public bool CanEmergencyUnlock { get; set; }
        public string ReasonTitle { get; set; }
        public string ReasonDetail { get; set; }
        public List<string> ProblemDevices { get; set; }
        public bool IsDriverInstallActive { get; set; }
        public bool HasCode14PendingReboot { get; set; }
        public bool HasBackgroundWuDrivers { get; set; }

        public DriverGateState()
        {
            ReasonTitle = string.Empty;
            ReasonDetail = string.Empty;
            ProblemDevices = new List<string>();
        }
    }

    /// <summary>
    /// Sloj B provere spremnosti drajvera: live Device Manager PnP kodovi + Basic Display Adapter.
    /// Ugradjeni WIS.UI sloj dopunjava ovu proveru stanjem Faze 2, Drivers taba i BackgroundWu (V344-01).
    /// </summary>
    public static class DriverReadinessGate
    {
        public static bool IsIgnoredPnpDevice(string deviceId, string pnpClass, bool hasHardwareId)
        {
            if (!hasHardwareId) return true;
            if (!string.IsNullOrEmpty(deviceId))
            {
                string idUp = deviceId.Trim().ToUpperInvariant();
                if (idUp.StartsWith("ROOT\\", StringComparison.Ordinal) ||
                    idUp.StartsWith("SWD\\", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            if (!string.IsNullOrEmpty(pnpClass))
            {
                string cls = pnpClass.Trim();
                if (cls.Equals("Printer", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("SoftwareDevice", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsAllowedErrorCode(uint errCode)
        {
            return errCode == 0u || errCode == 22u || errCode == 45u;
        }

        public static bool IsGenericDisplayDriverName(string nameOrDesc)
        {
            if (string.IsNullOrEmpty(nameOrDesc)) return false;
            return nameOrDesc.IndexOf("Microsoft Basic Display Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   nameOrDesc.IndexOf("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   nameOrDesc.IndexOf("Standard VGA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private const string DriverStageStatusPath = @"C:\setup\driver-stage-status.json";
        private const string DeployStatePath = @"C:\setup\deploy-state.json";

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private static DateTime GetSystemBootTimeUtc()
        {
            try
            {
                ulong uptimeMs = GetTickCount64();
                return DateTime.UtcNow.AddMilliseconds(-(double)uptimeMs);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool IsPowerShellWorkerAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p == null || p.HasExited) return false;
                    string pname = p.ProcessName ?? string.Empty;
                    return pname.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           pname.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseIsoUtc(string iso, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(iso)) return false;
            DateTime parsed;
            if (DateTime.TryParse(iso.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                utc = (parsed.Kind == DateTimeKind.Utc) ? parsed : parsed.ToUniversalTime();
                return true;
            }
            return false;
        }

        private static EnumerationOptions CreateWmiOptions(int timeoutMs)
        {
            return new EnumerationOptions
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                ReturnImmediately = true,
                Rewindable = false
            };
        }

        private static void RunWithTimeout(Action action, int timeoutMs)
        {
            if (action == null) return;
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try { action(); }
                catch { }
            }));
            worker.IsBackground = true;
            worker.Start();
            worker.Join(timeoutMs);
        }

        private static string ReadActiveDriverStageState()
        {
            try
            {
                if (!File.Exists(DriverStageStatusPath)) return string.Empty;
                string json = File.ReadAllText(DriverStageStatusPath);
                if (string.IsNullOrEmpty(json)) return string.Empty;
                Match m = Regex.Match(json, "\"state\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (!m.Success) return string.Empty;

                string normState = m.Groups[1].Value.Trim().ToUpperInvariant();
                if (normState == "SEARCHING" || normState == "DOWNLOADING" || normState == "INSTALLING")
                {
                    int workerPid = 0;
                    Match mp = Regex.Match(json, "\"worker_pid\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                    if (mp.Success)
                    {
                        int.TryParse(mp.Groups[1].Value, out workerPid);
                    }

                    DateTime updatedUtc = DateTime.MinValue;
                    Match mt = Regex.Match(json, "\"updated_at_utc\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!mt.Success || !TryParseIsoUtc(mt.Groups[1].Value, out updatedUtc))
                    {
                        try { updatedUtc = File.GetLastWriteTimeUtc(DriverStageStatusPath); }
                        catch { updatedUtc = DateTime.MinValue; }
                    }

                    DateTime bootUtc = GetSystemBootTimeUtc();
                    if (bootUtc != DateTime.MinValue && updatedUtc != DateTime.MinValue && updatedUtc < bootUtc.AddSeconds(-5.0))
                    {
                        return string.Empty;
                    }

                    double ageSec = (updatedUtc != DateTime.MinValue) ? (DateTime.UtcNow - updatedUtc).TotalSeconds : 0.0;
                    if (workerPid > 0)
                    {
                        if (!IsPowerShellWorkerAlive(workerPid) && ageSec >= 5.0)
                        {
                            return string.Empty;
                        }
                    }
                    else
                    {
                        double maxStaleSec = (normState == "INSTALLING") ? 1800.0 : 1200.0;
                        if (updatedUtc == DateTime.MinValue || ageSec > maxStaleSec)
                        {
                            return string.Empty;
                        }
                    }
                }
                return normState;
            }
            catch { }
            return string.Empty;
        }

        private static bool IsPostRebootFinalizePending()
        {
            try
            {
                if (!File.Exists(DeployStatePath)) return false;
                string json = File.ReadAllText(DeployStatePath);
                if (string.IsNullOrEmpty(json)) return false;
                return Regex.IsMatch(json, "\"post_reboot_finalize\"\\s*:\\s*true\\b", RegexOptions.IgnoreCase);
            }
            catch { }
            return false;
        }

        public static DriverGateState EvaluateLocal()
        {
            DriverGateState state = new DriverGateState();
            string stageState = ReadActiveDriverStageState();
            if (stageState == "SEARCHING" || stageState == "DOWNLOADING" || stageState == "INSTALLING")
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.IsDriverInstallActive = true;
                state.ReasonTitle = HwTestLocalization.PickFormat(
                    "Preuzimanje/instalacija drajvera je u toku ({0})",
                    "正在下载/安装驱动程序 ({0})",
                    stageState);
                state.ReasonDetail = HwTestLocalization.Pick(
                    "Interaktivni testovi i konačne provere čekaju završetak instalacije drajvera i kontrolisani restart.",
                    "交互式测试与最终检测正在等待驱动安装和受控重启完成。");
                return state;
            }

            if (IsPostRebootFinalizePending())
            {
                state.IsUnlocked = false;
                state.CanEmergencyUnlock = false;
                state.IsDriverInstallActive = true;
                state.HasCode14PendingReboot = true;
                state.ReasonTitle = HwTestLocalization.Pick(
                    "Čeka se kontrolisani restart i finalizacija drajvera",
                    "正在等待受控重启与驱动收尾完成");
                state.ReasonDetail = HwTestLocalization.Pick(
                    "Drajveri čekaju kontrolisani restart pre Faze 5 kako bi se aktivirali svi uređaji.",
                    "驱动程序正在等待阶段 5 前的受控重启以激活所有设备。");
                return state;
            }

            HashSet<string> seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            RunWithTimeout(delegate
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        new ManagementScope(@"root\CIMV2"),
                        new SelectQuery("SELECT Name, Description, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"),
                        CreateWmiOptions(2500)))
                    {
                        using (ManagementObjectCollection results = searcher.Get())
                        {
                            foreach (ManagementObject mo in results)
                            {
                                uint errCode = 0u;
                                try
                                {
                                    if (mo["ConfigManagerErrorCode"] != null)
                                    {
                                        errCode = Convert.ToUInt32(mo["ConfigManagerErrorCode"]);
                                    }
                                }
                                catch { }

                                if (IsAllowedErrorCode(errCode)) continue;

                                string devId = mo["DeviceID"] != null ? mo["DeviceID"].ToString() : "";
                                string pnpClass = mo["PNPClass"] != null ? mo["PNPClass"].ToString() : "";
                                string[] hwIds = mo["HardwareID"] as string[];
                                bool hasHwId = hwIds != null && hwIds.Length > 0;

                                if (IsIgnoredPnpDevice(devId, pnpClass, hasHwId)) continue;

                                if (errCode == 14u)
                                {
                                    state.HasCode14PendingReboot = true;
                                }

                                string devName = mo["Name"] != null ? mo["Name"].ToString().Trim() : "";
                                if (string.IsNullOrEmpty(devName))
                                {
                                    devName = mo["Description"] != null ? mo["Description"].ToString().Trim() : "";
                                }
                                if (string.IsNullOrEmpty(devName))
                                {
                                    devName = !string.IsNullOrEmpty(devId) ? devId : "PnP Device";
                                }

                                string entry = string.Format("{0} (Code {1})", devName, errCode);
                                if (seenEntries.Add(devId + "|" + errCode))
                                {
                                    state.ProblemDevices.Add(entry);
                                }
                            }
                        }
                    }
                }
                catch { }
            }, 3500);

            try
            {
                using (ManagementObjectSearcher gpuSearcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\CIMV2"),
                    new SelectQuery("SELECT Name, Description, PNPDeviceID FROM Win32_VideoController"),
                    CreateWmiOptions(2500)))
                {
                    using (ManagementObjectCollection gpuResults = gpuSearcher.Get())
                    {
                        foreach (ManagementObject mo in gpuResults)
                        {
                            string gpuName = mo["Name"] != null ? mo["Name"].ToString().Trim() : "";
                            string gpuDesc = mo["Description"] != null ? mo["Description"].ToString().Trim() : "";
                            string pnpId = mo["PNPDeviceID"] != null ? mo["PNPDeviceID"].ToString().Trim() : "";

                            bool isPci = !string.IsNullOrEmpty(pnpId) &&
                                         pnpId.StartsWith("PCI\\VEN_", StringComparison.OrdinalIgnoreCase);
                            if (!isPci) continue;

                            if (IsGenericDisplayDriverName(gpuName) || IsGenericDisplayDriverName(gpuDesc))
                            {
                                string label = !string.IsNullOrEmpty(gpuName) ? gpuName : "Microsoft Basic Display Adapter";
                                string entry = HwTestLocalization.PickFormat(
                                    "{0} (nedostaje originalni GPU drajver)",
                                    "{0} (缺少原厂显卡驱动)",
                                    label);
                                if (seenEntries.Add("GPU|" + pnpId))
                                {
                                    state.ProblemDevices.Add(entry);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (state.ProblemDevices.Count == 0 && !state.HasCode14PendingReboot)
            {
                state.IsUnlocked = true;
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = string.Empty;
                state.ReasonDetail = string.Empty;
                return state;
            }

            state.IsUnlocked = false;
            if (state.HasCode14PendingReboot)
            {
                state.CanEmergencyUnlock = false;
                state.ReasonTitle = HwTestLocalization.Pick(
                    "Potreban restart sistema radi učitavanja drajvera (Code 14)",
                    "需要重启系统以加载驱动程序 (Code 14)");
                state.ReasonDetail = HwTestLocalization.PickFormat(
                    "Detektovani uređaji čekaju restart pre pokretanja interaktivnih testova: {0}",
                    "检测到以下设备需要重启后才能运行交互式测试: {0}",
                    string.Join(", ", state.ProblemDevices.ToArray()));
            }
            else
            {
                state.CanEmergencyUnlock = true;
                state.ReasonTitle = HwTestLocalization.Pick(
                    "Svi drajveri nisu instalirani (Device Manager ima greške)",
                    "并非所有驱动程序都已安装 (设备管理器存在错误)");
                state.ReasonDetail = HwTestLocalization.PickFormat(
                    "Uklonite ili instalirajte drajvere za problematične uređaje pre testiranja periferija ({0}): {1}",
                    "请在测试外设前安装或处理以下异常设备驱动 ({0}): {1}",
                    state.ProblemDevices.Count,
                    string.Join(", ", state.ProblemDevices.ToArray()));
            }

            return state;
        }
    }
}
