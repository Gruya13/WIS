using System;
using System.Collections.Generic;
using System.Text;

namespace WIS.AutopilotAuditor
{
    public enum MatrixStatus
    {
        LOCKED_TENANT,
        HIDDEN_LOCK,
        RELEASED_LOCAL_TRACES,
        CONFIRMED_CLEAN,
        UNVERIFIED_LOCAL_TRACES,
        UNVERIFIED_CLEAN,
        OFFLINE_LOCAL_TRACES,
        OFFLINE_CLEAN_LOCAL,
        UNKNOWN
    }

    public enum StatusSeverity
    {
        CRITICAL,
        WARNING,
        OK,
        INFO,
        ERROR
    }

    public class HardwareDetails
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string SmbiosUuid { get; set; }
        public bool TpmPresent { get; set; }
        public bool TpmReady { get; set; }
        public string TpmVersion { get; set; }
        public string WindowsProductId { get; set; }
        public string HardwareHash { get; set; }

        public HardwareDetails()
        {
            Manufacturer = "Nepoznato";
            Model = "Nepoznato";
            SerialNumber = "Nepoznato";
            SmbiosUuid = "Nepoznato";
            TpmPresent = false;
            TpmReady = false;
            TpmVersion = "N/A";
            WindowsProductId = "";
            HardwareHash = "";
        }
    }

    public class LocalForensicsDetails
    {
        public bool HasTraces { get; set; }
        public string TenantId { get; set; }
        public string TenantDomain { get; set; }
        public string AssignedDeviceName { get; set; }
        public bool PolicyCacheFound { get; set; }
        public bool EstablishedCorr { get; set; }
        public bool AzureAdJoined { get; set; }
        public bool MdmEnrolled { get; set; }
        public string AzureAdStatus { get; set; }
        public string MdmStatus { get; set; }
        public List<string> MdmEnrollmentUrls { get; set; }
        public List<string> OrgCertificates { get; set; }
        public string CloudDomainJoinTenant { get; set; }
        public string CloudDomainJoinUser { get; set; }

        public LocalForensicsDetails()
        {
            HasTraces = false;
            TenantId = "";
            TenantDomain = "";
            AssignedDeviceName = "";
            PolicyCacheFound = false;
            EstablishedCorr = false;
            AzureAdJoined = false;
            MdmEnrolled = false;
            AzureAdStatus = "Nije pridružen (NO)";
            MdmStatus = "Nije upisan (NO)";
            MdmEnrollmentUrls = new List<string>();
            OrgCertificates = new List<string>();
            CloudDomainJoinTenant = "";
            CloudDomainJoinUser = "";
        }
    }

    public class CloudZtdDetails
    {
        public bool OnlineChecked { get; set; }
        public bool IsRegistered { get; set; }
        public bool ConfirmedFree { get; set; }
        public string QueryMode { get; set; }
        public string ErrorCode { get; set; }
        public string RawStatusMessage { get; set; }
        public string ProfileTenantId { get; set; }
        public string ProfileTenantDomain { get; set; }
        public string ProfileDeviceName { get; set; }
        public string GdsServerUrl { get; set; }

        public CloudZtdDetails()
        {
            OnlineChecked = false;
            IsRegistered = false;
            ConfirmedFree = false;
            QueryMode = "NONE";
            ErrorCode = "";
            RawStatusMessage = "Nije provereno";
            ProfileTenantId = "";
            ProfileTenantDomain = "";
            ProfileDeviceName = "";
            GdsServerUrl = "";
        }
    }

    public class AuditorResult
    {
        public string Timestamp { get; set; }
        public MatrixStatus MatrixCode { get; set; }
        public string MatrixCodeString { get { return MatrixCode.ToString(); } }
        public StatusSeverity StatusLevel { get; set; }
        public string StatusLevelString { get { return StatusLevel.ToString(); } }
        public string StatusTitle { get; set; }
        public string StatusDescription { get; set; }
        public string RecommendedAction { get; set; }
        public bool IsLocked { get; set; }
        public string TenantDomain { get; set; }
        public string TenantId { get; set; }
        public HardwareDetails Hardware { get; set; }
        public LocalForensicsDetails LocalForensics { get; set; }
        public CloudZtdDetails CloudZtd { get; set; }

        public AuditorResult()
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            MatrixCode = MatrixStatus.UNKNOWN;
            StatusLevel = StatusSeverity.INFO;
            StatusTitle = "NEPOZNATO STANJE";
            StatusDescription = "Analiza nije pokrenuta.";
            RecommendedAction = "Pokrenite proveru radi dobijanja rezultata.";
            IsLocked = false;
            TenantDomain = "";
            TenantId = "";
            Hardware = new HardwareDetails();
            LocalForensics = new LocalForensicsDetails();
            CloudZtd = new CloudZtdDetails();
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.AppendFormat("  \"timestamp\": \"{0}\",\n", EscapeJson(Timestamp));
            sb.AppendFormat("  \"matrix_code\": \"{0}\",\n", MatrixCodeString);
            sb.AppendFormat("  \"status_level\": \"{0}\",\n", StatusLevelString);
            sb.AppendFormat("  \"status_title\": \"{0}\",\n", EscapeJson(StatusTitle));
            sb.AppendFormat("  \"status_description\": \"{0}\",\n", EscapeJson(StatusDescription));
            sb.AppendFormat("  \"recommended_action\": \"{0}\",\n", EscapeJson(RecommendedAction));
            sb.AppendFormat("  \"is_locked\": {0},\n", IsLocked ? "true" : "false");
            sb.AppendFormat("  \"tenant_domain\": \"{0}\",\n", EscapeJson(TenantDomain));
            sb.AppendFormat("  \"tenant_id\": \"{0}\",\n", EscapeJson(TenantId));
            sb.AppendFormat("  \"device_serial\": \"{0}\",\n", EscapeJson(Hardware.SerialNumber));
            sb.AppendFormat("  \"device_model\": \"{0}\",\n", EscapeJson(Hardware.Model));

            // Hardware
            sb.Append("  \"hardware\": {\n");
            sb.AppendFormat("    \"manufacturer\": \"{0}\",\n", EscapeJson(Hardware.Manufacturer));
            sb.AppendFormat("    \"model\": \"{0}\",\n", EscapeJson(Hardware.Model));
            sb.AppendFormat("    \"serial_number\": \"{0}\",\n", EscapeJson(Hardware.SerialNumber));
            sb.AppendFormat("    \"smbios_uuid\": \"{0}\",\n", EscapeJson(Hardware.SmbiosUuid));
            sb.AppendFormat("    \"tpm_present\": {0},\n", Hardware.TpmPresent ? "true" : "false");
            sb.AppendFormat("    \"tpm_ready\": {0},\n", Hardware.TpmReady ? "true" : "false");
            sb.AppendFormat("    \"tpm_version\": \"{0}\",\n", EscapeJson(Hardware.TpmVersion));
            sb.AppendFormat("    \"has_hardware_hash\": {0}\n", !string.IsNullOrEmpty(Hardware.HardwareHash) ? "true" : "false");
            sb.Append("  },\n");

            // Local forensics
            sb.Append("  \"local_forensics\": {\n");
            sb.AppendFormat("    \"has_traces\": {0},\n", LocalForensics.HasTraces ? "true" : "false");
            sb.AppendFormat("    \"tenant_domain\": \"{0}\",\n", EscapeJson(LocalForensics.TenantDomain));
            sb.AppendFormat("    \"tenant_id\": \"{0}\",\n", EscapeJson(LocalForensics.TenantId));
            sb.AppendFormat("    \"assigned_device_name\": \"{0}\",\n", EscapeJson(LocalForensics.AssignedDeviceName));
            sb.AppendFormat("    \"policy_cache_found\": {0},\n", LocalForensics.PolicyCacheFound ? "true" : "false");
            sb.AppendFormat("    \"established_corr\": {0},\n", LocalForensics.EstablishedCorr ? "true" : "false");
            sb.AppendFormat("    \"azure_ad_joined\": {0},\n", LocalForensics.AzureAdJoined ? "true" : "false");
            sb.AppendFormat("    \"mdm_enrolled\": {0},\n", LocalForensics.MdmEnrolled ? "true" : "false");
            sb.AppendFormat("    \"azure_ad_status\": \"{0}\",\n", EscapeJson(LocalForensics.AzureAdStatus));
            sb.AppendFormat("    \"mdm_status\": \"{0}\",\n", EscapeJson(LocalForensics.MdmStatus));
            sb.AppendFormat("    \"org_certificates_count\": {0},\n", LocalForensics.OrgCertificates.Count);

            sb.Append("    \"org_certificates\": [");
            for (int i = 0; i < LocalForensics.OrgCertificates.Count; i++)
            {
                sb.AppendFormat("\"{0}\"{1}", EscapeJson(LocalForensics.OrgCertificates[i]), i < LocalForensics.OrgCertificates.Count - 1 ? ", " : "");
            }
            sb.Append("],\n");

            sb.Append("    \"mdm_enrollment_urls\": [");
            for (int i = 0; i < LocalForensics.MdmEnrollmentUrls.Count; i++)
            {
                sb.AppendFormat("\"{0}\"{1}", EscapeJson(LocalForensics.MdmEnrollmentUrls[i]), i < LocalForensics.MdmEnrollmentUrls.Count - 1 ? ", " : "");
            }
            sb.Append("]\n");
            sb.Append("  },\n");

            // Cloud ZTD
            sb.Append("  \"cloud_ztd\": {\n");
            sb.AppendFormat("    \"online_checked\": {0},\n", CloudZtd.OnlineChecked ? "true" : "false");
            sb.AppendFormat("    \"is_registered\": {0},\n", CloudZtd.IsRegistered ? "true" : "false");
            sb.AppendFormat("    \"confirmed_free\": {0},\n", CloudZtd.ConfirmedFree ? "true" : "false");
            sb.AppendFormat("    \"query_mode\": \"{0}\",\n", EscapeJson(CloudZtd.QueryMode));
            sb.AppendFormat("    \"error_code\": \"{0}\",\n", EscapeJson(CloudZtd.ErrorCode));
            sb.AppendFormat("    \"raw_status_message\": \"{0}\",\n", EscapeJson(CloudZtd.RawStatusMessage));
            sb.AppendFormat("    \"profile_tenant_domain\": \"{0}\",\n", EscapeJson(CloudZtd.ProfileTenantDomain));
            sb.AppendFormat("    \"profile_tenant_id\": \"{0}\",\n", EscapeJson(CloudZtd.ProfileTenantId));
            sb.AppendFormat("    \"profile_device_name\": \"{0}\"\n", EscapeJson(CloudZtd.ProfileDeviceName));
            sb.Append("  }\n");

            sb.Append("}\n");
            return sb.ToString();
        }

        private static string EscapeJson(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
