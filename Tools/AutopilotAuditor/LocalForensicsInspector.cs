using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WIS.AutopilotAuditor
{
    public static class LocalForensicsInspector
    {
        private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        private static readonly Regex WaadDeviceNameRegex = new Regex(@"\bW-AAD-\d{9}\b", RegexOptions.IgnoreCase);
        private static readonly Regex GuidRegex = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        public static bool IsWaadCorporateDeviceName(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            return WaadDeviceNameRegex.IsMatch(candidate);
        }

        private static bool IsValidNonZeroGuid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string trimmed = value.Trim().Trim('{', '}');
            if (string.Equals(trimmed, ZeroGuid, StringComparison.OrdinalIgnoreCase)) return false;
            return GuidRegex.IsMatch(trimmed);
        }

        private static void AddEvidence(LocalForensicsDetails report, string detail)
        {
            if (report == null || string.IsNullOrEmpty(detail)) return;
            if (!report.EvidenceDetails.Contains(detail))
            {
                report.EvidenceDetails.Add(detail);
            }
        }

        private static void CheckDeviceNameForCorporatePattern(LocalForensicsDetails report, string candidate, string sourceTag)
        {
            if (report == null || string.IsNullOrEmpty(candidate)) return;
            string trimmed = candidate.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            Match waadMatch = WaadDeviceNameRegex.Match(trimmed);
            if (waadMatch.Success)
            {
                report.HasTraces = true;
                report.WaadDeviceNameMatched = true;
                if (string.IsNullOrEmpty(report.MatchedWaadName))
                {
                    report.MatchedWaadName = waadMatch.Value.ToUpperInvariant();
                }
                if (string.IsNullOrEmpty(report.AssignedDeviceName))
                {
                    report.AssignedDeviceName = waadMatch.Value.ToUpperInvariant();
                }
                AddEvidence(report, string.Format("W-AAD obrazac ({0}: {1})", sourceTag, waadMatch.Value.ToUpperInvariant()));
            }
        }

        private static void ExtractProfileHintsFromText(LocalForensicsDetails report, string rawText, string sourceTag)
        {
            if (report == null || string.IsNullOrEmpty(rawText)) return;

            Match mTid = Regex.Match(rawText, "\"CloudAssignedTenantId\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (mTid.Success && IsValidNonZeroGuid(mTid.Groups[1].Value))
            {
                report.HasTraces = true;
                if (string.IsNullOrEmpty(report.TenantId))
                    report.TenantId = mTid.Groups[1].Value.Trim().Trim('{', '}');
                AddEvidence(report, string.Format("TenantId u {0}", sourceTag));
            }

            Match mDom = Regex.Match(rawText, "\"CloudAssignedTenantDomain\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (mDom.Success && !string.IsNullOrEmpty(mDom.Groups[1].Value.Trim()))
            {
                report.HasTraces = true;
                if (string.IsNullOrEmpty(report.TenantDomain))
                    report.TenantDomain = mDom.Groups[1].Value.Trim();
                AddEvidence(report, string.Format("TenantDomain u {0}", sourceTag));
            }

            Match mDev = Regex.Match(rawText, "\"CloudAssignedDeviceName\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (mDev.Success && !string.IsNullOrEmpty(mDev.Groups[1].Value.Trim()))
            {
                string dev = mDev.Groups[1].Value.Trim();
                report.HasTraces = true;
                if (string.IsNullOrEmpty(report.AssignedDeviceName))
                    report.AssignedDeviceName = dev;
                CheckDeviceNameForCorporatePattern(report, dev, sourceTag);
                AddEvidence(report, string.Format("CloudAssignedDeviceName u {0}: {1}", sourceTag, dev));
            }

            CheckDeviceNameForCorporatePattern(report, rawText, sourceTag);
        }

        private static int CountAndScanRegistrySubtree(RegistryKey key, LocalForensicsDetails report, string sourceTag, int maxDepth)
        {
            if (key == null || maxDepth < 0) return 0;
            int foundEntries = 0;

            try
            {
                string[] valueNames = key.GetValueNames();
                for (int i = 0; i < valueNames.Length; i++)
                {
                    string vName = valueNames[i];
                    try
                    {
                        object val = key.GetValue(vName);
                        if (val == null) continue;
                        string strVal = val.ToString().Trim();
                        if (string.IsNullOrEmpty(strVal)) continue;

                        foundEntries++;
                        if (string.Equals(vName, "CloudAssignedTenantId", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(vName, "TenantId", StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsValidNonZeroGuid(strVal))
                            {
                                report.HasTraces = true;
                                if (string.IsNullOrEmpty(report.TenantId))
                                    report.TenantId = strVal.Trim('{', '}');
                            }
                        }
                        else if (string.Equals(vName, "CloudAssignedTenantDomain", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(vName, "TenantDomain", StringComparison.OrdinalIgnoreCase))
                        {
                            report.HasTraces = true;
                            if (string.IsNullOrEmpty(report.TenantDomain))
                                report.TenantDomain = strVal;
                        }
                        else if (string.Equals(vName, "CloudAssignedDeviceName", StringComparison.OrdinalIgnoreCase))
                        {
                            report.HasTraces = true;
                            if (string.IsNullOrEmpty(report.AssignedDeviceName))
                                report.AssignedDeviceName = strVal;
                            CheckDeviceNameForCorporatePattern(report, strVal, sourceTag);
                        }
                        else
                        {
                            ExtractProfileHintsFromText(report, strVal, sourceTag + "\\" + vName);
                        }
                    }
                    catch { }
                }

                if (maxDepth > 0)
                {
                    string[] subNames = key.GetSubKeyNames();
                    for (int i = 0; i < subNames.Length; i++)
                    {
                        string subName = subNames[i];
                        foundEntries++;
                        CheckDeviceNameForCorporatePattern(report, subName, sourceTag);
                        try
                        {
                            using (RegistryKey subKey = key.OpenSubKey(subName))
                            {
                                if (subKey != null)
                                {
                                    foundEntries += CountAndScanRegistrySubtree(subKey, report, sourceTag + "\\" + subName, maxDepth - 1);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return foundEntries;
        }

        public static LocalForensicsDetails Inspect()
        {
            LocalForensicsDetails report = new LocalForensicsDetails();

            // 0. Provera aktivnog i inicijalnog imena računara na korporativni W-AAD-<9 cifara> obrazac
            try
            {
                CheckDeviceNameForCorporatePattern(report, Environment.MachineName, "MachineName");
                string[] hostHintFiles = new string[]
                {
                    @"C:\setup\initial-hostname.txt",
                    @"C:\Windows\Logs\WIS\initial-hostname.txt"
                };
                for (int i = 0; i < hostHintFiles.Length; i++)
                {
                    if (File.Exists(hostHintFiles[i]))
                    {
                        FileInfo fi = new FileInfo(hostHintFiles[i]);
                        if (fi.Length > 0 && fi.Length <= 512)
                        {
                            string rawHost = File.ReadAllText(hostHintFiles[i], Encoding.UTF8).Trim();
                            CheckDeviceNameForCorporatePattern(report, rawHost, "initial-hostname.txt");
                        }
                    }
                }
            }
            catch { }

            // 1. Diagnostics\AutoPilot (uključujući podključ EstablishedCorrelations!)
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot"))
                {
                    if (key != null)
                    {
                        object tId = key.GetValue("CloudAssignedTenantId");
                        if (tId != null && IsValidNonZeroGuid(tId.ToString()))
                        {
                            report.HasTraces = true;
                            report.TenantId = tId.ToString().Trim().Trim('{', '}');
                            AddEvidence(report, "Diagnostics\\AutoPilot\\CloudAssignedTenantId");
                        }

                        object tDom = key.GetValue("CloudAssignedTenantDomain");
                        if (tDom != null && !string.IsNullOrEmpty(tDom.ToString().Trim()))
                        {
                            report.HasTraces = true;
                            report.TenantDomain = tDom.ToString().Trim();
                            AddEvidence(report, "Diagnostics\\AutoPilot\\CloudAssignedTenantDomain");
                        }

                        object devName = key.GetValue("CloudAssignedDeviceName");
                        if (devName != null && !string.IsNullOrEmpty(devName.ToString().Trim()))
                        {
                            string devStr = devName.ToString().Trim();
                            report.HasTraces = true;
                            report.AssignedDeviceName = devStr;
                            CheckDeviceNameForCorporatePattern(report, devStr, "Diagnostics\\AutoPilot\\CloudAssignedDeviceName");
                            AddEvidence(report, "Diagnostics\\AutoPilot\\CloudAssignedDeviceName=" + devStr);
                        }

                        // EstablishedCorrelations je u Windows 10/11 PODKLJUČ (OpenSubKey + nabrajanje podključeva i vrednosti)
                        using (RegistryKey estSubKey = key.OpenSubKey("EstablishedCorrelations"))
                        {
                            if (estSubKey != null)
                            {
                                int corrEntries = CountAndScanRegistrySubtree(estSubKey, report, "EstablishedCorrelations", 2);
                                if (corrEntries > 0)
                                {
                                    report.HasTraces = true;
                                    report.EstablishedCorr = true;
                                    report.EstablishedCorrelationsCount = corrEntries;
                                    AddEvidence(report, string.Format("EstablishedCorrelations podključ ({0} unosa)", corrEntries));
                                }
                            }
                        }

                        // Sekundarni fallback u slučaju vrednosti istog imena
                        if (!report.EstablishedCorr)
                        {
                            object estVal = key.GetValue("EstablishedCorrelations");
                            if (estVal != null && !string.IsNullOrEmpty(estVal.ToString().Trim()))
                            {
                                report.HasTraces = true;
                                report.EstablishedCorr = true;
                                report.EstablishedCorrelationsCount = 1;
                                AddEvidence(report, "EstablishedCorrelations vrednost");
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. AutopilotPolicyCache (PolicyJsonCache je VREDNOST unutar AutopilotPolicyCache + provera svih vrednosti i podključeva)
            try
            {
                using (RegistryKey cacheKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
                {
                    if (cacheKey != null)
                    {
                        int cacheCount = 0;
                        object policyJsonVal = cacheKey.GetValue("PolicyJsonCache");
                        if (policyJsonVal != null && !string.IsNullOrEmpty(policyJsonVal.ToString().Trim()))
                        {
                            string policyStr = policyJsonVal.ToString().Trim();
                            report.HasTraces = true;
                            report.PolicyCacheFound = true;
                            cacheCount++;
                            ExtractProfileHintsFromText(report, policyStr, "AutopilotPolicyCache\\PolicyJsonCache");
                            AddEvidence(report, "AutopilotPolicyCache\\PolicyJsonCache vrednost");
                        }

                        int subtreeEntries = CountAndScanRegistrySubtree(cacheKey, report, "AutopilotPolicyCache", 2);
                        if (subtreeEntries > 0)
                        {
                            report.HasTraces = true;
                            report.PolicyCacheFound = true;
                            if (subtreeEntries > cacheCount) cacheCount = subtreeEntries;
                            AddEvidence(report, string.Format("AutopilotPolicyCache ({0} unosa)", cacheCount));
                        }
                        report.PolicyCacheEntriesCount = cacheCount;
                    }
                }
            }
            catch { }

            // 2b. AutopilotSettings (HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotSettings)
            try
            {
                using (RegistryKey settingsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotSettings"))
                {
                    if (settingsKey != null)
                    {
                        int settingsEntries = CountAndScanRegistrySubtree(settingsKey, report, "AutopilotSettings", 2);
                        if (settingsEntries > 0)
                        {
                            report.AutopilotSettingsFound = true;
                            report.HasTraces = true;
                            AddEvidence(report, string.Format("AutopilotSettings ({0} unosa)", settingsEntries));
                        }
                    }
                }
            }
            catch { }

            // 2c. EnterpriseAssignedAccess
            try
            {
                using (RegistryKey eaaKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\EnterpriseAssignedAccess"))
                {
                    if (eaaKey != null)
                    {
                        int eaaEntries = CountAndScanRegistrySubtree(eaaKey, report, "EnterpriseAssignedAccess", 2);
                        if (eaaEntries > 0)
                        {
                            report.EnterpriseAssignedAccessFound = true;
                            report.HasTraces = true;
                            AddEvidence(report, string.Format("EnterpriseAssignedAccess ({0} unosa)", eaaEntries));
                        }
                    }
                }
            }
            catch { }

            // 2d. EnrollmentStatusTracking (ESP / DevicePreparation / DeviceSetup)
            try
            {
                string[] espTrackingPaths = new string[]
                {
                    @"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking",
                    @"SOFTWARE\Microsoft\Provisioning\EnrollmentStatusTracking",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeviceManagement\EnrollmentStatusTracking",
                    @"SOFTWARE\Microsoft\Enrollments\Status"
                };

                for (int i = 0; i < espTrackingPaths.Length; i++)
                {
                    try
                    {
                        using (RegistryKey espKey = Registry.LocalMachine.OpenSubKey(espTrackingPaths[i]))
                        {
                            if (espKey != null)
                            {
                                int espEntries = CountAndScanRegistrySubtree(espKey, report, "EnrollmentStatusTracking", 2);
                                if (espEntries > 0)
                                {
                                    report.EnrollmentStatusTrackingFound = true;
                                    report.HasTraces = true;
                                    AddEvidence(report, string.Format("EnrollmentStatusTracking ({0})", espTrackingPaths[i]));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Enrollments (MDM upis i ZTD unosi)
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
                                if (sub.IndexOf("ZTD", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    report.HasTraces = true;
                                    AddEvidence(report, "Enrollments\\" + sub);
                                }
                                using (RegistryKey subKey = enrollments.OpenSubKey(sub))
                                {
                                    if (subKey != null)
                                    {
                                        object url = subKey.GetValue("DiscoveryServiceFullURL");
                                        if (url != null && !string.IsNullOrEmpty(url.ToString().Trim()))
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
                                        if (tId != null && IsValidNonZeroGuid(tId.ToString()))
                                        {
                                            report.HasTraces = true;
                                            report.CloudDomainJoinTenant = tId.ToString().Trim().Trim('{', '}');
                                            if (string.IsNullOrEmpty(report.TenantId))
                                                report.TenantId = report.CloudDomainJoinTenant;
                                            AddEvidence(report, "CloudDomainJoin\\JoinInfo\\TenantId");
                                        }

                                        object uEmail = subKey.GetValue("UserEmail");
                                        if (uEmail != null && !string.IsNullOrEmpty(uEmail.ToString().Trim()))
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
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            try { proc.BeginErrorReadLine(); } catch { }
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
            }
            catch { }

            return report;
        }
    }
}
