using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WIS.UI
{
    public partial class MainWindow
    {
        private int _peripheralsRefreshInFlight = 0;
        private int _peripheralsRefreshPending = 0;

        /// <summary>
        /// Primenjuje odluku o pozadinskom osvetljenju tastature (iz OEM BOM-a ili Faze 5 HwTest-a) na ViewModel.
        /// </summary>
        public void ApplyBacklitKeyboardVerdict(bool? confirmed, string sourceReason)
        {
            try
            {
                Action applyAction = delegate
                {
                    if (ViewModel != null && ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.ApplyBacklitKeyboardVerdict(confirmed, sourceReason);
                    }
                };

                if (Dispatcher != null && !Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(applyAction);
                }
                else
                {
                    applyAction();
                }
            }
            catch { }
        }

        /// <summary>
        /// Bezbedno asinhrono osvežava periferije (Bluetooth verzija, 4G/5G, eSIM i pregled) nakon instalacije drajvera.
        /// </summary>
        public void RefreshPeripheralsAfterDrivers()
        {
            Interlocked.Exchange(ref _peripheralsRefreshPending, 1);
            if (Interlocked.CompareExchange(ref _peripheralsRefreshInFlight, 1, 0) != 0)
            {
                return;
            }

            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        while (Interlocked.Exchange(ref _peripheralsRefreshPending, 0) != 0)
                        {
                            DetectPeripherals();
                        }
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _peripheralsRefreshInFlight, 0);
                        if (Interlocked.CompareExchange(ref _peripheralsRefreshPending, 0, 0) != 0)
                        {
                            RefreshPeripheralsAfterDrivers();
                        }
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _peripheralsRefreshInFlight, 0);
            }
        }

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
                    ramSlotsSummaryStr = LocalizationService.Get("HwRamSolderedNoSlots", "Integrisana na ploči (Bez slotova)");
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
                    : (isSoldered ? LocalizationService.Get("HwRamSolderedOnboard", "Integrisana na matičnoj ploči") : string.Format(LocalizationService.Get("HwRamOneModule", "1 modul ({0})"), locators.Count > 0 ? locators[0] : "Slot 1"));

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
                        ViewModel.HardwareInfo.RamSummary = LocalizationService.Get("HwRamDetectError", "Greška pri detekciji RAM");
                        ViewModel.HardwareInfo.RamSlotsSummary = "N/A";
                    }
                });
            }
        }

        private static int ResolveSystemDiskIndex()
        {
            string sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (string.IsNullOrWhiteSpace(sysDrive)) sysDrive = "C:";
            sysDrive = sysDrive.Trim().TrimEnd('\\');
            if (sysDrive.Length == 1) sysDrive = sysDrive + ":";
            char driveLetter = char.ToUpperInvariant(sysDrive[0]);

            // 1. Pokušaj preko MSFT_Partition (root\Microsoft\Windows\Storage)
            try
            {
                using (ManagementObjectSearcher partSearcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    string.Format("SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter = '{0}'", driveLetter)))
                {
                    foreach (ManagementObject obj in partSearcher.Get())
                    {
                        if (obj["DiskNumber"] != null)
                        {
                            int dn = -1;
                            if (int.TryParse(obj["DiskNumber"].ToString(), out dn) && dn >= 0)
                            {
                                return dn;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback preko Win32_LogicalDisk -> Win32_DiskPartition
            try
            {
                string assocQuery = string.Format(
                    "ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{0}'}} WHERE AssocClass = Win32_LogicalDiskToPartition",
                    sysDrive);
                using (ManagementObjectSearcher partSearcher = new ManagementObjectSearcher(assocQuery))
                {
                    foreach (ManagementObject part in partSearcher.Get())
                    {
                        if (part["DiskIndex"] != null)
                        {
                            int idx = -1;
                            if (int.TryParse(part["DiskIndex"].ToString(), out idx) && idx >= 0)
                            {
                                return idx;
                            }
                        }
                    }
                }
            }
            catch { }

            return -1;
        }

        private static bool IsExcludedPhysicalBusType(int busType)
        {
            // 7=USB, 9=iSCSI, 12=SD, 13=MMC, 14=Virtual, 15=File Backed Virtual, 16=Spaces
            return busType == 7 || busType == 9 || busType == 12 || busType == 13 ||
                   busType == 14 || busType == 15 || busType == 16;
        }

        private static bool IsExcludedWin32Disk(string iface, string mediaType, string pnpId)
        {
            string ifU = (iface ?? "").Trim().ToUpperInvariant();
            if (ifU == "USB" || ifU == "1394") return true;

            string mt = (mediaType ?? "").Trim();
            if (mt.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mt.IndexOf("External", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string pnp = (pnpId ?? "").Trim();
            if (pnp.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ||
                pnp.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                pnp.StartsWith("SD\\", StringComparison.OrdinalIgnoreCase) ||
                pnp.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) ||
                pnp.IndexOf("VEN_MSFT&PROD_VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        public void DetectStorage()
        {
            try
            {
                int systemDiskIndex = ResolveSystemDiskIndex();
                string bestModel = null;
                string bestType = "SATA SSD";
                ulong bestSize = 0;
                int bestPriority = -1;

                // 1. Primarno: MSFT_PhysicalDisk (tačniji BusType/MediaType za NVMe/SSD/HDD)
                try
                {
                    using (ManagementObjectSearcher msftSearcher = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Storage",
                        "SELECT DeviceId, FriendlyName, Model, Size, BusType, MediaType FROM MSFT_PhysicalDisk"))
                    {
                        foreach (ManagementObject obj in msftSearcher.Get())
                        {
                            int busType = 0;
                            if (obj["BusType"] != null) int.TryParse(obj["BusType"].ToString(), out busType);
                            if (IsExcludedPhysicalBusType(busType)) continue;

                            int diskId = -1;
                            if (obj["DeviceId"] != null) int.TryParse(obj["DeviceId"].ToString(), out diskId);
                            bool isSysDisk = (systemDiskIndex >= 0 && diskId == systemDiskIndex);

                            ulong sizeBytes = 0;
                            if (obj["Size"] != null) ulong.TryParse(obj["Size"].ToString(), out sizeBytes);
                            if (sizeBytes == 0) continue;

                            string model = obj["FriendlyName"] != null ? obj["FriendlyName"].ToString().Trim() : "";
                            if (string.IsNullOrWhiteSpace(model) && obj["Model"] != null) model = obj["Model"].ToString().Trim();
                            if (string.IsNullOrWhiteSpace(model)) model = "Nepoznat Disk";

                            int mediaType = 0;
                            if (obj["MediaType"] != null) int.TryParse(obj["MediaType"].ToString(), out mediaType);

                            string storageType = "SATA SSD";
                            if (busType == 17)
                            {
                                storageType = "NVMe PCIe SSD";
                            }
                            else if (mediaType == 4)
                            {
                                storageType = model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ? "NVMe PCIe SSD" : "SSD";
                            }
                            else if (mediaType == 3)
                            {
                                storageType = "HDD";
                            }
                            else if (model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                storageType = "NVMe PCIe SSD";
                            }

                            int priority = isSysDisk ? 1000 : (busType == 17 ? 100 : (busType == 11 || busType == 8 ? 50 : 10));
                            if (priority > bestPriority || (priority == bestPriority && sizeBytes > bestSize))
                            {
                                bestPriority = priority;
                                bestSize = sizeBytes;
                                bestModel = model;
                                bestType = storageType;
                            }
                        }
                    }
                }
                catch { }

                // 2. Fallback: Win32_DiskDrive (sa strogim USB/Removable/External/PNP filtriranjem)
                if (string.IsNullOrWhiteSpace(bestModel))
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Index, DeviceID, Model, Size, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string iface = obj["InterfaceType"] != null ? obj["InterfaceType"].ToString() : "";
                            string mediaType = obj["MediaType"] != null ? obj["MediaType"].ToString() : "";
                            string pnpId = obj["PNPDeviceID"] != null ? obj["PNPDeviceID"].ToString() : "";
                            if (IsExcludedWin32Disk(iface, mediaType, pnpId)) continue;

                            int diskIdx = -1;
                            if (obj["Index"] != null) int.TryParse(obj["Index"].ToString(), out diskIdx);
                            bool isSysDisk = (systemDiskIndex >= 0 && diskIdx == systemDiskIndex);

                            string model = obj["Model"] != null ? obj["Model"].ToString().Trim() : "Nepoznat Disk";
                            ulong sizeBytes = 0;
                            if (obj["Size"] != null) ulong.TryParse(obj["Size"].ToString(), out sizeBytes);
                            if (sizeBytes == 0 && !isSysDisk) continue;

                            string storageType = "SATA SSD";
                            if (model.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                iface.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                pnpId.IndexOf("NVME", StringComparison.OrdinalIgnoreCase) >= 0 ||
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

                            int priority = isSysDisk ? 1000 : (storageType == "NVMe PCIe SSD" ? 100 : 50);
                            if (priority > bestPriority || (priority == bestPriority && sizeBytes > bestSize))
                            {
                                bestPriority = priority;
                                bestSize = sizeBytes;
                                bestModel = model;
                                bestType = storageType;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(bestModel))
                {
                    double sizeGb = Math.Round((double)bestSize / (1000.0 * 1000.0 * 1000.0));
                    string finalModel = string.Format("{0} ({1} GB)", bestModel, sizeGb);
                    string finalType = bestType;

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
                                ViewModel.HardwareInfo.StorageHealth = LocalizationService.Get("HwDiskHealth100", "100% Zdravlje");
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
                                ViewModel.HardwareInfo.StorageHours = LocalizationService.Get("HwDiskHoursNaSmart", "N/A (Zahteva S.M.A.R.T.)");
                            }

                            if (_thermalEngine != null && _thermalEngine.SsdTemperature > 0f)
                            {
                                ViewModel.HardwareInfo.StorageTemp = string.Format("{0:F1} °C", _thermalEngine.SsdTemperature);
                            }
                            else
                            {
                                ViewModel.HardwareInfo.StorageTemp = LocalizationService.Get("HwDiskTempNaSensor", "N/A (Senzor nedostupan)");
                            }
                        }

                        HardwareIdentityService.TryUpdate("DiskInfo", finalModel, HardwareUpdateSource.WmiFresh, ViewModel);
                    });
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageModel = LocalizationService.Get("HwDiskDetectError", "Greška pri detekciji diska");
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

                    string wearStr = string.Format(LocalizationService.Get("HwBatteryWearFmt", "{0:0.0}% Istrošenost ({1:N0} / {2:N0} mWh)"), wearLevel, fullCap, designCap);
                    string capStr = string.Format("{0:N0} mWh / {1:N0} mWh", fullCap, designCap);
                    string powerStr = (batteryStatus == 2) ? LocalizationService.Get("HwBatteryChargingAc", "Punjenje (AC Adapter)") : (batteryStatus == 1 ? LocalizationService.Get("HwBatteryOnBatteryDc", "Na bateriji (DC)") : LocalizationService.Get("HwBatteryAcFull", "AC Mreža / Puna"));

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
                    string powerStr = (batteryStatus == 2) ? LocalizationService.Get("HwBatteryChargingAc", "Punjenje (AC Adapter)") : (batteryStatus == 1 ? LocalizationService.Get("HwBatteryOnBatteryDc", "Na bateriji (DC)") : LocalizationService.Get("HwBatteryAcOrBatt", "AC Mreža / Baterija"));
                    string cyclesStr = hasCycles ? string.Format("{0} ciklusa", cycleCount) : "N/A";
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (ViewModel.HardwareInfo != null)
                        {
                            ViewModel.HardwareInfo.BatteryWear = LocalizationService.Get("HwBatteryDetected", "Detektovana baterija");
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
                            ViewModel.HardwareInfo.BatteryCapacity = LocalizationService.Get("HwBatteryCapNaFixedAc", "N/A (Fiksno AC napajanje)");
                            ViewModel.HardwareInfo.BatteryCycles = "N/A";
                            ViewModel.HardwareInfo.BatteryPowerStatus = LocalizationService.Get("HwBatteryDesktopAc", "Desktop AC Napajanje");
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
                        ViewModel.HardwareInfo.BatteryPowerStatus = LocalizationService.Get("HwBatteryAcPower", "AC Napajanje");
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

                string resClass = LabelPrintService.ClassifyResolution(screenW, screenH);
                string formatTag = (!string.IsNullOrEmpty(resClass) && resClass.IndexOf('x') < 0)
                    ? string.Format("({0})", resClass)
                    : "";

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
                        ? string.Format(LocalizationService.Get("HwTouchYes", "🖐️ Podržan ({0} tačaka dodira)"), touches)
                        : LocalizationService.Get("HwTouchYesSimple", "🖐️ Podržan (Touchscreen)");
                    touchBrush = BaseViewModel.CreateFrozenBrush(78, 201, 176);
                }
                else
                {
                    touchStatusStr = LocalizationService.Get("HwTouchNo", "Nije detektovan (Standardni displej)");
                    touchBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);
                }

                string panelType = DetectDisplayPanelType(screenW, screenH, ViewModel.IsLaptop);

                string displayModel = "";
                int displayNits = 0;
                int displaySrgb = 0;
                int displayNtsc = 0;
                double displayDiagonalInches = 0.0;
                GetEdidDisplayDetails(out displayModel, out displayNits, out displaySrgb, out displayNtsc, out displayDiagonalInches);

                string displaySpecStr = "";
                if (!string.IsNullOrEmpty(displayModel) && displayModel != "Standardni panel")
                {
                    displaySpecStr = displayModel + " • ";
                }
                displaySpecStr += string.Format("{0}x{1} @ {2}Hz {3}", screenW, screenH, refreshRate, formatTag).Trim();
                if (displayDiagonalInches > 0.0)
                {
                    displaySpecStr += string.Format(System.Globalization.CultureInfo.InvariantCulture, " • {0:0.#}\"", displayDiagonalInches);
                }
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
                        ViewModel.HardwareInfo.DisplayWidth = screenW;
                        ViewModel.HardwareInfo.DisplayHeight = screenH;
                        ViewModel.HardwareInfo.DisplayRefreshHz = refreshRate;
                        ViewModel.HardwareInfo.DisplayPanelType = panelType ?? "";
                        ViewModel.HardwareInfo.DisplayDiagonalInches = displayDiagonalInches;
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
                        ViewModel.HardwareInfo.DisplayDiagonalInches = 0.0;
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
                                    secureBootStr = (enabled == 1) ? LocalizationService.Get("HwSecureBootOn", "Omogućen (SecureBoot ON)") : LocalizationService.Get("HwSecureBootOff", "Onemogućen (SecureBoot OFF)");
                                }
                            }
                        }
                    }
                }
                catch { }

                string biosFull = !string.IsNullOrEmpty(biosDate)
                    ? string.Format("{0} ({1})", biosVer, biosDate)
                    : biosVer;

                string finalMbProd = !string.IsNullOrEmpty(bbProd) ? bbProd : LocalizationService.Get("HwMbStandard", "Standardna matična ploča");
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
                        ViewModel.HardwareInfo.MotherboardModel = LocalizationService.Get("HwMbDetectError", "Greška pri detekciji ploče");
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

                bool? backlitConfirmed = null;
                string backlitReason = LocalizationService.Get("HwKbUnconfirmed", "Nije potvrđeno (provera u Fazi 5)");
                bool backlitFromHwTestFile = false;

                bool isLaptopForBacklit = false;
                try
                {
                    isLaptopForBacklit = ViewModel != null && ViewModel.IsLaptop;
                }
                catch { }

                bool hasExternalAuthBacklit = false;
                bool? existingAuthConfirmed = null;
                string existingAuthReason = "";
                try
                {
                    if (ViewModel != null && ViewModel.HardwareInfo != null &&
                        ViewModel.HardwareInfo.HasAuthoritativeBacklitVerdict &&
                        ViewModel.HardwareInfo.BacklitKeyboardConfirmed.HasValue)
                    {
                        hasExternalAuthBacklit = true;
                        existingAuthConfirmed = ViewModel.HardwareInfo.BacklitKeyboardConfirmed;
                        existingAuthReason = ViewModel.HardwareInfo.BacklitKeyboardReason;
                    }
                }
                catch { }

                DetectKeyboardBacklightConservative(
                    isLaptopForBacklit,
                    hasExternalAuthBacklit,
                    existingAuthConfirmed,
                    existingAuthReason,
                    out backlitConfirmed,
                    out backlitReason,
                    out backlitFromHwTestFile);

                string fpStatus = LocalizationService.Instance.GetString("HwFpNo", "Nije detektovan");
                string fpDetectedName = "";
                bool hasFp = false;
                SolidColorBrush fpBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, Description, DeviceID, PNPClass, Service, ConfigManagerErrorCode FROM Win32_PnPEntity"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                            string desc = obj["Description"] != null ? obj["Description"].ToString().Trim() : "";
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                            string pnpClass = obj["PNPClass"] != null ? obj["PNPClass"].ToString().Trim() : "";
                            string svc = obj["Service"] != null ? obj["Service"].ToString().Trim() : "";
                            uint errCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;

                            bool isBiometricClass;
                            if (!WIS.HwTest.Core.HardwareProbe.IsFingerprintPnpCandidate(name, desc, devId, pnpClass, svc, errCode, out isBiometricClass))
                            {
                                continue;
                            }

                            string fpCandName = !string.IsNullOrEmpty(name) ? name : desc;
                            if (errCode == 0 && !string.IsNullOrEmpty(fpCandName))
                            {
                                hasFp = true;
                                fpDetectedName = fpCandName;
                                fpStatus = string.Format(LocalizationService.Instance.GetString("HwFpYes", "👆 Da ({0})"), fpCandName);
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

                bool hasWifi = false;
                string wifiAdapterName = "";
                string wifiStandardLabel = "";
                bool hasBluetooth = false;
                string bluetoothInfo = "";
                string bluetoothVersionLabel = "";
                bool hasWwan = false;
                string wwanInfo = "";
                string wwanAdapterName = "";
                bool hasEsim = false;
                string esimInfo = "";
                bool hasSmartCard = false;
                bool hasNfc = false;
                bool hasThunderbolt = false;

                DetectWifiAndExtraPeripherals(
                    out hasWifi,
                    out wifiAdapterName,
                    out wifiStandardLabel,
                    out hasBluetooth,
                    out bluetoothInfo,
                    out bluetoothVersionLabel,
                    out hasWwan,
                    out wwanInfo,
                    out wwanAdapterName,
                    out hasEsim,
                    out esimInfo,
                    out hasSmartCard,
                    out hasNfc,
                    out hasThunderbolt);

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

                        if (backlitFromHwTestFile)
                        {
                            ViewModel.HardwareInfo.ApplyBacklitKeyboardVerdict(backlitConfirmed, backlitReason);
                        }
                        else
                        {
                            ViewModel.HardwareInfo.ApplyDetectedBacklitKeyboard(backlitConfirmed, backlitReason);
                        }

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

                        ViewModel.HardwareInfo.WifiAdapterName = wifiAdapterName;
                        ViewModel.HardwareInfo.WifiStandardLabel = wifiStandardLabel;
                        ViewModel.HardwareInfo.HasWifi = hasWifi;
                        ViewModel.HardwareInfo.HasBluetooth = hasBluetooth;
                        ViewModel.HardwareInfo.BluetoothInfo = bluetoothInfo;
                        ViewModel.HardwareInfo.BluetoothVersionLabel = bluetoothVersionLabel;
                        ViewModel.HardwareInfo.WwanAdapterName = wwanAdapterName;
                        ViewModel.HardwareInfo.WwanInfo = wwanInfo;
                        ViewModel.HardwareInfo.HasWwan = hasWwan;
                        ViewModel.HardwareInfo.HasEsim = hasEsim;
                        ViewModel.HardwareInfo.EsimInfo = esimInfo;
                        ViewModel.HardwareInfo.HasSmartCard = hasSmartCard;
                        ViewModel.HardwareInfo.HasNfc = hasNfc;
                        ViewModel.HardwareInfo.HasThunderbolt = hasThunderbolt;

                        ViewModel.HardwareInfo.RebuildOverviewPeripherals(true);
                    }
                });
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HwDevPropKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
        private static extern int HwInfoLocateDevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
        private static extern int HwInfoGetDevNodePropertyW(
            uint dnDevInst,
            ref HwDevPropKey PropertyKey,
            out uint PropertyType,
            byte[] PropertyBuffer,
            ref uint PropertyBufferSize,
            uint ulFlags);

        private static void DetectKeyboardBacklightConservative(
            bool isLaptop,
            bool hasExternalAuth,
            bool? existingAuthConfirmed,
            string existingAuthReason,
            out bool? confirmed,
            out string reason,
            out bool fromHwTestFile)
        {
            confirmed = null;
            reason = LocalizationService.Get("HwKbUnconfirmed", "Nije potvrđeno (provera u Fazi 5)");
            fromHwTestFile = false;

            bool? fileConfirmed;
            string fileReason;
            if (TryReadBacklitVerdictFromHwTestFile(out fileConfirmed, out fileReason))
            {
                confirmed = fileConfirmed;
                reason = fileReason;
                fromHwTestFile = true;
                return;
            }

            if (hasExternalAuth && existingAuthConfirmed.HasValue)
            {
                confirmed = existingAuthConfirmed;
                reason = existingAuthReason;
                return;
            }

            // Pouzdani hardverski kontroleri (bez lažnih pozitiva poput LEN0071, DELLABCE, HP_BIOSSetting i Lenovo_SetKeyboardBacklightStatus)
            string pmDetail;
            int pmLevel;
            try
            {
                if (WIS.HwTest.Core.HardwareProbe.TryProbeLenovoIbmPmBacklight(out pmDetail, out pmLevel))
                {
                    confirmed = true;
                    reason = pmDetail;
                    return;
                }
            }
            catch { }

            string energyDetail;
            if (TryProbeLenovoEnergyDrvStrict(out energyDetail))
            {
                confirmed = true;
                reason = energyDetail;
                return;
            }

            string hidBacklightDetail;
            if (TryDetectKeyboardHidOrLampArray(out hidBacklightDetail))
            {
                confirmed = true;
                reason = hidBacklightDetail;
                return;
            }

            string dellDetail;
            if (TryDetectDellBiosBacklightStrict(out dellDetail))
            {
                confirmed = true;
                reason = dellDetail;
                return;
            }

            if (!isLaptop)
            {
                confirmed = false;
                reason = LocalizationService.Get("HwKbDesktopNone", "Desktop sistem (bez ugrađenog pozadinskog osvetljenja)");
                return;
            }

            confirmed = null;
            reason = LocalizationService.Get("HwKbUnconfirmed", "Nije potvrđeno (provera u Fazi 5)");
        }

        private static bool TryReadBacklitVerdictFromHwTestFile(out bool? confirmed, out string reason)
        {
            confirmed = null;
            reason = "";
            try
            {
                string path = @"C:\setup\hwtest-result.json";
                if (!File.Exists(path)) return false;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return false;

                JsonNode root = JsonHelper.Parse(json);
                if (root == null || !root.ContainsKey("tests") || !root["tests"].IsArray) return false;

                JsonNode tests = root["tests"];
                for (int i = 0; i < tests.Count; i++)
                {
                    JsonNode t = tests[i];
                    if (t == null) continue;
                    string id = t["id"].AsString("");
                    if (!string.Equals(id, "keyboard", StringComparison.OrdinalIgnoreCase)) continue;

                    JsonNode details = t["details"];
                    if (details == null || !details.IsObject) break;

                    string blPassed = details["backlight_passed"].AsString("").Trim();
                    if (string.Equals(blPassed, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(blPassed, "1", StringComparison.OrdinalIgnoreCase))
                    {
                        confirmed = true;
                        reason = LocalizationService.Get("HwTBacklitConfirmed", "Potvrđeno u Fazi 5 (Test tastature)");
                        return true;
                    }
                    if (string.Equals(blPassed, "no_hardware", StringComparison.OrdinalIgnoreCase))
                    {
                        confirmed = false;
                        reason = LocalizationService.Get("HwKbConfirmedNone", "Potvrđeno u Fazi 5 (Model nema pozadinsko osvetljenje)");
                        return true;
                    }
                    break;
                }
            }
            catch { }
            return false;
        }

        private static bool TryProbeLenovoEnergyDrvStrict(out string detail)
        {
            detail = "";
            IntPtr hDev = IntPtr.Zero;
            try
            {
                hDev = WIS.HwTest.Core.HardwareProbe.OpenKernelDeviceHandle(@"\\.\EnergyDrv");
                if (hDev == IntPtr.Zero) return false;

                uint[] queries = new uint[] { 0x01u, 0x32u };
                for (int i = 0; i < queries.Length; i++)
                {
                    uint inVal = queries[i];
                    uint outVal = 0;
                    uint bytesRet = 0;
                    if (WIS.HwTest.Interop.NativeMethods.DeviceIoControl(hDev, 0x83102144u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                        bytesRet == 4u &&
                        (outVal & 0x01u) != 0u)
                    {
                        detail = string.Format("EnergyDrv (0x83102144, q=0x{0:X}, res=0x{1:X})", inVal, outVal);
                        return true;
                    }
                }
            }
            catch { }
            finally
            {
                if (hDev != IntPtr.Zero)
                {
                    try { WIS.HwTest.Interop.NativeMethods.CloseHandle(hDev); } catch { }
                }
            }
            return false;
        }

        private static bool TryDetectKeyboardHidOrLampArray(out string detail)
        {
            detail = "";
            bool hasPhysicalKeyboard = false;
            bool hasLampArrayHid = false;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, PNPClass, Service, ConfigManagerErrorCode FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        uint errCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;
                        if (errCode != 0) continue;

                        string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                        string pnpClass = obj["PNPClass"] != null ? obj["PNPClass"].ToString().Trim() : "";
                        string service = obj["Service"] != null ? obj["Service"].ToString().Trim() : "";

                        string upName = name.ToUpperInvariant();
                        string upId = devId.ToUpperInvariant();
                        string upSvc = service.ToUpperInvariant();

                        if (string.Equals(pnpClass, "Keyboard", StringComparison.OrdinalIgnoreCase) ||
                            upId.Contains("HID_DEVICE_SYSTEM_KEYBOARD"))
                        {
                            hasPhysicalKeyboard = true;
                        }

                        if (upId.Contains("UP:0059") && (upId.Contains("HID\\") || upName.Contains("LAMP") || upName.Contains("LIGHTING")))
                        {
                            hasLampArrayHid = true;
                        }

                        if (upSvc.Contains("KBDBACKLIGHT") ||
                            ((upName.Contains("KEYBOARD") || upSvc.Contains("KBD") || upId.Contains("HID\\")) &&
                             (upName.Contains("BACKLIGHT") || upName.Contains("ILLUMINATED") || upName.Contains("RGB KEYBOARD"))))
                        {
                            detail = "PnP HID/KBD: " + (!string.IsNullOrEmpty(name) ? name : devId);
                            return true;
                        }
                    }
                }
            }
            catch { }

            if (hasPhysicalKeyboard && hasLampArrayHid)
            {
                detail = "Windows Dynamic Lighting / HID LampArray (UP:0059)";
                return true;
            }

            if (hasPhysicalKeyboard && TryCheckWinRtKeyboardLampArray())
            {
                detail = "Windows Dynamic Lighting / LampArray";
                return true;
            }

            return false;
        }

        private static bool TryCheckWinRtKeyboardLampArray()
        {
            try
            {
                Type lampArrayType = Type.GetType("Windows.Devices.Lights.LampArray, Windows, ContentType=WindowsRuntime", false);
                Type devInfoType = Type.GetType("Windows.Devices.Enumeration.DeviceInformation, Windows, ContentType=WindowsRuntime", false);
                if (lampArrayType == null || devInfoType == null) return false;

                System.Reflection.MethodInfo getSelector = lampArrayType.GetMethod(
                    "GetDeviceSelector",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                if (getSelector == null) return false;
                string selector = getSelector.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(selector)) return false;

                System.Reflection.MethodInfo findAll = devInfoType.GetMethod("FindAllAsync", new Type[] { typeof(string) });
                if (findAll == null) return false;
                object asyncOp = findAll.Invoke(null, new object[] { selector });
                object collection = AwaitWinRtSimple(asyncOp, 1500);
                System.Collections.IEnumerable items = collection as System.Collections.IEnumerable;
                if (items == null) return false;

                System.Reflection.MethodInfo fromId = lampArrayType.GetMethod("FromIdAsync", new Type[] { typeof(string) });
                if (fromId == null) return false;

                foreach (object devInfo in items)
                {
                    if (devInfo == null) continue;
                    System.Reflection.PropertyInfo idProp = devInfo.GetType().GetProperty("Id");
                    string id = idProp != null ? idProp.GetValue(devInfo, null) as string : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    object laOp = fromId.Invoke(null, new object[] { id });
                    object lampArray = AwaitWinRtSimple(laOp, 1500);
                    if (lampArray == null) continue;

                    try
                    {
                        System.Reflection.PropertyInfo kindProp = lampArray.GetType().GetProperty("LampArrayKind");
                        if (kindProp != null)
                        {
                            object kindVal = kindProp.GetValue(lampArray, null);
                            if (kindVal != null && Convert.ToInt32(kindVal) == 1)
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        IDisposable disp = lampArray as IDisposable;
                        if (disp != null) disp.Dispose();
                    }
                }
            }
            catch { }
            return false;
        }

        private static object AwaitWinRtSimple(object asyncOp, int timeoutMs)
        {
            if (asyncOp == null) return null;
            try
            {
                System.Reflection.PropertyInfo statusProp = asyncOp.GetType().GetProperty("Status");
                System.Reflection.MethodInfo getResults = asyncOp.GetType().GetMethod("GetResults");
                if (statusProp == null || getResults == null) return null;

                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    object stObj = statusProp.GetValue(asyncOp, null);
                    int st = stObj != null ? Convert.ToInt32(stObj) : -1;
                    // AsyncStatus.Completed == 1, Canceled == 2, Error == 3
                    if (st == 1)
                    {
                        return getResults.Invoke(asyncOp, null);
                    }
                    if (st == 2 || st == 3)
                    {
                        return null;
                    }
                    Thread.Sleep(25);
                }
            }
            catch { }
            return null;
        }

        private static bool TryDetectDellBiosBacklightStrict(out string detail)
        {
            detail = "";
            string[] dellScopes = new string[]
            {
                @"root\dcim\sysman|DCIM_BIOSEnumeration",
                @"root\dcim\sysman\biosattributes|EnumerationAttribute"
            };
            for (int sIdx = 0; sIdx < dellScopes.Length; sIdx++)
            {
                string[] sp = dellScopes[sIdx].Split('|');
                try
                {
                    using (ManagementObjectSearcher dellSearcher = new ManagementObjectSearcher(sp[0], "SELECT * FROM " + sp[1]))
                    {
                        foreach (ManagementObject obj in dellSearcher.Get())
                        {
                            string attrName = ExtractWmiValueString(obj, "AttributeName");
                            if (string.IsNullOrEmpty(attrName)) continue;

                            string upName = attrName.ToUpperInvariant();
                            if (!upName.Contains("KEYBOARD") ||
                                (!upName.Contains("BACKLIGHT") && !upName.Contains("ILLUMINATION")))
                            {
                                continue;
                            }

                            string curVal = ExtractWmiValueString(obj, "CurrentValue");
                            string possVal = ExtractWmiValueString(obj, "PossibleValues");
                            string upCur = curVal.ToUpperInvariant();
                            string upPoss = possVal.ToUpperInvariant();

                            if (string.IsNullOrEmpty(upPoss) && string.IsNullOrEmpty(upCur)) continue;
                            if (upCur.Contains("NOT SUPPORTED") || upCur.Contains("UNAVAILABLE")) continue;
                            if (upPoss.Contains("NOT SUPPORTED") && !upPoss.Contains("BRIGHT") && !upPoss.Contains("DIM")) continue;

                            bool hasActivePoss =
                                upPoss.Contains("BRIGHT") ||
                                upPoss.Contains("DIM") ||
                                upPoss.Contains("ENABLED") ||
                                upPoss.Contains("100%") ||
                                upPoss.Contains("75%") ||
                                upPoss.Contains("50%") ||
                                upPoss.Contains("25%") ||
                                Regex.IsMatch(upPoss, @"\bON\b");

                            bool hasActiveCur =
                                upCur.Contains("BRIGHT") ||
                                upCur.Contains("DIM") ||
                                upCur.Contains("ENABLED") ||
                                upCur.Contains("100%") ||
                                upCur.Contains("75%") ||
                                upCur.Contains("50%") ||
                                upCur.Contains("25%") ||
                                Regex.IsMatch(upCur, @"\bON\b");

                            if (hasActivePoss || hasActiveCur)
                            {
                                detail = string.Format("Dell BIOS ({0}: {1})", attrName, !string.IsNullOrEmpty(curVal) ? curVal : possVal);
                                return true;
                            }
                        }
                    }
                }
                catch { }
            }
            return false;
        }

        private static string ExtractWmiValueString(ManagementBaseObject obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return "";
            try
            {
                object v = obj[propName];
                if (v == null) return "";
                string[] arr = v as string[];
                if (arr != null) return string.Join(", ", arr).Trim();
                return v.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }

        private sealed class BluetoothCandidateInfo
        {
            public string Name;
            public string DeviceId;
            public string HardwareIds;
            public string Service;
            public int LmpVersion;
        }

        private static void DetectWifiAndExtraPeripherals(
            out bool hasWifi,
            out string wifiAdapterName,
            out string wifiStandardLabel,
            out bool hasBluetooth,
            out string bluetoothInfo,
            out string bluetoothVersionLabel,
            out bool hasWwan,
            out string wwanInfo,
            out string wwanAdapterName,
            out bool hasEsim,
            out string esimInfo,
            out bool hasSmartCard,
            out bool hasNfc,
            out bool hasThunderbolt)
        {
            hasWifi = false;
            wifiAdapterName = "";
            wifiStandardLabel = "";
            hasBluetooth = false;
            bluetoothInfo = "";
            bluetoothVersionLabel = "";
            hasWwan = false;
            wwanInfo = "";
            wwanAdapterName = "";
            hasEsim = false;
            esimInfo = "";
            hasSmartCard = false;
            hasNfc = false;
            hasThunderbolt = false;

            List<BluetoothCandidateInfo> btCandidates = new List<BluetoothCandidateInfo>();
            List<string> pnpEuiccHints = new List<string>();
            StringBuilder wwanIdCollector = new StringBuilder();
            string wwanFallbackCandidateName = "";

            // 1. Wi-Fi adapter i eventualni WWAN adapter iz Win32_NetworkAdapter
            try
            {
                using (ManagementObjectSearcher wifiSearcher = new ManagementObjectSearcher(
                    "SELECT Name, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True"))
                {
                    foreach (ManagementObject obj in wifiSearcher.Get())
                    {
                        string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        if (!hasWifi && IsPhysicalWifiAdapterName(name))
                        {
                            hasWifi = true;
                            wifiAdapterName = name;
                            wifiStandardLabel = FormatWifiStandardLabel(name);
                        }
                        if (!hasWwan && IsWwanModemName(name))
                        {
                            hasWwan = true;
                            wwanAdapterName = name;
                        }
                    }
                }
            }
            catch { }

            // 2. Jedinstven prolaz kroz Win32_PnPEntity za Net, Bluetooth (+LMP), WWAN (ime + PCI/USB ID), eUICC/eSIM, SmartCard, NFC
            try
            {
                using (ManagementObjectSearcher pnpSearcher = new ManagementObjectSearcher(
                    "SELECT Name, Description, DeviceID, HardwareID, PNPClass, Service, ConfigManagerErrorCode FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject obj in pnpSearcher.Get())
                    {
                        string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        string desc = obj["Description"] != null ? obj["Description"].ToString().Trim() : "";
                        string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                        string pnpClass = obj["PNPClass"] != null ? obj["PNPClass"].ToString().Trim() : "";
                        string service = obj["Service"] != null ? obj["Service"].ToString().Trim() : "";
                        uint errCode = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;

                        string[] hwIds = null;
                        try { hwIds = obj["HardwareID"] as string[]; } catch { }
                        string hwJoined = hwIds != null ? string.Join(" ", hwIds) : "";
                        string displayCandidate = !string.IsNullOrEmpty(name) ? name : desc;

                        if (string.Equals(pnpClass, "Net", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!hasWifi && IsPhysicalWifiAdapterName(displayCandidate))
                            {
                                hasWifi = true;
                                wifiAdapterName = displayCandidate;
                                wifiStandardLabel = FormatWifiStandardLabel(displayCandidate);
                            }
                            if (!hasWwan && IsWwanModemName(displayCandidate))
                            {
                                hasWwan = true;
                                wwanAdapterName = displayCandidate;
                            }
                        }

                        if (IsBluetoothPnpCandidate(displayCandidate, devId, pnpClass, service))
                        {
                            hasBluetooth = true;
                            int lmp = 0;
                            bool isPhysicalBtRadio = !string.IsNullOrEmpty(devId) &&
                                !devId.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase) &&
                                !devId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) &&
                                !devId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) &&
                                !devId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) &&
                                IsGoodBluetoothDisplayName(displayCandidate);
                            if (isPhysicalBtRadio)
                            {
                                TryGetBluetoothLmpVersion(obj, devId, out lmp);
                            }
                            btCandidates.Add(new BluetoothCandidateInfo
                            {
                                Name = displayCandidate,
                                DeviceId = devId,
                                HardwareIds = hwJoined,
                                Service = service,
                                LmpVersion = lmp
                            });
                        }

                        if (string.Equals(pnpClass, "SmartCardReader", StringComparison.OrdinalIgnoreCase) &&
                            displayCandidate.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            hasSmartCard = true;
                        }

                        if (string.Equals(pnpClass, "Proximity", StringComparison.OrdinalIgnoreCase) ||
                            displayCandidate.IndexOf("NFC", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasNfc = true;
                        }

                        if (displayCandidate.IndexOf("Thunderbolt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            displayCandidate.IndexOf("USB4", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasThunderbolt = true;
                        }

                        if (IsEuiccPnpCandidate(displayCandidate, devId, pnpClass, service, errCode))
                        {
                            pnpEuiccHints.Add(!string.IsNullOrEmpty(displayCandidate) ? displayCandidate : devId);
                        }

                        string hwidModelHint;
                        bool isAuxiliaryPort;
                        if (IsWwanPnpCandidate(displayCandidate, desc, devId, hwIds, pnpClass, service, out hwidModelHint, out isAuxiliaryPort))
                        {
                            if (!string.IsNullOrEmpty(devId)) wwanIdCollector.Append(devId).Append(' ');
                            if (!string.IsNullOrEmpty(hwJoined)) wwanIdCollector.Append(hwJoined).Append(' ');
                            if (!string.IsNullOrEmpty(displayCandidate)) wwanIdCollector.Append(displayCandidate).Append(' ');

                            string chosenWwanName = !string.IsNullOrEmpty(hwidModelHint) && IsGenericPnPDeviceName(displayCandidate)
                                ? hwidModelHint
                                : (!string.IsNullOrEmpty(displayCandidate) ? displayCandidate : hwidModelHint);

                            if (!isAuxiliaryPort)
                            {
                                hasWwan = true;
                                if (string.IsNullOrEmpty(wwanAdapterName) || IsGenericPnPDeviceName(wwanAdapterName))
                                {
                                    wwanAdapterName = chosenWwanName;
                                }
                            }
                            else if (string.IsNullOrEmpty(wwanFallbackCandidateName))
                            {
                                wwanFallbackCandidateName = chosenWwanName;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!hasWwan && !string.IsNullOrEmpty(wwanFallbackCandidateName))
            {
                hasWwan = true;
                wwanAdapterName = wwanFallbackCandidateName;
            }

            // 3. netsh mbn show interfaces za preciznu WWAN klasu (LTE / 5G / NR) i model
            string mbnInterfacesOutput = RunNetshCommand("mbn show interfaces", 3000);
            string mbnDataClass = "";
            string mbnModelDesc = "";
            if (TryParseNetshMbnInterface(mbnInterfacesOutput, out mbnModelDesc, out mbnDataClass))
            {
                hasWwan = true;
                if (!string.IsNullOrEmpty(mbnModelDesc) &&
                    (string.IsNullOrEmpty(wwanAdapterName) ||
                     IsGenericPnPDeviceName(wwanAdapterName) ||
                     mbnModelDesc.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    wwanAdapterName = mbnModelDesc;
                }
            }

            if (hasWwan)
            {
                wwanInfo = FormatWwanLabel(wwanAdapterName + " " + wwanIdCollector.ToString(), mbnDataClass);
            }

            // 4. Bluetooth verzija (DEVPKEY_Bluetooth_RadioLmpVersion -> SIG tabela -> combo adapter fallback)
            if (hasBluetooth)
            {
                ResolveBluetoothDetails(btCandidates, wifiAdapterName, out bluetoothVersionLabel, out bluetoothInfo);
            }

            // 5. eSIM podrška (strogo bez lažnih pozitiva)
            DetectEsimSupport(
                hasWwan,
                wwanAdapterName,
                wwanIdCollector.ToString(),
                mbnInterfacesOutput,
                pnpEuiccHints,
                out hasEsim,
                out esimInfo);
        }

        private static bool IsGenericPnPDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string s = name.Trim().ToUpperInvariant();
            return s == "PCI DEVICE" ||
                   s == "PCI SIMPLE COMMUNICATIONS CONTROLLER" ||
                   s == "UNKNOWN DEVICE" ||
                   s == "USB COMPOSITE DEVICE" ||
                   s == "NETWORK CONTROLLER" ||
                   s == "OTHER DEVICE";
        }

        private static bool IsBluetoothPnpCandidate(string name, string devId, string pnpClass, string service)
        {
            if (string.Equals(pnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(service, "BTHUSB", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(service))
            {
                string upSvc = service.ToUpperInvariant();
                if (upSvc == "IBTUSB" || upSvc == "BTHMTK" || upSvc == "RTKBTFILTER" || upSvc == "QCABT")
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsGoodBluetoothDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOf("Enumerator", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("RFCOMM", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Protocol", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Avrcp", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("A2dp", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Personal Area Network", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool TryGetBluetoothLmpVersion(ManagementObject pnpObj, string devId, out int lmpVersion)
        {
            lmpVersion = 0;
            if (string.IsNullOrEmpty(devId)) return false;

            // 1. Primarno: cfgmgr32 CM_Get_DevNode_PropertyW za DEVPKEY_Bluetooth_RadioLmpVersion ({A92F26CA-EDA7-4B1D-9DB2-27B68AA5A2EB}, PID 2)
            try
            {
                uint devInst = 0;
                if (HwInfoLocateDevNodeW(out devInst, devId, 0) == 0 && devInst != 0)
                {
                    HwDevPropKey key = new HwDevPropKey
                    {
                        fmtid = new Guid("A92F26CA-EDA7-4B1D-9DB2-27B68AA5A2EB"),
                        pid = 2u
                    };
                    uint propType = 0;
                    uint bufLen = 16;
                    byte[] buf = new byte[bufLen];
                    int cr = HwInfoGetDevNodePropertyW(devInst, ref key, out propType, buf, ref bufLen, 0);
                    if (cr == 0 && bufLen > 0)
                    {
                        int parsed = ParseLmpFromBytes(buf, (int)Math.Min(bufLen, (uint)buf.Length));
                        if (parsed >= 4 && parsed <= 20)
                        {
                            lmpVersion = parsed;
                            return true;
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback: WMI Win32_PnPEntity.GetDeviceProperties
            if (pnpObj != null)
            {
                try
                {
                    ManagementBaseObject inParams = pnpObj.GetMethodParameters("GetDeviceProperties");
                    inParams["devicePropertyKeys"] = new string[] { "{A92F26CA-EDA7-4B1D-9DB2-27B68AA5A2EB} 2" };
                    ManagementBaseObject outParams = pnpObj.InvokeMethod("GetDeviceProperties", inParams, null);
                    if (outParams != null)
                    {
                        ManagementBaseObject[] props = outParams["deviceProperties"] as ManagementBaseObject[];
                        if (props != null)
                        {
                            for (int i = 0; i < props.Length; i++)
                            {
                                object dataObj = props[i] != null ? props[i]["Data"] : null;
                                int parsed = ParseLmpFromObject(dataObj);
                                if (parsed >= 4 && parsed <= 20)
                                {
                                    lmpVersion = parsed;
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Fallback: Registry Properties ključ
            try
            {
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + devId + @"\Properties\{a92f26ca-eda7-4b1d-9db2-27b68aa5a2eb}\0002";
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(regPath))
                {
                    if (rk != null)
                    {
                        object val = rk.GetValue("");
                        int parsed = ParseLmpFromObject(val);
                        if (parsed >= 4 && parsed <= 20)
                        {
                            lmpVersion = parsed;
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static int ParseLmpFromBytes(byte[] buf, int len)
        {
            if (buf == null || len <= 0) return 0;
            if (len == 1) return buf[0];
            if (len == 2) return BitConverter.ToUInt16(buf, 0);
            if (len >= 4) return (int)BitConverter.ToUInt32(buf, 0);
            return 0;
        }

        private static int ParseLmpFromObject(object val)
        {
            if (val == null) return 0;
            byte[] bytes = val as byte[];
            if (bytes != null) return ParseLmpFromBytes(bytes, bytes.Length);
            try
            {
                int parsed = Convert.ToInt32(val);
                return parsed;
            }
            catch
            {
                return 0;
            }
        }

        private static string MapBluetoothLmpToVersion(int lmp)
        {
            switch (lmp)
            {
                case 6: return "4.0";
                case 7: return "4.1";
                case 8: return "4.2";
                case 9: return "5.0";
                case 10: return "5.1";
                case 11: return "5.2";
                case 12: return "5.3";
                case 13: return "5.4";
                case 14: return "6.0";
                default: return "";
            }
        }

        private static string GuessBluetoothVersionFromAdapter(string btName, string wifiName, string btIdSummary)
        {
            string combined = ((btName ?? "") + " " + (wifiName ?? "") + " " + (btIdSummary ?? "")).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(combined)) return "";

            if (combined.Contains("BE200") || combined.Contains("BE201") || combined.Contains("BE202") ||
                combined.Contains("QCNCM865") || combined.Contains("MT7927") || combined.Contains("VID_8087&PID_0036"))
            {
                return "5.4";
            }

            if (combined.Contains("AX211") || combined.Contains("AX210") || combined.Contains("AX411") ||
                combined.Contains("AX1675") || combined.Contains("AX1690") || combined.Contains("MT7922") ||
                combined.Contains("RZ616") || combined.Contains("WCN6856") || combined.Contains("RTL8852CE") ||
                combined.Contains("VID_8087&PID_0033") || combined.Contains("VID_8087&PID_0032"))
            {
                return "5.3";
            }

            if (combined.Contains("AX201") || combined.Contains("AX200") || combined.Contains("AX101") ||
                combined.Contains("AX1650") || combined.Contains("MT7921") || combined.Contains("RZ608") ||
                combined.Contains("RTL8852BE") || combined.Contains("RTL8852AE") || combined.Contains("RTL8852") ||
                combined.Contains("VID_8087&PID_0026") || combined.Contains("VID_8087&PID_0029"))
            {
                return "5.2";
            }

            if (combined.Contains("9560") || combined.Contains("9260") ||
                combined.Contains("VID_8087&PID_0AAA") || combined.Contains("VID_8087&PID_0025"))
            {
                return "5.1";
            }

            if (combined.Contains("8265") || combined.Contains("8260") || combined.Contains("VID_8087&PID_0A2B"))
            {
                return "4.2";
            }

            return "";
        }

        private static void ResolveBluetoothDetails(
            List<BluetoothCandidateInfo> candidates,
            string wifiAdapterName,
            out string bluetoothVersionLabel,
            out string bluetoothInfo)
        {
            bluetoothVersionLabel = "Bluetooth";
            bluetoothInfo = "Bluetooth adapter detektovan";

            string bestName = "";
            int bestLmp = 0;
            StringBuilder idSummary = new StringBuilder();

            for (int i = 0; i < candidates.Count; i++)
            {
                BluetoothCandidateInfo c = candidates[i];
                if (!string.IsNullOrEmpty(c.DeviceId)) idSummary.Append(c.DeviceId).Append(' ');
                if (!string.IsNullOrEmpty(c.HardwareIds)) idSummary.Append(c.HardwareIds).Append(' ');

                bool isPhysicalRadio = !string.IsNullOrEmpty(c.DeviceId) &&
                    (c.DeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                     c.DeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                     c.DeviceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Service, "BTHUSB", StringComparison.OrdinalIgnoreCase));

                if (c.LmpVersion > 0 && (bestLmp == 0 || isPhysicalRadio))
                {
                    bestLmp = c.LmpVersion;
                }

                if (IsGoodBluetoothDisplayName(c.Name) && (string.IsNullOrEmpty(bestName) || isPhysicalRadio))
                {
                    bestName = c.Name;
                }
            }

            string baseName = !string.IsNullOrEmpty(bestName) ? bestName : "Bluetooth adapter";
            string btVer = bestLmp > 0 ? MapBluetoothLmpToVersion(bestLmp) : "";
            bool fromLmp = !string.IsNullOrEmpty(btVer);
            if (!fromLmp)
            {
                btVer = GuessBluetoothVersionFromAdapter(baseName, wifiAdapterName, idSummary.ToString());
            }

            if (!string.IsNullOrEmpty(btVer))
            {
                bluetoothVersionLabel = "Bluetooth " + btVer;
                bluetoothInfo = fromLmp
                    ? string.Format("{0} (LMP {1} — Bluetooth {2})", baseName, bestLmp, btVer)
                    : string.Format("{0} (Bluetooth {1})", baseName, btVer);
            }
            else
            {
                bluetoothVersionLabel = "Bluetooth";
                bluetoothInfo = !string.IsNullOrEmpty(bestName) ? bestName : "Bluetooth adapter detektovan";
            }
        }

        private static bool IsWwanPnpCandidate(
            string name,
            string desc,
            string devId,
            string[] hwIds,
            string pnpClass,
            string service,
            out string inferredModel,
            out bool isAuxiliaryPort)
        {
            inferredModel = "";
            isAuxiliaryPort = false;

            string upClass = (pnpClass ?? "").ToUpperInvariant();
            if (upClass == "MOUSE" || upClass == "KEYBOARD" || upClass == "HIDCLASS" ||
                upClass == "PRINTER" || upClass == "MONITOR" || upClass == "AUDIOENDPOINT" ||
                upClass == "MEDIA" || upClass == "CAMERA" || upClass == "IMAGE" ||
                upClass == "BLUETOOTH" || upClass == "BIOMETRIC" || upClass == "DISKDRIVE" ||
                upClass == "VOLUME" || upClass == "USBHUB")
            {
                return false;
            }

            string upName = (name ?? "").ToUpperInvariant();
            string upDesc = (desc ?? "").ToUpperInvariant();
            if (upName.Contains("ROOT_HUB") || upName.Contains("USB HUB") ||
                upName.Contains("MINIPORT") || upName.Contains("VIRTUAL") ||
                upName.Contains("LOOPBACK") || upName.Contains("WI-FI") ||
                upName.Contains("WIFI") || upName.Contains("ETHERNET") ||
                upName.Contains("BLUETOOTH"))
            {
                return false;
            }

            if (upName.Contains("GNSS") || upName.Contains("NMEA") || upName.Contains("AT PORT") ||
                upName.Contains("DM PORT") || upName.Contains("DIAGNOSTIC") || upName.Contains("LOCATION"))
            {
                isAuxiliaryPort = true;
            }

            if (IsWwanModemName(name) || IsWwanModemName(desc))
            {
                return true;
            }

            StringBuilder allIdsSb = new StringBuilder();
            if (!string.IsNullOrEmpty(devId)) allIdsSb.Append(devId.ToUpperInvariant()).Append(' ');
            if (hwIds != null)
            {
                for (int i = 0; i < hwIds.Length; i++)
                {
                    if (!string.IsNullOrEmpty(hwIds[i]))
                    {
                        allIdsSb.Append(hwIds[i].ToUpperInvariant()).Append(' ');
                    }
                }
            }
            string allIds = allIdsSb.ToString();
            if (string.IsNullOrWhiteSpace(allIds)) return false;

            if (allIds.Contains("USB\\ROOT_HUB")) return false;

            // Nedvosmisleni WWAN kontroleri
            if (allIds.Contains("VEN_8086&DEV_7560")) { inferredModel = "Intel XMM 7560 4G LTE"; return true; }
            if (allIds.Contains("VEN_8086&DEV_7360")) { inferredModel = "Intel XMM 7360 4G LTE"; return true; }
            if (allIds.Contains("VEN_14C3&DEV_4D75")) { inferredModel = "Fibocom FM350-GL 5G"; return true; }
            if (allIds.Contains("VID_2CB7")) { inferredModel = "Fibocom WWAN"; return true; }
            if (allIds.Contains("VID_2C7C") || allIds.Contains("VEN_1EAC")) { inferredModel = "Quectel WWAN"; return true; }
            if (allIds.Contains("VID_1199")) { inferredModel = "Sierra Wireless WWAN"; return true; }

            // Qualcomm VEN_17CB pravi i Wi-Fi kartice (DEV_1100..1107); isključi Wi-Fi ID-eve
            if (allIds.Contains("VEN_17CB"))
            {
                bool isQualcommWifi =
                    allIds.Contains("DEV_1100") || allIds.Contains("DEV_1101") ||
                    allIds.Contains("DEV_1102") || allIds.Contains("DEV_1103") ||
                    allIds.Contains("DEV_1104") || allIds.Contains("DEV_1107") ||
                    allIds.Contains("DEV_003E") || allIds.Contains("DEV_0042") ||
                    IsPhysicalWifiAdapterName(name);
                if (!isQualcommWifi)
                {
                    inferredModel = "Qualcomm Snapdragon WWAN";
                    return true;
                }
            }

            // Foxconn VID_0489 / Dell VID_413C / HP VID_03F0 — strogo samo WWAN PID-evi ili Modem/MBIM kontekst
            bool isModemOrNet = upClass == "MODEM" || upClass == "NET" ||
                                (service ?? "").ToUpperInvariant().Contains("MBIM") ||
                                (service ?? "").ToUpperInvariant().Contains("QMI") ||
                                upName.Contains("MBIM") || upDesc.Contains("MBIM");

            if (allIds.Contains("VID_0489"))
            {
                bool knownFoxconnWwan =
                    allIds.Contains("PID_E0B4") || allIds.Contains("PID_E0B5") ||
                    allIds.Contains("PID_E0C3") || allIds.Contains("PID_E0D8") ||
                    allIds.Contains("PID_E0DA") || allIds.Contains("PID_E0E5") ||
                    allIds.Contains("PID_E0F5") || isModemOrNet;
                if (knownFoxconnWwan && !IsPhysicalWifiAdapterName(name))
                {
                    inferredModel = "Foxconn WWAN";
                    return true;
                }
            }

            if (allIds.Contains("VID_413C"))
            {
                bool knownDellWwan =
                    allIds.Contains("PID_81B6") || allIds.Contains("PID_81C2") ||
                    allIds.Contains("PID_81CC") || allIds.Contains("PID_81D7") ||
                    allIds.Contains("PID_81E0") || allIds.Contains("PID_81E4") ||
                    allIds.Contains("PID_81E6") || allIds.Contains("PID_81F6") ||
                    allIds.Contains("PID_8201") || allIds.Contains("PID_8207") ||
                    (isModemOrNet && !IsPhysicalWifiAdapterName(name));
                if (knownDellWwan)
                {
                    inferredModel = "Dell Wireless WWAN";
                    return true;
                }
            }

            if (allIds.Contains("VID_03F0"))
            {
                bool knownHpWwan =
                    allIds.Contains("PID_0857") || allIds.Contains("PID_0A6C") ||
                    allIds.Contains("PID_0B6C") || allIds.Contains("PID_066C") ||
                    allIds.Contains("PID_421D") || allIds.Contains("PID_521D") ||
                    allIds.Contains("PID_581D") || allIds.Contains("PID_681D") ||
                    allIds.Contains("PID_9D1D") || allIds.Contains("PID_A31D") ||
                    (isModemOrNet && !IsPhysicalWifiAdapterName(name));
                if (knownHpWwan)
                {
                    inferredModel = "HP Mobile Broadband";
                    return true;
                }
            }

            return false;
        }

        private static bool IsEuiccPnpCandidate(string name, string devId, string pnpClass, string service, uint errCode)
        {
            string upSvc = (service ?? "").ToUpperInvariant();
            string upName = (name ?? "").ToUpperInvariant();
            string upId = (devId ?? "").ToUpperInvariant();

            if (upId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                upName.Contains("VIRTUAL") || upName.Contains("EMULATOR") || upName.Contains("SIMULATOR"))
            {
                return false;
            }

            if (upSvc == "EUICC") return true;
            if (upId.Contains("EUICC")) return true;

            if (errCode == 0 &&
                (upName.Contains("EUICC") ||
                 upName.Contains("ESIM") ||
                 upName.Contains("EMBEDDED SIM") ||
                 upName.Contains("MICROSOFT UICC ISO DEVICE")))
            {
                return true;
            }
            return false;
        }

        private static string RunNetshCommand(string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (Process proc = new Process { StartInfo = psi })
                {
                    StringBuilder output = new StringBuilder();
                    proc.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            lock (output)
                            {
                                output.AppendLine(e.Data);
                            }
                        }
                    };

                    if (!proc.Start()) return "";
                    proc.BeginOutputReadLine();

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                    }
                    else
                    {
                        // Daj malo vremena asinhronom čitaču da isprazni bafer
                        proc.WaitForExit(200);
                    }

                    lock (output)
                    {
                        return output.ToString();
                    }
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool TryParseNetshMbnInterface(string mbnOutput, out string modelDesc, out string dataClass)
        {
            modelDesc = "";
            dataClass = "";
            if (string.IsNullOrWhiteSpace(mbnOutput)) return false;

            string upper = mbnOutput.ToUpperInvariant();
            if (upper.Contains("THERE IS NO MOBILE BROADBAND INTERFACE") ||
                upper.Contains("NO MOBILE BROADBAND INTERFACE") ||
                upper.Contains("NOT RUNNING") ||
                upper.Contains("NIJE POKRENUT"))
            {
                return false;
            }

            string desc = "";
            string model = "";
            string mfg = "";
            string cls = "";
            bool hasInterfaceFields = false;

            string[] lines = mbnOutput.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                string key = line.Substring(0, colonIdx).Trim().ToUpperInvariant();
                string val = line.Substring(colonIdx + 1).Trim();
                if (string.IsNullOrEmpty(val)) continue;

                if (key == "DESCRIPTION")
                {
                    desc = val;
                    hasInterfaceFields = true;
                }
                else if (key == "MODEL")
                {
                    model = val;
                    hasInterfaceFields = true;
                }
                else if (key == "MANUFACTURER")
                {
                    mfg = val;
                    hasInterfaceFields = true;
                }
                else if (key == "DATA CLASS")
                {
                    cls = val;
                    hasInterfaceFields = true;
                }
                else if (key == "DEVICE ID" || key == "PHYSICAL ADDRESS")
                {
                    hasInterfaceFields = true;
                }
            }

            if (!hasInterfaceFields) return false;

            dataClass = cls;
            if (!string.IsNullOrEmpty(desc) && desc.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0)
            {
                modelDesc = desc;
            }
            else if (!string.IsNullOrEmpty(mfg) && !string.IsNullOrEmpty(model))
            {
                modelDesc = mfg + " " + model;
            }
            else if (!string.IsNullOrEmpty(model))
            {
                modelDesc = model;
            }
            else
            {
                modelDesc = desc;
            }

            return true;
        }

        private static void DetectEsimSupport(
            bool hasWwan,
            string wwanAdapterName,
            string wwanDeviceIds,
            string mbnInterfacesOutput,
            List<string> pnpEuiccHints,
            out bool hasEsim,
            out string esimInfo)
        {
            hasEsim = false;
            esimInfo = "";

            // 1. Primarno (a): Win32_PnPEntity (eUICC / eSIM / Embedded SIM / Microsoft UICC ISO Device)
            if (pnpEuiccHints != null && pnpEuiccHints.Count > 0)
            {
                hasEsim = true;
                esimInfo = string.Format(LocalizationService.Get("HwEsimSupportedPnp", "Podržan eSIM (PnP: {0})"), pnpEuiccHints[0]);
                return;
            }

            // 2. Primarno (b): netsh mbn show interfaces + show readyinfo (samo ako postoji WWAN/MBN interfejs)
            bool hasMbnInterface = !string.IsNullOrWhiteSpace(mbnInterfacesOutput) &&
                mbnInterfacesOutput.IndexOf("no Mobile Broadband interface", StringComparison.OrdinalIgnoreCase) < 0 &&
                mbnInterfacesOutput.IndexOf("not running", StringComparison.OrdinalIgnoreCase) < 0;

            if (hasMbnInterface || hasWwan)
            {
                string readyOutput = hasMbnInterface ? RunNetshCommand("mbn show readyinfo interface=*", 3000) : "";
                string combinedMbn = (mbnInterfacesOutput ?? "") + "\n" + (readyOutput ?? "");
                string mbnReason;
                if (HasEsimIndicatorInMbnOutput(combinedMbn, out mbnReason))
                {
                    hasEsim = true;
                    esimInfo = mbnReason;
                    return;
                }
            }

            // 3. Primarno (c): Registry HKLM\SOFTWARE\Microsoft\WwanSvc\Interfaces (EmbeddedSim / EID / eUICC)
            string regReason;
            if (TryDetectEsimFromWwanSvcRegistry(out regReason))
            {
                hasEsim = true;
                esimInfo = regReason;
                return;
            }

            // 4. Sekundarno: poznati eSIM-sposobni WWAN moduli — STROGO samo ako je fizička 4G/5G WWAN kartica prisutna
            if (hasWwan)
            {
                string combinedWwan = ((wwanAdapterName ?? "") + " " + (wwanDeviceIds ?? "") + " " + (mbnInterfacesOutput ?? "")).ToUpperInvariant();
                string[] knownEsimModules = new string[]
                {
                    "FM350-GL", "L860-GL-16", "EM05-G", "EM120R-GL", "EM160R-GL",
                    "RM520N-GL", "T99W175", "T99W373", "DW5930E", "DW5931E",
                    "DW5821E", "EM9190"
                };
                for (int i = 0; i < knownEsimModules.Length; i++)
                {
                    if (combinedWwan.Contains(knownEsimModules[i]))
                    {
                        hasEsim = true;
                        esimInfo = string.Format(LocalizationService.Get("HwEsimSupportedWwan", "Podržan eSIM (WWAN modul {0})"), knownEsimModules[i]);
                        return;
                    }
                }
            }
        }

        private static bool HasEsimIndicatorInMbnOutput(string mbnText, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(mbnText)) return false;

            string[] lines = mbnText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string key = line.Substring(0, colon).Trim().ToUpperInvariant();
                string val = line.Substring(colon + 1).Trim();
                if (string.IsNullOrEmpty(val)) continue;
                string upVal = val.ToUpperInvariant();

                if (key == "EID" || key.Contains("EUICC ID"))
                {
                    if (IsValidEidString(val))
                    {
                        reason = LocalizationService.Get("HwEsimSupportedEid", "Podržan eSIM (EID detektovan)");
                        return true;
                    }
                }
                else if (key.Contains("EMBEDDED") || key.Contains("ESIM") || key.Contains("EUICC"))
                {
                    if (upVal == "YES" || upVal == "TRUE" || upVal == "1" || upVal.Contains("SUPPORTED") || upVal.Contains("EMBEDDED"))
                    {
                        reason = LocalizationService.Get("HwEsimSupportedEuiccSlot", "Podržan eSIM (eUICC slot)");
                        return true;
                    }
                }
                else if (key.Contains("SLOT") && (upVal.Contains("EMBEDDED") || upVal.Contains("EUICC") || upVal.Contains("ESIM")))
                {
                    reason = LocalizationService.Get("HwEsimSupportedEmbeddedSlot", "Podržan eSIM (Embedded SIM slot)");
                    return true;
                }
            }
            return false;
        }

        private static bool IsValidEidString(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return false;
            string clean = val.Trim();
            string up = clean.ToUpperInvariant();
            if (up.Contains("NOT AVAILABLE") || up.Contains("NOT SUPPORTED") || up == "NONE" || up == "N/A" || up == "UNKNOWN")
            {
                return false;
            }
            int hexDigits = 0;
            bool allZeros = true;
            for (int i = 0; i < clean.Length; i++)
            {
                char ch = clean[i];
                if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))
                {
                    hexDigits++;
                    if (ch != '0') allZeros = false;
                }
            }
            return hexDigits >= 16 && !allZeros;
        }

        private static bool TryDetectEsimFromWwanSvcRegistry(out string reason)
        {
            reason = "";
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WwanSvc\Interfaces"))
                {
                    if (root == null) return false;
                    string[] subNames = root.GetSubKeyNames();
                    for (int i = 0; i < subNames.Length; i++)
                    {
                        using (RegistryKey ifaceKey = root.OpenSubKey(subNames[i]))
                        {
                            if (ScanWwanSvcKeyForEsim(ifaceKey, 0, out reason))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool ScanWwanSvcKeyForEsim(RegistryKey key, int depth, out string reason)
        {
            reason = "";
            if (key == null || depth > 3) return false;

            try
            {
                string[] valNames = key.GetValueNames();
                for (int i = 0; i < valNames.Length; i++)
                {
                    string vn = valNames[i] ?? "";
                    string upVn = vn.ToUpperInvariant();
                    object raw = key.GetValue(vn);
                    if (raw == null) continue;

                    if (upVn == "EID" || upVn.Contains("UICCEID") || upVn.Contains("EUICC"))
                    {
                        byte[] rawBytes = raw as byte[];
                        string strVal = rawBytes != null
                            ? BitConverter.ToString(rawBytes).Replace("-", "")
                            : raw.ToString();
                        if (IsValidEidString(strVal))
                        {
                            reason = LocalizationService.Get("HwEsimSupportedWwanSvcEid", "Podržan eSIM (WwanSvc EID)");
                            return true;
                        }
                    }

                    if (upVn == "EMBEDDEDSIM" || upVn == "ISEMBEDDEDSIM" || upVn == "ISEMBEDDED")
                    {
                        int num = 0;
                        if (int.TryParse(raw.ToString(), out num) && num == 1)
                        {
                            reason = LocalizationService.Get("HwEsimSupportedWwanSvcEmbedded", "Podržan eSIM (WwanSvc EmbeddedSim)");
                            return true;
                        }
                        if (string.Equals(raw.ToString(), "True", StringComparison.OrdinalIgnoreCase))
                        {
                            reason = LocalizationService.Get("HwEsimSupportedWwanSvcEmbedded", "Podržan eSIM (WwanSvc EmbeddedSim)");
                            return true;
                        }
                    }
                }

                string[] subKeys = key.GetSubKeyNames();
                for (int i = 0; i < subKeys.Length; i++)
                {
                    string subName = subKeys[i] ?? "";
                    using (RegistryKey sk = key.OpenSubKey(subName))
                    {
                        if (sk == null) continue;
                        if (ScanWwanSvcKeyForEsim(sk, depth + 1, out reason))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsWwanModemName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string s = name.ToUpperInvariant();
            if (s.Contains("MINIPORT") || s.Contains("VIRTUAL") || s.Contains("LOOPBACK") ||
                s.Contains("BLUETOOTH") || s.Contains("WI-FI") || s.Contains("WIFI") ||
                s.Contains("ETHERNET") || s.Contains("SENSOR") || s.Contains("GNSS") ||
                s.Contains("NMEA") || s.Contains("AT PORT") || s.Contains("DM PORT") ||
                s.Contains("LOCATION"))
            {
                return false;
            }
            return s.Contains("WWAN") || s.Contains("MOBILE BROADBAND") || s.Contains("CELLULAR") ||
                   s.Contains("4G LTE") || s.Contains("5G MODEM") || s.Contains("5G NR") ||
                   s.Contains("MBIM") || s.Contains("LTE ") || s.Contains(" LTE") ||
                   s.Contains("FIBOCOM") || s.Contains("QUECTEL") || s.Contains("SIERRA WIRELESS") ||
                   s.Contains("XMM 7") || s.Contains("XMM7") ||
                   Regex.IsMatch(s, @"\b(HP\s+LT4[12]|LT4[12]\d*|EM0[56]|EM12\d*|EM160\S*)\b") ||
                   s.Contains("DW58") || s.Contains("DW59") || s.Contains("T77W") || s.Contains("T99W") ||
                   s.Contains("EM74") || s.Contains("EM75") || s.Contains("EM91") ||
                   s.Contains("FM350") || s.Contains("FM101") || s.Contains("L850") || s.Contains("L860") ||
                   s.Contains("RM520") || s.Contains("SNAPDRAGON X55") || s.Contains("SNAPDRAGON X62") ||
                   s.Contains("SNAPDRAGON X65") || s.Contains("SNAPDRAGON X20") || s.Contains("SNAPDRAGON X24");
        }

        private static string FormatWwanLabel(string rawName, string mbnDataClass = "")
        {
            string raw = (rawName ?? "").Trim();
            string s = raw.ToUpperInvariant();
            string upData = (mbnDataClass ?? "").ToUpperInvariant();

            bool is5G = upData.Contains("5G") || Regex.IsMatch(upData, @"\bNR\b") ||
                        s.Contains("5G") || s.Contains("FM350") || s.Contains("RM520") ||
                        s.Contains("EM9190") || s.Contains("DW5930") || s.Contains("DW5931") ||
                        s.Contains("DW5932") || s.Contains("T99W175") || s.Contains("T99W373") ||
                        s.Contains("X55") || s.Contains("X62") || s.Contains("X65");
            string genShort = is5G ? "5G" : "4G LTE";

            string brand = "";
            if (s.Contains("FIBOCOM")) brand = "Fibocom";
            else if (s.Contains("QUECTEL")) brand = "Quectel";
            else if (s.Contains("SIERRA")) brand = "Sierra";
            else if (s.Contains("INTEL") || s.Contains("XMM")) brand = "Intel";
            else if (s.Contains("SNAPDRAGON") || s.Contains("QUALCOMM")) brand = "Qualcomm";
            else if (s.Contains("DELL") || s.Contains("DW58") || s.Contains("DW59")) brand = "Dell";
            else if (s.Contains("HP ") || s.Contains("LT41") || s.Contains("LT42")) brand = "HP";
            else if (s.Contains("FOXCONN") || s.Contains("T77W") || s.Contains("T99W")) brand = "Foxconn";

            string[] models = new string[]
            {
                "FM350-GL", "FM350", "FM101",
                "L860-GL-16", "L860-GL", "L860", "L850-GL", "L850",
                "RM520N-GL", "RM520",
                "EM9190", "EM7565", "EM7455", "EM7430", "EM160R-GL", "EM160", "EM120R-GL", "EM120", "EM12", "EM06", "EM05-G", "EM05",
                "DW5932", "DW5931E", "DW5930E", "DW5931", "DW5930", "DW5829", "DW5821E", "DW5820E", "DW5821", "DW5820", "DW5814", "DW5811",
                "T99W373", "T99W175", "T77W968",
                "LT4211", "LT4210", "LT4132", "LT4120", "LT42", "LT41",
                "XMM 7560", "XMM 7360", "XMM7560", "XMM7360",
                "X65", "X62", "X55", "X24", "X20"
            };
            string model = "";
            for (int i = 0; i < models.Length; i++)
            {
                if (s.Contains(models[i]))
                {
                    model = models[i];
                    break;
                }
            }

            if (!string.IsNullOrEmpty(model))
            {
                string detail = string.IsNullOrEmpty(brand) ? model : (brand + " " + model);
                return string.Format("{0} ({1})", genShort, detail);
            }
            return is5G ? "5G kartica" : "4G LTE kartica";
        }

        private static bool IsPhysicalWifiAdapterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string s = name.ToUpperInvariant();
            if (s.Contains("VIRTUAL") || s.Contains("WI-FI DIRECT") || s.Contains("WAN MINIPORT") ||
                s.Contains("HYPER-V") || s.Contains("VETHERNET") || s.Contains("LOOPBACK") ||
                s.Contains("BLUETOOTH") || s.Contains("TAP-") || s.Contains("VPN") ||
                s.Contains("ETHERNET") || s.Contains("GBE FAMILY"))
            {
                return false;
            }
            return s.Contains("WI-FI") || s.Contains("WIFI") || s.Contains("WIRELESS") ||
                   s.Contains("802.11") || s.Contains("AX2") || s.Contains("AX1") || s.Contains("BE2") ||
                   s.Contains("MT79") || s.Contains("RTL885") || s.Contains("RTL882") ||
                   s.Contains("QCNCM") || s.Contains("WCN685") || s.Contains("QCA61") ||
                   s.Contains("QCA93") || s.Contains("CENTRINO");
        }

        private static string FormatWifiStandardLabel(string adapterName)
        {
            string raw = (adapterName ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) return "Wi-Fi";
            string s = raw.ToUpperInvariant();

            string brand = "";
            if (s.Contains("INTEL")) brand = "Intel";
            else if (s.Contains("REALTEK")) brand = "Realtek";
            else if (s.Contains("MEDIATEK")) brand = "MediaTek";
            else if (s.Contains("QUALCOMM") || s.Contains("ATHEROS")) brand = "Qualcomm";
            else if (s.Contains("KILLER")) brand = "Killer";
            else if (s.Contains("BROADCOM")) brand = "Broadcom";

            string[] modelTokens = new string[]
            {
                "BE200", "BE201", "BE202", "QCNCM865", "MT7927",
                "AX211", "AX210", "AX411", "AX1690", "AX1675", "RZ616", "MT7922", "WCN6856", "WCN685",
                "AX201", "AX200", "AX101", "AX1650", "MT7921", "RZ608", "RTL8852CE", "RTL8852BE", "RTL8852AE", "RTL8852",
                "9560", "9260", "8265", "8260", "7265", "7260", "3168", "3165",
                "RTL8822CE", "RTL8822BE", "RTL8821CE", "QCA6174", "QCA9377"
            };
            string model = "";
            for (int i = 0; i < modelTokens.Length; i++)
            {
                if (s.Contains(modelTokens[i]))
                {
                    model = modelTokens[i];
                    break;
                }
            }

            string gen = "";
            if (s.Contains("BE200") || s.Contains("BE201") || s.Contains("BE202") || s.Contains("MT7927") ||
                s.Contains("QCNCM865") || s.Contains("WI-FI 7") || s.Contains("WIFI 7") || s.Contains("802.11BE"))
            {
                gen = "Wi-Fi 7";
            }
            else if (s.Contains("AX210") || s.Contains("AX211") || s.Contains("AX411") || s.Contains("AX1675") ||
                     s.Contains("AX1690") || s.Contains("RZ616") || s.Contains("MT7922") || s.Contains("WCN685") ||
                     s.Contains("WI-FI 6E") || s.Contains("WIFI 6E"))
            {
                gen = "Wi-Fi 6E";
            }
            else if (s.Contains("AX200") || s.Contains("AX201") || s.Contains("AX101") || s.Contains("AX1650") ||
                     s.Contains("MT7921") || s.Contains("RZ608") || s.Contains("RTL8852") ||
                     s.Contains("WI-FI 6") || s.Contains("WIFI 6") || s.Contains("802.11AX"))
            {
                gen = "Wi-Fi 6";
            }
            else if (s.Contains("9560") || s.Contains("9260") || s.Contains("8265") || s.Contains("8260") ||
                     s.Contains("7265") || s.Contains("7260") || s.Contains("3168") || s.Contains("3165") ||
                     s.Contains("RTL8822") || s.Contains("RTL8821") || s.Contains("QCA61") || s.Contains("QCA93") ||
                     s.Contains("WI-FI 5") || s.Contains("WIFI 5") || s.Contains("802.11AC") || s.Contains("WIRELESS-AC"))
            {
                gen = "Wi-Fi 5";
            }
            else if (s.Contains("802.11N") || s.Contains("WIRELESS-N") || s.Contains("CENTRINO"))
            {
                gen = "Wi-Fi 4";
            }

            if (!string.IsNullOrEmpty(gen) && !string.IsNullOrEmpty(model))
            {
                string detail = string.IsNullOrEmpty(brand) ? model : (brand + " " + model);
                return string.Format("{0} ({1})", gen, detail);
            }

            string clean = raw.Replace("(R)", "").Replace("(TM)", "").Replace("(r)", "").Replace("(tm)", "");
            clean = clean.Replace("Wireless Network Adapter", "").Replace("Network Adapter", "").Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            if (clean.Length > 34) clean = clean.Substring(0, 31) + "...";

            if (!string.IsNullOrEmpty(gen))
            {
                return string.IsNullOrEmpty(clean) ? gen : string.Format("{0} ({1})", gen, clean);
            }
            return string.IsNullOrEmpty(clean) ? "Wi-Fi adapter" : clean;
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

                if (rawText.Contains("OLED") || rawText.Contains("AMOLED") ||
                    rawText.Contains("ATNA") || rawText.Contains("SDC41") || rawText.Contains("SDC42") || rawText.Contains("AMS"))
                {
                    return "OLED";
                }

                if (rawText.Contains("IPS") || rawText.Contains("AHVA") || rawText.Contains("PLS") ||
                    rawText.Contains("ADS") || rawText.Contains("AH-IPS") || rawText.Contains("IGZO") ||
                    rawText.Contains("WVA") || rawText.Contains("UWVA"))
                {
                    return "IPS";
                }

                bool hasKnownIpsPrefix = rawText.Contains("NV13") || rawText.Contains("NV14") || rawText.Contains("NV15") || rawText.Contains("NV16") || rawText.Contains("NV17") ||
                                        rawText.Contains("LP13") || rawText.Contains("LP14") || rawText.Contains("LP15") || rawText.Contains("LP16") || rawText.Contains("LP17") ||
                                        rawText.Contains("B133HAN") || rawText.Contains("B140HAN") || rawText.Contains("B156HAN") || rawText.Contains("B173HAN") ||
                                        rawText.Contains("B160") || rawText.Contains("N133") || rawText.Contains("N160") ||
                                        rawText.Contains("NE13") || rawText.Contains("NE14") || rawText.Contains("NE15") || rawText.Contains("NE16") ||
                                        rawText.Contains("MNE") || rawText.Contains("MND") || rawText.Contains("MNF") ||
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

        private static void GetEdidDisplayDetails(out string model, out int nits, out int srgb, out int ntsc, out double diagonalInches)
        {
            model = "";
            nits = 0;
            srgb = 0;
            ntsc = 0;
            diagonalInches = 0.0;

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

            diagonalInches = DetectDisplayDiagonalInches(edidBytes);
        }

        private static double DetectDisplayDiagonalInches(byte[] edidBytes)
        {
            double rawInches = 0.0;

            try
            {
                // 1. Primarni izvor: EDID Detailed Timing Descriptor #1 (precizne dimenzije u mm)
                if (edidBytes != null && edidBytes.Length >= 72 && (edidBytes[54] != 0 || edidBytes[55] != 0))
                {
                    int wMm = edidBytes[66] | ((edidBytes[68] & 0xF0) << 4);
                    int hMm = edidBytes[67] | ((edidBytes[68] & 0x0F) << 8);
                    if (wMm >= 120 && wMm <= 1800 && hMm >= 70 && hMm <= 1100)
                    {
                        double candidate = Math.Sqrt((double)(wMm * wMm + hMm * hMm)) / 25.4;
                        if (candidate >= 8.0 && candidate <= 55.0)
                        {
                            rawInches = candidate;
                        }
                    }
                }
            }
            catch { }

            if (rawInches <= 0.0)
            {
                try
                {
                    // 2. WMI root\wmi -> WmiMonitorBasicDisplayParams (cm -> inči, prioritet Active == true)
                    var scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();
                    using (var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery("SELECT MaxHorizontalImageSize, MaxVerticalImageSize, Active FROM WmiMonitorBasicDisplayParams")))
                    {
                        double fallbackCandidate = 0.0;
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            int wCm = obj["MaxHorizontalImageSize"] != null ? Convert.ToInt32(obj["MaxHorizontalImageSize"]) : 0;
                            int hCm = obj["MaxVerticalImageSize"] != null ? Convert.ToInt32(obj["MaxVerticalImageSize"]) : 0;
                            bool isActive = obj["Active"] != null && Convert.ToBoolean(obj["Active"]);
                            if (wCm > 10 && hCm > 6)
                            {
                                double candidate = Math.Sqrt((double)(wCm * wCm + hCm * hCm)) / 2.54;
                                if (candidate >= 8.0 && candidate <= 55.0)
                                {
                                    if (isActive)
                                    {
                                        rawInches = candidate;
                                        break;
                                    }
                                    if (fallbackCandidate <= 0.0) fallbackCandidate = candidate;
                                }
                            }
                        }
                        if (rawInches <= 0.0 && fallbackCandidate > 0.0)
                        {
                            rawInches = fallbackCandidate;
                        }
                    }
                }
                catch { }
            }

            if (rawInches <= 0.0)
            {
                try
                {
                    // 3. Fallback: EDID header bajtovi 21 i 22 (cm)
                    if (edidBytes != null && edidBytes.Length >= 23)
                    {
                        int wCm = edidBytes[21];
                        int hCm = edidBytes[22];
                        if (wCm > 10 && hCm > 6)
                        {
                            double candidate = Math.Sqrt((double)(wCm * wCm + hCm * hCm)) / 2.54;
                            if (candidate >= 8.0 && candidate <= 55.0)
                            {
                                rawInches = candidate;
                            }
                        }
                    }
                }
                catch { }
            }

            return NormalizeDisplayDiagonalInches(rawInches);
        }

        private static double NormalizeDisplayDiagonalInches(double rawInches)
        {
            if (rawInches < 8.0 || rawInches > 55.0) return 0.0;

            double[] standardSizes = new double[]
            {
                10.1, 11.6, 12.3, 12.5, 13.0, 13.3, 13.5, 14.0, 14.5, 15.0,
                15.6, 16.0, 16.1, 17.0, 17.3, 18.0, 21.5, 23.8, 24.0, 27.0,
                31.5, 32.0, 34.0
            };

            double bestMatch = 0.0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < standardSizes.Length; i++)
            {
                double diff = Math.Abs(rawInches - standardSizes[i]);
                if (diff <= 0.25 && diff < bestDiff)
                {
                    bestDiff = diff;
                    bestMatch = standardSizes[i];
                }
            }

            if (bestMatch > 0.0) return bestMatch;
            return Math.Round(rawInches, 1);
        }
    }
}
