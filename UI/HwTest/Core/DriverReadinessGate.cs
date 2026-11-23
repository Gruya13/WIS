using System;
using System.Collections.Generic;
using System.Management;

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

        public static DriverGateState EvaluateLocal()
        {
            DriverGateState state = new DriverGateState();
            HashSet<string> seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT Name, Description, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
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

            try
            {
                using (ManagementObjectSearcher gpuSearcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT Name, Description, PNPDeviceID FROM Win32_VideoController"))
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
