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
        private bool _isEnabled = true;
        private bool _isDisposed = false;
        private int _consecutiveFailures = 0;
        private bool _benchmarkFetched = false;

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

                    string jsonBody = JsonHelper.Serialize(payload);
                    string url = HubUrl + "/api/lifecycle";
                    HttpPost(url, jsonBody, 1500);
                }
                catch { }
            });
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
                string respBody = HttpPostWithResponse(url, jsonBody, 1000, out ok);
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

        private static bool IsPlaceholderHwValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return true;
            string low = val.Trim().ToLowerInvariant();
            if (low == "n/a" || low == "na" || low == "unknown" || low == "none" || low == "null" ||
                low == "nepoznat model" || low == "nepoznat serijski" || low == "nepoznato" ||
                low == "genericpc" || low == "system product name" || low == "default string" ||
                low == "to be filled by o.e.m.")
            {
                return true;
            }
            if (low.StartsWith("učitavanje") || low.StartsWith("ucitavanje") || low.StartsWith("detekcija"))
            {
                return true;
            }
            return false;
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

            string serial = _viewModel.SerialNumber ?? "";
            if (IsPlaceholderHwValue(serial) && _viewModel.Autopilot != null && !IsPlaceholderHwValue(_viewModel.Autopilot.HwSerialNumber))
            {
                serial = _viewModel.Autopilot.HwSerialNumber.Trim();
            }
            if (IsPlaceholderHwValue(serial) && _viewModel.HardwareInfo != null && !IsPlaceholderHwValue(_viewModel.HardwareInfo.SerialNumber))
            {
                serial = _viewModel.HardwareInfo.SerialNumber.Trim();
            }

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
            return hw;
        }

        private Dictionary<string, object> CollectHeartbeatData()
        {
            var data = new Dictionary<string, object>();
            data["computer_name"] = Environment.MachineName;
            data["timestamp"] = DateTime.Now.ToString("o");

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

                // Apps
                var appsDict = new Dictionary<string, string>();
                if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        _dispatcher.Invoke((Action)(() =>
                        {
                            foreach (var app in _viewModel.Apps)
                            {
                                if (!string.IsNullOrEmpty(app.Id))
                                {
                                    appsDict[app.Id] = app.Status;
                                }
                            }
                        }));
                    }
                    catch { }
                }
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

        private string HttpPostWithResponse(string url, string jsonBody, int timeoutMs, out bool success)
        {
            success = false;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;

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
