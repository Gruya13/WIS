using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WIS.UI
{
    public partial class MainWindow
    {
        public void RefreshCategories(HwCategory categories)
        {
            if ((categories & HwCategory.Cpu) != 0) DetectCpu();
            if ((categories & HwCategory.Memory) != 0) DetectMemory();
            if ((categories & HwCategory.Storage) != 0) DetectStorage();
            if ((categories & HwCategory.Battery) != 0) DetectBattery();
            if ((categories & HwCategory.Gpu) != 0) DetectGpu();
            if ((categories & HwCategory.Display) != 0) DetectDisplay();
            if ((categories & HwCategory.SystemIdentity) != 0) DetectSystemIdentity();
            if ((categories & HwCategory.Peripherals) != 0) DetectPeripherals();
        }

        public void DetectCpu()
        {
            try
            {
                string cpuName = "";
                uint cores = 0;
                uint threads = 0;
                uint maxClock = 0;
                string socket = "N/A";

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            if (obj["Name"] != null) cpuName = obj["Name"].ToString();
                            if (obj["NumberOfCores"] != null) uint.TryParse(obj["NumberOfCores"].ToString(), out cores);
                            if (obj["NumberOfLogicalProcessors"] != null) uint.TryParse(obj["NumberOfLogicalProcessors"].ToString(), out threads);
                            if (obj["MaxClockSpeed"] != null) uint.TryParse(obj["MaxClockSpeed"].ToString(), out maxClock);
                            if (obj["SocketDesignation"] != null) socket = obj["SocketDesignation"].ToString().Trim();
                            break;
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(cpuName) || cpuName.StartsWith("Greška") || cpuName == "Nepoznat Procesor")
                {
                    try
                    {
                        using (RegistryKey cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                        {
                            if (cpuKey != null)
                            {
                                object pName = cpuKey.GetValue("ProcessorNameString");
                                if (pName != null) cpuName = pName.ToString();

                                if (maxClock == 0)
                                {
                                    object mhz = cpuKey.GetValue("~MHz");
                                    if (mhz != null) uint.TryParse(mhz.ToString(), out maxClock);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(cpuName)) cpuName = "Standardni x86/x64 Procesor";
                cpuName = Regex.Replace(cpuName, @"\s+", " ").Trim();

                if (threads == 0) threads = (uint)Environment.ProcessorCount;
                if (cores == 0) cores = threads > 1 ? threads / 2 : 1;
                if (string.IsNullOrEmpty(socket)) socket = "N/A";

                string clockStr = maxClock > 0
                    ? string.Format("{0:0.00} GHz (Max)", maxClock / 1000.0)
                    : "N/A";

                string coresThreadsStr = (cores > 0 || threads > 0)
                    ? string.Format("{0} Jezgara / {1} Niti", cores, threads)
                    : "N/A";

                string finalCpuName = cpuName;
                string finalCores = coresThreadsStr;
                string finalClock = clockStr;
                string finalSocket = socket;

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.CpuModel = finalCpuName;
                        ViewModel.HardwareInfo.CpuCoresThreads = finalCores;
                        ViewModel.HardwareInfo.CpuClock = finalClock;
                        ViewModel.HardwareInfo.CpuSocket = finalSocket;
                    }

                    HardwareIdentityService.TryUpdate("CpuInfo", finalCpuName, HardwareUpdateSource.WmiFresh, ViewModel);
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.CpuModel = "Greška pri detekciji CPU";
                    }
                });
            }
        }

        public void DetectMemory()
        {
            try
            {
                ulong kernelTotalBytes = 0;
                try
                {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        kernelTotalBytes = memStatus.ullTotalPhys;
                    }
                }
                catch { }

                ulong totalBytes = 0;
                int moduleCount = 0;
                uint maxSpeed = 0;
                string ramType = "DDR";
                bool isSoldered = false;
                List<string> locators = new List<string>();

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, MemoryType, SMBIOSMemoryType, FormFactor, DeviceLocator, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            moduleCount++;

                            if (obj["Capacity"] != null)
                            {
                                ulong cap = 0;
                                if (ulong.TryParse(obj["Capacity"].ToString(), out cap))
                                {
                                    totalBytes += cap;
                                }
                            }

                            if (obj["Speed"] != null)
                            {
                                uint sp = 0;
                                if (uint.TryParse(obj["Speed"].ToString(), out sp))
                                {
                                    if (sp > maxSpeed) maxSpeed = sp;
                                }
                            }

                            uint smbiosType = 0;
                            if (obj["SMBIOSMemoryType"] != null)
                            {
                                uint.TryParse(obj["SMBIOSMemoryType"].ToString(), out smbiosType);
                            }

                            uint formFactor = 999;
                            if (obj["FormFactor"] != null)
                            {
                                uint.TryParse(obj["FormFactor"].ToString(), out formFactor);
                            }

                            string locator = obj["DeviceLocator"] != null ? obj["DeviceLocator"].ToString() : "";
                            string partNum = obj["PartNumber"] != null ? obj["PartNumber"].ToString() : "";

                            if (!string.IsNullOrEmpty(locator))
                            {
                                locators.Add(locator);
                            }

                            string detectedType = "";
                            if (smbiosType == 26) detectedType = "DDR4";
                            else if (smbiosType == 34) detectedType = "DDR5";
                            else if (smbiosType == 30) detectedType = "LPDDR4";
                            else if (smbiosType == 35) detectedType = "LPDDR5";
                            else if (smbiosType == 24) detectedType = "DDR3";

                            if (!string.IsNullOrEmpty(detectedType))
                            {
                                ramType = detectedType;
                            }

                            bool hasLpddr = ramType.IndexOf("LPDDR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            partNum.IndexOf("LPDDR", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool locatorOnBoard = locator.IndexOf("On-board", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  locator.IndexOf("Embedded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                  locator.IndexOf("Soldered", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (smbiosType == 35 || (smbiosType == 34 && formFactor == 0) || hasLpddr || formFactor == 0 || locatorOnBoard)
                            {
                                isSoldered = true;
                            }
                        }
                    }
                }
                catch { }

                if (totalBytes == 0 && kernelTotalBytes > 0)
                {
                    totalBytes = kernelTotalBytes;
                }

                int totalSlots = 0;
                try
                {
                    using (ManagementObjectSearcher searcherArray = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                    {
                        foreach (ManagementObject obj in searcherArray.Get())
                        {
                            if (obj["MemoryDevices"] != null)
                            {
                                int dev = 0;
                                if (int.TryParse(obj["MemoryDevices"].ToString(), out dev))
                                {
                                    totalSlots += dev;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (totalSlots < moduleCount)
                {
                    totalSlots = moduleCount;
                }
                if (totalSlots == 0 && moduleCount == 0 && totalBytes > 0)
                {
                    totalSlots = 1;
                    moduleCount = 1;
                }

                int freeSlots = totalSlots >= moduleCount ? (totalSlots - moduleCount) : 0;

                string ramSlotsSummaryStr = "";
                if (isSoldered && (totalSlots == 0 || freeSlots == 0))
                {
                    ramSlotsSummaryStr = "Integrisana na ploči (Bez slotova)";
                }
                else if (isSoldered && freeSlots > 0)
                {
                    ramSlotsSummaryStr = string.Format("{0} od {1} zauzeto (Zalemljeno + slobodan SO-DIMM)", moduleCount, totalSlots);
                }
                else
                {
                    if (freeSlots > 0)
                    {
                        ramSlotsSummaryStr = string.Format("{0} od {1} zauzeto ({2} slobodan za nadogradnju)", moduleCount, totalSlots, freeSlots);
                    }
                    else
                    {
                        ramSlotsSummaryStr = string.Format("{0} od {1} zauzeto (Popunjeno)", moduleCount, totalSlots);
                    }
                }

                double totalGb = Math.Round((double)totalBytes / (1024.0 * 1024.0 * 1024.0));
                string ramSummaryStr = string.Format("{0} GB RAM ({1})", totalGb, ramType);
                string speedStr = maxSpeed > 0 ? string.Format("{0} MHz", maxSpeed) : "N/A";
                string detailsStr = moduleCount > 1
                    ? string.Format("{0} modula ({1})", moduleCount, string.Join(", ", locators.ToArray()))
                    : (isSoldered ? "Integrisana na matičnoj ploči" : string.Format("1 modul ({0})", locators.Count > 0 ? locators[0] : "Slot 1"));

                string solderedBadgeStr = isSoldered ? "Integrisan (LPDDR / Soldered)" : "Standardni moduli (SO-DIMM)";

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.RamSummary = ramSummaryStr;
                        ViewModel.HardwareInfo.RamDetails = detailsStr;
                        ViewModel.HardwareInfo.RamSpeed = speedStr;
                        ViewModel.HardwareInfo.RamType = ramType;
                        ViewModel.HardwareInfo.IsRamSoldered = isSoldered;
                        ViewModel.HardwareInfo.RefreshLocalization();
                        ViewModel.HardwareInfo.RamSlotsSummary = ramSlotsSummaryStr;
                        ViewModel.HardwareInfo.RamTotalSlots = totalSlots;
                        ViewModel.HardwareInfo.RamUsedSlots = moduleCount;
                        ViewModel.HardwareInfo.RamFreeSlots = freeSlots;
                    }

                    string summaryRam = string.Format("{0} GB {1} {2}", totalGb, ramType, speedStr);
                    HardwareIdentityService.TryUpdate("RamInfo", summaryRam, HardwareUpdateSource.WmiFresh, ViewModel);
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.RamSummary = "Greška pri detekciji RAM";
                        ViewModel.HardwareInfo.RamSlotsSummary = "N/A";
                    }
                });
            }
        }

        public void DetectStorage()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string model = obj["Model"] != null ? obj["Model"].ToString().Trim() : "Nepoznat Disk";
                        ulong sizeBytes = 0;
                        if (obj["Size"] != null) ulong.TryParse(obj["Size"].ToString(), out sizeBytes);
                        double sizeGb = Math.Round((double)sizeBytes / (1000.0 * 1000.0 * 1000.0));

                        string iface = obj["InterfaceType"] != null ? obj["InterfaceType"].ToString() : "";
                        string storageType = "SATA SSD";

                        if (model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            iface.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (iface.IndexOf("SCSI", StringComparison.OrdinalIgnoreCase) >= 0 && model.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            storageType = "NVMe PCIe SSD";
                        }
                        else if (model.IndexOf("SATA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 iface.IndexOf("IDE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 iface.IndexOf("SATA", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            storageType = "SATA SSD / HDD";
                        }

                        string finalModel = string.Format("{0} ({1} GB)", model, sizeGb);
                        string finalType = storageType;

                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (ViewModel.HardwareInfo != null)
                            {
                                ViewModel.HardwareInfo.StorageModel = finalModel;
                                ViewModel.HardwareInfo.StorageType = finalType;

                                if (!string.IsNullOrEmpty(ViewModel.DiskWearText))
                                {
                                    ViewModel.HardwareInfo.StorageHealth = ViewModel.DiskWearText;
                                }
                                else
                                {
                                    ViewModel.HardwareInfo.StorageHealth = "100% Zdravlje";
                                }

                                if (ViewModel.SmartStatusBrush is SolidColorBrush)
                                {
                                    ViewModel.HardwareInfo.StorageHealthBrush = (SolidColorBrush)ViewModel.SmartStatusBrush;
                                }

                                if (ViewModel.DiskPowerOnHours >= 0)
                                {
                                    ViewModel.HardwareInfo.StorageHours = string.Format("{0:N0} h", ViewModel.DiskPowerOnHours);
                                }
                                else
                                {
                                    ViewModel.HardwareInfo.StorageHours = "N/A (Zahteva S.M.A.R.T.)";
                                }

                                if (_thermalEngine != null && _thermalEngine.SsdTemperature > 0f)
                                {
                                    ViewModel.HardwareInfo.StorageTemp = string.Format("{0:F1} °C", _thermalEngine.SsdTemperature);
                                }
                                else
                                {
                                    ViewModel.HardwareInfo.StorageTemp = "N/A (Senzor nedostupan)";
                                }
                            }

                            HardwareIdentityService.TryUpdate("DiskInfo", finalModel, HardwareUpdateSource.WmiFresh, ViewModel);
                        });
                        break;
                    }
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageModel = "Greška pri detekciji diska";
                    }
                });
            }
        }

        public void DetectBattery()
        {
            try
            {
                bool hasBattery = false;
                uint designCap = 0;
                uint fullCap = 0;
                uint cycleCount = 0;
                bool hasCycles = false;

                try
                {
                    ManagementScope wmiScope = new ManagementScope(@"\\.\root\wmi");
                    wmiScope.Connect();

                    using (ManagementObjectSearcher staticSearcher = new ManagementObjectSearcher(wmiScope, new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData")))
                    {
                        foreach (ManagementObject obj in staticSearcher.Get())
                        {
                            if (obj["DesignedCapacity"] != null)
                            {
                                uint dc = 0;
                                if (uint.TryParse(obj["DesignedCapacity"].ToString(), out dc) && dc > 0)
                                {
                                    designCap = dc;
                                    hasBattery = true;
                                    break;
                                }
                            }
                        }
                    }

                    using (ManagementObjectSearcher fullSearcher = new ManagementObjectSearcher(wmiScope, new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
                    {
                        foreach (ManagementObject obj in fullSearcher.Get())
                        {
                            if (obj["FullChargedCapacity"] != null)
                            {
                                uint fc = 0;
                                if (uint.TryParse(obj["FullChargedCapacity"].ToString(), out fc) && fc > 0)
                                {
                                    fullCap = fc;
                                    hasBattery = true;
                                    break;
                                }
                            }
                        }
                    }

                    try
                    {
                        using (ManagementObjectSearcher cycleSearcher = new ManagementObjectSearcher(wmiScope, new ObjectQuery("SELECT CycleCount FROM BatteryCycleCount")))
                        {
                            foreach (ManagementObject obj in cycleSearcher.Get())
                            {
                                if (obj["CycleCount"] != null)
                                {
                                    uint cc = 0;
                                    if (uint.TryParse(obj["CycleCount"].ToString(), out cc))
                                    {
                                        cycleCount = cc;
                                        hasCycles = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
                catch { }

                uint batteryStatus = 0;
                if (designCap == 0 || fullCap == 0)
                {
                    try
                    {
                        using (ManagementObjectSearcher win32Searcher = new ManagementObjectSearcher("SELECT DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                        {
                            foreach (ManagementObject obj in win32Searcher.Get())
                            {
                                hasBattery = true;
                                uint dc = 0, fc = 0, bs = 0;
                                if (obj["DesignCapacity"] != null) uint.TryParse(obj["DesignCapacity"].ToString(), out dc);
                                if (obj["FullChargeCapacity"] != null) uint.TryParse(obj["FullChargeCapacity"].ToString(), out fc);
                                if (obj["BatteryStatus"] != null) uint.TryParse(obj["BatteryStatus"].ToString(), out bs);

                                if (designCap == 0 && dc > 0) designCap = dc;
                                if (fullCap == 0 && fc > 0) fullCap = fc;
                                if (bs > 0) batteryStatus = bs;
                                break;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        using (ManagementObjectSearcher bsSearcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery"))
                        {
                            foreach (ManagementObject obj in bsSearcher.Get())
                            {
                                if (obj["BatteryStatus"] != null) uint.TryParse(obj["BatteryStatus"].ToString(), out batteryStatus);
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (hasBattery && designCap > 0 && fullCap > 0)
                {
                    double wearLevel = (1.0 - ((double)fullCap / (double)designCap)) * 100.0;
                    if (wearLevel < 0.0) wearLevel = 0.0;

                    string wearStr = string.Format("{0:0.0}% Istrošenost ({1:N0} / {2:N0} mWh)", wearLevel, fullCap, designCap);
                    string capStr = string.Format("{0:N0} mWh / {1:N0} mWh", fullCap, designCap);
                    string powerStr = (batteryStatus == 2) ? "Punjenje (AC Adapter)" : (batteryStatus == 1 ? "Na bateriji (DC)" : "AC Mreža / Puna");

                    SolidColorBrush healthBrush;
                    if (wearLevel < 15.0)
                    {
                        healthBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                    }
                    else if (wearLevel < 35.0)
                    {
                        healthBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    }
                    else
                    {
                        healthBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                    }

                    string cyclesStr = hasCycles ? string.Format("{0} ciklusa", cycleCount) : (!string.IsNullOrEmpty(ViewModel.BatteryCycles) && ViewModel.BatteryCycles != "N/A" ? ViewModel.BatteryCycles : "Detektovano");

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (ViewModel.HardwareInfo != null)
                        {
                            ViewModel.HardwareInfo.BatteryWear = wearStr;
                            ViewModel.HardwareInfo.BatteryCapacity = capStr;
                            ViewModel.HardwareInfo.BatteryCycles = cyclesStr;
                            ViewModel.HardwareInfo.BatteryPowerStatus = powerStr;
                            ViewModel.HardwareInfo.BatteryHealthBrush = healthBrush;
                        }

                        ViewModel.BatteryHealth = (int)Math.Max(0, Math.Min(100, Math.Round(100.0 - wearLevel)));
                        ViewModel.BatteryCycles = cyclesStr;
                        ViewModel.IsLaptop = true;
                        HardwareIdentityService.TryUpdate("BatteryStatus", powerStr, HardwareUpdateSource.WmiFresh, ViewModel);
                    });
                }
                else if (hasBattery)
                {
                    string powerStr = (batteryStatus == 2) ? "Punjenje (AC Adapter)" : (batteryStatus == 1 ? "Na bateriji (DC)" : "AC Mreža / Baterija");
                    string cyclesStr = hasCycles ? string.Format("{0} ciklusa", cycleCount) : "N/A";
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (ViewModel.HardwareInfo != null)
                        {
                            ViewModel.HardwareInfo.BatteryWear = "Detektovana baterija";
                            ViewModel.HardwareInfo.BatteryCapacity = "N/A";
                            ViewModel.HardwareInfo.BatteryCycles = cyclesStr;
                            ViewModel.HardwareInfo.BatteryPowerStatus = powerStr;
                            ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                        }

                        ViewModel.IsLaptop = true;
                        HardwareIdentityService.TryUpdate("BatteryStatus", powerStr, HardwareUpdateSource.WmiFresh, ViewModel);
                    });
                }
                else
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (ViewModel.HardwareInfo != null)
                        {
                            ViewModel.HardwareInfo.BatteryWear = "Nema baterije (Desktop / AC Only)";
                            ViewModel.HardwareInfo.BatteryCapacity = "N/A (Fiksno AC napajanje)";
                            ViewModel.HardwareInfo.BatteryCycles = "N/A";
                            ViewModel.HardwareInfo.BatteryPowerStatus = "Desktop AC Napajanje";
                            ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                        }

                        ViewModel.BatteryHealth = 0;
                        ViewModel.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                        ViewModel.BatteryHealthText = "Fiksno napajanje (Desktop)";
                        ViewModel.BatteryCycles = "N/A";
                        ViewModel.IsLaptop = false;
                        HardwareIdentityService.TryUpdate("BatteryStatus", "Desktop (Fiksno AC)", HardwareUpdateSource.WmiFresh, ViewModel);
                    });
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.BatteryWear = "Nema baterije (Desktop / AC Only)";
                        ViewModel.HardwareInfo.BatteryCapacity = "N/A";
                        ViewModel.HardwareInfo.BatteryCycles = "N/A";
                        ViewModel.HardwareInfo.BatteryPowerStatus = "AC Napajanje";
                        ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                    }

                    ViewModel.BatteryHealth = 0;
                    ViewModel.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                    ViewModel.BatteryHealthText = "Fiksno napajanje (Desktop)";
                    ViewModel.BatteryCycles = "N/A";
                    ViewModel.IsLaptop = false;
                    HardwareIdentityService.TryUpdate("BatteryStatus", "Desktop (Fiksno AC)", HardwareUpdateSource.WmiFresh, ViewModel);
                });
            }
        }

        public void DetectGpu()
        {
            try
            {
                List<string> gpus = new List<string>();
                List<ulong> gpuRams = new List<ulong>();
                string primaryDriverVer = "";

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        ulong ram = 0;
                        if (obj["AdapterRAM"] != null)
                        {
                            ulong.TryParse(obj["AdapterRAM"].ToString(), out ram);
                        }

                        string drvVer = obj["DriverVersion"] != null ? obj["DriverVersion"].ToString().Trim() : "";

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (!gpus.Contains(name))
                            {
                                gpus.Add(name);
                                gpuRams.Add(ram);
                                if (string.IsNullOrEmpty(primaryDriverVer) && !string.IsNullOrEmpty(drvVer))
                                {
                                    primaryDriverVer = drvVer;
                                }
                            }
                        }
                    }
                }

                string gpu1 = gpus.Count > 0 ? gpus[0] : "Standardni VGA grafički adapter";
                ulong ram1 = gpuRams.Count > 0 ? gpuRams[0] : 0;
                string gpu1Vram = GetGpuVram(gpu1, ram1);

                string gpu2 = gpus.Count > 1 ? gpus[1] : "Nema (Single GPU)";
                ulong ram2 = gpuRams.Count > 1 ? gpuRams[1] : 0;
                bool hasDual = gpus.Count > 1;
                string gpu2Vram = hasDual ? GetGpuVram(gpu2, ram2) : "";

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.GpuPrimary = gpu1;
                        ViewModel.HardwareInfo.GpuPrimaryVram = gpu1Vram;
                        ViewModel.HardwareInfo.GpuSecondary = gpu2;
                        ViewModel.HardwareInfo.GpuSecondaryVram = gpu2Vram;
                        ViewModel.HardwareInfo.HasDualGpu = hasDual;
                    }

                    HardwareIdentityService.TryUpdate("GpuName", gpu1, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("GpuVram", gpu1Vram, HardwareUpdateSource.WmiFresh, ViewModel);
                    if (!string.IsNullOrEmpty(primaryDriverVer))
                    {
                        HardwareIdentityService.TryUpdate("GpuDriverVersion", primaryDriverVer, HardwareUpdateSource.WmiFresh, ViewModel);
                    }
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.GpuPrimary = "Greška pri detekciji GPU";
                        ViewModel.HardwareInfo.GpuPrimaryVram = "N/A";
                        ViewModel.HardwareInfo.GpuSecondary = "";
                        ViewModel.HardwareInfo.GpuSecondaryVram = "";
                    }
                });
            }
        }

        public void DetectDisplay()
        {
            try
            {
                int screenW = 0;
                int screenH = 0;
                int refreshRate = 60;

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        int w = 0, h = 0, hz = 0;
                        if (obj["CurrentHorizontalResolution"] != null) int.TryParse(obj["CurrentHorizontalResolution"].ToString(), out w);
                        if (obj["CurrentVerticalResolution"] != null) int.TryParse(obj["CurrentVerticalResolution"].ToString(), out h);
                        if (obj["CurrentRefreshRate"] != null) int.TryParse(obj["CurrentRefreshRate"].ToString(), out hz);

                        if (w > 0 && h > 0)
                        {
                            screenW = w;
                            screenH = h;
                            if (hz > 0) refreshRate = hz;
                            break;
                        }
                    }
                }

                if (screenW == 0 || screenH == 0)
                {
                    try
                    {
                        screenW = GetSystemMetrics(SM_CXSCREEN);
                        screenH = GetSystemMetrics(SM_CYSCREEN);
                    }
                    catch { }
                }

                string formatTag = "";
                if (screenW == 1920 && screenH == 1080) formatTag = "(FHD)";
                else if (screenW == 1920 && screenH == 1200) formatTag = "(FHD+)";
                else if (screenW == 2560 && screenH == 1440) formatTag = "(QHD / 2K)";
                else if (screenW == 2560 && screenH == 1600) formatTag = "(QHD+)";
                else if (screenW == 3840 && screenH == 2160) formatTag = "(4K UHD)";
                else if (screenW == 1366 && screenH == 768) formatTag = "(HD)";
                else if (screenW == 1600 && screenH == 900) formatTag = "(HD+)";

                int digitizerFlags = 0;
                int touches = 0;
                try
                {
                    digitizerFlags = GetSystemMetrics(SM_DIGITIZER);
                    touches = GetSystemMetrics(SM_MAXIMUMTOUCHES);
                }
                catch { }

                bool hasTouch = touches > 0 || ((digitizerFlags & 0x80) != 0);
                string touchStatusStr;
                SolidColorBrush touchBrush;

                if (hasTouch)
                {
                    touchStatusStr = touches > 0
                        ? string.Format("🖐️ Podržan ({0} tačaka dodira)", touches)
                        : "🖐️ Podržan (Touchscreen)";
                    touchBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                }
                else
                {
                    touchStatusStr = "Nije detektovan (Standardni displej)";
                    touchBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);
                }

                string panelType = DetectDisplayPanelType(screenW, screenH, ViewModel.IsLaptop);

                string displayModel = "";
                int displayNits = 0;
                int displaySrgb = 0;
                int displayNtsc = 0;
                GetEdidDisplayDetails(out displayModel, out displayNits, out displaySrgb, out displayNtsc);

                string displaySpecStr = "";
                if (!string.IsNullOrEmpty(displayModel) && displayModel != "Standardni panel")
                {
                    displaySpecStr = displayModel + " • ";
                }
                displaySpecStr += string.Format("{0}x{1} @ {2}Hz {3}", screenW, screenH, refreshRate, formatTag).Trim();
                if (displayNits > 0)
                {
                    displaySpecStr += string.Format(" • ~{0} nits (Procenjeno)", displayNits);
                }
                if (displaySrgb > 0)
                {
                    displaySpecStr += string.Format(" • ~{0}% sRGB (~{1}% NTSC)", displaySrgb, displayNtsc);
                }
                if (!string.IsNullOrEmpty(panelType))
                {
                    displaySpecStr += " • " + panelType;
                }
                if (hasTouch)
                {
                    displaySpecStr += " • 🖐️ Touchscreen";
                }

                string finalDisplayModel = !string.IsNullOrEmpty(displayModel) ? displayModel : "Standardni panel";
                string finalDisplayNits = displayNits > 0 ? string.Format("~{0} nits (Procenjeno)", displayNits) : "Standardno (~250-300 nits)";
                string finalDisplayGamut = displaySrgb > 0 ? string.Format("~{0}% sRGB (~{1}% NTSC)", displaySrgb, displayNtsc) : "Standardni gamut";

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.DisplaySpec = displaySpecStr;
                        ViewModel.HardwareInfo.DisplayModel = finalDisplayModel;
                        ViewModel.HardwareInfo.DisplayNits = finalDisplayNits;
                        ViewModel.HardwareInfo.DisplayColorGamut = finalDisplayGamut;
                        ViewModel.HardwareInfo.HasTouchscreen = hasTouch;
                        ViewModel.HardwareInfo.TouchscreenStatus = touchStatusStr;
                        ViewModel.HardwareInfo.TouchscreenBrush = touchBrush;
                    }

                    HardwareIdentityService.TryUpdate("DisplayResolution", string.Format("{0}x{1}", screenW, screenH), HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("DisplayRefreshRate", string.Format("{0} Hz", refreshRate), HardwareUpdateSource.WmiFresh, ViewModel);
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.DisplaySpec = "N/A";
                        ViewModel.HardwareInfo.DisplayModel = "N/A";
                        ViewModel.HardwareInfo.DisplayNits = "N/A";
                        ViewModel.HardwareInfo.DisplayColorGamut = "N/A";
                        ViewModel.HardwareInfo.HasTouchscreen = false;
                        ViewModel.HardwareInfo.TouchscreenStatus = "N/A";
                    }
                });
            }
        }

        public void DetectSystemIdentity()
        {
            try
            {
                string csMfg = "";
                string csModel = "";
                string cspProductName = "";
                string cspVersion = "";
                string cspSerial = "";
                string baseBoardMfg = "";
                string baseBoardProd = "";
                string bbSerial = "";

                try
                {
                    using (ManagementObjectSearcher csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                    {
                        foreach (ManagementObject obj in csSearcher.Get())
                        {
                            if (obj["Manufacturer"] != null) csMfg = obj["Manufacturer"].ToString().Trim();
                            if (obj["Model"] != null) csModel = obj["Model"].ToString().Trim();
                            break;
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher cspSearcher = new ManagementObjectSearcher("SELECT Name, Version, IdentifyingNumber FROM Win32_ComputerSystemProduct"))
                    {
                        foreach (ManagementObject obj in cspSearcher.Get())
                        {
                            if (obj["Name"] != null) cspProductName = obj["Name"].ToString().Trim();
                            if (obj["Version"] != null) cspVersion = obj["Version"].ToString().Trim();
                            if (obj["IdentifyingNumber"] != null) cspSerial = obj["IdentifyingNumber"].ToString().Trim();
                            break;
                        }
                    }
                }
                catch { }

                try
                {
                    using (ManagementObjectSearcher bbSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"))
                    {
                        foreach (ManagementObject obj in bbSearcher.Get())
                        {
                            if (obj["Manufacturer"] != null) baseBoardMfg = obj["Manufacturer"].ToString().Trim();
                            if (obj["Product"] != null) baseBoardProd = obj["Product"].ToString().Trim();
                            if (obj["SerialNumber"] != null) bbSerial = obj["SerialNumber"].ToString().Trim();
                            break;
                        }
                    }
                }
                catch { }

                string regSerial = "";
                try
                {
                    using (RegistryKey biosReg = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                    {
                        if (biosReg != null)
                        {
                            if (string.IsNullOrEmpty(csMfg))
                            {
                                object sm = biosReg.GetValue("SystemManufacturer");
                                if (sm != null) csMfg = sm.ToString().Trim();
                            }
                            if (string.IsNullOrEmpty(csModel))
                            {
                                object sp = biosReg.GetValue("SystemProductName");
                                if (sp != null) csModel = sp.ToString().Trim();
                            }
                            if (string.IsNullOrEmpty(baseBoardMfg))
                            {
                                object bm = biosReg.GetValue("BaseBoardManufacturer");
                                if (bm != null) baseBoardMfg = bm.ToString().Trim();
                            }
                            if (string.IsNullOrEmpty(baseBoardProd))
                            {
                                object bp = biosReg.GetValue("BaseBoardProduct");
                                if (bp != null) baseBoardProd = bp.ToString().Trim();
                            }
                            object sNum = biosReg.GetValue("SystemSerialNumber");
                            if (sNum != null) regSerial = sNum.ToString().Trim();
                        }
                    }
                }
                catch { }

                string rawMfg = (csMfg ?? "").Trim();
                string rawModel = (csModel ?? "").Trim();
                string cspVer = (cspVersion ?? "").Trim();
                string cspName = (cspProductName ?? "").Trim();
                string bbProd = (baseBoardProd ?? "").Trim();
                string bbMfg = (baseBoardMfg ?? "").Trim();

                string finalModel = "";
                if (rawMfg.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finalModel = !string.IsNullOrEmpty(rawModel) ? "Dell " + rawModel : "Dell Inc.";
                }
                else if (rawMfg.IndexOf("Lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!string.IsNullOrEmpty(cspVer) && cspVer != "None" && !cspVer.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        finalModel = "Lenovo " + cspVer;
                    }
                    else if (!string.IsNullOrEmpty(cspName) && cspName != "None")
                    {
                        finalModel = "Lenovo " + cspName;
                    }
                    else if (!string.IsNullOrEmpty(rawModel))
                    {
                        finalModel = "Lenovo " + rawModel;
                    }
                    else
                    {
                        finalModel = "Lenovo " + bbProd;
                    }
                }
                else if (rawMfg.IndexOf("HP", StringComparison.OrdinalIgnoreCase) >= 0 || rawMfg.IndexOf("Hewlett", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finalModel = rawModel.StartsWith("HP", StringComparison.OrdinalIgnoreCase) ? rawModel : "HP " + rawModel;
                }
                else if (string.IsNullOrEmpty(rawModel) || rawModel.IndexOf("System Product", StringComparison.OrdinalIgnoreCase) >= 0 || rawModel.IndexOf("To be filled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finalModel = string.Format("{0} {1}", rawMfg, bbProd).Trim();
                }
                else
                {
                    finalModel = string.Format("{0} {1}", rawMfg, rawModel).Trim();
                }

                if (string.IsNullOrEmpty(finalModel))
                {
                    finalModel = string.Format("{0} {1}", bbMfg, bbProd).Trim();
                }
                if (string.IsNullOrEmpty(finalModel))
                {
                    finalModel = "Standardni PC";
                }

                string finalSerial = !string.IsNullOrEmpty(cspSerial) && cspSerial != "None" ? cspSerial :
                                     (!string.IsNullOrEmpty(bbSerial) && bbSerial != "None" ? bbSerial :
                                     (!string.IsNullOrEmpty(regSerial) ? regSerial : "N/A"));

                string biosVer = "";
                string biosDate = "";
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["SMBIOSBIOSVersion"] != null) biosVer = obj["SMBIOSBIOSVersion"].ToString().Trim();
                        if (obj["ReleaseDate"] != null)
                        {
                            string rawDate = obj["ReleaseDate"].ToString().Trim();
                            if (rawDate.Length >= 8)
                            {
                                biosDate = string.Format("{0}-{1}-{2}", rawDate.Substring(0, 4), rawDate.Substring(4, 2), rawDate.Substring(6, 2));
                            }
                            else
                            {
                                biosDate = rawDate;
                            }
                        }
                        break;
                    }
                }

                string secureBootStr = "Legacy BIOS / Nepoznato";
                try
                {
                    using (RegistryKey sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                    {
                        if (sbKey != null)
                        {
                            object val = sbKey.GetValue("UEFISecureBootEnabled");
                            if (val != null)
                            {
                                int enabled = 0;
                                if (int.TryParse(val.ToString(), out enabled))
                                {
                                    secureBootStr = (enabled == 1) ? "Omogućen (SecureBoot ON)" : "Onemogućen (SecureBoot OFF)";
                                }
                            }
                        }
                    }
                }
                catch { }

                string biosFull = !string.IsNullOrEmpty(biosDate)
                    ? string.Format("{0} ({1})", biosVer, biosDate)
                    : biosVer;

                string finalMbProd = !string.IsNullOrEmpty(bbProd) ? bbProd : "Standardna matična ploča";
                string finalMbMfg = !string.IsNullOrEmpty(bbMfg) ? bbMfg : rawMfg;

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.MotherboardModel = finalMbProd;
                        ViewModel.HardwareInfo.MotherboardManufacturer = finalMbMfg;
                        ViewModel.HardwareInfo.SerialNumber = finalSerial;
                        ViewModel.HardwareInfo.BiosVersionDate = biosFull;
                        ViewModel.HardwareInfo.SecureBootStatus = secureBootStr;
                    }

                    HardwareIdentityService.TryUpdate("ComputerModel", finalModel, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("SerialNumber", finalSerial, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("MotherboardModel", finalMbProd, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("BiosVersion", biosVer, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("BiosReleaseDate", biosDate, HardwareUpdateSource.WmiFresh, ViewModel);
                    HardwareIdentityService.TryUpdate("SecureBootStatus", secureBootStr, HardwareUpdateSource.WmiFresh, ViewModel);
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.MotherboardModel = "Greška pri detekciji ploče";
                    }
                });
            }
        }

        public void DetectPeripherals()
        {
            try
            {
                string webcamName = LocalizationService.Instance.GetString("HwCamNo", "Nije detektovana");
                bool hasWebcam = false;
                SolidColorBrush webcamBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                            if (!string.IsNullOrEmpty(name))
                            {
                                webcamName = name;
                                hasWebcam = true;
                                webcamBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                break;
                            }
                        }
                    }
                }
                catch { }

                string irCameraStatus = LocalizationService.Instance.GetString("HwIrNo", "Ne (Standardna RGB kamera)");
                bool hasIrCamera = false;
                SolidColorBrush irCameraBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                        "WHERE (PNPClass = 'Camera' OR PNPClass = 'Image' OR PNPClass = 'Sensor') " +
                        "AND (Name LIKE '%IR%' OR Name LIKE '%Infrared%' OR Name LIKE '%Hello Face%' OR DeviceID LIKE '%IR_CAMERA%' OR DeviceID LIKE '%INFRARED%')"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString() : "";
                            string name = obj["Name"] != null ? obj["Name"].ToString() : "";
                            uint errCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;

                            if (devId.StartsWith("SWC\\", StringComparison.OrdinalIgnoreCase) ||
                                devId.IndexOf("MS_FACE_AUTH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                devId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                                name.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                continue;
                            }

                            if (errCode == 0)
                            {
                                hasIrCamera = true;
                                irCameraStatus = LocalizationService.Instance.GetString("HwIrYes", "👁️ Da (Windows Hello Face)");
                                irCameraBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                break;
                            }
                        }
                    }
                }
                catch { }

                string backlitStatus = LocalizationService.Instance.GetString("HwKbNo", "Standardna (Bez osvetljenja)");
                bool hasBacklit = false;
                bool backlitIsManual = false;
                SolidColorBrush backlitBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, Service FROM Win32_PnPEntity " +
                        "WHERE (Name LIKE '%Backlight%' OR Name LIKE '%Illuminated%' OR Name LIKE '%RGB Keyboard%' " +
                        "OR Service LIKE '%KbdBacklight%' OR Service LIKE '%Backlight%' OR DeviceID LIKE '%DELLABCE%' OR DeviceID LIKE '%LEN0071%')"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            hasBacklit = true;
                            backlitStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
                            backlitBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                            break;
                        }
                    }

                    if (!hasBacklit)
                    {
                        try
                        {
                            using (ManagementObjectSearcher lenovoSearcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM Lenovo_SetKeyboardBacklightStatus"))
                            {
                                if (lenovoSearcher.Get().Count > 0)
                                {
                                    hasBacklit = true;
                                    backlitStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
                                    backlitBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                }
                            }
                        }
                        catch { }
                    }

                    if (!hasBacklit)
                    {
                        try
                        {
                            using (ManagementObjectSearcher dellSearcher = new ManagementObjectSearcher(@"root\dcim\sysman", "SELECT * FROM DCIM_BiosEnumeration WHERE AttributeName LIKE '%Keyboard%Backlight%'"))
                            {
                                if (dellSearcher.Get().Count > 0)
                                {
                                    hasBacklit = true;
                                    backlitStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
                                    backlitBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                }
                            }
                        }
                        catch { }
                    }

                    if (!hasBacklit)
                    {
                        try
                        {
                            using (ManagementObjectSearcher hpSearcher = new ManagementObjectSearcher(@"root\hp\instrumentedBIOS", "SELECT * FROM HP_BIOSSetting WHERE Name LIKE '%Backlit%'"))
                            {
                                if (hpSearcher.Get().Count > 0)
                                {
                                    hasBacklit = true;
                                    backlitStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
                                    backlitBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                }
                            }
                        }
                        catch { }
                    }

                    if (!hasBacklit && ViewModel.IsLaptop)
                    {
                        backlitIsManual = true;
                        backlitStatus = LocalizationService.Instance.GetString("HwKbCheckManual", "Standardna / Proveri ručno (Fn+Space)");
                        backlitBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    }
                }
                catch { }

                string fpStatus = LocalizationService.Instance.GetString("HwFpNo", "Nije detektovan");
                string fpDetectedName = "";
                bool hasFp = false;
                SolidColorBrush fpBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Biometric'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString() : "";
                            uint errCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;

                            if (name.IndexOf("Face", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                devId.IndexOf("MS_FACE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                devId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (errCode == 0 && !string.IsNullOrEmpty(name))
                            {
                                hasFp = true;
                                fpDetectedName = name;
                                fpStatus = string.Format(LocalizationService.Instance.GetString("HwFpYes", "👆 Da ({0})"), name);
                                fpBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                break;
                            }
                        }
                    }
                }
                catch { }

                string speakersName = "";
                string micName = "";
                SolidColorBrush speakersBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);
                SolidColorBrush micBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'AudioEndpoint'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString() : "";
                            if (string.IsNullOrEmpty(name)) continue;

                            if (devId.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Speakers", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Zvučnici", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (string.IsNullOrEmpty(speakersName))
                                {
                                    speakersName = name;
                                    speakersBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                }
                            }
                            else if (devId.IndexOf("Capture", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("Microphone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("Mikrofon", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (string.IsNullOrEmpty(micName))
                                {
                                    micName = name;
                                    micBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(speakersName))
                    {
                        using (ManagementObjectSearcher soundSearcher = new ManagementObjectSearcher(
                            "SELECT Name, Status FROM Win32_SoundDevice WHERE Status = 'OK'"))
                        {
                            foreach (ManagementObject obj in soundSearcher.Get())
                            {
                                string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                                if (!string.IsNullOrEmpty(name))
                                {
                                    speakersName = name;
                                    speakersBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                string finalSpeakersStatus = !string.IsNullOrEmpty(speakersName)
                    ? "🔊 " + speakersName
                    : LocalizationService.Instance.GetString("HwAudioSpeakersDefault", "Standardni Audio uređaj");

                string finalMicStatus = !string.IsNullOrEmpty(micName)
                    ? string.Format(LocalizationService.Instance.GetString("HwMicYes", "🎤 Da ({0})"), micName)
                    : LocalizationService.Instance.GetString("HwMicNo", "Nije detektovan");

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.WebcamModel = webcamName;
                        ViewModel.HardwareInfo.HasWebcam = hasWebcam;
                        ViewModel.HardwareInfo.WebcamBrush = webcamBrush;

                        ViewModel.HardwareInfo.IrCameraStatus = irCameraStatus;
                        ViewModel.HardwareInfo.HasIrCamera = hasIrCamera;
                        ViewModel.HardwareInfo.IrCameraBrush = irCameraBrush;

                        ViewModel.HardwareInfo.BacklitKeyboardStatus = backlitStatus;
                        ViewModel.HardwareInfo.HasBacklitKeyboard = hasBacklit;
                        ViewModel.HardwareInfo.BacklitIsManual = backlitIsManual;
                        ViewModel.HardwareInfo.BacklitKeyboardBrush = backlitBrush;

                        ViewModel.HardwareInfo.FingerprintDeviceName = fpDetectedName;
                        ViewModel.HardwareInfo.FingerprintSensor = fpStatus;
                        ViewModel.HardwareInfo.HasFingerprint = hasFp;
                        ViewModel.HardwareInfo.FingerprintBrush = fpBrush;

                        ViewModel.HardwareInfo.SpeakersDeviceName = speakersName;
                        ViewModel.HardwareInfo.AudioSpeakers = finalSpeakersStatus;
                        ViewModel.HardwareInfo.AudioSpeakersBrush = speakersBrush;

                        ViewModel.HardwareInfo.MicDeviceName = micName;
                        ViewModel.HardwareInfo.MicrophoneStatus = finalMicStatus;
                        ViewModel.HardwareInfo.MicrophoneBrush = micBrush;
                    }
                });
            }
            catch { }
        }

        private string GetGpuVram(string gpuName, ulong wmiAdapterRam)
        {
            if (string.IsNullOrEmpty(gpuName) || gpuName.StartsWith("Greška") || gpuName == "Standardni VGA grafički adapter" || gpuName.StartsWith("Nema"))
            {
                return "";
            }

            ulong finalBytes = 0;

            try
            {
                using (RegistryKey classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (classKey != null)
                    {
                        string[] subKeyNames = classKey.GetSubKeyNames();
                        foreach (string subName in subKeyNames)
                        {
                            if (subName.Length == 4)
                            {
                                using (RegistryKey subKey = classKey.OpenSubKey(subName))
                                {
                                    if (subKey != null)
                                    {
                                        string driverDesc = subKey.GetValue("DriverDesc") as string;
                                        if (!string.IsNullOrEmpty(driverDesc) &&
                                            (driverDesc.IndexOf(gpuName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             gpuName.IndexOf(driverDesc, StringComparison.OrdinalIgnoreCase) >= 0))
                                        {
                                            object qwObj = subKey.GetValue("HardwareInformation.qwMemorySize");
                                            if (qwObj != null)
                                            {
                                                if (qwObj is long)
                                                {
                                                    finalBytes = (ulong)(long)qwObj;
                                                }
                                                else if (qwObj is ulong)
                                                {
                                                    finalBytes = (ulong)qwObj;
                                                }
                                                else if (qwObj is byte[])
                                                {
                                                    byte[] b = (byte[])qwObj;
                                                    if (b.Length >= 8) finalBytes = BitConverter.ToUInt64(b, 0);
                                                    else if (b.Length >= 4) finalBytes = (ulong)BitConverter.ToUInt32(b, 0);
                                                }
                                            }

                                            if (finalBytes == 0)
                                            {
                                                object dwordObj = subKey.GetValue("HardwareInformation.MemorySize");
                                                if (dwordObj != null)
                                                {
                                                    if (dwordObj is int)
                                                    {
                                                        finalBytes = (ulong)(uint)(int)dwordObj;
                                                    }
                                                    else if (dwordObj is uint)
                                                    {
                                                        finalBytes = (ulong)(uint)dwordObj;
                                                    }
                                                    else if (dwordObj is byte[])
                                                    {
                                                        byte[] b = (byte[])dwordObj;
                                                        if (b.Length >= 4) finalBytes = (ulong)BitConverter.ToUInt32(b, 0);
                                                    }
                                                }
                                            }

                                            if (finalBytes > 0)
                                            {
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (finalBytes == 0 && wmiAdapterRam > 0)
            {
                finalBytes = wmiAdapterRam;
            }

            bool isIntegrated = false;
            string lower = gpuName.ToLowerInvariant();
            if (lower.Contains("intel") && (lower.Contains("hd graph") || lower.Contains("uhd graph") || lower.Contains("iris") || lower.Contains("graphics") || lower.Contains("xe")))
            {
                isIntegrated = true;
            }
            else if (lower.Contains("amd") && (lower.Contains("radeon(tm) graph") || lower.Contains("vega") || lower.Contains("radeon graphics") || lower.Contains("integrated")))
            {
                isIntegrated = true;
            }
            else if (lower.Contains("basic display") || lower.Contains("standard vga"))
            {
                isIntegrated = true;
            }

            if (finalBytes >= 1024UL * 1024UL * 1024UL)
            {
                double gb = Math.Round((double)finalBytes / (1024.0 * 1024.0 * 1024.0));
                if (isIntegrated)
                {
                    return string.Format("{0} GB (Deljena sistemska memorija)", (int)gb);
                }
                return string.Format("{0} GB VRAM", (int)gb);
            }
            else if (finalBytes >= 1024UL * 1024UL)
            {
                long mb = (long)(finalBytes / (1024UL * 1024UL));
                if (isIntegrated)
                {
                    return string.Format("{0} MB (Deljena memorija)", mb);
                }
                return string.Format("{0} MB VRAM", mb);
            }
            else
            {
                if (isIntegrated)
                {
                    return "Deljena sistemska memorija (iGPU)";
                }
                return "N/A";
            }
        }

        private static string DecodeWmiStringArray(object raw)
        {
            if (raw == null) return string.Empty;
            ushort[] ushortArr = raw as ushort[];
            if (ushortArr != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (ushort u in ushortArr)
                {
                    if (u == 0) break;
                    sb.Append((char)u);
                }
                return sb.ToString().Trim();
            }
            byte[] byteArr = raw as byte[];
            if (byteArr != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte b in byteArr)
                {
                    if (b == 0) break;
                    sb.Append((char)b);
                }
                return sb.ToString().Trim();
            }
            return raw.ToString().Trim();
        }

        private static string GetMonitorInfoFromWmi()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ManufacturerName, UserFriendlyName, ProductCodeID FROM WmiMonitorID")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string mfg = DecodeWmiStringArray(obj["ManufacturerName"]);
                        string friendly = DecodeWmiStringArray(obj["UserFriendlyName"]);
                        string prodCode = DecodeWmiStringArray(obj["ProductCodeID"]);
                        if (!string.IsNullOrEmpty(mfg)) sb.Append(mfg).Append(" ");
                        if (!string.IsNullOrEmpty(friendly)) sb.Append(friendly).Append(" ");
                        if (!string.IsNullOrEmpty(prodCode)) sb.Append(prodCode).Append(" ");
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        private static string GetMonitorInfoFromRegistry()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                using (RegistryKey displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (displayKey != null)
                    {
                        foreach (string subKeyName in displayKey.GetSubKeyNames())
                        {
                            sb.Append(subKeyName).Append(" ");
                            try
                            {
                                using (RegistryKey subKey = displayKey.OpenSubKey(subKeyName))
                                {
                                    if (subKey != null)
                                    {
                                        foreach (string instName in subKey.GetSubKeyNames())
                                        {
                                            using (RegistryKey instKey = subKey.OpenSubKey(instName))
                                            {
                                                if (instKey != null)
                                                {
                                                    object devDesc = instKey.GetValue("DeviceDesc");
                                                    if (devDesc != null) sb.Append(devDesc.ToString()).Append(" ");
                                                }
                                            }
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
            return sb.ToString();
        }

        private string DetectDisplayPanelType(int screenW, int screenH, bool isLaptop)
        {
            try
            {
                string rawText = (GetMonitorInfoFromWmi() + " " + GetMonitorInfoFromRegistry()).ToUpperInvariant();

                if (rawText.Contains("OLED") || rawText.Contains("AMOLED"))
                {
                    return "OLED";
                }

                if (rawText.Contains("IPS") || rawText.Contains("AHVA") || rawText.Contains("PLS") ||
                    rawText.Contains("ADS") || rawText.Contains("AH-IPS") || rawText.Contains("IGZO"))
                {
                    return "IPS";
                }

                bool hasKnownIpsPrefix = rawText.Contains("NV13") || rawText.Contains("NV14") || rawText.Contains("NV15") || rawText.Contains("NV16") || rawText.Contains("NV17") ||
                                        rawText.Contains("LP13") || rawText.Contains("LP14") || rawText.Contains("LP15") || rawText.Contains("LP16") || rawText.Contains("LP17") ||
                                        rawText.Contains("B140HAN") || rawText.Contains("B156HAN") || rawText.Contains("B173HAN") ||
                                        rawText.Contains("N140HCE") || rawText.Contains("N156HCE") || rawText.Contains("N173HCE");
                if (hasKnownIpsPrefix)
                {
                    return "IPS";
                }

                if (isLaptop && screenW >= 1920 && screenH >= 1080)
                {
                    return "IPS";
                }

                if (isLaptop && (screenW <= 1366 || (screenW == 1600 && screenH == 900)))
                {
                    return "TN / Standard";
                }

                if ((rawText.Contains("BOE") || rawText.Contains("AUO") || rawText.Contains("LGD") || rawText.Contains("CMN") || rawText.Contains("INN")) && screenW >= 1920)
                {
                    return "IPS";
                }

                if (screenW >= 1920 && screenH >= 1080)
                {
                    return "IPS";
                }
            }
            catch { }

            return "TN / Standard";
        }

        private static void GetEdidDisplayDetails(out string model, out int nits, out int srgb, out int ntsc)
        {
            model = "";
            nits = 0;
            srgb = 0;
            ntsc = 0;

            byte[] edidBytes = null;

            try
            {
                using (RegistryKey displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (displayKey != null)
                    {
                        foreach (string monKeyName in displayKey.GetSubKeyNames())
                        {
                            using (RegistryKey monKey = displayKey.OpenSubKey(monKeyName))
                            {
                                if (monKey == null) continue;
                                foreach (string instKeyName in monKey.GetSubKeyNames())
                                {
                                    using (RegistryKey instKey = monKey.OpenSubKey(instKeyName))
                                    {
                                        if (instKey == null) continue;
                                        using (RegistryKey paramKey = instKey.OpenSubKey("Device Parameters"))
                                        {
                                            if (paramKey != null)
                                            {
                                                byte[] edid = paramKey.GetValue("EDID") as byte[];
                                                if (edid != null && edid.Length >= 128)
                                                {
                                                    if (edid[0] == 0x00 && edid[1] == 0xFF && edid[2] == 0xFF && edid[3] == 0xFF &&
                                                        edid[4] == 0xFF && edid[5] == 0xFF && edid[6] == 0xFF && edid[7] == 0x00)
                                                    {
                                                        edidBytes = edid;
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            if (edidBytes != null) break;
                        }
                    }
                }
            }
            catch { }

            if (edidBytes == null)
            {
                try
                {
                    ManagementScope scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT UserFriendlyName, ProductCodeID FROM WmiMonitorID")))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string friendly = DecodeWmiStringArray(obj["UserFriendlyName"]);
                            string prod = DecodeWmiStringArray(obj["ProductCodeID"]);
                            if (!string.IsNullOrEmpty(friendly)) { model = friendly; break; }
                            if (!string.IsNullOrEmpty(prod)) { model = prod; break; }
                        }
                    }
                }
                catch { }
            }

            if (edidBytes != null && edidBytes.Length >= 128)
            {
                int[] descriptorOffsets = new int[] { 54, 72, 90, 108 };
                foreach (int off in descriptorOffsets)
                {
                    if (off + 18 <= edidBytes.Length)
                    {
                        if (edidBytes[off] == 0 && edidBytes[off + 1] == 0 && edidBytes[off + 2] == 0 && edidBytes[off + 3] == 0xFC)
                        {
                            StringBuilder sb = new StringBuilder();
                            for (int i = off + 5; i < off + 18; i++)
                            {
                                byte b = edidBytes[i];
                                if (b == 0x0A || b == 0x00) break;
                                if (b >= 32 && b <= 126) sb.Append((char)b);
                            }
                            string extracted = sb.ToString().Trim();
                            if (!string.IsNullOrEmpty(extracted))
                            {
                                model = extracted;
                                break;
                            }
                        }
                        else if (string.IsNullOrEmpty(model) && edidBytes[off] == 0 && edidBytes[off + 1] == 0 && edidBytes[off + 2] == 0 && edidBytes[off + 3] == 0xFE)
                        {
                            StringBuilder sb = new StringBuilder();
                            for (int i = off + 5; i < off + 18; i++)
                            {
                                byte b = edidBytes[i];
                                if (b == 0x0A || b == 0x00) break;
                                if (b >= 32 && b <= 126) sb.Append((char)b);
                            }
                            string extracted = sb.ToString().Trim();
                            if (!string.IsNullOrEmpty(extracted))
                            {
                                model = extracted;
                            }
                        }
                    }
                }

                try
                {
                    int b25 = edidBytes[25];
                    int b26 = edidBytes[26];
                    int rxRaw = (edidBytes[27] << 2) | ((b25 >> 6) & 0x03);
                    int ryRaw = (edidBytes[28] << 2) | ((b25 >> 4) & 0x03);
                    int gxRaw = (edidBytes[29] << 2) | ((b25 >> 2) & 0x03);
                    int gyRaw = (edidBytes[30] << 2) | (b25 & 0x03);
                    int bxRaw = (edidBytes[31] << 2) | ((b26 >> 6) & 0x03);
                    int byRaw = (edidBytes[32] << 2) | ((b26 >> 4) & 0x03);

                    double rX = rxRaw / 1024.0, rY = ryRaw / 1024.0;
                    double gX = gxRaw / 1024.0, gY = gyRaw / 1024.0;
                    double bX = bxRaw / 1024.0, bY = byRaw / 1024.0;

                    double area = 0.5 * Math.Abs(rX * (gY - bY) + gX * (bY - rY) + bX * (rY - gY));
                    if (area > 0.02 && area < 0.25)
                    {
                        ntsc = (int)Math.Round((area / 0.1582) * 100.0);
                        srgb = (int)Math.Round((area / 0.1118) * 100.0);
                    }
                }
                catch { }
            }

            if (srgb <= 0 || ntsc <= 0)
            {
                string mUpper = (model ?? "").ToUpperInvariant();
                if (mUpper.Contains("OLED") || mUpper.Contains("AMOLED"))
                {
                    srgb = 100;
                    ntsc = 100;
                    nits = 400;
                }
                else if (mUpper.Contains("HAN") || mUpper.Contains("HCE") || mUpper.Contains("DELL") || mUpper.Contains("HP") || mUpper.Contains("LENOVO"))
                {
                    srgb = 99;
                    ntsc = 72;
                    nits = 300;
                }
                else
                {
                    srgb = 65;
                    ntsc = 45;
                    nits = 250;
                }
            }
            else
            {
                string mUpper = (model ?? "").ToUpperInvariant();
                if (mUpper.Contains("OLED") || mUpper.Contains("AMOLED"))
                {
                    nits = 400;
                }
                else if (srgb >= 95)
                {
                    nits = 350;
                }
                else if (srgb >= 70)
                {
                    nits = 300;
                }
                else
                {
                    nits = 250;
                }
            }

            if (string.IsNullOrEmpty(model))
            {
                model = "Standardni panel";
            }
        }
    }
}
