using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WIS.UI
{
    public class HubRemoteCommand
    {
        public string CommandId { get; set; }
        public string Action { get; set; }
        public Dictionary<string, object> Params { get; set; }
        public string CreatedAt { get; set; }

        public HubRemoteCommand()
        {
            CommandId = "";
            Action = "";
            Params = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            CreatedAt = "";
        }

        public string GetParamString(string key, string fallback = "")
        {
            if (Params == null || string.IsNullOrEmpty(key)) return fallback;
            object val;
            if (Params.TryGetValue(key, out val) && val != null)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? fallback;
            }
            return fallback;
        }

        public bool TryGetParamInt(string key, out int result)
        {
            result = 0;
            if (Params == null || string.IsNullOrEmpty(key)) return false;
            object val;
            if (!Params.TryGetValue(key, out val) || val == null) return false;
            if (val is int) { result = (int)val; return true; }
            if (val is long)
            {
                long lv = (long)val;
                if (lv < int.MinValue || lv > int.MaxValue) return false;
                result = (int)lv;
                return true;
            }
            if (val is double)
            {
                double dv = (double)val;
                if (Math.Abs(dv - Math.Round(dv)) > 0.0001) return false;
                result = (int)Math.Round(dv);
                return true;
            }
            string s = Convert.ToString(val, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return false;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        public bool GetParamBool(string key, bool fallback = false)
        {
            if (Params == null || string.IsNullOrEmpty(key)) return fallback;
            object val;
            if (!Params.TryGetValue(key, out val) || val == null) return fallback;
            if (val is bool) return (bool)val;
            string s = (Convert.ToString(val, CultureInfo.InvariantCulture) ?? "").Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "0" || s == "no") return false;
            return fallback;
        }
    }

    public class HubCommandExecutionResult
    {
        public string CommandId { get; set; }
        public string Action { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public string AppliedAt { get; set; }
        public Action PostAckAction { get; set; }

        public HubCommandExecutionResult()
        {
            CommandId = "";
            Action = "";
            Status = "FAILED";
            Detail = "";
            AppliedAt = DateTime.UtcNow.ToString("o");
        }
    }

    public class VerifiedHotfixManifest
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

    /// <summary>
    /// Kriptografski verifikator za live hotfix pakete (IMP-102):
    /// koristi IDENTIČAN RSA-2048 javni ključ (TrustedPublicKeyXml) i Zip-Slip/SHA-256 pravila kao Launcher/PayloadManager.cs.
    /// Striktno kompatibilan sa C# 5.0 (.NET 4.8).
    /// </summary>
    public static class PayloadHotfixVerifier
    {
        // Ugrađeni RSA-2048 javni ključ (1:1 identičan Launcher/PayloadManager.cs Root of Trust)
        public const string TrustedPublicKeyXml = "<RSAKeyValue><Modulus>19koZSk56CQwEH1WnrjnxJF7zso3zGFnRvoAXG9ztlPqlQS0GOJmek3jMo2hs2exapyntvYk3IgeuUtnl+EDJFt7ENkY/bM7k6jFLhoXeDZZRomBD+/N9+IBmXkaqRHbeYBAlHvSA+n6ZsUsW9iVFTb4owboOcADS9HGBrVmY0fbS303YQKlcNi3ujYzYz94n476vZNlRDdhaa6ak0a7srwpt6cS7Dc0dZ1JaBFiJ6IMB1OVSm2sxfH20O5jfRXTm/HmXdVqyXFxNuXPdflD3DQs8U/Zr/LK8a6aZzWvXfIpqaQK+CZrlc0fhVSMebvkAkCi0jmB/EdoiJn74My+Ew==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        public const string MinAcceptedPayloadVersion = "3.1.0";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string name);

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

        public static string CalculateSha256(string filePath)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
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

        public static int CompareVersions(string v1Raw, string v2Raw)
        {
            if (string.IsNullOrEmpty(v1Raw) && string.IsNullOrEmpty(v2Raw)) return 0;
            if (string.IsNullOrEmpty(v1Raw)) return -1;
            if (string.IsNullOrEmpty(v2Raw)) return 1;

            string[] p1 = v1Raw.Trim().TrimStart('v', 'V').Split('.');
            string[] p2 = v2Raw.Trim().TrimStart('v', 'V').Split('.');
            int maxLen = Math.Max(p1.Length, p2.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int n1 = 0;
                int n2 = 0;
                if (i < p1.Length)
                {
                    Match m1 = Regex.Match(p1[i], @"^\d+");
                    if (m1.Success) int.TryParse(m1.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n1);
                }
                if (i < p2.Length)
                {
                    Match m2 = Regex.Match(p2[i], @"^\d+");
                    if (m2.Success) int.TryParse(m2.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n2);
                }
                if (n1 != n2) return n1.CompareTo(n2);
            }
            return 0;
        }

        private static bool IsReservedUpdateItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return string.Equals(name, ".update_attempt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".staging_extract", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".hotfix_attempt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".hotfix_staging", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".backup_prev", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, ".update_tx.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "gwadt-payload.zip", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "wis-payload.zip", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSafeRelativeZipEntry(string entryName, string fullStagingRootWithSlash, out string error)
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
            if (entryName.StartsWith("/") || entryName.StartsWith("\\") || entryName.IndexOf(':') >= 0)
            {
                error = "Zip-Slip detektovan (apsolutna putanja ili oznaka diska): " + entryName;
                return false;
            }
            string normalized = entryName.Replace('\\', '/');
            if (normalized.IndexOf("//", StringComparison.Ordinal) >= 0)
            {
                error = "Nedozvoljeni prazni segmenti putanje: " + entryName;
                return false;
            }
            string[] segments = normalized.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || IsReservedUpdateItem(segments[0]) ||
                segments[0].Equals("payload_meta.json", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals(".backup_prev.new", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals(".backup_prev.old", StringComparison.OrdinalIgnoreCase))
            {
                error = "Nedozvoljena rezervisana update putanja: " + entryName;
                return false;
            }
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].EndsWith(".") || segments[i].EndsWith(" ") ||
                    segments[i].IndexOfAny(new char[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
                    Regex.IsMatch(segments[i], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase) ||
                    string.Equals(segments[i], "..", StringComparison.Ordinal) ||
                    string.Equals(segments[i], ".", StringComparison.Ordinal))
                {
                    error = "Zip-Slip detektovan (nedozvoljeni segment putanje): " + entryName;
                    return false;
                }
            }
            if (!string.IsNullOrEmpty(fullStagingRootWithSlash))
            {
                string rootWithSlash = Path.GetFullPath(fullStagingRootWithSlash)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string combined = Path.GetFullPath(Path.Combine(rootWithSlash, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!combined.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Zip-Slip detektovan (izlazak van staging direktorijuma): " + entryName;
                    return false;
                }
            }
            return true;
        }

        public static bool IsScriptsOnlyEligibleRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string norm = relativePath.Replace('\\', '/').Trim();
            string pathErr;
            if (!IsSafeRelativeZipEntry(norm, null, out pathErr)) return false;

            string[] segs = norm.Split('/');
            if (segs.Length != 3) return false;
            if (!string.Equals(segs[0], "Skripte", StringComparison.OrdinalIgnoreCase)) return false;

            if (string.Equals(segs[1], "Modules", StringComparison.OrdinalIgnoreCase) &&
                segs[2].EndsWith(".psm1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(segs[1], "Phases", StringComparison.OrdinalIgnoreCase) &&
                segs[2].EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public static bool ParseAndVerifyManifest(string json, out VerifiedHotfixManifest info, out string error)
        {
            return ParseAndVerifyManifest(json, out info, out error, null);
        }

        public static bool ParseAndVerifyManifest(string json, out VerifiedHotfixManifest info, out string error, string publicKeyXmlOverride)
        {
            info = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Manifest je prazan.";
                return false;
            }

            try
            {
                JsonNode root = JsonHelper.Parse(json);
                if (root == null || !root.IsObject)
                {
                    error = "Manifest nije validan JSON objekat.";
                    return false;
                }

                VerifiedHotfixManifest parsed = new VerifiedHotfixManifest
                {
                    RawJson = json,
                    RequiredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    IsSignatureValid = false
                };

                parsed.Product = root["product"].AsString("");
                parsed.SchemaVersion = root["schema_version"].AsInt(0);
                parsed.Version = root["version"].AsString("");
                parsed.ReleaseId = root["release_id"].AsString("");
                parsed.MinLauncherVersion = root["min_launcher_version"].AsString("");
                parsed.Sha256 = root["sha256"].AsString("");
                parsed.SizeBytes = root.ContainsKey("size") ? root["size"].AsLong(0L) : root["size_bytes"].AsLong(0L);
                parsed.BuiltAt = root["built_at"].AsString("");
                parsed.SignedPayload = root["signed_payload"].AsString("");
                parsed.Signature = root["signature"].AsString("");

                JsonNode reqNode = root["required_files"];
                if (reqNode == null || !reqNode.IsObject || reqNode.Count == 0)
                {
                    error = "Nedostaje required_files objekat u manifestu.";
                    return false;
                }

                foreach (KeyValuePair<string, JsonNode> kv in reqNode.Properties)
                {
                    string rel = (kv.Key ?? "").Replace('\\', '/');
                    string hash = kv.Value != null ? kv.Value.AsString("") : "";
                    string pathErr;
                    if (!Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z") ||
                        !IsSafeRelativeZipEntry(rel, null, out pathErr) ||
                        parsed.RequiredFiles.ContainsKey(rel))
                    {
                        error = "Neispravna ili duplirana stavka u required_files: " + kv.Key;
                        return false;
                    }
                    string lowRel = rel.ToLowerInvariant();
                    if (lowRel.Contains("librehardwaremonitorlib.dll") ||
                        lowRel.Contains("hidsharp.dll") ||
                        lowRel.Contains("winring0"))
                    {
                        error = "Bezbednosna blokada: zabranjen WinRing0/LHM modul u paketu (" + rel + ").";
                        return false;
                    }
                    parsed.RequiredFiles.Add(rel, hash.ToLowerInvariant());
                }

                if (!string.Equals(parsed.Product, "WIS", StringComparison.Ordinal))
                {
                    error = "Manifest ne sadrži validan 'product': 'WIS'.";
                    return false;
                }
                if (parsed.SchemaVersion != 2)
                {
                    error = "Nepodržana verzija šeme manifesta (očekivano schema_version = 2).";
                    return false;
                }
                if (string.IsNullOrEmpty(parsed.Version) ||
                    string.IsNullOrEmpty(parsed.ReleaseId) ||
                    string.IsNullOrEmpty(parsed.MinLauncherVersion) ||
                    !Regex.IsMatch(parsed.Sha256, @"\A[a-fA-F0-9]{64}\z") ||
                    parsed.SizeBytes <= 0 ||
                    string.IsNullOrEmpty(parsed.SignedPayload) ||
                    string.IsNullOrEmpty(parsed.Signature))
                {
                    error = "Manifestu nedostaju obavezna kriptografska polja.";
                    return false;
                }
                if (CompareVersions(parsed.Version, MinAcceptedPayloadVersion) < 0)
                {
                    error = string.Format("Anti-downgrade zaštita: verzija ({0}) je starija od minimalne ({1}).",
                        parsed.Version, MinAcceptedPayloadVersion);
                    return false;
                }

                string reqCanonicalSha = ComputeRequiredFilesCanonicalSha256(parsed.RequiredFiles);
                string expectedSignedPayload = string.Format(
                    CultureInfo.InvariantCulture,
                    "WIS|v2|{0}|{1}|{2}|{3}|{4}|{5}",
                    parsed.Version,
                    parsed.ReleaseId,
                    parsed.SizeBytes,
                    parsed.Sha256.ToLowerInvariant(),
                    parsed.MinLauncherVersion,
                    reqCanonicalSha);

                if (!string.Equals(parsed.SignedPayload, expectedSignedPayload, StringComparison.Ordinal))
                {
                    error = "Polje signed_payload ne odgovara kanonskim poljima manifesta.";
                    return false;
                }

                if (!VerifyRsaSha256Signature(expectedSignedPayload, parsed.Signature, publicKeyXmlOverride))
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
                error = "Greška pri verifikaciji manifesta: " + ex.Message;
                return false;
            }
        }

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
                    {
                        error = "Višedelni i ZIP64 paketi nisu podržani.";
                        return false;
                    }

                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fs.Seek(cdOffset, SeekOrigin.Begin);
                    for (int idx = 0; idx < totalEntries; idx++)
                    {
                        byte[] header = br.ReadBytes(46);
                        if (header.Length != 46 || BitConverter.ToUInt32(header, 0) != 0x02014b50U)
                        {
                            error = "Neispravan centralni ZIP header.";
                            return false;
                        }
                        ushort flags = BitConverter.ToUInt16(header, 8);
                        uint compressedSize = BitConverter.ToUInt32(header, 20);
                        ushort nameLen = BitConverter.ToUInt16(header, 28);
                        ushort extraLen = BitConverter.ToUInt16(header, 30);
                        ushort commentLen = BitConverter.ToUInt16(header, 32);
                        uint externalAttributes = BitConverter.ToUInt32(header, 38);
                        uint localOffset = BitConverter.ToUInt32(header, 42);
                        uint unixType = (externalAttributes >> 16) & 0xF000;
                        if ((flags & 1) != 0 || (unixType != 0 && unixType != 0x8000 && unixType != 0x4000))
                        {
                            error = "Šifrovani, symlink i posebni ZIP ulazi nisu dozvoljeni.";
                            return false;
                        }

                        byte[] nameBytes = br.ReadBytes(nameLen);
                        if (nameBytes.Length != nameLen)
                        {
                            error = "Prekinuto ZIP ime.";
                            return false;
                        }
                        string entryName = new UTF8Encoding(false, true).GetString(nameBytes);
                        if (!IsSafeRelativeZipEntry(entryName, fullStagingRoot, out error))
                        {
                            return false;
                        }
                        if (!names.Add(entryName.Replace('\\', '/').TrimEnd('/')))
                        {
                            error = "Duplirana ZIP putanja: " + entryName;
                            return false;
                        }

                        long nextCentral = fs.Position + extraLen + commentLen;
                        if (nextCentral > (long)cdOffset + cdSize)
                        {
                            error = "Centralni ZIP zapis izlazi van granica.";
                            return false;
                        }

                        fs.Seek(localOffset, SeekOrigin.Begin);
                        byte[] local = br.ReadBytes(30);
                        if (local.Length != 30 || BitConverter.ToUInt32(local, 0) != 0x04034b50U ||
                            BitConverter.ToUInt16(local, 6) != flags || BitConverter.ToUInt16(local, 26) != nameLen)
                        {
                            error = "Lokalni i centralni ZIP header se ne poklapaju.";
                            return false;
                        }
                        byte[] localName = br.ReadBytes(nameLen);
                        if (localName.Length != nameLen || new UTF8Encoding(false, true).GetString(localName) != entryName)
                        {
                            error = "Lokalna i centralna ZIP putanja se ne poklapaju.";
                            return false;
                        }
                        if (fs.Position + BitConverter.ToUInt16(local, 28) + compressedSize > cdOffset)
                        {
                            error = "ZIP podaci izlaze van granica arhive.";
                            return false;
                        }

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

        public static bool ContainsReparsePointsRecursive(string rootDir, out string offendingPath)
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
            catch
            {
                offendingPath = rootDir;
                return true;
            }
            return false;
        }

        public static bool ExtractZipWithTar(string zipPath, string destDir)
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string tarExe = !string.IsNullOrEmpty(sysDir) ? Path.Combine(sysDir, "tar.exe") : "tar.exe";
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
                    if (p != null && p.WaitForExit(90000) && p.ExitCode == 0)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void AtomicReplaceFile(string sourcePath, string targetPath)
        {
            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string tmpPath = targetPath + ".hotfix.tmp";
            File.Copy(sourcePath, tmpPath, true);
            if (File.Exists(targetPath))
            {
                try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { }
                File.Replace(tmpPath, targetPath, null);
            }
            else
            {
                File.Move(tmpPath, targetPath);
            }
            UnblockFileZone(targetPath);
        }

        public static bool VerifyDirectoryMatchesManifest(string directory, VerifiedHotfixManifest manifest, out string error)
        {
            error = null;
            if (manifest == null || manifest.RequiredFiles == null || manifest.RequiredFiles.Count == 0)
            {
                error = "Nevalidan manifest za verifikaciju direktorijuma.";
                return false;
            }
            foreach (KeyValuePair<string, string> kv in manifest.RequiredFiles)
            {
                string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                string fullPath = Path.Combine(directory, rel);
                if (!File.Exists(fullPath))
                {
                    error = "Nedostaje obavezna komponenta u paketu: " + kv.Key;
                    return false;
                }
                string sha = CalculateSha256(fullPath);
                if (string.IsNullOrEmpty(sha) || !string.Equals(sha, kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    error = "SHA-256 neslaganje za komponentu: " + kv.Key;
                    return false;
                }
            }
            return true;
        }

        public static bool ApplyScriptsOnlyFromStaging(string stagingDir, string setupRootDir, VerifiedHotfixManifest manifest, out int updatedCount, out string error)
        {
            updatedCount = 0;
            error = null;

            if (manifest == null || !manifest.IsSignatureValid || manifest.RequiredFiles == null)
            {
                error = "Manifest nije kriptografski verifikovan.";
                return false;
            }

            List<KeyValuePair<string, string>> stagedToTarget = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in manifest.RequiredFiles)
            {
                if (!IsScriptsOnlyEligibleRelativePath(kv.Key)) continue;
                string relOs = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                string stagedFile = Path.Combine(stagingDir, relOs);
                if (!File.Exists(stagedFile))
                {
                    error = "Nedostaje potpisana skripta u staging-u: " + kv.Key;
                    return false;
                }
                string actualSha = CalculateSha256(stagedFile);
                if (string.IsNullOrEmpty(actualSha) || !string.Equals(actualSha, kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    error = "SHA-256 neslaganje za skriptu: " + kv.Key;
                    return false;
                }
                string destFile = Path.Combine(setupRootDir, relOs);
                stagedToTarget.Add(new KeyValuePair<string, string>(stagedFile, destFile));
            }

            if (stagedToTarget.Count == 0)
            {
                error = "Potpisani paket ne sadrži nijednu Skripte/Modules/*.psm1 niti Skripte/Phases/*.ps1 skriptu.";
                return false;
            }

            for (int i = 0; i < stagedToTarget.Count; i++)
            {
                AtomicReplaceFile(stagedToTarget[i].Key, stagedToTarget[i].Value);
                updatedCount++;
            }

            return true;
        }
    }

    public class TelemetryService : IDisposable
    {
        private readonly MainViewModel _viewModel;
        private readonly Dispatcher _dispatcher;
        private readonly object _stateLock = new object();
        private readonly object _logLock = new object();
        private readonly object _cmdLock = new object();

        private string _hubUrl;
        private string _hubVersion;
        private int _hubPort = 8080;
        private string _clientToken = null;
        private bool _isEnabled = true;
        private bool _isDisposed = false;
        private int _consecutiveFailures = 0;
        private bool _benchmarkFetched = false;
        private string _cachedInstallSessionId = null;
        private readonly string _initialMachineHostname = Environment.MachineName ?? "";
        private string _cachedPreviousHostname = null;
        private bool? _cachedEnclosureIsLaptop = null;
        private string _cachedImageType = null;
        private string _cachedWimVersion = null;

        private bool _executedCommandsLoaded = false;
        private readonly Dictionary<string, HubCommandExecutionResult> _executedCommands = new Dictionary<string, HubCommandExecutionResult>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _executedCommandOrder = new List<string>();
        private Func<HubRemoteCommand, HubCommandExecutionResult> _remoteCommandHandler = null;

        private const string InstallSessionIdPath = @"C:\setup\install-session-id.txt";
        private const string DefaultImageProfilePath = @"C:\Windows\Logs\WIS\image-profile.json";
        private const string DefaultExecutedHubCommandsPath = @"C:\setup\executed-hub-commands.json";
        private const string DefaultPendingHubCommandsPath = @"C:\setup\pending-hub-commands.json";
        private const string DefaultSkipMarkerPath = @"C:\setup\.skip";

        public string ExecutedCommandsFilePath { get; set; }
        public string PendingCommandsFilePath { get; set; }
        public string SkipMarkerFilePath { get; set; }
        public string ImageProfileFilePath { get; set; }
        public bool AsyncCommandDispatch { get; set; }

        private static readonly HashSet<string> UiAllowlistedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unlock_driver_gate",
            "retry_phase",
            "jump_to_phase",
            "re_resolve_drivers",
            "reboot_and_resume",
            "apply_payload_update"
        };

        private static readonly HashSet<string> PsAllowlistedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "skip_step",
            "cancel",
            "abort",
            "reboot_pxe"
        };

        private static readonly string[] LaptopModelKeywords = new string[]
        {
            "Laptop", "Surface", "ThinkPad", "Latitude", "EliteBook", "ProBook", "ZenBook",
            "VivoBook", "ProArt", "IdeaPad", "ThinkBook", "Yoga", "LifeBook",
            "Aspire", "TravelMate", "Swift", "Pavilion", "Envy", "Omen", "Victus", "OmniBook",
            "XPS", "Vostro", "Inspiron", "Prestige", "Modern", "Katana", "Stealth", "Creator"
        };

        public void SetRemoteCommandHandler(Func<HubRemoteCommand, HubCommandExecutionResult> handler)
        {
            lock (_cmdLock)
            {
                _remoteCommandHandler = handler;
            }
        }

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
            ExecutedCommandsFilePath = DefaultExecutedHubCommandsPath;
            PendingCommandsFilePath = DefaultPendingHubCommandsPath;
            SkipMarkerFilePath = DefaultSkipMarkerPath;
            ImageProfileFilePath = DefaultImageProfilePath;
            AsyncCommandDispatch = true;
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
                    string sessionId = GetOrCreateInstallSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        payload["install_session_id"] = sessionId;
                    }

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
                    string canonDevId = GetCanonicalDeviceId(serialSnapshot);
                    if (!string.IsNullOrEmpty(canonDevId) && !payload.ContainsKey("device_id"))
                    {
                        payload["device_id"] = canonDevId;
                    }
                    string prevHost = GetPreviousHostname();
                    if (!string.IsNullOrEmpty(prevHost) && !payload.ContainsKey("previous_hostname"))
                    {
                        payload["previous_hostname"] = prevHost;
                    }

                    string imgType, wimVer;
                    GetImageProfileInfo(out imgType, out wimVer);
                    if (!payload.ContainsKey("image_type") || payload["image_type"] == null)
                    {
                        payload["image_type"] = imgType;
                    }
                    if (!payload.ContainsKey("wim_version") || payload["wim_version"] == null)
                    {
                        payload["wim_version"] = wimVer;
                    }

                    if (string.Equals(eventType, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eventType, "FINAL_REPORT", StringComparison.OrdinalIgnoreCase))
                    {
                        EnrichLicenseTelemetryPayload(payload);
                        if (!payload.ContainsKey("autopilot") || payload["autopilot"] == null)
                        {
                            payload["autopilot"] = BuildAutopilotPayload();
                        }
                        ApplyHotfixMetadata(payload);
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
                Dictionary<string, string> hbHeaders = BuildClientAuthHeaders();
                string respBody = HttpPostWithResponse(url, jsonBody, 2500, out ok, hbHeaders);
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

        public static bool IsKnownLaptopModelName(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            for (int i = 0; i < LaptopModelKeywords.Length; i++)
            {
                if (model.IndexOf(LaptopModelKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private bool ResolveIsLaptop()
        {
            if (_viewModel != null && _viewModel.IsLaptop) return true;
            if (_cachedEnclosureIsLaptop.HasValue) return _cachedEnclosureIsLaptop.Value;

            bool detected = false;
            bool wmiSucceeded = false;
            bool wmiHadChassisData = false;
            try
            {
                EnumerationOptions opts = new EnumerationOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(1500),
                    ReturnImmediately = true,
                    Rewindable = false
                };
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\CIMV2"),
                    new SelectQuery("SELECT ChassisTypes FROM Win32_SystemEnclosure"),
                    opts))
                {
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject mo in results)
                        {
                            ushort[] types = mo["ChassisTypes"] as ushort[];
                            if (types == null || types.Length == 0) continue;
                            wmiHadChassisData = true;
                            for (int i = 0; i < types.Length; i++)
                            {
                                ushort ct = types[i];
                                if (ct == 8 || ct == 9 || ct == 10 || ct == 11 || ct == 12 || ct == 14 || ct == 30 || ct == 31 || ct == 32)
                                {
                                    detected = true;
                                    break;
                                }
                            }
                            if (detected) break;
                        }
                    }
                }
                wmiSucceeded = true;
            }
            catch
            {
                wmiSucceeded = false;
            }

            string model = "";
            if (_viewModel != null)
            {
                model = _viewModel.ComputerModel ?? "";
                if (IsPlaceholderHwValue(model) && _viewModel.Autopilot != null && !IsPlaceholderHwValue(_viewModel.Autopilot.HwModel))
                {
                    model = _viewModel.Autopilot.HwModel;
                }
            }

            if (!detected && !IsPlaceholderHwValue(model))
            {
                if (IsKnownLaptopModelName(model))
                {
                    detected = true;
                }
            }

            // IMP-108: Ne keširaj trajno false ako je WMI bacio timeout ili model još nije popunjen!
            bool modelPopulated = !IsPlaceholderHwValue(model);
            if (detected)
            {
                _cachedEnclosureIsLaptop = true;
            }
            else if (wmiSucceeded && (wmiHadChassisData || modelPopulated))
            {
                _cachedEnclosureIsLaptop = false;
            }

            return detected;
        }

        private string GetCanonicalDeviceId(string serialSnapshot)
        {
            if (!IsPlaceholderHwValue(serialSnapshot))
            {
                string cleanSn = SanitizeFleetIdSegment(serialSnapshot);
                if (!string.IsNullOrEmpty(cleanSn) && cleanSn != "UNKNOWN")
                {
                    string chassisTag = ResolveIsLaptop() ? "LAPTOP" : "DESKTOP";
                    return "TK-" + chassisTag + "-" + cleanSn;
                }
            }
            return "";
        }

        private string GetPreviousHostname()
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrEmpty(_cachedPreviousHostname))
                {
                    return _cachedPreviousHostname;
                }
            }

            string found = "";
            try
            {
                string statePath = @"C:\setup\deploy-state.json";
                if (File.Exists(statePath))
                {
                    string json = File.ReadAllText(statePath, Encoding.UTF8);
                    JsonNode root = JsonHelper.Parse(json);
                    if (root != null && root.IsObject)
                    {
                        string[] keys = new string[] { "previous_hostname", "initial_hostname" };
                        for (int i = 0; i < keys.Length; i++)
                        {
                            if (root.ContainsKey(keys[i]))
                            {
                                string cand = root[keys[i]].AsString("");
                                if (!IsPlaceholderHwValue(cand))
                                {
                                    found = cand.Trim();
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(found) && root.ContainsKey("computer_name"))
                        {
                            string cn = root["computer_name"].AsString("");
                            if (!IsPlaceholderHwValue(cn) && !cn.Trim().StartsWith("TK-", StringComparison.OrdinalIgnoreCase))
                            {
                                found = cn.Trim();
                            }
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(found) && !IsPlaceholderHwValue(_initialMachineHostname) &&
                !_initialMachineHostname.Trim().StartsWith("TK-", StringComparison.OrdinalIgnoreCase))
            {
                found = _initialMachineHostname.Trim();
            }

            if (!string.IsNullOrEmpty(found))
            {
                lock (_stateLock)
                {
                    _cachedPreviousHostname = found;
                }
            }
            return found;
        }

        public static void ReadImageProfile(string profilePath, out string imageType, out string wimVersion)
        {
            imageType = "lean";
            wimVersion = "unknown";
            if (string.IsNullOrWhiteSpace(profilePath)) return;

            try
            {
                if (!File.Exists(profilePath)) return;
                string raw = File.ReadAllText(profilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return;
                JsonNode root = JsonHelper.Parse(raw);
                if (root == null || !root.IsObject) return;

                if (root.ContainsKey("image_type"))
                {
                    string rawType = (root["image_type"].AsString("") ?? "").Trim().ToLowerInvariant();
                    if (rawType == "preinstalled")
                    {
                        imageType = "preinstalled";
                    }
                    else if (rawType == "lean")
                    {
                        imageType = "lean";
                    }
                }
                if (root.ContainsKey("wim_version"))
                {
                    string rawVer = (root["wim_version"].AsString("") ?? "").Trim();
                    if (!string.IsNullOrEmpty(rawVer))
                    {
                        wimVersion = rawVer;
                    }
                }
            }
            catch { }
        }

        private void GetImageProfileInfo(out string imageType, out string wimVersion)
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrEmpty(_cachedImageType) && !string.IsNullOrEmpty(_cachedWimVersion))
                {
                    imageType = _cachedImageType;
                    wimVersion = _cachedWimVersion;
                    return;
                }
            }

            string path = string.IsNullOrWhiteSpace(ImageProfileFilePath) ? DefaultImageProfilePath : ImageProfileFilePath;
            ReadImageProfile(path, out imageType, out wimVersion);

            lock (_stateLock)
            {
                _cachedImageType = imageType;
                _cachedWimVersion = wimVersion;
            }
        }

        private static string CleanAutopilotField(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string trimmed = raw.Trim();
            if (IsPlaceholderHwValue(trimmed)) return "";
            if (trimmed.StartsWith("Nema (", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Nema", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return trimmed;
        }

        public Dictionary<string, object> BuildAutopilotPayload()
        {
            string matrixCode = "PENDING";
            string statusLevel = "WARNING";
            bool isLocked = false;
            bool isRegistered = false;
            bool confirmedFree = false;
            string tenantDomain = "";
            string tenantId = "";
            string assignedDeviceName = "";
            string queryMode = "";
            bool hasLocalTraces = false;
            string source = "ui";

            if (_viewModel != null && _viewModel.Autopilot != null)
            {
                AutopilotViewModel ap = _viewModel.Autopilot;
                matrixCode = string.IsNullOrWhiteSpace(ap.MatrixCode) ? "PENDING" : ap.MatrixCode.Trim().ToUpperInvariant();
                statusLevel = string.IsNullOrWhiteSpace(ap.StatusLevel) ? "WARNING" : ap.StatusLevel.Trim().ToUpperInvariant();
                isLocked = ap.IsLocked;
                isRegistered = ap.IsRegistered;
                confirmedFree = ap.ConfirmedFree && !isLocked && !isRegistered;
                tenantDomain = CleanAutopilotField(!string.IsNullOrWhiteSpace(ap.ProfileTenantDomain) ? ap.ProfileTenantDomain : ap.TenantDomain);
                tenantId = CleanAutopilotField(!string.IsNullOrWhiteSpace(ap.ProfileTenantId) ? ap.ProfileTenantId : ap.TenantId);
                assignedDeviceName = CleanAutopilotField(ap.AssignedDeviceName);
                queryMode = (ap.QueryMode ?? "").Trim();
                hasLocalTraces = ap.HasLocalTraces;
                if (!string.IsNullOrWhiteSpace(ap.Source)) source = ap.Source.Trim();
            }

            // Dopuna / zaštita integriteta iz lokalnog fajla (npr. WinPE pre-format ili OOBE ZTD sa is_locked=true)
            string[] diskCandidates = new string[]
            {
                @"C:\setup\autopilot-status.json",
                @"C:\Windows\Logs\WIS\autopilot_report.json",
                @"C:\setup\autopilot_report.json"
            };
            for (int i = 0; i < diskCandidates.Length; i++)
            {
                try
                {
                    if (!File.Exists(diskCandidates[i])) continue;
                    string raw = File.ReadAllText(diskCandidates[i], Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    JsonNode root = JsonHelper.Parse(raw);
                    if (root == null || !root.IsObject) continue;

                    string fileMatrix = root["matrix_code"].AsString(root["status"].AsString("")).Trim().ToUpperInvariant();
                    JsonNode cz = root["cloud_ztd"];
                    JsonNode lf = root["local_forensics"];
                    bool fileReg = (cz.IsObject && cz["is_registered"].AsBool(false)) || root["is_registered"].AsBool(false);
                    bool fileLocked = root["is_locked"].AsBool(false) || fileReg || fileMatrix == "LOCKED_TENANT" || fileMatrix == "HIDDEN_LOCK";
                    bool fileConfirmedFree = (root["confirmed_free"].AsBool(false) || (cz.IsObject && cz["confirmed_free"].AsBool(false))) && !fileLocked && !fileReg;

                    bool shouldAdopt = fileLocked && !isLocked;
                    if (!shouldAdopt && (matrixCode == "PENDING" || matrixCode == "SCANNING" || matrixCode == "UNKNOWN") && !string.IsNullOrEmpty(fileMatrix))
                    {
                        shouldAdopt = true;
                    }
                    else if (!shouldAdopt && !isLocked && !confirmedFree && fileConfirmedFree)
                    {
                        shouldAdopt = true;
                    }

                    if (shouldAdopt)
                    {
                        if (!string.IsNullOrEmpty(fileMatrix)) matrixCode = fileMatrix;
                        isLocked = fileLocked;
                        isRegistered = fileReg || fileLocked;
                        confirmedFree = fileConfirmedFree;
                        if (lf.IsObject)
                        {
                            if (lf["has_traces"].AsBool(false)) hasLocalTraces = true;
                            if (string.IsNullOrEmpty(tenantDomain)) tenantDomain = CleanAutopilotField(lf["tenant_domain"].AsString(""));
                            if (string.IsNullOrEmpty(tenantId)) tenantId = CleanAutopilotField(lf["tenant_id"].AsString(""));
                            if (string.IsNullOrEmpty(assignedDeviceName)) assignedDeviceName = CleanAutopilotField(lf["assigned_device_name"].AsString(""));
                        }
                        if (cz.IsObject)
                        {
                            if (string.IsNullOrEmpty(tenantDomain)) tenantDomain = CleanAutopilotField(cz["profile_tenant_domain"].AsString(""));
                            if (string.IsNullOrEmpty(tenantId)) tenantId = CleanAutopilotField(cz["profile_tenant_id"].AsString(""));
                            if (string.IsNullOrEmpty(queryMode)) queryMode = (cz["query_mode"].AsString("") ?? "").Trim();
                        }
                        if (string.IsNullOrEmpty(tenantDomain)) tenantDomain = CleanAutopilotField(root["tenant_domain"].AsString(root["tenant_name"].AsString("")));
                        if (string.IsNullOrEmpty(tenantId)) tenantId = CleanAutopilotField(root["tenant_id"].AsString(""));
                        if (string.IsNullOrEmpty(assignedDeviceName)) assignedDeviceName = CleanAutopilotField(root["assigned_device_name"].AsString(""));
                        if (string.IsNullOrEmpty(queryMode)) queryMode = (root["query_mode"].AsString("") ?? "").Trim();
                        if (root.ContainsKey("has_local_traces") && root["has_local_traces"].AsBool(false)) hasLocalTraces = true;
                        string fileSrc = (root["source"].AsString("") ?? "").Trim();
                        if (!string.IsNullOrEmpty(fileSrc)) source = fileSrc;
                    }
                }
                catch { }
            }

            if (isRegistered || matrixCode == "LOCKED_TENANT" || matrixCode == "HIDDEN_LOCK")
            {
                isLocked = true;
                confirmedFree = false;
            }

            string binaryVerdict = AutopilotViewModel.ComputeBinaryVerdict(matrixCode, isLocked, isRegistered, confirmedFree);
            if (binaryVerdict == "IMA_AUTOPILOT")
            {
                statusLevel = "CRITICAL";
                confirmedFree = false;
                if (matrixCode != "LOCKED_TENANT" && matrixCode != "HIDDEN_LOCK")
                {
                    matrixCode = hasLocalTraces ? "LOCKED_TENANT" : "HIDDEN_LOCK";
                }
            }
            else if (binaryVerdict == "NEMA_AUTOPILOT")
            {
                statusLevel = (matrixCode == "RELEASED_LOCAL_TRACES" || hasLocalTraces) ? "WARNING" : "OK";
            }
            else
            {
                // Nikad INFO ili OK za UNVERIFIED / OFFLINE / NIJE_PROVERENO!
                statusLevel = "WARNING";
                confirmedFree = false;
            }

            return new Dictionary<string, object>
            {
                { "matrix_code", matrixCode },
                { "status_level", statusLevel },
                { "binary_verdict", binaryVerdict },
                { "is_locked", isLocked },
                { "confirmed_free", confirmedFree },
                { "tenant_domain", tenantDomain ?? "" },
                { "tenant_id", tenantId ?? "" },
                { "assigned_device_name", assignedDeviceName ?? "" },
                { "query_mode", queryMode ?? "" },
                { "has_local_traces", hasLocalTraces },
                { "source", string.IsNullOrEmpty(source) ? "ui" : source }
            };
        }

        private static void ApplyHotfixMetadata(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            try
            {
                const string statePath = @"C:\setup\deploy-state.json";
                if (!File.Exists(statePath)) return;
                string json = File.ReadAllText(statePath, Encoding.UTF8);
                JsonNode root = JsonHelper.Parse(json);
                if (root == null || !root.IsObject || !root.ContainsKey("hotfix_applied")) return;
                JsonNode hf = root["hotfix_applied"];
                if (hf != null && hf.IsObject)
                {
                    payload["hotfix_applied"] = JsonNodeToNativeObject(hf);
                }
            }
            catch { }
        }

        private static void ApplyManualDriverBypassMetadata(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            bool bypass = false;
            string reason = "";

            string[] files = new string[] { @"C:\setup\deploy-state.json", @"C:\setup\hwtest-result.json" };
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    if (!File.Exists(files[i])) continue;
                    string json = File.ReadAllText(files[i], Encoding.UTF8);
                    JsonNode root = JsonHelper.Parse(json);
                    if (root == null || !root.IsObject) continue;
                    if (root.ContainsKey("manual_driver_bypass") && root["manual_driver_bypass"].AsBool(false))
                    {
                        bypass = true;
                    }
                    if (string.IsNullOrEmpty(reason) && root.ContainsKey("driver_gate_emergency_override_reason"))
                    {
                        string r = root["driver_gate_emergency_override_reason"].AsString("");
                        if (!string.IsNullOrWhiteSpace(r))
                        {
                            reason = r.Trim();
                            bypass = true;
                        }
                    }
                }
                catch { }
            }

            if (bypass)
            {
                payload["manual_driver_bypass"] = true;
                if (!string.IsNullOrEmpty(reason))
                {
                    payload["driver_gate_emergency_override_reason"] = reason;
                }
            }
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

        public string EnsureInstallSessionIdPersisted()
        {
            return GetOrCreateInstallSessionId();
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
            string resolvedSerial = GetResolvedSerialNumber();
            if (!string.IsNullOrEmpty(resolvedSerial))
            {
                data["serial_number"] = resolvedSerial;
                string canonDevId = GetCanonicalDeviceId(resolvedSerial);
                if (!string.IsNullOrEmpty(canonDevId))
                {
                    data["device_id"] = canonDevId;
                }
            }
            string prevHost = GetPreviousHostname();
            if (!string.IsNullOrEmpty(prevHost))
            {
                data["previous_hostname"] = prevHost;
            }

            string imgType, wimVer;
            GetImageProfileInfo(out imgType, out wimVer);
            data["image_type"] = imgType;
            data["wim_version"] = wimVer;
            data["autopilot"] = BuildAutopilotPayload();
            List<object> recentExecCmds = GetRecentExecutedCommandsForHeartbeat();
            if (recentExecCmds.Count > 0)
            {
                data["executed_commands"] = recentExecCmds;
            }
            ApplyHotfixMetadata(data);

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
                        string canonDevId = GetCanonicalDeviceId(resolvedSerial);
                        if (!string.IsNullOrEmpty(canonDevId))
                        {
                            payload["device_id"] = canonDevId;
                        }
                    }
                    string prevHost = GetPreviousHostname();
                    if (!string.IsNullOrEmpty(prevHost))
                    {
                        payload["previous_hostname"] = prevHost;
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

        #region HTTP Helpers & Remote Commands (IMP-102)

        public static string NormalizeRemoteAction(string rawAction)
        {
            if (string.IsNullOrWhiteSpace(rawAction)) return "";
            string norm = rawAction.Trim().ToLowerInvariant();
            if (norm == "apply_hotfix") return "apply_payload_update";
            return norm;
        }

        public static bool IsActionAllowlisted(string action)
        {
            string norm = NormalizeRemoteAction(action);
            if (string.IsNullOrEmpty(norm)) return false;
            return UiAllowlistedActions.Contains(norm) || PsAllowlistedActions.Contains(norm);
        }

        private static string ComputeShortDeterministicId(string seed)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""));
                    StringBuilder sb = new StringBuilder(16);
                    for (int i = 0; i < 8 && i < h.Length; i++)
                    {
                        sb.Append(h[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return Guid.NewGuid().ToString("N").Substring(0, 12);
            }
        }

        public static List<HubRemoteCommand> ParsePendingCommands(string respBody)
        {
            var result = new List<HubRemoteCommand>();
            if (string.IsNullOrWhiteSpace(respBody)) return result;

            try
            {
                JsonNode root = JsonHelper.Parse(respBody);
                if (root == null || !root.IsObject || !root.ContainsKey("pending_commands")) return result;
                JsonNode arr = root["pending_commands"];
                if (arr == null || !arr.IsArray) return result;

                for (int i = 0; i < arr.Elements.Count; i++)
                {
                    JsonNode item = arr.Elements[i];
                    if (item == null || item.IsNull) continue;

                    if (item.IsString)
                    {
                        string rawAct = (item.AsString("") ?? "").Trim();
                        if (string.IsNullOrEmpty(rawAct)) continue;
                        string normAct = NormalizeRemoteAction(rawAct);
                        HubRemoteCommand cmd = new HubRemoteCommand
                        {
                            Action = normAct,
                            CommandId = "legacy-" + normAct + "-" + ComputeShortDeterministicId("str|" + rawAct + "|" + i)
                        };
                        result.Add(cmd);
                    }
                    else if (item.IsObject)
                    {
                        string rawAct = item["action"].AsString(item["command"].AsString(item["type"].AsString(""))).Trim();
                        if (string.IsNullOrEmpty(rawAct)) continue;
                        string normAct = NormalizeRemoteAction(rawAct);

                        HubRemoteCommand cmd = new HubRemoteCommand
                        {
                            Action = normAct,
                            CreatedAt = item["created_at"].AsString(item["timestamp"].AsString("")).Trim()
                        };

                        if (item.ContainsKey("params") && item["params"].IsObject)
                        {
                            Dictionary<string, object> pDict = JsonNodeToNativeObject(item["params"]) as Dictionary<string, object>;
                            if (pDict != null)
                            {
                                foreach (KeyValuePair<string, object> kv in pDict)
                                {
                                    cmd.Params[kv.Key] = kv.Value;
                                }
                            }
                        }

                        // Kompatibilnost sa starim flat formatom: kopiraj poznata polja u Params ako nisu već u params
                        string[] legacyParamKeys = new string[] { "reason", "phase", "clear_step_guard", "step_id", "version", "mode", "target_phase" };
                        for (int k = 0; k < legacyParamKeys.Length; k++)
                        {
                            string pk = legacyParamKeys[k];
                            if (!cmd.Params.ContainsKey(pk) && item.ContainsKey(pk) && !item[pk].IsNull)
                            {
                                cmd.Params[pk] = JsonNodeToNativeObject(item[pk]);
                            }
                        }

                        string cmdId = item["command_id"].AsString(item["id"].AsString("")).Trim();
                        if (string.IsNullOrEmpty(cmdId))
                        {
                            string seed = normAct + "|" + cmd.CreatedAt + "|" + JsonHelper.Serialize(cmd.Params);
                            cmdId = "legacy-" + normAct + "-" + ComputeShortDeterministicId(seed);
                        }
                        cmd.CommandId = cmdId;
                        result.Add(cmd);
                    }
                }
            }
            catch { }

            return result;
        }

        private void EnsureExecutedCommandsLoadedLocked()
        {
            if (_executedCommandsLoaded) return;
            _executedCommandsLoaded = true;

            try
            {
                string path = string.IsNullOrWhiteSpace(ExecutedCommandsFilePath)
                    ? DefaultExecutedHubCommandsPath
                    : ExecutedCommandsFilePath;
                if (!File.Exists(path)) return;

                string raw = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw)) return;
                JsonNode root = JsonHelper.Parse(raw);
                if (root == null || !root.IsObject) return;

                JsonNode itemsNode = root["commands"];
                if (itemsNode != null && itemsNode.IsArray)
                {
                    for (int i = 0; i < itemsNode.Elements.Count; i++)
                    {
                        JsonNode n = itemsNode.Elements[i];
                        if (n == null || !n.IsObject) continue;
                        string cid = n["command_id"].AsString("").Trim();
                        if (string.IsNullOrEmpty(cid)) continue;
                        HubCommandExecutionResult r = new HubCommandExecutionResult
                        {
                            CommandId = cid,
                            Action = n["action"].AsString(""),
                            Status = n["status"].AsString("APPLIED"),
                            Detail = n["detail"].AsString(""),
                            AppliedAt = n["applied_at"].AsString("")
                        };
                        if (!_executedCommands.ContainsKey(cid))
                        {
                            _executedCommandOrder.Add(cid);
                        }
                        _executedCommands[cid] = r;
                    }
                }
            }
            catch { }
        }

        private void SaveExecutedCommandsLocked()
        {
            try
            {
                while (_executedCommandOrder.Count > 200)
                {
                    string oldest = _executedCommandOrder[0];
                    _executedCommandOrder.RemoveAt(0);
                    _executedCommands.Remove(oldest);
                }

                string path = string.IsNullOrWhiteSpace(ExecutedCommandsFilePath)
                    ? DefaultExecutedHubCommandsPath
                    : ExecutedCommandsFilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                List<object> list = new List<object>();
                for (int i = 0; i < _executedCommandOrder.Count; i++)
                {
                    string cid = _executedCommandOrder[i];
                    HubCommandExecutionResult r;
                    if (_executedCommands.TryGetValue(cid, out r) && r != null)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            { "command_id", r.CommandId ?? "" },
                            { "action", r.Action ?? "" },
                            { "status", r.Status ?? "" },
                            { "detail", r.Detail ?? "" },
                            { "applied_at", r.AppliedAt ?? "" }
                        });
                    }
                }

                var doc = new Dictionary<string, object>
                {
                    { "schema", "wis.executed_hub_commands.v1" },
                    { "updated_at", DateTime.UtcNow.ToString("o") },
                    { "commands", list }
                };

                string serialized = JsonHelper.Serialize(doc);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, serialized, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch { }
        }

        private bool TryReserveCommandExecution(HubRemoteCommand cmd)
        {
            if (cmd == null || string.IsNullOrWhiteSpace(cmd.CommandId)) return false;
            lock (_cmdLock)
            {
                EnsureExecutedCommandsLoadedLocked();
                if (_executedCommands.ContainsKey(cmd.CommandId))
                {
                    return false;
                }
                HubCommandExecutionResult placeholder = new HubCommandExecutionResult
                {
                    CommandId = cmd.CommandId,
                    Action = cmd.Action,
                    Status = "IN_PROGRESS",
                    Detail = "Command accepted for execution",
                    AppliedAt = DateTime.UtcNow.ToString("o")
                };
                _executedCommands[cmd.CommandId] = placeholder;
                _executedCommandOrder.Add(cmd.CommandId);
                SaveExecutedCommandsLocked();
                return true;
            }
        }

        private void RecordCommandExecutionResult(HubCommandExecutionResult res)
        {
            if (res == null || string.IsNullOrWhiteSpace(res.CommandId)) return;
            if (string.IsNullOrWhiteSpace(res.AppliedAt))
            {
                res.AppliedAt = DateTime.UtcNow.ToString("o");
            }
            lock (_cmdLock)
            {
                EnsureExecutedCommandsLoadedLocked();
                if (!_executedCommands.ContainsKey(res.CommandId))
                {
                    _executedCommandOrder.Add(res.CommandId);
                }
                _executedCommands[res.CommandId] = res;
                SaveExecutedCommandsLocked();
            }
        }

        public List<object> GetRecentExecutedCommandsForHeartbeat()
        {
            var list = new List<object>();
            lock (_cmdLock)
            {
                EnsureExecutedCommandsLoadedLocked();
                int start = Math.Max(0, _executedCommandOrder.Count - 20);
                for (int i = start; i < _executedCommandOrder.Count; i++)
                {
                    string cid = _executedCommandOrder[i];
                    HubCommandExecutionResult r;
                    if (_executedCommands.TryGetValue(cid, out r) && r != null)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            { "command_id", r.CommandId ?? "" },
                            { "action", r.Action ?? "" },
                            { "status", r.Status ?? "" },
                            { "detail", r.Detail ?? "" },
                            { "applied_at", r.AppliedAt ?? "" }
                        });
                    }
                }
            }
            return list;
        }

        private string ResolveCommandAckDeviceId()
        {
            string serial = GetResolvedSerialNumber();
            string canon = GetCanonicalDeviceId(serial);
            if (!string.IsNullOrWhiteSpace(canon)) return canon;
            return string.IsNullOrWhiteSpace(Environment.MachineName) ? "UNKNOWN" : Environment.MachineName.Trim();
        }

        private Dictionary<string, string> BuildClientAuthHeaders()
        {
            string tok = GetClientToken();
            if (string.IsNullOrEmpty(tok)) return null;
            var headers = new Dictionary<string, string>();
            headers["X-ServisHub-Client-Token"] = tok;
            return headers;
        }

        public bool SendCommandAck(HubCommandExecutionResult res)
        {
            if (res == null || string.IsNullOrWhiteSpace(res.CommandId)) return false;
            string currentHub = HubUrl;
            if (string.IsNullOrEmpty(currentHub)) return false;

            try
            {
                string devId = ResolveCommandAckDeviceId();
                string url = currentHub.TrimEnd('/') + "/api/devices/" + Uri.EscapeDataString(devId) + "/command-ack";
                var payload = new Dictionary<string, object>
                {
                    { "command_id", res.CommandId ?? "" },
                    { "action", res.Action ?? "" },
                    { "status", res.Status ?? "FAILED" },
                    { "detail", res.Detail ?? "" },
                    { "applied_at", string.IsNullOrWhiteSpace(res.AppliedAt) ? DateTime.UtcNow.ToString("o") : res.AppliedAt },
                    { "device_id", devId },
                    { "computer_name", Environment.MachineName ?? "" }
                };

                bool ok;
                HttpPostWithResponse(url, JsonHelper.Serialize(payload), 4000, out ok, BuildClientAuthHeaders());
                return ok;
            }
            catch
            {
                return false;
            }
        }

        public void ProcessHeartbeatCommands(string respBody)
        {
            if (string.IsNullOrWhiteSpace(respBody)) return;
            if (respBody.IndexOf("\"pending_commands\"", StringComparison.OrdinalIgnoreCase) < 0) return;

            List<HubRemoteCommand> commands = ParsePendingCommands(respBody);
            if (commands.Count == 0) return;

            List<object> psDelegatedPayload = new List<object>();

            for (int i = 0; i < commands.Count; i++)
            {
                HubRemoteCommand cmd = commands[i];
                if (cmd == null || string.IsNullOrWhiteSpace(cmd.CommandId)) continue;

                if (!TryReserveCommandExecution(cmd))
                {
                    continue; // Već izvršeno ili odbijeno (trajna deduplikacija po command_id)
                }

                // STROGA ALLOWLISTA: svaka akcija van allowliste se strogo odbija (APSOLUTNO ZABRANJENO proizvoljno PS/CMD izvršavanje)
                if (!IsActionAllowlisted(cmd.Action))
                {
                    string rejectMsg = string.Format("Odbijena nedozvoljena komanda van allowliste: '{0}' (command_id={1})", cmd.Action, cmd.CommandId);
                    PushLog("[WARN] [RemoteCmd] " + rejectMsg);
                    HubCommandExecutionResult rej = new HubCommandExecutionResult
                    {
                        CommandId = cmd.CommandId,
                        Action = cmd.Action,
                        Status = "REJECTED",
                        Detail = "Action not in strict allowlist",
                        AppliedAt = DateTime.UtcNow.ToString("o")
                    };
                    RecordCommandExecutionResult(rej);
                    SendCommandAck(rej);
                    continue;
                }

                if (PsAllowlistedActions.Contains(cmd.Action))
                {
                    psDelegatedPayload.Add(new Dictionary<string, object>
                    {
                        { "command_id", cmd.CommandId },
                        { "action", cmd.Action },
                        { "params", cmd.Params ?? new Dictionary<string, object>() },
                        { "created_at", cmd.CreatedAt ?? "" }
                    });

                    if (string.Equals(cmd.Action, "skip_step", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string skipPath = string.IsNullOrWhiteSpace(SkipMarkerFilePath) ? DefaultSkipMarkerPath : SkipMarkerFilePath;
                            string skipDir = Path.GetDirectoryName(skipPath);
                            if (!string.IsNullOrEmpty(skipDir) && !Directory.Exists(skipDir)) Directory.CreateDirectory(skipDir);
                            File.WriteAllText(skipPath, "hub_remote_skip", Encoding.UTF8);
                        }
                        catch { }
                    }

                    PushLog(string.Format("[INFO] [RemoteCmd] Allowlist PS akcija '{0}' prosleđena (command_id={1}).", cmd.Action, cmd.CommandId));
                    HubCommandExecutionResult psRes = new HubCommandExecutionResult
                    {
                        CommandId = cmd.CommandId,
                        Action = cmd.Action,
                        Status = string.Equals(cmd.Action, "skip_step", StringComparison.OrdinalIgnoreCase) ? "APPLIED" : "DELEGATED_PS",
                        Detail = "Forwarded allowlisted action to PS command queue",
                        AppliedAt = DateTime.UtcNow.ToString("o")
                    };
                    RecordCommandExecutionResult(psRes);
                    SendCommandAck(psRes);
                    continue;
                }

                // UI-izvršive allowlist akcije
                Func<HubRemoteCommand, HubCommandExecutionResult> handler;
                lock (_cmdLock)
                {
                    handler = _remoteCommandHandler;
                }

                if (handler == null)
                {
                    HubCommandExecutionResult noHandler = new HubCommandExecutionResult
                    {
                        CommandId = cmd.CommandId,
                        Action = cmd.Action,
                        Status = "FAILED",
                        Detail = "UI remote command handler is not registered",
                        AppliedAt = DateTime.UtcNow.ToString("o")
                    };
                    RecordCommandExecutionResult(noHandler);
                    SendCommandAck(noHandler);
                    continue;
                }

                HubRemoteCommand capturedCmd = cmd;
                Func<HubRemoteCommand, HubCommandExecutionResult> capturedHandler = handler;
                Action execWork = delegate
                {
                    HubCommandExecutionResult execRes = null;
                    try
                    {
                        PushLog(string.Format("[INFO] [RemoteCmd] Izvršavam allowlist akciju '{0}' (command_id={1})...", capturedCmd.Action, capturedCmd.CommandId));
                        execRes = capturedHandler(capturedCmd);
                    }
                    catch (Exception ex)
                    {
                        execRes = new HubCommandExecutionResult
                        {
                            CommandId = capturedCmd.CommandId,
                            Action = capturedCmd.Action,
                            Status = "FAILED",
                            Detail = "Exception: " + ex.Message,
                            AppliedAt = DateTime.UtcNow.ToString("o")
                        };
                    }

                    if (execRes == null)
                    {
                        execRes = new HubCommandExecutionResult
                        {
                            CommandId = capturedCmd.CommandId,
                            Action = capturedCmd.Action,
                            Status = "FAILED",
                            Detail = "Handler returned null result",
                            AppliedAt = DateTime.UtcNow.ToString("o")
                        };
                    }
                    execRes.CommandId = capturedCmd.CommandId;
                    execRes.Action = capturedCmd.Action;
                    if (string.IsNullOrWhiteSpace(execRes.AppliedAt))
                    {
                        execRes.AppliedAt = DateTime.UtcNow.ToString("o");
                    }

                    RecordCommandExecutionResult(execRes);
                    PushLog(string.Format("[INFO] [RemoteCmd] Akcija '{0}' završena: {1} ({2})", execRes.Action, execRes.Status, execRes.Detail ?? ""));
                    SendCommandAck(execRes);

                    if (execRes.PostAckAction != null)
                    {
                        try { execRes.PostAckAction(); } catch { }
                    }
                };

                if (AsyncCommandDispatch)
                {
                    ThreadPool.QueueUserWorkItem(delegate { execWork(); });
                }
                else
                {
                    execWork();
                }
            }

            if (psDelegatedPayload.Count > 0)
            {
                try
                {
                    string cmdFile = string.IsNullOrWhiteSpace(PendingCommandsFilePath)
                        ? DefaultPendingHubCommandsPath
                        : PendingCommandsFilePath;
                    string cmdDir = Path.GetDirectoryName(cmdFile);
                    if (!string.IsNullOrEmpty(cmdDir) && !Directory.Exists(cmdDir))
                    {
                        Directory.CreateDirectory(cmdDir);
                    }
                    var safeWrapper = new Dictionary<string, object>
                    {
                        { "pending_commands", psDelegatedPayload }
                    };
                    File.WriteAllText(cmdFile, JsonHelper.Serialize(safeWrapper), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public string HttpGetString(string relativePath, int timeoutMs, out bool success)
        {
            success = false;
            string currentHub = HubUrl;
            if (string.IsNullOrEmpty(currentHub))
            {
                DiscoverHub();
                currentHub = HubUrl;
            }
            if (string.IsNullOrEmpty(currentHub)) return null;

            try
            {
                string url = currentHub.TrimEnd('/') + "/" + (relativePath ?? "").TrimStart('/');
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                Dictionary<string, string> headers = BuildClientAuthHeaders();
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> kv in headers)
                    {
                        req.Headers.Add(kv.Key, kv.Value);
                    }
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
                }
            }
            catch { }
            return null;
        }

        public bool DownloadToFileWithLimit(string relativePath, string destPath, long maxBytes, int timeoutMs, out string error)
        {
            error = null;
            string currentHub = HubUrl;
            if (string.IsNullOrEmpty(currentHub))
            {
                DiscoverHub();
                currentHub = HubUrl;
            }
            if (string.IsNullOrEmpty(currentHub))
            {
                error = "ServiceHub URL nije dostupan.";
                return false;
            }

            try
            {
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string url = currentHub.TrimEnd('/') + "/" + (relativePath ?? "").TrimStart('/');
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
                Dictionary<string, string> headers = BuildClientAuthHeaders();
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> kv in headers)
                    {
                        req.Headers.Add(kv.Key, kv.Value);
                    }
                }

                long totalRead = 0;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode < 200 || (int)resp.StatusCode >= 300)
                    {
                        error = "HTTP greška pri preuzimanju: " + (int)resp.StatusCode;
                        return false;
                    }
                    using (Stream rs = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (rs == null)
                        {
                            error = "Prazan HTTP odgovor pri preuzimanju.";
                            return false;
                        }
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                        {
                            totalRead += n;
                            if (maxBytes > 0 && totalRead > maxBytes)
                            {
                                error = "Preuzeti tok prelazi potpisanu veličinu iz manifesta.";
                                return false;
                            }
                            fs.Write(buf, 0, n);
                        }
                        fs.Flush(true);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Greška pri preuzimanju paketa: " + ex.Message;
                return false;
            }
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

        public void SendPartialHwTestReportAsync()
        {
            if (!_isEnabled) return;

            Dictionary<string, object> hwSnapshot = BuildHardwarePayload();
            bool isLaptopSnapshot = ResolveIsLaptop();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (string.IsNullOrEmpty(HubUrl))
                    {
                        DiscoverHub();
                        if (string.IsNullOrEmpty(HubUrl)) return;
                    }

                    string hwTestJsonPath = @"C:\setup\hwtest-result.json";
                    if (!File.Exists(hwTestJsonPath)) return;

                    string hwJson = File.ReadAllText(hwTestJsonPath, Encoding.UTF8);
                    JsonNode hwNode = JsonHelper.Parse(hwJson);
                    if (hwNode == null || !hwNode.IsObject) return;

                    string serialSnapshot = hwSnapshot.ContainsKey("serial") && hwSnapshot["serial"] != null
                        ? hwSnapshot["serial"].ToString()
                        : "";
                    string devId = GetCanonicalDeviceId(serialSnapshot);
                    if (string.IsNullOrWhiteSpace(devId))
                    {
                        string chassisTag = isLaptopSnapshot ? "LAPTOP" : "DESKTOP";
                        string idSeed = !IsPlaceholderHwValue(serialSnapshot) ? serialSnapshot : Environment.MachineName;
                        devId = "TK-" + chassisTag + "-" + SanitizeFleetIdSegment(idSeed);
                    }

                    var partialPayload = new Dictionary<string, object>
                    {
                        { "device_id", devId },
                        { "computer_name", Environment.MachineName },
                        { "chassis", isLaptopSnapshot ? "Laptop" : "Desktop" },
                        { "hw", hwSnapshot },
                        { "hwtest", JsonNodeToNativeObject(hwNode) },
                        { "is_partial_hwtest", true },
                        { "report_stage", "phase5_hwtest" },
                        { "timestamp", DateTime.Now.ToString("o") }
                    };

                    if (!IsPlaceholderHwValue(serialSnapshot))
                    {
                        partialPayload["serial_number"] = serialSnapshot;
                    }
                    string prevHost = GetPreviousHostname();
                    if (!string.IsNullOrEmpty(prevHost))
                    {
                        partialPayload["previous_hostname"] = prevHost;
                    }
                    string sessionId = GetOrCreateInstallSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        partialPayload["install_session_id"] = sessionId;
                    }
                    string partialImgType, partialWimVer;
                    GetImageProfileInfo(out partialImgType, out partialWimVer);
                    partialPayload["image_type"] = partialImgType;
                    partialPayload["wim_version"] = partialWimVer;
                    partialPayload["autopilot"] = BuildAutopilotPayload();
                    ApplyManualDriverBypassMetadata(partialPayload);
                    ApplyHotfixMetadata(partialPayload);

                    string serialized = JsonHelper.Serialize(partialPayload);
                    string url = HubUrl.TrimEnd('/') + "/api/devices/" + Uri.EscapeDataString(devId) + "/report";
                    Dictionary<string, string> headers = null;
                    string tok = GetClientToken();
                    if (!string.IsNullOrEmpty(tok))
                    {
                        headers = new Dictionary<string, string>();
                        headers["X-ServisHub-Client-Token"] = tok;
                    }

                    bool ok;
                    HttpPostWithResponse(url, serialized, 5000, out ok, headers);
                }
                catch { }
            });
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

                    string canonDevId = GetCanonicalDeviceId(serialSnapshot);
                    string devId = reportPayload.ContainsKey("device_id") && reportPayload["device_id"] != null
                        ? reportPayload["device_id"].ToString()
                        : "";
                    if (!string.IsNullOrEmpty(canonDevId))
                    {
                        devId = canonDevId;
                        reportPayload["device_id"] = devId;
                    }
                    else if (string.IsNullOrWhiteSpace(devId))
                    {
                        string chassisTag = isLaptopSnapshot ? "LAPTOP" : "DESKTOP";
                        devId = "TK-" + chassisTag + "-" + SanitizeFleetIdSegment(serialSnapshot);
                        reportPayload["device_id"] = devId;
                    }
                    if (!reportPayload.ContainsKey("computer_name") || reportPayload["computer_name"] == null)
                    {
                        reportPayload["computer_name"] = Environment.MachineName;
                    }
                    string prevHost = GetPreviousHostname();
                    if (!string.IsNullOrEmpty(prevHost) && !reportPayload.ContainsKey("previous_hostname"))
                    {
                        reportPayload["previous_hostname"] = prevHost;
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
                    reportPayload["is_partial_hwtest"] = false;
                    reportPayload["report_stage"] = "phase6_final";
                    reportPayload["timestamp"] = DateTime.Now.ToString("o");
                    string sessionId = GetOrCreateInstallSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        reportPayload["install_session_id"] = sessionId;
                    }
                    string finalImgType, finalWimVer;
                    GetImageProfileInfo(out finalImgType, out finalWimVer);
                    if (!reportPayload.ContainsKey("image_type") || reportPayload["image_type"] == null || string.IsNullOrWhiteSpace(Convert.ToString(reportPayload["image_type"])))
                    {
                        reportPayload["image_type"] = finalImgType;
                    }
                    if (!reportPayload.ContainsKey("wim_version") || reportPayload["wim_version"] == null || string.IsNullOrWhiteSpace(Convert.ToString(reportPayload["wim_version"])))
                    {
                        reportPayload["wim_version"] = finalWimVer;
                    }
                    Dictionary<string, object> apFinalPayload = BuildAutopilotPayload();
                    bool existingApLocked = false;
                    if (reportPayload.ContainsKey("autopilot") && reportPayload["autopilot"] is Dictionary<string, object>)
                    {
                        var existingAp = (Dictionary<string, object>)reportPayload["autopilot"];
                        object lockObj;
                        if (existingAp.TryGetValue("is_locked", out lockObj) && lockObj is bool && (bool)lockObj)
                        {
                            existingApLocked = true;
                        }
                    }
                    bool newApLocked = apFinalPayload.ContainsKey("is_locked") && apFinalPayload["is_locked"] is bool && (bool)apFinalPayload["is_locked"];
                    if (!existingApLocked || newApLocked)
                    {
                        reportPayload["autopilot"] = apFinalPayload;
                    }
                    ApplyManualDriverBypassMetadata(reportPayload);
                    ApplyHotfixMetadata(reportPayload);
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
