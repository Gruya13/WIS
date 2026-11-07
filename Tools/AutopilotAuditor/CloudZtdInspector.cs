using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WIS.AutopilotAuditor
{
    public static class CloudZtdInspector
    {
        public static CloudZtdDetails Inspect(string targetSerial)
        {
            CloudZtdDetails report = new CloudZtdDetails();

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
            if (!report.IsRegistered)
            {
                try
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string serviceStateJson = Path.Combine(winDir, @"ServiceState\Autopilot\AutopilotDDSZTDFile.json");

                    if (File.Exists(serviceStateJson))
                    {
                        string rawJson = File.ReadAllText(serviceStateJson);
                        if (!string.IsNullOrEmpty(rawJson))
                        {
                            string tenantId = ExtractJsonString(rawJson, "CloudAssignedTenantId");
                            if (!string.IsNullOrEmpty(tenantId))
                            {
                                report.IsRegistered = true;
                                report.ProfileTenantId = tenantId;
                                report.ProfileTenantDomain = ExtractJsonString(rawJson, "CloudAssignedTenantDomain");
                                report.ProfileDeviceName = ExtractJsonString(rawJson, "CloudAssignedDeviceName");
                                report.QueryMode = "SERVICE_STATE_CACHE";
                                report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi (ServiceState)";
                            }
                        }
                    }
                }
                catch { }
            }

            // 4. Opcioni upit ka lokalnom ServisHub / GDS serveru (ako postoji u radionici)
            if (!report.IsRegistered && !string.IsNullOrEmpty(targetSerial) && targetSerial != "Nepoznato")
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

            try
            {
                Directory.CreateDirectory(outDir);

                // 1. Silent OOBE servisi - obezbedi da su pokrenuti pre slanja upita
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
                                        string tenantId = ExtractJsonString(rawJson, "CloudAssignedTenantId");
                                        if (!string.IsNullOrEmpty(tenantId))
                                        {
                                            report.IsRegistered = true;
                                            report.ProfileTenantId = tenantId;
                                            report.ProfileTenantDomain = ExtractJsonString(rawJson, "CloudAssignedTenantDomain");
                                            report.ProfileDeviceName = ExtractJsonString(rawJson, "CloudAssignedDeviceName");
                                            report.QueryMode = "ACTIVE_ZTD_DIAG";
                                            report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi";
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
                            string tenantId = ExtractJsonString(rawJson, "CloudAssignedTenantId");
                            if (!string.IsNullOrEmpty(tenantId))
                            {
                                report.IsRegistered = true;
                                report.ProfileTenantId = tenantId;
                                report.ProfileTenantDomain = ExtractJsonString(rawJson, "CloudAssignedTenantDomain");
                                report.ProfileDeviceName = ExtractJsonString(rawJson, "CloudAssignedDeviceName");
                                report.QueryMode = "ACTIVE_ZTD_DIAG";
                                report.RawStatusMessage = "Uređaj je 100% REGISTROVAN u Microsoft Autopilot bazi";
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
                    }
                    else if (record.Id == 807)
                    {
                        report.IsRegistered = false;
                        report.ConfirmedFree = true;
                        report.ErrorCode = "807 - ZtdDeviceIsNotRegistered";
                        report.QueryMode = "LIVE_EVENT_LOG";
                        report.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uređaj nije registrovan)";
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
