using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WIS.HwTest.Core
{
    public static class HwTestLogger
    {
        private static readonly object _fileLock = new object();
        private static readonly object _queueLock = new object();
        private static readonly List<string> _hubQueue = new List<string>();

        private static string _nasLogPath = null;
        private static string _uncLogPath = null;
        private static string _localLogPath = null;
        private static string _hubBaseUrl = null;
        private static string _computerName = "UNKNOWN-PC";
        private static bool _initialized = false;
        private static Thread _workerThread = null;
        private static volatile bool _stopping = false;
        private static readonly AutoResetEvent _flushSignal = new AutoResetEvent(false);
        private static bool _indexAnnounced = false;

        public static string NasLogPath { get { return _nasLogPath ?? _uncLogPath; } }
        public static string LocalLogPath { get { return _localLogPath; } }

        public static void Initialize(string hubBaseUrl)
        {
            if (_initialized) return;
            _initialized = true;
            _hubBaseUrl = string.IsNullOrEmpty(hubBaseUrl) ? "http://192.168.170.254:8080" : hubBaseUrl.TrimEnd('/');

            try
            {
                string rawName = Environment.MachineName ?? "UNKNOWN-PC";
                StringBuilder clean = new StringBuilder();
                foreach (char ch in rawName)
                {
                    if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                        clean.Append(ch);
                    else
                        clean.Append('_');
                }
                _computerName = clean.Length > 0 ? clean.ToString() : "UNKNOWN-PC";
            }
            catch { }

            string logFileName = string.Format("hwtest_{0}.log", _computerName);

            // 1. Pokušaj pored exe-a (npr. lokalno ili ako je pokrenut sa \\192.168.170.254\Servis\)
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string nasDir = Path.Combine(baseDir, "hwtest-logs");
                    if (!Directory.Exists(nasDir))
                        Directory.CreateDirectory(nasDir);

                    string candidate = Path.Combine(nasDir, logFileName);
                    File.AppendAllText(candidate, "", Encoding.UTF8);
                    _nasLogPath = candidate;
                }
            }
            catch
            {
                _nasLogPath = null;
            }

            // 2. Lokalni fajl (C:\setup\hwtest-debug.log ili %TEMP%\wis-hwtest-debug.log)
            try
            {
                string setupDir = @"C:\setup";
                if (!Directory.Exists(setupDir)) Directory.CreateDirectory(setupDir);
                string localCand = Path.Combine(setupDir, "hwtest-debug.log");
                File.AppendAllText(localCand, "", Encoding.UTF8);
                _localLogPath = localCand;
            }
            catch
            {
                try
                {
                    _localLogPath = Path.Combine(Path.GetTempPath(), "wis-hwtest-debug.log");
                }
                catch { }
            }

            // 3. Pokreni pozadinski worker koji probava UNC NAS share + HTTP /api/log
            _workerThread = new Thread(HubSenderLoop) { IsBackground = true };
            _workerThread.Start();

            Info("INIT", string.Format("WIS.HwTest pokrenut na '{0}' | BaseDir={1} | HubUrl={2}",
                _computerName, AppDomain.CurrentDomain.BaseDirectory, _hubBaseUrl ?? "N/A"));
        }

        public static void Info(string category, string message)
        {
            Write("INFO", category, message);
        }

        public static void Warn(string category, string message)
        {
            Write("WARN", category, message);
        }

        public static void Error(string category, string message)
        {
            Write("ERROR", category, message);
        }

        public static void Write(string level, string category, string message)
        {
            if (!_initialized) Initialize(null);

            string line = string.Format("[{0}] [{1}] [{2}] {3}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                level ?? "INFO",
                category ?? "HwTest",
                message ?? "");

            lock (_fileLock)
            {
                if (!string.IsNullOrEmpty(_nasLogPath))
                {
                    try { File.AppendAllText(_nasLogPath, line + Environment.NewLine, Encoding.UTF8); }
                    catch { }
                }
                if (!string.IsNullOrEmpty(_localLogPath))
                {
                    try { File.AppendAllText(_localLogPath, line + Environment.NewLine, Encoding.UTF8); }
                    catch { }
                }
            }

            if (!string.IsNullOrEmpty(_hubBaseUrl) || string.IsNullOrEmpty(_uncLogPath))
            {
                lock (_queueLock)
                {
                    if (_hubQueue.Count < 800)
                        _hubQueue.Add(line);
                }
                if (level == "ERROR" || level == "WARN" || category == "TEST_START" || category == "TEST_END" || category == "INIT" || category == "REPORT_JSON")
                {
                    try { _flushSignal.Set(); } catch { }
                }
            }
        }

        public static void FlushAndStop()
        {
            _stopping = true;
            try { _flushSignal.Set(); } catch { }
            try { FlushHubBatch(); }
            catch { }
        }

        private static void EnsureUncPath()
        {
            if (!string.IsNullOrEmpty(_uncLogPath)) return;
            try
            {
                string uncDir = @"\\192.168.170.254\Servis\hwtest-logs";
                if (!Directory.Exists(uncDir))
                    Directory.CreateDirectory(uncDir);

                string candidate = Path.Combine(uncDir, string.Format("hwtest_{0}.log", _computerName));
                File.AppendAllText(candidate, "", Encoding.UTF8);
                _uncLogPath = candidate;
            }
            catch
            {
                _uncLogPath = null;
            }
        }

        private static void HubSenderLoop()
        {
            EnsureUncPath();
            while (!_stopping)
            {
                try { _flushSignal.WaitOne(600); } catch { Thread.Sleep(600); }
                try { FlushHubBatch(); }
                catch { }
            }
        }

        private static void PostLogBatch(string targetComputerName, string[] lines)
        {
            if (string.IsNullOrEmpty(_hubBaseUrl) || lines == null || lines.Length == 0) return;

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "computer_name", targetComputerName },
                { "lines", lines }
            };
            string json = SimpleJson.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_hubBaseUrl + "/api/log");
            req.Method = "POST";
            req.Timeout = 2000;
            req.ContentType = "application/json; charset=utf-8";
            req.ContentLength = bytes.Length;

            using (Stream st = req.GetRequestStream())
            {
                st.Write(bytes, 0, bytes.Length);
            }
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                // Ok
            }
        }

        private static void FlushHubBatch()
        {
            List<string> batch = null;
            lock (_queueLock)
            {
                if (_hubQueue.Count == 0) return;
                int take = Math.Min(40, _hubQueue.Count);
                batch = _hubQueue.GetRange(0, take);
                _hubQueue.RemoveRange(0, take);
            }

            if (string.IsNullOrEmpty(_uncLogPath)) EnsureUncPath();

            // 1. Pokušaj direktan upis na NAS SMB share ako exe nije već na NAS-u
            if (!string.IsNullOrEmpty(_uncLogPath) && !string.Equals(_uncLogPath, _nasLogPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.AppendAllText(_uncLogPath, string.Join(Environment.NewLine, batch.ToArray()) + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }

            // 2. Pošalji na ServisHub HTTP /api/log (i pod imenom mašine i u zajednički HWTEST_LIVE stream)
            if (!string.IsNullOrEmpty(_hubBaseUrl))
            {
                try
                {
                    string[] linesArr = batch.ToArray();
                    if (!_indexAnnounced)
                    {
                        _indexAnnounced = true;
                        PostLogBatch("HWTEST_INDEX", new string[] {
                            string.Format("[{0}] ACTIVE_HOST={1} | USER={2} | OS={3}",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                _computerName, Environment.UserName ?? "", Environment.OSVersion.ToString())
                        });
                    }
                    PostLogBatch(_computerName, linesArr);

                    List<string> liveTagged = new List<string>(linesArr.Length);
                    foreach (string l in linesArr)
                    {
                        liveTagged.Add("[" + _computerName + "] " + l);
                    }
                    PostLogBatch("HWTEST_LIVE", liveTagged.ToArray());
                }
                catch
                {
                    // Ignoriši mrežni prekid da nikada ne blokira test
                }
            }
        }
    }
}
