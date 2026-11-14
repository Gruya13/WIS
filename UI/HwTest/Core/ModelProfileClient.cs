using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace WIS.HwTest.Core
{
    public class ModelProfileData
    {
        public string Model { get; set; }
        public List<string> RemovedKeys { get; set; }
        public List<string> AddedKeys { get; set; }
        public string BaseLayout { get; set; }
        public bool? IsIso { get; set; }
        public bool? HasNumpad { get; set; }
        public bool HasCopilot { get; set; }
        public string Source { get; set; }
        public string Confidence { get; set; }
        public string OemPartDescription { get; set; }
        public int? UsbPortCount { get; set; }

        public ModelProfileData()
        {
            RemovedKeys = new List<string>();
            AddedKeys = new List<string>();
            Source = "";
            Confidence = "";
            OemPartDescription = "";
        }
    }

    public class ModelProfileClient
    {
        private readonly string _hubBaseUrl;
        private readonly string _pendingQueuePath;
        private bool _hubUnreachable = false;
        private readonly HashSet<string> _missingGetEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ModelProfileClient(string hubBaseUrl = null)
        {
            _hubBaseUrl = string.IsNullOrEmpty(hubBaseUrl) ? DetectHubUrl() : hubBaseUrl.TrimEnd('/');
            _pendingQueuePath = @"C:\setup\hwtest-pending.json";
        }

        public string HubBaseUrl { get { return _hubBaseUrl; } }
        public bool IsHubUnreachable { get { return _hubUnreachable; } }

        public bool IsEndpointDisabled(string route)
        {
            if (_hubUnreachable) return true;
            return !string.IsNullOrEmpty(route) && _missingGetEndpoints.Contains(route);
        }

        public void RecordHubFailure(string route, Exception ex)
        {
            bool isTimeout = false;
            WebException wex = ex as WebException;
            if (wex != null)
            {
                HttpWebResponse httpResp = wex.Response as HttpWebResponse;
                if (httpResp != null && (int)httpResp.StatusCode == 404)
                {
                    if (!string.IsNullOrEmpty(route))
                        _missingGetEndpoints.Add(route);
                    return;
                }
                if (wex.Status == WebExceptionStatus.Timeout)
                {
                    isTimeout = true;
                }
                else if (wex.Status == WebExceptionStatus.ConnectFailure ||
                         wex.Status == WebExceptionStatus.NameResolutionFailure ||
                         wex.Status == WebExceptionStatus.KeepAliveFailure)
                {
                    _hubUnreachable = true;
                    return;
                }
            }

            if (!isTimeout && ex != null &&
                (ex.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ex.Message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                isTimeout = true;
            }

            if (isTimeout)
            {
                // Timeout na sporim OEM rutama ne sme blokirati brze lokalne endpointe ServiceHub-a
                if (!string.IsNullOrEmpty(route) &&
                    (string.Equals(route, "/api/hardware-spec", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(route, "/api/keyboard-layout", StringComparison.OrdinalIgnoreCase)))
                {
                    _missingGetEndpoints.Add(route);
                    return;
                }
                _hubUnreachable = true;
                return;
            }

            if (ex != null && ex.Message.IndexOf("Unable to connect", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _hubUnreachable = true;
            }
        }

        private string DetectHubUrl()
        {
            try
            {
                string[] candidates = new string[] {
                    @"C:\setup\Wifi-Servis.ini",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wifi-Servis.ini"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Wifi-Servis.ini")
                };
                foreach (string path in candidates)
                {
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path))
                        {
                            string t = line.Trim();
                            if (t.StartsWith("#") || t.StartsWith(";") || t.StartsWith("[")) continue;
                            if (t.StartsWith("HubUrl=", StringComparison.OrdinalIgnoreCase) ||
                                t.StartsWith("ServiceHub=", StringComparison.OrdinalIgnoreCase) ||
                                t.StartsWith("Url=", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] parts = t.Split(new char[] { '=' }, 2);
                                if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1].Trim()) &&
                                    parts[1].Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    return parts[1].Trim().TrimEnd('/');
                            }
                        }
                    }
                }
            }
            catch { }
            return "http://192.168.170.254:8080";
        }

        public ModelProfileData GetProfile(string modelName)
        {
            ModelProfileData result = new ModelProfileData { Model = modelName };
            if (string.IsNullOrEmpty(modelName)) return result;
            if (IsEndpointDisabled("/api/model-profile")) return result;

            try
            {
                string url = string.Format("{0}/api/model-profile?model={1}", _hubBaseUrl, Uri.EscapeDataString(modelName));
                HwTestLogger.Info("HUB", "GET " + url);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 1500;
                req.Method = "GET";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rdr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = rdr.ReadToEnd();
                    HwTestLogger.Info("HUB", "GET /api/model-profile OK: " + json);
                    ParseProfileJson(json, result);
                }
            }
            catch (Exception ex)
            {
                RecordHubFailure("/api/model-profile", ex);
                HwTestLogger.Warn("HUB", "GET /api/model-profile fallback: " + ex.Message);
            }
            return result;
        }

        public ModelProfileData ResolveKeyboardLayout(
            string manufacturer,
            string model,
            string serial,
            string sku,
            double screenInches,
            bool isDesktop,
            bool? osOem102)
        {
            ModelProfileData result = new ModelProfileData { Model = model };
            if (IsEndpointDisabled("/api/keyboard-layout")) return result;

            try
            {
                StringBuilder qs = new StringBuilder();
                qs.Append("manufacturer=").Append(Uri.EscapeDataString(manufacturer ?? ""));
                qs.Append("&model=").Append(Uri.EscapeDataString(model ?? ""));
                qs.Append("&serial=").Append(Uri.EscapeDataString(serial ?? ""));
                qs.Append("&sku=").Append(Uri.EscapeDataString(sku ?? ""));
                qs.Append("&screen_inches=").Append(screenInches.ToString("0.0", CultureInfo.InvariantCulture));
                qs.Append("&is_desktop=").Append(isDesktop ? "true" : "false");
                if (osOem102.HasValue)
                    qs.Append("&os_oem102=").Append(osOem102.Value ? "true" : "false");

                string url = string.Format("{0}/api/keyboard-layout?{1}", _hubBaseUrl, qs.ToString());
                HwTestLogger.Info("HUB", "GET " + url);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 2000;
                req.Method = "GET";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rdr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = rdr.ReadToEnd();
                    HwTestLogger.Info("HUB", "GET /api/keyboard-layout OK: " + json);
                    ParseProfileJson(json, result);
                }
            }
            catch (Exception ex)
            {
                RecordHubFailure("/api/keyboard-layout", ex);
                HwTestLogger.Warn("HUB", "GET /api/keyboard-layout fallback: " + ex.Message);
            }
            return result;
        }

        public bool PostKeyboardOverride(
            string modelName,
            List<string> removedKeys,
            List<string> addedKeys,
            string baseLayout,
            string by = "serviser",
            bool? isIso = null,
            bool? hasNumpad = null,
            string source = null)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", modelName },
                { "removed_keys", removedKeys ?? new List<string>() },
                { "added_keys", addedKeys ?? new List<string>() },
                { "base_layout", baseLayout ?? "" },
                { "confirmed", true },
                { "by", by }
            };
            if (isIso.HasValue) payload["is_iso"] = isIso.Value;
            if (hasNumpad.HasValue) payload["has_numpad"] = hasNumpad.Value;
            if (!string.IsNullOrEmpty(source)) payload["source"] = source;

            return SendPostOrQueue("/api/model-profile/keyboard", payload);
        }

        public bool PostUsbOverride(string modelName, int portCount, string by = "serviser")
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", modelName },
                { "port_count", portCount },
                { "by", by }
            };
            return SendPostOrQueue("/api/model-profile/usb", payload);
        }

        private bool SendPostOrQueue(string endpoint, Dictionary<string, object> payload)
        {
            string json = SimpleJson.Serialize(payload);
            try
            {
                string url = _hubBaseUrl + endpoint;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 3000;
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                req.ContentLength = bytes.Length;
                using (Stream st = req.GetRequestStream())
                {
                    st.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                EnqueueOffline(endpoint, payload);
                return false;
            }
        }

        private void EnqueueOffline(string endpoint, Dictionary<string, object> payload)
        {
            try
            {
                string dir = Path.GetDirectoryName(_pendingQueuePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Dictionary<string, object> entry = new Dictionary<string, object>
                {
                    { "endpoint", endpoint },
                    { "payload", payload },
                    { "queued_at", DateTime.Now.ToString("o") }
                };
                File.AppendAllText(_pendingQueuePath, SimpleJson.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static void ParseStringArrayField(string json, string fieldName, List<string> targetList)
        {
            string needle = "\"" + fieldName + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return;

            int startArr = json.IndexOf('[', colon);
            if (startArr < 0) return;

            int endArr = json.IndexOf(']', startArr);
            if (endArr <= startArr) return;

            string arrStr = json.Substring(startArr + 1, endArr - startArr - 1);
            string[] tokens = arrStr.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string tok in tokens)
            {
                string clean = tok.Trim().Trim('"').Trim();
                if (!string.IsNullOrEmpty(clean) && !targetList.Contains(clean))
                    targetList.Add(clean);
            }
        }

        private static string ParseStringField(string json, string fieldName)
        {
            string needle = "\"" + fieldName + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;
            if (json[i] == 'n') return null; // null
            if (json[i] != '"') return null;

            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length)
            {
                char ch = json[i];
                if (ch == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    if (next == '"' || next == '\\' || next == '/') { sb.Append(next); i += 2; continue; }
                }
                if (ch == '"') break;
                sb.Append(ch);
                i++;
            }
            return sb.ToString();
        }

        private static bool? ParseBoolField(string json, string fieldName)
        {
            string needle = "\"" + fieldName + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;

            int end = json.IndexOfAny(new char[] { ',', '}', '\r', '\n' }, colon + 1);
            if (end <= colon) end = json.Length;

            string raw = json.Substring(colon + 1, end - colon - 1).Trim().ToLowerInvariant();
            if (raw == "true") return true;
            if (raw == "false") return false;
            return null;
        }

        private static int? ParseIntField(string json, string fieldName)
        {
            string needle = "\"" + fieldName + "\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;

            int end = json.IndexOfAny(new char[] { ',', '}', '\r', '\n' }, colon + 1);
            if (end <= colon) end = json.Length;

            string numStr = json.Substring(colon + 1, end - colon - 1).Trim();
            int p;
            if (int.TryParse(numStr, out p)) return p;
            return null;
        }

        private void ParseProfileJson(string json, ModelProfileData target)
        {
            if (string.IsNullOrEmpty(json) || target == null) return;
            try
            {
                ParseStringArrayField(json, "removed_keys", target.RemovedKeys);
                ParseStringArrayField(json, "added_keys", target.AddedKeys);

                string baseLayout = ParseStringField(json, "base_layout");
                if (!string.IsNullOrEmpty(baseLayout)) target.BaseLayout = baseLayout;

                string src = ParseStringField(json, "source");
                if (!string.IsNullOrEmpty(src)) target.Source = src;

                string conf = ParseStringField(json, "confidence");
                if (!string.IsNullOrEmpty(conf)) target.Confidence = conf;

                string oemDesc = ParseStringField(json, "oem_part_description");
                if (!string.IsNullOrEmpty(oemDesc)) target.OemPartDescription = oemDesc;

                bool? isIso = ParseBoolField(json, "is_iso");
                if (isIso.HasValue) target.IsIso = isIso.Value;

                bool? hasNumpad = ParseBoolField(json, "has_numpad");
                if (hasNumpad.HasValue) target.HasNumpad = hasNumpad.Value;

                bool? hasCopilot = ParseBoolField(json, "has_copilot");
                if (hasCopilot.HasValue) target.HasCopilot = hasCopilot.Value;

                int? portCount = ParseIntField(json, "port_count");
                if (portCount.HasValue) target.UsbPortCount = portCount.Value;
            }
            catch { }
        }
    }
}
