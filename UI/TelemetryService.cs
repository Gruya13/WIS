using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WIS.UI
{
    public class TelemetryService : IDisposable
    {
        private readonly MainViewModel _viewModel;
        private readonly Dispatcher _dispatcher;
        private readonly object _stateLock = new object();
        private readonly object _logLock = new object();

        private string _hubUrl;
        private string _hubVersion;
        private int _hubPort = 8080;
        private string _clientToken = null;
        private bool _isEnabled = true;
        private bool _isDisposed = false;
        private int _consecutiveFailures = 0;
        private bool _benchmarkFetched = false;
        private string _cachedInstallSessionId = null;

        private const string InstallSessionIdPath = @"C:\setup\install-session-id.txt";

        private readonly List<string> _logBuffer = new List<string>();
        private Timer _heartbeatTimer;
        private Timer _logFlushTimer;

        // Predefinisane servisne IP adrese po specifikaciji
        private static readonly string[] DefaultServiceIps = new string[]
        {
            "192.168.170.250",
            "192.168.170.254",
            "192.168.88.50",
            "192.168.180.50"
        };

        private readonly List<string> _configuredHosts = new List<string>();

        public string HubUrl
        {
            get { lock (_stateLock) return _hubUrl; }
            private set { lock (_stateLock) _hubUrl = value; }
        }

        public string HubVersion
        {
            get { lock (_stateLock) return _hubVersion; }
            private set { lock (_stateLock) _hubVersion = value; }
        }

        public bool IsConnected
        {
            get { return !string.IsNullOrEmpty(HubUrl); }
        }

        public TelemetryService(MainViewModel viewModel, Dispatcher dispatcher = null)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher ?? (Application.Current != null ? Application.Current.Dispatcher : null);
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    LoadConfiguration();
                    if (!_isEnabled) return;

                    DiscoverHub();

                    // Periodično slanje Heartbeat-a svake 2 sekunde
                    _heartbeatTimer = new Timer(OnHeartbeatTimer, null, 2000, 2000);

                    // Periodično slanje logova svakih 1.5 sekundi (1500 ms)
                    _logFlushTimer = new Timer(OnLogFlushTimer, null, 1500, 1500);
                }
                catch { }
            });
        }

        public void Stop()
        {
            try
            {
                if (_heartbeatTimer != null)
                {
                    _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _heartbeatTimer.Dispose();
                    _heartbeatTimer = null;
                }
                if (_logFlushTimer != null)
                {
                    _logFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _logFlushTimer.Dispose();
                    _logFlushTimer = null;
                }
                FlushLogs();
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                Stop();
            }
        }

        #region Konfiguracija i INI čitač

        private void LoadConfiguration()
        {
            try
            {
                var candidatePaths = new List<string>
                {
                    @"C:\setup\Wifi-Servis.ini",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wifi-Servis.ini"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\Wifi-Servis.ini"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\Wifi-Servis.ini")
                };

                string iniPath = null;
                foreach (var p in candidatePaths)
                {
                    if (File.Exists(p))
                    {
                        iniPath = p;
                        break;
                    }
                }

                if (iniPath != null)
                {
                    ParseIniFile(iniPath);
                }
            }
            catch { }
        }

        private void ParseIniFile(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                string currentSection = "";

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    if (currentSection == "servishub" || currentSection == "gdsserver")
                    {
                        int eqIdx = line.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            string key = line.Substring(0, eqIdx).Trim().ToLowerInvariant();
                            string val = line.Substring(eqIdx + 1).Trim();

                            if (key == "enabled")
                            {
                                if (val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase))
                                {
                                    _isEnabled = false;
                                }
                            }
                            else if (key == "serverhost")
                            {
                                string[] tokens = val.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var t in tokens)
                                {
                                    string host = t.Trim();
                                    if (!string.IsNullOrEmpty(host) && !_configuredHosts.Contains(host))
                                    {
                                        _configuredHosts.Add(host);
                                    }
                                }
                            }
                            else if (key == "serverport")
                            {
                                int p;
                                if (int.TryParse(val, out p) && p > 0)
                                {
                                    _hubPort = p;
                                }
                            }
                            else if (key == "clienttoken")
                            {
                                lock (_stateLock)
                                {
                                    _clientToken = val;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Discovery (UDP & Multi-IP Probe)

        public bool DiscoverHub()
        {
            if (!_isEnabled) return false;

            try
            {
                var candidateIps = new List<string>();

                // 1. Domaćini iz konfiguracionog fajla (osim "auto")
                foreach (var h in _configuredHosts)
                {
                    if (!h.Equals("auto", StringComparison.OrdinalIgnoreCase) && !candidateIps.Contains(h))
                    {
                        candidateIps.Add(h);
                    }
                }

                // 2. UDP Auto-Discovery (ako je dozvoljeno ili je "auto" u listi)
                bool allowAuto = _configuredHosts.Contains("auto") || _configuredHosts.Count == 0;
                if (allowAuto)
                {
                    string discoveredIp = RunUdpDiscovery();
                    if (!string.IsNullOrEmpty(discoveredIp) && !candidateIps.Contains(discoveredIp))
                    {
                        candidateIps.Insert(0, discoveredIp);
                    }
                }

                // 3. Dodavanje predefinisanih servisnih adresa
                foreach (var sip in DefaultServiceIps)
                {
                    if (!candidateIps.Contains(sip))
                    {
                        candidateIps.Add(sip);
                    }
                }

                // 4. Default Gateway adrese sa mrežnih kartica
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        var ipProps = ni.GetIPProperties();
                        foreach (var gw in ipProps.GatewayAddresses)
                        {
                            if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(gw.Address))
                            {
                                string gwStr = gw.Address.ToString();
                                if (!gwStr.StartsWith("0.") && !candidateIps.Contains(gwStr))
                                {
                                    candidateIps.Add(gwStr);
                                }
                            }
                        }
                    }
                }
                catch { }

                // 5. Brzo sondiranje kandidata (Fast HTTP Probe: prioritet _hubPort, zatim 8080 pa 8000)
                int[] portsToProbe = (_hubPort == 8080)
                    ? new int[] { 8080, 8000 }
                    : ((_hubPort == 8000) ? new int[] { 8080, 8000 } : new int[] { _hubPort, 8080, 8000 });

                foreach (var candIp in candidateIps)
                {
                    foreach (var p in portsToProbe)
                    {
                        string testUrl = string.Format("http://{0}:{1}", candIp, p);
                        string discoveredVer;
                        if (ProbeHub(testUrl, out discoveredVer))
                        {
                            _hubPort = p;
                            HubUrl = testUrl;
                            if (!string.IsNullOrEmpty(discoveredVer))
                            {
                                HubVersion = discoveredVer;
                            }
                            _consecutiveFailures = 0;
                            NotifyHubStatusChanged(testUrl, HubVersion, true);
                            if (!_benchmarkFetched)
                            {
                                FetchBenchmarkBaselineAsync(testUrl);
                            }
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private string RunUdpDiscovery()
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = 450;

                byte[] reqBytes = Encoding.UTF8.GetBytes(string.Format("GWADT_DISCOVER client={0}", Environment.MachineName));
                var broadcastTargets = new List<IPEndPoint>
                {
                    new IPEndPoint(IPAddress.Broadcast, 8888)
                };

                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        var ipProps = ni.GetIPProperties();
                        foreach (var u in ipProps.UnicastAddresses)
                        {
                            if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                            {
                                byte[] ipBytes = u.Address.GetAddressBytes();
                                if (ipBytes.Length == 4 && ipBytes[0] != 169)
                                {
                                    byte[] bcastBytes = new byte[] { ipBytes[0], ipBytes[1], ipBytes[2], 255 };
                                    broadcastTargets.Add(new IPEndPoint(new IPAddress(bcastBytes), 8888));
                                }
                            }
                        }
                    }
                }
                catch { }

                foreach (var ep in broadcastTargets)
                {
                    try { udp.Send(reqBytes, reqBytes.Length, ep); } catch { }
                }

                IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] respBytes = udp.Receive(ref remoteEp);
                string respStr = Encoding.UTF8.GetString(respBytes);

                if (!string.IsNullOrEmpty(respStr) && respStr.Contains("GWADT_HUB_OFFER"))
                {
                    var portMatch = Regex.Match(respStr, @"port=([0-9]+)");
                    if (portMatch.Success)
                    {
                        int p;
                        if (int.TryParse(portMatch.Groups[1].Value, out p) && p > 0)
                        {
                            _hubPort = p;
                        }
                    }
                    return remoteEp.Address.ToString();
                }
            }
            catch { }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
            }

            return null;
        }

        private bool ProbeHub(string baseUrl, out string hubVersion)
        {
            hubVersion = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/status");
                req.Method = "GET";
                req.Timeout = 400;
                req.ReadWriteTimeout = 400;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 200 && code < 400)
                    {
                        try
                        {
                            using (var stream = resp.GetResponseStream())
                            {
                                if (stream != null)
                                {
                                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                                    {
                                        string body = reader.ReadToEnd();
                                        var m = Regex.Match(body, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                                        if (m.Success)
                                        {
                                            hubVersion = m.Groups[1].Value.Trim();
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Lifecycle Events

        public void SendLifecycleEvent(string eventType, Dictionary<string, object> data)
        {
            if (!_isEnabled) return;

            Dictionary<string, object> hwSnapshot = BuildHardwarePayload();
            string serialSnapshot = hwSnapshot.ContainsKey("serial") && hwSnapshot["serial"] != null
                ? hwSnapshot["serial"].ToString()
                : "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (string.IsNullOrEmpty(HubUrl))
                    {
                        DiscoverHub();
                        if (string.IsNullOrEmpty(HubUrl)) return;
                    }

                    var payload = new Dictionary<string, object>
                    {
                        { "event", eventType },
                        { "computer_name", Environment.MachineName },
                        { "timestamp", DateTime.Now.ToString("o") }
                    };

                    if (data != null)
                    {
                        foreach (var kvp in data)
                        {
                            payload[kvp.Key] = kvp.Value;
                        }
                    }

                    if (!payload.ContainsKey("hw") || payload["hw"] == null)
                    {
                        payload["hw"] = hwSnapshot;
                    }
                    if ((!payload.ContainsKey("serial_number") || IsPlaceholderHwValue(payload["serial_number"] != null ? payload["serial_number"].ToString() : ""))
                        && !IsPlaceholderHwValue(serialSnapshot))
                    {
                        payload["serial_number"] = serialSnapshot;
                    }

                    if (string.Equals(eventType, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventType, "FINAL_REPORT", StringComparison.OrdinalIgnoreCase))
                    {
                        EnrichLicenseTelemetryPayload(payload);
                    }

                    string jsonBody = JsonHelper.Serialize(payload);
                    string url = HubUrl + "/api/lifecycle";
                    HttpPost(url, jsonBody, 1500);
                }
                catch { }
            });
        }

        private static readonly Regex LicenseKeyMaskRegex = new Regex(
            "([A-Za-z0-9]{5}-){4}([A-Za-z0-9]{5})",
            RegexOptions.Compiled);

        private static string MaskLicenseKeyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return LicenseKeyMaskRegex.Replace(text, "*****-*****-*****-*****-$2");
        }

        private static bool HasNonNullKey(Dictionary<string, object> payload, string key)
        {
            object val;
            return payload != null && payload.TryGetValue(key, out val) && val != null;
        }

        private static bool TryReadJsonBool(JsonNode root, JsonNode licNode, bool hasLicObj, string key, out bool value)
        {
            if (root != null && root.ContainsKey(key) && !root[key].IsNull)
            {
                value = root[key].AsBool(false);
                return true;
            }
            if (hasLicObj && licNode != null && licNode.ContainsKey(key) && !licNode[key].IsNull)
            {
                value = licNode[key].AsBool(false);
                return true;
            }
            value = false;
            return false;
        }

        private static bool TryReadJsonString(JsonNode root, JsonNode licNode, bool hasLicObj, string key, out string value)
        {
            if (root != null && root.ContainsKey(key) && !root[key].IsNull)
            {
                value = root[key].AsString("");
                return true;
            }
            if (hasLicObj && licNode != null && licNode.ContainsKey(key) && !licNode[key].IsNull)
            {
                value = licNode[key].AsString("");
                return true;
            }
            value = "";
            return false;
        }

        private static void MaskPayloadField(Dictionary<string, object> payload, string key)
        {
            if (HasNonNullKey(payload, key) && payload[key] is string)
            {
                payload[key] = MaskLicenseKeyText((string)payload[key]);
            }
        }

        private static void MaskExistingDictField(System.Collections.IDictionary dict, string key)
        {
            if (dict != null && dict.Contains(key) && dict[key] is string)
            {
                dict[key] = MaskLicenseKeyText((string)dict[key]);
            }
        }

        /// <summary>
        /// Dopunjava završni telemetrijski payload strukturisanim statusom aktivacije Windows-a i Office-a
        /// iz C:\setup\license-status.json (uz fallback na C:\setup\servisni_karton.json), uz strogo maskiranje ključeva.
        /// </summary>
        private void EnrichLicenseTelemetryPayload(Dictionary<string, object> payload)
        {
            if (payload == null) return;

            try
            {
                // Odbrana po dubini: maskiraj eventualne product ključeve ako su već prosleđeni u payload-u
                MaskPayloadField(payload, "office_license_detail");
                MaskPayloadField(payload, "office_license_method");
                MaskPayloadField(payload, "windows_license_detail");
                MaskPayloadField(payload, "windows_license_method");
                MaskPayloadField(payload, "windows_edition");

                bool hasWinActivated = false;
                bool winActivated = false;
                bool hasWinOa3Present = false;
                bool winOa3Present = false;
                bool hasWinEdition = false;
                string winEdition = "";
                bool hasWinMethod = false;
                string winLicenseMethod = "";
                bool hasWinDetail = false;
                string winLicenseDetail = "";
                bool hasOfficeInstalled = false;
                bool officeInstalled = false;
                bool hasOfficeActivated = false;
                bool officeActivated = false;
                bool hasMethod = false;
                string licenseMethod = "";
                bool hasDetail = false;
                string licenseDetail = "";
                bool hasMode = false;
                string activationMode = "";

                string[] candidateFiles = new string[]
                {
                    @"C:\setup\license-status.json",
                    @"C:\setup\servisni_karton.json"
                };

                for (int i = 0; i < candidateFiles.Length; i++)
                {
                    string filePath = candidateFiles[i];
                    try
                    {
                        if (!File.Exists(filePath)) continue;
                        string json = File.ReadAllText(filePath, Encoding.UTF8);
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        JsonNode root = JsonHelper.Parse(json);
                        if (root == null || !root.IsObject) continue;

                        JsonNode licNode = root["licenses"];
                        bool hasLicObj = licNode != null && licNode.IsObject;
                        JsonNode appsNode = root["apps"];
                        bool hasAppsObj = appsNode != null && appsNode.IsObject;

                        if (!hasWinActivated)
                        {
                            if (TryReadJsonBool(root, licNode, hasLicObj, "windows_activated", out winActivated))
                            {
                                hasWinActivated = true;
                            }
                            else if (hasLicObj && licNode.ContainsKey("windows") && !licNode["windows"].IsNull)
                            {
                                winActivated = licNode["windows"].AsBool(false);
                                hasWinActivated = true;
                            }
                        }

                        if (!hasWinOa3Present)
                        {
                            if (TryReadJsonBool(root, licNode, hasLicObj, "windows_oa3_present", out winOa3Present))
                            {
                                hasWinOa3Present = true;
                            }
                        }

                        if (!hasWinEdition)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "windows_edition", out winEdition))
                            {
                                hasWinEdition = true;
                            }
                        }

                        if (!hasWinMethod)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "windows_license_method", out winLicenseMethod))
                            {
                                hasWinMethod = true;
                            }
                        }

                        if (!hasWinDetail)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "windows_license_detail", out winLicenseDetail))
                            {
                                hasWinDetail = true;
                            }
                        }

                        if (!hasOfficeInstalled)
                        {
                            if (TryReadJsonBool(root, licNode, hasLicObj, "office_installed", out officeInstalled))
                            {
                                hasOfficeInstalled = true;
                            }
                            else if (hasAppsObj && appsNode.ContainsKey("office") && !appsNode["office"].IsNull)
                            {
                                officeInstalled = appsNode["office"].AsBool(false);
                                hasOfficeInstalled = true;
                            }
                        }

                        if (!hasOfficeActivated)
                        {
                            if (TryReadJsonBool(root, licNode, hasLicObj, "office_activated", out officeActivated))
                            {
                                hasOfficeActivated = true;
                            }
                            else if (hasLicObj && licNode.ContainsKey("office") && !licNode["office"].IsNull)
                            {
                                officeActivated = licNode["office"].AsBool(false);
                                hasOfficeActivated = true;
                            }
                        }

                        if (!hasMethod)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "office_license_method", out licenseMethod))
                            {
                                hasMethod = true;
                            }
                            else if (hasLicObj && licNode.ContainsKey("method") && !licNode["method"].IsNull)
                            {
                                licenseMethod = licNode["method"].AsString("");
                                hasMethod = true;
                            }
                        }

                        if (!hasDetail)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "office_license_detail", out licenseDetail))
                            {
                                hasDetail = true;
                            }
                            else if (hasLicObj && licNode.ContainsKey("detail") && !licNode["detail"].IsNull)
                            {
                                licenseDetail = licNode["detail"].AsString("");
                                hasDetail = true;
                            }
                        }

                        if (!hasMode)
                        {
                            if (TryReadJsonString(root, licNode, hasLicObj, "office_activation_mode", out activationMode))
                            {
                                hasMode = true;
                            }
                        }
                    }
                    catch { }
                }

                if (hasDetail) licenseDetail = MaskLicenseKeyText(licenseDetail);
                if (hasMethod) licenseMethod = MaskLicenseKeyText(licenseMethod);
                if (hasWinEdition) winEdition = MaskLicenseKeyText(winEdition);
                if (hasWinMethod) winLicenseMethod = MaskLicenseKeyText(winLicenseMethod);
                if (hasWinDetail) winLicenseDetail = MaskLicenseKeyText(winLicenseDetail);

                bool hasFileLicenseData = hasWinActivated || hasWinOa3Present || hasWinEdition || hasWinMethod || hasWinDetail ||
                                          hasOfficeInstalled || hasOfficeActivated || hasMethod || hasDetail;
                bool hasPayloadLicenseData = HasNonNullKey(payload, "windows_activated") ||
                                             HasNonNullKey(payload, "windows_oa3_present") ||
                                             HasNonNullKey(payload, "windows_edition") ||
                                             HasNonNullKey(payload, "windows_license_method") ||
                                             HasNonNullKey(payload, "windows_license_detail") ||
                                             HasNonNullKey(payload, "office_installed") ||
                                             HasNonNullKey(payload, "office_activated") ||
                                             HasNonNullKey(payload, "office_license_method") ||
                                             HasNonNullKey(payload, "office_license_detail");

                if (!hasFileLicenseData && !hasPayloadLicenseData)
                {
                    return;
                }

                if (hasWinActivated && !HasNonNullKey(payload, "windows_activated"))
                {
                    payload["windows_activated"] = winActivated;
                }

                if (hasWinOa3Present && !HasNonNullKey(payload, "windows_oa3_present"))
                {
                    payload["windows_oa3_present"] = winOa3Present;
                }

                if (hasWinEdition && !string.IsNullOrEmpty(winEdition) && !HasNonNullKey(payload, "windows_edition"))
                {
                    payload["windows_edition"] = winEdition;
                }

                if (!HasNonNullKey(payload, "windows_license_method") &&
                    (hasWinMethod || hasWinActivated || hasWinOa3Present || hasWinEdition || hasWinDetail))
                {
                    payload["windows_license_method"] = hasWinMethod ? (winLicenseMethod ?? "") : "";
                }

                if (!HasNonNullKey(payload, "windows_license_detail") &&
                    (hasWinDetail || hasWinActivated || hasWinOa3Present || hasWinEdition || hasWinMethod))
                {
                    payload["windows_license_detail"] = hasWinDetail ? (winLicenseDetail ?? "") : "";
                }

                if (!HasNonNullKey(payload, "office_installed") && (hasOfficeInstalled || hasOfficeActivated))
                {
                    payload["office_installed"] = hasOfficeInstalled ? officeInstalled : officeActivated;
                }

                if (hasOfficeActivated && !HasNonNullKey(payload, "office_activated"))
                {
                    payload["office_activated"] = officeActivated;
                }

                if (!HasNonNullKey(payload, "office_license_method"))
                {
                    payload["office_license_method"] = hasMethod ? (licenseMethod ?? "") : "";
                }

                if (!HasNonNullKey(payload, "office_license_detail"))
                {
                    payload["office_license_detail"] = hasDetail ? (licenseDetail ?? "") : "";
                }

                if (hasMode && !string.IsNullOrEmpty(activationMode) && !HasNonNullKey(payload, "office_activation_mode"))
                {
                    payload["office_activation_mode"] = activationMode;
                }

                if (!HasNonNullKey(payload, "licenses"))
                {
                    var licensesDict = new Dictionary<string, object>();

                    if (HasNonNullKey(payload, "windows_activated"))
                    {
                        bool winVal = payload["windows_activated"] is bool ? (bool)payload["windows_activated"] : winActivated;
                        licensesDict["windows"] = winVal;
                        licensesDict["windows_activated"] = winVal;
                    }

                    if (HasNonNullKey(payload, "windows_oa3_present"))
                    {
                        bool oa3Val = payload["windows_oa3_present"] is bool ? (bool)payload["windows_oa3_present"] : winOa3Present;
                        licensesDict["windows_oa3_present"] = oa3Val;
                    }

                    if (HasNonNullKey(payload, "windows_edition"))
                    {
                        licensesDict["windows_edition"] = MaskLicenseKeyText(payload["windows_edition"].ToString());
                    }

                    if (HasNonNullKey(payload, "windows_license_method"))
                    {
                        licensesDict["windows_license_method"] = MaskLicenseKeyText(payload["windows_license_method"].ToString());
                    }

                    if (HasNonNullKey(payload, "windows_license_detail"))
                    {
                        licensesDict["windows_license_detail"] = MaskLicenseKeyText(payload["windows_license_detail"].ToString());
                    }

                    if (HasNonNullKey(payload, "office_installed"))
                    {
                        bool offInstVal = payload["office_installed"] is bool ? (bool)payload["office_installed"] : officeInstalled;
                        licensesDict["office_installed"] = offInstVal;
                    }

                    if (HasNonNullKey(payload, "office_activated"))
                    {
                        bool offActVal = payload["office_activated"] is bool ? (bool)payload["office_activated"] : officeActivated;
                        licensesDict["office"] = offActVal;
                        licensesDict["office_activated"] = offActVal;
                    }

                    string finalMethod = HasNonNullKey(payload, "office_license_method")
                        ? MaskLicenseKeyText(payload["office_license_method"].ToString())
                        : "";
                    string finalDetail = HasNonNullKey(payload, "office_license_detail")
                        ? MaskLicenseKeyText(payload["office_license_detail"].ToString())
                        : "";
                    licensesDict["office_license_method"] = finalMethod;
                    licensesDict["office_license_detail"] = finalDetail;

                    if (HasNonNullKey(payload, "office_activation_mode"))
                    {
                        licensesDict["office_activation_mode"] = payload["office_activation_mode"].ToString();
                    }

                    payload["licenses"] = licensesDict;
                }
                else
                {
                    var existingLicenses = payload["licenses"] as System.Collections.IDictionary;
                    if (existingLicenses != null && !existingLicenses.IsReadOnly)
                    {
                        MaskExistingDictField(existingLicenses, "office_license_detail");
                        MaskExistingDictField(existingLicenses, "office_license_method");
                        MaskExistingDictField(existingLicenses, "detail");
                        MaskExistingDictField(existingLicenses, "windows_license_detail");
                        MaskExistingDictField(existingLicenses, "windows_license_method");
                        MaskExistingDictField(existingLicenses, "windows_edition");

                        if (HasNonNullKey(payload, "windows_oa3_present") && !existingLicenses.Contains("windows_oa3_present"))
                        {
                            existingLicenses["windows_oa3_present"] = payload["windows_oa3_present"];
                        }
                        if (HasNonNullKey(payload, "windows_edition") && !existingLicenses.Contains("windows_edition"))
                        {
                            existingLicenses["windows_edition"] = payload["windows_edition"];
                        }
                        if (HasNonNullKey(payload, "windows_license_method") && !existingLicenses.Contains("windows_license_method"))
                        {
                            existingLicenses["windows_license_method"] = payload["windows_license_method"];
                        }
                        if (HasNonNullKey(payload, "windows_license_detail") && !existingLicenses.Contains("windows_license_detail"))
                        {
                            existingLicenses["windows_license_detail"] = payload["windows_license_detail"];
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Heartbeat

        private void OnHeartbeatTimer(object state)
        {
            if (!_isEnabled) return;

            try
            {
                if (string.IsNullOrEmpty(HubUrl))
                {
                    DiscoverHub();
                    if (string.IsNullOrEmpty(HubUrl)) return;
                }

                var payload = CollectHeartbeatData();
                string jsonBody = JsonHelper.Serialize(payload);
                string url = HubUrl + "/api/heartbeat";

                bool ok;
                string respBody = HttpPostWithResponse(url, jsonBody, 2500, out ok);
                if (!ok)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 2)
                    {
                        HubUrl = null;
                        _consecutiveFailures = 0;
                        NotifyHubStatusChanged(null, HubVersion, false);
                    }
                }
                else
                {
                    _consecutiveFailures = 0;
                    ProcessHeartbeatCommands(respBody);
                }
            }
            catch
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 2)
                {
                    HubUrl = null;
                    _consecutiveFailures = 0;
                    NotifyHubStatusChanged(null, HubVersion, false);
                }
            }
        }

        public static bool IsPlaceholderHwValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return true;
            string trimmed = val.Trim();
            if (trimmed.Length < 2) return true;
            if (trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith("…", StringComparison.Ordinal))
            {
                return true;
            }
            string low = trimmed.ToLowerInvariant();
            if (low == "n/a" || low == "na" || low == "unknown" || low == "none" || low == "null" ||
                low == "nepoznat model" || low == "nepoznat serijski" || low == "nepoznato" ||
                low == "standardni pc" || low == "genericpc" || low == "system product name" ||
                low == "system serial number" || low == "default string" ||
                low == "to be filled by o.e.m." || low == "0" || low == "123456789" ||
                low == "未知" || low == "未知设备" || low == "未知型号" || low == "未启动" || low == "不可用")
            {
                return true;
            }
            if (low.StartsWith("učitavanje") || low.StartsWith("ucitavanje") ||
                low.StartsWith("detekcija") || low.StartsWith("očitavanje") || low.StartsWith("ocitavanje") ||
                low.StartsWith("provera") || low.StartsWith("povezivanje") ||
                low.StartsWith("loading") || low.StartsWith("detecting") ||
                low.StartsWith("reading") || low.StartsWith("checking") ||
                low.StartsWith("to be filled") ||
                low.Contains("加载中") || low.Contains("正在加载") ||
                low.Contains("检测中") || low.Contains("正在检测") ||
                low.Contains("读取中") || low.Contains("正在读取"))
            {
                return true;
            }
            return false;
        }

        private string GetResolvedSerialNumber()
        {
            if (_viewModel == null) return "";
            string serial = _viewModel.SerialNumber ?? "";
            if (IsPlaceholderHwValue(serial) && _viewModel.Autopilot != null && !IsPlaceholderHwValue(_viewModel.Autopilot.HwSerialNumber))
            {
                serial = _viewModel.Autopilot.HwSerialNumber.Trim();
            }
            if (IsPlaceholderHwValue(serial) && _viewModel.HardwareInfo != null && !IsPlaceholderHwValue(_viewModel.HardwareInfo.SerialNumber))
            {
                serial = _viewModel.HardwareInfo.SerialNumber.Trim();
            }
            return IsPlaceholderHwValue(serial) ? "" : serial.Trim();
        }

        private Dictionary<string, object> BuildHardwarePayload()
        {
            var hw = new Dictionary<string, object>();
            if (_viewModel == null) return hw;

            string manufacturer = "";
            if (_viewModel.Autopilot != null && !IsPlaceholderHwValue(_viewModel.Autopilot.HwManufacturer))
            {
                manufacturer = _viewModel.Autopilot.HwManufacturer.Trim();
            }

            string model = _viewModel.ComputerModel ?? "";
            if (IsPlaceholderHwValue(model) && _viewModel.Autopilot != null && !IsPlaceholderHwValue(_viewModel.Autopilot.HwModel))
            {
                model = _viewModel.Autopilot.HwModel.Trim();
            }
            if (IsPlaceholderHwValue(model) && _viewModel.HardwareInfo != null && !IsPlaceholderHwValue(_viewModel.HardwareInfo.MotherboardModel))
            {
                model = _viewModel.HardwareInfo.MotherboardModel.Trim();
            }
            model = IsPlaceholderHwValue(model) ? "" : model.Trim();

            string serial = GetResolvedSerialNumber();

            if (!string.IsNullOrEmpty(manufacturer))
            {
                hw["manufacturer"] = manufacturer;
            }
            hw["model"] = model;
            hw["serial"] = serial;
            hw["cpu"] = _viewModel.CpuInfo ?? "";
            hw["ram"] = _viewModel.RamInfo ?? "";
            hw["disk"] = _viewModel.DiskInfo ?? "";
            hw["battery"] = _viewModel.BatteryStatus ?? "";
            string disp = (_viewModel.HardwareInfo != null) ? (_viewModel.HardwareInfo.OverviewResolution ?? "") : "";
            hw["display"] = IsPlaceholderHwValue(disp) ? "" : disp.Trim();
            return hw;
        }

        private string GetOrCreateInstallSessionId()
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrEmpty(_cachedInstallSessionId))
                {
                    return _cachedInstallSessionId;
                }
                try
                {
                    if (File.Exists(InstallSessionIdPath))
                    {
                        string existing = (File.ReadAllText(InstallSessionIdPath, Encoding.UTF8) ?? "").Trim();
                        if (!string.IsNullOrEmpty(existing))
                        {
                            _cachedInstallSessionId = existing;
                            return _cachedInstallSessionId;
                        }
                    }
                    string dir = Path.GetDirectoryName(InstallSessionIdPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string generated = Guid.NewGuid().ToString("D");
                    File.WriteAllText(InstallSessionIdPath, generated, new UTF8Encoding(false));
                    _cachedInstallSessionId = generated;
                    return _cachedInstallSessionId;
                }
                catch
                {
                    _cachedInstallSessionId = Guid.NewGuid().ToString("D");
                    return _cachedInstallSessionId;
                }
            }
        }

        private Dictionary<string, object> CollectHeartbeatData()
        {
            var data = new Dictionary<string, object>();
            data["computer_name"] = Environment.MachineName;
            data["timestamp"] = DateTime.Now.ToString("o");
            string sessionId = GetOrCreateInstallSessionId();
            if (!string.IsNullOrEmpty(sessionId))
            {
                data["install_session_id"] = sessionId;
            }

            if (_viewModel != null)
            {
                data["phase"] = _viewModel.CurrentPhase;
                data["phase_name"] = _viewModel.PhaseName ?? "";
                data["live_status"] = _viewModel.LiveStatus ?? "";
                data["worker_progress"] = _viewModel.Progress;

                // Hardware
                data["hw"] = BuildHardwarePayload();

                // Network
                var net = new Dictionary<string, object>
                {
                    { "wifi_ssid", _viewModel.WifiSsid ?? "" },
                    { "ip", _viewModel.IpAddress ?? "" },
                    { "connection_type", _viewModel.ConnectionType ?? "" },
                    { "nas_latency", _viewModel.NasLatency ?? "" },
                    { "nas_status", _viewModel.NasStatus ?? "" },
                    { "downloaded_mb", _viewModel.DownloadedMb ?? "" }
                };
                data["network"] = net;

                // Apps (IMP-39: čitanje iz thread-safe keša bez blokirajućeg _dispatcher.Invoke)
                Dictionary<string, string> appsDict = _viewModel.GetAppsStatusSnapshot();
                data["apps"] = appsDict;

                // Diagnostics
                var diag = new Dictionary<string, object>
                {
                    { "wear", _viewModel.DiskWear },
                    { "smart_status", _viewModel.SmartStatus ?? "" },
                    { "battery_health", _viewModel.BatteryHealth },
                    { "cpu_usage", _viewModel.CpuUsage ?? "" },
                    { "ram_usage", _viewModel.RamUsage ?? "" }
                };
                data["diag"] = diag;
            }

            return data;
        }

        #endregion

        #region Log Streaming & Batching

        public void PushLog(string logLine)
        {
            if (string.IsNullOrWhiteSpace(logLine) || !_isEnabled) return;

            bool shouldFlush = false;
            lock (_logLock)
            {
                _logBuffer.Add(logLine.Trim());
                if (_logBuffer.Count >= 10)
                {
                    shouldFlush = true;
                }
            }

            if (shouldFlush)
            {
                ThreadPool.QueueUserWorkItem(_ => FlushLogs());
            }
        }

        private void OnLogFlushTimer(object state)
        {
            FlushLogs();
        }

        public void FlushLogs()
        {
            if (!_isEnabled) return;

            List<string> linesToSend = null;
            lock (_logLock)
            {
                if (_logBuffer.Count == 0) return;
                linesToSend = new List<string>(_logBuffer);
                _logBuffer.Clear();
            }

            if (linesToSend == null || linesToSend.Count == 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (string.IsNullOrEmpty(HubUrl))
                    {
                        DiscoverHub();
                        if (string.IsNullOrEmpty(HubUrl))
                        {
                            RequeueLogs(linesToSend);
                            return;
                        }
                    }

                    var payload = new Dictionary<string, object>
                    {
                        { "computer_name", Environment.MachineName },
                        { "lines", linesToSend },
                        { "timestamp", DateTime.Now.ToString("o") }
                    };
                    string resolvedSerial = GetResolvedSerialNumber();
                    if (!string.IsNullOrEmpty(resolvedSerial))
                    {
                        payload["serial_number"] = resolvedSerial;
                    }
                    string sessionId = GetOrCreateInstallSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        payload["install_session_id"] = sessionId;
                    }

                    string jsonBody = JsonHelper.Serialize(payload);
                    string url = HubUrl + "/api/log";

                    bool ok = HttpPost(url, jsonBody, 1200);
                    if (!ok)
                    {
                        RequeueLogs(linesToSend);
                        _consecutiveFailures++;
                        if (_consecutiveFailures >= 2)
                        {
                            HubUrl = null;
                            _consecutiveFailures = 0;
                        }
                    }
                }
                catch
                {
                    RequeueLogs(linesToSend);
                }
            });
        }

        private void RequeueLogs(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            lock (_logLock)
            {
                if (_logBuffer.Count < 200)
                {
                    _logBuffer.InsertRange(0, lines);
                    while (_logBuffer.Count > 200)
                    {
                        _logBuffer.RemoveAt(_logBuffer.Count - 1);
                    }
                }
            }
        }

        #endregion

        #region HTTP Helpers

        private void ProcessHeartbeatCommands(string respBody)
        {
            if (string.IsNullOrWhiteSpace(respBody)) return;
            if (respBody.IndexOf("\"pending_commands\"", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (Regex.IsMatch(respBody, "\"pending_commands\"\\s*:\\s*\\[\\s*\\]", RegexOptions.IgnoreCase)) return;

            try
            {
                if (!Directory.Exists(@"C:\setup"))
                {
                    Directory.CreateDirectory(@"C:\setup");
                }
                string cmdFile = @"C:\setup\pending-hub-commands.json";
                File.WriteAllText(cmdFile, respBody, Encoding.UTF8);

                if (Regex.IsMatch(respBody, "\"(?:action|command|type)\"\\s*:\\s*\"skip_step\"", RegexOptions.IgnoreCase) ||
                    respBody.IndexOf("\"skip_step\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { File.WriteAllText(@"C:\setup\.skip", "hub_remote_skip", Encoding.UTF8); } catch { }
                }
            }
            catch { }
        }

        private string GetClientToken()
        {
            string tok = null;
            lock (_stateLock)
            {
                tok = _clientToken;
            }
            if (!string.IsNullOrWhiteSpace(tok))
            {
                return tok.Trim();
            }
            try
            {
                string envTok = Environment.GetEnvironmentVariable("SERVIS_HUB_CLIENT_TOKEN");
                if (!string.IsNullOrWhiteSpace(envTok))
                {
                    return envTok.Trim();
                }
            }
            catch { }
            return null;
        }

        private static object JsonNodeToNativeObject(JsonNode node)
        {
            if (node == null || node.IsNull) return null;
            switch (node.NodeType)
            {
                case JsonNodeType.Boolean:
                case JsonNodeType.Number:
                case JsonNodeType.String:
                    return node.Value;
                case JsonNodeType.Array:
                    var list = new List<object>();
                    for (int i = 0; i < node.Elements.Count; i++)
                    {
                        list.Add(JsonNodeToNativeObject(node.Elements[i]));
                    }
                    return list;
                case JsonNodeType.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, JsonNode> kvp in node.Properties)
                    {
                        dict[kvp.Key] = JsonNodeToNativeObject(kvp.Value);
                    }
                    return dict;
                default:
                    return null;
            }
        }

        private static string SanitizeFleetIdSegment(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "UNKNOWN";
            string cleaned = Regex.Replace(raw.Trim(), "[^A-Za-z0-9_-]", "").ToUpperInvariant();
            return string.IsNullOrEmpty(cleaned) ? "UNKNOWN" : cleaned;
        }

        public void ReuploadUpdatedReportAfterHwTestAsync(Action<bool, string> onCompleted)
        {
            if (!_isEnabled)
            {
                if (onCompleted != null) { try { onCompleted(false, "Telemetry disabled"); } catch { } }
                return;
            }

            Dictionary<string, object> hwSnapshot = BuildHardwarePayload();
            bool isLaptopSnapshot = (_viewModel != null && _viewModel.IsLaptop);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (string.IsNullOrEmpty(HubUrl))
                    {
                        DiscoverHub();
                        if (string.IsNullOrEmpty(HubUrl))
                        {
                            if (onCompleted != null) { try { onCompleted(false, "ServiceHub nije dostupan"); } catch { } }
                            return;
                        }
                    }

                    Dictionary<string, object> reportPayload = null;
                    string[] kartonCandidates = new string[]
                    {
                        @"C:\setup\servisni_karton.json",
                        @"C:\Windows\Logs\WIS\servisni_karton.json"
                    };

                    string existingKartonPath = null;
                    for (int i = 0; i < kartonCandidates.Length; i++)
                    {
                        if (File.Exists(kartonCandidates[i]))
                        {
                            try
                            {
                                string rawKarton = File.ReadAllText(kartonCandidates[i], Encoding.UTF8);
                                JsonNode parsedKarton = JsonHelper.Parse(rawKarton);
                                if (parsedKarton != null && parsedKarton.IsObject)
                                {
                                    reportPayload = JsonNodeToNativeObject(parsedKarton) as Dictionary<string, object>;
                                    if (reportPayload != null)
                                    {
                                        existingKartonPath = kartonCandidates[i];
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (reportPayload == null)
                    {
                        reportPayload = new Dictionary<string, object>();
                    }

                    // Učitaj svež C:\setup\hwtest-result.json
                    string hwTestJsonPath = @"C:\setup\hwtest-result.json";
                    if (File.Exists(hwTestJsonPath))
                    {
                        string hwJson = File.ReadAllText(hwTestJsonPath, Encoding.UTF8);
                        JsonNode hwNode = JsonHelper.Parse(hwJson);
                        if (hwNode != null && hwNode.IsObject)
                        {
                            reportPayload["hwtest"] = JsonNodeToNativeObject(hwNode);
                        }
                    }

                    string serialSnapshot = hwSnapshot.ContainsKey("serial") && hwSnapshot["serial"] != null
                        ? hwSnapshot["serial"].ToString()
                        : "";
                    if (IsPlaceholderHwValue(serialSnapshot))
                    {
                        serialSnapshot = Environment.MachineName;
                    }

                    string devId = reportPayload.ContainsKey("device_id") && reportPayload["device_id"] != null
                        ? reportPayload["device_id"].ToString()
                        : "";
                    if (string.IsNullOrWhiteSpace(devId))
                    {
                        string chassisTag = isLaptopSnapshot ? "LAPTOP" : "DESKTOP";
                        devId = "TK-" + chassisTag + "-" + SanitizeFleetIdSegment(serialSnapshot);
                        reportPayload["device_id"] = devId;
                    }
                    if (!reportPayload.ContainsKey("computer_name") || reportPayload["computer_name"] == null)
                    {
                        reportPayload["computer_name"] = Environment.MachineName;
                    }
                    if (!reportPayload.ContainsKey("chassis") || reportPayload["chassis"] == null)
                    {
                        reportPayload["chassis"] = isLaptopSnapshot ? "Laptop" : "Desktop";
                    }
                    if (!reportPayload.ContainsKey("serial_number") || IsPlaceholderHwValue(Convert.ToString(reportPayload["serial_number"])))
                    {
                        reportPayload["serial_number"] = serialSnapshot;
                    }
                    if (!reportPayload.ContainsKey("hw") || reportPayload["hw"] == null)
                    {
                        reportPayload["hw"] = hwSnapshot;
                    }

                    reportPayload["status"] = "COMPLETED";
                    reportPayload["handover_ready"] = true;
                    reportPayload["timestamp"] = DateTime.Now.ToString("o");
                    string sessionId = GetOrCreateInstallSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        reportPayload["install_session_id"] = sessionId;
                    }
                    EnrichLicenseTelemetryPayload(reportPayload);

                    string serialized = JsonHelper.Serialize(reportPayload);
                    if (!string.IsNullOrEmpty(existingKartonPath))
                    {
                        try { File.WriteAllText(existingKartonPath, serialized, new UTF8Encoding(false)); } catch { }
                    }

                    string url = HubUrl.TrimEnd('/') + "/api/devices/" + Uri.EscapeDataString(devId) + "/report";
                    Dictionary<string, string> headers = null;
                    string tok = GetClientToken();
                    if (!string.IsNullOrEmpty(tok))
                    {
                        headers = new Dictionary<string, string>();
                        headers["X-ServisHub-Client-Token"] = tok;
                    }

                    bool ok = false;
                    int[] delays = new int[] { 0, 1000, 2000 };
                    for (int a = 0; a < delays.Length; a++)
                    {
                        if (delays[a] > 0) Thread.Sleep(delays[a]);
                        HttpPostWithResponse(url, serialized, 10000, out ok, headers);
                        if (ok) break;
                    }

                    if (ok)
                    {
                        var compInfo = new Dictionary<string, object>
                        {
                            { "phase", 6 },
                            { "phase_name", "Zavrsetak" },
                            { "status", "SUCCESS" },
                            { "hwtest_updated", true }
                        };
                        SendLifecycleEvent("COMPLETED", compInfo);
                    }

                    if (onCompleted != null)
                    {
                        try { onCompleted(ok, ok ? url : "HTTP slanje izveštaja nije uspelo"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    if (onCompleted != null)
                    {
                        try { onCompleted(false, ex.Message); } catch { }
                    }
                }
            });
        }

        private string HttpPostWithResponse(string url, string jsonBody, int timeoutMs, out bool success)
        {
            return HttpPostWithResponse(url, jsonBody, timeoutMs, out success, null);
        }

        private string HttpPostWithResponse(string url, string jsonBody, int timeoutMs, out bool success, Dictionary<string, string> headers)
        {
            success = false;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> kvp in headers)
                    {
                        if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                        {
                            req.Headers.Add(kvp.Key, kvp.Value);
                        }
                    }
                }

                byte[] bytes = Encoding.UTF8.GetBytes(jsonBody ?? "{}");
                req.ContentLength = bytes.Length;

                using (var os = req.GetRequestStream())
                {
                    os.Write(bytes, 0, bytes.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    success = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                    using (var rs = resp.GetResponseStream())
                    {
                        if (rs != null)
                        {
                            using (var sr = new StreamReader(rs, Encoding.UTF8))
                            {
                                return sr.ReadToEnd();
                            }
                        }
                    }
                    return "";
                }
            }
            catch
            {
                return null;
            }
        }

        private bool HttpPost(string url, string jsonBody, int timeoutMs)
        {
            bool ok;
            HttpPostWithResponse(url, jsonBody, timeoutMs, out ok);
            return ok;
        }

        private void NotifyHubStatusChanged(string hubUrl, string hubVersion, bool isConnected)
        {
            if (_viewModel == null) return;
            try
            {
                if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
                {
                    _dispatcher.BeginInvoke((Action)delegate
                    {
                        try { _viewModel.UpdateServiceHubConnection(hubUrl, hubVersion, isConnected); } catch { }
                    });
                }
                else
                {
                    _viewModel.UpdateServiceHubConnection(hubUrl, hubVersion, isConnected);
                }
            }
            catch { }
        }

        private void FetchBenchmarkBaselineAsync(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl) || _viewModel == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string cleanBase = baseUrl.TrimEnd('/');
                    var req = (HttpWebRequest)WebRequest.Create(cleanBase + "/api/benchmark/summary");
                    req.Method = "GET";
                    req.Timeout = 2500;
                    req.ReadWriteTimeout = 2500;
                    string body = null;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        int code = (int)resp.StatusCode;
                        if (code >= 200 && code < 400)
                        {
                            using (var stream = resp.GetResponseStream())
                            {
                                if (stream != null)
                                {
                                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                                    {
                                        body = reader.ReadToEnd();
                                    }
                                }
                            }
                        }
                    }
                    if (string.IsNullOrWhiteSpace(body)) return;

                    JsonNode root = JsonHelper.Parse(body);
                    if (root == null || !root.IsObject) return;

                    var baseline = new BenchmarkBaseline();
                    double v3P50 = root["cohorts"]["v3_modern"]["p50_duration_seconds"].AsDouble(0.0);
                    double fleetP50 = root["p50_duration_seconds"].AsDouble(0.0);
                    double fleetAvg = root["avg_duration_seconds"].AsDouble(0.0);
                    baseline.FleetP50TotalSeconds = v3P50 > 60.0
                        ? v3P50
                        : (fleetP50 > 60.0 ? fleetP50 : ((fleetAvg > 60.0 && fleetAvg <= 7200.0) ? fleetAvg : 0.0));

                    JsonNode phaseNode = root["phase_averages_sec"];
                    if (phaseNode != null && phaseNode.IsObject)
                    {
                        for (int p = 2; p <= 6; p++)
                        {
                            double val = phaseNode["Phase" + p].AsDouble(0.0);
                            if (val >= 15.0 && val <= 2400.0)
                            {
                                baseline.PhaseAveragesSeconds[p] = val;
                            }
                        }
                    }

                    JsonNode byModelNode = root["by_model"];
                    if (byModelNode != null && byModelNode.IsArray)
                    {
                        for (int i = 0; i < byModelNode.Count; i++)
                        {
                            JsonNode item = byModelNode[i];
                            if (item == null || !item.IsObject) continue;
                            string modelName = item["model"].AsString("");
                            double avgSec = item["avg_duration_seconds"].AsDouble(0.0);
                            if (!string.IsNullOrWhiteSpace(modelName) && avgSec >= 120.0 && avgSec <= 5400.0)
                            {
                                baseline.ModelAveragesSeconds[modelName.Trim()] = avgSec;
                            }
                        }
                    }

                    int totalInst = (int)root["total_installations"].AsDouble(0.0);
                    baseline.SourceLabel = totalInst > 0
                        ? string.Format("ServiceHub benchmark ({0} instalacija)", totalInst)
                        : "ServiceHub benchmark";

                    _benchmarkFetched = true;
                    _viewModel.ApplyBenchmarkBaseline(baseline);
                }
                catch { }
            });
        }

        #endregion
    }
}
