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
        private int _hubPort = 8080;
        private bool _isEnabled = true;
        private bool _isDisposed = false;
        private int _consecutiveFailures = 0;

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
                        if (ProbeHub(testUrl))
                        {
                            _hubPort = p;
                            HubUrl = testUrl;
                            _consecutiveFailures = 0;
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

        private bool ProbeHub(string baseUrl)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/status");
                req.Method = "GET";
                req.Timeout = 400;
                req.ReadWriteTimeout = 400;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400;
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

                bool ok = HttpPost(url, jsonBody, 1000);
                if (!ok)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 2)
                    {
                        HubUrl = null;
                        _consecutiveFailures = 0;
                    }
                }
                else
                {
                    _consecutiveFailures = 0;
                }
            }
            catch
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 2)
                {
                    HubUrl = null;
                    _consecutiveFailures = 0;
                }
            }
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
                var hw = new Dictionary<string, object>
                {
                    { "model", _viewModel.ComputerModel ?? "" },
                    { "serial", _viewModel.SerialNumber ?? "" },
                    { "cpu", _viewModel.CpuInfo ?? "" },
                    { "ram", _viewModel.RamInfo ?? "" },
                    { "disk", _viewModel.DiskInfo ?? "" },
                    { "battery", _viewModel.BatteryStatus ?? "" }
                };
                data["hw"] = hw;

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

        private bool HttpPost(string url, string jsonBody, int timeoutMs)
        {
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
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
