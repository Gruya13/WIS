using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WIS.Launcher
{
    /// <summary>
    /// Mrežni mehanizam za pronalaženje adrese ServisHub servera (UDP broadcast, Gateway, INI, fiksne IP).
    /// BEZBEDNOSNA GRANICA (Nalaz 3): HubDiscovery služi ISKLJUČIVO za pronalaženje kandidata adrese (hubUrl).
    /// Dostupnost endpointa NIJE dokaz autentičnosti paketa — svaki preuzeti manifest i ZIP paket
    /// moraju proći kriptografsku RSA-2048 i SHA-256 verifikaciju u PayloadManager-u pre upotrebe.
    /// </summary>
    public static class HubDiscovery
    {
        private const int UDP_PORT = 8888;
        private const int DEFAULT_HTTP_PORT = 8080;

        public static string DiscoverHubUrl(Action<string> statusCallback)
        {
            if (statusCallback != null) statusCallback("Slanje UDP broadcasta za ServisHub (port 8888)...");

            // 1. Pokusaj preko UDP broadcasta
            string udpResult = TryUdpBroadcast(statusCallback);
            if (!string.IsNullOrEmpty(udpResult))
            {
                if (statusCallback != null) statusCallback("ServisHub kandidat pronađen preko UDP-a: " + udpResult);
                return udpResult;
            }

            // 2. Fallback na Default Gateway
            if (statusCallback != null) statusCallback("Traženje Default Gateway adrese...");
            string gwResult = TryDefaultGateway(statusCallback);
            if (!string.IsNullOrEmpty(gwResult))
            {
                if (statusCallback != null) statusCallback("ServisHub kandidat pronađen na Gateway-u: " + gwResult);
                return gwResult;
            }

            // 3. Fallback na Wifi-Servis.ini [ServisHub] konfiguraciju
            string iniResult = TryIniConfig(statusCallback);
            if (!string.IsNullOrEmpty(iniResult))
            {
                if (statusCallback != null) statusCallback("ServisHub kandidat pronađen iz Wifi-Servis.ini: " + iniResult);
                return iniResult;
            }

            // 4. Provera poznate fiksne adrese TrueNAS servera (192.168.170.254)
            string[] knownNasIps = new string[] { "192.168.170.254", "192.168.88.50", "192.168.180.50", "192.168.170.188" };
            foreach (string ip in knownNasIps)
            {
                string candidate = string.Format("http://{0}:{1}", ip, DEFAULT_HTTP_PORT);
                if (ValidateHubEndpoint(candidate))
                {
                    if (statusCallback != null) statusCallback("ServisHub kandidat pronađen na NAS adresi: " + candidate);
                    return candidate;
                }
            }

            // 5. Fallback na localhost (lokalno testiranje)
            string localResult = TryProbeHub("http://127.0.0.1:8080");
            if (!string.IsNullOrEmpty(localResult))
            {
                return localResult;
            }

            return null;
        }

        public static string GetNasHost(string hubUrl)
        {
            if (!string.IsNullOrEmpty(hubUrl))
            {
                try
                {
                    Uri u = new Uri(hubUrl);
                    return u.Host;
                }
                catch { }
            }
            return "192.168.170.254";
        }

        private static string TryUdpBroadcast(Action<string> statusCallback)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (UdpClient client = new UdpClient())
                    {
                        client.EnableBroadcast = true;
                        client.Client.ReceiveTimeout = 1500;

                        byte[] reqData = Encoding.UTF8.GetBytes(string.Format("GWADT_DISCOVER client={0}", Environment.MachineName));
                        IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, UDP_PORT);

                        client.Send(reqData, reqData.Length, broadcastEp);

                        // Pokusaj i subnet broadcast adrese ako su dostupne
                        try
                        {
                            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                            {
                                if (ni.OperationalStatus == OperationalStatus.Up)
                                {
                                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                                    {
                                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                                        {
                                            byte[] ipBytes = u.Address.GetAddressBytes();
                                            byte[] maskBytes = u.IPv4Mask.GetAddressBytes();
                                            byte[] subBroadcast = new byte[4];
                                            for (int i = 0; i < 4; i++)
                                                subBroadcast[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
                                            
                                            IPAddress subBcAddr = new IPAddress(subBroadcast);
                                            client.Send(reqData, reqData.Length, new IPEndPoint(subBcAddr, UDP_PORT));
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                        byte[] respBytes = client.Receive(ref remoteEp);
                        string resp = Encoding.UTF8.GetString(respBytes).Trim();

                        if (resp.Contains("GWADT_HUB_OFFER"))
                        {
                            int port = DEFAULT_HTTP_PORT;
                            Match m = Regex.Match(resp, @"port=(\d+)");
                            if (m.Success)
                            {
                                int.TryParse(m.Groups[1].Value, out port);
                            }

                            string hubIp = remoteEp.Address.ToString();
                            string hubUrl = string.Format("http://{0}:{1}", hubIp, port);

                            // Brza provera dostupnosti HTTP kandidata (ne potvrđuje autentičnost paketa)
                            if (ValidateHubEndpoint(hubUrl))
                            {
                                return hubUrl;
                            }
                        }
                    }
                }
                catch
                {
                    // Timeout ili mreza nedostupna na ovom pokusaju
                }
            }

            return null;
        }

        private static string TryDefaultGateway(Action<string> statusCallback)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string gwIp = gw.Address.ToString();
                            if (gwIp == "0.0.0.0") continue;

                            int[] testPorts = new int[] { 8080, 8000 };
                            foreach (int port in testPorts)
                            {
                                string candidateUrl = string.Format("http://{0}:{1}", gwIp, port);
                                if (ValidateHubEndpoint(candidateUrl))
                                {
                                    return candidateUrl;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryIniConfig(Action<string> statusCallback)
        {
            string iniPath = WifiHelper.FindWifiConfigFile();
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return null;

            try
            {
                string[] lines = File.ReadAllLines(iniPath);
                string currentSection = "";
                string serverHosts = "";
                int serverPort = 8080;

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

                        if (currentSection.Equals("ServisHub", StringComparison.OrdinalIgnoreCase))
                        {
                            if (key.Equals("ServerHost", StringComparison.OrdinalIgnoreCase)) serverHosts = val;
                            else if (key.Equals("ServerPort", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out serverPort);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(serverHosts))
                {
                    string[] hosts = serverHosts.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string h in hosts)
                    {
                        string host = h.Trim();
                        if (host.Equals("auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(host))
                            continue;

                        string candidateUrl = string.Format("http://{0}:{1}", host, serverPort);
                        if (ValidateHubEndpoint(candidateUrl))
                        {
                            return candidateUrl;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryProbeHub(string baseUrl)
        {
            if (ValidateHubEndpoint(baseUrl))
                return baseUrl;
            return null;
        }

        /// <summary>
        /// Proverava isključivo mrežnu dostupnost HTTP endpoint kandidata.
        /// Ne garantuje autentičnost servera niti integritet payload-a (za to je obavezan RSA potpis manifesta).
        /// </summary>
        public static bool ValidateHubEndpoint(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;

            try
            {
                Uri parsedUri;
                if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out parsedUri))
                    return false;
                if (!string.Equals(parsedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                    return false;

                string testUrl = baseUrl.TrimEnd('/') + "/api/payload/version";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(testUrl);
                req.Method = "GET";
                req.Timeout = 1500;
                req.ReadWriteTimeout = 1500;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                // Takodje proveravamo fallback na osnovni /api/devices
                try
                {
                    string testUrl2 = baseUrl.TrimEnd('/') + "/api/devices";
                    HttpWebRequest req2 = (HttpWebRequest)WebRequest.Create(testUrl2);
                    req2.Method = "GET";
                    req2.Timeout = 1000;
                    req2.ReadWriteTimeout = 1000;
                    using (HttpWebResponse resp2 = (HttpWebResponse)req2.GetResponse())
                    {
                        return resp2.StatusCode == HttpStatusCode.OK;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
