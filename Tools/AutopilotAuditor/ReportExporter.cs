using System;
using System.IO;
using System.Text;

namespace WIS.AutopilotAuditor
{
    public static class ReportExporter
    {
        public static void ExportJson(AuditorResult result, string filePath)
        {
            string json = result.ToJson();
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        public static void ExportHardwareHashCsv(AuditorResult result, string filePath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Serial Number,Windows Product ID,Hardware Hash");
            string serial = !string.IsNullOrEmpty(result.Hardware.SerialNumber) ? result.Hardware.SerialNumber : "Unknown";
            string pId = !string.IsNullOrEmpty(result.Hardware.WindowsProductId) ? result.Hardware.WindowsProductId : "";
            string hash = !string.IsNullOrEmpty(result.Hardware.HardwareHash) ? result.Hardware.HardwareHash : "";
            sb.AppendFormat("\"{0}\",\"{1}\",\"{2}\"\n", serial, pId, hash);
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        public static void ExportHtml(AuditorResult result, string filePath)
        {
            string statusColor = "#27ae60"; // Zelena
            string badgeText = "100% ČISTO / SLOBODAN";

            switch (result.StatusLevel)
            {
                case StatusSeverity.CRITICAL:
                    statusColor = "#e74c3c"; // Crvena
                    badgeText = result.MatrixCode == MatrixStatus.LOCKED_TENANT ? "ZAKLJUČAN U ORGANIZACIJI" : "SKRIVENA OPASNOST (ZAKLJUČAN U OBLAKU)";
                    break;
                case StatusSeverity.WARNING:
                    statusColor = "#f39c12"; // Žuta/Narandžasta
                    badgeText = "UPOZORENJE / LOKALNI TRAGOVI";
                    break;
                case StatusSeverity.INFO:
                    statusColor = "#2980b9"; // Plava
                    badgeText = "USLOVNO ČISTO (OFFLINE / UNVERIFIED)";
                    break;
            }

            StringBuilder html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html lang=\"sr\">");
            html.AppendLine("<head>");
            html.AppendLine("  <meta charset=\"UTF-8\">");
            html.AppendLine("  <title>Microsoft Autopilot Forensic Auditor - Izveštaj</title>");
            html.AppendLine("  <style>");
            html.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; }");
            html.AppendLine("    body { background-color: #0f172a; color: #f8fafc; padding: 30px; }");
            html.AppendLine("    .container { max-width: 900px; margin: 0 auto; background: #1e293b; border-radius: 12px; border: 1px solid #334155; box-shadow: 0 10px 25px rgba(0,0,0,0.5); overflow: hidden; }");
            html.AppendLine("    .header { background: #0f172a; padding: 25px; border-bottom: 2px solid #334155; display: flex; justify-content: space-between; align-items: center; }");
            html.AppendLine("    .header h1 { font-size: 20px; color: #38bdf8; text-transform: uppercase; letter-spacing: 1px; }");
            html.AppendLine("    .header .timestamp { font-size: 13px; color: #94a3b8; }");
            html.AppendFormat("    .hero-banner {{ background: {0}22; border-left: 6px solid {0}; padding: 20px; margin: 25px; border-radius: 6px; }}\n", statusColor);
            html.AppendFormat("    .hero-title {{ font-size: 18px; font-weight: bold; color: {0}; margin-bottom: 8px; }}\n", statusColor);
            html.AppendLine("    .hero-desc { font-size: 14px; color: #cbd5e1; line-height: 1.5; margin-bottom: 12px; }");
            html.AppendFormat("    .hero-rec {{ font-size: 13px; font-weight: bold; background: {0}33; color: #ffffff; padding: 10px; border-radius: 4px; border: 1px solid {0}66; }}\n", statusColor);
            html.AppendLine("    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; padding: 0 25px 25px 25px; }");
            html.AppendLine("    .card { background: #0f172a; border: 1px solid #334155; border-radius: 8px; padding: 18px; }");
            html.AppendLine("    .card-title { font-size: 15px; font-weight: bold; color: #38bdf8; border-bottom: 1px solid #334155; padding-bottom: 8px; margin-bottom: 12px; }");
            html.AppendLine("    .row { display: flex; justify-content: space-between; padding: 6px 0; border-bottom: 1px solid #1e293b; font-size: 13px; }");
            html.AppendLine("    .row:last-child { border-bottom: none; }");
            html.AppendLine("    .label { color: #94a3b8; }");
            html.AppendLine("    .value { color: #f1f5f9; font-weight: 500; text-align: right; word-break: break-all; max-width: 60%; }");
            html.AppendLine("    .badge { display: inline-block; padding: 4px 10px; border-radius: 4px; font-size: 11px; font-weight: bold; }");
            html.AppendLine("    .footer { background: #0f172a; padding: 15px 25px; text-align: center; font-size: 12px; color: #64748b; border-top: 1px solid #334155; }");
            html.AppendLine("  </style>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");
            html.AppendLine("  <div class=\"container\">");
            html.AppendLine("    <div class=\"header\">");
            html.AppendLine("      <div>");
            html.AppendLine("        <h1>🛡️ Microsoft Autopilot Forensic Auditor</h1>");
            html.AppendLine("        <div style=\"font-size:12px; color:#64748b; margin-top:3px;\">Tehno kutak WIS Service Suite</div>");
            html.AppendLine("      </div>");
            html.AppendFormat("      <div class=\"timestamp\">Datum i vreme: {0}</div>\n", result.Timestamp);
            html.AppendLine("    </div>");

            // Hero banner
            html.AppendLine("    <div class=\"hero-banner\">");
            html.AppendFormat("      <div class=\"hero-title\">{0}</div>\n", result.StatusTitle);
            html.AppendFormat("      <div class=\"hero-desc\">{0}</div>\n", result.StatusDescription);
            html.AppendFormat("      <div class=\"hero-rec\"><strong>PREPORUKA SERVISA:</strong> {0}</div>\n", result.RecommendedAction);
            html.AppendLine("    </div>");

            // Grid kartice
            html.AppendLine("    <div class=\"grid\">");

            // Kartica 1: Oblak i Organizacija
            html.AppendLine("      <div class=\"card\">");
            html.AppendLine("        <div class=\"card-title\">☁️ Status u Microsoft Oblaku & Organizacija</div>");
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Autopilot Status:</span><span class=\"value\" style=\"color:{0};font-weight:bold;\">{1}</span></div>\n", statusColor, badgeText);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Pripada Tenantu:</span><span class=\"value\">{0}</span></div>\n", !string.IsNullOrEmpty(result.TenantDomain) ? result.TenantDomain : "Nema (Nije dodeljen)");
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Tenant GUID:</span><span class=\"value\">{0}</span></div>\n", !string.IsNullOrEmpty(result.TenantId) ? result.TenantId : "N/A");
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">ZTD Online Provera:</span><span class=\"value\">{0}</span></div>\n", result.CloudZtd.OnlineChecked ? "Završena online" : "OFFLINE");
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Metod analize:</span><span class=\"value\">{0}</span></div>\n", result.CloudZtd.QueryMode);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Poruka servisa:</span><span class=\"value\">{0}</span></div>\n", result.CloudZtd.RawStatusMessage);
            html.AppendLine("      </div>");

            // Kartica 2: Hardverski profil
            html.AppendLine("      <div class=\"card\">");
            html.AppendLine("        <div class=\"card-title\">💻 Hardverski Otisak & TPM</div>");
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Proizvođač:</span><span class=\"value\">{0}</span></div>\n", result.Hardware.Manufacturer);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Model računara:</span><span class=\"value\">{0}</span></div>\n", result.Hardware.Model);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Serijski broj:</span><span class=\"value\">{0}</span></div>\n", result.Hardware.SerialNumber);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">SMBIOS UUID:</span><span class=\"value\">{0}</span></div>\n", result.Hardware.SmbiosUuid);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">TPM 2.0 status:</span><span class=\"value\">{0}</span></div>\n", result.Hardware.TpmPresent && result.Hardware.TpmReady ? "Spreman (Aktivan)" : (result.Hardware.TpmPresent ? "Prisutan (Nije spreman)" : "Nije detektovan"));
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Azure AD (Entra ID):</span><span class=\"value\">{0}</span></div>\n", result.LocalForensics.AzureAdStatus);
            html.AppendFormat("        <div class=\"row\"><span class=\"label\">Intune MDM upis:</span><span class=\"value\">{0}</span></div>\n", result.LocalForensics.MdmStatus);
            html.AppendLine("      </div>");

            html.AppendLine("    </div>");

            // Footer
            html.AppendLine("    <div class=\"footer\">");
            html.AppendLine("      Generisano putem samostalnog alata: <strong>WIS-AutopilotAuditor Standalone v2.0</strong> &bull; Tehno kutak");
            html.AppendLine("    </div>");

            html.AppendLine("  </div>");
            html.AppendLine("</body>");
            html.AppendLine("</html>");

            File.WriteAllText(filePath, html.ToString(), Encoding.UTF8);
        }

        public static string GetFormattedConsoleText(AuditorResult res)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("               MICROSOFT AUTOPILOT FORENSIC AUDITOR v2.0                        ");
            sb.AppendLine("                 Samostalni izvršni alat (Standalone EXE)                       ");
            sb.AppendLine("================================================================================");
            sb.AppendFormat("Vreme provere:   {0}\n", res.Timestamp);
            sb.AppendFormat("Računar:         {0} {1}\n", res.Hardware.Manufacturer, res.Hardware.Model);
            sb.AppendFormat("Serijski broj:   {0}\n", res.Hardware.SerialNumber);
            sb.AppendFormat("SMBIOS UUID:     {0}\n", res.Hardware.SmbiosUuid);
            sb.AppendFormat("TPM 2.0:         {0}\n", res.Hardware.TpmPresent && res.Hardware.TpmReady ? "DA (Spreman)" : (res.Hardware.TpmPresent ? "DA (Nije inicijalizovan)" : "NE"));
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendFormat("Lokalni tragovi: {0}\n", res.LocalForensics.HasTraces ? "PRONAĐENI (Registar/Sertifikati)" : "ČISTO (Nema zapisa)");
            if (!string.IsNullOrEmpty(res.TenantDomain))
                sb.AppendFormat("Organizacija:    {0} ({1})\n", res.TenantDomain, res.TenantId);
            sb.AppendFormat("Azure AD / MDM:  {0} | MDM: {1}\n", res.LocalForensics.AzureAdStatus, res.LocalForensics.MdmStatus);
            sb.AppendFormat("Cloud ZTD upit:  {0} [{1}]\n", res.CloudZtd.OnlineChecked ? "ONLINE" : "OFFLINE", res.CloudZtd.QueryMode);
            sb.AppendFormat("Poruka servera:  {0}\n", res.CloudZtd.RawStatusMessage);
            sb.AppendLine("================================================================================");
            sb.AppendFormat("KONAČNA PRESUDA: {0}\n", res.StatusTitle);
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendFormat("Opis:            {0}\n", res.StatusDescription);
            sb.AppendFormat("Preporuka:       {0}\n", res.RecommendedAction);
            sb.AppendLine("================================================================================");
            return sb.ToString();
        }
    }
}
