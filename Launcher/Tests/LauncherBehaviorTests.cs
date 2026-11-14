using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using WIS.Launcher;

class LauncherBehaviorTests
{
    static RSACryptoServiceProvider key;
    static int passed;
    static string fixtures;
    static JavaScriptSerializer json = new JavaScriptSerializer();
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static string FilePath(string root, string relative) { return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); }
    static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); }
    static string Manifest(string root, string version, string archive, string minimumLauncher = "2.0.0")
    {
        Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            hashes[path.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')] = PayloadManager.CalculateSha256(path);
        long size = archive == null ? 1 : new FileInfo(archive).Length;
        string sha = archive == null ? new string('a', 64) : PayloadManager.CalculateSha256(archive);
        string signed = "WIS|v2|" + version + "|commit-0123456789|" + size + "|" + sha + "|" + minimumLauncher + "|" + PayloadManager.ComputeRequiredFilesCanonicalSha256(hashes);
        Dictionary<string, object> doc = new Dictionary<string, object> {
            {"product", "WIS"}, {"schema_version", 2}, {"version", version}, {"release_id", "commit-0123456789"},
            {"min_launcher_version", minimumLauncher}, {"sha256", sha}, {"size", size}, {"required_files", hashes},
            {"signed_payload", signed}, {"signature", Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(signed), "SHA256"))}
        };
        return json.Serialize(doc);
    }
    static string MakeFiles(string version, bool extra)
    {
        string root = Path.Combine(fixtures, Guid.NewGuid().ToString("N"));
        foreach (string relative in PayloadManager.MandatoryRelativeFiles) Write(FilePath(root, relative), version + relative);
        if (PayloadManager.CompareVersions(version, "2.17.0") >= 0)
            foreach (string relative in PayloadManager.MandatoryRelease217Files) Write(FilePath(root, relative), version + relative);
        if (extra) Write(FilePath(root, "Skripte/new.dll"), version + "extra");
        return root;
    }
    static void Install(string version)
    {
        string root = MakeFiles(version, false);
        foreach (string relative in PayloadManager.MandatoryRelativeFiles) Write(FilePath(PayloadManager.SetupDir, relative), File.ReadAllText(FilePath(root, relative)));
        Write(PayloadManager.MetaPath, Manifest(root, version, null));
    }
    static string Candidate(string version, bool extra)
    {
        string root = MakeFiles(version, extra);
        string archive = root + ".zip";
        ZipFile.CreateFromDirectory(root, archive);
        Directory.CreateDirectory(PayloadManager.UpdateAttemptDir);
        File.Copy(archive, PayloadManager.PartialZipPath, true);
        string metadata = Manifest(root, version, archive);
        Write(PayloadManager.PendingMetaPath, metadata);
        return metadata;
    }
    static void VerifyVersion(string expected)
    {
        PayloadVersionInfo info; string error;
        Assert(PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out info, out error), "Installed verification failed: " + error);
        Assert(info.Version == expected, "Unexpected version: " + info.Version);
    }
    static void Test(string name, Action action)
    {
        if (Directory.Exists(PayloadManager.SetupDir)) Directory.Delete(PayloadManager.SetupDir, true);
        Directory.CreateDirectory(PayloadManager.SetupDir);
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        fixtures = Path.Combine(Path.GetTempPath(), "wis-launcher-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        using (key = new RSACryptoServiceProvider(2048))
        {
            PayloadManager.TrustedPublicKeyXml = key.ToXmlString(false);
            try
            {
                Test("valid signed local payload", delegate { Install("2.16.2"); VerifyVersion("2.16.2"); Assert(PayloadManager.GetLocalVersion() == "2.16.2", "GetLocalVersion did not return verified package version"); });
                Test("independent launcher version accepts agreed minimum", delegate {
                    string root = MakeFiles("2.17.0", false);
                    Write(FilePath(root, "version.json"), "{\"version\":\"2.17.0\",\"components\":{\"ui\":{\"version\":\"2.1.0\"},\"launcher\":{\"version\":\"2.2.17\"}}}");
                    PayloadVersionInfo info; string error;
                    Assert(PayloadManager.ParseAndVerifyManifest(Manifest(root, "2.17.0", null, "2.2.17"), out info, out error), error);
                    Assert(info.Version == "2.17.0", "Package version confused with UI version");
                });
                Test("newer minimum launcher is rejected", delegate {
                    string root = MakeFiles("2.17.0", false); PayloadVersionInfo info; string error;
                    Assert(!PayloadManager.ParseAndVerifyManifest(Manifest(root, "2.17.0", null, "3.0.1"), out info, out error), "Incompatible minimum accepted");
                });
                Test("tampered local UI cannot start", delegate {
                    Install("2.16.2"); Write(PayloadManager.TargetUiExe, "tampered"); string error;
                    Assert(!PayloadManager.TryStartVerifiedLocalUi(out error), "Tampered UI was launched");
                });
                Test("unsigned local UI cannot start", delegate {
                    Write(PayloadManager.TargetUiExe, "unsigned"); string error;
                    Assert(!PayloadManager.TryStartVerifiedLocalUi(out error), "Unsigned UI was launched");
                });
                Test("wrong RSA signature rejected", delegate {
                    Install("2.16.2"); Dictionary<string, object> doc = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(PayloadManager.MetaPath));
                    doc["signature"] = Convert.ToBase64String(new byte[256]); Write(PayloadManager.MetaPath, json.Serialize(doc));
                    PayloadVersionInfo info; string error;
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out info, out error), "Bad RSA accepted");
                });
                Test("malformed JSON rejected", delegate {
                    Install("2.16.2"); string text = File.ReadAllText(PayloadManager.MetaPath); PayloadVersionInfo info; string error;
                    Assert(!PayloadManager.ParseAndVerifyManifest("invalid " + text, out info, out error), "Invalid JSON accepted");
                });
                Test("extra unsigned DLL rejected", delegate {
                    Install("2.16.2"); Write(FilePath(PayloadManager.SetupDir, "Skripte/evil.dll"), "evil");
                    PayloadVersionInfo info; string error;
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out info, out error), "Unsigned DLL accepted");
                });
                Test("external Programi cache does not block signed WIS verification", delegate {
                    Install("2.16.2"); Write(FilePath(PayloadManager.SetupDir, "Programi/ChromeSetup.exe"), "external installer without WIS hash");
                    Write(FilePath(PayloadManager.SetupDir, "Programi/Office/setup.exe"), "external Office installer");
                    VerifyVersion("2.16.2");
                });
                Test("Programi exception does not allow unsigned root or nested WIS executables", delegate {
                    Install("2.16.2"); Write(FilePath(PayloadManager.SetupDir, "Programi/ChromeSetup.exe"), "external installer");
                    string evil = FilePath(PayloadManager.SetupDir, "evil.exe"); Write(evil, "unsigned root code");
                    PayloadVersionInfo manifest; string error;
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out manifest, out error), "Unsigned root EXE accepted");
                    File.Delete(evil);
                    Write(FilePath(PayloadManager.SetupDir, "Skripte/Programi/evil.exe"), "unsigned nested code");
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out manifest, out error), "Unsigned nested WIS EXE accepted");
                });
                Test("signed Programi file is still hash verified", delegate {
                    string root = MakeFiles("2.16.2", false);
                    foreach (string relative in PayloadManager.MandatoryRelativeFiles) Write(FilePath(PayloadManager.SetupDir, relative), File.ReadAllText(FilePath(root, relative)));
                    string relativeInstaller = "Programi/ChromeSetup.exe";
                    Write(FilePath(root, relativeInstaller), "signed installer"); Write(FilePath(PayloadManager.SetupDir, relativeInstaller), "signed installer");
                    Write(PayloadManager.MetaPath, Manifest(root, "2.16.2", null)); VerifyVersion("2.16.2");
                    Write(FilePath(PayloadManager.SetupDir, relativeInstaller), "tampered installer"); PayloadVersionInfo manifest; string error;
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out manifest, out error), "Signed installer hash skipped");
                });
                Test("missing phase rejected", delegate {
                    Install("2.16.2"); File.Delete(FilePath(PayloadManager.SetupDir, PayloadManager.MandatoryRelativeFiles[1]));
                    PayloadVersionInfo info; string error;
                    Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out info, out error), "Missing phase accepted");
                });
                Test("new release requires signed dependencies and Windows validation receipts", delegate {
                    foreach (string dependency in PayloadManager.MandatoryRelease217Files)
                    {
                        string root = MakeFiles("2.17.0", false);
                        File.Delete(FilePath(root, dependency));
                        string signedIncomplete = Manifest(root, "2.17.0", null);
                        PayloadVersionInfo manifest; string error;
                        Assert(!PayloadManager.ParseAndVerifyManifest(signedIncomplete, out manifest, out error), "Incomplete new release accepted: " + dependency);
                        Assert(error.Contains(dependency), "Missing dependency not identified: " + error);
                    }
                });
                Test("valid archive promotes and preserves verified backup", delegate {
                    Install("2.16.2"); Candidate("2.17.0", true);
                    Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError); VerifyVersion("2.17.0");
                    PayloadVersionInfo backup; string error;
                    Assert(PayloadManager.TryVerifyInstalledPayload(PayloadManager.BackupPrevDir, out backup, out error), error);
                    Assert(backup.Version == "2.16.2", "Backup version wrong");
                    Assert(PayloadManager.ReadTransactionState() == "committed", "Missing commit marker");
                });
                Test("tampered installation can be repaired while last verified backup is preserved", delegate {
                    Install("2.16.2"); Candidate("2.17.0", false); Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError);
                    Write(PayloadManager.TargetUiExe, "tampered"); Candidate("2.18.0", false);
                    Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError); VerifyVersion("2.18.0");
                    PayloadVersionInfo backup; string error;
                    Assert(PayloadManager.TryVerifyInstalledPayload(PayloadManager.BackupPrevDir, out backup, out error), error);
                    Assert(backup.Version == "2.16.2", "Corrupt active version replaced trusted backup");
                });
                Test("unverified PXE OEM setup with extra root or VMD binaries is cleaned and promoted", delegate {
                    Write(FilePath(PayloadManager.SetupDir, "HidSharp.dll"), "stale root dll");
                    Write(FilePath(PayloadManager.SetupDir, "WIS.UI.exe"), "stale root exe");
                    Write(FilePath(PayloadManager.SetupDir, "Drivers/VMD/01_RST_v20/RstMwService.exe"), "stale vmd exe");
                    Write(PayloadManager.UpdateTxPath, "{\"state\":\"promoting\"}");
                    bool rolled; string recMsg;
                    Assert(PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out recMsg), recMsg);
                    Candidate("2.17.0", false);
                    Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError);
                    VerifyVersion("2.17.0");
                    Assert(!File.Exists(FilePath(PayloadManager.SetupDir, "HidSharp.dll")), "Stale root HidSharp.dll remained");
                    Assert(!File.Exists(FilePath(PayloadManager.SetupDir, "Drivers/VMD/01_RST_v20/RstMwService.exe")), "Stale VMD exe remained");
                });
                Test("truncated download preserves installed version", delegate {
                    Install("2.16.2"); Candidate("2.17.0", false);
                    using (FileStream file = File.OpenWrite(PayloadManager.PartialZipPath)) file.SetLength(23);
                    Assert(!PayloadManager.ExtractPayload(null), "Truncated ZIP accepted"); VerifyVersion("2.16.2");
                });
                Test("same-size corrupt ZIP preserves installed version", delegate {
                    Install("2.16.2"); Candidate("2.17.0", false);
                    byte[] zip = File.ReadAllBytes(PayloadManager.PartialZipPath); zip[100] ^= 1; File.WriteAllBytes(PayloadManager.PartialZipPath, zip);
                    Assert(!PayloadManager.ExtractPayload(null), "Wrong hash accepted"); VerifyVersion("2.16.2");
                });
                Test("actual HTTP download validates bytes before promotion", delegate {
                    Install("2.16.2"); string metadata = Candidate("2.17.0", false);
                    byte[] payload = File.ReadAllBytes(PayloadManager.PartialZipPath);
                    TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                    Thread server = new Thread(delegate() {
                        using (TcpClient client = listener.AcceptTcpClient()) using (NetworkStream stream = client.GetStream())
                        {
                            byte[] request = new byte[4096]; stream.Read(request, 0, request.Length);
                            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length); stream.Write(payload, 0, payload.Length);
                        }
                    });
                    server.Start();
                    PayloadVersionInfo manifest; string error; Assert(PayloadManager.ParseAndVerifyManifest(metadata, out manifest, out error), error);
                    try
                    {
                        Assert(PayloadManager.DownloadPayload("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, manifest.Sha256, metadata, null), PayloadManager.LastError);
                        VerifyVersion("2.16.2"); Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError); VerifyVersion("2.17.0");
                    }
                    finally { listener.Stop(); server.Join(); }
                });
                Test("interrupted actual HTTP stream never promotes", delegate {
                    Install("2.16.2"); string metadata = Candidate("2.17.0", false);
                    byte[] payload = File.ReadAllBytes(PayloadManager.PartialZipPath);
                    TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                    Thread server = new Thread(delegate() {
                        using (TcpClient client = listener.AcceptTcpClient()) using (NetworkStream stream = client.GetStream())
                        {
                            byte[] request = new byte[4096]; stream.Read(request, 0, request.Length);
                            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length); stream.Write(payload, 0, payload.Length / 2);
                        }
                    });
                    server.Start(); PayloadVersionInfo manifest; string error; Assert(PayloadManager.ParseAndVerifyManifest(metadata, out manifest, out error), error);
                    try
                    {
                        Assert(!PayloadManager.DownloadPayload("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, manifest.Sha256, metadata, null), "Truncated HTTP accepted");
                        VerifyVersion("2.16.2"); Assert(!File.Exists(PayloadManager.PendingMetaPath), "Failed download left promotable metadata");
                    }
                    finally { listener.Stop(); server.Join(); }
                });
                Test("locked UI blocks promotion before installed files change", delegate {
                    Install("2.16.2"); Candidate("2.17.0", false);
                    using (FileStream locked = new FileStream(PayloadManager.TargetUiExe, FileMode.Open, FileAccess.Read, FileShare.None))
                        Assert(!PayloadManager.ExtractPayload(null), "Locked UI was promoted");
                    VerifyVersion("2.16.2");
                    Assert(!File.Exists(PayloadManager.UpdateTxPath), "Unchanged version incorrectly left uncertain journal");
                });
                Test("post-promotion failure rolls back complete version", delegate {
                    Install("2.16.2"); Candidate("2.17.0", true);
                    Assert(!PayloadManager.ExtractPayload(delegate(string step, int current, int total) { if (current == 9) throw new IOException("injected interruption"); }), "Injected failure ignored");
                    VerifyVersion("2.16.2");
                    bool rolled; string error; Assert(PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error), error);
                    VerifyVersion("2.16.2");
                });
                Test("partial backup never overwrites active version and journal survives", delegate {
                    Install("2.16.2"); Write(FilePath(PayloadManager.BackupPrevDir, "broken.dll"), "partial");
                    Write(PayloadManager.UpdateTxPath, "{\"state\":\"promoting\"}"); bool rolled; string error;
                    Assert(!PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error), "Partial backup accepted");
                    VerifyVersion("2.16.2"); Assert(File.Exists(PayloadManager.UpdateTxPath), "Journal deleted after failed recovery");
                });
                Test("failed rollback keeps verified backup for retry", delegate {
                    Install("2.16.2"); Candidate("2.17.0", false); Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError);
                    string target = FilePath(PayloadManager.SetupDir, PayloadManager.MandatoryRelativeFiles[1]); File.Delete(target); Directory.CreateDirectory(target);
                    Write(PayloadManager.UpdateTxPath, "{\"state\":\"promoting\"}"); bool rolled; string error;
                    Assert(!PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error), "Rollback unexpectedly succeeded");
                    Assert(File.Exists(PayloadManager.UpdateTxPath), "Journal lost"); Assert(Directory.Exists(PayloadManager.BackupPrevDir), "Backup lost");
                    Directory.Delete(target); Assert(PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error), error); VerifyVersion("2.16.2");
                });
                foreach (string state in new string[] { "staging_verified", "backup_created" })
                    Test("pre-promotion interruption " + state + " retains current version", delegate {
                        Install("2.16.2"); Candidate("2.17.0", false); Assert(PayloadManager.ExtractPayload(null), PayloadManager.LastError);
                        Write(PayloadManager.UpdateTxPath, "{\"state\":\"" + state + "\"}"); bool rolled; string error;
                        Assert(PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error), error); Assert(!rolled, "Older backup incorrectly restored"); VerifyVersion("2.17.0");
                    });
                Test("self-update detection skips identical or external executable", delegate {
                    string running = FilePath(PayloadManager.SetupDir, "WIS.Launcher.exe"); string staged = FilePath(PayloadManager.StagingExtractDir, "WIS.Launcher.exe");
                    Write(running, "old"); Write(staged, "old"); Assert(!PayloadManager.NeedsSelfUpdate(running, PayloadManager.StagingExtractDir), "Identical EXE delegated");
                    Write(staged, "new"); Assert(PayloadManager.NeedsSelfUpdate(running, PayloadManager.StagingExtractDir), "Changed EXE not delegated");
                    Assert(!PayloadManager.NeedsSelfUpdate(typeof(LauncherBehaviorTests).Assembly.Location, PayloadManager.StagingExtractDir), "External helper delegated recursively");
                });
                Test("mutex prevents recovery cleanup while update is active", delegate {
                    Install("2.16.2"); Write(FilePath(PayloadManager.StagingExtractDir, "marker"), "keep");
                    Exception failure = null;
                    Thread other;
                    using (PayloadManager.BeginUpdateSession())
                    {
                        other = new Thread(delegate() { try { bool rolled; string error; PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolled, out error); } catch (Exception ex) { failure = ex; } });
                        other.Start(); Thread.Sleep(300); Assert(File.Exists(FilePath(PayloadManager.StagingExtractDir, "marker")), "Recovery cleaned an active update");
                    }
                    Assert(other.Join(20000), "Recovery thread failed to finish after mutex release");
                    Assert(failure == null, "Recovery thread exception");
                });
                if (Path.DirectorySeparatorChar == '/')
                    Test("symlink local component rejected before hashing", delegate {
                        Install("2.16.2"); string target = PayloadManager.TargetUiExe; string outside = Path.Combine(fixtures, "outside-ui");
                        File.Copy(target, outside); File.Delete(target);
                        using (Process process = Process.Start(new ProcessStartInfo { FileName = "ln", Arguments = "-s \"" + outside + "\" \"" + target + "\"", UseShellExecute = false }))
                            Assert(process.WaitForExit(5000) && process.ExitCode == 0, "Symlink fixture failed");
                        PayloadVersionInfo manifest; string error;
                        Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out manifest, out error), "Symlink accepted");
                        File.Delete(target);
                    });
                if (Path.DirectorySeparatorChar == '/')
                    Test("external Programi cache still rejects symlink installer", delegate {
                        Install("2.16.2"); string outside = Path.Combine(fixtures, "outside-installer");
                        File.WriteAllText(outside, "external installer"); string target = FilePath(PayloadManager.SetupDir, "Programi/ChromeSetup.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (Process process = Process.Start(new ProcessStartInfo { FileName = "ln", Arguments = "-s \"" + outside + "\" \"" + target + "\"", UseShellExecute = false }))
                            Assert(process.WaitForExit(5000) && process.ExitCode == 0, "Symlink fixture failed");
                        PayloadVersionInfo manifest; string error;
                        Assert(!PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out manifest, out error), "Installer cache symlink bypassed guard");
                        File.Delete(target);
                    });
                Test("ZIP traversal, reserved paths, duplicates and header mismatch rejected", delegate {
                    foreach (string name in new string[] { "../escape", "C:/escape", ".update_tx.json", "payload_meta.json", "Skripte/CON.dll", "Skripte/a.dll." })
                    {
                        string zip = Path.Combine(fixtures, Guid.NewGuid() + ".zip");
                        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create)) using (StreamWriter writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("x");
                        List<string> entries; string error; Assert(!PayloadManager.ValidateZipEntriesAgainstZipSlip(zip, PayloadManager.StagingExtractDir, out entries, out error), "Unsafe ZIP accepted: " + name);
                    }
                    string duplicate = Path.Combine(fixtures, "duplicate.zip");
                    using (ZipArchive archive = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { archive.CreateEntry("a.dll"); archive.CreateEntry("A.dll"); }
                    List<string> listed; string reason; Assert(!PayloadManager.ValidateZipEntriesAgainstZipSlip(duplicate, PayloadManager.StagingExtractDir, out listed, out reason), "Duplicate ZIP accepted");
                    string mismatch = Path.Combine(fixtures, "mismatch.zip");
                    using (ZipArchive archive = ZipFile.Open(mismatch, ZipArchiveMode.Create)) archive.CreateEntry("safe.dll");
                    byte[] data = File.ReadAllBytes(mismatch); data[30] = (byte)'X'; File.WriteAllBytes(mismatch, data);
                    Assert(!PayloadManager.ValidateZipEntriesAgainstZipSlip(mismatch, PayloadManager.StagingExtractDir, out listed, out reason), "Mismatched local header accepted");
                });
            }
            finally { Directory.Delete(fixtures, true); }
        }
        Console.WriteLine("RESULT " + passed + " behavioral tests passed");
    }
}
