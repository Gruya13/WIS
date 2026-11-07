using System;
using System.Management;
using Microsoft.Win32;

namespace WIS.AutopilotAuditor
{
    public static class HardwareInspector
    {
        public static HardwareDetails Inspect()
        {
            HardwareDetails hw = new HardwareDetails();

            // 1. Proizvođač i Model
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["Manufacturer"] != null)
                            hw.Manufacturer = obj["Manufacturer"].ToString().Trim();
                        if (obj["Model"] != null)
                            hw.Model = obj["Model"].ToString().Trim();
                    }
                }
            }
            catch { }

            // 2. Serijski broj BIOS-a
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["SerialNumber"] != null)
                        {
                            string s = obj["SerialNumber"].ToString().Trim();
                            if (!string.IsNullOrEmpty(s) && s != "0" && s != "To be filled by O.E.M.")
                                hw.SerialNumber = s;
                        }
                    }
                }
            }
            catch { }

            // 3. SMBIOS UUID računara
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["UUID"] != null)
                            hw.SmbiosUuid = obj["UUID"].ToString().Trim();
                    }
                }
            }
            catch { }

            // 4. TPM 2.0 status
            try
            {
                ManagementScope tpmScope = new ManagementScope(@"root\CIMv2\Security\MicrosoftTpm");
                tpmScope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(tpmScope, new ObjectQuery("SELECT IsActivated_InitialValue, IsEnabled_InitialValue, IsOwned_InitialValue, SpecVersion FROM Win32_Tpm")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        hw.TpmPresent = true;
                        bool activated = false;
                        bool enabled = false;

                        if (obj["IsActivated_InitialValue"] != null)
                            bool.TryParse(obj["IsActivated_InitialValue"].ToString(), out activated);
                        if (obj["IsEnabled_InitialValue"] != null)
                            bool.TryParse(obj["IsEnabled_InitialValue"].ToString(), out enabled);

                        hw.TpmReady = (activated && enabled);

                        if (obj["SpecVersion"] != null)
                            hw.TpmVersion = obj["SpecVersion"].ToString().Trim();
                    }
                }
            }
            catch
            {
                hw.TpmPresent = false;
                hw.TpmReady = false;
                hw.TpmVersion = "Nema TPM čipa";
            }

            // 5. Windows Product ID
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object pId = key.GetValue("ProductId");
                        if (pId != null)
                            hw.WindowsProductId = pId.ToString().Trim();
                    }
                }
            }
            catch { }

            // 6. Autopilot 4K Hardware Hash (DeviceHardwareData)
            try
            {
                ManagementScope mdmScope = new ManagementScope(@"root\cimv2\mdm\dmmap");
                mdmScope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(mdmScope, new ObjectQuery("SELECT DeviceHardwareData FROM MDM_DevDetail_Ext01")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["DeviceHardwareData"] != null)
                        {
                            string hash = obj["DeviceHardwareData"].ToString().Trim();
                            if (!string.IsNullOrEmpty(hash))
                            {
                                hw.HardwareHash = hash;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            return hw;
        }
    }
}
