using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WIS.HwTest.Core
{
    public static class FactorySpecProbe
    {
        private const string CacheDir = @"C:\setup\hwtest-bom-cache";

        static FactorySpecProbe()
        {
            try
            {
                // .NET 4.8 podrazumevano ume da ostavi samo TLS 1.0/1.1 u starijim procesima
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | SecurityProtocolType.Tls12;
            }
            catch { }
        }

        private class OemPartEntry
        {
            public string Group;
            public string Category;
            public string Description;
            public string PartNumber;
        }

        public static void ResolveAndMerge(HardwareProbeResult hw, ModelProfileClient profileClient)
        {
            if (hw == null) return;

            string vendor = DetectVendor(hw.Manufacturer, hw.ModelName, hw.SystemSkuNumber);
            string cacheKey = BuildCacheKey(vendor, hw.SerialNumber, hw.SystemSkuNumber, hw.ModelName);

            // 1. Lokalni disk keš (ako postoji i validan je)
            if (!string.IsNullOrEmpty(cacheKey) && TryLoadFromCache(cacheKey, hw))
            {
                ApplyOfflineFallbackHeuristics(hw);
                LogSummary(hw);
                return;
            }

            // 2. ServiceHub /api/hardware-spec (sa circuit-breaker zaštitom)
            if (profileClient != null && !string.IsNullOrEmpty(profileClient.HubBaseUrl))
            {
                if (TryQueryServiceHub(hw, profileClient))
                {
                    ApplyOfflineFallbackHeuristics(hw);
                    if (hw.FactoryBomLoaded && !string.IsNullOrEmpty(cacheKey))
                        SaveToCache(cacheKey, hw);
                    LogSummary(hw);
                    return;
                }
            }

            // 3. Direktni OEM Parts / BOM API sa klijenta (HP PartSurfer radi javno; Lenovo redport 405 i Dell asset 403/404 su ugašeni)
            bool oemOk = false;
            try
            {
                if (vendor == "HP")
                {
                    oemOk = QueryHpPartSurferBom(hw);
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("FACTORY_BOM", "Greška u OEM BOM upitu (" + vendor + "): " + ex.Message);
            }

            ApplyOfflineFallbackHeuristics(hw);

            if (oemOk && !string.IsNullOrEmpty(cacheKey))
            {
                SaveToCache(cacheKey, hw);
            }

            LogSummary(hw);
        }

        private static string DetectVendor(string mfg, string model, string sku)
        {
            string combined = string.Format("{0} {1} {2}", mfg ?? "", model ?? "", sku ?? "").ToUpperInvariant();
            if (combined.Contains("HP") || combined.Contains("HEWLETT") || combined.Contains("ELITEBOOK") ||
                combined.Contains("PROBOOK") || combined.Contains("ZBOOK") || combined.Contains("ELITEDESK") ||
                combined.Contains("PRODESK"))
                return "HP";
            if (combined.Contains("LENOVO") || combined.Contains("THINKPAD") || combined.Contains("IDEAPAD") ||
                combined.Contains("THINKCENTRE") || combined.Contains("YOGA") || combined.Contains("LEGION"))
                return "Lenovo";
            if (combined.Contains("DELL") || combined.Contains("LATITUDE") || combined.Contains("OPTIPLEX") ||
                combined.Contains("PRECISION") || combined.Contains("XPS") || combined.Contains("VOSTRO") ||
                combined.Contains("INSPIRON"))
                return "Dell";
            return "";
        }

        private static string SanitizeToken(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            return Regex.Replace(val.Trim().ToUpperInvariant(), @"[^A-Z0-9_-]", "_").Trim('_');
        }

        private static string BuildCacheKey(string vendor, string serial, string sku, string model)
        {
            string cleanSn = SanitizeToken(serial);
            if (cleanSn.StartsWith("NEPOZNAT") || cleanSn.Length < 4 || cleanSn == "TO_BE_FILLED_BY_O_E_M")
                cleanSn = "";
            string cleanSku = SanitizeToken(sku);
            string cleanModel = SanitizeToken(model);
            string v = string.IsNullOrEmpty(vendor) ? "GEN" : vendor.ToUpperInvariant();

            if (!string.IsNullOrEmpty(cleanSn))
                return v + "_SN_" + cleanSn;
            if (!string.IsNullOrEmpty(cleanSku))
                return v + "_SKU_" + cleanSku;
            if (!string.IsNullOrEmpty(cleanModel))
                return v + "_MOD_" + cleanModel;
            return "";
        }

        private static bool TryLoadFromCache(string cacheKey, HardwareProbeResult hw)
        {
            try
            {
                string path = Path.Combine(CacheDir, cacheKey + ".json");
                if (!File.Exists(path)) return false;

                FileInfo fi = new FileInfo(path);
                if ((DateTime.Now - fi.LastWriteTime).TotalDays > 30) return false;

                string json = File.ReadAllText(path, Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var dict = ser.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null) return false;

                bool loaded = ReadBoolProp(dict, "bom_loaded") ?? false;
                if (!loaded) return false;
                if (ReadStrProp(dict, "schema_version") != "3") return false;

                hw.FactoryBomLoaded = true;
                hw.FactorySource = (ReadStrProp(dict, "source") ?? "bom") + " (cache)";
                hw.FactorySummary = ReadStrProp(dict, "summary") ?? "";
                hw.FactoryTouchscreen = ReadNullableBoolProp(dict, "has_touchscreen");
                hw.FactoryTouchscreenEvidence = ReadStrProp(dict, "touchscreen_evidence") ?? "";
                hw.FactoryFingerprint = ReadNullableBoolProp(dict, "has_fingerprint");
                hw.FactoryFingerprintEvidence = ReadStrProp(dict, "fingerprint_evidence") ?? "";
                hw.FactoryWebcam = ReadNullableBoolProp(dict, "has_webcam");
                hw.FactoryWebcamEvidence = ReadStrProp(dict, "webcam_evidence") ?? "";
                hw.FactoryIrCamera = ReadNullableBoolProp(dict, "has_ir_camera");
                hw.FactoryIrCameraEvidence = ReadStrProp(dict, "ir_camera_evidence") ?? "";
                hw.FactoryTouchpad = ReadNullableBoolProp(dict, "has_touchpad");
                hw.FactorySpeakers = ReadNullableBoolProp(dict, "has_speakers");
                hw.FactoryMicrophone = ReadNullableBoolProp(dict, "has_microphone");
                hw.FactoryKeyboardIso = ReadNullableBoolProp(dict, "has_keyboard_iso");
                hw.FactoryKeyboardNumpad = ReadNullableBoolProp(dict, "has_keyboard_numpad");
                hw.FactoryKeyboardBacklight = ReadNullableBoolProp(dict, "has_keyboard_backlight");
                hw.FactoryKeyboardDesc = ReadStrProp(dict, "keyboard_desc") ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveToCache(string cacheKey, HardwareProbeResult hw)
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                    Directory.CreateDirectory(CacheDir);

                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "schema_version", "3" },
                    { "bom_loaded", hw.FactoryBomLoaded },
                    { "source", hw.FactorySource ?? "" },
                    { "summary", hw.FactorySummary ?? "" },
                    { "model", hw.ModelName ?? "" },
                    { "serial", hw.SerialNumber ?? "" },
                    { "sku", hw.SystemSkuNumber ?? "" },
                    { "has_touchscreen", hw.FactoryTouchscreen },
                    { "touchscreen_evidence", hw.FactoryTouchscreenEvidence ?? "" },
                    { "has_fingerprint", hw.FactoryFingerprint },
                    { "fingerprint_evidence", hw.FactoryFingerprintEvidence ?? "" },
                    { "has_webcam", hw.FactoryWebcam },
                    { "webcam_evidence", hw.FactoryWebcamEvidence ?? "" },
                    { "has_ir_camera", hw.FactoryIrCamera },
                    { "ir_camera_evidence", hw.FactoryIrCameraEvidence ?? "" },
                    { "has_touchpad", hw.FactoryTouchpad },
                    { "has_speakers", hw.FactorySpeakers },
                    { "has_microphone", hw.FactoryMicrophone },
                    { "has_keyboard_iso", hw.FactoryKeyboardIso },
                    { "has_keyboard_numpad", hw.FactoryKeyboardNumpad },
                    { "has_keyboard_backlight", hw.FactoryKeyboardBacklight },
                    { "keyboard_desc", hw.FactoryKeyboardDesc ?? "" },
                    { "cached_at", DateTime.Now.ToString("o") }
                };

                string path = Path.Combine(CacheDir, cacheKey + ".json");
                File.WriteAllText(path, SimpleJson.Serialize(payload), Encoding.UTF8);
            }
            catch { }
        }

        private static bool TryQueryServiceHub(HardwareProbeResult hw, ModelProfileClient profileClient)
        {
            if (profileClient == null || string.IsNullOrEmpty(profileClient.HubBaseUrl)) return false;
            if (profileClient.IsEndpointDisabled("/api/hardware-spec")) return false;

            try
            {
                StringBuilder qs = new StringBuilder();
                qs.Append("manufacturer=").Append(Uri.EscapeDataString(hw.Manufacturer ?? ""));
                qs.Append("&model=").Append(Uri.EscapeDataString(hw.ModelName ?? ""));
                qs.Append("&serial=").Append(Uri.EscapeDataString(hw.SerialNumber ?? ""));
                qs.Append("&sku=").Append(Uri.EscapeDataString(hw.SystemSkuNumber ?? ""));
                qs.Append("&is_desktop=").Append(hw.IsLaptop ? "false" : "true");

                string url = profileClient.HubBaseUrl.TrimEnd('/') + "/api/hardware-spec?" + qs.ToString();
                string json = HttpGetString(url, 4500, null, profileClient, "/api/hardware-spec");
                if (string.IsNullOrEmpty(json)) return false;

                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var dict = ser.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null) return false;

                bool bomLoaded = ReadBoolProp(dict, "bom_loaded") ?? false;
                if (!bomLoaded) return false;

                hw.FactoryBomLoaded = true;
                hw.FactorySource = ReadStrProp(dict, "source") ?? "servishub";
                hw.FactorySummary = ReadStrProp(dict, "summary") ?? "";
                hw.FactoryTouchscreen = ReadNullableBoolProp(dict, "has_touchscreen");
                hw.FactoryTouchscreenEvidence = ReadStrProp(dict, "touchscreen_evidence") ?? "";
                hw.FactoryFingerprint = ReadNullableBoolProp(dict, "has_fingerprint");
                hw.FactoryFingerprintEvidence = ReadStrProp(dict, "fingerprint_evidence") ?? "";
                hw.FactoryWebcam = ReadNullableBoolProp(dict, "has_webcam");
                hw.FactoryWebcamEvidence = ReadStrProp(dict, "webcam_evidence") ?? "";
                hw.FactoryIrCamera = ReadNullableBoolProp(dict, "has_ir_camera");
                hw.FactoryIrCameraEvidence = ReadStrProp(dict, "ir_camera_evidence") ?? "";
                hw.FactoryTouchpad = ReadNullableBoolProp(dict, "has_touchpad");
                hw.FactorySpeakers = ReadNullableBoolProp(dict, "has_speakers");
                hw.FactoryMicrophone = ReadNullableBoolProp(dict, "has_microphone");
                hw.FactoryKeyboardBacklight = ReadNullableBoolProp(dict, "has_keyboard_backlight");
                string kbdDescHub = ReadStrProp(dict, "keyboard_desc");
                if (!string.IsNullOrEmpty(kbdDescHub)) hw.FactoryKeyboardDesc = kbdDescHub;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string HttpGetString(string url, int timeoutMs, Dictionary<string, string> headers, ModelProfileClient circuitClient = null, string circuitRoute = null)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WIS-HwTest/1.0";
                req.Accept = "application/json";
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> kv in headers)
                    {
                        if (kv.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                            req.Headers["Origin"] = kv.Value;
                        else if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                            req.Referer = kv.Value;
                        else
                            req.Headers[kv.Key] = kv.Value;
                    }
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200)
                    {
                        HwTestLogger.Warn("FACTORY_BOM", string.Format("HTTP {0} za {1}", (int)resp.StatusCode, url));
                        return null;
                    }
                    using (Stream st = resp.GetResponseStream())
                    using (StreamReader rdr = new StreamReader(st, Encoding.UTF8))
                    {
                        return rdr.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                if (circuitClient != null)
                {
                    circuitClient.RecordHubFailure(circuitRoute, ex);
                }
                HwTestLogger.Warn("FACTORY_BOM", string.Format("HTTP greška za {0}: {1}", url, ex.Message));
                return null;
            }
        }

        // =====================================================================
        // 1. HP PartSurfer BFF BOM Lookup
        // =====================================================================

        private static bool QueryHpPartSurferBom(HardwareProbeResult hw)
        {
            string cleanSn = Regex.Replace((hw.SerialNumber ?? "").Trim().ToUpperInvariant(), @"[^A-Z0-9]", "");
            if (cleanSn.StartsWith("NEPOZNAT") || cleanSn.Length < 6) cleanSn = "";
            string cleanPn = ((hw.SystemSkuNumber ?? "").Trim().ToUpperInvariant().Split('#')[0]);
            cleanPn = Regex.Replace(cleanPn, @"[^A-Z0-9_-]", "");

            if (string.IsNullOrEmpty(cleanSn) && string.IsNullOrEmpty(cleanPn))
                return false;

            List<string> urls = new List<string>();
            if (!string.IsNullOrEmpty(cleanSn))
            {
                urls.Add(string.Format(
                    "https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/SerialNumber/GetSerialNumber/{0}/country/US/usertype/EXT",
                    Uri.EscapeDataString(cleanSn)));
            }
            else if (!string.IsNullOrEmpty(cleanPn))
            {
                urls.Add(string.Format(
                    "https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/Search/GenericSearch/{0}/country/US/usertype/EXT",
                    Uri.EscapeDataString(cleanPn)));
            }

            Dictionary<string, string> hpHeaders = new Dictionary<string, string>
            {
                { "Origin", "https://partsurfer.hp.com" },
                { "Referer", "https://partsurfer.hp.com/" }
            };

            foreach (string url in urls)
            {
                string json = HttpGetString(url, 4500, hpHeaders);
                if (string.IsNullOrEmpty(json)) continue;

                List<OemPartEntry> unitConfigParts = new List<OemPartEntry>();
                List<OemPartEntry> otherBomParts = new List<OemPartEntry>();

                if (ParseHpPartSurferJson(json, unitConfigParts, otherBomParts))
                {
                    EvaluateBomEntries(hw, "hp_partsurfer_bom", unitConfigParts, otherBomParts);
                    return hw.FactoryBomLoaded;
                }
                else
                {
                    HwTestLogger.Info("FACTORY_BOM", string.Format("HP BOM odgovor prazan/neparsiran ({0} chars)", json.Length));
                }
            }
            return false;
        }

        private static bool ParseHpPartSurferJson(string json, List<OemPartEntry> unitConfigParts, List<OemPartEntry> otherBomParts)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var root = ser.DeserializeObject(json) as IDictionary<string, object>;
                if (root == null) return false;

                var body = GetDict(root, "Body");
                if (body == null) return false;

                var sbom = GetDict(body, "SerialNumberBOM");
                if (sbom != null)
                {
                    object ucObj;
                    IEnumerable ucArr = sbom.TryGetValue("unit_configuration", out ucObj) ? AsJsonArray(ucObj) : null;
                    if (ucArr != null)
                    {
                        foreach (object item in ucArr)
                        {
                            var d = item as IDictionary<string, object>;
                            if (d == null) continue;
                            string desc = ReadStrProp(d, "part_description") ?? "";
                            string pn = ReadStrProp(d, "part_number") ?? "";
                            if (string.IsNullOrEmpty(desc) || desc == "-N/A-") continue;
                            unitConfigParts.Add(new OemPartEntry
                            {
                                Group = "unit_configuration",
                                Category = "",
                                PartNumber = pn,
                                Description = desc
                            });
                        }
                    }

                    object spObj;
                    IEnumerable spArr = sbom.TryGetValue("spare_part", out spObj) ? AsJsonArray(spObj) : null;
                    if (spArr != null)
                    {
                        foreach (object item in spArr)
                        {
                            var d = item as IDictionary<string, object>;
                            if (d == null) continue;
                            string desc = (ReadStrProp(d, "spare_part_description") ?? "") + " " + (ReadStrProp(d, "spare_enhance_desc") ?? "");
                            desc = desc.Trim();
                            string pn = ReadStrProp(d, "spare_part_no") ?? "";
                            if (string.IsNullOrEmpty(desc) || desc == "-N/A-") continue;
                            otherBomParts.Add(new OemPartEntry
                            {
                                Group = "spare_part",
                                Category = ReadStrProp(d, "sp_category") ?? "",
                                PartNumber = pn,
                                Description = desc
                            });
                        }
                    }
                }

                object pbObj;
                IEnumerable pbArr = body.TryGetValue("ProductBOM", out pbObj) ? AsJsonArray(pbObj) : null;
                if (pbArr != null)
                {
                    foreach (object item in pbArr)
                    {
                        var d = item as IDictionary<string, object>;
                        if (d == null) continue;
                        string desc = ReadStrProp(d, "EnhancedDescription") ?? "";
                        string pn = ReadStrProp(d, "PartNumber") ?? "";
                        if (string.IsNullOrEmpty(desc) || desc == "-N/A-") continue;
                        otherBomParts.Add(new OemPartEntry
                        {
                            Group = "product_bom",
                            Category = ReadStrProp(d, "CategoryName") ?? "",
                            PartNumber = pn,
                            Description = desc
                        });
                    }
                }

                return (unitConfigParts.Count + otherBomParts.Count) > 0;
            }
            catch
            {
                return false;
            }
        }

        // =====================================================================
        // 2. Lenovo Parts API BOM Lookup
        // =====================================================================

        private static bool QueryLenovoPartsBom(HardwareProbeResult hw)
        {
            string cleanSn = Regex.Replace((hw.SerialNumber ?? "").Trim().ToUpperInvariant(), @"[^A-Z0-9]", "");
            if (cleanSn.StartsWith("NEPOZNAT") || cleanSn.Length < 5) cleanSn = "";
            string cleanMtm = (hw.ProductMtm ?? hw.SystemSkuNumber ?? "").Trim().ToUpperInvariant();
            if (cleanMtm.Length > 4 && Regex.IsMatch(cleanMtm, @"^[A-Z0-9]{10}$"))
                cleanMtm = cleanMtm.Substring(0, 4);
            else if (cleanMtm.Length >= 4 && Regex.IsMatch(cleanMtm.Substring(0, 4), @"^[0-9][A-Z0-9]{3}$"))
                cleanMtm = cleanMtm.Substring(0, 4);
            else
                cleanMtm = "";

            List<string> urls = new List<string>();
            if (!string.IsNullOrEmpty(cleanSn))
            {
                string u = "https://pcsupport.lenovo.com/api/v4/upsell/redport/getParts?serialNumber=" + Uri.EscapeDataString(cleanSn);
                if (!string.IsNullOrEmpty(cleanMtm))
                    u += "&productId=" + Uri.EscapeDataString(cleanMtm);
                urls.Add(u);
            }
            if (!string.IsNullOrEmpty(cleanMtm))
            {
                urls.Add("https://pcsupport.lenovo.com/api/v4/upsell/redport/getProductParts?productId=" + Uri.EscapeDataString(cleanMtm));
            }

            foreach (string url in urls)
            {
                string json = HttpGetString(url, 8000, null);
                if (string.IsNullOrEmpty(json)) continue;

                List<OemPartEntry> parts = ExtractGenericPartsFromJson(json, "lenovo_parts");
                if (parts.Count >= 3)
                {
                    EvaluateBomEntries(hw, "lenovo_parts_api", new List<OemPartEntry>(), parts);
                    return hw.FactoryBomLoaded;
                }
            }
            return false;
        }

        // =====================================================================
        // 3. Dell Asset Parts API BOM Lookup
        // =====================================================================

        private static string ConvertExpressCodeToServiceTag(string rawSerial)
        {
            string clean = Regex.Replace((rawSerial ?? "").Trim().ToUpperInvariant(), @"[^A-Z0-9]", "");
            if (Regex.IsMatch(clean, @"^\d{10,11}$"))
            {
                long num;
                if (long.TryParse(clean, out num) && num > 0)
                {
                    const string alpha = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                    StringBuilder sb = new StringBuilder();
                    while (num > 0)
                    {
                        int rem = (int)(num % 36);
                        sb.Insert(0, alpha[rem]);
                        num /= 36;
                    }
                    return sb.ToString().PadLeft(7, '0');
                }
            }
            return clean;
        }

        private static bool QueryDellAssetPartsBom(HardwareProbeResult hw)
        {
            string tag = ConvertExpressCodeToServiceTag(hw.SerialNumber);
            if (tag.Length != 7 || !Regex.IsMatch(tag, @"^[A-Z0-9]{7}$"))
                return false;

            string[] urls = new string[]
            {
                "https://apigtwb2c.us.dell.com/support/assetinfo/v4/getassetparts?servicetags=" + Uri.EscapeDataString(tag),
                "https://www.dell.com/support/components/dashboard/api/Asset/GetAssetParts?serviceTag=" + Uri.EscapeDataString(tag)
            };

            foreach (string url in urls)
            {
                string json = HttpGetString(url, 8000, null);
                if (string.IsNullOrEmpty(json)) continue;

                List<OemPartEntry> parts = ExtractGenericPartsFromJson(json, "dell_parts");
                if (parts.Count >= 3)
                {
                    EvaluateBomEntries(hw, "dell_asset_parts_api", parts, new List<OemPartEntry>());
                    return hw.FactoryBomLoaded;
                }
            }
            return false;
        }

        private static List<OemPartEntry> ExtractGenericPartsFromJson(string json, string groupName)
        {
            List<OemPartEntry> result = new List<OemPartEntry>();
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                object root = ser.DeserializeObject(json);
                WalkPartsTree(root, groupName, result);
            }
            catch { }
            return result;
        }

        private static void WalkPartsTree(object node, string groupName, List<OemPartEntry> acc)
        {
            if (node == null) return;
            var dict = node as IDictionary<string, object>;
            if (dict != null)
            {
                string cat = ReadStrProp(dict, "category") ?? ReadStrProp(dict, "commodity") ??
                             ReadStrProp(dict, "partCategory") ?? ReadStrProp(dict, "CategoryName") ?? "";
                string desc = ReadStrProp(dict, "partDescription") ?? ReadStrProp(dict, "itemDescription") ??
                              ReadStrProp(dict, "description") ?? ReadStrProp(dict, "partDesc") ??
                              ReadStrProp(dict, "componentName") ?? ReadStrProp(dict, "name") ?? "";
                string pn = ReadStrProp(dict, "partNumber") ?? ReadStrProp(dict, "id") ?? "";

                if (!string.IsNullOrEmpty(desc) && desc != "-N/A-")
                {
                    acc.Add(new OemPartEntry
                    {
                        Group = groupName,
                        Category = cat,
                        PartNumber = pn,
                        Description = desc
                    });
                }

                foreach (object v in dict.Values)
                {
                    if (v is IDictionary<string, object> || AsJsonArray(v) != null)
                        WalkPartsTree(v, groupName, acc);
                }
                return;
            }

            IEnumerable arr = AsJsonArray(node);
            if (arr != null)
            {
                foreach (object item in arr)
                {
                    WalkPartsTree(item, groupName, acc);
                }
            }
        }

        // =====================================================================
        // Evaluacija BOM stavki (Unit Configuration ima prednost nad Spare Parts)
        // =====================================================================

        private static void EvaluateBomEntries(
            HardwareProbeResult hw,
            string source,
            List<OemPartEntry> primaryEntries,
            List<OemPartEntry> secondaryEntries)
        {
            int total = primaryEntries.Count + secondaryEntries.Count;
            if (total == 0) return;

            hw.FactoryBomLoaded = true;
            hw.FactorySource = source;

            // 1. Touchscreen evaluacija
            // Prvo tražimo u primaryEntries (npr. HP unit_configuration ili Dell AssetParts)
            bool? touchFromPrimary = AnalyzeTouchEntries(primaryEntries, true, hw);
            if (touchFromPrimary.HasValue)
            {
                hw.FactoryTouchscreen = touchFromPrimary.Value;
            }
            else if (secondaryEntries.Count > 0)
            {
                bool? touchFromSecondary = AnalyzeTouchEntries(secondaryEntries, false, hw);
                if (touchFromSecondary.HasValue)
                {
                    hw.FactoryTouchscreen = touchFromSecondary.Value;
                }
                else
                {
                    hw.FactoryTouchscreen = false;
                    hw.FactoryTouchscreenEvidence = string.Format("BOM ({0} delova, bez touch panela)", total);
                }
            }
            else
            {
                hw.FactoryTouchscreen = false;
                hw.FactoryTouchscreenEvidence = string.Format("BOM ({0} delova, bez touch panela)", total);
            }

            // 2. Fingerprint evaluacija (kombinujemo primary + secondary)
            List<OemPartEntry> allEntries = new List<OemPartEntry>(total);
            allEntries.AddRange(primaryEntries);
            allEntries.AddRange(secondaryEntries);

            bool hasFpEvidence = false;
            bool explicitNoFp = false;
            string fpEv = "";
            foreach (OemPartEntry e in allEntries)
            {
                string txt = (e.Category + " " + e.Description).ToUpperInvariant();
                if (txt.Contains("SMART CARD") && !txt.Contains("FINGER") && !Regex.IsMatch(txt, @"\bFPR\b"))
                    continue;

                if (txt.Contains("NO FPR") || txt.Contains("W/O FPR") || txt.Contains("WO/FPR") ||
                    txt.Contains("NO FINGERPRINT") || txt.Contains("WITHOUT FPR"))
                {
                    explicitNoFp = true;
                    fpEv = e.Description;
                    continue;
                }

                if (txt.Contains("FINGERPRINT") || txt.Contains("FINGER PRINT") ||
                    txt.Contains("BIOMETRIC") || txt.Contains("TOUCHCHIP") ||
                    txt.Contains("FP READER") || txt.Contains("FP SENSOR") ||
                    Regex.IsMatch(txt, @"\bFPR\b"))
                {
                    hasFpEvidence = true;
                    fpEv = (!string.IsNullOrEmpty(e.PartNumber) ? e.PartNumber + " " : "") + e.Description;
                    break;
                }
            }

            if (hasFpEvidence)
            {
                hw.FactoryFingerprint = true;
                hw.FactoryFingerprintEvidence = fpEv;
            }
            else if (explicitNoFp || total >= 5)
            {
                hw.FactoryFingerprint = false;
                hw.FactoryFingerprintEvidence = explicitNoFp
                    ? fpEv
                    : string.Format("BOM ({0} delova, bez FP čitača)", total);
            }

            // 3. Kamera & IR kamera
            bool hasCam = false;
            bool explicitNoCam = false;
            string camEv = "";
            bool hasIr = false;
            string irEv = "";

            foreach (OemPartEntry e in allEntries)
            {
                string txt = (e.Category + " " + e.Description).ToUpperInvariant();
                if (txt.Contains("SCREW") || txt.Contains("BRACKET")) continue;

                if (txt.Contains("NO CAM") || txt.Contains("NO WEBCAM") || txt.Contains("W/O CAM") || txt.Contains("WITHOUT CAM"))
                {
                    explicitNoCam = true;
                    camEv = e.Description;
                    continue;
                }

                if (txt.Contains("WEBCAM") || txt.Contains("CAMERA") || Regex.IsMatch(txt, @"\b(HD|FHD|5MP|RGB)\s+CAM\b"))
                {
                    hasCam = true;
                    if (string.IsNullOrEmpty(camEv)) camEv = e.Description;
                    if (txt.Contains("IR CAM") || txt.Contains("INFRARED") || txt.Contains("RGB/IR") ||
                        txt.Contains("RGB+IR") || Regex.IsMatch(txt, @"\bIR\b"))
                    {
                        hasIr = true;
                        irEv = e.Description;
                    }
                }
            }

            if (hasCam)
            {
                hw.FactoryWebcam = true;
                hw.FactoryWebcamEvidence = camEv;
                hw.FactoryIrCamera = hasIr;
                hw.FactoryIrCameraEvidence = hasIr ? irEv : "Standardna RGB kamera (bez IR)";
            }
            else if (explicitNoCam || total >= 8)
            {
                hw.FactoryWebcam = false;
                hw.FactoryWebcamEvidence = explicitNoCam ? camEv : string.Format("BOM ({0} delova, bez kamere)", total);
                hw.FactoryIrCamera = false;
            }

            // 4. Touchpad, Zvučnici, Mikrofon
            foreach (OemPartEntry e in allEntries)
            {
                string txt = (e.Category + " " + e.Description).ToUpperInvariant();
                if (txt.Contains("TOUCHPAD") || txt.Contains("CLICKPAD") || txt.Contains("TRACKPAD") || Regex.IsMatch(txt, @"\bTPAD\b"))
                    hw.FactoryTouchpad = true;
                if (txt.Contains("SPEAKER") || Regex.IsMatch(txt, @"\bSPKR\b"))
                    hw.FactorySpeakers = true;
                if (txt.Contains("MICROPHONE") || Regex.IsMatch(txt, @"\bDMIC\b") || Regex.IsMatch(txt, @"\bMIC\b"))
                    hw.FactoryMicrophone = true;
            }
            if (hw.FactoryWebcam == true && !hw.FactoryMicrophone.HasValue && hw.IsLaptop)
            {
                hw.FactoryMicrophone = true;
            }

            // 5. Tastatura (interna tastatura iz BOM opisa: jezik/ISO/ANSI, numpad)
            // BITNO: ISO/ANSI i konkretan jezik čitamo SAMO iz primaryEntries (unit_configuration),
            // jer spare_part lista sadrži sve regionalne varijante za model (US, ENG/ARAB, BEL, GR, FR...)!
            List<OemPartEntry> kbdPrimary = primaryEntries.Count > 0 ? primaryEntries : allEntries;
            bool allowIsoFromList = primaryEntries.Count > 0;
            foreach (OemPartEntry e in kbdPrimary)
            {
                ParseKeyboardFromBomEntry(e, hw, allowIsoFromList);
            }
            if (string.IsNullOrEmpty(hw.FactoryKeyboardDesc) && primaryEntries.Count > 0)
            {
                foreach (OemPartEntry e in allEntries)
                {
                    ParseKeyboardFromBomEntry(e, hw, false);
                }
            }

            hw.FactorySummary = string.Format("{0} ({1} delova)", source, total);
        }

        private static void ParseKeyboardFromBomEntry(OemPartEntry e, HardwareProbeResult hw, bool allowIsoFromEntry)
        {
            if (e == null) return;
            string rawDesc = (e.Description ?? "").Trim();
            if (string.IsNullOrEmpty(rawDesc)) return;

            string catUp = (e.Category ?? "").ToUpperInvariant();
            string pnUp = (e.PartNumber ?? "").ToUpperInvariant();
            string up = (catUp + " " + rawDesc).ToUpperInvariant();

            bool looksLikeKbd =
                up.Contains("KEYBOARD") ||
                up.Contains("TOP COVER WITH KEYBOARD") ||
                Regex.IsMatch(up, @"\bKBD[A-Z0-9]*\b") ||
                Regex.IsMatch(up, @"\bKEYB\b");

            if (!looksLikeKbd) return;

            if (up.Contains("EXTERNAL") || up.Contains("USB ") || up.Contains("WIRELESS") ||
                up.Contains("DONGLE") || up.Contains("MOUSE") || up.Contains("CABLE") ||
                up.Contains("SCREW") || up.Contains("BRACKET") || up.Contains("MYLAR") ||
                up.Contains("CARDREADER") || up.Contains("KEYBOARD FRAME"))
            {
                return;
            }

            if (string.IsNullOrEmpty(hw.FactoryKeyboardDesc) || rawDesc.Length > hw.FactoryKeyboardDesc.Length)
            {
                hw.FactoryKeyboardDesc = rawDesc;
            }

            if (!hw.FactoryKeyboardNumpad.HasValue)
            {
                if (up.Contains("NO KEYPAD") || up.Contains("NKP") || up.Contains("W/O KP") ||
                    up.Contains("WITHOUT KEYPAD") || up.Contains("NO NUMPAD"))
                {
                    hw.FactoryKeyboardNumpad = false;
                }
                else if (up.Contains("WITH KEYPAD") || up.Contains("W/KP") || up.Contains("NUMPAD") ||
                         up.Contains("NUMERIC KEYPAD"))
                {
                    hw.FactoryKeyboardNumpad = true;
                }
            }

            if (allowIsoFromEntry && !hw.FactoryKeyboardIso.HasValue)
            {
                if (up.Contains("FRENCH") || up.Contains("BELGIAN") || up.Contains("GERMAN") ||
                    up.Contains("SWISS") || up.Contains("NORDIC") || up.Contains("SWEDISH") ||
                    up.Contains("FINNISH") || up.Contains("DANISH") || up.Contains("NORWEGIAN") ||
                    up.Contains("UNITED KINGDOM") || up.Contains("BRITISH") ||
                    up.Contains("SPANISH") || up.Contains("ITALIAN") || up.Contains("PORTUGUESE") ||
                    up.Contains("HUNGARIAN") || up.Contains("CZECH") || up.Contains("SLOVAK") ||
                    up.Contains("SLOVENIAN") || up.Contains("CROATIAN") || up.Contains("SERBIAN") ||
                    up.Contains("POLISH") || up.Contains("TURKISH") || up.Contains("DUTCH") ||
                    up.Contains("BULGARIAN") || up.Contains("ROMANIAN") || up.Contains("GREEK") ||
                    up.Contains("ARABIC") || up.Contains("RUSSIAN") || up.Contains("EURO") ||
                    Regex.IsMatch(up, @"\b(ISO|BEL|FR|GR|UK|SP|ITL|DEN|NOR|SWD|SWI|PORT|TURK|HU|CZ|SLV|BH|SRB|HR)\b") ||
                    Regex.IsMatch(pnUp, @"-(A41|031|041|051|061|071|081|091|131|141|151|211|251|291|B31|BG1|FL1|BH4)\b"))
                {
                    hw.FactoryKeyboardIso = true;
                }
                else if (up.Contains("UNITED STATES") || up.Contains("ENGLISH-US") ||
                         Regex.IsMatch(up, @"\b(US|ANSI|INTL)\b") ||
                         up.Contains("CANADIAN ENGLISH") ||
                         Regex.IsMatch(pnUp, @"-(001|B1U)\b"))
                {
                    hw.FactoryKeyboardIso = false;
                }
            }

            if (!hw.FactoryKeyboardBacklight.HasValue)
            {
                if (up.Contains("NON-BACKLIT") || up.Contains("NON BACKLIT") || up.Contains("NONBACKLIT") ||
                    up.Contains("NOT BACKLIT") || up.Contains("WITHOUT BACKLIGHT") || up.Contains("W/O BACKLIGHT") ||
                    up.Contains("NO BACKLIGHT") || up.Contains("W/O BL") || up.Contains("NO BL") ||
                    Regex.IsMatch(up, @"\bNBL\b"))
                {
                    hw.FactoryKeyboardBacklight = false;
                }
                else if (up.Contains("BACKLIT") || up.Contains("BACKLIGHT") || up.Contains("ILLUMINATED") ||
                         up.Contains("BLGT") || Regex.IsMatch(up, @"\b(BL|RGB)\b"))
                {
                    hw.FactoryKeyboardBacklight = true;
                }
            }
        }

        private static bool? AnalyzeTouchEntries(List<OemPartEntry> entries, bool isPrimary, HardwareProbeResult hw)
        {
            if (entries == null || entries.Count == 0) return null;

            bool sawPanel = false;
            string nonTouchPanelEv = "";

            foreach (OemPartEntry e in entries)
            {
                string txt = (e.Category + " " + e.Description).ToUpperInvariant();
                // Isključujemo touchpad iz touchscreen pretrage!
                if (txt.Contains("TOUCHPAD") || txt.Contains("TOUCH PAD") || txt.Contains("CLICKPAD") ||
                    txt.Contains("TOUCHCHIP") || txt.Contains("TOUCHSTYK"))
                    continue;

                bool isDisplayItem =
                    txt.Contains("PNL") || txt.Contains("PANEL") || txt.Contains("LCD") ||
                    txt.Contains("DISPLAY") || txt.Contains("OLED") || txt.Contains("DIGITIZER") ||
                    txt.Contains("TOUCHSCREEN") || txt.Contains("TOUCH SCREEN");

                if (!isDisplayItem) continue;

                // Ignorišemo nosače, kablove i poklopce kad procenjujemo panel, osim ako eksplicitno kaže TOUCHSCREEN
                if ((txt.Contains("BACK COVER") || txt.Contains("BEZEL") || txt.Contains("CABLE") ||
                     txt.Contains("HINGE") || txt.Contains("SUPPORT KIT") || txt.Contains("BASE ENCLOSURE")) &&
                    !txt.Contains("TOUCHSCREEN") && !txt.Contains("TOUCH SCREEN") && !txt.Contains("DIGITIZER"))
                    continue;

                sawPanel = true;
                if (string.IsNullOrEmpty(nonTouchPanelEv))
                    nonTouchPanelEv = (!string.IsNullOrEmpty(e.PartNumber) ? e.PartNumber + " " : "") + e.Description;

                bool explicitNoTouch =
                    txt.Contains("NON-TOUCH") || txt.Contains("NON TOUCH") ||
                    txt.Contains("NO TOUCH") || txt.Contains("W/O TOUCH") ||
                    txt.Contains("WITHOUT TOUCH") || Regex.IsMatch(txt, @"\bNT\b");

                if (explicitNoTouch)
                {
                    hw.FactoryTouchscreenEvidence = "Non-touch panel: " + e.Description;
                    return false;
                }

                bool hasTouch =
                    txt.Contains("TOUCHSCREEN") || txt.Contains("TOUCH SCREEN") ||
                    txt.Contains("DIGITIZER") || txt.Contains("ON-CELL") || txt.Contains("IN-CELL") ||
                    Regex.IsMatch(txt, @"\bTOUCH\b") ||
                    Regex.IsMatch(txt, @"\b(RAW\s+PANEL|PNL|LCD|BV|AG)\s+TS\b") ||
                    Regex.IsMatch(txt, @"\bTS\s+\d{2}(\.\d)?");

                if (hasTouch)
                {
                    hw.FactoryTouchscreenEvidence = (!string.IsNullOrEmpty(e.PartNumber) ? e.PartNumber + " " : "") + e.Description;
                    return true;
                }
            }

            if (isPrimary && sawPanel)
            {
                hw.FactoryTouchscreenEvidence = "UnitConfig panel (non-touch): " + nonTouchPanelEv;
                return false;
            }
            if (!isPrimary && sawPanel)
            {
                hw.FactoryTouchscreenEvidence = "Panel bez Touch oznake: " + nonTouchPanelEv;
                return false;
            }

            return null;
        }

        private static void ApplyOfflineFallbackHeuristics(HardwareProbeResult hw)
        {
            string upModel = (hw.ModelName ?? "").ToUpperInvariant();

            // Ako BOM nije dao vrednost za touchscreen, proveri konvertibilne/touch modele po nazivu
            if (!hw.FactoryTouchscreen.HasValue)
            {
                if (upModel.Contains("X360") || upModel.Contains("2-IN-1") || upModel.Contains("2 IN 1") ||
                    upModel.Contains("CONVERTIBLE") || upModel.Contains("DETACHABLE") ||
                    upModel.Contains("TABLET") || upModel.Contains("SURFACE") ||
                    upModel.Contains("TOUCH") || upModel.Contains("FOLIO"))
                {
                    hw.FactoryTouchscreen = true;
                    hw.FactoryTouchscreenEvidence = "Model ukazuje na Touchscreen/2-in-1 (" + hw.ModelName + ")";
                }
                else if (!hw.IsLaptop)
                {
                    hw.FactoryTouchscreen = false;
                    hw.FactoryTouchscreenEvidence = "Desktop šasija";
                }
            }

            if (!hw.IsLaptop)
            {
                if (!hw.FactoryTouchpad.HasValue) hw.FactoryTouchpad = false;
                if (!hw.FactoryFingerprint.HasValue) hw.FactoryFingerprint = false;
                if (!hw.FactoryWebcam.HasValue) hw.FactoryWebcam = false;
                if (!hw.FactoryMicrophone.HasValue) hw.FactoryMicrophone = false;
                if (!hw.FactoryKeyboardBacklight.HasValue) hw.FactoryKeyboardBacklight = false;
            }
            else
            {
                if (!hw.FactoryTouchpad.HasValue) hw.FactoryTouchpad = true;
            }

            if (!hw.HasKeyboardBacklight.HasValue && hw.FactoryKeyboardBacklight.HasValue)
            {
                hw.HasKeyboardBacklight = hw.FactoryKeyboardBacklight;
                if (string.IsNullOrEmpty(hw.KeyboardBacklightEvidence))
                {
                    hw.KeyboardBacklightEvidence = !string.IsNullOrEmpty(hw.FactoryKeyboardDesc)
                        ? "OEM BOM: " + hw.FactoryKeyboardDesc
                        : "OEM/Heuristika";
                }
            }

            if (!hw.FactoryKeyboardIso.HasValue)
            {
                hw.FactoryKeyboardIso = ParseHpSkuKeyboardIso(hw.SystemSkuNumber);
            }

            if (string.IsNullOrEmpty(hw.FactorySummary))
            {
                hw.FactorySummary = hw.IsLaptop ? "Lokalna heuristika + WMI/BIOS" : "Desktop šasija + WMI";
            }

            HardwareProbe.RecomputeRecommendedLayout(hw);
        }

        private static bool? ParseHpSkuKeyboardIso(string sku)
        {
            if (string.IsNullOrEmpty(sku)) return null;
            int hashIdx = sku.IndexOf('#');
            if (hashIdx < 0 || hashIdx + 1 >= sku.Length) return null;
            string suffix = sku.Substring(hashIdx + 1).Trim().ToUpperInvariant();
            if (suffix.Length > 3) suffix = suffix.Substring(0, 3);

            string[] ansiSuffixes = new string[] { "ABA", "ABL", "ABC", "ABM", "AB4" };
            for (int i = 0; i < ansiSuffixes.Length; i++)
            {
                if (suffix == ansiSuffixes[i]) return false;
            }

            string[] isoSuffixes = new string[] {
                "UUG", "UUZ", "UUW", "ABD", "ABU", "ABF", "ABE", "ABZ", "ABH", "ABB",
                "BED", "AKN", "AKC", "AKB", "AKD", "AKQ", "AKS", "B1N", "B1R", "B10",
                "BCM", "BH4", "B31", "AB8", "AB9"
            };
            for (int i = 0; i < isoSuffixes.Length; i++)
            {
                if (suffix == isoSuffixes[i]) return true;
            }

            return null;
        }

        private static void LogSummary(HardwareProbeResult hw)
        {
            HwTestLogger.Info("FACTORY_BOM", string.Format(
                "Loaded={0} | Src='{1}' | Summary='{2}' | Touch={3} ({4}) | FP={5} ({6}) | Cam={7} ({8}) | IR={9} | Pad={10} | Mic={11} | Spk={12} | KbdIso={13} | KbdNum={14} | KbdBL={15} | NavCol={16} | Kbd='{17}'",
                hw.FactoryBomLoaded,
                hw.FactorySource,
                hw.FactorySummary,
                NullableBoolText(hw.FactoryTouchscreen), hw.FactoryTouchscreenEvidence,
                NullableBoolText(hw.FactoryFingerprint), hw.FactoryFingerprintEvidence,
                NullableBoolText(hw.FactoryWebcam), hw.FactoryWebcamEvidence,
                NullableBoolText(hw.FactoryIrCamera),
                NullableBoolText(hw.FactoryTouchpad),
                NullableBoolText(hw.FactoryMicrophone),
                NullableBoolText(hw.FactorySpeakers),
                NullableBoolText(hw.FactoryKeyboardIso),
                NullableBoolText(hw.FactoryKeyboardNumpad),
                NullableBoolText(hw.HasKeyboardBacklight),
                hw.HasNavColumn,
                hw.FactoryKeyboardDesc ?? ""));
        }

        private static string NullableBoolText(bool? v)
        {
            return v.HasValue ? (v.Value ? "True" : "False") : "null";
        }

        private static IEnumerable AsJsonArray(object obj)
        {
            if (obj == null || obj is string || obj is IDictionary<string, object>) return null;
            return obj as IEnumerable;
        }

        private static IDictionary<string, object> GetDict(IDictionary<string, object> parent, string key)
        {
            if (parent == null) return null;
            object val;
            if (parent.TryGetValue(key, out val))
                return val as IDictionary<string, object>;
            return null;
        }

        private static string ReadStrProp(IDictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
                return Convert.ToString(val).Trim();
            return null;
        }

        private static bool? ReadBoolProp(IDictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object val;
            if (!dict.TryGetValue(key, out val) || val == null) return null;
            if (val is bool) return (bool)val;
            string s = Convert.ToString(val).Trim().ToLowerInvariant();
            if (s == "true" || s == "1") return true;
            if (s == "false" || s == "0") return false;
            return null;
        }

        private static bool? ReadNullableBoolProp(IDictionary<string, object> dict, string key)
        {
            return ReadBoolProp(dict, key);
        }
    }
}
