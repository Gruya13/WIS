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
        private ThermalSensorEngine _sensorEngine;
        private bool _sensorInitAttempted;
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
            ThermalSensorEngine eng = GetSensors();
            if (eng != null)
            {
                try { eng.Update(); }
                catch { }
            }
            return eng;
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
            TestItemResult res = new TestItemResult("auto_security", "BIOS, TPM 2.0 i PnP Drajveri",
                "Provera BIOS lozinke, Computrace statusa, TPM 2.0 čipa, OA3 licence i žutih uzvičnika u Device Manageru");
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            bool biosPassLocked = false;
            string biosPassDetail = "Otključan (bez lozinke)";
            bool computraceActive = false;
            string computraceStatus = "Neaktivan / Nije detektovan";
            bool tpmPresent = false;
            bool tpmEnabled = false;
            bool tpmActivated = false;
            string tpmVersion = "";
            string secureBootStr = "Nepoznat";
            bool hasOa3Key = false;
            List<string> pnpErrorDevices = new List<string>();

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
                                        biosPassDetail = string.Format("ZAKLJUČAN (Lenovo PasswordState={0})", state);
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
                                    biosPassDetail = "ZAKLJUČAN (Dell " + (string.IsNullOrEmpty(aName) ? "BIOS Password" : aName) + ")";
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
                                secureBootStr = (sb == 1) ? "Uključen (Enabled)" : "Isključen (Disabled)";
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
                            if (string.IsNullOrEmpty(name)) name = devId;
                            if (IsIgnorableVirtualPnpDevice(name, devId)) continue;

                            pnpErrorDevices.Add(string.Format("{0} [Code {1}]", name, err));
                        }
                    }
                }
                catch { }

                bool tpmOk = tpmPresent && tpmEnabled && tpmActivated;
                string tpmStatusStr = !tpmPresent
                    ? "Nije detektovan / Isključen u BIOS-u"
                    : (tpmOk
                        ? string.Format("Aktivan ({0})", string.IsNullOrEmpty(tpmVersion) ? "OK" : ("v" + tpmVersion))
                        : string.Format("Prisutan ali neaktivan (Enabled={0}, Activated={1})", tpmEnabled, tpmActivated));

                res.Details["bios_password_locked"] = biosPassLocked;
                res.Details["bios_password_state"] = biosPassDetail;
                res.Details["computrace_active"] = computraceActive;
                res.Details["computrace_status"] = computraceStatus;
                res.Details["tpm_status"] = tpmStatusStr;
                res.Details["secure_boot"] = secureBootStr;
                res.Details["oa3_oem_key"] = hasOa3Key ? "Prisutan u BIOS-u (OA3)" : "Nije pronađen";
                res.Details["pnp_error_count"] = pnpErrorDevices.Count;
                res.Details["pnp_errors"] = pnpErrorDevices.Count > 0 ? string.Join(" | ", pnpErrorDevices.ToArray()) : "Nema grešaka (0)";

                string tpmBadge = tpmOk ? (string.IsNullOrEmpty(tpmVersion) ? "OK" : ("v" + tpmVersion)) : "Neaktivan";
                res.MetricSummary = string.Format("BIOS: {0} | Computrace: {1} | TPM: {2} | PnP greške: {3}",
                    biosPassLocked ? "ZAKLJUČAN" : "Otključan",
                    computraceActive ? "AKTIVAN" : "OK",
                    tpmBadge,
                    pnpErrorDevices.Count);

                if (biosPassLocked || computraceActive)
                {
                    res.Status = TestStatus.Fail;
                    List<string> criticals = new List<string>();
                    if (biosPassLocked) criticals.Add(biosPassDetail);
                    if (computraceActive) criticals.Add(computraceStatus);
                    res.Reason = "KRITIČNO: " + string.Join("; ", criticals.ToArray());
                }
                else if (pnpErrorDevices.Count > 0 || !tpmOk)
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = true;
                    List<string> warns = new List<string>();
                    if (pnpErrorDevices.Count > 0)
                        warns.Add(string.Format("Device Manager ima {0} uređaj(a) sa greškom: {1}", pnpErrorDevices.Count, string.Join(", ", pnpErrorDevices.ToArray())));
                    if (!tpmOk)
                        warns.Add("TPM 2.0 nije aktivan (" + tpmStatusStr + ")");
                    res.Reason = "UPOZORENJE: " + string.Join("; ", warns.ToArray());
                }
                else
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = false;
                    res.Reason = string.Format("BIOS otključan, {0}, TPM {1}, Device Manager čist (0 grešaka)",
                        hasOa3Key ? "OA3 licenca prisutna" : "bez OA3 ključa", tpmBadge);
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = "Greška bezbednosne / PnP provere: " + ex.Message;
                res.MetricSummary = "Greška očitavanja";
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
                    biosPassDetail = string.Format("ZAKLJUČAN ({0}={1})", key, val);
                }
            }

            if (uk.Contains("COMPUTRACE") || uk.Contains("ABSOLUTE PERSISTENCE") || uk.Contains("ABSOLUTE MODULE"))
            {
                if (uv.Contains("ACTIVATED") || uv == "ACTIVATE" || uv == "ENABLE" || uv == "ENABLED" || uv == "ACTIVE")
                {
                    if (!uv.Contains("DISABLE") && !uv.Contains("DEACTIVATE") && !uv.Contains("INACTIVE"))
                    {
                        computraceActive = true;
                        computraceStatus = string.Format("AKTIVAN ({0}={1})", key, val);
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
            TestItemResult res = new TestItemResult("auto_battery", "Baterija i napajanje", "Provera zdravlja baterije, kapaciteta (Design vs Full Charge) i AC punjača");
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
                    res.Reason = "Uređaj nema bateriju (Desktop računar ili baterija nije povezana)";
                    res.MetricSummary = acOnline ? "AC napajanje aktivno | Nema baterije" : "Nema baterije";
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
                if (chargeWatts > 0.1) ratePart = string.Format(" | Puni: {0:0.0}W", chargeWatts);
                else if (dischargeWatts > 0.1) ratePart = string.Format(" | Troši: {0:0.0}W", dischargeWatts);
                else if (acOnline && hasBatteryStatusRate) ratePart = " | Punjenje: 0.0W";
                string voltPart = voltageV > 1.0 ? string.Format(" ({0:0.0}V)", voltageV) : "";

                if (designMwh > 0 && fullMwh > 0)
                {
                    double healthPct = Math.Min(100.0, Math.Max(0.0, Math.Round((fullMwh / designMwh) * 100.0, 1)));
                    double wearPct = Math.Round(100.0 - healthPct, 1);
                    res.Details["health_percent"] = healthPct;
                    res.Details["wear_percent"] = wearPct;

                    string cyclePart = (cycleCount >= 0) ? string.Format(" | Ciklusi: {0}", cycleCount) : "";
                    string chargePart = (chargePct >= 0) ? string.Format(" | Napunjenost: {0}%", chargePct) : "";
                    res.MetricSummary = string.Format("Zdravlje: {0:0}% ({1:0.0}/{2:0.0} Wh) | Wear: {3:0}%{4}{5}{6}{7} | AC: {8}",
                        healthPct, fullMwh / 1000.0, designMwh / 1000.0, wearPct, cyclePart, chargePart,
                        ratePart, voltPart, acOnline ? "Online" : "Baterija");

                    if (criticalStatus || healthPct < 40.0)
                    {
                        res.Status = TestStatus.Fail;
                        res.Reason = criticalStatus
                            ? string.Format("Kritičan WMI status baterije ({0}), zdravlje {1:0}%", batteryStatusText, healthPct)
                            : string.Format("Baterija je istrošena (zdravlje {0:0}% < 40% minimuma)", healthPct);
                    }
                    else if (healthPct < 65.0 || acNotChargingWarn)
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = true;
                        List<string> bw = new List<string>();
                        if (healthPct < 65.0) bw.Add(string.Format("Smanjen kapacitet baterije (zdravlje {0:0}%, wear {1:0}%)", healthPct, wearPct));
                        if (acNotChargingWarn) bw.Add(string.Format("Punjač je priključen ({0}%), ali baterija ne prima struju (ChargeRate 0 W)", chargePct));
                        res.Reason = "UPOZORENJE: " + string.Join("; ", bw.ToArray());
                    }
                    else
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = false;
                        res.Reason = string.Format("Baterija ispravna (zdravlje {0:0}%{1})", healthPct,
                            chargeWatts > 0.1 ? string.Format(", punjenje {0:0.0}W", chargeWatts) : "");
                    }
                }
                else
                {
                    res.MetricSummary = string.Format("{0} | Napunjenost: {1}%{2}{3} | AC: {4} (kapacitet nedostupan)",
                        string.IsNullOrEmpty(batteryName) ? "Baterija" : batteryName,
                        chargePct >= 0 ? chargePct.ToString() : "?",
                        ratePart, voltPart,
                        acOnline ? "Online" : "Baterija");

                    if (criticalStatus)
                    {
                        res.Status = TestStatus.Fail;
                        res.Reason = "Kritičan status baterije: " + batteryStatusText;
                    }
                    else
                    {
                        res.Status = TestStatus.Pass;
                        res.HasWarning = true;
                        res.Reason = acNotChargingWarn
                            ? "UPOZORENJE: Firmware ne prijavljuje Design/Full kapacitet i AC ne puni bateriju (0 W)"
                            : "UPOZORENJE: Firmware ne prijavljuje Design/Full kapacitet baterije preko WMI";
                    }
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = "Greška pri očitavanju baterije: " + ex.Message;
                res.MetricSummary = "Greška očitavanja";
            }

            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }
        #endregion

        #region 2. Mreža (Wi-Fi / LAN & ServisHub)
        public TestItemResult RunNetworkCheck(ModelProfileClient profileClient)
        {
            TestItemResult res = new TestItemResult("auto_network", "Mreža (Wi-Fi / LAN & Hub)", "Provera mrežnih adaptera, link brzine, gateway-a i veze sa ServisHub serverom");
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
                            string name = mo["Name"] != null ? mo["Name"].ToString() : "Nepoznat adapter";
                            if (IsVirtualAdapterName(name)) continue;
                            uint err = mo["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(mo["ConfigManagerErrorCode"]) : 0;
                            if (err != 0 && err != 22) // 22 = Disabled by user, ostalo je stvaran kvar drajvera
                            {
                                pnpErrors.Add(string.Format("{0} (Code {1})", name, err));
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
                        ? string.Format("Povezan ({0} Mbps{1})", mbps, string.IsNullOrEmpty(ipv4) ? "" : ", " + ipv4)
                        : "Nije povezan";

                    ifaceSummaries.Add(string.Format("{0}: {1}", ni.Description, stateStr));
                    if (isWifi)
                    {
                        wifiCount++;
                        if (up) wifiUp = true;
                        if (wifiInfo == "Nema" || up)
                            wifiInfo = up ? string.Format("{0} Mbps", mbps) : "Spreman";
                    }
                    else if (isLan)
                    {
                        lanCount++;
                        if (lanInfo == "Nema" || up)
                            lanInfo = up ? string.Format("{0} Mbps", mbps) : "Spreman";
                    }
                }

                // 3. Provera dostupnosti ServisHub-a i gateway-a
                string hubUrl = (profileClient != null && !string.IsNullOrEmpty(profileClient.HubBaseUrl))
                    ? profileClient.HubBaseUrl
                    : "http://192.168.170.254:8080";
                bool hubOk = false;
                long hubMs = -1;
                string hubStatusMsg = "Nedostupan";

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
                        hubStatusMsg = "Nedostupan (" + hex.Message + ")";
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
                if (wifiCount > 0)
                {
                    ProbeWifiSignalAndNetworks(out wifiVisibleNetworks, out wifiMaxSignalPct);
                }

                bool btPresent;
                bool btOk;
                string btSummary;
                ProbeBluetoothStatus(out btPresent, out btOk, out btSummary);

                res.Details["wifi_adapters"] = wifiCount;
                res.Details["lan_adapters"] = lanCount;
                res.Details["wifi_visible_networks"] = wifiVisibleNetworks;
                res.Details["wifi_max_signal_pct"] = wifiMaxSignalPct;
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

                string wifiSigTxt = (wifiCount > 0 && (wifiMaxSignalPct > 0 || wifiVisibleNetworks > 0))
                    ? string.Format(" ({0}%, {1} mr.)", wifiMaxSignalPct, wifiVisibleNetworks)
                    : "";
                string btBadge = btPresent ? (btOk ? "OK" : "GREŠKA") : "Nema";
                res.MetricSummary = string.Format("Wi-Fi: {0}{1} | LAN: {2} | BT: {3} | Hub: {4}",
                    wifiInfo, wifiSigTxt, lanInfo, btBadge, anyUp ? hubStatusMsg : "Offline");

                bool noWifiSignalAtAll = (wifiCount > 0 && !wifiUp && wifiVisibleNetworks == 0 && wifiMaxSignalPct == 0);
                bool weakWifiAntenna = (wifiCount > 0 && ((wifiVisibleNetworks <= 1 && wifiMaxSignalPct > 0 && wifiMaxSignalPct < 35) || noWifiSignalAtAll));

                if (pnpErrors.Count > 0 || (btPresent && !btOk))
                {
                    res.Status = TestStatus.Fail;
                    List<string> failParts = new List<string>();
                    if (pnpErrors.Count > 0) failParts.Add("Greška mrežnog drajvera: " + string.Join(", ", pnpErrors.ToArray()));
                    if (btPresent && !btOk) failParts.Add("Bluetooth greška: " + btSummary);
                    res.Reason = string.Join("; ", failParts.ToArray());
                }
                else if (wifiCount == 0 && lanCount == 0)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = "Nije pronađen nijedan fizički Wi-Fi ili LAN adapter (fali drajver ili kvar kartice)";
                }
                else if (weakWifiAntenna || !anyUp || !hubOk)
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = true;
                    List<string> warns = new List<string>();
                    if (noWifiSignalAtAll)
                        warns.Add("Wi-Fi adapter je prisutan, ali ne vidi nijednu mrežu u dometu (0 mreža) — proverite Wi-Fi antene ili hardverski prekidač");
                    else if (weakWifiAntenna)
                        warns.Add(string.Format("Slab Wi-Fi prijem ({0}%, {1} mreža) — proverite Main/Aux kablove antene", wifiMaxSignalPct, wifiVisibleNetworks));
                    if (!anyUp)
                        warns.Add("Mrežni adapteri su ispravni, ali računar nije povezan ni na Wi-Fi ni na LAN");
                    else if (!hubOk && gwPingMs < 0)
                        warns.Add("Link je aktivan, ali gateway i ServisHub nisu odgovorili na proveru");
                    else if (!hubOk)
                        warns.Add("Mreža radi (gateway " + gwPingMs + " ms), ali ServisHub nije dostupan");
                    res.Reason = "UPOZORENJE: " + string.Join("; ", warns.ToArray());
                }
                else
                {
                    res.Status = TestStatus.Pass;
                    res.HasWarning = false;
                    res.Reason = string.Format("Mreža, Wi-Fi antena ({0}% / {1} mreža){2} i ServisHub dostupni ({3} ms)",
                        wifiMaxSignalPct, wifiVisibleNetworks, btPresent ? ", Bluetooth OK" : "", hubMs);
                }
            }
            catch (Exception ex)
            {
                res.Status = TestStatus.Fail;
                res.Reason = "Greška mrežne provere: " + ex.Message;
                res.MetricSummary = "Greška provere";
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

        private static void ProbeWifiSignalAndNetworks(out int visibleNetworks, out int maxSignalPct)
        {
            visibleNetworks = 0;
            maxSignalPct = 0;
            try
            {
                string output = RunNetshStdout("wlan show networks mode=bssid", 1800);
                if (!string.IsNullOrEmpty(output))
                {
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

                // Fallback: kada je uređaj već povezan na Wi-Fi ili Windows keš vrati 0 mreža, očitaj aktivan interfejs
                if (maxSignalPct == 0 || visibleNetworks == 0)
                {
                    string ifaceOut = RunNetshStdout("wlan show interfaces", 1500);
                    if (!string.IsNullOrEmpty(ifaceOut))
                    {
                        string[] ifaceLines = ifaceOut.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < ifaceLines.Length; i++)
                        {
                            string trimmed = ifaceLines[i].Trim();
                            if (trimmed.IndexOf("%", StringComparison.Ordinal) >= 0)
                            {
                                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                                    trimmed, @":\s*(\d{1,3})\s*%");
                                if (m.Success)
                                {
                                    int pct;
                                    if (int.TryParse(m.Groups[1].Value, out pct) && pct > 0 && pct <= 100)
                                    {
                                        if (pct > maxSignalPct) maxSignalPct = pct;
                                    }
                                }
                            }
                        }
                        if (maxSignalPct > 0 && visibleNetworks == 0)
                        {
                            visibleNetworks = 1;
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
            btSummary = "Nije detektovan";
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
                                firstError = string.Format("{0} (Code {1})", string.IsNullOrEmpty(name) ? "Bluetooth" : name, err);
                        }
                    }
                }
            }
            catch { }

            if (btPresent)
            {
                btSummary = btOk
                    ? ("Aktivan (" + (string.IsNullOrEmpty(primaryName) ? "Bluetooth" : primaryName) + ")")
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
            TestItemResult res = new TestItemResult("auto_disk", "Disk / SSD / NVMe", "SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark");
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();
            PopulateDiskHealthDetails(res, true);
            sw.Stop();
            res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
            return res;
        }

        public TestItemResult RunDiskBenchmark(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_disk", "Disk / SSD / NVMe", "SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark");
            res.IsAutomated = true;
            Stopwatch sw = Stopwatch.StartNew();

            PopulateDiskHealthDetails(res, false);
            bool smartFail = (res.Status == TestStatus.Fail);
            bool smartWarn = res.HasWarning;
            string smartReason = res.Reason;
            string primaryModel = res.Details.ContainsKey("primary_model") ? res.Details["primary_model"].ToString() : "Sistemski disk";
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

                    if (onProgress != null) onProgress(string.Format("Upis {0} MB na {1}...", testSizeMb, rootDrive));

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
                                onProgress(string.Format("Seq Write: {0}/{1} MB", m + 1, testSizeMb));
                            }
                        }
                        fs.Flush(true);
                    }
                    wsw.Stop();
                    writeMBs = Math.Round(testSizeMb / Math.Max(0.05, wsw.Elapsed.TotalSeconds), 1);

                    // 2. Sekvencijalni Read bez OS file cache (FILE_FLAG_NO_BUFFERING)
                    if (onProgress != null) onProgress(string.Format("Čitanje {0} MB (No-Buffering)...", testSizeMb));
                    readMBs = MeasureUnbufferedSequentialRead(tempFile, testSizeMb, onProgress, cancel);

                    // 3. Kratki 4K Random Read test (800 čitanja = ~3.1 MB)
                    if (onProgress != null) onProgress("4K Random Read test...");
                    rand4kIops = MeasureUnbuffered4KRandomRead(tempFile, testSizeMb, 800, cancel);

                    benchExecuted = true;
                }
                else
                {
                    benchErr = "Premalo slobodnog prostora na " + rootDrive + " (< 400 MB) za benchmark upisa";
                }
            }
            catch (OperationCanceledException)
            {
                res.Status = TestStatus.Skip;
                res.Reason = "Benchmark diska je prekinut od strane korisnika";
                res.MetricSummary = "Prekinuto";
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

            string healthTxt = wearPct.HasValue ? string.Format("Zdravlje: {0}%", Math.Max(0, 100 - wearPct.Value)) : "SMART OK";
            string tempTxt = tempC.HasValue ? string.Format(" | {0}°C", tempC.Value) : "";
            string speedTxt = benchExecuted
                ? string.Format(" | R/W: {0:0}/{1:0} MB/s ({2:0} IOPS)", readMBs, writeMBs, rand4kIops)
                : (!string.IsNullOrEmpty(benchErr) ? " | Bench preskočen" : "");
            res.MetricSummary = string.Format("{0} ({1}) | {2}{3}{4}", ShortModel(primaryModel, 26), mediaKind, healthTxt, tempTxt, speedTxt);

            bool isHdd = mediaKind.IndexOf("HDD", StringComparison.OrdinalIgnoreCase) >= 0;
            bool lowSpeed = benchExecuted && (isHdd ? (readMBs < 40 || writeMBs < 35) : (readMBs < 180 || writeMBs < 120));
            bool highTemp = tempC.HasValue && tempC.Value >= 70;

            if (smartFail)
            {
                res.Status = TestStatus.Fail;
                res.Reason = smartReason;
            }
            else if (!benchExecuted && !string.IsNullOrEmpty(benchErr) && benchErr.IndexOf("Premalo", StringComparison.OrdinalIgnoreCase) < 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = "I/O greška tokom testa diska: " + benchErr;
            }
            else if (smartWarn || highTemp || lowSpeed || !benchExecuted)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                List<string> warns = new List<string>();
                if (smartWarn && !string.IsNullOrEmpty(smartReason)) warns.Add(smartReason);
                if (highTemp) warns.Add(string.Format("Visoka temperatura diska ({0}°C >= 70°C)", tempC.Value));
                if (lowSpeed) warns.Add(string.Format("Niska brzina prenosa za {0} (R {1:0} / W {2:0} MB/s)", mediaKind, readMBs, writeMBs));
                if (!benchExecuted && !string.IsNullOrEmpty(benchErr)) warns.Add(benchErr);
                res.Reason = "UPOZORENJE: " + string.Join("; ", warns.ToArray());
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = string.Format("Disk zdrav ({0}), R/W {1:0}/{2:0} MB/s", healthTxt, readMBs, writeMBs);
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
                        string wTxt = wearByDeviceId.TryGetValue(did, out w) ? string.Format(", Zdravlje {0}%", Math.Max(0, 100 - w)) : "";
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
                            diskLines.Add(string.Format("{0} ({1:0} GB, Status {2})", model, sizeBytes / (1024.0 * 1024.0 * 1024.0), st));
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
                            issues.Add("SMART PredictFailure = TRUE (disk je pred otkazom!)");
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
                issues.Add(string.Format("Detektovano {0} neispravljivih Read/Write grešaka", totalUncorrected));
            }
            if (worstWear.HasValue && worstWear.Value >= 90)
            {
                anyCritical = true;
                issues.Add(string.Format("SSD istrošenost kritična (Wear {0}%, preostalo {1}%)", worstWear.Value, Math.Max(0, 100 - worstWear.Value)));
            }
            else if (worstWear.HasValue && worstWear.Value >= 75)
            {
                anyWarn = true;
                issues.Add(string.Format("Visoka istrošenost SSD-a (Wear {0}%, preostalo {1}%)", worstWear.Value, Math.Max(0, 100 - worstWear.Value)));
            }

            if (maxTemp.HasValue && maxTemp.Value >= 70)
            {
                anyWarn = true;
                issues.Add(string.Format("Visoka temperatura diska ({0}°C)", maxTemp.Value));
            }

            res.Details["primary_model"] = string.IsNullOrEmpty(primaryModel) ? "Sistemski disk" : primaryModel;
            res.Details["primary_media"] = primaryMedia;
            res.Details["disks_summary"] = string.Join(" | ", diskLines.ToArray());
            res.Details["smart_predict_fail"] = predictFail;
            res.Details["uncorrected_errors"] = totalUncorrected;
            if (worstWear.HasValue) res.Details["wear_percent"] = worstWear.Value;
            if (maxTemp.HasValue) res.Details["temp_c"] = maxTemp.Value;

            if (passiveOnly)
            {
                string healthTxt = worstWear.HasValue ? string.Format("Zdravlje: {0}%", Math.Max(0, 100 - worstWear.Value)) : "SMART OK";
                string tempTxt = maxTemp.HasValue ? string.Format(" | {0}°C", maxTemp.Value) : "";
                res.MetricSummary = string.Format("{0} ({1}) | {2}{3} | Benchmark čeka pokretanje",
                    ShortModel(res.Details["primary_model"].ToString(), 26), primaryMedia, healthTxt, tempTxt);

                if (anyCritical)
                {
                    res.Status = TestStatus.Fail;
                    res.Reason = string.Join("; ", issues.ToArray());
                }
                else if (anyWarn)
                {
                    res.HasWarning = true;
                    res.Reason = "UPOZORENJE (SMART): " + string.Join("; ", issues.ToArray());
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
                            onProgress(string.Format("Seq Read: {0}/{1} MB", m + 1, totalMb));
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
            TestItemResult res = new TestItemResult("auto_ram", "Radna memorija (RAM)", "Detekcija modula/brzine i write/read verifikacija integriteta memorije");
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
                ? string.Format("{0}/{1} slota", dimmList.Count, totalSlots)
                : string.Format("{0} modula", dimmList.Count);

            res.Details["total_gb"] = totalGb;
            res.Details["avail_mb"] = availPhysMb;
            res.Details["dimm_count"] = dimmList.Count;
            res.Details["total_slots"] = totalSlots;
            res.Details["speed_mhz"] = maxSpeedMhz;
            res.Details["memory_type"] = memTypeName;
            res.Details["dimms"] = string.Join(" | ", dimmList.ToArray());

            res.MetricSummary = string.Format("{0:0.#} GB {1}{2} ({3}) | Slobodno: {4:0.0} GB | Čeka pattern test",
                totalGb, memTypeName, maxSpeedMhz > 0 ? ("-" + maxSpeedMhz) : "",
                slotLabel, availPhysMb / 1024.0);
        }

        public TestItemResult RunRamCheck(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_ram", "Radna memorija (RAM)", "Detekcija modula/brzine i write/read verifikacija integriteta memorije");
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
                        onProgress(string.Format("RAM Pattern: {0}/{1} MB ({2} grešaka)", testedMb, totalBlocks * blockMb, bitErrors));
                }
                ioSw.Stop();
            }
            catch (OperationCanceledException)
            {
                res.Status = TestStatus.Skip;
                res.Reason = "RAM test je prekinut od strane korisnika";
                res.MetricSummary = "Prekinuto";
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
                    res.Reason = "Nedovoljno slobodne memorije za izvršavanje RAM testa";
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
                res.Reason = "Greška tokom RAM testa: " + ex.Message;
                res.MetricSummary = "Greška testa";
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }

            double bwGbs = Math.Round((totalBytesTransferred / (1024.0 * 1024.0 * 1024.0)) / Math.Max(0.05, ioSw.Elapsed.TotalSeconds), 2);
            res.Details["tested_mb"] = testedMb;
            res.Details["bit_errors"] = bitErrors;
            res.Details["bandwidth_gbs"] = bwGbs;

            string slotLabel = totalSlots > 0
                ? string.Format("{0}/{1} slota", dimmCount, totalSlots)
                : string.Format("{0} modula", dimmCount);
            res.MetricSummary = string.Format("{0:0.#} GB {1}{2} ({3}) | Testirano: {4} MB ({5:0.0} GB/s) | {6} grešaka",
                totalGb, memType, speedMhz > 0 ? ("-" + speedMhz) : "", slotLabel, testedMb, bwGbs, bitErrors);

            if (bitErrors > 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = string.Format("KRITIČNO: Detektovano {0} grešaka memorije tokom Write/Read verifikacije!", bitErrors);
            }
            else if (availMb < 192)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                res.Reason = string.Format("UPOZORENJE: Premalo slobodnog RAM-a ({0} MB); testirano samo {1} MB (0 grešaka)", availMb, testedMb);
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = string.Format("RAM integritet OK (testirano {0} MB u 3 pattern prolaza, 0 grešaka)", testedMb);
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
            TestItemResult res = new TestItemResult("auto_cpu", "Procesor (CPU)", "Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature");
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

            string tStr = temp > 0 ? string.Format(" | Idle: {0:0}°C", temp) : "";
            res.MetricSummary = string.Format("{0} ({1}C/{2}T){3} | Čeka stres test", ShortModel(name, 30), cores, logical, tStr);
            return res;
        }

        public TestItemResult RunCpuStress(Action<string> onProgress, CancelFlag cancel)
        {
            TestItemResult res = new TestItemResult("auto_cpu", "Procesor (CPU)", "Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature");
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
            float avgClock = 0f;
            int clockSamples = 0;
            float maxThrottle = 0f;
            int maxFanRpm = 0;
            int consecutiveOverheat = 0;
            bool thermalAbort = false;
            bool userCancelled = false;

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
                    try
                    {
                        eng.Update();
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

                if (curTemp > peakTemp) peakTemp = curTemp;
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
                    onProgress(string.Format("CPU Stres ({0}T): {1} | {2} | {3}s", threadCount, tempLabel, clkLabel, secLeft));
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
                res.Reason = "CPU stres test je prekinut od strane korisnika";
                res.MetricSummary = "Prekinuto";
                sw.Stop();
                res.DurationSeconds = (int)Math.Ceiling(sw.Elapsed.TotalSeconds);
                return res;
            }

            float meanClockMhz = clockSamples > 0 ? (avgClock / clockSamples) : 0f;
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
            res.Details["sensor_source"] = eng != null ? eng.SensorSource : "NONE";

            string tempStr = peakTemp > 0 ? string.Format("Peak: {0:0}°C", peakTemp) : "Temp: N/A";
            string clkStr = meanClockMhz > 0 ? string.Format(" | Takt: {0:0} MHz", meanClockMhz) : string.Format(" | Baza: {0} MHz", baseClockMhz);
            string fanStr = maxFanRpm > 0 ? string.Format(" | Fan: {0} RPM", maxFanRpm) : "";
            res.MetricSummary = string.Format("{0} ({1}T) | {2}{3}{4}", ShortModel(cpuName, 24), threadCount, tempStr, clkStr, fanStr);

            bool severeThrottle = (baseClockMhz > 500 && meanClockMhz > 100 && meanClockMhz < (baseClockMhz * 0.45f) && peakTemp >= 85f);
            bool belowBaseModerate = (baseClockMhz > 500 && meanClockMhz > 100 && meanClockMhz < (baseClockMhz * 0.72f));
            bool sensorThrottleWarn = (maxThrottle >= 25f) && (peakTemp >= 85f || baseClockMhz <= 500 || (meanClockMhz > 100 && meanClockMhz < (baseClockMhz * 0.88f)));
            bool moderateThrottle = belowBaseModerate || sensorThrottleWarn;

            if (workerCrashCount > 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = string.Format("Nestabilnost procesora: {0} radnih niti je prijavilo grešku!", workerCrashCount);
            }
            else if (thermalAbort || peakTemp >= 95.0f)
            {
                res.Status = TestStatus.Fail;
                res.Reason = string.Format("KRITIČNO PREGREVANJE CPU-a: {0:0.0}°C (>= 95°C) — potrebna provera hlađenja/paste!", peakTemp);
            }
            else if (severeThrottle)
            {
                res.Status = TestStatus.Fail;
                res.Reason = string.Format("Ekstreman thermal throttling: takt pao na {0:0} MHz (baza {1} MHz) uz {2:0}°C", meanClockMhz, baseClockMhz, peakTemp);
            }
            else if (peakTemp >= 85.0f || moderateThrottle)
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = true;
                List<string> warns = new List<string>();
                if (peakTemp >= 85.0f) warns.Add(string.Format("Povišena temperatura pod opterećenjem ({0:0}°C)", peakTemp));
                if (moderateThrottle) warns.Add(string.Format("Detektovan umereni throttling (takt {0:0}/{1} MHz, throttle {2:0}%)", meanClockMhz, baseClockMhz, maxThrottle));
                res.Reason = "UPOZORENJE: " + string.Join("; ", warns.ToArray());
            }
            else
            {
                res.Status = TestStatus.Pass;
                res.HasWarning = false;
                res.Reason = peakTemp > 0
                    ? string.Format("CPU stabilan pod 100% opterećenjem ({0} niti, max {1:0}°C)", threadCount, peakTemp)
                    : string.Format("CPU stabilan pod 100% opterećenjem ({0} niti, 0 grešaka; temp senzor nedostupan)", threadCount);
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
            TestItemResult res = new TestItemResult("auto_gpu", "Grafika (GPU)", "Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)");
            res.IsAutomated = true;
            string primaryGpu;
            string driverVer;
            long vramMb;
            uint errCode;
            bool isBasicAdapter;
            string allGpus;
            InspectGpuAdapters(out primaryGpu, out driverVer, out vramMb, out errCode, out isBasicAdapter, out allGpus);

            int tier = (System.Windows.Media.RenderCapability.Tier >> 16);
            res.Details["gpu_name"] = primaryGpu;
            res.Details["driver_version"] = driverVer;
            res.Details["vram_mb"] = vramMb;
            res.Details["pnp_error_code"] = errCode;
            res.Details["is_basic_display"] = isBasicAdapter;
            res.Details["wpf_render_tier"] = tier;
            res.Details["all_adapters"] = allGpus;

            string vramTxt = vramMb > 0 ? string.Format(" | VRAM: {0} MB", vramMb) : "";
            res.MetricSummary = string.Format("{0}{1} | Tier {2} | Čeka 3D stres", ShortModel(primaryGpu, 26), vramTxt, tier);

            if (isBasicAdapter || errCode != 0)
            {
                res.Status = TestStatus.Fail;
                res.Reason = isBasicAdapter
                    ? "Aktivan je Microsoft Basic Display Adapter (nedostaje grafički drajver!)"
                    : string.Format("Greška GPU uređaja u Device Manager-u (Error Code {0})", errCode);
            }
            return res;
        }

        public static void InspectGpuAdapters(out string primaryName, out string driverVersion, out long vramMb, out uint errorCode, out bool isBasicDisplay, out string allAdaptersSummary)
        {
            primaryName = "Nepoznat GPU";
            driverVersion = "";
            vramMb = 0;
            errorCode = 0;
            isBasicDisplay = false;
            List<string> list = new List<string>();

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

                        list.Add(string.Format("{0} (Drv: {1}, Err: {2})", name, drv, err));

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
