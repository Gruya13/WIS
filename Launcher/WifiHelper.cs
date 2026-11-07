using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WIS.Launcher
{
    public static class WifiHelper
    {
        private static bool _isMonitoring = false;
        private static readonly object _monitorLock = new object();
        private static bool _lastKnownStatus = false;

        /// <summary>
        /// Događaj koji se okida kada dođe do promene stanja mreže.
        /// Parametar: bool isConnected (true ako je aktivna mreža sa validnom IP adresom).
        /// </summary>
        public static event Action<bool> NetworkStatusChanged;

        /// <summary>
        /// Izvorni .NET mrežni događaj za promenu mrežnih adresa ili adaptera.
        /// </summary>
        public static event NetworkAddressChangedEventHandler NetworkAddressChanged;

        /// <summary>
        /// Poslednje poznato stanje mrežne dostupnosti.
        /// </summary>
        public static bool LastKnownStatus
        {
            get { return _lastKnownStatus; }
        }

        /// <summary>
        /// Da li je aktivno reaktivno praćenje statusa mreže.
        /// </summary>
        public static bool IsMonitoring
        {
            get { return _isMonitoring; }
        }

        static WifiHelper()
        {
            StartMonitoring();
        }

        /// <summary>
        /// Pokreće reaktivno praćenje mrežnog statusa preko NetworkChange.NetworkAddressChanged.
        /// </summary>
        public static void StartMonitoring()
        {
            lock (_monitorLock)
            {
                if (_isMonitoring) return;
                try
                {
                    NetworkChange.NetworkAddressChanged += OnNetworkAddressChangedInternal;
                    _lastKnownStatus = IsNetworkConnected();
                    _isMonitoring = true;
                }
                catch
                {
                    // Ignoriši izuzetke ako mrežni stek još nije inicijalizovan
                }
            }
        }

        /// <summary>
        /// Zaustavlja reaktivno praćenje mrežnog statusa.
        /// </summary>
        public static void StopMonitoring()
        {
            lock (_monitorLock)
            {
                if (!_isMonitoring) return;
                try
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkAddressChangedInternal;
                    _isMonitoring = false;
                }
                catch { }
            }
        }

        private static void OnNetworkAddressChangedInternal(object sender, EventArgs e)
        {
            try
            {
                bool isConnected = IsNetworkConnected();
                _lastKnownStatus = isConnected;

                NetworkAddressChangedEventHandler rawHandlers = NetworkAddressChanged;
                if (rawHandlers != null)
                {
                    rawHandlers(sender, e);
                }

                Action<bool> statusHandlers = NetworkStatusChanged;
                if (statusHandlers != null)
                {
                    statusHandlers(isConnected);
                }
            }
            catch { }
        }

        /// <summary>
        /// Brza provera i detekcija trenutne dostupnosti mreže (IPv4 van APIPA i loopback opsega).
        /// </summary>
        public static bool IsNetworkConnected()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    _lastKnownStatus = false;
                    return false;
                }

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = u.Address.ToString();
                            if (!ipStr.StartsWith("169.254.") && !ipStr.StartsWith("127."))
                            {
                                _lastKnownStatus = true;
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignorisi greske mrezne detekcije
            }
            _lastKnownStatus = false;
            return false;
        }

        public static List<string> GetNetworkAdapterStatus()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string typeStr = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "LAN";
                    string statusStr = ni.OperationalStatus == OperationalStatus.Up ? "POVEZAN" : "NIJE POVEZAN";
                    string ipStr = "";

                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                string s = u.Address.ToString();
                                if (!s.StartsWith("169.254.") && !s.StartsWith("127."))
                                {
                                    ipStr = " (" + s + ")";
                                    break;
                                }
                            }
                        }
                    }

                    result.Add(string.Format("[{0}] {1}: {2}{3}", typeStr, ni.Name, statusStr, ipStr));
                }
            }
            catch (Exception ex)
            {
                result.Add("Greška pri očitavanju adaptera: " + ex.Message);
            }

            if (result.Count == 0)
            {
                result.Add("Nisu detektovani fizički mrežni adapteri.");
            }

            return result;
        }

        public static string FindWifiConfigFile()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = new string[]
            {
                Path.Combine(baseDir, "Wifi-Servis.ini"),
                @"C:\setup\Wifi-Servis.ini",
                @"C:\setup\Skripte\Wifi-Servis.ini"
            };

            foreach (string p in searchPaths)
            {
                if (File.Exists(p))
                    return p;
            }

            try
            {
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
            catch
            {
                // Ignorisi greske pristupa drajvovima
            }

            return null;
        }

        public static bool TryConnectServisWifi(Action<string> statusCallback)
        {
            string iniPath = FindWifiConfigFile();
            if (string.IsNullOrEmpty(iniPath))
            {
                if (statusCallback != null) statusCallback("Wifi-Servis.ini nije pronađen.");
                return false;
            }

            string enabled = "1";
            string ssidConfig = "";
            string password = "";

            try
            {
                string[] lines = File.ReadAllLines(iniPath);
                string currentSection = "";
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    int eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string key = line.Substring(0, eqIdx).Trim();
                        string val = line.Substring(eqIdx + 1).Trim();

                        if (currentSection.Equals("ServisWifi", StringComparison.OrdinalIgnoreCase) ||
                            currentSection.Equals("WiFi", StringComparison.OrdinalIgnoreCase))
                        {
                            if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) enabled = val;
                            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase)) ssidConfig = val;
                            else if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                                     key.Equals("Key", StringComparison.OrdinalIgnoreCase)) password = val;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (statusCallback != null) statusCallback("Greška čitanja Wifi-Servis.ini: " + ex.Message);
                return false;
            }

            if (enabled == "0" || string.IsNullOrEmpty(ssidConfig))
            {
                return false;
            }

            string[] ssids = ssidConfig.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawSsid in ssids)
            {
                string ssid = rawSsid.Trim();
                if (string.IsNullOrEmpty(ssid)) continue;

                if (statusCallback != null) statusCallback("Konfiguracija profila za Wi-Fi: " + ssid + "...");

                string tempXml = Path.Combine(Path.GetTempPath(), "WIS_TempWifi_" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    string xml = string.Format(
@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{0}</name>
    <SSIDConfig>
        <SSID><name>{0}</name></SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{1}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>", ssid, password);

                    File.WriteAllText(tempXml, xml, Encoding.UTF8);

                    RunCommand("netsh.exe", string.Format("wlan add profile filename=\"{0}\" user=all", tempXml), 3000);

                    if (statusCallback != null) statusCallback("Povezivanje na " + ssid + "...");
                    RunCommand("netsh.exe", string.Format("wlan connect name=\"{0}\"", ssid), 3000);

                    // Cekanje do 6 sekundi na uspostavljanje IP adrese
                    for (int i = 0; i < 12; i++)
                    {
                        Thread.Sleep(500);
                        if (IsNetworkConnected())
                        {
                            if (statusCallback != null) statusCallback("Uspešno povezan na Wi-Fi: " + ssid);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (statusCallback != null) statusCallback("Greška pri spajanju na " + ssid + ": " + ex.Message);
                }
                finally
                {
                    try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
                }
            }

            return IsNetworkConnected();
        }

        private static int RunCommand(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p.WaitForExit(timeoutMs))
                    {
                        return p.ExitCode;
                    }
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
            return -1;
        }
    }
}
