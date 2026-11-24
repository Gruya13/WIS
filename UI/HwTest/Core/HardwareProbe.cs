using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Core
{
    public class HardwareProbeResult
    {
        public bool HasTouchscreen { get; set; }
        public int MaxTouches { get; set; }
        public bool TouchscreenPnpPresent { get; set; }
        public bool TouchscreenBiosPresent { get; set; }
        public bool TouchscreenDisabledInBios { get; set; }
        public string TouchscreenBiosStatus { get; set; }

        public bool HasTouchpad { get; set; }
        public bool TouchpadBiosPresent { get; set; }
        public bool TouchpadDisabledInBios { get; set; }
        public string TouchpadBiosStatus { get; set; }

        public bool TrackPointBiosPresent { get; set; }
        public bool TrackPointDisabledInBios { get; set; }
        public string TrackPointBiosStatus { get; set; }
        public bool? FactoryTrackPoint { get; set; }
        public string FactoryTrackPointEvidence { get; set; }

        public bool HasWebcam { get; set; }
        public bool WebcamPnpPresent { get; set; }
        public string WebcamName { get; set; }
        public bool CameraBiosPresent { get; set; }
        public bool CameraDisabledInBios { get; set; }
        public string CameraBiosStatus { get; set; }

        public bool HasIrCamera { get; set; }
        public string IrCameraName { get; set; }
        public bool HasSpeakers { get; set; }
        public bool HasMicrophone { get; set; }
        public bool MicrophoneBiosPresent { get; set; }
        public bool MicrophoneDisabledInBios { get; set; }
        public string MicrophoneBiosStatus { get; set; }

        public bool HasFingerprint { get; set; }
        public string FingerprintName { get; set; }
        public string FingerprintDeviceId { get; set; }
        public uint FingerprintPnpError { get; set; }
        public bool FingerprintBiosPresent { get; set; }
        public bool FingerprintDisabledInBios { get; set; }
        public string FingerprintBiosStatus { get; set; }
        public string WbioServiceState { get; set; }
        public uint FingerprintUnitId { get; set; }
        public List<string> PnpAuditNotes { get; set; }
        public List<string> BiosAuditNotes { get; set; }
        public bool IsLaptop { get; set; }
        public string RecommendedLayout { get; set; }

        // Fabrička konfiguracija (OEM BOM lookup + offline heuristika)
        public bool FactoryBomLoaded { get; set; }
        public string FactorySource { get; set; }
        public string FactorySummary { get; set; }
        public bool? FactoryTouchscreen { get; set; }
        public string FactoryTouchscreenEvidence { get; set; }
        public bool? FactoryFingerprint { get; set; }
        public string FactoryFingerprintEvidence { get; set; }
        public bool? FactoryWebcam { get; set; }
        public string FactoryWebcamEvidence { get; set; }
        public bool? FactoryIrCamera { get; set; }
        public string FactoryIrCameraEvidence { get; set; }
        public bool? FactoryTouchpad { get; set; }
        public bool? FactorySpeakers { get; set; }
        public bool? FactoryMicrophone { get; set; }

        // Dodatni OEM / hardverski podaci za preciznu rezoluciju tastature
        public string Manufacturer { get; set; }
        public string ModelName { get; set; }
        public string SerialNumber { get; set; }
        public string SystemSkuNumber { get; set; }
        public string ProductMtm { get; set; }
        public double ScreenInches { get; set; }
        public bool? OsHasOem102 { get; set; }
        public int KeyboardType { get; set; }
        public bool HasNavColumn { get; set; }
        public bool IsThinkPad { get; set; }
        public bool? FactoryKeyboardIso { get; set; }
        public bool? FactoryKeyboardNumpad { get; set; }
        public string FactoryKeyboardDesc { get; set; }
        public bool? FactoryKeyboardBacklight { get; set; }
        public string FactoryKeyboardBacklightEvidence { get; set; }
        public bool? HasKeyboardBacklight { get; set; }
        public bool KeyboardBacklightBiosDisabled { get; set; }
        public string KeyboardBacklightEvidence { get; set; }
        public int EstimatedUsbPortCount { get; set; }
        public bool HasStylusPen { get; set; }
        public string StylusPenName { get; set; }
        public bool HasTrackPoint { get; set; }
        public bool HasRotationSensor { get; set; }

        public HardwareProbeResult()
        {
            RecommendedLayout = "laptop-tkl-ansi";
            FactoryKeyboardDesc = "";
            FactoryKeyboardBacklightEvidence = "";
            KeyboardBacklightEvidence = "";
            EstimatedUsbPortCount = 3;
            StylusPenName = "";
            TouchscreenBiosStatus = "";
            TouchpadBiosStatus = "";
            TrackPointBiosStatus = "";
            FactoryTrackPointEvidence = "";
            WebcamName = "Nije pronađena";
            CameraBiosStatus = "";
            IrCameraName = "Nije pronađena";
            MicrophoneBiosStatus = "";
            FingerprintName = "Nije pronađen";
            FingerprintDeviceId = "";
            FingerprintBiosStatus = "";
            WbioServiceState = "";
            PnpAuditNotes = new List<string>();
            BiosAuditNotes = new List<string>();
            FactorySource = "";
            FactorySummary = "";
            FactoryTouchscreenEvidence = "";
            FactoryFingerprintEvidence = "";
            FactoryWebcamEvidence = "";
            FactoryIrCameraEvidence = "";
            Manufacturer = "";
            ModelName = "";
            SerialNumber = "";
            SystemSkuNumber = "";
            ProductMtm = "";
        }
    }

    public static class HardwareProbe
    {
        private static EnumerationOptions CreateWmiOptions(int timeoutMs)
        {
            return new EnumerationOptions
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                ReturnImmediately = true,
                Rewindable = false
            };
        }

        private static ManagementObjectSearcher CreateSearcher(string query)
        {
            return new ManagementObjectSearcher(
                new ManagementScope(@"root\CIMV2"),
                new SelectQuery(query),
                CreateWmiOptions(2500));
        }

        private static ManagementObjectSearcher CreateSearcher(string scopePath, string query)
        {
            return new ManagementObjectSearcher(
                new ManagementScope(scopePath),
                new SelectQuery(query),
                CreateWmiOptions(2500));
        }

        private static bool RunWmiWithTimeout(Action action, int timeoutMs)
        {
            if (action == null) return false;
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try { action(); }
                catch { }
            }));
            worker.IsBackground = true;
            worker.Start();
            return worker.Join(timeoutMs);
        }

        public static HardwareProbeResult DetectHardware()
        {
            HardwareProbeResult res = new HardwareProbeResult();

            // 1. Touchscreen & Stylus digitizer
            try
            {
                int touches = NativeMethods.GetSystemMetrics(NativeMethods.SM_MAXIMUMTOUCHES);
                int digitizer = NativeMethods.GetSystemMetrics(NativeMethods.SM_DIGITIZER);
                res.MaxTouches = touches;
                res.HasTouchscreen = (touches > 0) || ((digitizer & 0x80) != 0);
                // Bit 1 (0x02) = Integrated Pen, Bit 2 (0x04) = External Pen
                if ((digitizer & 0x06) != 0)
                {
                    res.HasStylusPen = true;
                    res.StylusPenName = "Windows Ink Pen Digitizer";
                }
            }
            catch { }

            // 2. Chassis / IsLaptop
            try
            {
                using (ManagementObjectSearcher searcher = CreateSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        ushort[] types = obj["ChassisTypes"] as ushort[];
                        if (types != null)
                        {
                            foreach (ushort t in types)
                            {
                                // 8: Portable, 9: Laptop, 10: Notebook, 11: Hand Held, 12: Docking Station, 14: Sub Notebook, 30: Tablet, 31: Convertible, 32: Detachable
                                if (t == 8 || t == 9 || t == 10 || t == 11 || t == 12 || t == 14 || t == 30 || t == 31 || t == 32)
                                {
                                    res.IsLaptop = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2b. Identitet uređaja (Manufacturer, Model, SKU, MTM, Serial) + EDID dijagonala
            try
            {
                using (ManagementObjectSearcher csSearcher = CreateSearcher("SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in csSearcher.Get())
                    {
                        res.Manufacturer = (obj["Manufacturer"] ?? "").ToString().Trim();
                        res.ModelName = (obj["Model"] ?? "").ToString().Trim();
                        res.SystemSkuNumber = (obj["SystemSKUNumber"] ?? "").ToString().Trim();
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher cspSearcher = CreateSearcher("SELECT Name, Version, IdentifyingNumber FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject obj in cspSearcher.Get())
                    {
                        string cspName = (obj["Name"] ?? "").ToString().Trim();
                        string cspVer = (obj["Version"] ?? "").ToString().Trim();
                        string cspSn = (obj["IdentifyingNumber"] ?? "").ToString().Trim();
                        res.ProductMtm = cspName;
                        if (string.IsNullOrEmpty(res.SerialNumber) && !string.IsNullOrEmpty(cspSn))
                            res.SerialNumber = cspSn;
                        // Lenovo često drži ljudski naziv u Version (npr. ThinkPad T14 Gen 2), a MTM u Name (20W00012US)
                        if (res.Manufacturer.IndexOf("Lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!string.IsNullOrEmpty(cspVer) &&
                                !cspVer.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                                !cspVer.Equals("ThinkPad", StringComparison.OrdinalIgnoreCase))
                            {
                                res.ModelName = cspVer;
                            }
                            if (string.IsNullOrEmpty(res.SystemSkuNumber) && !string.IsNullOrEmpty(cspName))
                            {
                                res.SystemSkuNumber = cspName;
                            }
                        }
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey biosReg = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (biosReg != null)
                    {
                        if (string.IsNullOrEmpty(res.Manufacturer))
                        {
                            object sm = biosReg.GetValue("SystemManufacturer");
                            if (sm != null) res.Manufacturer = sm.ToString().Trim();
                        }
                        if (string.IsNullOrEmpty(res.ModelName))
                        {
                            object sp = biosReg.GetValue("SystemProductName");
                            if (sp != null) res.ModelName = sp.ToString().Trim();
                        }
                        if (string.IsNullOrEmpty(res.SystemSkuNumber))
                        {
                            object skuVal = biosReg.GetValue("SystemSKU");
                            if (skuVal != null) res.SystemSkuNumber = skuVal.ToString().Trim();
                        }
                        if (string.IsNullOrEmpty(res.SerialNumber))
                        {
                            object snVal = biosReg.GetValue("SystemSerialNumber");
                            if (snVal != null) res.SerialNumber = snVal.ToString().Trim();
                        }
                    }
                }
            }
            catch { }

            // Dijagonala ugrađenog panela preko WmiMonitorBasicDisplayParams (cm -> inči)
            try
            {
                using (ManagementObjectSearcher monSearcher = CreateSearcher(@"root\WMI", "SELECT MaxHorizontalImageSize, MaxVerticalImageSize, Active FROM WmiMonitorBasicDisplayParams"))
                {
                    foreach (ManagementObject obj in monSearcher.Get())
                    {
                        byte wCm = obj["MaxHorizontalImageSize"] != null ? Convert.ToByte(obj["MaxHorizontalImageSize"]) : (byte)0;
                        byte hCm = obj["MaxVerticalImageSize"] != null ? Convert.ToByte(obj["MaxVerticalImageSize"]) : (byte)0;
                        if (wCm > 10 && hCm > 6)
                        {
                            double diagInches = Math.Sqrt((double)(wCm * wCm + hCm * hCm)) / 2.54;
                            if (diagInches >= 8.0 && diagInches <= 45.0)
                            {
                                res.ScreenInches = Math.Round(diagInches, 1);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // Win32 tip tastature i provera da li OS aktivni layout mapira VK_OEM_102 (0xE2)
            try
            {
                res.KeyboardType = NativeMethods.GetKeyboardType(0);
                uint oem102Scan = NativeMethods.MapVirtualKey(0xE2, 0);
                res.OsHasOem102 = (oem102Scan != 0);
            }
            catch { }

            // 3. Touchpad
            try
            {
                if (res.IsLaptop)
                {
                    res.HasTouchpad = true; // Svi moderni laptopovi imaju touchpad
                }
                else
                {
                    using (ManagementObjectSearcher searcher = CreateSearcher("SELECT DeviceID, PNPDeviceID FROM Win32_PointingDevice"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string pnp = (obj["PNPDeviceID"] ?? "").ToString().ToUpperInvariant();
                            if (pnp.Contains("SYN") || pnp.Contains("ELAN") || pnp.Contains("ALPS") || pnp.Contains("MSFT0001") || pnp.Contains("I2C"))
                            {
                                res.HasTouchpad = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 4. Web Kamera & IR (razdvojena RGB vs IR klasifikacija bez lažnog podstringa "IR")
            if (!RunWmiWithTimeout(delegate
            {
                using (ManagementObjectSearcher searcher = CreateSearcher(
                    "SELECT Name, Description, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = (obj["Name"] ?? "").ToString().Trim();
                        string desc = (obj["Description"] ?? "").ToString().Trim();
                        string devId = (obj["DeviceID"] ?? "").ToString().Trim();
                        string pnpClass = (obj["PNPClass"] ?? "").ToString().Trim();
                        uint errCode = 0;
                        try
                        {
                            if (obj["ConfigManagerErrorCode"] != null)
                                errCode = Convert.ToUInt32(obj["ConfigManagerErrorCode"]);
                        }
                        catch { }

                        if (string.IsNullOrEmpty(name)) continue;

                        string upCam = (name + " " + desc + " " + devId).ToUpperInvariant();
                        if (devId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) ||
                            devId.StartsWith("SWC\\", StringComparison.OrdinalIgnoreCase) ||
                            devId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                            upCam.Contains("SOFTWARE DEVICE") ||
                            upCam.Contains("FACIAL RECOGNITION") ||
                            upCam.Contains("MS_FACE_AUTH"))
                        {
                            continue;
                        }

                        if (pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) &&
                            IsNonPhysicalCameraImageDevice(upCam))
                        {
                            continue;
                        }

                        if (upCam.Contains("VIRTUAL") ||
                            Regex.IsMatch(upCam, @"\bOBS\b") ||
                            upCam.Contains("OBS_") ||
                            upCam.Contains("OBS-"))
                        {
                            continue;
                        }

                        bool isIrEntry = IsIrCameraEntry(name, desc, devId);
                        if (errCode != 0 && res.PnpAuditNotes.Count < 45)
                        {
                            res.PnpAuditNotes.Add(string.Format(
                                "Kamera PnP greška: Class='{0}' Err={1} IsIR={2} Name='{3}' Id='{4}'",
                                pnpClass, errCode, isIrEntry, name, devId));
                        }

                        if (isIrEntry)
                        {
                            if (!res.HasIrCamera)
                            {
                                res.HasIrCamera = true;
                                res.IrCameraName = name;
                            }
                        }
                        else
                        {
                            res.WebcamPnpPresent = true;
                            if (errCode == 0 && !res.HasWebcam)
                            {
                                res.HasWebcam = true;
                                res.WebcamName = name;
                            }
                            else if (errCode != 0 && (string.IsNullOrEmpty(res.WebcamName) || res.WebcamName == "Nije pronađena"))
                            {
                                res.WebcamName = name;
                            }
                        }
                    }
                }
            }, 3500))
            {
                res.PnpAuditNotes.Add("PnP Camera query timeout (>3500ms)");
            }

            // 5. Audio Endpoints (Speakers / Microphone)
            try
            {
                var enumerator = (NativeMethods.IMMDeviceEnumerator)new NativeMethods.MMDeviceEnumeratorComObject();
                NativeMethods.IMMDevice renderDevice;
                // dataFlow: 0 = eRender, role: 1 = eMultimedia
                int hrRender = enumerator.GetDefaultAudioEndpoint(0, 1, out renderDevice);
                res.HasSpeakers = (hrRender == 0 && renderDevice != null);

                NativeMethods.IMMDevice captureDevice;
                // dataFlow: 1 = eCapture, role: 1 = eMultimedia
                int hrCapture = enumerator.GetDefaultAudioEndpoint(1, 1, out captureDevice);
                res.HasMicrophone = (hrCapture == 0 && captureDevice != null);
            }
            catch
            {
                // Fallback: ako MMDevice baci grešku, pretpostavi da postoji ako je laptop
                res.HasSpeakers = true;
                res.HasMicrophone = res.IsLaptop;
            }

            // 5b. Čitač otiska prsta (Biometric / WBF / USB-ACPI FP / Missing-Driver uređaji + BIOS)
            string wbioState = "";
            string wbioStartMode = "";
            try
            {
                using (ManagementObjectSearcher svcSearcher = CreateSearcher(
                    "SELECT Name, State, StartMode FROM Win32_Service WHERE Name = 'WbioSrvc'"))
                {
                    foreach (ManagementObject sObj in svcSearcher.Get())
                    {
                        wbioState = (sObj["State"] ?? "").ToString().Trim();
                        wbioStartMode = (sObj["StartMode"] ?? "").ToString().Trim();
                        res.WbioServiceState = string.Format("{0} ({1})", wbioState, wbioStartMode);
                        break;
                    }
                }
            }
            catch { }

            ProbeBiosFingerprintStatus(res);

            bool hasBiometricPnpCandidate = false;
            if (!RunWmiWithTimeout(delegate
            {
                using (ManagementObjectSearcher fpSearcher = CreateSearcher(
                    "SELECT Name, Description, DeviceID, PNPClass, Service, ConfigManagerErrorCode FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject obj in fpSearcher.Get())
                    {
                        string name = (obj["Name"] ?? "").ToString().Trim();
                        string desc = (obj["Description"] ?? "").ToString().Trim();
                        string devId = (obj["DeviceID"] ?? "").ToString().Trim();
                        string pnpClass = (obj["PNPClass"] ?? "").ToString().Trim();
                        string svc = (obj["Service"] ?? "").ToString().Trim();
                        uint errCode = 0;
                        try
                        {
                            if (obj["ConfigManagerErrorCode"] != null)
                                errCode = Convert.ToUInt32(obj["ConfigManagerErrorCode"]);
                        }
                        catch { }

                        string upId = devId.ToUpperInvariant();
                        string combinedText = (name + " " + desc + " " + svc + " " + pnpClass).ToUpperInvariant();

                        // Touchscreen PnP detekcija (i kad fali drajver ili je isključen)
                        if (combinedText.Contains("TOUCH SCREEN") ||
                            combinedText.Contains("TOUCHSCREEN") ||
                            combinedText.Contains("DIGITIZER") ||
                            (pnpClass.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) &&
                             (combinedText.Contains("ELAN") || combinedText.Contains("GOODIX") || combinedText.Contains("WACOM")) &&
                             combinedText.Contains("TOUCH")))
                        {
                            res.TouchscreenPnpPresent = true;
                        }

                        // Aktivna olovka / Wacom AES / EMR Stylus PnP detekcija
                        if (combinedText.Contains("WACHIDROUTERISD") ||
                            combinedText.Contains("LENOVO PEN") ||
                            combinedText.Contains("WACOM") ||
                            combinedText.Contains("ACTIVE PEN") ||
                            combinedText.Contains("STYLUS") ||
                            combinedText.Contains("PEN DIGITIZER") ||
                            upId.Contains("WACF"))
                        {
                            res.HasStylusPen = true;
                            if (!string.IsNullOrEmpty(name) &&
                                (string.IsNullOrEmpty(res.StylusPenName) || res.StylusPenName == "Windows Ink Pen Digitizer"))
                            {
                                res.StylusPenName = name;
                            }
                        }

                        // Senzor rotacije / Akcelerometar / Žiroskop (za 2-u-1 i tablet uređaje)
                        if (combinedText.Contains("SIMPLEORIENTATION") ||
                            combinedText.Contains("ACCELEROMETER") ||
                            combinedText.Contains("INCLINOMETER") ||
                            combinedText.Contains("GYROSCOPE") ||
                            combinedText.Contains("SENSOR COLLECTION") ||
                            upId.Contains("HID_SENSOR_200073") ||
                            upId.Contains("HID_SENSOR_20008A"))
                        {
                            res.HasRotationSensor = true;
                        }

                        // TrackPoint / Pointing Stick PnP detekcija
                        if (res.IsThinkPad ||
                            combinedText.Contains("TRACKPOINT") ||
                            combinedText.Contains("POINTING STICK") ||
                            combinedText.Contains("POINTSTICK") ||
                            combinedText.Contains("DUALPOINT"))
                        {
                            res.HasTrackPoint = true;
                        }

                        // Kamera PnP bez ispravnog drajvera (ne sme prepisati IR kameru niti skener/štampač/virtuelnu kameru)
                        string camCheckText = (name + " " + desc + " " + devId).ToUpperInvariant();
                        bool isNonPhysicalCam =
                            (pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) && IsNonPhysicalCameraImageDevice(camCheckText)) ||
                            camCheckText.Contains("VIRTUAL") ||
                            Regex.IsMatch(camCheckText, @"\bOBS\b") ||
                            camCheckText.Contains("OBS_") ||
                            camCheckText.Contains("OBS-");
                        bool isIrCamNode = IsIrCameraEntry(name, desc, devId);
                        bool isSoftwareCamNode =
                            upId.StartsWith("SWD\\") ||
                            upId.StartsWith("SWC\\") ||
                            upId.StartsWith("ROOT\\") ||
                            combinedText.Contains("FACIAL RECOGNITION") ||
                            combinedText.Contains("SOFTWARE DEVICE");

                        if (!isNonPhysicalCam && !isSoftwareCamNode)
                        {
                            if (isIrCamNode)
                            {
                                if ((pnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                                     pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("IR CAMERA") ||
                                     combinedText.Contains("INFRARED")) &&
                                    !res.HasIrCamera && !string.IsNullOrEmpty(name))
                                {
                                    res.HasIrCamera = true;
                                    res.IrCameraName = name;
                                }
                            }
                            else if (pnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                                     pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                                     combinedText.Contains("INTEGRATED CAMERA") ||
                                     combinedText.Contains("WEBCAM"))
                            {
                                res.WebcamPnpPresent = true;
                            }
                        }

                        // Beležimo uređaje bez drajvera / sa greškom, USB/SPI/ACPI senzore i biometric za dijagnostiku u logu
                        if (errCode != 0 || upId.StartsWith("USB\\VID_") ||
                            upId.StartsWith("SPI\\") || upId.StartsWith("HID\\SYNA") || upId.StartsWith("HID\\ELAN") ||
                            upId.Contains("GXFP") || upId.Contains("FPC") || upId.Contains("VFS") ||
                            pnpClass.Equals("Biometric", StringComparison.OrdinalIgnoreCase) ||
                            pnpClass.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("FINGER") || combinedText.Contains("BIOMETRIC") ||
                            combinedText.Contains("ELAN") || combinedText.Contains("SYNAPTICS") ||
                            combinedText.Contains("GOODIX") || combinedText.Contains("VALIDITY") ||
                            combinedText.Contains("WBDI"))
                        {
                            if (res.PnpAuditNotes.Count < 45)
                            {
                                res.PnpAuditNotes.Add(string.Format(
                                    "Class='{0}' Err={1} Svc='{2}' Name='{3}' Id='{4}'",
                                    pnpClass, errCode, svc, name, devId));
                            }
                        }

                        bool isBiometricClass;
                        if (!IsFingerprintPnpCandidate(name, desc, devId, pnpClass, svc, errCode, out isBiometricClass))
                        {
                            continue;
                        }

                        if (isBiometricClass)
                        {
                            hasBiometricPnpCandidate = true;
                        }

                        if (!res.HasFingerprint)
                        {
                            res.HasFingerprint = true;
                            res.FingerprintName = !string.IsNullOrEmpty(name) ? name : (!string.IsNullOrEmpty(desc) ? desc : devId);
                            res.FingerprintDeviceId = devId;
                            res.FingerprintPnpError = errCode;
                        }
                    }
                }
            }, 3500))
            {
                res.PnpAuditNotes.Add("PnP Full Enum query timeout (>3500ms)");
            }

            // Bezbedno buđenje WbioSrvc servisa pre WinBioEnumBiometricUnits ako je Stopped (a nije Disabled)
            if ((hasBiometricPnpCandidate || res.HasFingerprint) &&
                wbioState.Equals("Stopped", StringComparison.OrdinalIgnoreCase) &&
                !wbioStartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                TryWakeWbioService(res, wbioStartMode);
            }

            try
            {
                IntPtr unitArr = IntPtr.Zero;
                UIntPtr unitCount = UIntPtr.Zero;
                int hrBio = NativeMethods.WinBioEnumBiometricUnits(NativeMethods.WINBIO_TYPE_FINGERPRINT, out unitArr, out unitCount);
                try
                {
                    if (hrBio == 0 && (int)unitCount > 0 && unitArr != IntPtr.Zero)
                    {
                        var schema = (NativeMethods.WINBIO_UNIT_SCHEMA)System.Runtime.InteropServices.Marshal.PtrToStructure(
                            unitArr, typeof(NativeMethods.WINBIO_UNIT_SCHEMA));
                        string wbfName = !string.IsNullOrEmpty(schema.Description) ? schema.Description.Trim()
                            : (!string.IsNullOrEmpty(schema.Model) ? schema.Model.Trim() : res.FingerprintName);
                        string wbfUpper = (wbfName ?? "").ToUpperInvariant();
                        bool isSoftwareFaceBio = wbfUpper.Contains("FACIAL RECOGNITION") ||
                            wbfUpper.Contains("HELLO FACE") ||
                            wbfUpper.Contains("SOFTWARE DEVICE");
                        if (!isSoftwareFaceBio)
                        {
                            res.HasFingerprint = true;
                            res.FingerprintUnitId = schema.UnitId;
                            if (!string.IsNullOrEmpty(wbfName) && wbfName != "Nije pronađen")
                            {
                                res.FingerprintName = wbfName;
                            }
                        }
                    }
                }
                finally
                {
                    if (unitArr != IntPtr.Zero)
                    {
                        try { NativeMethods.WinBioFree(unitArr); } catch { }
                    }
                }
            }
            catch { }

            // 6. Lokalni fallback raspored tastature (pre BOM / ServiceHub dopune)
            RecomputeRecommendedLayout(res);

            // 7. Procena pozadinskog osvetljenja tastature (PnP / BIOS / Lenovo WMI)
            DetectKeyboardBacklight(res);

            // 8. Procena broja fizičkih USB portova (OEM katalog / SMBIOS Win32_PortConnector)
            res.EstimatedUsbPortCount = DetectEstimatedUsbPortCount(res);

            return res;
        }

        public static int DetectEstimatedUsbPortCount(HardwareProbeResult res)
        {
            if (res == null) return 3;
            string up = string.Format("{0} {1}", res.Manufacturer ?? "", res.ModelName ?? "").ToUpperInvariant();

            if (!res.IsLaptop)
            {
                // Desktop: proveri SMBIOS konektore, fallback 6
                int smbiosDesktop = QuerySmbiosExternalUsbCount();
                return smbiosDesktop >= 4 ? smbiosDesktop : 6;
            }

            // Poznate poslovne laptop šasije sa tačnim brojem fizičkih USB portova (USB-A + USB-C)
            if (up.Contains("ELITEBOOK") || up.Contains("ZBOOK"))
            {
                if (up.Contains("630"))
                    return 3; // 2x USB-A + 1x USB-C (630 G9/G10)
                if (up.Contains("640") || up.Contains("645") || up.Contains("650") || up.Contains("655") ||
                    up.Contains("FIREFLY"))
                    return 4;
                if (up.Contains("830") || up.Contains("835") || up.Contains("840") || up.Contains("845") ||
                    up.Contains("850") || up.Contains("855") || up.Contains("860") || up.Contains("865") ||
                    up.Contains("1030") || up.Contains("1040"))
                {
                    if (up.Contains("G5") || up.Contains("G6"))
                        return 3; // 2x USB-A + 1x USB-C
                    if (Regex.IsMatch(up, @"\bG(7|8|9|10|11)\b"))
                        return 4; // 2x USB-A + 2x USB-C Thunderbolt
                }
            }
            else if (up.Contains("PROBOOK"))
            {
                if (up.Contains("430") || up.Contains("440") || up.Contains("445") || up.Contains("450") || up.Contains("455"))
                {
                    if (Regex.IsMatch(up, @"\bG(8|9|10|11)\b"))
                        return 4; // 3x USB-A + 1x USB-C (ili 2+2)
                }
            }
            else if (up.Contains("THINKPAD"))
            {
                if (up.Contains("X380") || up.Contains("X390") || up.Contains("X280") ||
                    up.Contains("L380") || up.Contains("L390"))
                    return 3; // 2x USB-A + 1x USB-C/TB3
                if (up.Contains("T14") || up.Contains("T480") || up.Contains("T490") || up.Contains("X1 CARBON"))
                    return 4;
            }
            else if (up.Contains("LATITUDE"))
            {
                if (up.Contains("5420") || up.Contains("5430") || up.Contains("5440") ||
                    up.Contains("5520") || up.Contains("5530") || up.Contains("5540") ||
                    up.Contains("7420") || up.Contains("7430") || up.Contains("7440"))
                    return 4;
            }

            int smbios = QuerySmbiosExternalUsbCount();
            if (smbios >= 2 && smbios <= 10)
                return smbios;

            return 3;
        }

        private static int QuerySmbiosExternalUsbCount()
        {
            try
            {
                int usbCount = 0;
                HashSet<string> seenDesignators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (ManagementObjectSearcher s = CreateSearcher(
                    "SELECT ExternalReferenceDesignator, InternalReferenceDesignator, ConnectorType FROM Win32_PortConnector"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string ext = (obj["ExternalReferenceDesignator"] ?? "").ToString().Trim().ToUpperInvariant();
                        string intl = (obj["InternalReferenceDesignator"] ?? "").ToString().Trim().ToUpperInvariant();
                        if (string.IsNullOrEmpty(ext) || ext == "NONE" || ext == "NOT SPECIFIED")
                        {
                            // Neki BIOS-i stavljaju "USB1" u InternalReferenceDesignator za onboard port
                            if (intl.Contains("USB") && !intl.Contains("HEADER") && !intl.Contains("INTERNAL") &&
                                !intl.Contains("CAMERA") && !intl.Contains("FINGER") && !intl.Contains("BT") &&
                                !intl.Contains("SMART") && !intl.Contains("WWAN") && !intl.Contains("WLAN") &&
                                !intl.Contains("DOCK") && !intl.Contains("CARD"))
                            {
                                usbCount++;
                            }
                            continue;
                        }
                        if (ext.Contains("INTERNAL") || ext.Contains("DOCK") || ext.Contains("ONELINK") ||
                            ext.Contains("CAMERA") || ext.Contains("SMART") || ext.Contains("FINGER"))
                        {
                            continue;
                        }
                        if (ext.Contains("USB") || ext.Contains("TYPE-C") || ext.Contains("TYPE C") || ext.Contains("THUNDERBOLT"))
                        {
                            if (!seenDesignators.Add(ext)) continue;
                            usbCount++;
                        }
                    }
                }
                return usbCount;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Prepoznaje da li laptop šasija ima namensku desnu kolonu za navigaciju
        /// (Home, PageUp, PageDown, End - npr. HP EliteBook 830/840 G5-G8, ProBook 430/440 G6-G8),
        /// za razliku od novijih modela bez nje (npr. EliteBook 630/640 G9, 840 G9+).
        /// </summary>
        public static void InferClientKeyboardFeatures(HardwareProbeResult res)
        {
            if (res == null || !res.IsLaptop)
            {
                if (res != null)
                {
                    res.HasNavColumn = false;
                    res.IsThinkPad = false;
                }
                return;
            }

            string up = string.Format(
                "{0} {1} {2} {3}",
                res.Manufacturer ?? "",
                res.ModelName ?? "",
                res.SystemSkuNumber ?? "",
                res.ProductMtm ?? "").ToUpperInvariant();

            res.IsThinkPad = up.Contains("THINKPAD");
            if (res.IsThinkPad && res.FactoryTrackPoint != false)
            {
                res.HasTrackPoint = true;
            }
            if (res.FactoryTrackPoint == false)
            {
                res.HasTrackPoint = false;
            }

            bool isHp = up.Contains("HP") || up.Contains("HEWLETT") || up.Contains("ELITEBOOK") ||
                        up.Contains("PROBOOK") || up.Contains("ZBOOK");

            res.HasNavColumn = false;
            if (!isHp || res.IsThinkPad) return;

            bool isNewGenWithoutNav =
                Regex.IsMatch(up, @"\bG(9|10|11|12)\b") ||
                up.Contains("630") || up.Contains("640") || up.Contains("645");

            if (isNewGenWithoutNav) return;

            // HP 13"/14" poslovne serije G4-G8 sa vertikalnom nav kolonom desno
            bool isElite1314 =
                (up.Contains("ELITEBOOK") || up.Contains("ZBOOK")) &&
                (up.Contains("830") || up.Contains("835") || up.Contains("840") || up.Contains("845") ||
                 up.Contains("1030") || up.Contains("1040") || up.Contains("735") || up.Contains("745") ||
                 up.Contains("14U") || up.Contains("FIREFLY 14"));

            bool isProBook1314G68 =
                up.Contains("PROBOOK") &&
                (up.Contains("430") || up.Contains("440") || up.Contains("445")) &&
                (up.Contains("G6") || up.Contains("G7") || up.Contains("G8"));

            if (isElite1314 || isProBook1314G68)
            {
                res.HasNavColumn = true;
            }
        }

        /// <summary>
        /// Računa najbolji lokalni raspored tastature kombinujući WMI/EDID, OS OEM102 i BOM/SKU podatke.
        /// Poziva se i nakon FactorySpecProbe.ResolveAndMerge kada stignu podaci o tastaturi iz BOM/SKU.
        /// </summary>
        public static void RecomputeRecommendedLayout(HardwareProbeResult res)
        {
            if (res == null) return;

            InferClientKeyboardFeatures(res);

            bool isIso = res.FactoryKeyboardIso.HasValue
                ? res.FactoryKeyboardIso.Value
                : (res.OsHasOem102.HasValue && res.OsHasOem102.Value);

            if (!res.IsLaptop)
            {
                res.HasNavColumn = false;
                res.RecommendedLayout = isIso ? "desktop-105-iso" : "desktop-104-ansi";
                return;
            }

            bool hasNumpad;
            if (res.FactoryKeyboardNumpad.HasValue)
            {
                hasNumpad = res.FactoryKeyboardNumpad.Value;
            }
            else
            {
                string up = (res.ModelName ?? "").ToUpperInvariant();
                bool force14NoNumpad =
                    up.Contains("630") || up.Contains("640") || up.Contains("645") ||
                    up.Contains("830") || up.Contains("835") || up.Contains("840") || up.Contains("845") ||
                    up.Contains("1030") || up.Contains("1040") || up.Contains("430") || up.Contains("440") ||
                    up.Contains("FIREFLY 14") || up.Contains("13 INCH") || up.Contains("14 INCH") ||
                    up.Contains("X380") || up.Contains("X280") || up.Contains("X390") ||
                    up.Contains("X13") || up.Contains("X12") || up.Contains("X1 CARBON") || up.Contains("X1 YOGA") ||
                    up.Contains("L380") || up.Contains("L390") || up.Contains("T480") || up.Contains("T490") ||
                    up.Contains("T14") || up.Contains("L14") || up.Contains("E14");
                bool force15Numpad =
                    up.Contains("650") || up.Contains("655") || up.Contains("850") || up.Contains("855") ||
                    up.Contains("860") || up.Contains("450") || up.Contains("455") || up.Contains("470") ||
                    up.Contains("15U") || up.Contains("FURY 15") || up.Contains("FURY 16") || up.Contains("FURY 17") ||
                    up.Contains("15 INCH") || up.Contains("16 INCH") || up.Contains("17 INCH") ||
                    up.Contains("T580") || up.Contains("T590") || up.Contains("T15") || up.Contains("T16") ||
                    up.Contains("L15") || up.Contains("E15") || up.Contains("P15") || up.Contains("P16") ||
                    up.Contains("P52") || up.Contains("P53");

                if (force15Numpad) hasNumpad = true;
                else if (force14NoNumpad) hasNumpad = false;
                else hasNumpad = (res.ScreenInches >= 15.0);
            }

            if (hasNumpad)
            {
                res.HasNavColumn = false;
                if (res.IsThinkPad)
                {
                    res.RecommendedLayout = isIso ? "thinkpad-numpad-iso" : "thinkpad-numpad-ansi";
                }
                else
                {
                    res.RecommendedLayout = isIso ? "laptop-numpad-iso" : "laptop-numpad-ansi";
                }
            }
            else if (res.IsThinkPad)
            {
                res.HasNavColumn = false;
                res.RecommendedLayout = isIso ? "thinkpad-tkl-iso" : "thinkpad-tkl-ansi";
            }
            else if (res.HasNavColumn)
            {
                res.RecommendedLayout = isIso ? "laptop-tkl-nav-iso" : "laptop-tkl-nav-ansi";
            }
            else
            {
                res.RecommendedLayout = isIso ? "laptop-tkl-iso" : "laptop-tkl-ansi";
            }
        }

        private static string ExtractHpActiveBiosValue(string val, string currentVal)
        {
            if (!string.IsNullOrEmpty(currentVal)) return currentVal.Trim();
            if (string.IsNullOrEmpty(val)) return "";
            string[] parts = val.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.StartsWith("*")) return t.Substring(1).Trim();
            }
            return val.Trim();
        }

        private static bool IsBiosOptionDisabledValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return false;
            string v = val.Trim().ToUpperInvariant();
            return v == "DISABLE" || v == "DISABLED" || v == "OFF" || v == "INACTIVE" || v == "0";
        }

        private static bool IsBiosOptionEnabledValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return false;
            string v = val.Trim().ToUpperInvariant();
            return v == "ENABLE" || v == "ENABLED" || v == "ON" || v == "ACTIVE" || v == "1";
        }

        private static bool IsPrimaryBiosSettingMatch(string name, string[] keywords, string[] exclusions)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToUpperInvariant();
            bool hit = false;
            foreach (string kw in keywords)
            {
                if (n.Contains(kw)) { hit = true; break; }
            }
            if (!hit) return false;
            if (exclusions != null)
            {
                foreach (string ex in exclusions)
                {
                    if (n.Contains(ex)) return false;
                }
            }
            return true;
        }

        private static void RecordBiosSetting(
            HardwareProbeResult res,
            string sourceTag,
            string settingName,
            string settingVal)
        {
            if (string.IsNullOrEmpty(settingName)) return;

            bool isFp = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "FINGERPRINT", "BIOMETRIC" },
                new string[] { "RESET", "CLEAR", "PREBOOT", "PRE-BOOT" });
            bool isCam = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "CAMERA", "WEBCAM" },
                new string[] { "LED", "INDICATOR", "SHUTTER" });
            bool isMic = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "MICROPHONE", "INTERNAL MIC", "BUILT-IN MIC" },
                new string[] { "MUTE LED", "HOTKEY" });
            bool isTs = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "TOUCHSCREEN", "TOUCH SCREEN", "TOUCH DISPLAY" },
                new string[] { "TOUCHPAD" });
            bool isTp = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "TOUCHPAD", "TRACKPAD", "INTERNAL POINTING" },
                new string[] { "DISABLE ON", "USB MOUSE" });
            bool isTrackPoint = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "TRACKPOINT", "POINTING STICK", "POINTSTICK" },
                new string[] { "DISABLE ON", "USB MOUSE" });
            bool isBacklit = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "BACKLIT", "BACKLIGHT", "ILLUMINATION", "ILLUMINATED" },
                null);

            bool isAuditExtra = IsPrimaryBiosSettingMatch(settingName,
                new string[] { "AUTH", "SENSOR", "PRIVACY", "AUTHENTICATION" },
                null);

            if (isAuditExtra && !(isFp || isCam || isMic || isTs || isTp || isTrackPoint || isBacklit) && res.BiosAuditNotes.Count < 45)
            {
                res.BiosAuditNotes.Add(string.Format("{0} (audit): '{1}' = '{2}'", sourceTag, settingName, settingVal));
            }

            if (!(isFp || isCam || isMic || isTs || isTp || isTrackPoint || isBacklit)) return;

            if (res.BiosAuditNotes.Count < 45)
            {
                res.BiosAuditNotes.Add(string.Format("{0}: '{1}' = '{2}'", sourceTag, settingName, settingVal));
            }

            bool disabled = IsBiosOptionDisabledValue(settingVal);
            bool enabled = IsBiosOptionEnabledValue(settingVal);
            string formatted = string.Format("{0} = {1}", settingName, settingVal);

            if (isFp)
            {
                res.FingerprintBiosPresent = true;
                if (disabled) { res.FingerprintDisabledInBios = true; res.FingerprintBiosStatus = formatted; }
                else if (!res.FingerprintDisabledInBios && (string.IsNullOrEmpty(res.FingerprintBiosStatus) || enabled))
                    res.FingerprintBiosStatus = formatted;
            }
            if (isCam)
            {
                res.CameraBiosPresent = true;
                if (disabled) { res.CameraDisabledInBios = true; res.CameraBiosStatus = formatted; }
                else if (!res.CameraDisabledInBios && (string.IsNullOrEmpty(res.CameraBiosStatus) || enabled))
                    res.CameraBiosStatus = formatted;
            }
            if (isMic)
            {
                res.MicrophoneBiosPresent = true;
                if (disabled) { res.MicrophoneDisabledInBios = true; res.MicrophoneBiosStatus = formatted; }
                else if (!res.MicrophoneDisabledInBios && (string.IsNullOrEmpty(res.MicrophoneBiosStatus) || enabled))
                    res.MicrophoneBiosStatus = formatted;
            }
            if (isTs)
            {
                res.TouchscreenBiosPresent = true;
                if (disabled) { res.TouchscreenDisabledInBios = true; res.TouchscreenBiosStatus = formatted; }
                else if (!res.TouchscreenDisabledInBios && (string.IsNullOrEmpty(res.TouchscreenBiosStatus) || enabled))
                    res.TouchscreenBiosStatus = formatted;
            }
            if (isTp)
            {
                res.TouchpadBiosPresent = true;
                if (disabled) { res.TouchpadDisabledInBios = true; res.TouchpadBiosStatus = formatted; }
                else if (!res.TouchpadDisabledInBios && (string.IsNullOrEmpty(res.TouchpadBiosStatus) || enabled))
                    res.TouchpadBiosStatus = formatted;
            }
            if (isTrackPoint)
            {
                res.TrackPointBiosPresent = true;
                if (disabled) { res.TrackPointDisabledInBios = true; res.TrackPointBiosStatus = formatted; }
                else if (!res.TrackPointDisabledInBios && (string.IsNullOrEmpty(res.TrackPointBiosStatus) || enabled))
                    res.TrackPointBiosStatus = formatted;
            }
            if (isBacklit)
            {
                if (disabled)
                {
                    res.KeyboardBacklightBiosDisabled = true;
                }
                // HP_BIOSSetting / Lenovo_BiosSetting / DCIM_BIOSEnumeration cesto postoje i na non-backlit plocama;
                // samo belezimo u BiosAuditNotes (gore) i disabled warning, bez postavljanja HasKeyboardBacklight = true.
            }
        }

        private static string SafeGetWmiProp(ManagementBaseObject obj, string propName)
        {
            try
            {
                object v = obj[propName];
                if (v == null) return "";
                string[] arr = v as string[];
                if (arr != null) return string.Join(",", arr);
                return v.ToString().Trim();
            }
            catch { return ""; }
        }

        private static void ProbeBiosFingerprintStatus(HardwareProbeResult res)
        {
            string mfg = res.Manufacturer ?? "";

            // 1. HP BIOS WMI (SELECT * bez WHERE LIKE jer dinamicki ACPI WMI provider cesto odbija LIKE)
            if (mfg.IndexOf("HP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mfg.IndexOf("Hewlett", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string[] hpClasses = new string[] { "HP_BIOSSetting", "HP_BIOSEnumeration" };
                foreach (string clsName in hpClasses)
                {
                    try
                    {
                        using (ManagementObjectSearcher biosSearcher = CreateSearcher(
                            @"root\HP\InstrumentedBIOS", "SELECT * FROM " + clsName))
                        {
                            foreach (ManagementObject bObj in biosSearcher.Get())
                            {
                                string bName = SafeGetWmiProp(bObj, "Name");
                                if (string.IsNullOrEmpty(bName)) continue;
                                string rawVal = SafeGetWmiProp(bObj, "Value");
                                string curVal = SafeGetWmiProp(bObj, "CurrentValue");
                                string activeVal = ExtractHpActiveBiosValue(rawVal, curVal);
                                RecordBiosSetting(res, "HP/" + clsName, bName, activeVal);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        res.BiosAuditNotes.Add(string.Format("HP/{0} ERR: {1}", clsName, ex.Message));
                    }
                }
            }

            // 2. Lenovo BIOS WMI (root\WMI -> Lenovo_BiosSetting)
            if (mfg.IndexOf("Lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!RunWmiWithTimeout(delegate
                {
                    using (ManagementObjectSearcher lenSearcher = CreateSearcher(
                        @"root\WMI", "SELECT CurrentSetting FROM Lenovo_BiosSetting"))
                    {
                        foreach (ManagementObject lObj in lenSearcher.Get())
                        {
                            string cs = SafeGetWmiProp(lObj, "CurrentSetting");
                            if (string.IsNullOrEmpty(cs)) continue;
                            int comma = cs.IndexOf(',');
                            string sName = comma > 0 ? cs.Substring(0, comma).Trim() : cs;
                            string sVal = comma > 0 ? cs.Substring(comma + 1).Trim() : "";
                            RecordBiosSetting(res, "Lenovo", sName, sVal);
                        }
                    }
                }, 3500))
                {
                    res.BiosAuditNotes.Add("Lenovo_BiosSetting timeout (>3500ms)");
                }
            }

            // 3. Dell BIOS WMI (root\dcim\sysman -> DCIM_BIOSEnumeration)
            if (mfg.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string[] dellScopes = new string[]
                {
                    @"root\dcim\sysman|DCIM_BIOSEnumeration",
                    @"root\dcim\sysman\biosattributes|EnumerationAttribute"
                };
                foreach (string pair in dellScopes)
                {
                    string[] sp = pair.Split('|');
                    try
                    {
                        using (ManagementObjectSearcher dellSearcher = CreateSearcher(
                            sp[0], "SELECT * FROM " + sp[1]))
                        {
                            foreach (ManagementObject dObj in dellSearcher.Get())
                            {
                                string dName = SafeGetWmiProp(dObj, "AttributeName");
                                string dVal = SafeGetWmiProp(dObj, "CurrentValue");
                                RecordBiosSetting(res, "Dell/" + sp[1], dName, dVal);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        res.BiosAuditNotes.Add(string.Format("Dell/{0} ERR: {1}", sp[1], ex.Message));
                    }
                }
            }
        }

        private static void DetectKeyboardBacklight(HardwareProbeResult res)
        {
            if (res == null) return;

            if (!RunWmiWithTimeout(delegate
            {
                using (ManagementObjectSearcher pnpSearcher = CreateSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject obj in pnpSearcher.Get())
                    {
                        string name = SafeGetWmiProp(obj, "Name");
                        string devId = SafeGetWmiProp(obj, "DeviceID");
                        string upperName = name.ToUpperInvariant();
                        string upperId = devId.ToUpperInvariant();

                        if (upperId.Contains("LEN0068") || upperId.Contains("LEN0268") || upperId.Contains("VPC2004"))
                        {
                            res.BiosAuditNotes.Add("PnP ACPI/PM: " + devId + (string.IsNullOrEmpty(name) ? "" : " (" + name + ")"));
                        }

                        if ((upperName.Contains("BACKLIGHT") ||
                             upperName.Contains("BACKLIT") ||
                             upperName.Contains("ILLUMINATED") ||
                             upperName.Contains("RGB KEYBOARD")) &&
                            !upperName.Contains("DISPLAY") &&
                            !upperName.Contains("SCREEN") &&
                            !upperName.Contains("MONITOR"))
                        {
                            res.HasKeyboardBacklight = true;
                            if (string.IsNullOrEmpty(res.KeyboardBacklightEvidence))
                            {
                                res.KeyboardBacklightEvidence = "PnP: " + (!string.IsNullOrEmpty(name) ? name : devId);
                            }
                        }
                    }
                }
            }, 3500))
            {
                res.BiosAuditNotes.Add("PnP KeyboardBacklight timeout (>3500ms)");
            }

            string pmDetail;
            int pmLevel;
            if (TryProbeLenovoIbmPmBacklight(out pmDetail, out pmLevel))
            {
                res.HasKeyboardBacklight = true;
                res.KeyboardBacklightEvidence = pmDetail;
            }
            else
            {
                string energyDetail;
                if (TryProbeLenovoEnergyDrvBacklight(out energyDetail))
                {
                    res.HasKeyboardBacklight = true;
                    res.KeyboardBacklightEvidence = energyDetail;
                }
            }

            string mfg = (res.Manufacturer ?? "").ToUpperInvariant();
            if (mfg.Contains("LENOVO"))
            {
                try
                {
                    using (ManagementClass lenClass = new ManagementClass(@"root\WMI", "Lenovo_SetKeyboardBacklightStatus", null))
                    {
                        lenClass.Get();
                        if (res.BiosAuditNotes.Count < 45)
                        {
                            res.BiosAuditNotes.Add("Lenovo WMI klasa prisutna (ne dokazuje LED hardver): Lenovo_SetKeyboardBacklightStatus");
                        }
                    }
                }
                catch { }
            }

            if (!res.IsLaptop && !res.HasKeyboardBacklight.HasValue)
            {
                res.HasKeyboardBacklight = false;
                if (string.IsNullOrEmpty(res.KeyboardBacklightEvidence))
                {
                    res.KeyboardBacklightEvidence = HwTestLocalization.Pick("Desktop sistem (bez ugrađenog pozadinskog osvetljenja)", "台式机系统(无内置背光)");
                }
            }
        }

        public static IntPtr OpenKernelDeviceHandle(string devicePath)
        {
            IntPtr invalid = new IntPtr(-1);
            IntPtr hDev = NativeMethods.CreateFile(
                devicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (hDev != IntPtr.Zero && hDev != invalid)
            {
                return hDev;
            }

            hDev = NativeMethods.CreateFile(
                devicePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (hDev != IntPtr.Zero && hDev != invalid)
            {
                return hDev;
            }

            return IntPtr.Zero;
        }

        public static bool TryProbeLenovoIbmPmBacklight(out string detail, out int currentLevel)
        {
            detail = "";
            currentLevel = -1;
            IntPtr hDev = OpenKernelDeviceHandle(@"\\.\IBMPmDrv");
            if (hDev == IntPtr.Zero) return false;

            try
            {
                uint inVal = 0;
                uint outVal = 0;
                uint bytesRet = 0;

                // 1. MultiASL (MLCG / MLCS): IOCTL 0x222680
                if (NativeMethods.DeviceIoControl(hDev, 0x222680u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u &&
                    (outVal & 0x50000u) == 0x50000u)
                {
                    currentLevel = (int)(outVal & 0x0Fu);
                    detail = string.Format("IBMPmDrv MultiASL (0x222680, level={0}, raw=0x{1:X})", currentLevel, outVal);
                    return true;
                }

                // 2. ASL: IOCTL 0x222660
                inVal = 0;
                outVal = 0;
                bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x222660u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u &&
                    ((outVal >> 16) & 1u) != 0u)
                {
                    currentLevel = (int)(outVal & 0x0Fu);
                    detail = string.Format("IBMPmDrv ASL (0x222660, level={0}, raw=0x{1:X})", currentLevel, outVal);
                    return true;
                }

                // 3. ThinkLight: IOCTL 0x2225EC
                inVal = 0;
                outVal = 0;
                bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x2225ECu, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u &&
                    ((outVal >> 16) & 1u) != 0u)
                {
                    currentLevel = (int)(outVal & 0x0Fu);
                    detail = string.Format("IBMPmDrv ThinkLight (0x2225EC, level={0}, raw=0x{1:X})", currentLevel, outVal);
                    return true;
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }

            return false;
        }

        public static bool TryProbeLenovoEnergyDrvBacklight(out string detail)
        {
            detail = "";
            IntPtr hDev = OpenKernelDeviceHandle(@"\\.\EnergyDrv");
            if (hDev == IntPtr.Zero) return false;

            try
            {
                uint inVal = 0x01u;
                uint outVal = 0;
                uint bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x83102144u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u)
                {
                    if ((outVal & 0x01u) != 0u)
                    {
                        detail = string.Format("EnergyDrv (0x83102144, q=0x01, res=0x{0:X})", outVal);
                        return true;
                    }
                    // q=0x01 vraca eksplicitnu capability masku (res=0x0 znaci da osvetljenje nije podrzano)
                    return false;
                }

                inVal = 0x32u;
                outVal = 0;
                bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x83102144u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u &&
                    (outVal & 0x01u) != 0u)
                {
                    detail = string.Format("EnergyDrv (0x83102144, q=0x32, res=0x{0:X})", outVal);
                    return true;
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }

            return false;
        }

        public static bool TryReadLenovoEnergyDrvBacklightRaw(out uint rawState)
        {
            rawState = 0u;
            IntPtr hDev = OpenKernelDeviceHandle(@"\\.\EnergyDrv");
            if (hDev == IntPtr.Zero) return false;

            try
            {
                uint inVal = 0x32u;
                uint outVal = 0;
                uint bytesRet = 0;
                if (NativeMethods.DeviceIoControl(hDev, 0x83102144u, ref inVal, 4u, ref outVal, 4u, out bytesRet, IntPtr.Zero) &&
                    bytesRet == 4u &&
                    (outVal & 0x80000000u) == 0u)
                {
                    rawState = outVal;
                    return true;
                }
            }
            catch { }
            finally
            {
                NativeMethods.CloseHandle(hDev);
            }

            return false;
        }

        /// <summary>
        /// Proverava da li uređaj u klasi Image predstavlja skener, štampač, virtuelnu kameru ili telefon umesto fizičke kamere.
        /// </summary>
        public static bool IsNonPhysicalCameraImageDevice(string upperText)
        {
            if (string.IsNullOrEmpty(upperText)) return false;
            return upperText.Contains("SCANNER") ||
                   upperText.Contains("PRINTER") ||
                   upperText.Contains("VIRTUAL") ||
                   Regex.IsMatch(upperText, @"\bOBS\b") ||
                   upperText.Contains("OBS_") ||
                   upperText.Contains("OBS-") ||
                   upperText.Contains("XIAOMI") ||
                   Regex.IsMatch(upperText, @"\bPHONE\b") ||
                   upperText.Contains("IPHONE") ||
                   upperText.Contains("SMARTPHONE");
        }

        /// <summary>
        /// Određuje da li PnP kamera predstavlja IR / Windows Hello Face senzor bez lažnih pogodaka podstringa "IR".
        /// </summary>
        public static bool IsIrCameraEntry(string name, string desc, string devId)
        {
            string combined = ((name ?? "") + " " + (desc ?? "") + " " + (devId ?? "")).ToUpperInvariant();
            if (string.IsNullOrEmpty(combined.Trim())) return false;

            if (Regex.IsMatch(combined, @"\bIR\b")) return true;

            return combined.Contains("IR CAMERA") ||
                   combined.Contains("IR SENSOR") ||
                   combined.Contains("INFRARED") ||
                   combined.Contains("HELLO FACE") ||
                   combined.Contains("WINDOWS HELLO") ||
                   combined.Contains("IR_CAMERA") ||
                   combined.Contains("DEPTH CAMERA");
        }

        /// <summary>
        /// Stroga provera PnP kandidata za čitač otiska prsta koja sprečava lažne pogotke za ELAN/Goodix/Synaptics touch i touchpad uređaje.
        /// </summary>
        public static bool IsFingerprintPnpCandidate(
            string name,
            string desc,
            string devId,
            string pnpClass,
            string svc,
            uint errCode,
            out bool isBiometricClass)
        {
            string pnpCls = (pnpClass ?? "").Trim();
            isBiometricClass = pnpCls.Equals("Biometric", StringComparison.OrdinalIgnoreCase);

            string upId = (devId ?? "").Trim().ToUpperInvariant();
            string combinedText = ((name ?? "") + " " + (desc ?? "") + " " + (svc ?? "") + " " + pnpCls).ToUpperInvariant();

            if (string.IsNullOrEmpty(upId) && string.IsNullOrEmpty(combinedText.Trim()))
            {
                return false;
            }

            // Isključujemo kamere/IR/audio/miš/tastature/system bridge i Windows Hello software face uređaje
            if (pnpCls.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
                pnpCls.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                upId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                upId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) ||
                combinedText.Contains("FACIAL RECOGNITION") ||
                combinedText.Contains("HELLO FACE") ||
                combinedText.Contains("SOFTWARE DEVICE"))
            {
                return false;
            }

            // Proširena lista isključenja za touch/pointing/smartcard/NFC/virtual čvorove (osim za Biometric klasu)
            if (!isBiometricClass && (
                combinedText.Contains("TOUCHSCREEN") ||
                combinedText.Contains("TOUCH SCREEN") ||
                combinedText.Contains("DIGITIZER") ||
                combinedText.Contains("TOUCHPAD") ||
                combinedText.Contains("TRACKPAD") ||
                combinedText.Contains("POINTING") ||
                combinedText.Contains("PEN ") ||
                combinedText.Contains("STYLUS") ||
                combinedText.Contains("SMART CARD") ||
                combinedText.Contains("SMARTCARD") ||
                combinedText.Contains("NFC") ||
                combinedText.Contains("VIRTUAL")))
            {
                return false;
            }

            // Ako je USB\ROOT_HUB ili HID čvor koji nije Biometric klasa, preskačemo
            if (upId.Contains("ROOT_HUB") || upId.StartsWith("HID\\"))
            {
                if (!isBiometricClass)
                    return false;
            }

            // Isključivi fingerprint VID-ovi i ACPI/SPI ID-jevi
            bool isExclusiveFpId =
                upId.StartsWith("USB\\VID_138A") || // Validity USB FP
                upId.StartsWith("USB\\VID_1C7A") || // EgisTec USB FP
                upId.StartsWith("USB\\VID_2808") || // FocalTech USB FP
                upId.StartsWith("USB\\VID_147E") || // AuthenTec/Upek USB FP
                upId.StartsWith("USB\\VID_08FF") || // AuthenTec USB FP
                upId.Contains("BIOMETRIC") ||
                upId.Contains("ELAN7") ||
                upId.Contains("SYNA36") ||
                upId.Contains("SYNAPTICS_FP") ||
                upId.Contains("GXFP") ||
                upId.Contains("FPC0") ||
                upId.Contains("VFS");

            bool hasExplicitFpToken =
                isBiometricClass ||
                combinedText.Contains("FINGERPRINT") ||
                combinedText.Contains("FINGER PRINT") ||
                combinedText.Contains("BIOMETRIC") ||
                combinedText.Contains("WBDI") ||
                combinedText.Contains("WBF") ||
                combinedText.Contains("TOUCHCHIP") ||
                combinedText.Contains("FPR");

            bool isExclusiveFpName =
                combinedText.Contains("VALIDITY") ||
                combinedText.Contains("EGISTEC") ||
                combinedText.Contains("EGIS ");

            // Multifunkcionalni vendori (ELAN, Synaptics, Goodix prave i touchscreen/touchpad i fingerprint)
            bool isMultiFuncVendor =
                upId.StartsWith("USB\\VID_04F3") || // ELAN
                upId.StartsWith("USB\\VID_06CB") || // Synaptics
                upId.StartsWith("USB\\VID_27C6") || // Goodix
                combinedText.Contains("GOODIX");

            if (isMultiFuncVendor)
            {
                bool isErrorOrUnknownNonHid =
                    (errCode != 0 || pnpCls.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) &&
                    !upId.StartsWith("HID\\") &&
                    !pnpCls.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) &&
                    !combinedText.Contains("TOUCH") &&
                    !combinedText.Contains("MOUSE") &&
                    !combinedText.Contains("CAMERA") &&
                    !combinedText.Contains("WEBCAM");

                return isExclusiveFpId || isBiometricClass || hasExplicitFpToken || isErrorOrUnknownNonHid;
            }

            return isExclusiveFpId || hasExplicitFpToken || isExclusiveFpName;
        }

        private static void TryWakeWbioService(HardwareProbeResult res, string fallbackStartMode)
        {
            try
            {
                using (ManagementObjectSearcher svcSearcher = CreateSearcher(
                    "SELECT Name, State, StartMode FROM Win32_Service WHERE Name = 'WbioSrvc'"))
                {
                    foreach (ManagementObject sObj in svcSearcher.Get())
                    {
                        string state = (sObj["State"] ?? "").ToString().Trim();
                        string sm = (sObj["StartMode"] ?? fallbackStartMode ?? "").ToString().Trim();
                        if (state.Equals("Running", StringComparison.OrdinalIgnoreCase))
                        {
                            res.WbioServiceState = string.Format("{0} ({1})", state, sm);
                            return;
                        }
                        if (sm.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        try
                        {
                            sObj.InvokeMethod("StartService", null);
                        }
                        catch
                        {
                            return;
                        }
                        break;
                    }
                }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(2500);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(250);
                    using (ManagementObjectSearcher pollSearcher = CreateSearcher(
                        "SELECT State, StartMode FROM Win32_Service WHERE Name = 'WbioSrvc'"))
                    {
                        foreach (ManagementObject pObj in pollSearcher.Get())
                        {
                            string curState = (pObj["State"] ?? "").ToString().Trim();
                            string curSm = (pObj["StartMode"] ?? fallbackStartMode ?? "").ToString().Trim();
                            res.WbioServiceState = string.Format("{0} ({1})", curState, curSm);
                            if (curState.Equals("Running", StringComparison.OrdinalIgnoreCase))
                                return;
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }
}
