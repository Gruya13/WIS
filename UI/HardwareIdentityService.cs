using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace WIS.UI
{
    public enum HardwareUpdateSource
    {
        WmiFresh,
        Ipc,
        Autopilot,
        Manual
    }

    [Flags]
    public enum HwCategory
    {
        None = 0,
        Cpu = 1,
        Memory = 2,
        Storage = 4,
        Battery = 8,
        Gpu = 16,
        Display = 32,
        SystemIdentity = 64,
        Peripherals = 128,
        All = Cpu | Memory | Storage | Battery | Gpu | Display | SystemIdentity | Peripherals
    }

    public static class HardwareValueQuality
    {
        private static readonly HashSet<string> _placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "N/A",
            "NA",
            "NONE",
            "UNKNOWN",
            "TO BE FILLED BY O.E.M.",
            "TO BE FILLED BY OEM",
            "SYSTEM PRODUCT NAME",
            "SYSTEM MANUFACTURER",
            "DEFAULT STRING",
            "NOT APPLICABLE",
            "0123456789",
            "STANDARD PC",
            "MICROSOFT BASIC DISPLAY ADAPTER",
            "MICROSOFT BASIC RENDER DRIVER",
            "SYSTEM SERIAL NUMBER",
            "CHASSIS SERIAL NUMBER",
            "BASE BOARD SERIAL NUMBER",
            "TYPE2 - BOARD SERIAL NUMBER",
            "TYPE2-BOARD SERIAL NUMBER",
            "INVALID",
            "NULL"
        };

        public static bool IsPlaceholder(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return true;
            string trimmed = val.Trim();
            if (trimmed.Length == 0) return true;
            if (trimmed.StartsWith("Detekcija", StringComparison.OrdinalIgnoreCase)) return true;
            if (_placeholders.Contains(trimmed)) return true;
            return false;
        }
    }

    public static class HardwareIdentityService
    {
        private static readonly object _syncLock = new object();
        private static readonly Dictionary<string, HardwareUpdateSource> _fieldSources = new Dictionary<string, HardwareUpdateSource>(StringComparer.OrdinalIgnoreCase);

        public static bool TryUpdate(string field, string newValue, HardwareUpdateSource source, MainViewModel vm)
        {
            if (vm == null || string.IsNullOrEmpty(field)) return false;

            lock (_syncLock)
            {
                string currentValue = GetCurrentFieldValue(field, vm);
                bool currentIsPlaceholder = HardwareValueQuality.IsPlaceholder(currentValue);
                bool newIsPlaceholder = HardwareValueQuality.IsPlaceholder(newValue);

                // 1. Nova vrednost je placeholder, a trenutna je prava vrednost -> ODBIJA SE
                if (newIsPlaceholder && !currentIsPlaceholder)
                {
                    return false;
                }

                // 2. Obe vrednosti su placeholder
                if (newIsPlaceholder && currentIsPlaceholder)
                {
                    if (string.IsNullOrEmpty(currentValue) && !string.IsNullOrEmpty(newValue))
                    {
                        ApplyFieldValue(field, newValue, vm);
                        return true;
                    }
                    return false;
                }

                // 3. Trenutna je placeholder, a nova je prava vrednost -> PRIHVATA SE UVEK
                if (currentIsPlaceholder && !newIsPlaceholder)
                {
                    _fieldSources[field] = source;
                    ApplyFieldValue(field, newValue, vm);
                    return true;
                }

                // 4. Obe su prave vrednosti
                if (string.Equals(currentValue, newValue, StringComparison.Ordinal))
                {
                    if (source == HardwareUpdateSource.WmiFresh)
                    {
                        _fieldSources[field] = source;
                    }
                    return true;
                }

                HardwareUpdateSource currentSource;
                if (!_fieldSources.TryGetValue(field, out currentSource))
                {
                    currentSource = HardwareUpdateSource.Manual;
                }

                bool shouldAccept = false;

                if (source == HardwareUpdateSource.WmiFresh)
                {
                    // WmiFresh pobeđuje IPC jer je najsvežiji lokalni podatak
                    shouldAccept = true;
                }
                else if (source == HardwareUpdateSource.Ipc || source == HardwareUpdateSource.Autopilot)
                {
                    // Iz IPC/Autopilot prihvatamo samo ako trenutna vrednost NIJE potekla iz WmiFresh
                    if (currentSource != HardwareUpdateSource.WmiFresh)
                    {
                        shouldAccept = true;
                    }
                }
                else
                {
                    shouldAccept = true;
                }

                if (shouldAccept)
                {
                    _fieldSources[field] = source;
                    LogDebugToFile(string.Format("[HW_IDENTITY] Field '{0}' changed from '{1}' to '{2}' by source {3}", field, currentValue, newValue, source));
                    ApplyFieldValue(field, newValue, vm);
                    return true;
                }

                return false;
            }
        }

        private static string GetCurrentFieldValue(string field, MainViewModel vm)
        {
            switch (field)
            {
                case "ComputerModel": return vm.ComputerModel;
                case "SerialNumber": return vm.SerialNumber;
                case "GpuName": return vm.GpuName;
                case "GpuVram": return vm.GpuVram;
                case "GpuDriverVersion": return vm.GpuDriverVersion;
                case "DisplayResolution": return vm.DisplayResolution;
                case "DisplayRefreshRate": return vm.DisplayRefreshRate;
                case "DisplayDpiScale": return vm.DisplayDpiScale;
                case "MotherboardModel": return vm.MotherboardModel;
                case "BiosVersion": return vm.BiosVersion;
                case "BiosReleaseDate": return vm.BiosReleaseDate;
                case "SecureBootStatus": return vm.SecureBootStatus;
                case "CpuInfo": return vm.CpuInfo;
                case "RamInfo": return vm.RamInfo;
                case "DiskInfo": return vm.DiskInfo;
                case "BatteryStatus": return vm.BatteryStatus;
                case "Autopilot.HwManufacturer": return vm.Autopilot != null ? vm.Autopilot.HwManufacturer : null;
                case "Autopilot.HwModel": return vm.Autopilot != null ? vm.Autopilot.HwModel : null;
                case "Autopilot.HwSerialNumber": return vm.Autopilot != null ? vm.Autopilot.HwSerialNumber : null;
                case "Autopilot.HwSmbiosUuid": return vm.Autopilot != null ? vm.Autopilot.HwSmbiosUuid : null;
                default: return null;
            }
        }

        private static void ApplyFieldValue(string field, string value, MainViewModel vm)
        {
            Action act = delegate
            {
                switch (field)
                {
                    case "ComputerModel":
                        vm.ComputerModel = value;
                        if (vm.Autopilot != null) vm.Autopilot.HwModel = value;
                        break;
                    case "SerialNumber":
                        vm.SerialNumber = value;
                        if (vm.Autopilot != null) vm.Autopilot.HwSerialNumber = value;
                        if (vm.HardwareInfo != null) vm.HardwareInfo.SerialNumber = value;
                        break;
                    case "GpuName":
                        vm.GpuName = value;
                        if (vm.HardwareInfo != null) vm.HardwareInfo.GpuPrimary = value;
                        break;
                    case "GpuVram":
                        vm.GpuVram = value;
                        if (vm.HardwareInfo != null) vm.HardwareInfo.GpuPrimaryVram = value;
                        break;
                    case "GpuDriverVersion":
                        vm.GpuDriverVersion = value;
                        break;
                    case "DisplayResolution":
                        vm.DisplayResolution = value;
                        break;
                    case "DisplayRefreshRate":
                        vm.DisplayRefreshRate = value;
                        break;
                    case "DisplayDpiScale":
                        vm.DisplayDpiScale = value;
                        break;
                    case "MotherboardModel":
                        vm.MotherboardModel = value;
                        if (vm.HardwareInfo != null) vm.HardwareInfo.MotherboardModel = value;
                        break;
                    case "BiosVersion":
                        vm.BiosVersion = value;
                        break;
                    case "BiosReleaseDate":
                        vm.BiosReleaseDate = value;
                        break;
                    case "SecureBootStatus":
                        vm.SecureBootStatus = value;
                        if (vm.HardwareInfo != null) vm.HardwareInfo.SecureBootStatus = value;
                        break;
                    case "CpuInfo":
                        vm.CpuInfo = value;
                        break;
                    case "RamInfo":
                        vm.RamInfo = value;
                        break;
                    case "DiskInfo":
                        vm.DiskInfo = value;
                        break;
                    case "BatteryStatus":
                        vm.BatteryStatus = value;
                        break;
                    case "Autopilot.HwManufacturer":
                        if (vm.Autopilot != null) vm.Autopilot.HwManufacturer = value;
                        break;
                    case "Autopilot.HwModel":
                        if (vm.Autopilot != null) vm.Autopilot.HwModel = value;
                        break;
                    case "Autopilot.HwSerialNumber":
                        if (vm.Autopilot != null) vm.Autopilot.HwSerialNumber = value;
                        break;
                    case "Autopilot.HwSmbiosUuid":
                        if (vm.Autopilot != null) vm.Autopilot.HwSmbiosUuid = value;
                        break;
                }
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(act);
            }
            else
            {
                act();
            }
        }

        private static void LogDebugToFile(string message)
        {
            try
            {
                string dir = @"C:\setup";
                if (!Directory.Exists(dir))
                {
                    dir = @"C:\Windows\Logs\WIS";
                }
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string logPath = Path.Combine(dir, "servis_deploy.log");
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {1}{2}", DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch { }
        }
    }
}
