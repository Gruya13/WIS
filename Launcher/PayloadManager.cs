using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Globalization;

namespace WIS.Launcher
{
    public class PayloadVersionInfo
    {
        public string Product { get; set; }
        public int SchemaVersion { get; set; }
        public string Version { get; set; }
        public string ReleaseId { get; set; }
        public string MinLauncherVersion { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string BuiltAt { get; set; }
        public Dictionary<string, string> RequiredFiles { get; set; }
        public string SignedPayload { get; set; }
        public string Signature { get; set; }
        public bool IsSignatureValid { get; set; }
        public string RawJson { get; set; }
    }

    public static class PayloadManager
    {
        public const string LauncherVersion = "WIS Launcher v3.0.0";
        public const string CurrentLauncherSemVer = "3.0.0";
        public const string UpdateMutexName = @"Global\WIS_Launcher_Update_Mutex";

        // Ugrađeni RSA-2048 javni ključ za verifikaciju potpisa manifesta (Root of Trust)
#if LAUNCHER_TESTS
        public static string TrustedPublicKeyXml;
#else
        public const string TrustedPublicKeyXml = "<RSAKeyValue><Modulus>19koZSk56CQwEH1WnrjnxJF7zso3zGFnRvoAXG9ztlPqlQS0GOJmek3jMo2hs2exapyntvYk3IgeuUtnl+EDJFt7ENkY/bM7k6jFLhoXeDZZRomBD+/N9+IBmXkaqRHbeYBAlHvSA+n6ZsUsW9iVFTb4owboOcADS9HGBrVmY0fbS303YQKlcNi3ujYzYz94n476vZNlRDdhaa6ak0a7srwpt6cS7Dc0dZ1JaBFiJ6IMB1OVSm2sxfH20O5jfRXTm/HmXdVqyXFxNuXPdflD3DQs8U/Zr/LK8a6aZzWvXfIpqaQK+CZrlc0fhVSMebvkAkCi0jmB/EdoiJn74My+Ew==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
#endif

        #if LAUNCHER_TESTS
        public static readonly string SetupDir = Environment.GetEnvironmentVariable("WIS_TEST_SETUP_DIR");
#else
        public static readonly string SetupDir = @"C:\setup";
#endif
        public static readonly string MetaPath = Path.Combine(SetupDir, "payload_meta.json");
        public static readonly string ZipPath = Path.Combine(SetupDir, "wis-payload.zip");

        // Izolovani direktorijumi za transakcioni update, staging i rollback
        public static readonly string UpdateAttemptDir = Path.Combine(SetupDir, ".update_attempt");
        public static readonly string PartialZipPath = Path.Combine(UpdateAttemptDir, "payload.zip.partial");
        public static readonly string PendingMetaPath = Path.Combine(UpdateAttemptDir, "payload_meta.pending.json");
        public static readonly string StagingExtractDir = Path.Combine(SetupDir, ".staging_extract");
        public static readonly string BackupPrevDir = Path.Combine(SetupDir, ".backup_prev");
        public static readonly string UpdateTxPath = Path.Combine(SetupDir, ".update_tx.json");

        public static readonly string TargetUiExe = Path.Combine(SetupDir, "Skripte", "WIS.UI", "WIS.UI.exe");
        public static readonly string LegacyUiExe = Path.Combine(SetupDir, "Skripte", "GWADT.UI", "GWADT.UI.exe");

        // Minimalni obavezni skup fajlova koji mora postojati u svakom paketu i manifestu
        public static readonly string[] MandatoryRelativeFiles = new string[]
        {
            "Skripte/WIS.UI/WIS.UI.exe",
            "Skripte/Phases/Phase1-Network.ps1",
            "Skripte/Phases/Phase2-Drivers.ps1",
            "Skripte/Phases/Phase3-Optimization.ps1",
            "Skripte/Phases/Phase4-Apps.ps1",
            "Skripte/Phases/Phase5-Stress.ps1",
            "Skripte/Phases/Phase6-Finalize.ps1",
            "Skripte/Modules/WIS.Common.psm1",
            "Skripte/Modules/WIS.Hardware.psm1",
            "Skripte/Modules/WIS.Network.psm1",
            "Skripte/Modules/WIS.Cleanup.psm1",
            "Skripte/Modules/WIS.Telemetry.psm1",
            "Skripte/Modules/WIS.ScreenCapture.ps1"
        };

        // Od 2.17 paket mora sadržati i ugovor statusa, runtime biblioteke i potvrde Windows provera.
        public static readonly string[] MandatoryRelease217Files = new string[]
        {
            "Skripte/Modules/WIS.StatusContract.psm1",
            "WIS.Launcher.exe",
            "Skripte/WIS.UI/HidSharp.dll",
            "Skripte/WIS.UI/LibreHardwareMonitorLib.dll",
            "build-attestation.json",
            "vm-validation.json"
        };

        private static string _lastError = string.Empty;
        public static string LastError
        {
            get { return _lastError; }
            private set { _lastError = value ?? string.Empty; }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);

        /// <summary>
        /// Uklanja Zone.Identifier alternativni NTFS tok podataka (deblokira fajl skinut sa mreže).
        /// </summary>
        public static void UnblockFileZone(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    DeleteFile(filePath + ":Zone.Identifier");
                }
            }
            catch { }
        }

        /// <summary>
        /// Rekurzivno deblokira sve izvršne i skript fajlove u navedenom direktorijumu.
        /// </summary>
        public static void UnblockAllSetupFiles(string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) return;

                string[] searchPatterns = new string[] { "*.exe", "*.dll", "*.cmd", "*.bat", "*.ps1", "*.psm1" };
                for (int i = 0; i < searchPatterns.Length; i++)
                {
                    string[] files = Directory.GetFiles(targetDir, searchPatterns[i], SearchOption.AllDirectories);
                    for (int j = 0; j < files.Length; j++)
                    {
                        UnblockFileZone(files[j]);
                    }
                }
            }
            catch { }
        }

        #region Međuprocesni Mutex (Global\WIS_Launcher_Update_Mutex)

        private static bool TryAcquireUpdateMutex(int timeoutMs, out Mutex mutex)
        {
            mutex = null;
            try
            {
                bool createdNew;
                mutex = new Mutex(false, UpdateMutexName, out createdNew);
                try
                {
                    if (mutex.WaitOne(timeoutMs, false))
                    {
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // Prethodni proces je neočekivano prekinut dok je držao mutex; preuzimamo vlasništvo
                    return true;
                }
                mutex.Close();
                mutex = null;
                return false;
            }
            catch
            {
                if (mutex != null) { try { mutex.Close(); } catch { } }
                mutex = null;
                return false;
            }
        }

        private static void ReleaseUpdateMutex(Mutex mutex)
        {
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); } catch { }
            try { mutex.Close(); } catch { }
        }

        // Jedan mutex kroz HTTP/SMB download i ekstrakciju; unutrašnje metode ga rekurzivno drže.
        public static IDisposable BeginUpdateSession()
        {
            Mutex mutex;
            if (!TryAcquireUpdateMutex(15000, out mutex))
                throw new IOException("Drugi Launcher proces već koristi paket.");
            return new UpdateSession(mutex);
        }

        private sealed class UpdateSession : IDisposable
        {
            private Mutex mutex;
            public UpdateSession(Mutex value) { mutex = value; }
            public void Dispose()
            {
                if (mutex == null) return;
                ReleaseUpdateMutex(mutex);
                mutex = null;
            }
        }

        #endregion

        #region Parsiranje i RSA-2048 Verifikacija Manifesta

        /// <summary>
        /// Verifikuje RSA-SHA256 potpis nad UTF-8 bajtovima kanonskog signedPayload stringa
        /// koristeći .NET 4.0 RSACryptoServiceProvider i javni ključ u XML formatu.
        /// </summary>
        public static bool VerifyRsaSha256Signature(string signedPayload, string base64Signature, string publicKeyXml)
        {
            if (string.IsNullOrEmpty(signedPayload) || string.IsNullOrEmpty(base64Signature))
                return false;

            string keyXml = string.IsNullOrEmpty(publicKeyXml) ? TrustedPublicKeyXml : publicKeyXml;
            if (string.IsNullOrEmpty(keyXml))
                return false;

            try
            {
                byte[] payloadBytes = Encoding.UTF8.GetBytes(signedPayload);
                byte[] signatureBytes = Convert.FromBase64String(base64Signature.Trim());

                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(keyXml);
                    object halg = CryptoConfig.MapNameToOID("SHA256");
                    if (halg == null) halg = "SHA256";
                    return rsa.VerifyData(payloadBytes, halg, signatureBytes);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Računa deterministički kanonski SHA-256 heš rečnika required_files:
        /// ključevi su normalizovani sa '/' i sortirani po StringComparer.Ordinal,
        /// formatirani kao "putanja:sha256" i spojeni sa "\n".
        /// </summary>
        public static string ComputeRequiredFilesCanonicalSha256(Dictionary<string, string> requiredFiles)
        {
            if (requiredFiles == null || requiredFiles.Count == 0)
                return string.Empty;

            List<string> keys = new List<string>();
            Dictionary<string, string> normMap = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> kvp in requiredFiles)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value))
                    continue;
                string normKey = kvp.Key.Replace("\\\\", "/").Replace("\\", "/").Trim();
                string normVal = kvp.Value.Trim().ToLowerInvariant();
                if (!normMap.ContainsKey(normKey))
                {
                    keys.Add(normKey);
                    normMap[normKey] = normVal;
                }
            }

            keys.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append("\n");
                string k = keys[i];
                sb.Append(k).Append(":").Append(normMap[k]);
            }

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(utf8Bytes);
                StringBuilder hex = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    hex.Append(hash[i].ToString("x2"));
                }
                return hex.ToString();
            }
        }

        /// <summary>
        /// Parsira JSON manifest i vrši kompletnu kriptografsku proveru šeme, obaveznih fajlova,
        /// kanonskog signed_payload stringa i RSA-2048 potpisa.
        /// </summary>
        public static bool ParseAndVerifyManifest(string json, out PayloadVersionInfo info, out string error)
        {
            info = null;
            error = null;

            if (string.IsNullOrEmpty(json))
            {
                error = "Manifest je prazan.";
                return false;
            }

            try
            {
                PayloadVersionInfo parsed = new PayloadVersionInfo
                {
                    RawJson = json,
                    RequiredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    IsSignatureValid = false
                };

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> document = serializer.Deserialize<Dictionary<string, object>>(json);
                if (document == null) { error = "Manifest nije JSON objekat."; return false; }
                parsed.Product = JsonString(document, "product");
                parsed.SchemaVersion = Convert.ToInt32(document["schema_version"], CultureInfo.InvariantCulture);
                parsed.Version = JsonString(document, "version");
                parsed.ReleaseId = JsonString(document, "release_id");
                parsed.MinLauncherVersion = JsonString(document, "min_launcher_version");
                parsed.Sha256 = JsonString(document, "sha256");
                parsed.SizeBytes = Convert.ToInt64(document.ContainsKey("size") ? document["size"] : document["size_bytes"], CultureInfo.InvariantCulture);
                parsed.BuiltAt = JsonString(document, "built_at");
                parsed.SignedPayload = JsonString(document, "signed_payload");
                parsed.Signature = JsonString(document, "signature");
                Dictionary<string, object> required = document.ContainsKey("required_files") ?
                    document["required_files"] as Dictionary<string, object> : null;
                if (required == null) { error = "Nedostaje required_files objekat."; return false; }
                foreach (KeyValuePair<string, object> item in required)
                {
                    string relative = item.Key.Replace('\\', '/');
                    string hash = item.Value as string;
                    string pathError;
                    if (hash == null || !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z") ||
                        !IsSafeRelativeZipEntry(relative, Path.GetFullPath(SetupDir) + Path.DirectorySeparatorChar, out pathError) ||
                        parsed.RequiredFiles.ContainsKey(relative))
                    { error = "Neispravna ili duplirana stavka required_files: " + item.Key; return false; }
                    parsed.RequiredFiles.Add(relative, hash.ToLowerInvariant());
                }

                if (!string.Equals(parsed.Product, "WIS", StringComparison.Ordinal))
                {
                    error = "Manifest ne sadrži validan 'product': 'WIS'.";
                    return false;
                }

                if (parsed.SchemaVersion != 2)
                {
                    error = "Manifest koristi zastarelu ili nepoznatu verziju šeme (očekivano schema_version = 2).";
                    return false;
                }

                if (string.IsNullOrEmpty(parsed.Version) ||
                    string.IsNullOrEmpty(parsed.ReleaseId) ||
                    string.IsNullOrEmpty(parsed.MinLauncherVersion) ||
                    string.IsNullOrEmpty(parsed.Sha256) ||
                    parsed.SizeBytes <= 0 ||
                    string.IsNullOrEmpty(parsed.SignedPayload) ||
                    string.IsNullOrEmpty(parsed.Signature))
                {
                    error = "Manifestu nedostaju obavezna kriptografska polja.";
                    return false;
                }

                if (CompareVersions(CurrentLauncherSemVer, parsed.MinLauncherVersion) < 0)
                {
                    error = string.Format("Trenutni Launcher ({0}) je stariji od minimalno zahtevane verzije ({1}).",
                        CurrentLauncherSemVer, parsed.MinLauncherVersion);
                    return false;
                }

                for (int i = 0; i < MandatoryRelativeFiles.Length; i++)
                {
                    string reqPath = MandatoryRelativeFiles[i];
                    if (!parsed.RequiredFiles.ContainsKey(reqPath))
                    {
                        error = "U manifestu nedostaje obavezna komponenta: " + reqPath;
                        return false;
                    }
                }

                if (CompareVersions(parsed.Version, "2.17.0") >= 0)
                {
                    foreach (string dependency in MandatoryRelease217Files)
                    {
                        if (!parsed.RequiredFiles.ContainsKey(dependency))
                        {
                            error = "U manifestu izdanja 2.17+ nedostaje obavezna komponenta: " + dependency;
                            return false;
                        }
                    }
                }

                string reqCanonicalSha = ComputeRequiredFilesCanonicalSha256(parsed.RequiredFiles);
                string expectedSignedPayload = string.Format(
                    "WIS|v2|{0}|{1}|{2}|{3}|{4}|{5}",
                    parsed.Version,
                    parsed.ReleaseId,
                    parsed.SizeBytes,
                    parsed.Sha256.ToLowerInvariant(),
                    parsed.MinLauncherVersion,
                    reqCanonicalSha);

                if (!string.Equals(parsed.SignedPayload, expectedSignedPayload, StringComparison.Ordinal))
                {
                    error = "Polje signed_payload ne odgovara kanonskim poljima u manifestu (moguća izmena JSON sadržaja).";
                    return false;
                }

                if (!VerifyRsaSha256Signature(expectedSignedPayload, parsed.Signature, TrustedPublicKeyXml))
                {
                    error = "RSA-2048 digitalni potpis manifesta nije validan!";
                    return false;
                }

                parsed.IsSignatureValid = true;
                info = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = "Greška pri parsiranju ili verifikaciji manifesta: " + ex.Message;
                return false;
            }
        }

        #endregion

        private static string JsonString(Dictionary<string, object> document, string name)
        {
            object value;
            return document.TryGetValue(name, out value) ? value as string : null;
        }

        public static PayloadVersionInfo GetRemoteVersion(string hubUrl)
        {
            if (string.IsNullOrEmpty(hubUrl))
            {
                LastError = "Hub URL nije naveden.";
                return null;
            }

            try
            {
                string url = hubUrl.TrimEnd('/') + "/api/payload/version";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    PayloadVersionInfo info;
                    string err;
                    if (!ParseAndVerifyManifest(json, out info, out err))
                    {
                        LastError = "Server je vratio nepotpisan ili nevalidan manifest: " + err;
                        return null;
                    }
                    return info;
                }
            }
            catch (Exception ex)
            {
                LastError = "Neuspešno dohvatanje manifesta sa servera: " + ex.Message;
                return null;
            }
        }

        public static string GetLocalSha256()
        {
            try
            {
                if (File.Exists(MetaPath))
                {
                    string json = File.ReadAllText(MetaPath, Encoding.UTF8);
                    PayloadVersionInfo info;
                    string err;
                    if (ParseAndVerifyManifest(json, out info, out err) && info != null)
                    {
                        return info.Sha256;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Očitava lokalnu verziju WIS.UI izvršnog fajla ili iz potvrđenog metapodatka.
        /// Vraća "0.0.0.0" ako fajl ne postoji.
        /// </summary>
        public static string GetLocalVersion()
        {
            try
            {
                if (File.Exists(MetaPath))
                {
                    string json = File.ReadAllText(MetaPath, Encoding.UTF8);
                    PayloadVersionInfo info;
                    string err;
                    if (ParseAndVerifyManifest(json, out info, out err) && info != null && !string.IsNullOrEmpty(info.Version))
                    {
                        return info.Version.Trim();
                    }
                }
            }
            catch { }

            string[] candidates = new string[]
            {
                TargetUiExe,
                Path.Combine(SetupDir, "WIS.UI.exe"),
                LegacyUiExe,
                Path.Combine(SetupDir, "GWADT.UI.exe")
            };

            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrEmpty(fvi.ProductVersion))
                        {
                            return fvi.ProductVersion.Trim();
                        }
                        if (!string.IsNullOrEmpty(fvi.FileVersion))
                        {
                            return fvi.FileVersion.Trim();
                        }
                    }
                }
                catch { }
            }

            return "0.0.0.0";
        }

        public static int CompareVersions(string localVer, string remoteVer)
        {
            if (string.IsNullOrEmpty(localVer) && string.IsNullOrEmpty(remoteVer))
                return 0;
            if (string.IsNullOrEmpty(localVer))
                return -1;
            if (string.IsNullOrEmpty(remoteVer))
                return 1;

            string v1 = localVer.Trim();
            string v2 = remoteVer.Trim();

            if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (v1.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                v1 = v1.Substring(1).Trim();
            if (v2.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                v2 = v2.Substring(1).Trim();

            if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
                return 0;

            Version ver1, ver2;
            string norm1 = NormalizeVersionString(v1);
            string norm2 = NormalizeVersionString(v2);

            if (Version.TryParse(norm1, out ver1) && Version.TryParse(norm2, out ver2))
            {
                return ver1.CompareTo(ver2);
            }

            long n1, n2;
            if (long.TryParse(v1, out n1) && long.TryParse(v2, out n2))
            {
                return n1.CompareTo(n2);
            }

            string[] parts1 = v1.Split(new char[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            string[] parts2 = v2.Split(new char[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            int maxLen = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < maxLen; i++)
            {
                string p1 = i < parts1.Length ? parts1[i] : "0";
                string p2 = i < parts2.Length ? parts2[i] : "0";

                long seg1, seg2;
                if (long.TryParse(p1, out seg1) && long.TryParse(p2, out seg2))
                {
                    if (seg1 != seg2)
                        return seg1.CompareTo(seg2);
                }
                else
                {
                    int strCmp = string.Compare(p1, p2, StringComparison.OrdinalIgnoreCase);
                    if (strCmp != 0)
                        return strCmp;
                }
            }

            return 0;
        }

        private static string NormalizeVersionString(string ver)
        {
            if (string.IsNullOrEmpty(ver)) return "0.0.0.0";
            string[] parts = ver.Split('.');
            if (parts.Length > 4) return ver;

            if (parts.Length == 1 && ver.Length > 5)
                return ver;

            foreach (string part in parts)
            {
                int dummy;
                if (!int.TryParse(part, out dummy) || dummy < 0)
                    return ver;
            }

            if (parts.Length == 1) return parts[0] + ".0.0.0";
            if (parts.Length == 2) return parts[0] + "." + parts[1] + ".0.0";
            if (parts.Length == 3) return parts[0] + "." + parts[1] + "." + parts[2] + ".0";
            if (parts.Length == 4) return ver;

            return ver;
        }

        public static bool DeleteLocalPayload()
        {
            Mutex mutex = null;
            if (!TryAcquireUpdateMutex(10000, out mutex))
            {
                LastError = "Paket je zauzet drugim Launcher procesom.";
                return false;
            }
            try
            {
                KillActiveUiProcesses();

                string[] uiDirs = new string[]
                {
                    Path.Combine(SetupDir, @"Skripte\WIS.UI"),
                    Path.Combine(SetupDir, @"Skripte\GWADT.UI"),
                    StagingExtractDir,
                    UpdateAttemptDir
                };

                foreach (string uiDir in uiDirs)
                {
                    SafeDeleteDirectory(uiDir);
                }

                SafeDeleteFile(MetaPath);
                SafeDeleteFile(ZipPath);
                SafeDeleteFile(ZipPath + ".part");
                SafeDeleteFile(PartialZipPath);
                SafeDeleteFile(PendingMetaPath);
                SafeDeleteFile(UpdateTxPath);

                if (Directory.Exists(SetupDir))
                {
                    try
                    {
                        string[] partFiles = Directory.GetFiles(SetupDir, "*.part");
                        foreach (string pf in partFiles)
                        {
                            SafeDeleteFile(pf);
                        }
                    }
                    catch { }
                }

                return !File.Exists(TargetUiExe) && !File.Exists(LegacyUiExe);
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseUpdateMutex(mutex);
            }
        }

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch { }
        }

        private static void SafeDeleteDirectory(string dirPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
                {
                    RemoveReadOnlyRecursive(dirPath);
                    try
                    {
                        Directory.Delete(dirPath, true);
                    }
                    catch
                    {
                        Thread.Sleep(300);
                        RemoveReadOnlyRecursive(dirPath);
                        Directory.Delete(dirPath, true);
                    }
                }
            }
            catch { }
        }

        private static void RemoveReadOnlyRecursive(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    DirectoryInfo di = new DirectoryInfo(path);
                    di.Attributes = FileAttributes.Normal;

                    foreach (FileInfo fi in di.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            fi.Attributes = FileAttributes.Normal;
                        }
                        catch { }
                    }

                    foreach (DirectoryInfo sub in di.GetDirectories())
                    {
                        try
                        {
                            sub.Attributes = FileAttributes.Normal;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Proverava da li je lokalni instalirani paket u C:\setup kompletan, verifikovan potpisanim manifestom
        /// i da li se njegov SHA-256 poklapa sa remoteSha256, pri čemu SVE obavezne komponente i TargetUiExe
        /// zaista postoje na disku i imaju očekivani SHA-256 heš iz MetaPath.
        /// </summary>
        public static bool IsLocalPayloadUpToDate(string remoteSha256)
        {
            if (string.IsNullOrEmpty(remoteSha256)) return false;

            // Ako postoji nezavršena transakcija ažuriranja, paket nije u potvrđenom stanju
            if (File.Exists(UpdateTxPath))
            {
                string txState = ReadTransactionState();
                if (!string.Equals(txState, "committed", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!File.Exists(MetaPath) || !File.Exists(TargetUiExe))
                return false;

            try
            {
                string metaJson = File.ReadAllText(MetaPath, Encoding.UTF8);
                PayloadVersionInfo localManifest;
                string err;
                if (!ParseAndVerifyManifest(metaJson, out localManifest, out err) || localManifest == null)
                    return false;

                if (!string.Equals(localManifest.Sha256, remoteSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;

                // Provera prisustva i SHA-256 heša svih obaveznih fajlova iz potpisanog manifesta na disku
                return VerifyDirectoryMatchesManifest(SetupDir, localManifest, out err);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Proverava da li u navedenom direktorijumu (staging ili C:\setup) postoje TargetUiExe
        /// i sve komponente iz manifesta sa tačnim SHA-256 heševima.
        /// </summary>
        public static bool VerifyDirectoryMatchesManifest(string baseDir, PayloadVersionInfo manifest, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            {
                error = "Ciljni direktorijum ne postoji: " + baseDir;
                return false;
            }

            if (manifest == null || !manifest.IsSignatureValid || manifest.RequiredFiles == null || manifest.RequiredFiles.Count == 0)
            {
                error = "Manifest nije validan ili nema potpisan popis obaveznih fajlova.";
                return false;
            }

            string unsafePath;
            if (ContainsReparsePointsRecursive(baseDir, out unsafePath))
            { error = "Nedozvoljena reparse putanja: " + unsafePath; return false; }
            foreach (string relative in manifest.RequiredFiles.Keys)
            {
                string pathError;
                if (!IsSafeRelativeZipEntry(relative, Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar, out pathError))
                { error = pathError; return false; }
            }

            for (int i = 0; i < MandatoryRelativeFiles.Length; i++)
            {
                string mandatoryRel = MandatoryRelativeFiles[i];
                string mandatoryFullPath = Path.Combine(baseDir, mandatoryRel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(mandatoryFullPath))
                {
                    error = "Nedostaje obavezna datoteka: " + mandatoryRel;
                    return false;
                }
            }

            foreach (KeyValuePair<string, string> kvp in manifest.RequiredFiles)
            {
                string relPath = kvp.Key.Replace('/', Path.DirectorySeparatorChar);
                string expectedHash = kvp.Value;
                string fullPath = Path.Combine(baseDir, relPath);

                if (!File.Exists(fullPath))
                {
                    error = "Fajl iz manifesta ne postoji na disku: " + kvp.Key;
                    return false;
                }

                string actualHash = CalculateSha256(fullPath);
                if (string.IsNullOrEmpty(actualHash) || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = string.Format("SHA-256 nepoklapanje za fajl '{0}' (očekivano: {1}, stvarno: {2}).",
                        kvp.Key, expectedHash, actualHash ?? "null");
                    return false;
                }
            }

            foreach (string file in EnumeratePayloadFiles(baseDir, true))
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".exe" && extension != ".dll" && extension != ".ps1" && extension != ".psm1" &&
                    extension != ".cmd" && extension != ".bat" && extension != ".vbs" && extension != ".js" && extension != ".py") continue;
                string relative = file.Substring(Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                if (!manifest.RequiredFiles.ContainsKey(relative))
                { error = "Nepotpisana izvršna komponenta: " + relative; return false; }
            }
            return true;
        }

        private static IEnumerable<string> EnumeratePayloadFiles(string directory, bool packageRoot)
        {
            foreach (string file in Directory.GetFiles(directory)) yield return Path.GetFullPath(file);
            foreach (string child in Directory.GetDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (IsReservedUpdateItem(name) || name == ".backup_prev.new" || name == ".backup_prev.old") continue;
                // C:\setup\Programi je spoljašnji installer cache iz Programi.tar, van WIS payload-a.
                // Ovaj izuzetak važi samo za dodatne izvršne fajlove u korenu; signed hash i reparse provere ostaju obavezne.
                if (packageRoot && name.Equals("Programi", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string file in EnumeratePayloadFiles(child, false)) yield return file;
            }
        }

        /// <summary>
        /// Identifikuje i zaustavlja aktivne WIS.UI / GWADT.UI procese isključivo po PID-u i putanji
        /// izvršnog fajla unutar C:\setup, bez slepog masovnog gašenja nevezanih procesa.
        /// </summary>
        public static void KillActiveUiProcesses()
        {
            int currentPid = -1;
            try { currentPid = Process.GetCurrentProcess().Id; } catch { }

            string fullSetupRoot = Path.GetFullPath(SetupDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string[] procNames = new string[] { "WIS.UI", "GWADT.UI" };
            foreach (string procName in procNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(procName);
                    foreach (Process p in procs)
                    {
                        try
                        {
                            if (p.Id == currentPid || p.HasExited)
                                continue;

                            bool isSetupProcess = false;
                            try
                            {
                                if (p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                                {
                                    string exeFull = Path.GetFullPath(p.MainModule.FileName);
                                    if (exeFull.StartsWith(fullSetupRoot, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isSetupProcess = true;
                                    }
                                }
                            }
                            catch
                            {
                                // Ako pristup MainModule nije moguć zbog privilegija, proveravamo da li drži lock na TargetUiExe
                                if (IsFileLockedForWrite(TargetUiExe) || IsFileLockedForWrite(LegacyUiExe))
                                {
                                    isSetupProcess = true;
                                }
                            }

                            if (isSetupProcess)
                            {
                                try
                                {
                                    if (p.CloseMainWindow())
                                    {
                                        p.WaitForExit(1500);
                                    }
                                }
                                catch { }

                                if (!p.HasExited)
                                {
                                    p.Kill();
                                    p.WaitForExit(3000);
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public static bool IsFileLockedForWrite(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                FileAttributes attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                }

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch
            {
                return true;
            }
        }

        private static bool AreExistingSetupBinariesLocked(out string lockedFile)
        {
            lockedFile = null;
            string[] candidates = new string[]
            {
                TargetUiExe,
                LegacyUiExe,
                Path.Combine(SetupDir, "WIS.UI.exe"),
                Path.Combine(SetupDir, "GWADT.UI.exe"),
                Path.Combine(SetupDir, @"Skripte\WIS.UI\LibreHardwareMonitorLib.dll"),
                Path.Combine(SetupDir, @"Skripte\WIS.UI\HidSharp.dll")
            };

            // Pokrij i sve launcher kopije / dodatne biblioteke iz novog paketa.
            if (Directory.Exists(StagingExtractDir))
            {
                foreach (string staged in Directory.GetFiles(StagingExtractDir, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(staged).ToLowerInvariant();
                    if (extension != ".exe" && extension != ".dll") continue;
                    string target = Path.Combine(SetupDir, staged.Substring(StagingExtractDir.Length + 1));
                    if (File.Exists(target) && CalculateSha256(target) != CalculateSha256(staged) && IsFileLockedForWrite(target))
                    { lockedFile = target; return true; }
                }
            }
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]) && IsFileLockedForWrite(candidates[i]))
                {
                    lockedFile = candidates[i];
                    return true;
                }
            }
            return false;
        }

        #region Transakcioni Download (HTTP i SMB) sa Obaveznom Verifikacijom Potpisa i SHA-256

        /// <summary>
        /// Preuzima payload paket preko HTTP-a u izolovani C:\setup\.update_attempt\payload.zip.partial.
        /// Obavezno zahteva i verifikuje RSA-2048 potpis manifesta, veličinu i SHA-256 heš preuzetog fajla.
        /// NE UPISUJE C:\setup\payload_meta.json pre uspešne ekstrakcije i promocije!
        /// </summary>
        public static bool DownloadPayload(string hubUrl, string expectedSha256, string rawMetaJson, Action<long, long, double> progressCallback)
        {
            LastError = string.Empty;
            if (string.IsNullOrEmpty(hubUrl))
            {
                LastError = "Hub URL nije definisan.";
                return false;
            }

            Mutex mutex = null;
            if (!TryAcquireUpdateMutex(15000, out mutex))
            {
                LastError = "Drugi WIS Launcher proces već izvršava ažuriranje (Mutex zauzet).";
                return false;
            }

            try
            {
                // Ako manifest nije prosleđen, pokušaj da ga preuzmeš sa servera
                if (string.IsNullOrEmpty(rawMetaJson))
                {
                    PayloadVersionInfo fetched = GetRemoteVersion(hubUrl);
                    if (fetched != null)
                    {
                        rawMetaJson = fetched.RawJson;
                        if (string.IsNullOrEmpty(expectedSha256))
                        {
                            expectedSha256 = fetched.Sha256;
                        }
                    }
                }

                if (string.IsNullOrEmpty(rawMetaJson) || string.IsNullOrEmpty(expectedSha256))
                {
                    LastError = "Preuzimanje odbijeno: nedostaje potpisani manifest ili očekivani SHA-256 heš.";
                    return false;
                }

                // 1. Kriptografska verifikacija RSA-2048 potpisa i šeme manifesta
                PayloadVersionInfo manifestInfo;
                string verifyErr;
                if (!ParseAndVerifyManifest(rawMetaJson, out manifestInfo, out verifyErr) || manifestInfo == null || !manifestInfo.IsSignatureValid)
                {
                    LastError = "Preuzimanje odbijeno — nevalidan potpis manifesta: " + verifyErr;
                    return false;
                }

                // Provera da li se prosleđeni expectedSha256 poklapa sa potpisanim manifestom
                if (!string.Equals(manifestInfo.Sha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "Preuzimanje odbijeno: prosleđeni SHA-256 se ne poklapa sa potpisanim manifestom.";
                    return false;
                }

                if (!Directory.Exists(SetupDir))
                {
                    Directory.CreateDirectory(SetupDir);
                }

                SafeDeleteDirectory(UpdateAttemptDir);
                Directory.CreateDirectory(UpdateAttemptDir);

                string downloadUrl = hubUrl.TrimEnd('/') + "/api/payload/download";

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(downloadUrl);
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 30000;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long totalBytes = resp.ContentLength > 0 ? resp.ContentLength : manifestInfo.SizeBytes;
                    using (Stream responseStream = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(PartialZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[65536];
                        int bytesRead;
                        long totalRead = 0;
                        Stopwatch sw = Stopwatch.StartNew();
                        long lastReportTicks = 0;

                        while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead > manifestInfo.SizeBytes)
                            {
                                throw new InvalidDataException("Preuzeti tok podataka prelazi potpisanu veličinu paketa u manifestu.");
                            }

                            fs.Write(buffer, 0, bytesRead);

                            long nowTicks = sw.ElapsedMilliseconds;
                            if (nowTicks - lastReportTicks >= 100 || totalRead == totalBytes)
                            {
                                lastReportTicks = nowTicks;
                                double seconds = sw.Elapsed.TotalSeconds;
                                double speedBytesPerSec = seconds > 0.05 ? (totalRead / seconds) : 0;
                                if (progressCallback != null)
                                {
                                    progressCallback(totalRead, totalBytes, speedBytesPerSec);
                                }
                            }
                        }
                        fs.Flush(true);
                    }
                }

                // 2. Provera tačne veličine preuzetog .partial fajla
                FileInfo partialFi = new FileInfo(PartialZipPath);
                if (!partialFi.Exists || partialFi.Length != manifestInfo.SizeBytes)
                {
                    LastError = string.Format("Veličina preuzetog paketa ({0} B) ne odgovara potpisanom manifestu ({1} B).",
                        partialFi.Exists ? partialFi.Length : 0, manifestInfo.SizeBytes);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                // 3. Provera SHA-256 integriteta nad payload.zip.partial
                string computedSha = CalculateSha256(PartialZipPath);
                if (string.IsNullOrEmpty(computedSha) ||
                    !string.Equals(computedSha, manifestInfo.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(computedSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "SHA-256 heš preuzetog paketa ne odgovara potpisanom manifestu!";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                // Čuvamo verifikovani manifest u privremenom .update_attempt folderu (NE u C:\setup\payload_meta.json!)
                File.WriteAllText(PendingMetaPath, rawMetaJson, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Greška pri HTTP preuzimanju paketa: " + ex.Message;
                SafeDeleteDirectory(UpdateAttemptDir);
                return false;
            }
            finally
            {
                ReleaseUpdateMutex(mutex);
            }
        }

        private static string FindIniConfigFile()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] searchPaths = new string[]
                {
                    Path.Combine(baseDir, "Wifi-Servis.ini"),
                    @"C:\setup\Wifi-Servis.ini",
                    @"C:\setup\Skripte\Wifi-Servis.ini"
                };
                for (int i = 0; i < searchPaths.Length; i++)
                {
                    if (File.Exists(searchPaths[i])) return searchPaths[i];
                }

                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Fixed))
                    {
                        string p = Path.Combine(drive.RootDirectory.FullName, "Wifi-Servis.ini");
                        if (File.Exists(p)) return p;
                        string p2 = Path.Combine(drive.RootDirectory.FullName, @"setup\Wifi-Servis.ini");
                        if (File.Exists(p2)) return p2;
                    }
                }
            }
            catch {}
            return null;
        }

        /// <summary>
        /// Osigurava SMB autentifikovanu sesiju za zaključana deljenja (WIS, Programi, servis)
        /// koristeći akreditive iz Wifi-Servis.ini (ili Guest fallback).
        /// </summary>
        private static void EnsureSmbAuthentication(string host)
        {
            if (string.IsNullOrEmpty(host)) return;
            try
            {
                string user = "admin";
                string pass = string.Empty;

                string iniPath = FindIniConfigFile();
                if (!string.IsNullOrEmpty(iniPath) && File.Exists(iniPath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(iniPath);
                        string section = string.Empty;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                            if (line.StartsWith("[") && line.EndsWith("]"))
                            {
                                section = line.Substring(1, line.Length - 2).Trim();
                                continue;
                            }
                            int eq = line.IndexOf('=');
                            if (eq > 0 && string.Equals(section, "ServisNas", StringComparison.OrdinalIgnoreCase))
                            {
                                string k = line.Substring(0, eq).Trim();
                                string v = line.Substring(eq + 1).Trim();
                                if (string.Equals(k, "Username", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
                                    user = v;
                                else if (string.Equals(k, "Password", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v) && !string.Equals(v, "promeni_lozinku", StringComparison.OrdinalIgnoreCase) && !string.Equals(v, "CHANGE_ME", StringComparison.OrdinalIgnoreCase))
                                    pass = v;
                            }
                        }
                    }
                    catch
                    {
                        // Nastavi sa podrazumevanim servisnim akreditivima
                    }
                }

                string userArg = user.IndexOf('\\') >= 0 ? user : string.Format(@"{0}\{1}", host, user);
                string[] shares = new string[] { "WIS", "Programi", "servis" };

                for (int s = 0; s < shares.Length; s++)
                {
                    string unc = string.Format(@"\\{0}\{1}", host, shares[s]);
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo();
                        psi.FileName = "net.exe";
                        if (!string.IsNullOrEmpty(pass))
                            psi.Arguments = string.Format("use \"{0}\" \"{1}\" /user:\"{2}\" /persistent:no", unc, pass, userArg);
                        else
                            psi.Arguments = string.Format("use \"{0}\" \"\" /user:guest /persistent:no", unc);
                        psi.CreateNoWindow = true;
                        psi.UseShellExecute = false;
                        psi.WindowStyle = ProcessWindowStyle.Hidden;

                        using (Process proc = Process.Start(psi))
                        {
                            if (proc != null) proc.WaitForExit(3000);
                        }
                    }
                    catch
                    {
                        // Ignoriši greške pojedinačnog povezivanja
                    }
                }
            }
            catch
            {
                // Ignoriši globalne greške autentifikacije
            }
        }

        /// <summary>
        /// Proverava potpisani payload_meta.json na SMB deljenju (\\host\WIS, \\host\servis...)
        /// kao fallback kada HTTP API nije dostupan ili u RAM-u drži zastareli manifest.
        /// </summary>
        public static PayloadVersionInfo GetRemoteSmbVersion(string host)
        {
            if (string.IsNullOrEmpty(host))
                return null;

            try
            {
                EnsureSmbAuthentication(host);

                string[] candidateDirs = new string[]
                {
                    string.Format(@"\\{0}\servis", host),
                    string.Format(@"\\{0}\servis\storage\cache", host),
                    string.Format(@"\\{0}\servis\servis-hub\storage\cache", host),
                    string.Format(@"\\{0}\WIS", host),
                    string.Format(@"\\{0}\WIS\storage\cache", host)
                };

                for (int i = 0; i < candidateDirs.Length; i++)
                {
                    string dir = candidateDirs[i];
                    string metaCandidate = Path.Combine(dir, "payload_meta.json");
                    if (!File.Exists(metaCandidate))
                        continue;

                    string wisZip = Path.Combine(dir, "wis-payload.zip");
                    string gwadtZip = Path.Combine(dir, "gwadt-payload.zip");
                    string foundZip = File.Exists(wisZip) ? wisZip : (File.Exists(gwadtZip) ? gwadtZip : null);
                    if (string.IsNullOrEmpty(foundZip))
                        continue;

                    string smbMetaJson = File.ReadAllText(metaCandidate, Encoding.UTF8);
                    PayloadVersionInfo manifestInfo;
                    string verifyErr;
                    if (ParseAndVerifyManifest(smbMetaJson, out manifestInfo, out verifyErr) && manifestInfo != null && manifestInfo.IsSignatureValid)
                    {
                        FileInfo fi = new FileInfo(foundZip);
                        if (fi.Exists && fi.Length == manifestInfo.SizeBytes)
                        {
                            return manifestInfo;
                        }
                    }
                }
            }
            catch
            {
                // Ignoriši greške čitanja SMB manifesta
            }

            return null;
        }

        /// <summary>
        /// Preuzima payload sa SMB deljenja uz OBAVEZNO učitavanje i verifikaciju potpisanog payload_meta.json
        /// manifesta, veličine i SHA-256 heša. Neprovereno direktno kopiranje UI foldera je zabranjeno.
        /// </summary>
        public static bool DownloadFromSmb(string host, Action<long, long, double> progressCallback)
        {
            LastError = string.Empty;
            if (string.IsNullOrEmpty(host))
            {
                LastError = "SMB host nije naveden.";
                return false;
            }

            Mutex mutex = null;
            if (!TryAcquireUpdateMutex(15000, out mutex))
            {
                LastError = "Drugi WIS Launcher proces već izvršava ažuriranje (Mutex zauzet).";
                return false;
            }

            try
            {
                if (!Directory.Exists(SetupDir))
                {
                    Directory.CreateDirectory(SetupDir);
                }

                EnsureSmbAuthentication(host);

                string[] candidateDirs = new string[]
                {
                    string.Format(@"\\{0}\servis", host),
                    string.Format(@"\\{0}\servis\storage\cache", host),
                    string.Format(@"\\{0}\servis\servis-hub\storage\cache", host),
                    string.Format(@"\\{0}\WIS", host),
                    string.Format(@"\\{0}\WIS\storage\cache", host)
                };

                string foundZipSource = null;
                string foundMetaSource = null;

                for (int i = 0; i < candidateDirs.Length; i++)
                {
                    string dir = candidateDirs[i];
                    string metaCandidate = Path.Combine(dir, "payload_meta.json");
                    if (!File.Exists(metaCandidate))
                        continue;

                    string wisZip = Path.Combine(dir, "wis-payload.zip");
                    string gwadtZip = Path.Combine(dir, "gwadt-payload.zip");

                    if (File.Exists(wisZip))
                    {
                        foundMetaSource = metaCandidate;
                        foundZipSource = wisZip;
                        break;
                    }
                    if (File.Exists(gwadtZip))
                    {
                        foundMetaSource = metaCandidate;
                        foundZipSource = gwadtZip;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(foundZipSource) || string.IsNullOrEmpty(foundMetaSource))
                {
                    LastError = "Na SMB deljenju nije pronađen potpisani par (payload_meta.json + payload ZIP).";
                    return false;
                }

                // 1. Učitavanje i kriptografska verifikacija SMB manifesta
                string smbMetaJson = File.ReadAllText(foundMetaSource, Encoding.UTF8);
                PayloadVersionInfo manifestInfo;
                string verifyErr;
                if (!ParseAndVerifyManifest(smbMetaJson, out manifestInfo, out verifyErr) || manifestInfo == null || !manifestInfo.IsSignatureValid)
                {
                    LastError = "SMB preuzimanje odbijeno — nevalidan RSA potpis manifesta: " + verifyErr;
                    return false;
                }

                FileInfo fi = new FileInfo(foundZipSource);
                if (!fi.Exists || fi.Length != manifestInfo.SizeBytes)
                {
                    LastError = "SMB preuzimanje odbijeno: veličina ZIP fajla na SMB deljenju ne odgovara potpisanom manifestu.";
                    return false;
                }

                SafeDeleteDirectory(UpdateAttemptDir);
                Directory.CreateDirectory(UpdateAttemptDir);

                long totalBytes = fi.Length;
                long totalRead = 0;
                Stopwatch sw = Stopwatch.StartNew();
                long lastReportTicks = 0;

                using (FileStream src = new FileStream(foundZipSource, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream dst = new FileStream(PartialZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > manifestInfo.SizeBytes)
                        {
                            throw new InvalidDataException("SMB tok podataka prelazi potpisanu veličinu iz manifesta.");
                        }

                        dst.Write(buffer, 0, read);

                        long nowTicks = sw.ElapsedMilliseconds;
                        if (nowTicks - lastReportTicks >= 100 || totalRead == totalBytes)
                        {
                            lastReportTicks = nowTicks;
                            double seconds = sw.Elapsed.TotalSeconds;
                            double speedBytesPerSec = seconds > 0.05 ? (totalRead / seconds) : 0;
                            if (progressCallback != null)
                            {
                                progressCallback(totalRead, totalBytes, speedBytesPerSec);
                            }
                        }
                    }
                    dst.Flush(true);
                }

                // 2. Verifikacija veličine i SHA-256 nad preuzetim payload.zip.partial
                FileInfo partialFi = new FileInfo(PartialZipPath);
                if (!partialFi.Exists || partialFi.Length != manifestInfo.SizeBytes)
                {
                    LastError = "Veličina preuzetog SMB paketa ne odgovara potpisanom manifestu.";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                string computedSha = CalculateSha256(PartialZipPath);
                if (string.IsNullOrEmpty(computedSha) || !string.Equals(computedSha, manifestInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "SHA-256 preuzetog SMB paketa ne odgovara potpisanom manifestu!";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                File.WriteAllText(PendingMetaPath, smbMetaJson, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Greška pri SMB preuzimanju paketa: " + ex.Message;
                SafeDeleteDirectory(UpdateAttemptDir);
                return false;
            }
            finally
            {
                ReleaseUpdateMutex(mutex);
            }
        }

        public static bool HasPendingVerifiedPayload()
        {
            return File.Exists(PartialZipPath) && File.Exists(PendingMetaPath);
        }

        #endregion

        #region Pre-Ekstrakciona Zip-Slip Inspekcija (.NET 4.0 Binarni Parser)

        /// <summary>
        /// Direktno pregleda binarne Central Directory i Local File Header zapise u ZIP arhivi
        /// pre bilo kakvog raspakivanja i odbija arhivu ako bilo koji ulaz sadrži '..', apsolutnu
        /// putanju, oznaku diska (':') ili izlazi izvan ciljnog stagingDir direktorijuma (Zip-Slip zaštita).
        /// </summary>
        public static bool ValidateZipEntriesAgainstZipSlip(string zipFilePath, string stagingDir, out List<string> entries, out string error)
        {
            entries = new List<string>();
            error = null;

            if (string.IsNullOrEmpty(zipFilePath) || !File.Exists(zipFilePath))
            {
                error = "ZIP fajl ne postoji za Zip-Slip proveru.";
                return false;
            }

            try
            {
                string fullStagingRoot = Path.GetFullPath(stagingDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                using (FileStream fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    long fileLen = fs.Length;
                    if (fileLen < 22)
                    {
                        error = "ZIP arhiva je premala ili oštećena.";
                        return false;
                    }

                    // Potraga za End of Central Directory (EOCD) potpisom 0x06054b50 sa kraja fajla
                    long maxSearch = Math.Min(fileLen, 65557L);
                    fs.Seek(fileLen - maxSearch, SeekOrigin.Begin);
                    byte[] tail = br.ReadBytes((int)maxSearch);

                    int eocdPos = -1;
                    for (int i = tail.Length - 22; i >= 0; i--)
                    {
                        if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                        {
                            eocdPos = i;
                            break;
                        }
                    }

                    if (eocdPos < 0)
                    {
                        error = "ZIP arhiva nema validan End of Central Directory (EOCD) zapis.";
                        return false;
                    }

                    ushort totalEntries = BitConverter.ToUInt16(tail, eocdPos + 10);
                    uint cdSize = BitConverter.ToUInt32(tail, eocdPos + 12);
                    uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

                    if ((long)cdOffset + (long)cdSize > fileLen)
                    {
                        error = "Central Directory u ZIP arhivi izlazi van granica fajla.";
                        return false;
                    }

                    if (BitConverter.ToUInt16(tail, eocdPos + 4) != 0 || BitConverter.ToUInt16(tail, eocdPos + 6) != 0 ||
                        totalEntries == 65535 || totalEntries != BitConverter.ToUInt16(tail, eocdPos + 8))
                        throw new InvalidDataException("Višedelni i ZIP64 paketi nisu podržani.");
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fs.Seek(cdOffset, SeekOrigin.Begin);
                    for (int idx = 0; idx < totalEntries; idx++)
                    {
                        byte[] header = br.ReadBytes(46);
                        if (header.Length != 46 || BitConverter.ToUInt32(header, 0) != 0x02014b50U)
                            throw new InvalidDataException("Neispravan centralni ZIP header.");
                        ushort flags = BitConverter.ToUInt16(header, 8);
                        uint compressedSize = BitConverter.ToUInt32(header, 20);
                        ushort nameLen = BitConverter.ToUInt16(header, 28);
                        ushort extraLen = BitConverter.ToUInt16(header, 30);
                        ushort commentLen = BitConverter.ToUInt16(header, 32);
                        uint externalAttributes = BitConverter.ToUInt32(header, 38);
                        uint localOffset = BitConverter.ToUInt32(header, 42);
                        uint unixType = (externalAttributes >> 16) & 0xF000;
                        if ((flags & 1) != 0 || (unixType != 0 && unixType != 0x8000 && unixType != 0x4000))
                            throw new InvalidDataException("Šifrovani, symlink i posebni ZIP ulazi nisu dozvoljeni.");
                        byte[] nameBytes = br.ReadBytes(nameLen);
                        if (nameBytes.Length != nameLen) throw new InvalidDataException("Prekinuto ZIP ime.");
                        string entryName = new UTF8Encoding(false, true).GetString(nameBytes);
                        if (!IsSafeRelativeZipEntry(entryName, fullStagingRoot, out error)) return false;
                        if (!names.Add(entryName.Replace('\\', '/').TrimEnd('/')))
                            throw new InvalidDataException("Duplirana ZIP putanja: " + entryName);
                        long nextCentral = fs.Position + extraLen + commentLen;
                        if (nextCentral > (long)cdOffset + cdSize) throw new InvalidDataException("Centralni ZIP zapis izlazi van granica.");
                        fs.Seek(localOffset, SeekOrigin.Begin);
                        byte[] local = br.ReadBytes(30);
                        if (local.Length != 30 || BitConverter.ToUInt32(local, 0) != 0x04034b50U ||
                            BitConverter.ToUInt16(local, 6) != flags || BitConverter.ToUInt16(local, 26) != nameLen)
                            throw new InvalidDataException("Lokalni i centralni ZIP header se ne poklapaju.");
                        byte[] localName = br.ReadBytes(nameLen);
                        if (localName.Length != nameLen || new UTF8Encoding(false, true).GetString(localName) != entryName)
                            throw new InvalidDataException("Lokalna i centralna ZIP putanja se ne poklapaju.");
                        if (fs.Position + BitConverter.ToUInt16(local, 28) + compressedSize > cdOffset)
                            throw new InvalidDataException("ZIP podaci izlaze van granica arhive.");
                        entries.Add(entryName);
                        fs.Seek(nextCentral, SeekOrigin.Begin);
                    }

                }

                if (entries.Count == 0)
                {
                    error = "ZIP arhiva ne sadrži nijedan fajl.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Greška pri Zip-Slip validaciji ZIP ulaza: " + ex.Message;
                return false;
            }
        }

        private static bool IsSafeRelativeZipEntry(string entryName, string fullStagingRootWithSlash, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(entryName))
            {
                error = "ZIP sadrži ulaz sa praznim imenom.";
                return false;
            }

            if (entryName.IndexOf('\0') >= 0)
            {
                error = "ZIP ulaz sadrži nedozvoljen null bajt.";
                return false;
            }

            // Zabrana apsolutnih putanja, UNC putanja i oznaka diska / NTFS ADS tokova (':')
            if (entryName.StartsWith("/") || entryName.StartsWith("\\") || entryName.IndexOf(':') >= 0)
            {
                error = "Zip-Slip detektovan (apsolutna putanja ili oznaka diska): " + entryName;
                return false;
            }

            string normalized = entryName.Replace('\\', '/');
            if (normalized.IndexOf("//", StringComparison.Ordinal) >= 0)
            { error = "Nedozvoljeni prazni segmenti putanje: " + entryName; return false; }
            string[] segments = normalized.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || IsReservedUpdateItem(segments[0]) ||
                segments[0].Equals("payload_meta.json", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals(".backup_prev.new", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals(".backup_prev.old", StringComparison.OrdinalIgnoreCase))
            { error = "Nedozvoljena rezervisana update putanja: " + entryName; return false; }
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].EndsWith(".") || segments[i].EndsWith(" ") ||
                    segments[i].IndexOfAny(new char[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
                    Regex.IsMatch(segments[i], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase) ||
                    string.Equals(segments[i], "..", StringComparison.Ordinal) ||
                    string.Equals(segments[i], ".", StringComparison.Ordinal))
                {
                    error = "Zip-Slip detektovan (relativni segment '..' ili '.'): " + entryName;
                    return false;
                }
            }

            // Ako je na Windows-u, dodatna provera preko Path.GetFullPath
            if (Path.DirectorySeparatorChar == '\\')
            {
                string targetPath = Path.GetFullPath(Path.Combine(fullStagingRootWithSlash, normalized.Replace('/', '\\')));
                if (!targetPath.StartsWith(fullStagingRootWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Zip-Slip detektovan (izlazak van staging direktorijuma): " + entryName;
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsReparsePointsRecursive(string rootDir, out string offendingPath)
        {
            offendingPath = null;
            try
            {
                DirectoryInfo di = new DirectoryInfo(rootDir);
                if ((di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    offendingPath = di.FullName;
                    return true;
                }

                foreach (DirectoryInfo sub in di.GetDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        offendingPath = sub.FullName;
                        return true;
                    }
                    if (ContainsReparsePointsRecursive(sub.FullName, out offendingPath)) return true;
                }

                foreach (FileInfo fi in di.GetFiles())
                {
                    if ((fi.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        offendingPath = fi.FullName;
                        return true;
                    }
                }
            }
            catch { offendingPath = rootDir; return true; }
            return false;
        }

        #endregion

        #region Transakcioni Update State, Staging, Promocija i Rollback

        private static void WriteTransactionState(string state, string version, string sha256, string detail)
        {
            try
            {
                if (!Directory.Exists(SetupDir))
                {
                    Directory.CreateDirectory(SetupDir);
                }

                string cleanDetail = (detail ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                string txJson = string.Format(
                    "{{\r\n  \"state\": \"{0}\",\r\n  \"version\": \"{1}\",\r\n  \"sha256\": \"{2}\",\r\n  \"updated_at\": \"{3}\",\r\n  \"detail\": \"{4}\"\r\n}}\r\n",
                    state ?? "unknown",
                    version ?? string.Empty,
                    sha256 ?? string.Empty,
                    DateTime.UtcNow.ToString("o"),
                    cleanDetail);

                AtomicWriteText(UpdateTxPath, txJson);
            }
            catch (Exception ex) { throw new IOException("Journal transakcije nije upisan.", ex); }
        }

        public static string ReadTransactionState()
        {
            try
            {
                if (!File.Exists(UpdateTxPath)) return null;
                string json = File.ReadAllText(UpdateTxPath, Encoding.UTF8);
                Match m = Regex.Match(json, @"""state""\s*:\s*""([^""]+)""");
                if (m.Success) return m.Groups[1].Value.Trim();
                return "corrupted";
            }
            catch
            {
                return "corrupted";
            }
        }

        /// <summary>
        /// Poziva se pri samom startu Launchera: ako C:\setup\.update_tx.json postoji a nije "committed",
        /// vraća kompletan prethodni paket iz C:\setup\.backup_prev, čisti nekompletni update i obaveštava UI.
        /// </summary>
        public static bool RecoverInterruptedUpdateIfNeeded(out bool rolledBack, out string recoveryMessage)
        {
            rolledBack = false;
            recoveryMessage = null;
            Mutex mutex;
            if (!TryAcquireUpdateMutex(15000, out mutex))
            {
                recoveryMessage = "Oporavak odložen: drugi Launcher koristi paket.";
                LastError = recoveryMessage;
                return false;
            }
            try
            {
                if (!Directory.Exists(SetupDir)) return true;
                string state = ReadTransactionState();
                if (state == null || state == "committed")
                {
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return true;
                }
                // Pre promocije aktivni paket nije izmenjen. Nikad ne vraćaj stariji backup.
                if (state == "staging_verified" || state == "backup_created")
                {
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    SafeDeleteFile(UpdateTxPath);
                    recoveryMessage = "Prekinuta priprema ažuriranja; postojeći paket je sačuvan.";
                    return true;
                }
                if (!RestoreFromBackup())
                {
                    if (!Directory.Exists(BackupPrevDir))
                    {
                        SafeDeleteDirectory(StagingExtractDir);
                        SafeDeleteDirectory(UpdateAttemptDir);
                        SafeDeleteFile(UpdateTxPath);
                        recoveryMessage = "Prekinuto inicijalno ažuriranje bez prethodnog backup-a; spreman za preuzimanje sa servera.";
                        return true;
                    }
                    recoveryMessage = "Oporavak nije potvrđen. Backup i journal su sačuvani; lokalno pokretanje je blokirano.";
                    LastError = recoveryMessage;
                    return false;
                }
                rolledBack = true;
                recoveryMessage = "Potvrđena prethodna verzija je vraćena posle prekinutog ažuriranja.";
                SafeDeleteDirectory(StagingExtractDir);
                SafeDeleteDirectory(UpdateAttemptDir);
                SafeDeleteFile(UpdateTxPath);
                return true;
            }
            catch (Exception ex)
            {
                recoveryMessage = "Oporavak nije uspeo: " + ex.Message;
                LastError = recoveryMessage;
                return false;
            }
            finally { ReleaseUpdateMutex(mutex); }
        }

        private static bool IsReservedUpdateItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return string.Equals(name, ".update_attempt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".staging_extract", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".backup_prev", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".update_tx.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "gwadt-payload.zip", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "wis-payload.zip", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CreateBackupOfCurrentSetup()
        {
            string temporary = BackupPrevDir + ".new";
            string retired = BackupPrevDir + ".old";
            try
            {
                // Sačuvaj samo kriptografski potvrđenu verziju, bez logova i tajnih runtime fajlova.
                PayloadVersionInfo manifest;
                string error;
                if (!TryVerifyInstalledPayload(SetupDir, out manifest, out error))
                {
                    // Prva instalacija nema prethodnu verziju; ne koristi zaostali backup.
                    PayloadVersionInfo previousBackup;
                    string backupError;
                    if (TryVerifyInstalledPayload(BackupPrevDir, out previousBackup, out backupError))
                        return true; // Zadrži poslednju potvrđenu verziju umesto oštećenog aktivnog paketa.
                    // Bez potvrđene prethodne verzije recovery će ostati blokiran ako promocija ne uspe.
                    SafeDeleteDirectory(BackupPrevDir);
                    return true;
                }
                SafeDeleteDirectory(temporary);
                Directory.CreateDirectory(temporary);
                foreach (KeyValuePair<string, string> item in manifest.RequiredFiles)
                {
                    string relative = item.Key.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(temporary, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(SetupDir, relative), target, true);
                }
                File.Copy(MetaPath, Path.Combine(temporary, "payload_meta.json"), true);
                PayloadVersionInfo checkedBackup;
                if (!TryVerifyInstalledPayload(temporary, out checkedBackup, out error))
                    throw new IOException("Backup nije potvrđen: " + error);
                SafeDeleteDirectory(retired);
                if (Directory.Exists(BackupPrevDir)) Directory.Move(BackupPrevDir, retired);
                Directory.Move(temporary, BackupPrevDir);
                SafeDeleteDirectory(retired);
                return true;
            }
            catch (Exception ex)
            {
                // Ako je prekid nastao između dva rename-a, prethodni backup ostaje dostupan.
                if (!Directory.Exists(BackupPrevDir) && Directory.Exists(retired))
                    Directory.Move(retired, BackupPrevDir);
                LastError = "Neuspešno kreiranje potvrđenog backup-a: " + ex.Message;
                return false;
            }
        }

        public static bool RestoreFromBackup()
        {
            try
            {
                PayloadVersionInfo manifest;
                string error;
                if (!TryVerifyInstalledPayload(BackupPrevDir, out manifest, out error))
                {
                    LastError = "Rollback odbijen: backup nije potvrđen. " + error;
                    return false;
                }
                KillActiveUiProcesses();
                // Ne briši aktivni paket pre kopiranja; backup ostaje netaknut i kod neuspeha.
                foreach (KeyValuePair<string, string> item in manifest.RequiredFiles)
                {
                    string relative = item.Key.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(SetupDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    CopyFileIfDifferent(Path.Combine(BackupPrevDir, relative), target);
                }
                PayloadVersionInfo failedManifest;
                string failedError;
                string failedMetadata = File.Exists(PendingMetaPath) ? PendingMetaPath : MetaPath;
                if (File.Exists(failedMetadata) && ParseAndVerifyManifest(File.ReadAllText(failedMetadata), out failedManifest, out failedError))
                    RemoveSupersededFiles(failedManifest, manifest, SetupDir);
                if (!VerifyDirectoryMatchesManifest(SetupDir, manifest, out error))
                    throw new IOException(error);
                AtomicWriteText(MetaPath, manifest.RawJson);
                UnblockAllSetupFiles(SetupDir);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Rollback nije potvrđen: " + ex.Message;
                return false;
            }
        }

        public static bool TryVerifyInstalledPayload(string directory, out PayloadVersionInfo manifest, out string error)
        {
            manifest = null;
            error = null;
            try
            {
                string metadata = Path.Combine(directory, "payload_meta.json");
                if (!File.Exists(metadata)) { error = "Nedostaje potpisani lokalni manifest."; return false; }
                if (!ParseAndVerifyManifest(File.ReadAllText(metadata, Encoding.UTF8), out manifest, out error)) return false;
                return VerifyDirectoryMatchesManifest(directory, manifest, out error);
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool TryStartVerifiedLocalUi(out string error)
        {
            error = null;
            Mutex mutex;
            if (!TryAcquireUpdateMutex(15000, out mutex))
            { error = "Paket koristi drugi Launcher proces."; return false; }
            try
            {
                string state = ReadTransactionState();
                if (state != null && state != "committed")
                { error = "Postoji nepotvrđena transakcija ažuriranja; prvo je potreban oporavak."; return false; }
                PayloadVersionInfo manifest;
                if (!TryVerifyInstalledPayload(SetupDir, out manifest, out error)) return false;
                UnblockFileZone(TargetUiExe);
                Process.Start(new ProcessStartInfo {
                    FileName = TargetUiExe, Arguments = "--master", UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(TargetUiExe)
                });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { ReleaseUpdateMutex(mutex); }
        }

        public static bool SelfUpdateDelegated { get; private set; }

        public static bool NeedsSelfUpdate(string runningExecutable, string stagingDirectory)
        {
            string root = Path.GetFullPath(SetupDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string running = Path.GetFullPath(runningExecutable);
            if (!running.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string staged = Path.Combine(stagingDirectory, running.Substring(root.Length));
            return File.Exists(staged) && CalculateSha256(staged) != CalculateSha256(running);
        }

        private static bool DelegateSelfUpdateIfNeeded()
        {
            string original = Assembly.GetExecutingAssembly().Location;
            if (!NeedsSelfUpdate(original, StagingExtractDir)) return false;
            // Helper radi van C:\setup i čeka da Windows otpusti originalni EXE.
            string helperDirectory = Path.Combine(Path.GetTempPath(), "WIS-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperDirectory);
            string helper = Path.Combine(helperDirectory, "WIS.UpdateHelper.exe");
            File.Copy(original, helper);
            Process process = Process.Start(new ProcessStartInfo {
                FileName = helper,
                Arguments = "--apply-pending-update " + Process.GetCurrentProcess().Id + " \"" + original + "\"",
                UseShellExecute = false, WorkingDirectory = helperDirectory
            });
            if (process == null) throw new IOException("Update helper nije pokrenut.");
            process.Dispose();
            SelfUpdateDelegated = true;
            LastError = "Ažuriranje Launchera nastavlja se nakon zatvaranja aktivnog procesa.";
            return true;
        }

        public static bool ApplyPendingUpdateAfterExit(int parentPid, string original, out string error)
        {
            error = null;
            try
            {
                string root = Path.GetFullPath(SetupDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                original = Path.GetFullPath(original);
                if (!original.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(original), "WIS.Launcher.exe", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Nedozvoljena putanja originalnog Launchera.");
                try
                {
                    using (Process parent = Process.GetProcessById(parentPid))
                    {
                        if (!parent.WaitForExit(60000)) throw new IOException("Aktivni Launcher nije zatvoren; paket nije izmenjen.");
                    }
                }
                catch (ArgumentException) { } // Proces je već završen.
                if (!ExtractPayload(null)) { error = LastError; return false; }
                Process.Start(new ProcessStartInfo { FileName = original, UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(original) });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally
            {
                // Windows ne dozvoljava brisanje aktivnog helper EXE-a: uklanjanje pri narednom reboot-u.
                try
                {
                    string helper = Assembly.GetExecutingAssembly().Location;
                    string directory = Path.GetDirectoryName(helper);
                    if (Path.GetFileName(helper) == "WIS.UpdateHelper.exe" &&
                        Path.GetFileName(directory).StartsWith("WIS-update-", StringComparison.Ordinal) &&
                        string.Equals(Path.GetDirectoryName(directory), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        MoveFileEx(helper, null, 4);
                        MoveFileEx(directory, null, 4);
                    }
                }
                catch { }
            }
        }

        private static void RemoveSupersededFiles(PayloadVersionInfo previous, PayloadVersionInfo next, string directory)
        {
            foreach (string relative in previous.RequiredFiles.Keys)
            {
                if (next.RequiredFiles.ContainsKey(relative)) continue;
                string file = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
        }

        private static void RemoveUnlistedPayloadExecutables(PayloadVersionInfo manifest, string directory)
        {
            if (manifest == null || manifest.RequiredFiles == null || !Directory.Exists(directory)) return;
            string rootPrefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            List<string> toRemove = new List<string>();
            foreach (string file in EnumeratePayloadFiles(directory, true))
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".exe" && extension != ".dll" && extension != ".ps1" && extension != ".psm1" &&
                    extension != ".cmd" && extension != ".bat" && extension != ".vbs" && extension != ".js" && extension != ".py") continue;
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = full.Substring(rootPrefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                if (!manifest.RequiredFiles.ContainsKey(relative))
                {
                    toRemove.Add(full);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                if (File.Exists(toRemove[i]))
                {
                    File.SetAttributes(toRemove[i], FileAttributes.Normal);
                    File.Delete(toRemove[i]);
                }
            }
        }

        private static void CopyFileIfDifferent(string source, string target)
        {
            if (Directory.Exists(target)) throw new IOException("Direktorijum blokira ciljnu datoteku: " + target);
            if (File.Exists(target) && CalculateSha256(source) == CalculateSha256(target)) return;
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(source, target, true);
        }

        private static void AtomicWriteText(string path, string contents)
        {
            string temporary = path + ".new";
            using (FileStream file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                if (File.Exists(destFile))
                {
                    try { File.SetAttributes(destFile, FileAttributes.Normal); } catch { }
                }
                CopyFileIfDifferent(file, destFile);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }

        /// <summary>
        /// Transakciona ekstrakcija, verifikacija i promocija:
        /// 1. Proverava RSA-2048 potpis manifesta, veličinu i SHA-256 preuzetog payload.zip.partial.
        /// 2. Vrši Zip-Slip proveru svih ulaza u ZIP arhivi.
        /// 3. Raspakuje isključivo u izolovani C:\setup\.staging_extract\.
        /// 4. Proverava prisustvo i SHA-256 SVIH obaveznih komponenti u .staging_extract.
        /// 5. Ako ekstrakcija ili verifikacija ne uspe, briše .staging_extract i vraća false (bez obzira na stari TargetUiExe!).
        /// 6. Proverava da aktivni UI nije zaključan, pravi backup u .backup_prev, promoviše u C:\setup,
        ///    verifikuje promovisane fajlove u C:\setup i tek onda upisuje C:\setup\payload_meta.json i state="committed".
        /// </summary>
        public static bool ExtractPayload(Action<string, int, int> progressCallback)
        {
            SelfUpdateDelegated = false;
            LastError = string.Empty;

            Mutex mutex = null;
            if (!TryAcquireUpdateMutex(15000, out mutex))
            {
                LastError = "Drugi WIS Launcher proces već izvršava ažuriranje (Mutex zauzet).";
                return false;
            }

            try
            {
                string zipSource = File.Exists(PartialZipPath) ? PartialZipPath : (File.Exists(ZipPath) ? ZipPath : null);
                if (string.IsNullOrEmpty(zipSource) || !File.Exists(zipSource))
                {
                    LastError = "Preuzeti ZIP paket ne postoji za ekstrakciju.";
                    return false;
                }

                if (!File.Exists(PendingMetaPath))
                {
                    LastError = "Nedostaje verifikovani manifest pokušaja (payload_meta.pending.json).";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                // 1. Ponovna verifikacija potpisa manifesta, veličine i SHA-256 heša ZIP-a pre ekstrakcije
                string pendingMetaJson = File.ReadAllText(PendingMetaPath, Encoding.UTF8);
                PayloadVersionInfo manifestInfo;
                string verifyErr;
                if (!ParseAndVerifyManifest(pendingMetaJson, out manifestInfo, out verifyErr) || manifestInfo == null || !manifestInfo.IsSignatureValid)
                {
                    LastError = "Ekstrakcija odbijena — nevalidan potpis manifesta: " + verifyErr;
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                FileInfo zipFi = new FileInfo(zipSource);
                if (!zipFi.Exists || zipFi.Length != manifestInfo.SizeBytes)
                {
                    LastError = "Ekstrakcija odbijena: veličina ZIP fajla ne odgovara potpisanom manifestu.";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                string zipSha = CalculateSha256(zipSource);
                if (string.IsNullOrEmpty(zipSha) || !string.Equals(zipSha, manifestInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "Ekstrakcija odbijena: SHA-256 ZIP paketa ne odgovara potpisanom manifestu.";
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                if (progressCallback != null) progressCallback("Provera bezbednosti ZIP ulaza (Zip-Slip zaštita)...", 1, 10);

                // 2. Binarni pregled svih ZIP ulaza protiv Zip-Slip ranjivosti pre ekstrakcije
                List<string> zipEntries;
                string zipSlipErr;
                if (!ValidateZipEntriesAgainstZipSlip(zipSource, StagingExtractDir, out zipEntries, out zipSlipErr))
                {
                    LastError = "Bezbednosna blokada ekstrakcije: " + zipSlipErr;
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                foreach (string entry in zipEntries)
                {
                    if (entry.EndsWith("/") || entry.EndsWith("\\")) continue;
                    if (!manifestInfo.RequiredFiles.ContainsKey(entry.Replace('\\', '/')))
                    {
                        LastError = "ZIP sadrži nepotpisanu komponentu: " + entry;
                        return false;
                    }
                }

                // 3. Priprema čistog .staging_extract direktorijuma
                SafeDeleteDirectory(StagingExtractDir);
                Directory.CreateDirectory(StagingExtractDir);

                if (progressCallback != null) progressCallback("Raspakivanje u izolovani staging direktorijum...", 3, 10);

                string stagedTargetUiExe = Path.Combine(StagingExtractDir, "Skripte", "WIS.UI", "WIS.UI.exe");
                bool extracted = false;

                if (ExtractWithTar(zipSource, StagingExtractDir) && File.Exists(stagedTargetUiExe))
                {
                    extracted = true;
                }
                else
                {
                    SafeDeleteDirectory(StagingExtractDir);
                    Directory.CreateDirectory(StagingExtractDir);

                    if (ExtractWithShellCom(zipSource, StagingExtractDir, stagedTargetUiExe) && File.Exists(stagedTargetUiExe))
                    {
                        extracted = true;
                    }
                    else
                    {
                        SafeDeleteDirectory(StagingExtractDir);
                        Directory.CreateDirectory(StagingExtractDir);

                        if (ExtractWithPowerShell(zipSource, StagingExtractDir) && File.Exists(stagedTargetUiExe))
                        {
                            extracted = true;
                        }
                    }
                }

                // UKLONJEN lažni uslov File.Exists(TargetUiExe): ako ekstrakcija u staging nije uspela, odmah prijavi grešku!
                if (!extracted)
                {
                    LastError = "Raspakivanje ZIP paketa u .staging_extract nije uspelo.";
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                // 4. Provera da u staging-u nema ReparsePoint (symlink/junction) ulaza
                string reparsePath;
                if (ContainsReparsePointsRecursive(StagingExtractDir, out reparsePath))
                {
                    LastError = "Bezbednosna blokada: detektovan ReparsePoint/symlink u staging direktorijumu (" + reparsePath + ").";
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                if (progressCallback != null) progressCallback("Verifikacija SHA-256 heševa svih komponenti u staging-u...", 6, 10);

                // 5. Verifikacija svih obaveznih fajlova iz manifesta unutar .staging_extract
                string stageVerifyErr;
                if (!VerifyDirectoryMatchesManifest(StagingExtractDir, manifestInfo, out stageVerifyErr))
                {
                    LastError = "Verifikacija raspakovanih komponenti u .staging_extract nije uspela: " + stageVerifyErr;
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    return false;
                }

                if (DelegateSelfUpdateIfNeeded()) return false;

                // Zapis stanja transakcije: staging_verified
                WriteTransactionState("staging_verified", manifestInfo.Version, manifestInfo.Sha256, "Staging verifikovan sa svim SHA-256 komponentama.");

                // 6. Zaustavljanje aktivnog UI procesa u C:\setup i provera da li su izvršni fajlovi zaključani za pisanje
                KillActiveUiProcesses();
                string lockedFile;
                if (AreExistingSetupBinariesLocked(out lockedFile))
                {
                    LastError = "Promocija odložena: izvršni fajl je zaključan za pisanje (" + lockedFile + "). Postojeći paket nije izmenjen.";
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    SafeDeleteFile(UpdateTxPath);
                    return false;
                }

                if (progressCallback != null) progressCallback("Kreiranje backup-a prethodne verzije (.backup_prev)...", 7, 10);

                // 7. Kreiranje backup-a trenutne ispravne verzije u C:\setup\.backup_prev\
                if (!CreateBackupOfCurrentSetup())
                {
                    SafeDeleteDirectory(StagingExtractDir);
                    SafeDeleteDirectory(UpdateAttemptDir);
                    SafeDeleteFile(UpdateTxPath);
                    return false;
                }

                WriteTransactionState("backup_created", manifestInfo.Version, manifestInfo.Sha256, "Prethodna verzija sačuvana u .backup_prev.");

                if (progressCallback != null) progressCallback("Transakciona promocija fajlova u C:\\setup...", 8, 10);

                // 8. Promocija iz .staging_extract u C:\setup
                WriteTransactionState("promoting", manifestInfo.Version, manifestInfo.Sha256, "Kopiranje verifikovanih fajlova iz .staging_extract u C:\\setup.");

                try
                {
                    CopyDirectory(StagingExtractDir, SetupDir);
                    PayloadVersionInfo previous;
                    string previousError;
                    if (TryVerifyInstalledPayload(BackupPrevDir, out previous, out previousError))
                        RemoveSupersededFiles(previous, manifestInfo, SetupDir);
                    RemoveUnlistedPayloadExecutables(manifestInfo, SetupDir);
                }
                catch (Exception copyEx)
                {
                    LastError = "Greška tokom promocije fajlova u C:\\setup (izvršen rollback): " + copyEx.Message;
                    bool restored = RestoreFromBackup();
                    if (restored)
                    {
                        SafeDeleteDirectory(StagingExtractDir);
                        SafeDeleteDirectory(UpdateAttemptDir);
                        SafeDeleteFile(UpdateTxPath);
                    }
                    return false;
                }

                if (progressCallback != null) progressCallback("Završna verifikacija promovisanog paketa u C:\\setup...", 9, 10);

                // 9. Post-promociona verifikacija u C:\setup
                string postVerifyErr;
                if (!VerifyDirectoryMatchesManifest(SetupDir, manifestInfo, out postVerifyErr))
                {
                    LastError = "Verifikacija promovisanih fajlova u C:\\setup nije uspela (izvršen rollback): " + postVerifyErr;
                    bool restored = RestoreFromBackup();
                    if (restored)
                    {
                        SafeDeleteDirectory(StagingExtractDir);
                        SafeDeleteDirectory(UpdateAttemptDir);
                        SafeDeleteFile(UpdateTxPath);
                    }
                    return false;
                }

                // 10. Tek kada je sve 100% potvrđeno na konačnoj lokaciji, upisujemo MetaPath i state = "committed"
                AtomicWriteText(MetaPath, pendingMetaJson);
                WriteTransactionState("committed", manifestInfo.Version, manifestInfo.Sha256, "Transakcija uspešno potvrđena.");

                UnblockAllSetupFiles(SetupDir);
                SafeDeleteDirectory(StagingExtractDir);
                SafeDeleteDirectory(UpdateAttemptDir);
                TryCleanupZip();

                if (progressCallback != null) progressCallback("Paket uspešno verifikovan i promovisan.", 10, 10);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Neočekivana greška u transakcionoj ekstrakciji/promociji: " + ex.Message;
                try
                {
                    string txState = ReadTransactionState();
                    if (string.Equals(txState, "promoting", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(txState, "backup_created", StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreFromBackup();
                    }
                }
                catch { }
                return false;
            }
            finally
            {
                ReleaseUpdateMutex(mutex);
            }
        }

        #endregion

        private static bool ExtractWithTar(string zipPath, string destDir)
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string tarExe = Path.Combine(sysDir, "tar.exe");
                if (!File.Exists(tarExe)) tarExe = "tar.exe";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = tarExe,
                    Arguments = string.Format("-xf \"{0}\" -C \"{1}\"", zipPath, destDir),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        bool finished = p.WaitForExit(60000);
                        if (finished && p.ExitCode == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool ExtractWithShellCom(string zipPath, string destDir, string expectedUiExeInDest)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;

                object shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                object destFolder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { destDir });
                object srcFolder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { zipPath });
                if (destFolder == null || srcFolder == null) return false;

                object items = srcFolder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, srcFolder, null);
                if (items == null) return false;

                int flags = 4 | 16 | 512 | 1024;
                destFolder.GetType().InvokeMember("CopyHere", BindingFlags.InvokeMethod, null, destFolder, new object[] { items, flags });

                for (int i = 0; i < 30; i++)
                {
                    if (File.Exists(expectedUiExeInDest)) return true;
                    Thread.Sleep(500);
                }

                return File.Exists(expectedUiExeInDest);
            }
            catch
            {
                return false;
            }
        }

        private static bool ExtractWithPowerShell(string zipPath, string destDir)
        {
            try
            {
                string escapedZip = zipPath.Replace("'", "''");
                string escapedDest = destDir.Replace("'", "''");
                string cmd = string.Format("Expand-Archive -Path '{0}' -DestinationPath '{1}' -Force", escapedZip, escapedDest);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        bool finished = p.WaitForExit(90000);
                        if (finished && p.ExitCode == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void TryCleanupZip()
        {
            SafeDeleteFile(ZipPath);
            SafeDeleteFile(ZipPath + ".part");
            SafeDeleteFile(PartialZipPath);
            SafeDeleteFile(PendingMetaPath);
        }

        public static string CalculateSha256(string filePath)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
