using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WIS.AutopilotAuditor
{
    public static class CloudZtdInspector
    {
        private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        private static readonly Regex WaadDeviceNameRegex = new Regex(@"\bW-AAD-\d{9}\b", RegexOptions.IgnoreCase);
        private static readonly Regex GuidRegex = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        private static bool IsValidNonZeroGuid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string cleaned = value.Trim().Trim('{', '}');
            if (string.Equals(cleaned, ZeroGuid, StringComparison.OrdinalIgnoreCase)) return false;
            return GuidRegex.IsMatch(cleaned);
        }

        public static List<KeyValuePair<string, string>> GetZtdFileCandidates()
        {
            List<KeyValuePair<string, string>> candidates = new List<KeyValuePair<string, string>>();
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(winDir))
            {
                winDir = @"C:\Windows";
            }

            // 1. PRIMARNA Windows 11 putanja: C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json
            candidates.Add(new KeyValuePair<string, string>(
                Path.Combine(winDir, @"ServiceState\wmansvc\AutopilotDDSZTDFile.json"),
                "WMANSVC_ZTD_FILE"));

            // 2. PRIMARNI Windows 11 Provisioning direktorijum: C:\Windows\Provisioning\Autopilot\
            string provDir = Path.Combine(winDir, @"Provisioning\Autopilot");
            candidates.Add(new KeyValuePair<string, string>(
                Path.Combine(provDir, "AutopilotConfigurationFile.json"),
                "PROVISIONING_AUTOPILOT"));
            candidates.Add(new KeyValuePair<string, string>(
                Path.Combine(provDir, "AutopilotDDSZTDFile.json"),
                "PROVISIONING_AUTOPILOT"));
            try
            {
                if (Directory.Exists(provDir))
                {
                    string[] provJsonFiles = Directory.GetFiles(provDir, "*.json");
                    for (int i = 0; i < provJsonFiles.Length; i++)
                    {
                        string pf = provJsonFiles[i];
                        bool alreadyAdded = false;
                        for (int j = 0; j < candidates.Count; j++)
                        {
                            if (string.Equals(candidates[j].Key, pf, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyAdded = true;
                                break;
                            }
                        }
                        if (!alreadyAdded)
                        {
                            candidates.Add(new KeyValuePair<string, string>(pf, "PROVISIONING_AUTOPILOT"));
                        }
                    }
                }
            }
            catch { }

            // 3. SEKUNDARNI fallback: Pre-OOBE rezervna kopija i starija ServiceState\Autopilot putanja
            candidates.Add(new KeyValuePair<string, string>(
                @"C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json",
                "PREOOBE_ZTD_FILE"));
            candidates.Add(new KeyValuePair<string, string>(
                Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json"),
                "SERVICE_STATE_CACHE"));

            return candidates;
        }

        private static bool TryInspectAllZtdFiles(CloudZtdDetails report, string overrideQueryMode)
        {
            List<KeyValuePair<string, string>> candidates = GetZtdFileCandidates();
            for (int i = 0; i < candidates.Count; i++)
            {
                string ztdPath = candidates[i].Key;
                string mode = !string.IsNullOrEmpty(overrideQueryMode) ? overrideQueryMode : candidates[i].Value;
                try
                {
                    if (File.Exists(ztdPath))
                    {
                        string rawZtd = File.ReadAllText(ztdPath, Encoding.UTF8);
                        if (TryParseZtdJsonContent(rawZtd, report, mode))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        public static CloudZtdDetails Inspect(string targetSerial)
        {
            CloudZtdDetails report = new CloudZtdDetails();

            // 0. Brza provera ZTD fajlova (primarno wmansvc + Provisioning\Autopilot) i sačuvanog Pre-OOBE izveštaja (radi i offline!)
            if (TryLoadCachedReport(targetSerial, report))
            {
                return report;
            }

            // 1. Provera živog Windows Autopilot Event Dnevnika (čak i ako je mreža trenutno offline, OOBE je možda već završio ZTD razmenu!)
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try
                {
                    TryReadLiveEventLog(report);
                    if (report.IsRegistered || report.ConfirmedFree)
                    {
                        return report;
                    }
                }
                catch { }
            }

            // 2. Provera mrežne dostupnosti Microsoft ZTD servisa (ztd.dds.microsoft.com:443)
            // BITNO: puko uspostavljanje TCP konekcije NIKADA ne postavlja OnlineChecked = true!
            // OnlineChecked = true se postavlja isključivo kada postoji realna ZTD razmena/odgovor.
            bool canReachZtd = TestTcpAndTlsConnection("ztd.dds.microsoft.com", 443, 3000);
            report.NetworkReachable = canReachZtd;

            // 3. Opcioni upit ka lokalnom ServisHub / GDS serveru (ako postoji u radionici)
            if (!report.IsRegistered && !report.ConfirmedFree && !string.IsNullOrEmpty(targetSerial) && targetSerial != "Nepoznato")
            {
                try
                {
                    CheckGdsServerStatus(targetSerial, report);
                    if (report.IsRegistered || report.ConfirmedFree)
                    {
                        return report;
                    }
                }
                catch { }
            }

            if (!canReachZtd)
            {
                report.OnlineChecked = false;
                report.RawStatusMessage = "OFFLINE - Microsoft ZTD servis (ztd.dds.microsoft.com:443) nije dostupan";
                report.QueryMode = "OFFLINE";
                return report;
            }

            // 4. Aktivno pozadinsko buđenje ZTD radnika ako je ztd.dds.microsoft.com dostupan, a još nema eksplicitnog odgovora
            if (!report.IsRegistered && !report.ConfirmedFree)
            {
                TriggerActiveZtdDiagnostics(report);
            }

            if (!report.IsRegistered && !report.ConfirmedFree)
            {
                report.OnlineChecked = false;
                if (string.IsNullOrEmpty(report.QueryMode) || report.QueryMode == "NONE")
                {
                    report.QueryMode = "UNVERIFIED_NO_ZTD_RESPONSE";
                }
                if (string.IsNullOrEmpty(report.RawStatusMessage) || report.RawStatusMessage == "Nije provereno")
                {
                    report.RawStatusMessage = "ztd.dds.microsoft.com je dostupan, ali Windows ZTD servis nije vratio eksplicitan Event 806/807 odgovor";
                }
            }

            return report;
        }

        public static bool TryParseZtdJsonContent(string rawJson, CloudZtdDetails report, string queryMode)
        {
            if (string.IsNullOrEmpty(rawJson) || report == null) return false;

            string tenantId = ExtractJsonString(rawJson, "CloudAssignedTenantId");
            string tenantDomain = ExtractJsonString(rawJson, "CloudAssignedTenantDomain");
            string deviceName = ExtractJsonString(rawJson, "CloudAssignedDeviceName");
            string ztdRegState = ExtractJsonString(rawJson, "ZtdRegistrationState");

            if (!string.IsNullOrEmpty(ztdRegState) && string.IsNullOrEmpty(report.ZtdRegistrationState))
            {
                report.ZtdRegistrationState = ztdRegState;
            }

            bool hasValidTenantId = IsValidNonZeroGuid(tenantId);
            bool hasTenantDomain = !string.IsNullOrEmpty(tenantDomain);
            bool hasWaadDeviceName = !string.IsNullOrEmpty(deviceName) && WaadDeviceNameRegex.IsMatch(deviceName);

            if (hasValidTenantId || hasTenantDomain || hasWaadDeviceName)
            {
                report.OnlineChecked = true;
                report.IsRegistered = true;
                report.ConfirmedFree = false;
                report.ProfileTenantId = hasValidTenantId ? tenantId.Trim().Trim('{', '}') : "";
                report.ProfileTenantDomain = tenantDomain;
                report.ProfileDeviceName = deviceName;
                report.QueryMode = queryMode;
                report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (" + queryMode + ")";
                return true;
            }

            // Eksplicitna ZTD 807 / ZtdDeviceIsNotRegistered potvrda je OBAVEZNA za ConfirmedFree = true.
            // Puko ProfileState=NotAssigned ili prazan CloudAssignedTenantId NIKADA ne sme dati ConfirmedFree = true!
            bool explicitNotRegistered =
                rawJson.IndexOf("ZtdDeviceIsNotRegistered", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(rawJson, "\"(?:ErrorCode|EventId|ZtdStatusCode)\"\\s*:\\s*(?:807|\"807[^\"]*\")", RegexOptions.IgnoreCase);

            if (explicitNotRegistered)
            {
                report.OnlineChecked = true;
                report.IsRegistered = false;
                report.ConfirmedFree = true;
                report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                report.QueryMode = queryMode;
                report.RawStatusMessage = "Slobodan (Microsoft ZTD eksplicitno potvrdio: Event 807 - ZtdDeviceIsNotRegistered)";
                return true;
            }

            // Zabeleži napomenu ako fajl sadrži prazan TenantId ili ProfileState=NotAssigned bez Event 807 potvrde
            bool hasEmptyTenantKey = Regex.IsMatch(rawJson, "\"CloudAssignedTenantId\"\\s*:", RegexOptions.IgnoreCase);
            bool hasNotAssignedState = Regex.IsMatch(rawJson, "\"ProfileState\"\\s*:\\s*(0|\"NotAssigned\")", RegexOptions.IgnoreCase);
            if (hasEmptyTenantKey || hasNotAssignedState)
            {
                report.IsRegistered = false;
                report.ConfirmedFree = false;
                if (string.IsNullOrEmpty(report.ZtdRegistrationState))
                {
                    report.ZtdRegistrationState = "Unverified_NotAssigned";
                }
                report.RawStatusMessage = "ZTD fajl sadrži prazan CloudAssignedTenantId / ProfileState=NotAssigned bez eksplicitne Event 807 (ZtdDeviceIsNotRegistered) potvrde";
            }

            return false;
        }

        public static bool TryLoadCachedReport(string targetSerial, CloudZtdDetails report)
        {
            if (report == null) return false;

            try
            {
                // 1. Prvo proveravamo primarne Windows 11 ZTD fajlove (wmansvc, Provisioning\Autopilot), pa sekundarne fallback lokacije
                if (TryInspectAllZtdFiles(report, null))
                {
                    return true;
                }

                // 2. Zatim proveravamo sačuvani trajni Pre-OOBE / OOBE izveštaj
                string[] reportCandidates = new string[]
                {
                    @"C:\Windows\Logs\WIS\autopilot_report.json",
                    @"C:\setup\autopilot_report.json"
                };

                for (int i = 0; i < reportCandidates.Length; i++)
                {
                    string repPath = reportCandidates[i];
                    try
                    {
                        if (!File.Exists(repPath)) continue;
                        string rawJson = File.ReadAllText(repPath, Encoding.UTF8);
                        if (string.IsNullOrEmpty(rawJson)) continue;

                        string cachedSerial = ExtractJsonString(rawJson, "device_serial");
                        if (!string.IsNullOrEmpty(cachedSerial) &&
                            !string.Equals(cachedSerial, "Nepoznato", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(targetSerial) &&
                            !string.Equals(targetSerial, "Nepoznato", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(cachedSerial, targetSerial, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string matrixCode = ExtractJsonString(rawJson, "matrix_code").ToUpperInvariant();
                        bool cachedRegistered = ExtractJsonBool(rawJson, "is_registered") || ExtractJsonBool(rawJson, "is_locked");
                        bool cachedFree = ExtractJsonBool(rawJson, "confirmed_free");
                        string profDom = ExtractJsonString(rawJson, "profile_tenant_domain");
                        if (string.IsNullOrEmpty(profDom)) profDom = ExtractJsonString(rawJson, "tenant_domain");
                        string profId = ExtractJsonString(rawJson, "profile_tenant_id");
                        if (string.IsNullOrEmpty(profId)) profId = ExtractJsonString(rawJson, "tenant_id");
                        string profDev = ExtractJsonString(rawJson, "profile_device_name");
                        if (string.IsNullOrEmpty(profDev)) profDev = ExtractJsonString(rawJson, "assigned_device_name");
                        string errCode = ExtractJsonString(rawJson, "error_code");
                        string rawMsg = ExtractJsonString(rawJson, "raw_status_message");

                        if (matrixCode == "LOCKED_TENANT" || matrixCode == "HIDDEN_LOCK" || cachedRegistered)
                        {
                            report.OnlineChecked = true;
                            report.IsRegistered = true;
                            report.ConfirmedFree = false;
                            report.ProfileTenantDomain = profDom;
                            report.ProfileTenantId = profId;
                            report.ProfileDeviceName = profDev;
                            report.ErrorCode = errCode;
                            report.QueryMode = "PREOOBE_CACHED_REPORT";
                            report.RawStatusMessage = !string.IsNullOrEmpty(rawMsg)
                                ? rawMsg
                                : "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Sačuvan OOBE izveštaj)";
                            return true;
                        }

                        // Samo eksplicitna 807 / ZtdDeviceIsNotRegistered potvrda iz sačuvanog OOBE izveštaja
                        bool hasExplicit807 =
                            !string.IsNullOrEmpty(errCode) &&
                            (errCode.IndexOf("807", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             errCode.IndexOf("ZtdDeviceIsNotRegistered", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            errCode.IndexOf("(ZTD File)", StringComparison.OrdinalIgnoreCase) < 0;

                        if (cachedFree && hasExplicit807 && (matrixCode == "CONFIRMED_CLEAN" || matrixCode == "RELEASED_LOCAL_TRACES"))
                        {
                            report.OnlineChecked = true;
                            report.IsRegistered = false;
                            report.ConfirmedFree = true;
                            report.ErrorCode = errCode;
                            report.QueryMode = "PREOOBE_CACHED_REPORT";
                            report.RawStatusMessage = "Slobodan (Potvrđeno iz sačuvanog OOBE Event 807 izveštaja)";
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static bool IsReportContentConfirmed(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            string matrixCode = ExtractJsonString(json, "matrix_code").ToUpperInvariant();
            if (matrixCode == "LOCKED_TENANT" || matrixCode == "HIDDEN_LOCK")
            {
                return true;
            }
            if (ExtractJsonBool(json, "is_registered") || ExtractJsonBool(json, "is_locked"))
            {
                return true;
            }
            if ((matrixCode == "CONFIRMED_CLEAN" || matrixCode == "RELEASED_LOCAL_TRACES") && ExtractJsonBool(json, "confirmed_free"))
            {
                string errCode = ExtractJsonString(json, "error_code");
                if (string.IsNullOrEmpty(errCode) || errCode.IndexOf("(ZTD File)", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static void SaveCachedReport(AuditorResult result)
        {
            if (result == null) return;

            bool newIsConfirmed = result.IsLocked || (result.CloudZtd != null && (result.CloudZtd.IsRegistered || result.CloudZtd.ConfirmedFree));
            string jsonContent = result.ToJson();
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);

            string[] targetPaths = new string[]
            {
                @"C:\Windows\Logs\WIS\autopilot_report.json",
                @"C:\setup\autopilot_report.json"
            };

            for (int i = 0; i < targetPaths.Length; i++)
            {
                string path = targetPaths[i];
                try
                {
                    if (i == 1 && !Directory.Exists(@"C:\setup"))
                    {
                        continue;
                    }

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (!newIsConfirmed && File.Exists(path))
                    {
                        try
                        {
                            string existing = File.ReadAllText(path, Encoding.UTF8);
                            if (IsReportContentConfirmed(existing))
                            {
                                continue;
                            }
                        }
                        catch { }
                    }

                    File.WriteAllText(path, jsonContent, utf8NoBom);
                }
                catch { }
            }

            // Rezervna kopija ZTD fajla sa primarnih Windows 11 putanja (wmansvc, Provisioning\Autopilot) pa tek onda sekundarnih
            try
            {
                string preOobeCopy = @"C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json";
                List<KeyValuePair<string, string>> candidates = GetZtdFileCandidates();
                for (int i = 0; i < candidates.Count; i++)
                {
                    string src = candidates[i].Key;
                    if (string.Equals(src, preOobeCopy, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(src))
                    {
                        string wisLogDir = Path.GetDirectoryName(preOobeCopy);
                        if (!string.IsNullOrEmpty(wisLogDir) && !Directory.Exists(wisLogDir))
                        {
                            Directory.CreateDirectory(wisLogDir);
                        }
                        File.Copy(src, preOobeCopy, true);
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool TestTcpAndTlsConnection(string hostname, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(hostname, port, null, null);
                    bool success = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!success) return false;

                    client.EndConnect(ar);

                    using (SslStream ssl = new SslStream(client.GetStream(), false, (sender, cert, chain, sslPolicyErrors) => sslPolicyErrors == System.Net.Security.SslPolicyErrors.None))
                    {
                        ssl.ReadTimeout = timeoutMs;
                        ssl.WriteTimeout = timeoutMs;
                        ssl.AuthenticateAsClient(hostname);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TriggerActiveZtdDiagnostics(CloudZtdDetails report)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AP_Auditor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string cabPath = Path.Combine(tempDir, "AutopilotReport.cab");
            string outDir = Path.Combine(tempDir, "Extracted");

            bool hadSkipNet = false;
            object origSkipNet = null;
            bool hadDisableOobe = false;
            object origDisableOobe = null;
            const string oobeSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE";

            try
            {
                Directory.CreateDirectory(outDir);

                // Privremeno uklanjanje SkipNetworkSetup i DisableOOBEConnection dok ZTD radnik šalje upit
                try
                {
                    using (RegistryKey oobeKey = Registry.LocalMachine.OpenSubKey(oobeSubKey, true))
                    {
                        if (oobeKey != null)
                        {
                            origSkipNet = oobeKey.GetValue("SkipNetworkSetup");
                            if (origSkipNet != null)
                            {
                                hadSkipNet = true;
                                oobeKey.DeleteValue("SkipNetworkSetup", false);
                            }
                            origDisableOobe = oobeKey.GetValue("DisableOOBEConnection");
                            if (origDisableOobe != null)
                            {
                                hadDisableOobe = true;
                                oobeKey.DeleteValue("DisableOOBEConnection", false);
                            }
                        }
                    }
                }
                catch { }

                // 1. Silent OOBE servisi - obezbedi da su pokrenuti pre slanja upita
                StartServiceSilently("tbs");
                StartServiceSilently("wlidsvc");
                StartServiceSilently("dmwappushservice");
                StartServiceSilently("wmansvc");

                // 2. Buđenje svih OOBE Provisioning zadataka u Task Scheduler-u
                string[] tasks = new string[]
                {
                    "\\Microsoft\\Windows\\Management\\Provisioning\\Autopilot",
                    "\\Microsoft\\Windows\\Management\\Provisioning\\Logon",
                    "\\Microsoft\\Windows\\Management\\Provisioning\\Cellular",
                    "\\Microsoft\\Windows\\Management\\Provisioning\\Retry"
                };
                foreach (string t in tasks)
                {
                    RunHiddenCommand("schtasks.exe", string.Format("/run /tn \"{0}\"", t), 500);
                }

                // 3. OOBE DeviceEnroller radnik
                string devEnroller = Path.Combine(Environment.SystemDirectory, "deviceenroller.exe");
                if (File.Exists(devEnroller))
                {
                    RunHiddenCommand(devEnroller, "/c /AutoEnrollMDM", 1000);
                }

                // 4. Pokretanje dubokog OOBE dijagnostičkog alata sa pametnim pollingom
                string toolTool = Path.Combine(Environment.SystemDirectory, "mdmdiagnosticstool.exe");
                if (File.Exists(toolTool))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = toolTool,
                        Arguments = string.Format("-area \"Autopilot;DeviceProvisioning;DeviceEnrollment\" -cab \"{0}\"", cabPath),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            try { proc.BeginOutputReadLine(); } catch { }
                            try { proc.BeginErrorReadLine(); } catch { }

                            // Pametni polling do 35 sekundi (sa ranim izlaskom čim Microsoft upiše odgovor)
                            for (int waitSec = 0; waitSec < 35; waitSec++)
                            {
                                if (proc.HasExited) break;
                                System.Threading.Thread.Sleep(1000);

                                // Proveri živi Event Log svake sekunde
                                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                                {
                                    try
                                    {
                                        TryReadLiveEventLog(report);
                                        if (report.IsRegistered || report.ConfirmedFree)
                                        {
                                            try { proc.Kill(); } catch { }
                                            return;
                                        }
                                    }
                                    catch { }
                                }

                                // Proveri primarne i sekundarne ZTD fajlove (wmansvc, Provisioning\Autopilot, fallback)
                                try
                                {
                                    if (TryInspectAllZtdFiles(report, "ACTIVE_ZTD_DIAG"))
                                    {
                                        try { proc.Kill(); } catch { }
                                        return;
                                    }
                                }
                                catch { }
                            }

                            if (!proc.HasExited)
                            {
                                try { proc.Kill(); } catch { }
                            }
                        }
                    }

                    // 5. Ako je CAB kreiran, raspakuj ga i proveri interne dokaze
                    if (File.Exists(cabPath))
                    {
                        ProcessStartInfo expPsi = new ProcessStartInfo
                        {
                            FileName = "expand.exe",
                            Arguments = string.Format("\"{0}\" -F:* \"{1}\"", cabPath, outDir),
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };

                        using (Process expProc = Process.Start(expPsi))
                        {
                            if (expProc != null)
                            {
                                try { expProc.BeginOutputReadLine(); } catch { }
                                try { expProc.BeginErrorReadLine(); } catch { }
                                if (!expProc.WaitForExit(5000))
                                {
                                    try { expProc.Kill(); } catch { }
                                }
                            }
                        }

                        // Provera raspakovanih JSON ZTD fajlova
                        string[] extractedJsonFiles = Directory.GetFiles(outDir, "*Autopilot*.json");
                        for (int i = 0; i < extractedJsonFiles.Length; i++)
                        {
                            try
                            {
                                string rawJson = File.ReadAllText(extractedJsonFiles[i], Encoding.UTF8);
                                if (TryParseZtdJsonContent(rawJson, report, "ACTIVE_ZTD_DIAG"))
                                {
                                    return;
                                }
                            }
                            catch { }
                        }

                        // Provera izvučenih .evtx logova (i registracije i eksplicitnog Event 807)
                        string[] evtxFiles = Directory.GetFiles(outDir, "*.evtx");
                        foreach (string ef in evtxFiles)
                        {
                            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                            {
                                try
                                {
                                    if (TryInspectEvtxFile(ef, report))
                                    {
                                        return;
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    // 6. Završna provera živog dnevnika nakon završetka dijagnostike
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        try
                        {
                            TryReadLiveEventLog(report);
                            if (report.IsRegistered || report.ConfirmedFree)
                            {
                                return;
                            }
                        }
                        catch { }
                    }

                    report.OnlineChecked = false;
                    report.RawStatusMessage = "ZTD dijagnostika nije dobila eksplicitan Event 806/807 odgovor od Microsoft oblaka (potrebna OOBE ZTD razmena)";
                    report.QueryMode = "ACTIVE_ZTD_UNCONFIRMED";
                }
            }
            catch (Exception ex)
            {
                report.OnlineChecked = false;
                report.RawStatusMessage = "Dijagnostička greška: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (hadSkipNet || hadDisableOobe)
                    {
                        bool isImageComplete = false;
                        try
                        {
                            using (RegistryKey stateKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State"))
                            {
                                if (stateKey != null)
                                {
                                    object imgState = stateKey.GetValue("ImageState");
                                    if (imgState != null && string.Equals(imgState.ToString().Trim(), "IMAGE_STATE_COMPLETE", StringComparison.OrdinalIgnoreCase))
                                    {
                                        isImageComplete = true;
                                    }
                                }
                            }
                        }
                        catch { }

                        if (!isImageComplete)
                        {
                            using (RegistryKey oobeKey = Registry.LocalMachine.OpenSubKey(oobeSubKey, true))
                            {
                                if (oobeKey != null)
                                {
                                    if (hadSkipNet && origSkipNet != null)
                                    {
                                        oobeKey.SetValue("SkipNetworkSetup", origSkipNet, RegistryValueKind.DWord);
                                    }
                                    if (hadDisableOobe && origDisableOobe != null)
                                    {
                                        oobeKey.SetValue("DisableOOBEConnection", origDisableOobe, RegistryValueKind.DWord);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private static void StartServiceSilently(string serviceName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = "start " + serviceName,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        try { p.BeginOutputReadLine(); } catch { }
                        try { p.BeginErrorReadLine(); } catch { }
                        if (!p.WaitForExit(1000))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private static void RunHiddenCommand(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        try { p.BeginOutputReadLine(); } catch { }
                        try { p.BeginErrorReadLine(); } catch { }
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private static void CheckGdsServerStatus(string serial, CloudZtdDetails report)
        {
            string[] hosts = new string[] { "192.168.170.254", "truenas.servis", "192.168.88.50", "192.168.1.100" };
            foreach (string host in hosts)
            {
                try
                {
                    string url = string.Format("http://{0}:8000/api/v1/devices/{1}/autopilot-status", host, Uri.EscapeDataString(serial));
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = 1500;
                    request.Method = "GET";

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                            {
                                string json = reader.ReadToEnd();
                                string status = ExtractJsonString(json, "status").ToUpperInvariant();
                                if (status == "LOCKED")
                                {
                                    report.OnlineChecked = true;
                                    report.IsRegistered = true;
                                    report.ConfirmedFree = false;
                                    report.ProfileTenantDomain = ExtractJsonString(json, "tenant_domain");
                                    report.ProfileTenantId = ExtractJsonString(json, "tenant_id");
                                    report.GdsServerUrl = url;
                                    report.QueryMode = "GDS_CENTRAL_SERVER";
                                    report.RawStatusMessage = "Uređaj je zaključan prema evidenciji ServisHub baze";
                                    break;
                                }
                                else if (status == "CLEAN")
                                {
                                    report.OnlineChecked = true;
                                    report.IsRegistered = false;
                                    report.ConfirmedFree = true;
                                    report.ErrorCode = "807 - GDS_CENTRAL_SERVER";
                                    report.GdsServerUrl = url;
                                    report.QueryMode = "GDS_CENTRAL_SERVER";
                                    report.RawStatusMessage = "Potvrđeno slobodan prema ServisHub bazi";
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string pattern = string.Format("\"{0}\"\\s*:\\s*\"([^\"]*)\"", Regex.Escape(key));
            Match m = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return "";
        }

        private static bool ExtractJsonBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            string pattern = string.Format("\"{0}\"\\s*:\\s*(true|false)", Regex.Escape(key));
            Match m = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string ExtractNamedFieldFromEvent(string text, string fieldName)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(fieldName)) return "";

            // 1. XML Data element format: <Data Name="CloudAssignedTenantDomain">firma.com</Data>
            string xmlPat = string.Format("<Data\\s+Name\\s*=\\s*[\"']{0}[\"'][^>]*>([^<]+)</Data>", Regex.Escape(fieldName));
            Match mx = Regex.Match(text, xmlPat, RegexOptions.IgnoreCase);
            if (mx.Success)
            {
                return mx.Groups[1].Value.Trim();
            }

            // 2. JSON or Key=Value / Key: Value format
            string kvPat = string.Format("\"?{0}\"?\\s*[:=]\\s*\"?([^\"\\r\\n,;<>]+)\"?", Regex.Escape(fieldName));
            Match mk = Regex.Match(text, kvPat, RegexOptions.IgnoreCase);
            if (mk.Success)
            {
                return mk.Groups[1].Value.Trim();
            }

            return "";
        }

        public static bool ParseAutopilotEventPayload(int eventId, string description, string xmlPayload, CloudZtdDetails report, string queryMode)
        {
            if (report == null) return false;
            string combined = (description ?? "") + "\n" + (xmlPayload ?? "");

            // Izvlačenje ključnih polja iz 24H2/25H2 događaja (100, 110, 170, 171, 806, 807 i ostalih)
            string tenantId = ExtractNamedFieldFromEvent(combined, "CloudAssignedTenantId");
            if (!IsValidNonZeroGuid(tenantId))
            {
                Match mTenant = Regex.Match(combined, @"CloudAssignedTenantId[^0-9a-fA-F]*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase);
                if (mTenant.Success && IsValidNonZeroGuid(mTenant.Groups[1].Value))
                {
                    tenantId = mTenant.Groups[1].Value.Trim();
                }
                else
                {
                    tenantId = "";
                }
            }
            else
            {
                tenantId = tenantId.Trim().Trim('{', '}');
            }

            string tenantDomain = ExtractNamedFieldFromEvent(combined, "CloudAssignedTenantDomain");
            string deviceName = ExtractNamedFieldFromEvent(combined, "CloudAssignedDeviceName");
            string ztdRegState = ExtractNamedFieldFromEvent(combined, "ZtdRegistrationState");

            if (!string.IsNullOrEmpty(ztdRegState) && string.IsNullOrEmpty(report.ZtdRegistrationState))
            {
                report.ZtdRegistrationState = ztdRegState;
            }
            if (string.IsNullOrEmpty(deviceName))
            {
                Match waadMatch = WaadDeviceNameRegex.Match(combined);
                if (waadMatch.Success)
                {
                    deviceName = waadMatch.Value.ToUpperInvariant();
                }
            }

            if (!string.IsNullOrEmpty(tenantId) && string.IsNullOrEmpty(report.ProfileTenantId))
            {
                report.ProfileTenantId = tenantId;
            }
            if (!string.IsNullOrEmpty(tenantDomain) && string.IsNullOrEmpty(report.ProfileTenantDomain))
            {
                report.ProfileTenantDomain = tenantDomain;
            }
            if (!string.IsNullOrEmpty(deviceName) && string.IsNullOrEmpty(report.ProfileDeviceName))
            {
                report.ProfileDeviceName = deviceName;
            }

            bool isModernProfileEvent = (eventId == 100 || eventId == 110 || eventId == 170 || eventId == 171);
            bool hasRegisteredEvidence =
                eventId == 806 ||
                !string.IsNullOrEmpty(tenantId) ||
                !string.IsNullOrEmpty(tenantDomain) ||
                (!string.IsNullOrEmpty(deviceName) && WaadDeviceNameRegex.IsMatch(deviceName)) ||
                (isModernProfileEvent && !string.IsNullOrEmpty(deviceName)) ||
                (!string.IsNullOrEmpty(ztdRegState) &&
                 ztdRegState.IndexOf("Registered", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 ztdRegState.IndexOf("NotRegistered", StringComparison.OrdinalIgnoreCase) < 0 &&
                 ztdRegState.IndexOf("Unregistered", StringComparison.OrdinalIgnoreCase) < 0);

            if (hasRegisteredEvidence)
            {
                report.OnlineChecked = true;
                report.IsRegistered = true;
                report.ConfirmedFree = false;
                report.QueryMode = queryMode;
                report.RawStatusMessage = string.Format("Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event {0})", eventId);
                return true;
            }

            // ISKLJUČIVO eksplicitni Event 807 ili ZtdDeviceIsNotRegistered daje ConfirmedFree = true.
            // NIKADA za puko ProfileState=NotAssigned!
            if (eventId == 807 || combined.IndexOf("ZtdDeviceIsNotRegistered", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.OnlineChecked = true;
                report.IsRegistered = false;
                report.ConfirmedFree = true;
                report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                report.QueryMode = queryMode;
                report.RawStatusMessage = "Slobodan (Microsoft potvrdio: Event 807 - ZtdDeviceIsNotRegistered)";
                return true;
            }

            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TryReadLiveEventLog(CloudZtdDetails report)
        {
            string logName = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
            EventLogQuery fullQuery = new EventLogQuery(logName, PathType.LogName, "*")
            {
                ReverseDirection = true
            };

            bool foundDecisive = false;
            int enrichRemaining = 0;

            using (EventLogReader reader = new EventLogReader(fullQuery))
            {
                EventRecord record;
                while ((record = reader.ReadEvent()) != null)
                {
                    using (record)
                    {
                        int evId = record.Id;
                        string desc = "";
                        string xml = "";
                        try { desc = record.FormatDescription() ?? ""; } catch { }
                        try { xml = record.ToXml() ?? ""; } catch { }

                        if (!foundDecisive)
                        {
                            if (ParseAutopilotEventPayload(evId, desc, xml, report, "LIVE_EVENT_LOG"))
                            {
                                foundDecisive = true;
                                if (report.IsRegistered && (string.IsNullOrEmpty(report.ProfileTenantDomain) || string.IsNullOrEmpty(report.ProfileTenantId) || string.IsNullOrEmpty(report.ProfileDeviceName)))
                                {
                                    // Nastavi kratko čitanje susednih 100/110/170/171 događaja radi dopune domena/imena uređaja
                                    enrichRemaining = 25;
                                    continue;
                                }
                                return;
                            }
                        }
                        else if (enrichRemaining > 0)
                        {
                            enrichRemaining--;
                            string combined = desc + "\n" + xml;
                            if (string.IsNullOrEmpty(report.ProfileTenantId))
                            {
                                string tid = ExtractNamedFieldFromEvent(combined, "CloudAssignedTenantId");
                                if (IsValidNonZeroGuid(tid)) report.ProfileTenantId = tid.Trim().Trim('{', '}');
                            }
                            if (string.IsNullOrEmpty(report.ProfileTenantDomain))
                            {
                                string dom = ExtractNamedFieldFromEvent(combined, "CloudAssignedTenantDomain");
                                if (!string.IsNullOrEmpty(dom)) report.ProfileTenantDomain = dom;
                            }
                            if (string.IsNullOrEmpty(report.ProfileDeviceName))
                            {
                                string dev = ExtractNamedFieldFromEvent(combined, "CloudAssignedDeviceName");
                                if (!string.IsNullOrEmpty(dev)) report.ProfileDeviceName = dev;
                            }
                            if (enrichRemaining == 0 || (!string.IsNullOrEmpty(report.ProfileTenantDomain) && !string.IsNullOrEmpty(report.ProfileTenantId) && !string.IsNullOrEmpty(report.ProfileDeviceName)))
                            {
                                return;
                            }
                        }
                    }
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryInspectEvtxFile(string ef, CloudZtdDetails report)
        {
            EventLogQuery evQuery = new EventLogQuery(ef, PathType.FilePath, "*")
            {
                ReverseDirection = true
            };
            using (EventLogReader reader = new EventLogReader(evQuery))
            {
                EventRecord rec;
                while ((rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        string desc = "";
                        string xml = "";
                        try { desc = rec.FormatDescription() ?? ""; } catch { }
                        try { xml = rec.ToXml() ?? ""; } catch { }

                        if (ParseAutopilotEventPayload(rec.Id, desc, xml, report, "ACTIVE_ZTD_DIAG"))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
