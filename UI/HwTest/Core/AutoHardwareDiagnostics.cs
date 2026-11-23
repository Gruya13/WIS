using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WIS.HwTest.Interop;
using WIS.UI;

namespace WIS.HwTest.Core
{
    public class CancelFlag
    {
        private volatile bool _isCancelled;
        public bool IsCancelled { get { return _isCancelled; } }
        public void Cancel() { _isCancelled = true; }
        public void Reset() { _isCancelled = false; }
    }

    public class AutoDiagnosticsEngine : IDisposable
    {
        private readonly object _sensorLock = new object();
        private readonly object _sensorWorkerSync = new object();
        private ThermalSensorEngine _sensorEngine;
        private bool _sensorInitAttempted;
        private bool _sensorTimedOut;
        private bool _sensorWorkerRunning;
        private bool _sensorFirstUpdateDone;
        private bool _disposed;

        private ThermalSensorEngine GetSensors()
        {
            lock (_sensorLock)
            {
                if (_disposed) return null;
                if (!_sensorInitAttempted)
                {
                    _sensorInitAttempted = true;
                    try
                    {
                        _sensorEngine = new ThermalSensorEngine();
                    }
                    catch (Exception ex)
                    {
                        HwTestLogger.Warn("AUTO_SENSORS", "ThermalSensorEngine init nije uspeo: " + ex.Message);
                        _sensorEngine = null;
                    }
                }
                return _sensorEngine;
            }
        }

        public ThermalSensorEngine EnsureSensorsAndUpdate()
        {
            if (_disposed) return null;

            object waitSync = _sensorWorkerSync;
            bool isInitialCall;
            bool workerStillRunning;

            lock (waitSync)
            {
                if (_disposed) return null;
                isInitialCall = !_sensorFirstUpdateDone;

                if (!_sensorWorkerRunning)
                {
                    _sensorWorkerRunning = true;
                    Thread t = new Thread(() =>
                    {
                        ThermalSensorEngine localEng = null;
                        try
                        {
                            localEng = GetSensors();
                            if (localEng != null)
                            {
                                localEng.Update();
                            }
                        }
                        catch { }
                        finally
                        {
                            lock (waitSync)
                            {
                                if (localEng != null)
                                {
                                    _sensorFirstUpdateDone = true;
                                }
                                _sensorWorkerRunning = false;
                                Monitor.PulseAll(waitSync);
                            }
                        }
                    });
                    t.IsBackground = true;
                    t.Start();
                }

                if (isInitialCall)
                {
                    Stopwatch waitSw = Stopwatch.StartNew();
                    while (_sensorWorkerRunning && !_disposed)
                    {
                        int remaining = 10000 - (int)waitSw.ElapsedMilliseconds;
                        if (remaining <= 0) break;
                        Monitor.Wait(waitSync, remaining);
                    }
                }
                else if (_sensorWorkerRunning && !_disposed)
                {
                    Monitor.Wait(waitSync, 4000);
                }

                workerStillRunning = _sensorWorkerRunning;
            }

            if (workerStillRunning)
            {
                if (!_sensorTimedOut)
                {
                    _sensorTimedOut = true;
                    HwTestLogger.Warn("AUTO_SENSORS", string.Format(
                        "EnsureSensorsAndUpdate prekoracio {0}ms timeout - pozadinska inicijalizacija nastavlja bez blokade.",
                        isInitialCall ? 10000 : 4000));
                }
                return null;
            }

            lock (_sensorLock)
            {
                if (_disposed) return null;
                return _sensorEngine;
            }
        }

        public void Dispose()
        {
            lock (_sensorLock)
            {
                _disposed = true;
                if (_sensorEngine != null)
                {
                    try { _sensorEngine.Dispose(); }
                    catch { }
                    _sensorEngine = null;
                }
            }
        }

        #region 0. BIOS, TPM 2.0 i PnP Audit
        public TestItemResult RunSecurityAndFirmwareCheck(HardwareProbeResult hwInfo)
        {
            TestItemResult res = new TestItemResult("auto_security", HwTestLocalization.Pick("BIOS, TPM 2.0 i PnP Drajveri", "BIOS、TPM 2.0 与 PnP 驱动"),
                HwTestLocalization.Pick("Provera BIOS lozinke, Computrace statusa, TPM 2.0 čipa, OA3 licence i žutih uzvičnika u Device Manageru", "检测 BIOS 密码、Computrace 状态、TPM 2.0 芯片、OA3 许可证以及设备管理器中的黄色感叹号设备"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            bool biosPassLocked = false;
            string biosPassDetail = HwTestLocalization.Pick("Otključan (bez lozinke)", "未锁定(无密码)");
            bool computraceActive = false;
            string computraceStatus = HwTestLocalization.Pick("Neaktivan / Nije detektovan", "未激活 / 未检测到");
            bool tpmPresent = false;
            bool tpmEnabled = false;
            bool tpmActivated = false;
            string tpmVersion = "";
            string secureBootStr = HwTestLocalization.Pick("Nepoznat", "未知");
            bool hasOa3Key = false;
            List<string> pnpErrorDevices = new List<string>();
            List<string> pendingFirmwareReboots = new List<string>();

            try
            {
                string mfg = (hwInfo != null ? (hwInfo.Manufacturer ?? "") : "").ToUpperInvariant();

                // 1a. Lenovo BIOS password & Computrace
                if (mfg.Contains("LENOVO"))
                {
                    try
                    {
                        using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\WMI", "SELECT PasswordState FROM Lenovo_BiosPasswordSettings"))
                        using (ManagementObjectCollection col = s.Get())
                        {
                            foreach (ManagementObject mo in col)
                            {
                                if (mo["PasswordState"] != null)
                                {
                                    uint state = Convert.ToUInt32(mo["PasswordState"]);
                                    if ((state & 0x07u) != 0u)
                                    {
                                        biosPassLocked = true;
                                        biosPassDetail = HwTestLocalization.PickFormat("ZAKLJUČAN (Lenovo PasswordState={0})", "已锁定 (Lenovo PasswordState={0})", state);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentSetting FROM Lenovo_BiosSetting"))
                        using (ManagementObjectCollection col = s.Get())
                        {
                            foreach (ManagementObject mo in col)
                            {
                                string cs = mo["CurrentSetting"] != null ? mo["CurrentSetting"].ToString().Trim() : "";
                                if (string.IsNullOrEmpty(cs)) continue;
                                int comma = cs.IndexOf(',');
                                string k = comma > 0 ? cs.Substring(0, comma).Trim() : cs;
                                string v = comma > 0 ? cs.Substring(comma + 1).Trim() : "";
                                AnalyzeSecurityBiosPair(k, v, ref biosPassLocked, ref biosPassDetail, ref computraceActive, ref computraceStatus);
                            }
                        }
                    }
                    catch { }
                }

                // 1b. HP BIOS password & Computrace
                if (mfg.Contains("HP") || mfg.Contains("HEWLETT"))
                {
                    string[] hpClasses = new string[] { "HP_BIOSSetting", "HP_BIOSEnumeration" };
                    for (int hi = 0; hi < hpClasses.Length; hi++)
                    {
                        try
                        {
                            using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\HP\InstrumentedBIOS", "SELECT * FROM " + hpClasses[hi]))
                            using (ManagementObjectCollection col = s.Get())
                            {
                                foreach (ManagementObject mo in col)
                                {
                                    string bName = "";
                                    try { if (mo["Name"] != null) bName = mo["Name"].ToString().Trim(); } catch { }
                                    if (string.IsNullOrEmpty(bName)) continue;

                                    string curVal = "";
                                    try { if (mo["CurrentValue"] != null) curVal = mo["CurrentValue"].ToString().Trim(); } catch { }
                                    if (string.IsNullOrEmpty(curVal))
                                    {
                                        try { if (mo["Value"] != null) curVal = mo["Value"].ToString().Trim(); } catch { }
                                    }
                                    if (string.IsNullOrEmpty(curVal))
                                    {
                                        try
                                        {
                                            object isSetObj = mo["IsSet"];
                                            if (isSetObj != null && Convert.ToInt32(isSetObj) == 1) curVal = "Set";
                                        }
                                        catch { }
                                    }
                                    AnalyzeSecurityBiosPair(bName, curVal, ref biosPassLocked, ref biosPassDetail, ref computraceActive, ref computraceStatus);
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 1c. Dell BIOS password & Computrace
                if (mfg.Contains("DELL"))
                {
                    try
                    {
                        using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\dcim\sysman", "SELECT * FROM DCIM_BIOSPassword"))
                        using (ManagementObjectCollection col = s.Get())
                        {
                            foreach (ManagementObject mo in col)
                            {
                                string aName = "";
                                try { if (mo["AttributeName"] != null) aName = mo["AttributeName"].ToString().Trim(); } catch { }
                                int isSet = 0;
                                try { if (mo["IsSet"] != null) isSet = Convert.ToInt32(mo["IsSet"]); } catch { }
                                if (isSet == 1)
                                {
                                    biosPassLocked = true;
                                    biosPassDetail = HwTestLocalization.Pick("ZAKLJUČAN (Dell ", "已锁定 (Dell ") + (string.IsNullOrEmpty(aName) ? "BIOS Password" : aName) + ")";
                                }
                            }
                        }
                    }
                    catch { }

                    string[] dellScopes = new string[]
                    {
                        @"root\dcim\sysman|DCIM_BIOSEnumeration",
                        @"root\dcim\sysman\biosattributes|EnumerationAttribute"
                    };
                    for (int di = 0; di < dellScopes.Length; di++)
                    {
                        string[] sp = dellScopes[di].Split('|');
                        try
                        {
                            using (ManagementObjectSearcher s = new ManagementObjectSearcher(sp[0], "SELECT * FROM " + sp[1]))
                            using (ManagementObjectCollection col = s.Get())
                            {
                                foreach (ManagementObject mo in col)
                                {
                                    string aName = "";
                                    string aVal = "";
                                    try { if (mo["AttributeName"] != null) aName = mo["AttributeName"].ToString().Trim(); } catch { }
                                    try { if (mo["CurrentValue"] != null) aVal = mo["CurrentValue"].ToString().Trim(); } catch { }
                                    AnalyzeSecurityBiosPair(aName, aVal, ref biosPassLocked, ref biosPassDetail, ref computraceActive, ref computraceStatus);
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 2. TPM 2.0 provera
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm",
                        "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            tpmPresent = true;
                            try { if (mo["IsEnabled_InitialValue"] != null) tpmEnabled = Convert.ToBoolean(mo["IsEnabled_InitialValue"]); } catch { }
                            try { if (mo["IsActivated_InitialValue"] != null) tpmActivated = Convert.ToBoolean(mo["IsActivated_InitialValue"]); } catch { }
                            try
                            {
                                if (mo["SpecVersion"] != null)
                                {
                                    string spec = mo["SpecVersion"].ToString().Trim();
                                    int cIdx = spec.IndexOf(',');
                                    tpmVersion = (cIdx > 0 ? spec.Substring(0, cIdx) : spec).Trim();
                                }
                            }
                            catch { }
                            break;
                        }
                    }
                }
                catch { }

                // Fallback za TPM preko Device Manager-a ako WMI Security namespace traži elevate/nije odgovorio
                if (!tpmPresent)
                {
                    try
                    {
                        using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                            "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'SecurityDevices'"))
                        using (ManagementObjectCollection col = s.Get())
                        {
                            foreach (ManagementObject mo in col)
                            {
                                string n = (mo["Name"] ?? "").ToString().Trim();
                                uint err = mo["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(mo["ConfigManagerErrorCode"]) : 0;
                                if (n.IndexOf("Trusted Platform Module", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    n.IndexOf("TPM", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    tpmPresent = true;
                                    tpmEnabled = (err == 0);
                                    tpmActivated = (err == 0);
                                    if (n.Contains("2.0")) tpmVersion = "2.0";
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 3. Secure Boot (Registry)
                try
                {
                    using (Microsoft.Win32.RegistryKey sbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                    {
                        if (sbKey != null)
                        {
                            object val = sbKey.GetValue("UEFISecureBootEnabled");
                            if (val != null)
                            {
                                int sb = Convert.ToInt32(val);
                                secureBootStr = (sb == 1) ? HwTestLocalization.Pick("Uključen (Enabled)", "已开启 (Enabled)") : HwTestLocalization.Pick("Isključen (Disabled)", "已关闭 (Disabled)");
                            }
                        }
                    }
                }
                catch { }

                // 4. OA3 Windows OEM licenca (samo prisustvo bez čitanja/logovanja samog ključa)
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                        "SELECT OA3xOriginalProductKey FROM SoftwareLicensingService"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            object k = mo["OA3xOriginalProductKey"];
                            if (k != null && !string.IsNullOrEmpty(k.ToString().Trim()))
                            {
                                hasOa3Key = true;
                                break;
                            }
                        }
                    }
                }
                catch { }

                // 5. Device Manager PnP Audit (ConfigManagerErrorCode != 0)
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            uint err = 0;
                            try { if (mo["ConfigManagerErrorCode"] != null) err = Convert.ToUInt32(mo["ConfigManagerErrorCode"]); } catch { }
                            if (err == 0 || err == 22) continue; // 22 = Disabled by user

                            string name = (mo["Name"] ?? "").ToString().Trim();
                            string devId = (mo["DeviceID"] ?? "").ToString().Trim();
                            string pnpClass = (mo["PNPClass"] ?? "").ToString().Trim();
                            if (string.IsNullOrEmpty(name)) name = devId;
                            if (IsIgnorableVirtualPnpDevice(name, devId)) continue;

                            bool isPendingFirmwareReboot = (err == 14u) && (
                                pnpClass.Equals("Firmware", StringComparison.OrdinalIgnoreCase) ||
                                devId.StartsWith("UEFI\\", StringComparison.OrdinalIgnoreCase) ||
                                name.IndexOf("Firmware", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (isPendingFirmwareReboot)
                            {
                                pendingFirmwareReboots.Add(HwTestLocalization.PickFormat("{0} [Code 14 - čeka restart]", "{0} [Code 14 - 等待重启]", name));
                                continue;
                            }

                            pnpErrorDevices.Add(string.Format("{0} [Code {1}]", name, err));
                        }
                    }
                }
                catch { }

                bool tpmOk = tpmPresent && tpmEnabled && tpmActivated;
                string tpmStatusStr = !tpmPresent
                    ? HwTestLocalization.Pick("Nije detektovan / Isključen u BIOS-u", "未检测到 / 已在 BIOS 中关闭")
                    : (tpmOk
                        ? HwTestLocalization.PickFormat("Aktivan ({0})", "已激活 ({0})", string.IsNullOrEmpty(tpmVersion) ? "OK" : ("v" + tpmVersion))
                        : HwTestLocalization.PickFormat("Prisutan ali neaktivan (Enabled={0}, Activated={1})", "存在但未激活 (Enabled={0}, Activated={1})", tpmEnabled, tpmActivated));

                res.Details["bios_password_locked"] = biosPassLocked;
                res.Details["bios_password_state"] = biosPassDetail;
                res.Details["computrace_active"] = computraceActive;
                res.Details["computrace_status"] = computraceStatus;
                res.Details["tpm_status"] = tpmStatusStr;
                res.Details["secure_boot"] = secureBootStr;
                res.Details["oa3_oem_key"] = hasOa3Key ? HwTestLocalization.Pick("Prisutan u BIOS-u (OA3)", "BIOS 中存在 (OA3)") : HwTestLocalization.Pick("Nije pronađen", "未找到");
                res.Details["pnp_error_count"] = pnpErrorDevices.Count;
                res.Details["pnp_errors"] = pnpErrorDevices.Count > 0 ? string.Join(" | ", pnpErrorDevices.ToArray()) : HwTestLocalization.Pick("Nema grešaka (0)", "无错误 (0)");
                res.Details["pending_firmware_reboot_count"] = pendingFirmwareReboots.Count;
                res.Details["pending_firmware_reboot"] = pendingFirmwareReboots.Count > 0 ? string.Join(" | ", pendingFirmwareReboots.ToArray()) : "";

                string tpmBadge = tpmOk ? (string.IsNullOrEmpty(tpmVersion) ? "OK" : ("v" + tpmVersion)) : HwTestLocalization.Pick("Neaktivan", "未激活");
                res.MetricSummary = HwTestLocalization.PickFormat("BIOS: {0} | Computrace: {1} | TPM: {2} | PnP greške: {3}", "BIOS: {0} | Computrace: {1} | TPM: {2} | PnP 错误: {3}",
                    biosPassLocked ? HwTestLocalization.Pick("ZAKLJUČAN", "已锁定") : HwTestLocalization.Pick("Otključan", "未锁定"),
                    computraceActive ? HwTestLocalization.Pick("AKTIVAN", "已激活") : "OK",
                    tpmBadge,
                    pnpErrorDevices.Count);

                if (biosPassLocked || computraceActive)
                {
                    res.Status = TestStatus.Fail;
                    List<string> criticals = new List<string>();
                    if (biosPassLocked) criticals.Add(biosPassDetail);
                    if (computraceActive) criticals.Add(computraceStatus);
                    res.Reason = HwTestLocalization.Pick("KRITIČNO: ", "严重: ") + string.Join("; ", criticals.ToArray());
                }
                else if (pnpErrorDevices.Count > 0 || !tpmOk)
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = true;
                    List<string> warns = new List<string>();
                    if (pnpErrorDevices.Count > 0)
                        warns.Add(HwTestLocalization.PickFormat("Device Manager ima {0} uređaj(a) sa greškom: {1}", "设备管理器中有 {0} 个设备报错: {1}", pnpErrorDevices.Count, string.Join(", ", pnpErrorDevices.ToArray())));
                    if (!tpmOk)
                        warns.Add(HwTestLocalization.Pick("TPM 2.0 nije aktivan (", "TPM 2.0 未激活 (") + tpmStatusStr + ")");
                    res.Reason = HwTestLocalization.Pick("UPOZORENJE: ", "警告: ") + string.Join("; ", warns.ToArray());
                }
                else
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = false;
                    string fwNote = pendingFirmwareReboots.Count > 0
                        ? (HwTestLocalization.Pick(", Firmware update čeka restart: ", ",固件更新等待重启: ") + string.Join(", ", pendingFirmwareReboots.ToArray()))
                        : "";
                    res.Reason = HwTestLocalization.PickFormat("BIOS otključan, {0}, TPM {1}, Device Manager čist (0 grešaka){2}", "BIOS 未锁定,{0},TPM {1},设备管理器正常 (0 个错误){2}",
                        hasOa3Key ? HwTestLocalization.Pick("OA3 licenca prisutna", "OA3 许可证存在") : HwTestLocalization.Pick("bez OA3 ključa", "无 OA3 密钥"), tpmBadge, fwNote);
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.Pick("Greška bezbednosne / PnP provere: ", "安全 / PnP 检测出错: ") + ex.Message;
                res.MetricSummary = HwTestLocalization.Pick("Greška očitavanja", "读取出错");
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        private static void AnalyzeSecurityBiosPair(
            string key,
            string val,
            ref bool biosPassLocked,
            ref string biosPassDetail,
            ref bool computraceActive,
            ref string computraceStatus)
        {
            if (string.IsNullOrEmpty(key)) return;
            string uk = key.ToUpperInvariant();
            string uv = (val ?? "").Trim().ToUpperInvariant();

            string normKey = uk.Replace(" ", "").Replace("-", "").Replace("_", "");
            bool isPolicyOrMeta =
                uk.Contains("CONTROL") || uk.Contains("POLICY") || uk.Contains("PROMPT") ||
                uk.Contains("BYPASS") || uk.Contains("COMPLEXITY") || uk.Contains("HISTORY") ||
                uk.Contains("CHANGE") || uk.Contains("CLEAR") || uk.Contains("LOCK") ||
                uk.Contains("UNATTENDED") || uk.Contains("REBOOT") || uk.Contains("BOOTDEVICE") ||
                uk.Contains("BEEP") || uk.Contains("COUNT") || uk.Contains("CHAR") ||
                uk.Contains("TYPE") || uk.Contains("STATE") || uk.Contains("SUPPORT") ||
                uk.Contains("RESET") || uk.Contains("MINIMUM") || uk.Contains("LENGTH") ||
                uk.Contains("MIN") || uk.Contains("MAX");

            bool isExactPassKey =
                normKey == "SUPERVISORPASSWORD" ||
                normKey == "ADMINPASSWORD" ||
                normKey == "ADMINISTRATORPASSWORD" ||
                normKey == "SYSTEMPASSWORD" ||
                normKey == "SETUPPASSWORD" ||
                normKey == "POWERONPASSWORD" ||
                normKey == "BIOSADMINPASSWORD" ||
                normKey == "PASSWORDSTATUS" ||
                normKey == "ISSUPERVISORPASSWORDSET" ||
                normKey == "ISADMINPASSWORDSET" ||
                normKey == "ISSYSTEMPASSWORDSET";

            if (!isPolicyOrMeta && isExactPassKey)
            {
                bool allowEnableKeyword = normKey.StartsWith("IS", StringComparison.Ordinal);
                bool lockedVal = (uv == "SET" || uv == "PRESENT" || uv == "1" || uv == "TRUE" || uv == "YES" || uv == "LOCKED" ||
                                  (allowEnableKeyword && (uv == "ENABLE" || uv == "ENABLED"))) &&
                                 !uv.Contains("NOT SET") && !uv.Contains("DISABLE");
                if (lockedVal)
                {
                    biosPassLocked = true;
                    biosPassDetail = HwTestLocalization.PickFormat("ZAKLJUČAN ({0}={1})", "已锁定 ({0}={1})", key, val);
                }
            }

            if (uk.Contains("COMPUTRACE") || uk.Contains("ABSOLUTE PERSISTENCE") || uk.Contains("ABSOLUTE MODULE"))
            {
                if (uv.Contains("ACTIVATED") || uv == "ACTIVATE" || uv == "ENABLE" || uv == "ENABLED" || uv == "ACTIVE")
                {
                    if (!uv.Contains("DISABLE") && !uv.Contains("DEACTIVATE") && !uv.Contains("INACTIVE"))
                    {
                        computraceActive = true;
                        computraceStatus = HwTestLocalization.PickFormat("AKTIVAN ({0}={1})", "已激活 ({0}={1})", key, val);
                    }
                }
                else if (uv.Contains("PERMANENTLY DISABLE") || uv.Contains("DISABLED"))
                {
                    if (!computraceActive) computraceStatus = string.Format("{0} ({1})", val, key);
                }
            }
        }

        private static bool IsIgnorableVirtualPnpDevice(string name, string devId)
        {
            string idUp = (devId ?? "").ToUpperInvariant();
            if (idUp.StartsWith("ROOT\\", StringComparison.Ordinal) ||
                idUp.StartsWith("SWD\\", StringComparison.Ordinal))
                return true;

            if (string.IsNullOrEmpty(name)) return false;
            string low = name.ToLowerInvariant();
            return low.Contains("wan miniport") || low.Contains("hyper-v") || low.Contains("vmware") ||
                   low.Contains("vbox") || low.Contains("microsoft kernel debug") || low.Contains("ras async") ||
                   low.Contains("ndis virtual") || low.Contains("teredo") || low.Contains("isatap") ||
                   low.Contains("umbus") || low.Contains("composite bus");
        }
        #endregion

        #region 1. Baterija i napajanje
        public TestItemResult RunBatteryCheck()
        {
            TestItemResult res = new TestItemResult("auto_battery", HwTestLocalization.Pick("Baterija i napajanje", "电池与电源"), HwTestLocalization.Pick("Provera zdravlja baterije, kapaciteta (Design vs Full Charge) i AC punjača", "检测电池健康度、容量 (设计容量 vs 满充容量) 以及 AC 充电器"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                NativeMethods.SYSTEM_POWER_STATUS pwr = new NativeMethods.SYSTEM_POWER_STATUS();
                bool gotPwr = NativeMethods.GetSystemPowerStatus(out pwr);
                bool acOnline = gotPwr && pwr.ACLineStatus == 1;
                bool noSysBattery = gotPwr && (pwr.BatteryFlag & 128) != 0;

                double designMwh = 0;
                double fullMwh = 0;
                int cycleCount = -1;
                string batteryName = "";
                string batteryChem = "";
                string batteryStatusText = "";
                int chargePct = (gotPwr && pwr.BatteryLifePercent <= 100) ? (int)pwr.BatteryLifePercent : -1;
                bool hasWmiBattery = false;
                uint chargeRateMw = 0;
                uint dischargeRateMw = 0;
                uint voltageMv = 0;
                bool isCharging = false;
                bool isDischarging = false;
                bool hasBatteryStatusRate = false;

                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\WMI", "SELECT DesignedCapacity, DeviceName, Chemistry FROM BatteryStaticData"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            hasWmiBattery = true;
                            object dc = mo["DesignedCapacity"];
                            if (dc != null)
                            {
                                double v = Convert.ToDouble(dc);
                                if (v > designMwh) designMwh = v;
                            }
                            if (string.IsNullOrEmpty(batteryName) && mo["DeviceName"] != null)
                                batteryName = mo["DeviceName"].ToString().Trim();
                            if (string.IsNullOrEmpty(batteryChem) && mo["Chemistry"] != null)
                                batteryChem = mo["Chemistry"].ToString().Trim();
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            hasWmiBattery = true;
                            object fc = mo["FullChargedCapacity"];
                            if (fc != null)
                            {
                                double v = Convert.ToDouble(fc);
                                if (v > fullMwh) fullMwh = v;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\WMI", "SELECT CycleCount FROM BatteryCycleCount"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            object cc = mo["CycleCount"];
                            if (cc != null)
                            {
                                int c = Convert.ToInt32(cc);
                                if (c >= 0 && c > cycleCount) cycleCount = c;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\WMI",
                        "SELECT ChargeRate, DischargeRate, Voltage, PowerOnline, Charging, Discharging FROM BatteryStatus"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            hasWmiBattery = true;
                            hasBatteryStatusRate = true;
                            try
                            {
                                if (mo["PowerOnline"] != null && Convert.ToBoolean(mo["PowerOnline"])) acOnline = true;
                                if (mo["Charging"] != null && Convert.ToBoolean(mo["Charging"])) isCharging = true;
                                if (mo["Discharging"] != null && Convert.ToBoolean(mo["Discharging"])) isDischarging = true;
                                if (mo["ChargeRate"] != null)
                                {
                                    uint cr = Convert.ToUInt32(mo["ChargeRate"]);
                                    if (cr > 0 && cr < 500000 && cr > chargeRateMw) chargeRateMw = cr;
                                }
                                if (mo["DischargeRate"] != null)
                                {
                                    uint dr = Convert.ToUInt32(mo["DischargeRate"]);
                                    if (dr > 0 && dr < 500000 && dr > dischargeRateMw) dischargeRateMw = dr;
                                }
                                if (mo["Voltage"] != null)
                                {
                                    uint vv = Convert.ToUInt32(mo["Voltage"]);
                                    if (vv > 1000 && vv < 100000 && vv > voltageMv) voltageMv = vv;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name, DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining, Status, BatteryStatus FROM Win32_Battery"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            hasWmiBattery = true;
                            if (string.IsNullOrEmpty(batteryName) && mo["Name"] != null)
                                batteryName = mo["Name"].ToString().Trim();
                            if (mo["Status"] != null)
                                batteryStatusText = mo["Status"].ToString().Trim();
                            if (chargePct < 0 && mo["EstimatedChargeRemaining"] != null)
                                chargePct = Convert.ToInt32(mo["EstimatedChargeRemaining"]);
                            if (designMwh <= 0 && mo["DesignCapacity"] != null)
                                designMwh = Convert.ToDouble(mo["DesignCapacity"]);
                            if (fullMwh <= 0 && mo["FullChargeCapacity"] != null)
                                fullMwh = Convert.ToDouble(mo["FullChargeCapacity"]);
                        }
                    }
                }
                catch { }

                if (!hasWmiBattery && noSysBattery)
                {
                    res.Status = TestStatus.NotApplicable;
                    res.Reason = HwTestLocalization.Pick("Uređaj nema bateriju (Desktop računar ili baterija nije povezana)", "设备没有电池 (台式机或电池未连接)");
                    res.MetricSummary = acOnline ? HwTestLocalization.Pick("AC napajanje aktivno | Nema baterije", "AC 电源已接通 | 无电池") : HwTestLocalization.Pick("Nema baterije", "无电池");
                    res.Details["ac_online"] = acOnline;
                    res.Details["battery_present"] = false;
                    sw.Stop();
                    res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                    return res;
                }

                double chargeWatts = Math.Round(chargeRateMw / 1000.0, 1);
                double dischargeWatts = Math.Round(dischargeRateMw / 1000.0, 1);
                double voltageV = Math.Round(voltageMv / 1000.0, 2);

                res.Details["battery_present"] = true;
                res.Details["battery_name"] = string.IsNullOrEmpty(batteryName) ? "System Battery" : batteryName;
                res.Details["ac_online"] = acOnline;
                res.Details["charge_percent"] = chargePct;
                res.Details["design_mwh"] = Math.Round(designMwh, 0);
                res.Details["full_charge_mwh"] = Math.Round(fullMwh, 0);
                res.Details["cycle_count"] = cycleCount;
                res.Details["charge_rate_w"] = chargeWatts;
                res.Details["discharge_rate_w"] = dischargeWatts;
                res.Details["voltage_v"] = voltageV;
                res.Details["is_charging"] = isCharging;
                res.Details["wmi_status"] = string.IsNullOrEmpty(batteryStatusText) ? "OK" : batteryStatusText;

                bool criticalStatus = !string.IsNullOrEmpty(batteryStatusText) &&
                    !batteryStatusText.Equals("OK", StringComparison.OrdinalIgnoreCase);
                bool acNotChargingWarn = acOnline && hasBatteryStatusRate && chargePct >= 0 && chargePct < 88 && chargeWatts <= 0.1 && !isCharging;

                string ratePart = "";
                if (chargeWatts > 0.1) ratePart = HwTestLocalization.PickFormat(" | Puni: {0:0.0}W", " | 充电: {0:0.0}W", chargeWatts);
                else if (dischargeWatts > 0.1) ratePart = HwTestLocalization.PickFormat(" | Troši: {0:0.0}W", " | 放电: {0:0.0}W", dischargeWatts);
                else if (acOnline && hasBatteryStatusRate) ratePart = HwTestLocalization.Pick(" | Punjenje: 0.0W", " | 充电: 0.0W");
                string voltPart = voltageV > 1.0 ? string.Format(" ({0:0.0}V)", voltageV) : "";

                if (designMwh > 0 && fullMwh > 0)
                {
                    double healthPct = Math.Min(100.0, Math.Max(0.0, Math.Round((fullMwh / designMwh) * 100.0, 1)));
                    double wearPct = Math.Round(100.0 - healthPct, 1);
                    res.Details["health_percent"] = healthPct;
                    res.Details["wear_percent"] = wearPct;

                    string cyclePart = (cycleCount >= 0) ? HwTestLocalization.PickFormat(" | Ciklusi: {0}", " | 循环次数: {0}", cycleCount) : "";
                    string chargePart = (chargePct >= 0) ? HwTestLocalization.PickFormat(" | Napunjenost: {0}%", " | 电量: {0}%", chargePct) : "";
                    res.MetricSummary = HwTestLocalization.PickFormat("Zdravlje: {0:0}% ({1:0.0}/{2:0.0} Wh) | Wear: {3:0}%{4}{5}{6}{7} | AC: {8}", "健康度: {0:0}% ({1:0.0}/{2:0.0} Wh) | 损耗: {3:0}%{4}{5}{6}{7} | AC: {8}",
                        healthPct, fullMwh / 1000.0, designMwh / 1000.0, wearPct, cyclePart, chargePart,
                        ratePart, voltPart, acOnline ? HwTestLocalization.Pick("Online", "在线") : HwTestLocalization.Pick("Baterija", "电池"));

                    if (criticalStatus || healthPct < 40.0)
                    {
                        res.Status = TestStatus.Fail;
                        res.Reason = criticalStatus
                            ? HwTestLocalization.PickFormat("Kritičan WMI status baterije ({0}), zdravlje {1:0}%", "电池 WMI 状态严重异常 ({0}),健康度 {1:0}%", batteryStatusText, healthPct)
                            : HwTestLocalization.PickFormat("Baterija je istrošena (zdravlje {0:0}% < 40% minimuma)", "电池已损耗 (健康度 {0:0}% < 最低 40%)", healthPct);
                    }
                    else if (healthPct < 65.0 || acNotChargingWarn)
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = true;
                        List<string> bw = new List<string>();
                        if (healthPct < 65.0) bw.Add(HwTestLocalization.PickFormat("Smanjen kapacitet baterije (zdravlje {0:0}%, wear {1:0}%)", "电池容量下降 (健康度 {0:0}%,损耗 {1:0}%)", healthPct, wearPct));
                        if (acNotChargingWarn) bw.Add(HwTestLocalization.PickFormat("Punjač je priključen ({0}%), ali baterija ne prima struju (ChargeRate 0 W)", "充电器已连接 ({0}%),但电池不充电 (ChargeRate 0 W)", chargePct));
                        res.Reason = HwTestLocalization.Pick("UPOZORENJE: ", "警告: ") + string.Join("; ", bw.ToArray());
                    }
                    else
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = false;
                        res.Reason = HwTestLocalization.PickFormat("Baterija ispravna (zdravlje {0:0}%{1})", "电池正常 (健康度 {0:0}%{1})", healthPct,
                            chargeWatts > 0.1 ? HwTestLocalization.PickFormat(", punjenje {0:0.0}W", ",充电 {0:0.0}W", chargeWatts) : "");
                    }
                }
                else
                {
                    res.MetricSummary = HwTestLocalization.PickFormat("{0} | Napunjenost: {1}%{2}{3} | AC: {4} (kapacitet nedostupan)", "{0} | 电量: {1}%{2}{3} | AC: {4} (无法获取容量)",
                        string.IsNullOrEmpty(batteryName) ? HwTestLocalization.Pick("Baterija", "电池") : batteryName,
                        chargePct >= 0 ? chargePct.ToString() : "?",
                        ratePart, voltPart,
                        acOnline ? HwTestLocalization.Pick("Online", "在线") : HwTestLocalization.Pick("Baterija", "电池"));

                    if (criticalStatus)
                    {
                        res.Status = TestStatus.Fail;
                        res.Reason = HwTestLocalization.Pick("Kritičan status baterije: ", "电池状态严重异常: ") + batteryStatusText;
                    }
                    else
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = true;
                        res.Reason = acNotChargingWarn
                            ? HwTestLocalization.Pick("UPOZORENJE: Firmware ne prijavljuje Design/Full kapacitet i AC ne puni bateriju (0 W)", "警告: 固件未上报设计/满充容量,且 AC 未给电池充电 (0 W)")
                            : HwTestLocalization.Pick("UPOZORENJE: Firmware ne prijavljuje Design/Full kapacitet baterije preko WMI", "警告: 固件未通过 WMI 上报电池设计/满充容量");
                    }
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.Pick("Greška pri očitavanju baterije: ", "读取电池出错: ") + ex.Message;
                res.MetricSummary = HwTestLocalization.Pick("Greška očitavanja", "读取出错");
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }
        #endregion

        #region 2. Mreža (Wi-Fi / LAN & ServisHub)
        public TestItemResult RunNetworkCheck(ModelProfileClient profileClient)
        {
            TestItemResult res = new TestItemResult("auto_network", HwTestLocalization.Pick("Mreža (Wi-Fi / LAN & Hub)", "网络 (Wi-Fi / LAN 与 Hub)"), HwTestLocalization.Pick("Provera mrežnih adaptera, link brzine, gateway-a i veze sa ServisHub serverom", "检测网络适配器、链路速率、网关以及与 ServisHub 服务器的连接"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                List<string> pnpErrors = new List<string>();
                int wifiCount = 0;
                int lanCount = 0;
                string wifiInfo = "Nema";
                string lanInfo = "Nema";
                bool anyUp = false;
                bool wifiUp = false;
                string gatewayIp = "";

                // 1. Provera WMI Device Manager error kodova na fizičkim mrežnim adapterima
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                        "SELECT Name, NetConnectionID, ConfigManagerErrorCode, PhysicalAdapter, AdapterType FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            string name = mo["Name"] != null ? mo["Name"].ToString() : HwTestLocalization.Pick("Nepoznat adapter", "未知适配器");
                            if (IsVirtualAdapterName(name)) continue;
                            uint err = mo["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(mo["ConfigManagerErrorCode"]) : 0;
                            if (err != 0 && err != 22) // 22 = Disabled by user, ostalo je stvaran kvar drajvera
                            {
                                pnpErrors.Add(HwTestLocalization.PickFormat("{0} (Code {1})", "{0} (Code {1})", name, err));
                            }
                        }
                    }
                }
                catch { }

                // 2. Aktivni interfejsi preko .NET NetworkInterface
                List<string> ifaceSummaries = new List<string>();
                NetworkInterface[] allIfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < allIfaces.Length; i++)
                {
                    NetworkInterface ni = allIfaces[i];
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    if (IsVirtualAdapterName(ni.Description) || IsVirtualAdapterName(ni.Name))
                        continue;

                    bool isWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                  ni.Description.IndexOf("wi-fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ni.Description.IndexOf("wireless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  ni.Description.IndexOf("wlan", StringComparison.OrdinalIgnoreCase) >= 0;

                    bool isLan = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                 ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                                 ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT;

                    if (!isWifi && !isLan) continue;

                    long mbps = ni.Speed > 0 ? (ni.Speed / 1000000L) : 0;
                    bool up = ni.OperationalStatus == OperationalStatus.Up;
                    if (up) anyUp = true;

                    IPInterfaceProperties ipProps = null;
                    string ipv4 = "";
                    try
                    {
                        ipProps = ni.GetIPProperties();
                        foreach (UnicastIPAddressInformation ua in ipProps.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                ipv4 = ua.Address.ToString();
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(gatewayIp) && ipProps.GatewayAddresses != null)
                        {
                            foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
                            {
                                if (gw.Address != null && gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    gatewayIp = gw.Address.ToString();
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    string stateStr = up
                        ? HwTestLocalization.PickFormat("Povezan ({0} Mbps{1})", "已连接 ({0} Mbps{1})", mbps, string.IsNullOrEmpty(ipv4) ? "" : ", " + ipv4)
                        : HwTestLocalization.Pick("Nije povezan", "未连接");

                    ifaceSummaries.Add(string.Format("{0}: {1}", ni.Description, stateStr));
                    if (isWifi)
                    {
                        wifiCount++;
                        if (up) wifiUp = true;
                        if (wifiInfo == "Nema" || up)
                            wifiInfo = up ? string.Format("{0} Mbps", mbps) : HwTestLocalization.Pick("Spreman", "就绪");
                    }
                    else if (isLan)
                    {
                        lanCount++;
                        if (lanInfo == "Nema" || up)
                            lanInfo = up ? string.Format("{0} Mbps", mbps) : HwTestLocalization.Pick("Spreman", "就绪");
                    }
                }

                // 3. Provera dostupnosti ServisHub-a i gateway-a
                string hubUrl = (profileClient != null && !string.IsNullOrEmpty(profileClient.HubBaseUrl))
                    ? profileClient.HubBaseUrl
                    : "http://192.168.170.254:8080";
                bool hubOk = false;
                long hubMs = -1;
                string hubStatusMsg = HwTestLocalization.Pick("Nedostupan", "不可用");

                if (anyUp)
                {
                    try
                    {
                        Stopwatch hsw = Stopwatch.StartNew();
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(hubUrl.TrimEnd('/') + "/api/health");
                        req.Method = "GET";
                        req.Timeout = 2500;
                        req.ReadWriteTimeout = 2500;
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        {
                            hsw.Stop();
                            hubMs = hsw.ElapsedMilliseconds;
                            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                            {
                                hubOk = true;
                                hubStatusMsg = string.Format("OK ({0} ms)", hubMs);
                            }
                            else
                            {
                                hubStatusMsg = "HTTP " + (int)resp.StatusCode;
                            }
                        }
                    }
                    catch (Exception hex)
                    {
                        hubStatusMsg = HwTestLocalization.Pick("Nedostupan (", "不可用 (") + hex.Message + ")";
                    }
                }

                long gwPingMs = -1;
                if (anyUp && !string.IsNullOrEmpty(gatewayIp))
                {
                    try
                    {
                        using (Ping p = new Ping())
                        {
                            PingReply pr = p.Send(gatewayIp, 1200);
                            if (pr != null && pr.Status == IPStatus.Success)
                                gwPingMs = pr.RoundtripTime;
                        }
                    }
                    catch { }
                }

                int wifiVisibleNetworks = 0;
                int wifiMaxSignalPct = 0;
                bool hwRadioOff = false;
                bool swRadioOff = false;
                int wifiBssidCount = 0;
                int wifi24GhzCount = 0;
                int wifi5GhzCount = 0;
                int wifi6GhzCount = 0;
                int wifiMaxRssiDbm = 0;
                bool wifiHasRssi = false;
                int wifiMax24Pct = 0;
                int wifiMax5Pct = 0;
                bool wifiSupports5Ghz = false;
                string serviceSsid = TryReadServiceWifiSsid();
                bool serviceSsidFound = false;
                int serviceSignalPct = 0;
                int serviceRssiDbm = 0;
                bool serviceHasRssi = false;
                string wlanSvcState = "";
                bool wifiWlanAccessDenied = false;
                uint wifiWlanLastError = 0u;
                bool wifiLocationPolicyDisabled = NativeMethods.IsLocationPolicyDisabled();
                bool wifiNetshLocationBlocked = false;

                if (wifiCount > 0)
                {
                    ProbeWifiSignalAndNetworks(
                        serviceSsid,
                        wifiUp,
                        out wifiVisibleNetworks,
                        out wifiMaxSignalPct,
                        out hwRadioOff,
                        out swRadioOff,
                        out wifiBssidCount,
                        out wifi24GhzCount,
                        out wifi5GhzCount,
                        out wifi6GhzCount,
                        out wifiMaxRssiDbm,
                        out wifiHasRssi,
                        out wifiMax24Pct,
                        out wifiMax5Pct,
                        out wifiSupports5Ghz,
                        out serviceSsidFound,
                        out serviceSignalPct,
                        out serviceRssiDbm,
                        out serviceHasRssi,
                        out wlanSvcState,
                        out wifiWlanAccessDenied,
                        out wifiWlanLastError,
                        out wifiLocationPolicyDisabled,
                        out wifiNetshLocationBlocked);
                }

                bool btPresent;
                bool btOk;
                string btSummary;
                ProbeBluetoothStatus(out btPresent, out btOk, out btSummary);

                res.Details["wifi_adapters"] = wifiCount;
                res.Details["lan_adapters"] = lanCount;
                res.Details["wifi_visible_networks"] = wifiVisibleNetworks;
                res.Details["wifi_max_signal_pct"] = wifiMaxSignalPct;
                res.Details["wifi_hw_radio_off"] = hwRadioOff;
                res.Details["wifi_sw_radio_off"] = swRadioOff;
                res.Details["wifi_bssid_count"] = wifiBssidCount;
                res.Details["wifi_24ghz_count"] = wifi24GhzCount;
                res.Details["wifi_5ghz_count"] = wifi5GhzCount;
                res.Details["wifi_6ghz_count"] = wifi6GhzCount;
                res.Details["wifi_max_rssi_dbm"] = wifiHasRssi ? wifiMaxRssiDbm : 0;
                res.Details["wifi_max_24ghz_pct"] = wifiMax24Pct;
                res.Details["wifi_max_5ghz_pct"] = wifiMax5Pct;
                res.Details["wifi_supports_5ghz"] = wifiSupports5Ghz;
                res.Details["wifi_service_ssid"] = string.IsNullOrEmpty(serviceSsid) ? "" : serviceSsid;
                res.Details["wifi_service_ssid_found"] = serviceSsidFound;
                res.Details["wifi_service_signal_pct"] = serviceSignalPct;
                res.Details["wifi_service_rssi_dbm"] = serviceHasRssi ? serviceRssiDbm : 0;
                res.Details["wifi_wlan_access_denied"] = wifiWlanAccessDenied;
                res.Details["wifi_wlan_last_error"] = wifiWlanLastError;
                res.Details["wifi_location_policy_disabled"] = wifiLocationPolicyDisabled;
                if (!string.IsNullOrEmpty(wlanSvcState)) res.Details["wlan_svc_state"] = wlanSvcState;
                res.Details["bluetooth_present"] = btPresent;
                res.Details["bluetooth_status"] = btSummary;
                res.Details["any_link_up"] = anyUp;
                res.Details["gateway_ip"] = gatewayIp;
                res.Details["gateway_ping_ms"] = gwPingMs;
                res.Details["hub_url"] = hubUrl;
                res.Details["hub_ok"] = hubOk;
                res.Details["hub_latency_ms"] = hubMs;
                res.Details["interfaces"] = string.Join(" | ", ifaceSummaries.ToArray());
                res.Details["pnp_errors"] = string.Join("; ", pnpErrors.ToArray());

                bool locationBlockedWifiScan = (wifiCount > 0 && (wifiWlanAccessDenied || wifiLocationPolicyDisabled || wifiNetshLocationBlocked));
                string wifiSigTxt = "";
                if (wifiCount > 0)
                {
                    if (hwRadioOff || swRadioOff)
                    {
                        wifiSigTxt = hwRadioOff ? HwTestLocalization.Pick(" (RF Kill HW OFF)", " (RF Kill 硬件已关闭)") : HwTestLocalization.Pick(" (Radio SW OFF)", " (无线软件已关闭)");
                    }
                    else if (wifiMaxSignalPct > 0 || wifiVisibleNetworks > 0 || wifiBssidCount > 0)
                    {
                        string rssiPart = wifiHasRssi ? string.Format("/{0}dBm", wifiMaxRssiDbm) : "";
                        string bandPart = string.Format("2.4G:{0} 5G:{1}", wifi24GhzCount, wifi5GhzCount);
                        if (wifi6GhzCount > 0) bandPart += string.Format(" 6G:{0}", wifi6GhzCount);
                        string svcPart = "";
                        if (!string.IsNullOrEmpty(serviceSsid))
                        {
                            svcPart = serviceSsidFound
                                ? string.Format(", AP:{0}%", serviceSignalPct)
                                : HwTestLocalization.Pick(", AP:nije vidljiv", ",AP: 不可见");
                        }
                        wifiSigTxt = string.Format(" ({0}%{1}, {2} BSSID [{3}]{4})",
                            wifiMaxSignalPct, rssiPart, wifiBssidCount > 0 ? wifiBssidCount : wifiVisibleNetworks, bandPart, svcPart);
                    }
                    else if (locationBlockedWifiScan)
                    {
                        wifiSigTxt = HwTestLocalization.Pick(" (Location blokira skeniranje)", " (定位权限阻止扫描)");
                    }
                }
                string btBadge = btPresent ? (btOk ? "OK" : HwTestLocalization.Pick("GREŠKA", "错误")) : "Nema";
                res.MetricSummary = HwTestLocalization.PickFormat("Wi-Fi: {0}{1} | LAN: {2} | BT: {3} | Hub: {4}", "Wi-Fi: {0}{1} | LAN: {2} | BT: {3} | Hub: {4}",
                    wifiInfo, wifiSigTxt, lanInfo, btBadge, anyUp ? hubStatusMsg : "Offline");

                bool radioOff = (wifiCount > 0 && (hwRadioOff || swRadioOff));
                bool noWifiSignalAtAll = (wifiCount > 0 && !wifiUp && wifiVisibleNetworks == 0 && wifiBssidCount == 0 && wifiMaxSignalPct == 0);
                bool noWifiSignalFail = (noWifiSignalAtAll && !locationBlockedWifiScan);
                bool wifiScanBlockedWarn = (locationBlockedWifiScan && (noWifiSignalAtAll || (wifiVisibleNetworks == 0 && wifiBssidCount == 0 && wifiMaxSignalPct == 0)));
                bool weakServiceSignal = (wifiCount > 0 && !string.IsNullOrEmpty(serviceSsid) && serviceSsidFound &&
                                          ((serviceSignalPct > 0 && serviceSignalPct < 55) || (serviceHasRssi && serviceRssiDbm < -75)));
                bool weakSurroundingSignal = (wifiCount > 0 && (string.IsNullOrEmpty(serviceSsid) || !serviceSsidFound) &&
                                              ((wifiMaxSignalPct > 0 && wifiMaxSignalPct < 55) || (wifiHasRssi && wifiMaxRssiDbm < -75)));
                bool weakWifiAntenna = weakServiceSignal || weakSurroundingSignal;
                bool missing5GhzBand = (wifiCount > 0 && !locationBlockedWifiScan && wifiSupports5Ghz && wifi24GhzCount > 0 && wifi5GhzCount == 0 && wifi6GhzCount == 0);
                bool missingServiceSsid = (wifiCount > 0 && !locationBlockedWifiScan && !string.IsNullOrEmpty(serviceSsid) && !serviceSsidFound && (wifiBssidCount > 0 || wifiVisibleNetworks > 0));

                if (pnpErrors.Count > 0 || (btPresent && !btOk) || radioOff || noWifiSignalFail)
                {
                    res.Status = TestStatus.Fail;
                    List<string> failParts = new List<string>();
                    if (pnpErrors.Count > 0) failParts.Add(HwTestLocalization.Pick("Greška mrežnog drajvera: ", "网络驱动错误: ") + string.Join(", ", pnpErrors.ToArray()));
                    if (btPresent && !btOk) failParts.Add(HwTestLocalization.Pick("Bluetooth greška: ", "蓝牙错误: ") + btSummary);
                    if (hwRadioOff) failParts.Add(HwTestLocalization.Pick("Wi-Fi hardverski radio prekidač (RF Kill) je isključen", "Wi-Fi 硬件无线开关 (RF Kill) 已关闭"));
                    if (swRadioOff) failParts.Add(HwTestLocalization.Pick("Wi-Fi softverski radio je isključen (Airplane mode / Wi-Fi OFF)", "Wi-Fi 软件无线已关闭 (飞行模式 / Wi-Fi OFF)"));
                    if (noWifiSignalFail && !radioOff)
                        failParts.Add(HwTestLocalization.Pick("Wi-Fi adapter je prisutan, ali vidi 0 Wi-Fi mreža nakon aktivnog skeniranja (otkačene antene ili neispravan RF modul)", "Wi-Fi 适配器存在,但主动扫描后看不到任何 Wi-Fi 网络 (天线脱落或射频模块故障)"));
                    res.Reason = string.Join("; ", failParts.ToArray());
                }
                else if (wifiCount == 0 && lanCount == 0)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = HwTestLocalization.Pick("Nije pronađen nijedan fizički Wi-Fi ili LAN adapter (fali drajver ili kvar kartice)", "未找到任何物理 Wi-Fi 或 LAN 适配器 (缺少驱动或网卡故障)");
                }
                else if (weakWifiAntenna || missing5GhzBand || missingServiceSsid || wifiScanBlockedWarn || !anyUp || !hubOk)
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = true;
                    List<string> warns = new List<string>();
                    if (wifiScanBlockedWarn)
                        warns.Add(HwTestLocalization.Pick("Windows Location politika blokira Wi-Fi skeniranje (DisableLocation / ERROR_ACCESS_DENIED) — provera okolnih Wi-Fi mreža nije moguća bez Location pristupa", "Windows 定位策略阻止了 Wi-Fi 扫描 (DisableLocation / ERROR_ACCESS_DENIED) — 没有定位权限无法检测周围 Wi-Fi 网络"));
                    if (weakServiceSignal)
                        warns.Add(HwTestLocalization.PickFormat("Slab signal servisnog AP-a '{0}' ({1}%{2}) — proverite Main/Aux kablove antene", "服务 AP '{0}' 信号弱 ({1}%{2}) — 请检查天线 Main/Aux 线缆",
                            serviceSsid, serviceSignalPct, serviceHasRssi ? HwTestLocalization.PickFormat(" / {0} dBm", " / {0} dBm", serviceRssiDbm) : ""));
                    else if (weakSurroundingSignal)
                        warns.Add(HwTestLocalization.PickFormat("Slab Wi-Fi prijem ({0}%{1}, {2} mreža) — proverite Main/Aux kablove antene", "Wi-Fi 接收信号弱 ({0}%{1}, {2} 个网络) — 请检查天线 Main/Aux 线缆",
                            wifiMaxSignalPct, wifiHasRssi ? HwTestLocalization.PickFormat(" / {0} dBm", " / {0} dBm", wifiMaxRssiDbm) : "", wifiVisibleNetworks));
                    if (missing5GhzBand)
                        warns.Add(HwTestLocalization.Pick("Dual-band Wi-Fi kartica vidi samo 2.4 GHz mreže (0 mreža na 5 GHz) — proverite konektore antene", "双频 Wi-Fi 网卡只能看到 2.4 GHz 网络 (5 GHz 为 0 个) — 请检查天线接口"));
                    if (missingServiceSsid)
                        warns.Add(HwTestLocalization.PickFormat("Servisni SSID '{0}' nije pronađen u rezultatima skeniranja", "扫描结果中未找到服务 SSID '{0}'", serviceSsid));
                    if (!anyUp)
                        warns.Add(HwTestLocalization.Pick("Mrežni adapteri su ispravni, ali računar nije povezan ni na Wi-Fi ni na LAN", "网络适配器正常,但电脑未连接 Wi-Fi 或 LAN"));
                    else if (!hubOk && gwPingMs < 0)
                        warns.Add(HwTestLocalization.Pick("Link je aktivan, ali gateway i ServisHub nisu odgovorili na proveru", "链路已连通,但网关和 ServisHub 均未响应检测"));
                    else if (!hubOk)
                        warns.Add(HwTestLocalization.Pick("Mreža radi (gateway ", "网络正常 (网关 ") + gwPingMs + HwTestLocalization.Pick(" ms), ali ServisHub nije dostupan", " ms),但 ServisHub 不可用"));
                    res.Reason = HwTestLocalization.Pick("UPOZORENJE: ", "警告: ") + string.Join("; ", warns.ToArray());
                }
                else
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = false;
                    string apInfo = (!string.IsNullOrEmpty(serviceSsid) && serviceSsidFound)
                        ? string.Format(", AP '{0}' {1}%", serviceSsid, serviceSignalPct)
                        : "";
                    res.Reason = HwTestLocalization.PickFormat("Mreža, Wi-Fi antena ({0}%{1}, 2.4G:{2}/5G:{3}{4}){5} i ServisHub dostupni ({6} ms)", "网络、Wi-Fi 天线 ({0}%{1}, 2.4G:{2}/5G:{3}{4}){5} 与 ServisHub 均正常 ({6} ms)",
                        wifiMaxSignalPct,
                        wifiHasRssi ? string.Format("/{0}dBm", wifiMaxRssiDbm) : "",
                        wifi24GhzCount,
                        wifi5GhzCount,
                        apInfo,
                        btPresent ? HwTestLocalization.Pick(", Bluetooth OK", ",蓝牙正常") : "",
                        hubMs);
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.Pick("Greška mrežne provere: ", "网络检测出错: ") + ex.Message;
                res.MetricSummary = HwTestLocalization.Pick("Greška provere", "检测出错");
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        private static string RunNetshStdout(string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return "";
                    StringBuilder sb = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.BeginOutputReadLine();
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                    }
                    else
                    {
                        proc.WaitForExit(300);
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        private static void ParseNetshNetworksOutput(string output, ref int visibleNetworks, ref int maxSignalPct)
        {
            if (string.IsNullOrEmpty(output)) return;
            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^SSID\s+\d+\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    visibleNetworks++;
                }
                else if (trimmed.IndexOf("%", StringComparison.Ordinal) >= 0)
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                        trimmed, @":\s*(\d{1,3})\s*%");
                    if (m.Success)
                    {
                        int pct;
                        if (int.TryParse(m.Groups[1].Value, out pct) && pct >= 0 && pct <= 100)
                        {
                            if (pct > maxSignalPct) maxSignalPct = pct;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Anti-Leak: čita isključivo vrednost ključa SSID= iz sekcije [WiFi] u Wifi-Servis.ini.
        /// Odbacuje prazan string i primer vrednost "YOUR_SERVICE_WIFI_SSID". Nikada ne čita druge ključeve.
        /// </summary>
        public static string TryReadServiceWifiSsid()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates = new string[] {
                    @"C:\setup\Wifi-Servis.ini",
                    Path.Combine(baseDir, "Wifi-Servis.ini"),
                    Path.Combine(baseDir, "..", "Wifi-Servis.ini"),
                    Path.Combine(baseDir, "..", "..", "Wifi-Servis.ini")
                };

                for (int i = 0; i < candidates.Length; i++)
                {
                    string path = candidates[i];
                    if (!File.Exists(path)) continue;

                    bool inWifiSection = false;
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int li = 0; li < lines.Length; li++)
                    {
                        string trimmed = (lines[li] ?? "").Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                            continue;

                        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                        {
                            inWifiSection = trimmed.Equals("[WiFi]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        if (!inWifiSection) continue;

                        int eqIdx = trimmed.IndexOf('=');
                        if (eqIdx <= 0) continue;

                        string key = trimmed.Substring(0, eqIdx).Trim();
                        if (!key.Equals("SSID", StringComparison.OrdinalIgnoreCase)) continue;

                        string val = trimmed.Substring(eqIdx + 1).Trim().Trim('"', '\'');
                        if (string.IsNullOrEmpty(val) ||
                            val.Equals("YOUR_SERVICE_WIFI_SSID", StringComparison.OrdinalIgnoreCase))
                        {
                            return "";
                        }
                        return val;
                    }
                }
            }
            catch { }
            return "";
        }

        private static bool EnsureWlanServiceRunning(out string serviceState)
        {
            serviceState = "Nepoznat";
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, State, StartMode FROM Win32_Service WHERE Name = 'WlanSvc'"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        string st = mo["State"] != null ? mo["State"].ToString().Trim() : "";
                        string mode = mo["StartMode"] != null ? mo["StartMode"].ToString().Trim() : "";
                        serviceState = string.IsNullOrEmpty(st) ? "Nepoznat" : st;

                        if (st.Equals("Running", StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            serviceState = "Disabled";
                            HwTestLogger.Warn("WIFI_SCAN", "WlanSvc servis je onemogućen (StartMode=Disabled).");
                            return false;
                        }

                        try
                        {
                            object startRes = mo.InvokeMethod("StartService", null);
                            HwTestLogger.Info("WIFI_SCAN", "Pokrećem WlanSvc servis (StartService=" + (startRes != null ? startRes.ToString() : "null") + ")");
                            Thread.Sleep(800);
                            serviceState = "StartRequested";
                            return true;
                        }
                        catch (Exception exStart)
                        {
                            HwTestLogger.Warn("WIFI_SCAN", "Neuspešno pokretanje WlanSvc servisa: " + exStart.Message);
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("WIFI_SCAN", "Greška pri proveri WlanSvc servisa: " + ex.Message);
            }
            return false;
        }

        private static bool CheckWifiSupports5GhzFromDrivers()
        {
            try
            {
                string drvOut = RunNetshStdout("wlan show drivers", 1500);
                if (!string.IsNullOrEmpty(drvOut))
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                        drvOut, @"\b802\.11(a|ac|ax|be)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void ParseNetshBssFallback(
            string output,
            string serviceSsid,
            ref int bssidCount,
            ref int band24Count,
            ref int band5Count,
            ref int band6Count,
            ref int max24Pct,
            ref int max5Pct,
            ref bool serviceSsidFound,
            ref int serviceSignalPct)
        {
            if (string.IsNullOrEmpty(output)) return;
            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string currentSsid = "";
            bool inBssid = false;
            int curBand = 0; // 24, 5, 6
            int curSignal = 0;

            for (int i = 0; i <= lines.Length; i++)
            {
                string trimmed = (i < lines.Length) ? lines[i].Trim() : "";
                bool isNewSsid = (i < lines.Length) && System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed, @"^SSID\s+\d+\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool isNewBssid = (i < lines.Length) && System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed, @"^BSSID\s+\d+\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (inBssid && (i == lines.Length || isNewSsid || isNewBssid))
                {
                    bssidCount++;
                    if (curBand == 24)
                    {
                        band24Count++;
                        if (curSignal > max24Pct) max24Pct = curSignal;
                    }
                    else if (curBand == 5)
                    {
                        band5Count++;
                        if (curSignal > max5Pct) max5Pct = curSignal;
                    }
                    else if (curBand == 6)
                    {
                        band6Count++;
                    }
                    inBssid = false;
                    curBand = 0;
                    curSignal = 0;
                }

                if (i == lines.Length) break;

                if (isNewSsid)
                {
                    int colon = trimmed.IndexOf(':');
                    currentSsid = (colon >= 0) ? trimmed.Substring(colon + 1).Trim() : "";
                    if (!string.IsNullOrEmpty(serviceSsid) &&
                        string.Equals(currentSsid, serviceSsid, StringComparison.Ordinal))
                    {
                        serviceSsidFound = true;
                    }
                    continue;
                }

                if (isNewBssid)
                {
                    inBssid = true;
                    curBand = 0;
                    curSignal = 0;
                    continue;
                }

                if (trimmed.IndexOf("%", StringComparison.Ordinal) >= 0)
                {
                    System.Text.RegularExpressions.Match ms = System.Text.RegularExpressions.Regex.Match(
                        trimmed, @":\s*(\d{1,3})\s*%");
                    if (ms.Success)
                    {
                        int pct;
                        if (int.TryParse(ms.Groups[1].Value, out pct) && pct >= 0 && pct <= 100)
                        {
                            curSignal = pct;
                            if (!string.IsNullOrEmpty(serviceSsid) &&
                                string.Equals(currentSsid, serviceSsid, StringComparison.Ordinal))
                            {
                                serviceSsidFound = true;
                                if (pct > serviceSignalPct) serviceSignalPct = pct;
                            }
                        }
                    }
                }
                else if (trimmed.IndexOf("6 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    curBand = 6;
                }
                else if (trimmed.IndexOf("5 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    curBand = 5;
                }
                else if (trimmed.IndexOf("2.4 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    curBand = 24;
                }
                else
                {
                    System.Text.RegularExpressions.Match mc = System.Text.RegularExpressions.Regex.Match(
                        trimmed, @"^(?:Channel|Kanal)\s*:\s*(\d{1,3})$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (mc.Success && curBand == 0)
                    {
                        int ch;
                        if (int.TryParse(mc.Groups[1].Value, out ch))
                        {
                            if (ch >= 1 && ch <= 14) curBand = 24;
                            else if (ch >= 32 && ch <= 177) curBand = 5;
                        }
                    }
                }
            }
        }

        private static bool IsNetshLocationBlockedMessage(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            string low = output.ToLowerInvariant();
            if (low.Contains("ms-settings:privacy-location")) return true;
            if (low.Contains("privacy & security") && low.Contains("location")) return true;
            if (low.Contains("location") && (low.Contains("permission") || low.Contains("access") || low.Contains("disabled") || low.Contains("turned off") || low.Contains("required") || low.Contains("denied"))) return true;
            if (low.Contains("lokacij") && (low.Contains("dozvol") || low.Contains("pristup") || low.Contains("isklju"))) return true;
            return false;
        }

        private static void ProbeWifiSignalAndNetworks(out int visibleNetworks, out int maxSignalPct)
        {
            bool hwRadioOff, swRadioOff, wifiHasRssi, wifiSupports5Ghz, serviceSsidFound, serviceHasRssi;
            bool wlanAccessDenied, locationPolicyDisabled, netshLocationBlocked;
            uint wlanLastError;
            int bssidCount, band24Count, band5Count, band6Count, maxRssiDbm, max24Pct, max5Pct, serviceSignalPct, serviceRssiDbm;
            string wlanSvcState;
            ProbeWifiSignalAndNetworks(
                "",
                false,
                out visibleNetworks,
                out maxSignalPct,
                out hwRadioOff,
                out swRadioOff,
                out bssidCount,
                out band24Count,
                out band5Count,
                out band6Count,
                out maxRssiDbm,
                out wifiHasRssi,
                out max24Pct,
                out max5Pct,
                out wifiSupports5Ghz,
                out serviceSsidFound,
                out serviceSignalPct,
                out serviceRssiDbm,
                out serviceHasRssi,
                out wlanSvcState,
                out wlanAccessDenied,
                out wlanLastError,
                out locationPolicyDisabled,
                out netshLocationBlocked);
        }

        private static void ProbeWifiSignalAndNetworks(
            string serviceSsid,
            bool wifiUp,
            out int visibleNetworks,
            out int maxSignalPct,
            out bool hwRadioOff,
            out bool swRadioOff,
            out int bssidCount,
            out int band24Count,
            out int band5Count,
            out int band6Count,
            out int maxRssiDbm,
            out bool hasRssi,
            out int max24Pct,
            out int max5Pct,
            out bool wifiSupports5Ghz,
            out bool serviceSsidFound,
            out int serviceSignalPct,
            out int serviceRssiDbm,
            out bool serviceHasRssi,
            out string wlanSvcState,
            out bool wlanAccessDenied,
            out uint wlanLastError,
            out bool locationPolicyDisabled,
            out bool netshLocationBlocked)
        {
            visibleNetworks = 0;
            maxSignalPct = 0;
            hwRadioOff = false;
            swRadioOff = false;
            bssidCount = 0;
            band24Count = 0;
            band5Count = 0;
            band6Count = 0;
            maxRssiDbm = 0;
            hasRssi = false;
            max24Pct = 0;
            max5Pct = 0;
            wifiSupports5Ghz = false;
            serviceSsidFound = false;
            serviceSignalPct = 0;
            serviceRssiDbm = 0;
            serviceHasRssi = false;
            wlanSvcState = "";
            wlanAccessDenied = false;
            wlanLastError = 0u;
            locationPolicyDisabled = NativeMethods.IsLocationPolicyDisabled();
            netshLocationBlocked = false;

            try
            {
                EnsureWlanServiceRunning(out wlanSvcState);
                NativeMethods.SuppressLocationPromptPopups();

                bool radioQueried;
                NativeMethods.QueryWifiRadioState(out radioQueried, out hwRadioOff, out swRadioOff);

                wifiSupports5Ghz = CheckWifiSupports5GhzFromDrivers();

                NativeMethods.WlanBssScanSummary bss = null;
                if (!hwRadioOff && !swRadioOff)
                {
                    NativeMethods.TriggerActiveWlanScan();
                    Thread.Sleep(1600);
                    bss = NativeMethods.GetWlanBssScanSummary(serviceSsid);

                    if (bss != null && bss.BssidCount == 0 && !bss.AccessDenied && NativeMethods.TriggerActiveWlanScan())
                    {
                        Thread.Sleep(1600);
                        bss = NativeMethods.GetWlanBssScanSummary(serviceSsid);
                    }

                    if (bss != null)
                    {
                        wlanAccessDenied = bss.AccessDenied;
                        wlanLastError = bss.LastError;
                        if (bss.AccessDenied || bss.LastError != 0u)
                        {
                            HwTestLogger.Warn("WIFI_SCAN", string.Format(
                                "WlanGetNetworkBssList upozorenje: AccessDenied={0}, LastError={1}, LocationPolicyDisabled={2}, BssidCount={3}",
                                bss.AccessDenied, bss.LastError, locationPolicyDisabled, bss.BssidCount));
                        }
                    }

                    if (bss != null && bss.BssidCount > 0)
                    {
                        bssidCount = bss.BssidCount;
                        visibleNetworks = bss.UniqueSsidCount > 0 ? bss.UniqueSsidCount : bss.BssidCount;
                        band24Count = bss.Band24Count;
                        band5Count = bss.Band5Count;
                        band6Count = bss.Band6Count;
                        maxSignalPct = bss.MaxSignalPct;
                        maxRssiDbm = bss.MaxRssiDbm;
                        hasRssi = bss.HasRssi;
                        max24Pct = bss.Max24Pct;
                        max5Pct = bss.Max5Pct;
                        serviceSsidFound = bss.ServiceSsidFound;
                        serviceSignalPct = bss.ServiceSignalPct;
                        serviceRssiDbm = bss.ServiceRssiDbm;
                        serviceHasRssi = bss.HasServiceRssi;
                        if (band5Count > 0 || band6Count > 0) wifiSupports5Ghz = true;
                    }
                }

                // Kada je Wi-Fi interfejs povezan (wifiUp == true) ili kad je BSSID lista prazna/odbijena,
                // pročitaj signal/SSID direktno iz WlanQueryInterface(wlan_intf_opcode_current_connection = 7u) nezavisno od jezika OS-a
                if (!hwRadioOff && !swRadioOff && (wifiUp || maxSignalPct == 0 || visibleNetworks == 0))
                {
                    NativeMethods.WlanCurrentConnectionInfo curConn = NativeMethods.QueryCurrentWifiConnection();
                    if (curConn != null && curConn.Connected)
                    {
                        if (curConn.SignalPct > maxSignalPct) maxSignalPct = curConn.SignalPct;
                        if (visibleNetworks == 0) visibleNetworks = 1;
                        if (bssidCount == 0) bssidCount = 1;

                        if (curConn.ChannelNumber >= 1u && curConn.ChannelNumber <= 14u)
                        {
                            if (band24Count == 0) band24Count = 1;
                            if (curConn.SignalPct > max24Pct) max24Pct = curConn.SignalPct;
                        }
                        else if (curConn.ChannelNumber >= 32u && curConn.ChannelNumber <= 177u)
                        {
                            if (band5Count == 0) band5Count = 1;
                            if (curConn.SignalPct > max5Pct) max5Pct = curConn.SignalPct;
                            wifiSupports5Ghz = true;
                        }
                        if (curConn.PhyType == 4u || curConn.PhyType == 8u || curConn.PhyType == 10u || curConn.PhyType == 11u)
                        {
                            wifiSupports5Ghz = true;
                        }

                        if (!string.IsNullOrEmpty(serviceSsid) &&
                            !serviceSsidFound &&
                            !string.IsNullOrEmpty(curConn.Ssid) &&
                            string.Equals(curConn.Ssid, serviceSsid, StringComparison.Ordinal))
                        {
                            serviceSsidFound = true;
                            if (curConn.SignalPct > serviceSignalPct) serviceSignalPct = curConn.SignalPct;
                        }
                    }
                }

                // Fallback na netsh ako je WlanGetNetworkBssList odbijen (npr. ERROR_ACCESS_DENIED) ili nije vratio mreže
                if (!hwRadioOff && !swRadioOff && (bssidCount == 0 || bss == null || !bss.ApiSucceeded || bss.AccessDenied))
                {
                    string output = RunNetshStdout("wlan show networks mode=bssid", 1800);
                    int netshVisible = 0;
                    int netshMaxPct = 0;
                    ParseNetshNetworksOutput(output, ref netshVisible, ref netshMaxPct);
                    if (netshVisible > visibleNetworks) visibleNetworks = netshVisible;
                    if (netshMaxPct > maxSignalPct) maxSignalPct = netshMaxPct;

                    if (netshVisible == 0 && bssidCount == 0 && IsNetshLocationBlockedMessage(output))
                    {
                        netshLocationBlocked = true;
                        HwTestLogger.Warn("WIFI_SCAN", "netsh wlan show networks blokiran od strane Windows Location politike.");
                    }

                    if (bssidCount == 0)
                    {
                        ParseNetshBssFallback(
                            output,
                            serviceSsid,
                            ref bssidCount,
                            ref band24Count,
                            ref band5Count,
                            ref band6Count,
                            ref max24Pct,
                            ref max5Pct,
                            ref serviceSsidFound,
                            ref serviceSignalPct);
                        if (band5Count > 0 || band6Count > 0) wifiSupports5Ghz = true;
                    }
                }

                // Fallback: kada je uređaj već povezan na Wi-Fi, proveri aktivni interfejs i radio status preko netsh
                if (maxSignalPct == 0 || visibleNetworks == 0 || !radioQueried)
                {
                    string ifaceOut = RunNetshStdout("wlan show interfaces", 1500);
                    if (!string.IsNullOrEmpty(ifaceOut))
                    {
                        if (bssidCount == 0 && visibleNetworks == 0 && IsNetshLocationBlockedMessage(ifaceOut))
                        {
                            netshLocationBlocked = true;
                            HwTestLogger.Warn("WIFI_SCAN", "netsh wlan show interfaces blokiran od strane Windows Location politike.");
                        }
                        string[] ifaceLines = ifaceOut.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        string connectedSsid = "";
                        int connectedBand = 0;
                        int connectedSignal = 0;
                        for (int i = 0; i < ifaceLines.Length; i++)
                        {
                            string trimmed = ifaceLines[i].Trim();
                            if (!radioQueried)
                            {
                                if (trimmed.IndexOf("Hardware Off", StringComparison.OrdinalIgnoreCase) >= 0)
                                    hwRadioOff = true;
                                if (trimmed.IndexOf("Software Off", StringComparison.OrdinalIgnoreCase) >= 0)
                                    swRadioOff = true;
                            }
                            if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                                !trimmed.StartsWith("SSID BSSID", StringComparison.OrdinalIgnoreCase) &&
                                trimmed.IndexOf(':') > 0)
                            {
                                connectedSsid = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                            }
                            else if (trimmed.IndexOf("5 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                connectedBand = 5;
                            }
                            else if (trimmed.IndexOf("6 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                connectedBand = 6;
                            }
                            else if (trimmed.IndexOf("2.4 GHz", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                connectedBand = 24;
                            }
                            else if (trimmed.IndexOf("%", StringComparison.Ordinal) >= 0)
                            {
                                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                                    trimmed, @":\s*(\d{1,3})\s*%");
                                if (m.Success)
                                {
                                    int pct;
                                    if (int.TryParse(m.Groups[1].Value, out pct) && pct > 0 && pct <= 100)
                                    {
                                        connectedSignal = pct;
                                        if (pct > maxSignalPct) maxSignalPct = pct;
                                    }
                                }
                            }
                        }
                        if (maxSignalPct > 0 && visibleNetworks == 0)
                        {
                            visibleNetworks = 1;
                            if (bssidCount == 0) bssidCount = 1;
                            if (connectedBand == 24 && band24Count == 0) { band24Count = 1; max24Pct = connectedSignal; }
                            else if (connectedBand == 5 && band5Count == 0) { band5Count = 1; max5Pct = connectedSignal; wifiSupports5Ghz = true; }
                            else if (connectedBand == 6 && band6Count == 0) { band6Count = 1; wifiSupports5Ghz = true; }
                        }
                        if (!string.IsNullOrEmpty(serviceSsid) &&
                            !serviceSsidFound &&
                            !string.IsNullOrEmpty(connectedSsid) &&
                            string.Equals(connectedSsid, serviceSsid, StringComparison.Ordinal))
                        {
                            serviceSsidFound = true;
                            if (connectedSignal > serviceSignalPct) serviceSignalPct = connectedSignal;
                        }
                    }
                }
            }
            catch { }
        }

        private static void ProbeBluetoothStatus(out bool btPresent, out bool btOk, out string btSummary)
        {
            btPresent = false;
            btOk = true;
            btSummary = HwTestLocalization.Pick("Nije detektovan", "未检测到");
            string primaryName = "";
            string firstError = "";

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, ConfigManagerErrorCode, PNPClass, Service FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth' OR Service = 'BTHUSB'"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        string name = (mo["Name"] ?? "").ToString().Trim();
                        string svc = (mo["Service"] ?? "").ToString().Trim();
                        uint err = 0;
                        try { if (mo["ConfigManagerErrorCode"] != null) err = Convert.ToUInt32(mo["ConfigManagerErrorCode"]); } catch { }

                        bool isRadio = svc.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase) ||
                                       (name.IndexOf("Enumerator", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        name.IndexOf("Transport", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        name.IndexOf("Avrcp", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        name.IndexOf("RFCOMM", StringComparison.OrdinalIgnoreCase) < 0);
                        if (!isRadio && err == 0) continue;

                        btPresent = true;
                        if (string.IsNullOrEmpty(primaryName) && !string.IsNullOrEmpty(name))
                            primaryName = name;

                        if (err != 0 && err != 22)
                        {
                            btOk = false;
                            if (string.IsNullOrEmpty(firstError))
                                firstError = HwTestLocalization.PickFormat("{0} (Code {1})", "{0} (Code {1})", string.IsNullOrEmpty(name) ? "Bluetooth" : name, err);
                        }
                    }
                }
            }
            catch { }

            if (btPresent)
            {
                btSummary = btOk
                    ? (HwTestLocalization.Pick("Aktivan (", "已激活 (") + (string.IsNullOrEmpty(primaryName) ? "Bluetooth" : primaryName) + ")")
                    : firstError;
            }
        }

        private static bool IsVirtualAdapterName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string low = name.ToLowerInvariant();
            return low.Contains("virtual") || low.Contains("hyper-v") || low.Contains("vmware") ||
                   low.Contains("vbox") || low.Contains("tap-") || low.Contains("tun") ||
                   low.Contains("loopback") || low.Contains("bluetooth") || low.Contains("miniport") ||
                   low.Contains("km-test") || low.Contains("pseudo");
        }
        #endregion

        #region 3. Disk / SSD / NVMe (Pasivno SMART + Aktivni Benchmark)
        public TestItemResult RunDiskPassiveCheck()
        {
            TestItemResult res = new TestItemResult("auto_disk", HwTestLocalization.Pick("Disk / SSD / NVMe", "磁盘 / SSD / NVMe"), HwTestLocalization.Pick("SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark", "SMART 健康状态、剩余寿命 (损耗 %)、温度以及快速读/写基准测试"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();
            PopulateDiskHealthDetails(res, true);
            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        public TestItemResult RunDiskBenchmark(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_disk", HwTestLocalization.Pick("Disk / SSD / NVMe", "磁盘 / SSD / NVMe"), HwTestLocalization.Pick("SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark", "SMART 健康状态、剩余寿命 (损耗 %)、温度以及快速读/写基准测试"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            PopulateDiskHealthDetails(res, false);
            bool smartFail = (res.Status == TestStatus.Fail);
            bool smartWarn = res.HasWarning;
            string smartReason = res.Reason;
            string primaryModel = res.Details.ContainsKey("primary_model") ? res.Details["primary_model"].ToString() : HwTestLocalization.Pick("Sistemski disk", "系统磁盘");
            string mediaKind = res.Details.ContainsKey("primary_media") ? res.Details["primary_media"].ToString() : "SSD";
            int? wearPct = res.Details.ContainsKey("wear_percent") ? (int?)Convert.ToInt32(res.Details["wear_percent"]) : null;
            int? tempC = res.Details.ContainsKey("temp_c") ? (int?)Convert.ToInt32(res.Details["temp_c"]) : null;

            string tempFile = null;
            double writeMBs = 0;
            double readMBs = 0;
            double rand4kIops = 0;
            bool benchExecuted = false;
            string benchErr = "";

            try
            {
                string tempDir = Path.GetTempPath();
                string rootDrive = Path.GetPathRoot(tempDir);
                DriveInfo di = new DriveInfo(rootDrive);
                long freeMb = di.AvailableFreeSpace / (1024L * 1024L);
                res.Details["system_drive"] = rootDrive;
                res.Details["free_gb"] = Math.Round(di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);

                int testSizeMb = 256;
                if (freeMb < 400) testSizeMb = 0;
                else if (freeMb < 1024) testSizeMb = 64;
                else if (freeMb < 2048) testSizeMb = 128;

                if (testSizeMb > 0)
                {
                    tempFile = Path.Combine(tempDir, string.Format("wis_disk_bench_{0}.tmp", Process.GetCurrentProcess().Id));
                    const int chunkBytes = 1024 * 1024; // 1 MB
                    byte[] writeBuf = new byte[chunkBytes];
                    for (int i = 0; i < writeBuf.Length; i++)
                        writeBuf[i] = (byte)((i * 31 + 7) & 0xFF);

                    if (onProgress != null) onProgress(HwTestLocalization.PickFormat("Upis {0} MB na {1}...", "正在向 {1} 写入 {0} MB...", testSizeMb, rootDrive));

                    // 1. Sekvencijalni Write (WriteThrough zaobilazi OS lazy-write cache)
                    Stopwatch wsw = Stopwatch.StartNew();
                    using (FileStream fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, chunkBytes, FileOptions.WriteThrough))
                    {
                        for (int m = 0; m < testSizeMb; m++)
                        {
                            if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                            fs.Write(writeBuf, 0, chunkBytes);
                            if ((m % 32) == 0 && onProgress != null)
                            {
                                onProgress(HwTestLocalization.PickFormat("Seq Write: {0}/{1} MB", "顺序写入: {0}/{1} MB", m + 1, testSizeMb));
                            }
                        }
                        fs.Flush(true);
                    }
                    wsw.Stop();
                    writeMBs = Math.Round(testSizeMb / Math.Max(0.05, wsw.Elapsed.TotalSeconds), 1);

                    // 2. Sekvencijalni Read bez OS file cache (FILE_FLAG_NO_BUFFERING)
                    if (onProgress != null) onProgress(HwTestLocalization.PickFormat("Čitanje {0} MB (No-Buffering)...", "正在读取 {0} MB (No-Buffering)...", testSizeMb));
                    readMBs = MeasureUnbufferedSequentialRead(tempFile, testSizeMb, onProgress, cancel);

                    // 3. Kratki 4K Random Read test (800 čitanja = ~3.1 MB)
                    if (onProgress != null) onProgress(HwTestLocalization.Pick("4K Random Read test...", "4K 随机读取测试..."));
                    rand4kIops = MeasureUnbuffered4KRandomRead(tempFile, testSizeMb, 800, cancel);

                    benchExecuted = true;
                }
                else
                {
                    benchErr = HwTestLocalization.PickFormat("Premalo slobodnog prostora na {0} (< 400 MB) za benchmark upisa", "{0} 可用空间不足 (< 400 MB),无法进行写入基准测试", rootDrive);
                }
            }
            catch (OperationCanceledException)
            {
                res.Status = TestStatus.Skip;
                res.Reason = HwTestLocalization.Pick("Benchmark diska je prekinut od strane korisnika", "磁盘基准测试已被用户中断");
                res.MetricSummary = HwTestLocalization.Pick("Prekinuto", "已中断");
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }
            catch (Exception ex)
            {
                benchErr = ex.Message;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); }
                    catch { }
                }
            }

            // Osveži temperaturu diska odmah nakon opterećenja
            try
            {
                ThermalSensorEngine eng = EnsureSensorsAndUpdate();
                if (eng != null && eng.SsdTemperature > 0 && (!tempC.HasValue || eng.SsdTemperature > tempC.Value))
                    tempC = (int)Math.Round(eng.SsdTemperature);
            }
            catch { }

            res.Details["bench_executed"] = benchExecuted;
            res.Details["seq_write_mbs"] = writeMBs;
            res.Details["seq_read_mbs"] = readMBs;
            res.Details["rand_4k_iops"] = Math.Round(rand4kIops, 0);
            if (tempC.HasValue) res.Details["temp_c"] = tempC.Value;
            if (!string.IsNullOrEmpty(benchErr)) res.Details["bench_error"] = benchErr;

            string healthTxt = wearPct.HasValue ? HwTestLocalization.PickFormat("Zdravlje: {0}%", "健康度: {0}%", Math.Max(0, 100 - wearPct.Value)) : HwTestLocalization.Pick("SMART OK", "SMART 正常");
            string tempTxt = tempC.HasValue ? string.Format(" | {0}°C", tempC.Value) : "";
            string speedTxt = benchExecuted
                ? HwTestLocalization.PickFormat(" | R/W: {0:0}/{1:0} MB/s ({2:0} IOPS)", " | 读/写: {0:0}/{1:0} MB/s ({2:0} IOPS)", readMBs, writeMBs, rand4kIops)
                : (!string.IsNullOrEmpty(benchErr) ? HwTestLocalization.Pick(" | Bench preskočen", " | 已跳过基准测试") : "");
            res.MetricSummary = string.Format("{0} ({1}) | {2}{3}{4}", ShortModel(primaryModel, 26), mediaKind, healthTxt, tempTxt, speedTxt);

            bool isHdd = mediaKind.IndexOf("HDD", StringComparison.OrdinalIgnoreCase) >= 0;
            bool lowSpeed = benchExecuted && (isHdd ? (readMBs < 40 || writeMBs < 35) : (readMBs < 180 || writeMBs < 120));
            bool highTemp = tempC.HasValue && tempC.Value >= 70;

            if (smartFail)
            {
                res.Status = TestStatus.Fail;
                res.Reason = smartReason;
            }
            else if (!benchExecuted && !string.IsNullOrEmpty(benchErr) && benchErr.IndexOf("Premalo", StringComparison.OrdinalIgnoreCase) < 0 && benchErr.IndexOf("空间不足", StringComparison.Ordinal) < 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.Pick("I/O greška tokom testa diska: ", "磁盘测试期间发生 I/O 错误: ") + benchErr;
            }
            else if (smartWarn || highTemp || lowSpeed || !benchExecuted)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                List<string> warns = new List<string>();
                if (smartWarn && !string.IsNullOrEmpty(smartReason)) warns.Add(smartReason);
                if (highTemp) warns.Add(HwTestLocalization.PickFormat("Visoka temperatura diska ({0}°C >= 70°C)", "磁盘温度过高 ({0}°C >= 70°C)", tempC.Value));
                if (lowSpeed) warns.Add(HwTestLocalization.PickFormat("Niska brzina prenosa za {0} (R {1:0} / W {2:0} MB/s)", "{0} 传输速度偏低 (读 {1:0} / 写 {2:0} MB/s)", mediaKind, readMBs, writeMBs));
                if (!benchExecuted && !string.IsNullOrEmpty(benchErr)) warns.Add(benchErr);
                res.Reason = HwTestLocalization.Pick("UPOZORENJE: ", "警告: ") + string.Join("; ", warns.ToArray());
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = HwTestLocalization.PickFormat("Disk zdrav ({0}), R/W {1:0}/{2:0} MB/s", "磁盘正常 ({0}),读/写 {1:0}/{2:0} MB/s", healthTxt, readMBs, writeMBs);
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        private void PopulateDiskHealthDetails(TestItemResult res, bool passiveOnly)
        {
            List<string> diskLines = new List<string>();
            bool anyCritical = false;
            bool anyWarn = false;
            List<string> issues = new List<string>();

            string primaryModel = "";
            string primaryMedia = "SSD";
            int? worstWear = null;
            int? maxTemp = null;
            long totalUncorrected = 0;
            bool predictFail = false;

            // 1. Storage WMI: MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter
            Dictionary<string, int> wearByDeviceId = new Dictionary<string, int>();
            Dictionary<string, int> tempByDeviceId = new Dictionary<string, int>();
            Dictionary<string, long> errByDeviceId = new Dictionary<string, long>();

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "root\\Microsoft\\Windows\\Storage",
                    "SELECT DeviceId, Temperature, Wear, ReadErrorsUncorrected, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        string did = mo["DeviceId"] != null ? mo["DeviceId"].ToString() : "";
                        if (mo["Wear"] != null)
                        {
                            int w = Convert.ToInt32(mo["Wear"]);
                            if (!string.IsNullOrEmpty(did)) wearByDeviceId[did] = w;
                            if (!worstWear.HasValue || w > worstWear.Value) worstWear = w;
                        }
                        if (mo["Temperature"] != null)
                        {
                            int t = Convert.ToInt32(mo["Temperature"]);
                            if (t > 0 && t < 150)
                            {
                                if (!string.IsNullOrEmpty(did)) tempByDeviceId[did] = t;
                                if (!maxTemp.HasValue || t > maxTemp.Value) maxTemp = t;
                            }
                        }
                        long re = mo["ReadErrorsUncorrected"] != null ? Convert.ToInt64(mo["ReadErrorsUncorrected"]) : 0;
                        long we = mo["WriteErrorsUncorrected"] != null ? Convert.ToInt64(mo["WriteErrorsUncorrected"]) : 0;
                        if (re + we > 0)
                        {
                            totalUncorrected += (re + we);
                            if (!string.IsNullOrEmpty(did)) errByDeviceId[did] = re + we;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "root\\Microsoft\\Windows\\Storage",
                    "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        string name = mo["FriendlyName"] != null ? mo["FriendlyName"].ToString().Trim() : "Disk";
                        string did = mo["DeviceId"] != null ? mo["DeviceId"].ToString() : "";
                        int busType = mo["BusType"] != null ? Convert.ToInt32(mo["BusType"]) : 0;
                        int mediaType = mo["MediaType"] != null ? Convert.ToInt32(mo["MediaType"]) : 0;
                        int health = mo["HealthStatus"] != null ? Convert.ToInt32(mo["HealthStatus"]) : 0;
                        ulong sizeBytes = mo["Size"] != null ? Convert.ToUInt64(mo["Size"]) : 0;
                        double sizeGb = Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 0);

                        // Preskoči USB flash diskove ako postoje interni diskovi
                        if (busType == 7) continue;

                        string mKind = (busType == 17) ? "NVMe" : (mediaType == 3 ? "HDD" : "SSD");
                        if (string.IsNullOrEmpty(primaryModel))
                        {
                            primaryModel = name;
                            primaryMedia = mKind;
                        }

                        string healthName = (health == 0) ? "Healthy" : (health == 1 ? "Warning" : "Unhealthy");
                        if (health == 2)
                        {
                            anyCritical = true;
                            issues.Add(name + ": HealthStatus = Unhealthy");
                        }
                        else if (health == 1)
                        {
                            anyWarn = true;
                            issues.Add(name + ": HealthStatus = Warning");
                        }

                        int w;
                        string wTxt = wearByDeviceId.TryGetValue(did, out w) ? HwTestLocalization.PickFormat(", Zdravlje {0}%", ",健康度 {0}%", Math.Max(0, 100 - w)) : "";
                        int t;
                        string tTxt = tempByDeviceId.TryGetValue(did, out t) ? string.Format(", {0}°C", t) : "";
                        diskLines.Add(string.Format("{0} ({1}, {2:0} GB, {3}{4}{5})", name, mKind, sizeGb, healthName, wTxt, tTxt));
                    }
                }
            }
            catch { }

            // 2. Fallback na Win32_DiskDrive i MSStorageDriver_FailurePredictStatus
            if (string.IsNullOrEmpty(primaryModel))
            {
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Model, InterfaceType, Size, Status FROM Win32_DiskDrive"))
                    using (ManagementObjectCollection col = s.Get())
                    {
                        foreach (ManagementObject mo in col)
                        {
                            string iface = mo["InterfaceType"] != null ? mo["InterfaceType"].ToString() : "";
                            if (iface.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
                            string model = mo["Model"] != null ? mo["Model"].ToString().Trim() : "Disk";
                            ulong sizeBytes = mo["Size"] != null ? Convert.ToUInt64(mo["Size"]) : 0;
                            string st = mo["Status"] != null ? mo["Status"].ToString() : "OK";
                            if (string.IsNullOrEmpty(primaryModel))
                            {
                                primaryModel = model;
                                primaryMedia = model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ? "NVMe" : "SSD/HDD";
                            }
                            if (!st.Equals("OK", StringComparison.OrdinalIgnoreCase))
                            {
                                anyCritical = true;
                                issues.Add(model + ": Win32 Status = " + st);
                            }
                            diskLines.Add(HwTestLocalization.PickFormat("{0} ({1:0} GB, Status {2})", "{0} ({1:0} GB, 状态 {2})", model, sizeBytes / (1024.0 * 1024.0 * 1024.0), st));
                        }
                    }
                }
                catch { }
            }

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\WMI", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        object pf = mo["PredictFailure"];
                        if (pf != null && Convert.ToBoolean(pf))
                        {
                            predictFail = true;
                            anyCritical = true;
                            issues.Add(HwTestLocalization.Pick("SMART PredictFailure = TRUE (disk je pred otkazom!)", "SMART PredictFailure = TRUE (磁盘即将故障!)"));
                        }
                    }
                }
            }
            catch { }

            // LHM dopuna temperature SSD-a
            try
            {
                ThermalSensorEngine eng = EnsureSensorsAndUpdate();
                if (eng != null && eng.SsdTemperature > 0)
                {
                    int lhmT = (int)Math.Round(eng.SsdTemperature);
                    if (!maxTemp.HasValue || lhmT > maxTemp.Value) maxTemp = lhmT;
                }
            }
            catch { }

            if (totalUncorrected > 0)
            {
                anyCritical = true;
                issues.Add(HwTestLocalization.PickFormat("Detektovano {0} neispravljivih Read/Write grešaka", "检测到 {0} 个无法纠正的读/写错误", totalUncorrected));
            }
            if (worstWear.HasValue && worstWear.Value >= 90)
            {
                anyCritical = true;
                issues.Add(HwTestLocalization.PickFormat("SSD istrošenost kritična (Wear {0}%, preostalo {1}%)", "SSD 损耗严重 (Wear {0}%,剩余 {1}%)", worstWear.Value, Math.Max(0, 100 - worstWear.Value)));
            }
            else if (worstWear.HasValue && worstWear.Value >= 75)
            {
                anyWarn = true;
                issues.Add(HwTestLocalization.PickFormat("Visoka istrošenost SSD-a (Wear {0}%, preostalo {1}%)", "SSD 损耗较高 (Wear {0}%,剩余 {1}%)", worstWear.Value, Math.Max(0, 100 - worstWear.Value)));
            }

            if (maxTemp.HasValue && maxTemp.Value >= 70)
            {
                anyWarn = true;
                issues.Add(HwTestLocalization.PickFormat("Visoka temperatura diska ({0}°C)", "磁盘温度过高 ({0}°C)", maxTemp.Value));
            }

            res.Details["primary_model"] = string.IsNullOrEmpty(primaryModel) ? HwTestLocalization.Pick("Sistemski disk", "系统磁盘") : primaryModel;
            res.Details["primary_media"] = primaryMedia;
            res.Details["disks_summary"] = string.Join(" | ", diskLines.ToArray());
            res.Details["smart_predict_fail"] = predictFail;
            res.Details["uncorrected_errors"] = totalUncorrected;
            if (worstWear.HasValue) res.Details["wear_percent"] = worstWear.Value;
            if (maxTemp.HasValue) res.Details["temp_c"] = maxTemp.Value;

            if (passiveOnly)
            {
                string healthTxt = worstWear.HasValue ? HwTestLocalization.PickFormat("Zdravlje: {0}%", "健康度: {0}%", Math.Max(0, 100 - worstWear.Value)) : HwTestLocalization.Pick("SMART OK", "SMART 正常");
                string tempTxt = maxTemp.HasValue ? string.Format(" | {0}°C", maxTemp.Value) : "";
                res.MetricSummary = HwTestLocalization.PickFormat("{0} ({1}) | {2}{3} | Benchmark čeka pokretanje", "{0} ({1}) | {2}{3} | 等待运行基准测试",
                    ShortModel(res.Details["primary_model"].ToString(), 26), primaryMedia, healthTxt, tempTxt);

                if (anyCritical)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = string.Join("; ", issues.ToArray());
                }
                else if (anyWarn)
                {
                    res.HasWarning = true;
                    res.Reason = HwTestLocalization.Pick("UPOZORENJE (SMART): ", "警告 (SMART): ") + string.Join("; ", issues.ToArray());
                    // Status ostaje Pending dok se ne pokrene i brzinski benchmark, osim ako je Fail
                }
            }
            else
            {
                if (anyCritical)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = string.Join("; ", issues.ToArray());
                }
                else if (anyWarn)
                {
                    res.HasWarning = true;
                    res.Reason = string.Join("; ", issues.ToArray());
                }
            }
        }

        private static double MeasureUnbufferedSequentialRead(string path, int totalMb, Action<string> onProgress, CancelFlag cancel)
        {
            const int bufSize = 1024 * 1024; // 1 MB (umnožak 4096)
            IntPtr rawPtr = Marshal.AllocHGlobal(bufSize + 4096);
            IntPtr alignedPtr = new IntPtr(((rawPtr.ToInt64() + 4095L) / 4096L) * 4096L);
            IntPtr hFile = new IntPtr(-1);

            try
            {
                hFile = NativeMethods.CreateFile(
                    path,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                    IntPtr.Zero);

                if (hFile != new IntPtr(-1) && hFile != IntPtr.Zero)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    long totalBytesRead = 0;
                    for (int m = 0; m < totalMb; m++)
                    {
                        if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                        uint readBytes;
                        if (!NativeMethods.ReadFile(hFile, alignedPtr, (uint)bufSize, out readBytes, IntPtr.Zero) || readBytes == 0)
                            break;
                        totalBytesRead += readBytes;
                        if ((m % 32) == 0 && onProgress != null)
                            onProgress(HwTestLocalization.PickFormat("Seq Read: {0}/{1} MB", "顺序读取: {0}/{1} MB", m + 1, totalMb));
                    }
                    sw.Stop();
                    double mb = totalBytesRead / (1024.0 * 1024.0);
                    return Math.Round(mb / Math.Max(0.05, sw.Elapsed.TotalSeconds), 1);
                }
            }
            finally
            {
                if (hFile != new IntPtr(-1) && hFile != IntPtr.Zero)
                    NativeMethods.CloseHandle(hFile);
                Marshal.FreeHGlobal(rawPtr);
            }

            // Fallback ako FILE_FLAG_NO_BUFFERING nije podržan (npr. virtuelni FS)
            byte[] fallback = new byte[bufSize];
            Stopwatch fsw = Stopwatch.StartNew();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, FileOptions.SequentialScan))
            {
                while (fs.Read(fallback, 0, fallback.Length) > 0)
                {
                    if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                }
            }
            fsw.Stop();
            return Math.Round(totalMb / Math.Max(0.05, fsw.Elapsed.TotalSeconds), 1);
        }

        private static double MeasureUnbuffered4KRandomRead(string path, int totalMb, int iterations, CancelFlag cancel)
        {
            const int sector4k = 4096;
            IntPtr rawPtr = Marshal.AllocHGlobal(sector4k * 2);
            IntPtr alignedPtr = new IntPtr(((rawPtr.ToInt64() + 4095L) / 4096L) * 4096L);
            IntPtr hFile = new IntPtr(-1);

            try
            {
                hFile = NativeMethods.CreateFile(
                    path,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_RANDOM_ACCESS,
                    IntPtr.Zero);

                if (hFile == new IntPtr(-1) || hFile == IntPtr.Zero)
                    return 0;

                long maxPages = ((long)totalMb * 1024L * 1024L) / sector4k;
                if (maxPages < 2) return 0;

                Random rng = new Random(1337);
                Stopwatch sw = Stopwatch.StartNew();
                int done = 0;
                for (int i = 0; i < iterations; i++)
                {
                    if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                    long offset = (long)rng.Next((int)Math.Min(int.MaxValue, maxPages - 1)) * sector4k;
                    long newPos;
                    NativeMethods.SetFilePointerEx(hFile, offset, out newPos, 0);
                    uint readBytes;
                    if (NativeMethods.ReadFile(hFile, alignedPtr, sector4k, out readBytes, IntPtr.Zero) && readBytes == sector4k)
                        done++;
                }
                sw.Stop();
                return done / Math.Max(0.02, sw.Elapsed.TotalSeconds);
            }
            finally
            {
                if (hFile != new IntPtr(-1) && hFile != IntPtr.Zero)
                    NativeMethods.CloseHandle(hFile);
                Marshal.FreeHGlobal(rawPtr);
            }
        }
        #endregion

        #region 4. RAM (Kapacitet, slotovi i Write/Read Pattern Test)
        public TestItemResult RunRamPassiveSummary()
        {
            TestItemResult res = new TestItemResult("auto_ram", HwTestLocalization.Pick("Radna memorija (RAM)", "内存 (RAM)"), HwTestLocalization.Pick("Detekcija modula/brzine i write/read verifikacija integriteta memorije", "检测内存条/频率,并通过写/读验证内存完整性"));
            res.IsAutomated = true;
            PopulateRamInventory(res);
            return res;
        }

        private void PopulateRamInventory(TestItemResult res)
        {
            ulong totalPhysMb = 0;
            ulong availPhysMb = 0;
            try
            {
                NativeMethods.MEMORYSTATUSEX ms = new NativeMethods.MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
                if (NativeMethods.GlobalMemoryStatusEx(ref ms))
                {
                    totalPhysMb = ms.ullTotalPhys / (1024UL * 1024UL);
                    availPhysMb = ms.ullAvailPhys / (1024UL * 1024UL);
                }
            }
            catch { }

            int totalSlots = 0;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        if (mo["MemoryDevices"] != null) totalSlots += Convert.ToInt32(mo["MemoryDevices"]);
                    }
                }
            }
            catch { }

            List<string> dimmList = new List<string>();
            ulong wmiTotalBytes = 0;
            int maxSpeedMhz = 0;
            int smbiosType = 0;

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        ulong cap = mo["Capacity"] != null ? Convert.ToUInt64(mo["Capacity"]) : 0;
                        wmiTotalBytes += cap;
                        int spd = 0;
                        if (mo["ConfiguredClockSpeed"] != null) spd = Convert.ToInt32(mo["ConfiguredClockSpeed"]);
                        if (spd <= 0 && mo["Speed"] != null) spd = Convert.ToInt32(mo["Speed"]);
                        if (spd > maxSpeedMhz) maxSpeedMhz = spd;
                        if (smbiosType == 0 && mo["SMBIOSMemoryType"] != null)
                            smbiosType = Convert.ToInt32(mo["SMBIOSMemoryType"]);

                        string loc = mo["DeviceLocator"] != null ? mo["DeviceLocator"].ToString().Trim() : "DIMM";
                        string mfg = mo["Manufacturer"] != null ? mo["Manufacturer"].ToString().Trim() : "";
                        string pn = mo["PartNumber"] != null ? mo["PartNumber"].ToString().Trim() : "";
                        dimmList.Add(string.Format("{0}: {1:0} GB @ {2} MHz ({3} {4})",
                            loc, cap / (1024.0 * 1024.0 * 1024.0), spd, mfg, pn).Trim());
                    }
                }
            }
            catch { }

            double totalGb = (wmiTotalBytes > 0)
                ? Math.Round(wmiTotalBytes / (1024.0 * 1024.0 * 1024.0), 1)
                : Math.Round(totalPhysMb / 1024.0, 1);

            string memTypeName = DecodeMemoryType(smbiosType);
            string slotLabel = totalSlots > 0
                ? HwTestLocalization.PickFormat("{0}/{1} slota", "{0}/{1} 个插槽", dimmList.Count, totalSlots)
                : HwTestLocalization.PickFormat("{0} modula", "{0} 条内存", dimmList.Count);

            res.Details["total_gb"] = totalGb;
            res.Details["avail_mb"] = availPhysMb;
            res.Details["dimm_count"] = dimmList.Count;
            res.Details["total_slots"] = totalSlots;
            res.Details["speed_mhz"] = maxSpeedMhz;
            res.Details["memory_type"] = memTypeName;
            res.Details["dimms"] = string.Join(" | ", dimmList.ToArray());

            res.MetricSummary = HwTestLocalization.PickFormat("{0:0.#} GB {1}{2} ({3}) | Slobodno: {4:0.0} GB | Čeka pattern test", "{0:0.#} GB {1}{2} ({3}) | 可用: {4:0.0} GB | 等待图案测试",
                totalGb, memTypeName, maxSpeedMhz > 0 ? ("-" + maxSpeedMhz) : "",
                slotLabel, availPhysMb / 1024.0);
        }

        public TestItemResult RunRamCheck(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_ram", HwTestLocalization.Pick("Radna memorija (RAM)", "内存 (RAM)"), HwTestLocalization.Pick("Detekcija modula/brzine i write/read verifikacija integriteta memorije", "检测内存条/频率,并通过写/读验证内存完整性"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            PopulateRamInventory(res);
            ulong availMb = res.Details.ContainsKey("avail_mb") ? Convert.ToUInt64(res.Details["avail_mb"]) : 512;
            double totalGb = res.Details.ContainsKey("total_gb") ? Convert.ToDouble(res.Details["total_gb"]) : 0;
            string memType = res.Details.ContainsKey("memory_type") ? res.Details["memory_type"].ToString() : "RAM";
            int speedMhz = res.Details.ContainsKey("speed_mhz") ? Convert.ToInt32(res.Details["speed_mhz"]) : 0;
            int dimmCount = res.Details.ContainsKey("dimm_count") ? Convert.ToInt32(res.Details["dimm_count"]) : 0;
            int totalSlots = res.Details.ContainsKey("total_slots") ? Convert.ToInt32(res.Details["total_slots"]) : 0;

            // Testiramo ~35% slobodne memorije (bezbedni opseg 64 MB - 1024 MB) kako ne bismo izazvali swap/OOM
            int targetMb = (int)Math.Min(1024UL, Math.Max(64UL, (availMb * 35UL) / 100UL));
            if (availMb < 128) targetMb = 32;

            const int blockMb = 16;
            const int intsPerBlock = (blockMb * 1024 * 1024) / 4; // 4,194,304 uint-a = 16 MB
            int totalBlocks = Math.Max(1, targetMb / blockMb);
            const int maxConcurrentBlocks = 16; // max 256 MB u jednoj grupi, pa oslobađanje da dotakne različite stranice

            long bitErrors = 0;
            int testedMb = 0;
            long totalBytesTransferred = 0;
            Stopwatch ioSw = Stopwatch.StartNew();

            try
            {
                int blockIdx = 0;
                while (blockIdx < totalBlocks)
                {
                    int batchCount = Math.Min(maxConcurrentBlocks, totalBlocks - blockIdx);
                    List<uint[]> batch = new List<uint[]>(batchCount);

                    for (int b = 0; b < batchCount; b++)
                    {
                        if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                        uint[] arr = new uint[intsPerBlock];
                        batch.Add(arr);
                    }

                    // 3 brza pattern prolaza na svakom bloku u grupi
                    uint[] patterns = new uint[] { 0x55555555U, 0xAAAAAAAAU, 0xA55A5AA5U };
                    for (int p = 0; p < patterns.Length; p++)
                    {
                        uint pat = patterns[p];
                        for (int b = 0; b < batch.Count; b++)
                        {
                            if (cancel != null && cancel.IsCancelled) throw new OperationCanceledException();
                            uint[] arr = batch[b];
                            uint seed = pat ^ (uint)((blockIdx + b) * 2654435761U);

                            // Write
                            for (int i = 0; i < arr.Length; i++)
                                arr[i] = seed ^ (uint)(i * 1664525U);

                            // Read & Verify
                            for (int i = 0; i < arr.Length; i++)
                            {
                                uint expected = seed ^ (uint)(i * 1664525U);
                                if (arr[i] != expected) bitErrors++;
                            }
                            totalBytesTransferred += (long)blockMb * 1024L * 1024L * 2L;
                        }
                    }

                    testedMb += batchCount * blockMb;
                    blockIdx += batchCount;
                    batch.Clear();
                    GC.Collect(2, GCCollectionMode.Forced, true);

                    if (onProgress != null)
                        onProgress(HwTestLocalization.PickFormat("RAM Pattern: {0}/{1} MB ({2} grešaka)", "RAM 图案测试: {0}/{1} MB ({2} 个错误)", testedMb, totalBlocks * blockMb, bitErrors));
                }
                ioSw.Stop();
            }
            catch (OperationCanceledException)
            {
                res.Status = TestStatus.Skip;
                res.Reason = HwTestLocalization.Pick("RAM test je prekinut od strane korisnika", "RAM 测试已被用户中断");
                res.MetricSummary = HwTestLocalization.Pick("Prekinuto", "已中断");
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }
            catch (OutOfMemoryException)
            {
                ioSw.Stop();
                if (testedMb < 32)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = HwTestLocalization.Pick("Nedovoljno slobodne memorije za izvršavanje RAM testa", "可用内存不足,无法执行 RAM 测试");
                    res.MetricSummary = "Out of Memory";
                    sw.Stop();
                    res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                    return res;
                }
            }
            catch (Exception ex)
            {
                ioSw.Stop();
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.Pick("Greška tokom RAM testa: ", "RAM 测试期间出错: ") + ex.Message;
                res.MetricSummary = HwTestLocalization.Pick("Greška testa", "测试出错");
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }

            double bwGbs = Math.Round((totalBytesTransferred / (1024.0 * 1024.0 * 1024.0)) / Math.Max(0.05, ioSw.Elapsed.TotalSeconds), 2);
            res.Details["tested_mb"] = testedMb;
            res.Details["bit_errors"] = bitErrors;
            res.Details["bandwidth_gbs"] = bwGbs;

            string slotLabel = totalSlots > 0
                ? HwTestLocalization.PickFormat("{0}/{1} slota", "{0}/{1} 个插槽", dimmCount, totalSlots)
                : HwTestLocalization.PickFormat("{0} modula", "{0} 条内存", dimmCount);
            res.MetricSummary = HwTestLocalization.PickFormat("{0:0.#} GB {1}{2} ({3}) | Testirano: {4} MB ({5:0.0} GB/s) | {6} grešaka", "{0:0.#} GB {1}{2} ({3}) | 已测试: {4} MB ({5:0.0} GB/s) | {6} 个错误",
                totalGb, memType, speedMhz > 0 ? ("-" + speedMhz) : "", slotLabel, testedMb, bwGbs, bitErrors);

            if (bitErrors > 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.PickFormat("KRITIČNO: Detektovano {0} grešaka memorije tokom Write/Read verifikacije!", "严重: 写/读验证期间检测到 {0} 个内存错误!", bitErrors);
            }
            else if (availMb < 192)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                res.Reason = HwTestLocalization.PickFormat("UPOZORENJE: Premalo slobodnog RAM-a ({0} MB); testirano samo {1} MB (0 grešaka)", "警告: 可用 RAM 过少 ({0} MB);仅测试了 {1} MB (0 个错误)", availMb, testedMb);
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = HwTestLocalization.PickFormat("RAM integritet OK (testirano {0} MB u 3 pattern prolaza, 0 grešaka)", "RAM 完整性正常 (3 轮图案测试共测试 {0} MB,0 个错误)", testedMb);
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        private static string DecodeMemoryType(int code)
        {
            switch (code)
            {
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return "RAM";
            }
        }
        #endregion

        #region 5. CPU (Multi-Core Stress, Takt, Throttling & Temperatura)
        public TestItemResult RunCpuPassiveSummary()
        {
            TestItemResult res = new TestItemResult("auto_cpu", HwTestLocalization.Pick("Procesor (CPU)", "处理器 (CPU)"), HwTestLocalization.Pick("Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature", "多核压力负载,检查频率、热降频与温度"));
            res.IsAutomated = true;
            string name = "Procesor";
            int cores = 0;
            int logical = Environment.ProcessorCount;
            int maxClock = 0;
            ReadCpuWmi(out name, out cores, out logical, out maxClock);

            ThermalSensorEngine eng = EnsureSensorsAndUpdate();
            float temp = eng != null ? eng.CpuTemperature : 0f;

            res.Details["cpu_name"] = name;
            res.Details["cores"] = cores;
            res.Details["logical_processors"] = logical;
            res.Details["base_clock_mhz"] = maxClock;
            if (temp > 0) res.Details["idle_temp_c"] = Math.Round(temp, 1);

            string tStr = temp > 0 ? HwTestLocalization.PickFormat(" | Idle: {0:0}°C", " | 空闲: {0:0}°C", temp) : "";
            res.MetricSummary = HwTestLocalization.PickFormat("{0} ({1}C/{2}T){3} | Čeka stres test", "{0} ({1}C/{2}T){3} | 等待压力测试", ShortModel(name, 30), cores, logical, tStr);
            return res;
        }

        public TestItemResult RunCpuStress(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_cpu", HwTestLocalization.Pick("Procesor (CPU)", "处理器 (CPU)"), HwTestLocalization.Pick("Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature", "多核压力负载,检查频率、热降频与温度"));
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            string cpuName;
            int cores;
            int logical;
            int baseClockMhz;
            ReadCpuWmi(out cpuName, out cores, out logical, out baseClockMhz);

            int threadCount = Math.Max(1, Environment.ProcessorCount);
            const int durationSec = 10;
            CancelFlag workerStop = new CancelFlag();
            int workerCrashCount = 0;
            long totalIterations = 0;

            ThermalSensorEngine eng = EnsureSensorsAndUpdate();
            if (eng != null)
            {
                try { eng.ResetCoreClockExtremes(); }
                catch { }
            }

            Thread[] workers = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                workers[t] = new Thread(() =>
                {
                    try
                    {
                        double acc = 1.000001 + (tid * 0.001);
                        ulong hash = 0x9E3779B97F4A7C15UL ^ (uint)tid;
                        long localIter = 0;
                        while (!workerStop.IsCancelled)
                        {
                            for (int k = 0; k < 4000; k++)
                            {
                                acc = Math.Sqrt(acc * acc + 1.234567) + Math.Sin(k * 0.013);
                                if (acc > 1e6 || double.IsNaN(acc)) acc = 1.000001;
                                hash = (hash * 6364136223846793005UL) ^ (ulong)(k + 1);
                            }
                            localIter++;
                        }
                        if (double.IsNaN(acc)) Interlocked.Increment(ref workerCrashCount);
                        Interlocked.Add(ref totalIterations, localIter);
                    }
                    catch
                    {
                        Interlocked.Increment(ref workerCrashCount);
                    }
                });
                workers[t].IsBackground = true;
                workers[t].Priority = ThreadPriority.BelowNormal;
                workers[t].Start();
            }

            float peakTemp = 0f;
            float minTemp = 0f;
            int tempSamples = 0;
            float avgClock = 0f;
            int clockSamples = 0;
            float maxThrottle = 0f;
            int maxFanRpm = 0;
            int consecutiveOverheat = 0;
            bool thermalAbort = false;
            bool userCancelled = false;

            int sensorBusy = 0;
            Stopwatch loopSw = Stopwatch.StartNew();
            while (loopSw.Elapsed.TotalSeconds < durationSec)
            {
                if (cancel != null && cancel.IsCancelled)
                {
                    userCancelled = true;
                    break;
                }

                Thread.Sleep(500);
                float curTemp = 0f;
                float curClock = 0f;
                float curThrottle = 0f;

                if (eng != null)
                {
                    if (Interlocked.CompareExchange(ref sensorBusy, 1, 0) == 0)
                    {
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { eng.Update(); }
                            catch { }
                            finally { Interlocked.Exchange(ref sensorBusy, 0); }
                        });
                    }
                    try
                    {
                        curTemp = eng.CpuTemperature;
                        curThrottle = eng.ThrottlingPercentage;
                        if (eng.FanRpm > maxFanRpm) maxFanRpm = eng.FanRpm;

                        List<CoreClockSensorInfo> clocks = eng.GetCoreClocksSnapshot();
                        if (clocks != null && clocks.Count > 0)
                        {
                            float sum = 0f;
                            int cnt = 0;
                            for (int i = 0; i < clocks.Count; i++)
                            {
                                if (clocks[i].CurrentMHz > 100f)
                                {
                                    sum += clocks[i].CurrentMHz;
                                    cnt++;
                                }
                            }
                            if (cnt > 0) curClock = sum / cnt;
                        }
                    }
                    catch { }
                }

                if (curTemp > 0f)
                {
                    if (curTemp > peakTemp) peakTemp = curTemp;
                    if (minTemp <= 0f || curTemp < minTemp) minTemp = curTemp;
                    tempSamples++;
                }
                if (curThrottle > maxThrottle) maxThrottle = curThrottle;
                if (curClock > 0)
                {
                    avgClock += curClock;
                    clockSamples++;
                }

                if (curTemp >= 97.0f)
                {
                    thermalAbort = true;
                    break;
                }
                if (curTemp >= 95.0f)
                {
                    consecutiveOverheat++;
                    if (consecutiveOverheat >= 2)
                    {
                        thermalAbort = true;
                        break;
                    }
                }
                else
                {
                    consecutiveOverheat = 0;
                }

                if (onProgress != null)
                {
                    int secLeft = Math.Max(0, durationSec - (int)loopSw.Elapsed.TotalSeconds);
                    string tempLabel = curTemp > 0 ? string.Format("{0:0}°C", curTemp) : "N/A";
                    string clkLabel = curClock > 0 ? string.Format("{0:0} MHz", curClock) : (baseClockMhz + " MHz");
                    onProgress(HwTestLocalization.PickFormat("CPU Stres ({0}T): {1} | {2} | {3}s", "CPU 压力测试 ({0}T): {1} | {2} | {3}s", threadCount, tempLabel, clkLabel, secLeft));
                }
            }

            workerStop.Cancel();
            for (int t = 0; t < workers.Length; t++)
            {
                try { workers[t].Join(1000); }
                catch { }
            }

            if (userCancelled)
            {
                res.Status = TestStatus.Skip;
                res.Reason = HwTestLocalization.Pick("CPU stres test je prekinut od strane korisnika", "CPU 压力测试已被用户中断");
                res.MetricSummary = HwTestLocalization.Pick("Prekinuto", "已中断");
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }

            float meanClockMhz = clockSamples > 0 ? (avgClock / clockSamples) : 0f;
            string rawSensorSrc = eng != null ? eng.SensorSource : "NONE";
            bool staticAcpiZone = (
                string.Equals(rawSensorSrc, "WMI_ACPI", StringComparison.OrdinalIgnoreCase) &&
                tempSamples >= 4 &&
                peakTemp > 0f &&
                peakTemp <= 32.0f &&
                (peakTemp - minTemp) < 0.5f
            );
            float rawAcpiTemp = peakTemp;
            if (staticAcpiZone)
            {
                peakTemp = 0f;
            }

            res.Details["cpu_name"] = cpuName;
            res.Details["cores"] = cores;
            res.Details["logical_processors"] = logical;
            res.Details["base_clock_mhz"] = baseClockMhz;
            res.Details["avg_clock_mhz"] = Math.Round(meanClockMhz, 0);
            res.Details["peak_temp_c"] = Math.Round(peakTemp, 1);
            res.Details["max_throttle_pct"] = Math.Round(maxThrottle, 1);
            res.Details["max_fan_rpm"] = maxFanRpm;
            res.Details["worker_errors"] = workerCrashCount;
            res.Details["iterations"] = totalIterations;
            res.Details["sensor_source"] = staticAcpiZone ? "WMI_ACPI_STATIC" : rawSensorSrc;
            if (staticAcpiZone)
            {
                res.Details["sensor_static_acpi"] = true;
                res.Details["raw_acpi_temp_c"] = Math.Round(rawAcpiTemp, 1);
            }

            string tempStr = peakTemp > 0
                ? HwTestLocalization.PickFormat("Peak: {0:0}°C", "峰值: {0:0}°C", peakTemp)
                : (staticAcpiZone ? HwTestLocalization.Pick("Temp: N/A (ACPI static)", "温度: 不可用 (ACPI 静态)") : "Temp: N/A");
            string clkStr = meanClockMhz > 0 ? HwTestLocalization.PickFormat(" | Takt: {0:0} MHz", " | 频率: {0:0} MHz", meanClockMhz) : HwTestLocalization.PickFormat(" | Baza: {0} MHz", " | 基础频率: {0} MHz", baseClockMhz);
            string fanStr = maxFanRpm > 0 ? HwTestLocalization.PickFormat(" | Fan: {0} RPM", " | 风扇: {0} RPM", maxFanRpm) : "";
            res.MetricSummary = string.Format("{0} ({1}T) | {2}{3}{4}", ShortModel(cpuName, 24), threadCount, tempStr, clkStr, fanStr);

            bool lowCoolTemp = (peakTemp > 0f && peakTemp < 75.0f);
            bool severeThrottle = (baseClockMhz > 500 && meanClockMhz > 100 && meanClockMhz < (baseClockMhz * 0.45f) && peakTemp >= 85f);
            bool belowBaseModerate = lowCoolTemp
                ? (baseClockMhz > 500 && meanClockMhz > 100 && meanClockMhz < Math.Min(900f, baseClockMhz * 0.38f))
                : (baseClockMhz > 500 && meanClockMhz > 100 && meanClockMhz < (baseClockMhz * 0.72f));
            bool sensorThrottleWarn = (maxThrottle >= 25f) && (peakTemp >= 80.0f || peakTemp <= 0f);
            bool moderateThrottle = belowBaseModerate || sensorThrottleWarn;

            if (workerCrashCount > 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.PickFormat("Nestabilnost procesora: {0} radnih niti je prijavilo grešku!", "处理器不稳定: {0} 个工作线程报告错误!", workerCrashCount);
            }
            else if (thermalAbort || peakTemp >= 95.0f)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.PickFormat("KRITIČNO PREGREVANJE CPU-a: {0:0.0}°C (>= 95°C) — potrebna provera hlađenja/paste!", "严重: CPU 过热 {0:0.0}°C (>= 95°C) — 需要检查散热器/硅脂!", peakTemp);
            }
            else if (severeThrottle)
            {
                res.Status = TestStatus.Fail;
                res.Reason = HwTestLocalization.PickFormat("Ekstreman thermal throttling: takt pao na {0:0} MHz (baza {1} MHz) uz {2:0}°C", "严重热降频: 频率降至 {0:0} MHz (基础 {1} MHz),温度 {2:0}°C", meanClockMhz, baseClockMhz, peakTemp);
            }
            else if (peakTemp >= 85.0f || moderateThrottle || staticAcpiZone)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                List<string> warns = new List<string>();
                if (peakTemp >= 85.0f) warns.Add(HwTestLocalization.PickFormat("Povišena temperatura pod opterećenjem ({0:0}°C)", "负载下温度偏高 ({0:0}°C)", peakTemp));
                if (moderateThrottle) warns.Add(HwTestLocalization.PickFormat("Detektovan umereni throttling (takt {0:0}/{1} MHz, throttle {2:0}%)", "检测到轻度降频 (频率 {0:0}/{1} MHz,降频 {2:0}%)", meanClockMhz, baseClockMhz, maxThrottle));
                if (staticAcpiZone) warns.Add(HwTestLocalization.PickFormat("Statična ACPI zona ({0:0.0}°C) — pravi CPU termalni senzor nedostupan", "ACPI 静态温区 ({0:0.0}°C) — 无法获取真实 CPU 温度传感器", rawAcpiTemp));
                res.Reason = HwTestLocalization.Pick("UPOZORENJE: ", "警告: ") + string.Join("; ", warns.ToArray());
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = peakTemp > 0
                    ? HwTestLocalization.PickFormat("CPU stabilan pod 100% opterećenjem ({0} niti, max {1:0}°C)", "CPU 在 100% 负载下稳定 ({0} 线程,最高 {1:0}°C)", threadCount, peakTemp)
                    : HwTestLocalization.PickFormat("CPU stabilan pod 100% opterećenjem ({0} niti, 0 grešaka; temp senzor nedostupan)", "CPU 在 100% 负载下稳定 ({0} 线程,0 个错误;温度传感器不可用)", threadCount);
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        private static void ReadCpuWmi(out string name, out int cores, out int logical, out int maxClock)
        {
            name = "CPU";
            cores = 0;
            logical = Environment.ProcessorCount;
            maxClock = 0;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        if (mo["Name"] != null) name = mo["Name"].ToString().Trim();
                        if (mo["NumberOfCores"] != null) cores += Convert.ToInt32(mo["NumberOfCores"]);
                        if (mo["NumberOfLogicalProcessors"] != null) logical = Convert.ToInt32(mo["NumberOfLogicalProcessors"]);
                        if (mo["MaxClockSpeed"] != null) maxClock = Math.Max(maxClock, Convert.ToInt32(mo["MaxClockSpeed"]));
                    }
                }
            }
            catch { }
            if (cores <= 0) cores = logical;
        }
        #endregion

        #region 6. GPU Pasivna inventarizacija
        public TestItemResult RunGpuPassiveSummary()
        {
            TestItemResult res = new TestItemResult("auto_gpu", HwTestLocalization.Pick("Grafika (GPU)", "显卡 (GPU)"), HwTestLocalization.Pick("Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)", "检测驱动、显存 (VRAM) 与 3D 硬件渲染 (WPF Direct3D)"));
            res.IsAutomated = true;
            string primaryGpu;
            string driverVer;
            long vramMb;
            uint errCode;
            bool isBasicAdapter;
            string allGpus;
            bool hasAnyBasicDisplay;
            bool hasAnyErrorAdapter;
            string problemAdapters;
            InspectGpuAdapters(
                out primaryGpu, out driverVer, out vramMb, out errCode, out isBasicAdapter, out allGpus,
                out hasAnyBasicDisplay, out hasAnyErrorAdapter, out problemAdapters);

            int tier = (System.Windows.Media.RenderCapability.Tier >> 16);
            res.Details["gpu_name"] = primaryGpu;
            res.Details["driver_version"] = driverVer;
            res.Details["vram_mb"] = vramMb;
            res.Details["pnp_error_code"] = errCode;
            res.Details["is_basic_display"] = isBasicAdapter;
            res.Details["has_any_basic_display"] = hasAnyBasicDisplay;
            res.Details["has_any_error_adapter"] = hasAnyErrorAdapter;
            res.Details["problem_adapters"] = problemAdapters;
            res.Details["wpf_render_tier"] = tier;
            res.Details["all_adapters"] = allGpus;

            string vramTxt = vramMb > 0 ? HwTestLocalization.PickFormat(" | VRAM: {0} MB", " | 显存: {0} MB", vramMb) : "";
            res.MetricSummary = HwTestLocalization.PickFormat("{0}{1} | Tier {2} | Čeka 3D stres", "{0}{1} | Tier {2} | 等待 3D 压力测试", ShortModel(primaryGpu, 26), vramTxt, tier);

            if (isBasicAdapter || errCode != 0 || hasAnyBasicDisplay || hasAnyErrorAdapter)
            {
                res.Status = TestStatus.Fail;
                if (isBasicAdapter)
                {
                    res.Reason = HwTestLocalization.Pick("Aktivan je Microsoft Basic Display Adapter (nedostaje grafički drajver!)", "当前使用 Microsoft Basic Display Adapter (缺少显卡驱动!)");
                }
                else if (errCode != 0)
                {
                    res.Reason = HwTestLocalization.PickFormat("Greška GPU uređaja u Device Manager-u (Error Code {0})", "设备管理器中 GPU 设备报错 (Error Code {0})", errCode);
                }
                else
                {
                    res.Reason = HwTestLocalization.PickFormat("Sekundarni GPU nema drajver ili ima grešku: {0}", "第二块 GPU 缺少驱动或报错: {0}", problemAdapters);
                }
            }
            return res;
        }

        public static bool TryValidateGpuDriverForStress(out TestItemResult blockedResult)
        {
            string primaryGpu;
            string driverVer;
            long vramMb;
            uint errCode;
            bool isBasicAdapter;
            string allGpus;
            bool hasAnyBasicDisplay;
            bool hasAnyErrorAdapter;
            string problemAdapters;

            InspectGpuAdapters(
                out primaryGpu, out driverVer, out vramMb, out errCode, out isBasicAdapter, out allGpus,
                out hasAnyBasicDisplay, out hasAnyErrorAdapter, out problemAdapters);

            int renderTier = 0;
            try
            {
                System.Windows.Application app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.Invoke(new Action(delegate { renderTier = (System.Windows.Media.RenderCapability.Tier >> 16); }));
                }
                else
                {
                    renderTier = (System.Windows.Media.RenderCapability.Tier >> 16);
                }
            }
            catch { }

            if (isBasicAdapter || hasAnyBasicDisplay || errCode != 0 || hasAnyErrorAdapter || renderTier == 0)
            {
                blockedResult = new TestItemResult("auto_gpu", HwTestLocalization.Pick("Grafika (GPU)", "显卡 (GPU)"), HwTestLocalization.Pick("Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)", "检测驱动、显存 (VRAM) 与 3D 硬件渲染 (WPF Direct3D)"));
                blockedResult.IsAutomated = true;
                blockedResult.Status = TestStatus.Fail;
                blockedResult.DurationSeconds = 0;
                blockedResult.Attempts = 1;
                blockedResult.Details["gpu_name"] = primaryGpu;
                blockedResult.Details["driver_version"] = driverVer;
                blockedResult.Details["vram_mb"] = vramMb;
                blockedResult.Details["pnp_error_code"] = errCode;
                blockedResult.Details["is_basic_display"] = isBasicAdapter;
                blockedResult.Details["has_any_basic_display"] = hasAnyBasicDisplay;
                blockedResult.Details["has_any_error_adapter"] = hasAnyErrorAdapter;
                blockedResult.Details["problem_adapters"] = problemAdapters;
                blockedResult.Details["wpf_render_tier"] = renderTier;
                blockedResult.Details["all_adapters"] = allGpus;
                blockedResult.Details["gpu_test_blocked_no_driver"] = true;

                string shortGpu = ShortModel(primaryGpu, 26);
                string vramTxt = vramMb > 0 ? HwTestLocalization.PickFormat(" | VRAM: {0} MB", " | 显存: {0} MB", vramMb) : "";
                blockedResult.MetricSummary = HwTestLocalization.PickFormat("{0}{1} | Tier {2} | Blokiran (nema drajver)", "{0}{1} | Tier {2} | 已阻止 (无驱动)", shortGpu, vramTxt, renderTier);

                if (isBasicAdapter)
                {
                    blockedResult.Reason = HwTestLocalization.Pick("GPU 3D test ne može da se pokrene: aktivan je Microsoft Basic Display Adapter (nedostaje originalni grafički drajver!)", "无法运行 GPU 3D 测试: 当前使用 Microsoft Basic Display Adapter (缺少原厂显卡驱动!)");
                }
                else if (errCode != 0)
                {
                    blockedResult.Reason = HwTestLocalization.PickFormat("GPU 3D test ne može da se pokrene: greška grafičke kartice u Device Manager-u (Error Code {0})", "无法运行 GPU 3D 测试: 设备管理器中显卡报错 (Error Code {0})", errCode);
                }
                else if (hasAnyBasicDisplay || hasAnyErrorAdapter)
                {
                    blockedResult.Reason = HwTestLocalization.PickFormat("GPU 3D test ne može da se pokrene: sekundarni GPU nema drajver ili ima grešku: {0}", "无法运行 GPU 3D 测试: 第二块 GPU 缺少驱动或报错: {0}", problemAdapters);
                }
                else
                {
                    blockedResult.Reason = HwTestLocalization.Pick("GPU 3D test ne može da se pokrene: Direct3D hardverska akceleracija nije aktivna (Render Tier 0)!", "无法运行 GPU 3D 测试: Direct3D 硬件加速未启用 (Render Tier 0)!");
                }
                return false;
            }

            blockedResult = null;
            return true;
        }

        public static void InspectGpuAdapters(out string primaryName, out string driverVersion, out long vramMb, out uint errorCode, out bool isBasicDisplay, out string allAdaptersSummary)
        {
            bool dummyBasic;
            bool dummyErr;
            string dummyProblem;
            InspectGpuAdapters(
                out primaryName, out driverVersion, out vramMb, out errorCode, out isBasicDisplay, out allAdaptersSummary,
                out dummyBasic, out dummyErr, out dummyProblem);
        }

        public static void InspectGpuAdapters(
            out string primaryName,
            out string driverVersion,
            out long vramMb,
            out uint errorCode,
            out bool isBasicDisplay,
            out string allAdaptersSummary,
            out bool hasAnyBasicDisplay,
            out bool hasAnyErrorAdapter,
            out string problemAdaptersSummary)
        {
            primaryName = HwTestLocalization.Pick("Nepoznat GPU", "未知 GPU");
            driverVersion = "";
            vramMb = 0;
            errorCode = 0;
            isBasicDisplay = false;
            hasAnyBasicDisplay = false;
            hasAnyErrorAdapter = false;
            List<string> list = new List<string>();
            List<string> problemList = new List<string>();

            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, AdapterRAM, ConfigManagerErrorCode, PNPDeviceID FROM Win32_VideoController"))
                using (ManagementObjectCollection col = s.Get())
                {
                    bool setPrimary = false;
                    foreach (ManagementObject mo in col)
                    {
                        string name = mo["Name"] != null ? mo["Name"].ToString().Trim() : "GPU";
                        string drv = mo["DriverVersion"] != null ? mo["DriverVersion"].ToString().Trim() : "";
                        uint err = mo["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(mo["ConfigManagerErrorCode"]) : 0;
                        long ramBytes = mo["AdapterRAM"] != null ? Convert.ToInt64(mo["AdapterRAM"]) : 0;
                        long mb = ramBytes > 0 ? (ramBytes / (1024L * 1024L)) : 0;

                        bool isBasic = name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       name.IndexOf("Standard VGA", StringComparison.OrdinalIgnoreCase) >= 0;

                        list.Add(HwTestLocalization.PickFormat("{0} (Drv: {1}, Err: {2})", "{0} (驱动: {1}, Err: {2})", name, drv, err));
                        if (isBasic)
                        {
                            hasAnyBasicDisplay = true;
                        }
                        if (err != 0)
                        {
                            hasAnyErrorAdapter = true;
                        }
                        if (isBasic || err != 0)
                        {
                            problemList.Add(HwTestLocalization.PickFormat("{0} (Err: {1})", "{0} (Err: {1})", name, err));
                        }

                        // Prioritet dajemo pravoj kartici (ne Basic Display), a ako su obe prave uzimamo prvu ili sa većim VRAM-om
                        if (!setPrimary || (!isBasic && isBasicDisplay) || (!isBasic && mb > vramMb))
                        {
                            primaryName = name;
                            driverVersion = drv;
                            vramMb = mb;
                            errorCode = err;
                            isBasicDisplay = isBasic;
                            setPrimary = true;
                        }
                    }
                }
            }
            catch { }

            // Dopuna za >4 GB VRAM iz Registry Video ključa ako je Win32_VideoController ograničen na ~4 GB
            try
            {
                using (Microsoft.Win32.RegistryKey baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (baseKey != null)
                    {
                        foreach (string sub in baseKey.GetSubKeyNames())
                        {
                            using (Microsoft.Win32.RegistryKey sk = baseKey.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                object desc = sk.GetValue("DriverDesc");
                                object qwMem = sk.GetValue("HardwareInformation.qwMemorySize");
                                if (desc != null && qwMem != null && desc.ToString().Equals(primaryName, StringComparison.OrdinalIgnoreCase))
                                {
                                    long realMb = 0;
                                    if (qwMem is long) realMb = ((long)qwMem) / (1024L * 1024L);
                                    else if (qwMem is byte[])
                                    {
                                        byte[] b = (byte[])qwMem;
                                        if (b.Length >= 8) realMb = BitConverter.ToInt64(b, 0) / (1024L * 1024L);
                                    }
                                    if (realMb > vramMb) vramMb = realMb;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            allAdaptersSummary = string.Join(" | ", list.ToArray());
            problemAdaptersSummary = string.Join(" | ", problemList.ToArray());
        }
        #endregion

        private static string ShortModel(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= maxLen ? s : (s.Substring(0, maxLen - 1) + "…");
        }
    }
}
