using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WIS.AutopilotAuditor
{
    public static class LocalForensicsInspector
    {
        public static LocalForensicsDetails Inspect()
        {
            LocalForensicsDetails report = new LocalForensicsDetails();

            // 1. Diagnostics\AutoPilot
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot"))
                {
                    if (key != null)
                    {
                        object tId = key.GetValue("CloudAssignedTenantId");
                        if (tId != null && !string.IsNullOrEmpty(tId.ToString()))
                        {
                            report.HasTraces = true;
                            report.TenantId = tId.ToString().Trim();
                        }

                        object tDom = key.GetValue("CloudAssignedTenantDomain");
                        if (tDom != null && !string.IsNullOrEmpty(tDom.ToString()))
                        {
                            report.HasTraces = true;
                            report.TenantDomain = tDom.ToString().Trim();
                        }

                        object devName = key.GetValue("CloudAssignedDeviceName");
                        if (devName != null && !string.IsNullOrEmpty(devName.ToString()))
                        {
                            report.AssignedDeviceName = devName.ToString().Trim();
                        }

                        object estCorr = key.GetValue("EstablishedCorrelations");
                        if (estCorr != null)
                        {
                            report.HasTraces = true;
                            report.EstablishedCorr = true;
                        }
                    }
                }
            }
            catch { }

            // 2. PolicyJsonCache
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache\PolicyJsonCache"))
                {
                    if (key != null)
                    {
                        report.HasTraces = true;
                        report.PolicyCacheFound = true;
                    }
                }
            }
            catch { }

            // 3. Enrollments (MDM upis)
            try
            {
                using (RegistryKey enrollments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments"))
                {
                    if (enrollments != null)
                    {
                        string[] subKeys = enrollments.GetSubKeyNames();
                        foreach (string sub in subKeys)
                        {
                            try
                            {
                                using (RegistryKey subKey = enrollments.OpenSubKey(sub))
                                {
                                    if (subKey != null)
                                    {
                                        object url = subKey.GetValue("DiscoveryServiceFullURL");
                                        if (url != null && !string.IsNullOrEmpty(url.ToString()))
                                        {
                                            report.HasTraces = true;
                                            string urlStr = url.ToString().Trim();
                                            if (!report.MdmEnrollmentUrls.Contains(urlStr))
                                                report.MdmEnrollmentUrls.Add(urlStr);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // 4. CloudDomainJoin
            try
            {
                using (RegistryKey cdj = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo"))
                {
                    if (cdj != null)
                    {
                        string[] subKeys = cdj.GetSubKeyNames();
                        foreach (string sub in subKeys)
                        {
                            try
                            {
                                using (RegistryKey subKey = cdj.OpenSubKey(sub))
                                {
                                    if (subKey != null)
                                    {
                                        object tId = subKey.GetValue("TenantId");
                                        if (tId != null && !string.IsNullOrEmpty(tId.ToString()))
                                        {
                                            report.HasTraces = true;
                                            report.CloudDomainJoinTenant = tId.ToString().Trim();
                                            if (string.IsNullOrEmpty(report.TenantId))
                                                report.TenantId = report.CloudDomainJoinTenant;
                                        }

                                        object uEmail = subKey.GetValue("UserEmail");
                                        if (uEmail != null && !string.IsNullOrEmpty(uEmail.ToString()))
                                        {
                                            report.CloudDomainJoinUser = uEmail.ToString().Trim();
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // 5. Sertifikati računara (LocalMachine\My)
            try
            {
                using (X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 cert in store.Certificates)
                    {
                        string issuer = cert.Issuer ?? "";
                        if (issuer.IndexOf("Intune MDM Device CA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            issuer.IndexOf("MS-Organization-Access", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            report.HasTraces = true;
                            string certDesc = string.Format("{0} (Izdavač: {1})", cert.Subject, issuer);
                            report.OrgCertificates.Add(certDesc);
                        }
                    }
                    store.Close();
                }
            }
            catch { }

            // 6. dsregcmd /status
            try
            {
                string dsregPath = Path.Combine(Environment.SystemDirectory, "dsregcmd.exe");
                if (File.Exists(dsregPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = dsregPath,
                        Arguments = "/status",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);

                        if (!string.IsNullOrEmpty(output))
                        {
                            report.AzureAdJoined = Regex.IsMatch(output, @"AzureAdJoined\s*:\s*YES", RegexOptions.IgnoreCase);
                            report.MdmEnrolled = Regex.IsMatch(output, @"MdmEnrolled\s*:\s*YES", RegexOptions.IgnoreCase);

                            report.AzureAdStatus = report.AzureAdJoined ? "PRIDRUŽEN (YES)" : "Nije pridružen (NO)";
                            report.MdmStatus = report.MdmEnrolled ? "UPISAN (YES)" : "Nije upisan (NO)";

                            if (report.AzureAdJoined || report.MdmEnrolled)
                            {
                                report.HasTraces = true;
                            }
                        }
                    }
                }
            }
            catch { }

            return report;
        }
    }
}
