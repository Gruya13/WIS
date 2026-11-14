using System;
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
        public static CloudZtdDetails Inspect(string targetSerial)
        {
            CloudZtdDetails report = new CloudZtdDetails();

            // 0. Brza provera trajno sačuvanog Pre-OOBE izveštaja i ZTD keša (radi i offline!)
            if (TryLoadCachedReport(targetSerial, report))
            {
                return report;
            }

            // 1. Provera mrežne konekcije ka Microsoft ZTD endpoint-ima
            bool canReachZtd = TestTcpAndTlsConnection("ztd.dds.microsoft.com", 443, 3000);
            bool canReachLive = TestTcpAndTlsConnection("login.live.com", 443, 3000);

            if (!canReachZtd && !canReachLive)
            {
                report.OnlineChecked = false;
                report.RawStatusMessage = "OFFLINE - Microsoft ZTD servis i internet nisu dostupni";
                report.QueryMode = "OFFLINE";
                return report;
            }

            report.OnlineChecked = true;

            // 2. ULTRA-FAST PATH: Provera živog Windows Autopilot Event Dnevnika
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try
                {
                    TryReadLiveEventLog(report);
                }
                catch { }
            }

            // 3. Provera postojećeg profila u ServiceState kešu
            if (!report.IsRegistered && !report.ConfirmedFree)
            {
                try
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string serviceStateJson = Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json");

                    if (File.Exists(serviceStateJson))
                    {
                        string rawJson = File.ReadAllText(serviceStateJson);
                        TryParseZtdJsonContent(rawJson, report, "SERVICE_STATE_CACHE");
                    }
                }
                catch { }
            }

            // 4. Opcioni upit ka lokalnom ServisHub / GDS serveru (ako postoji u radionici)
            if (!report.IsRegistered && !report.ConfirmedFree && !string.IsNullOrEmpty(targetSerial) && targetSerial != "Nepoznato")
            {
                try
                {
                    CheckGdsServerStatus(targetSerial, report);
                }
                catch { }
            }

            // 5. AKTIVNO POZADINSKO BUĐENJE ZTD RADNIKA (ako još nismo dobili eksplicitan odgovor)
            if (!report.IsRegistered && !report.ConfirmedFree)
            {
                TriggerActiveZtdDiagnostics(report);
            }

            return report;
        }

        public static bool TryParseZtdJsonContent(string rawJson, CloudZtdDetails report, string queryMode)
        {
            if (string.IsNullOrEmpty(rawJson) || report == null) return false;

            bool hasTenantKey = Regex.IsMatch(rawJson, "\"CloudAssignedTenantId\"\\s*:", RegexOptions.IgnoreCase);
            if (hasTenantKey)
            {
                string tenantId = ExtractJsonString(rawJson, "CloudAssignedTenantId");
                if (!string.IsNullOrEmpty(tenantId) &&
                    !string.Equals(tenantId, "00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                {
                    report.OnlineChecked = true;
                    report.IsRegistered = true;
                    report.ConfirmedFree = false;
                    report.ProfileTenantId = tenantId;
                    report.ProfileTenantDomain = ExtractJsonString(rawJson, "CloudAssignedTenantDomain");
                    report.ProfileDeviceName = ExtractJsonString(rawJson, "CloudAssignedDeviceName");
                    report.QueryMode = queryMode;
                    report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (" + queryMode + ")";
                    return true;
                }

                // Validan ZTD odgovor bez dodeljenog tenanta znači da uređaj nije registrovan u Autopilot bazi
                report.OnlineChecked = true;
                report.IsRegistered = false;
                report.ConfirmedFree = true;
                report.ErrorCode = "807 - ZtdDeviceIsNotRegistered (ZTD File)";
                report.QueryMode = queryMode;
                report.RawStatusMessage = "Slobodan (Microsoft ZTD profil potvrdio: Uređaj nije registrovan)";
                return true;
            }

            if (Regex.IsMatch(rawJson, "\"ProfileState\"\\s*:\\s*(0|\"NotAssigned\")", RegexOptions.IgnoreCase))
            {
                report.OnlineChecked = true;
                report.IsRegistered = false;
                report.ConfirmedFree = true;
                report.ErrorCode = "807 - ZtdDeviceIsNotRegistered (ZTD File)";
                report.QueryMode = queryMode;
                report.RawStatusMessage = "Slobodan (Microsoft ZTD profil potvrdio: Uređaj nije registrovan)";
                return true;
            }

            return false;
        }

        public static bool TryLoadCachedReport(string targetSerial, CloudZtdDetails report)
        {
            if (report == null) return false;

            try
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] ztdFileCandidates = new string[]
                {
                    @"C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json",
                    Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json")
                };

                for (int i = 0; i < ztdFileCandidates.Length; i++)
                {
                    string ztdPath = ztdFileCandidates[i];
                    try
                    {
                        if (File.Exists(ztdPath))
                        {
                            string rawZtd = File.ReadAllText(ztdPath, Encoding.UTF8);
                            string mode = i == 0 ? "PREOOBE_ZTD_FILE" : "SERVICE_STATE_CACHE";
                            if (TryParseZtdJsonContent(rawZtd, report, mode))
                            {
                                return true;
                            }
                        }
                    }
                    catch { }
                }

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
                        bool cachedRegistered = ExtractJsonBool(rawJson, "is_registered");
                        bool cachedFree = ExtractJsonBool(rawJson, "confirmed_free");
                        string profDom = ExtractJsonString(rawJson, "profile_tenant_domain");
                        if (string.IsNullOrEmpty(profDom)) profDom = ExtractJsonString(rawJson, "tenant_domain");
                        string profId = ExtractJsonString(rawJson, "profile_tenant_id");
                        if (string.IsNullOrEmpty(profId)) profId = ExtractJsonString(rawJson, "tenant_id");
                        string profDev = ExtractJsonString(rawJson, "profile_device_name");
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

                        if (matrixCode == "CONFIRMED_CLEAN" || matrixCode == "RELEASED_LOCAL_TRACES" || cachedFree)
                        {
                            report.OnlineChecked = true;
                            report.IsRegistered = false;
                            report.ConfirmedFree = true;
                            report.ErrorCode = !string.IsNullOrEmpty(errCode) ? errCode : "807 - CachedPreOOBE";
                            report.QueryMode = "PREOOBE_CACHED_REPORT";
                            report.RawStatusMessage = "Slobodan (Potvrđeno iz sačuvanog OOBE izveštaja)";
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
            if (matrixCode == "LOCKED_TENANT" ||
                matrixCode == "HIDDEN_LOCK" ||
                matrixCode == "RELEASED_LOCAL_TRACES" ||
                matrixCode == "CONFIRMED_CLEAN")
            {
                return true;
            }
            return ExtractJsonBool(json, "is_registered") || ExtractJsonBool(json, "confirmed_free");
        }

        public static void SaveCachedReport(AuditorResult result)
        {
            if (result == null) return;

            bool newIsConfirmed = result.CloudZtd != null && (result.CloudZtd.IsRegistered || result.CloudZtd.ConfirmedFree);
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

            try
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string serviceStateJson = Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json");
                string preOobeCopy = @"C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json";
                if (File.Exists(serviceStateJson))
                {
                    string wisLogDir = Path.GetDirectoryName(preOobeCopy);
                    if (!string.IsNullOrEmpty(wisLogDir) && !Directory.Exists(wisLogDir))
                    {
                        Directory.CreateDirectory(wisLogDir);
                    }
                    File.Copy(serviceStateJson, preOobeCopy, true);
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

                    // Pokušaj osnovnog TLS handshaka
                    try
                    {
                        using (SslStream ssl = new SslStream(client.GetStream(), false, (sender, cert, chain, sslPolicyErrors) => true))
                        {
                            ssl.ReadTimeout = timeoutMs;
                            ssl.WriteTimeout = timeoutMs;
                            ssl.AuthenticateAsClient(hostname);
                            return true;
                        }
                    }
                    catch
                    {
                        // Ako TLS baci izuzetak, TCP konekcija je ipak uspela
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
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
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
                                            // Odgovor iz oblaka je stigao!
                                            try { proc.Kill(); } catch { }
                                            return;
                                        }
                                    }
                                    catch { }
                                }

                                // Proveri ServiceState keš fajl
                                try
                                {
                                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                                    string sStateJson = Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json");
                                    if (File.Exists(sStateJson))
                                    {
                                        string rawJson = File.ReadAllText(sStateJson);
                                        if (TryParseZtdJsonContent(rawJson, report, "ACTIVE_ZTD_DIAG"))
                                        {
                                            try { proc.Kill(); } catch { }
                                            return;
                                        }
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
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        using (Process expProc = Process.Start(expPsi))
                        {
                            if (expProc != null && !expProc.WaitForExit(5000))
                            {
                                try { expProc.Kill(); } catch { }
                            }
                        }

                        // Provera raspakovanog AutopilotDDSZTDFile.json
                        string ztdJsonFile = Path.Combine(outDir, "AutopilotDDSZTDFile.json");
                        if (File.Exists(ztdJsonFile))
                        {
                            string rawJson = File.ReadAllText(ztdJsonFile);
                            if (TryParseZtdJsonContent(rawJson, report, "ACTIVE_ZTD_DIAG"))
                            {
                                return;
                            }
                        }

                        // Provera izvučenih .evtx logova
                        string[] evtxFiles = Directory.GetFiles(outDir, "*.evtx");
                        foreach (string ef in evtxFiles)
                        {
                            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                            {
                                try
                                {
                                    if (TryCheckEvtxForEvent807(ef))
                                    {
                                        report.ConfirmedFree = true;
                                        report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                                        report.QueryMode = "ACTIVE_ZTD_DIAG";
                                        report.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uređaj nije registrovan)";
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

                    report.RawStatusMessage = "ZTD dijagnostički upit poslat, čeka se obrada servisa";
                    report.QueryMode = "ACTIVE_ZTD_UNCONFIRMED";
                }
            }
            catch (Exception ex)
            {
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
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(1000);
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
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(timeoutMs);
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
                                    report.IsRegistered = true;
                                    report.ProfileTenantDomain = ExtractJsonString(json, "tenant_domain");
                                    report.ProfileTenantId = ExtractJsonString(json, "tenant_id");
                                    report.GdsServerUrl = url;
                                    report.QueryMode = "GDS_CENTRAL_SERVER";
                                    report.RawStatusMessage = "Uređaj je zaključan prema evidenciji ServisHub baze";
                                    break;
                                }
                                else if (status == "CLEAN")
                                {
                                    report.ConfirmedFree = true;
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

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TryReadLiveEventLog(CloudZtdDetails report)
        {
            string logName = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
            EventLogQuery query = new EventLogQuery(logName, PathType.LogName, "*[System[(EventID=806 or EventID=807)]]")
            {
                ReverseDirection = true
            };

            using (EventLogReader reader = new EventLogReader(query))
            {
                EventRecord record = reader.ReadEvent();
                if (record != null)
                {
                    if (record.Id == 806)
                    {
                        report.IsRegistered = true;
                        report.QueryMode = "LIVE_EVENT_LOG";
                        report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event 806)";
                        return;
                    }
                    else if (record.Id == 807)
                    {
                        report.IsRegistered = false;
                        report.ConfirmedFree = true;
                        report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                        report.QueryMode = "LIVE_EVENT_LOG";
                        report.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uređaj nije registrovan)";
                        return;
                    }
                }
            }

            // Šira provera poslednjih 50 događaja ako 806/807 nisu eksplicitno upisani pod tim ID-om
            EventLogQuery broadQuery = new EventLogQuery(logName, PathType.LogName, "*")
            {
                ReverseDirection = true
            };
            using (EventLogReader broadReader = new EventLogReader(broadQuery))
            {
                int checkedCount = 0;
                EventRecord record;
                while (checkedCount < 50 && (record = broadReader.ReadEvent()) != null)
                {
                    checkedCount++;
                    string desc = "";
                    try { desc = record.FormatDescription() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(desc)) continue;

                    if (desc.IndexOf("ZtdDeviceIsNotRegistered", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Regex.IsMatch(desc, @"ProfileState\s*=\s*NotAssigned", RegexOptions.IgnoreCase))
                    {
                        report.IsRegistered = false;
                        report.ConfirmedFree = true;
                        report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                        report.QueryMode = "LIVE_EVENT_LOG";
                        report.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uređaj nije registrovan)";
                        return;
                    }

                    Match mTenant = Regex.Match(desc, @"CloudAssignedTenantId[^0-9a-fA-F]*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.IgnoreCase);
                    if (mTenant.Success)
                    {
                        string guid = mTenant.Groups[1].Value;
                        if (!string.Equals(guid, "00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                        {
                            report.IsRegistered = true;
                            report.ProfileTenantId = guid;
                            report.QueryMode = "LIVE_EVENT_LOG";
                            report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event Log)";
                            return;
                        }
                    }
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryCheckEvtxForEvent807(string ef)
        {
            EventLogQuery evQuery = new EventLogQuery(ef, PathType.FilePath, "*[System[(EventID=807)]]");
            using (EventLogReader reader = new EventLogReader(evQuery))
            {
                EventRecord rec = reader.ReadEvent();
                return (rec != null);
            }
        }
    }
}
