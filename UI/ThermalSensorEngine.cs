using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Reflection;

namespace WIS.UI
{
    /// <summary>
    /// Podaci o taktu pojedinačnog procesorskog jezgra (trenutni, minimalni i maksimalni MHz).
    /// </summary>
    public class CoreClockSensorInfo
    {
        public int CoreIndex { get; set; }
        public string CoreName { get; set; }
        public float CurrentMHz { get; set; }
        public float MinMHz { get; set; }
        public float MaxMHz { get; set; }
    }

    /// <summary>
    /// Senzorski telemetrijski motor za praćenje temperature CPU, GPU, MB, SSD, taktova jezgara,
    /// throttling procenta i broja obrtaja ventilatora.
    /// Koristi LibreHardwareMonitorLib (isključivo refleksijom ako je DLL prisutan na disku)
    /// sa automatskim fallback-om na čiste WMI/ACPI/PDH mehanizme bez kernel drajvera.
    /// Striktna kompatibilnost sa C# 5.0 (.NET Framework 4.8 / MSBuild 4.0).
    /// </summary>
    public class ThermalSensorEngine : IDisposable
    {
        private object _computer;
        private MethodInfo _miComputerOpen;
        private MethodInfo _miComputerClose;
        private PropertyInfo _piComputerHardware;

        private PropertyInfo _piHwType;
        private PropertyInfo _piHwSensors;
        private PropertyInfo _piHwSubHardware;
        private MethodInfo _miHwUpdate;

        private PropertyInfo _piSensorName;
        private PropertyInfo _piSensorType;
        private PropertyInfo _piSensorValue;

        private bool _isDisposed = false;
        private readonly object _syncLock = new object();

        private float _cpuTemperature;
        private float _gpuTemperature;
        private float _motherboardTemperature;
        private float _ssdTemperature;
        private float _throttlingPercentage;
        private int _fanRpm;
        private bool _isLhmActive;
        private bool _hasFanData;
        private readonly List<CoreClockSensorInfo> _coreClocks = new List<CoreClockSensorInfo>();
        private float _historicalPeakClockMHz = 0f;
        private float _baseMaxClockMHz = 0f;

        public float CpuTemperature
        {
            get { return _cpuTemperature; }
            private set { _cpuTemperature = value; }
        }

        public float GpuTemperature
        {
            get { return _gpuTemperature; }
            private set { _gpuTemperature = value; }
        }

        public float MotherboardTemperature
        {
            get { return _motherboardTemperature; }
            private set { _motherboardTemperature = value; }
        }

        public float SsdTemperature
        {
            get { return _ssdTemperature; }
            private set { _ssdTemperature = value; }
        }

        public float ThrottlingPercentage
        {
            get { return _throttlingPercentage; }
            private set { _throttlingPercentage = value; }
        }

        public int FanRpm
        {
            get { return _fanRpm; }
            private set { _fanRpm = value; }
        }

        public bool IsLhmActive
        {
            get { return _isLhmActive; }
            private set { _isLhmActive = value; }
        }

        public bool HasFanData
        {
            get { return _hasFanData; }
            private set { _hasFanData = value; }
        }

        public bool IsSensorAvailable { get; private set; }
        public string SensorSource { get; private set; }
        public DateTime LastValidSampleUtc { get; private set; }
        public bool IsThrottlingMetricAvailable { get; private set; }

        public List<CoreClockSensorInfo> CoreClocks
        {
            get { return GetCoreClocksSnapshot(); }
        }

        public List<CoreClockSensorInfo> GetCoreClocksSnapshot()
        {
            lock (_syncLock)
            {
                List<CoreClockSensorInfo> snapshot = new List<CoreClockSensorInfo>(_coreClocks.Count);
                for (int i = 0; i < _coreClocks.Count; i++)
                {
                    CoreClockSensorInfo src = _coreClocks[i];
                    CoreClockSensorInfo copy = new CoreClockSensorInfo();
                    copy.CoreIndex = src.CoreIndex;
                    copy.CoreName = src.CoreName;
                    copy.CurrentMHz = src.CurrentMHz;
                    copy.MinMHz = src.MinMHz;
                    copy.MaxMHz = src.MaxMHz;
                    snapshot.Add(copy);
                }
                return snapshot;
            }
        }

        /// <summary>
        /// Resetuje izmerene MinMHz i MaxMHz vrednosti svih jezgara na trenutni takt (CurrentMHz).
        /// </summary>
        public void ResetCoreClockExtremes()
        {
            lock (_syncLock)
            {
                for (int i = 0; i < _coreClocks.Count; i++)
                {
                    _coreClocks[i].MinMHz = _coreClocks[i].CurrentMHz;
                    _coreClocks[i].MaxMHz = _coreClocks[i].CurrentMHz;
                }
            }
        }

        public ThermalSensorEngine()
        {
            IsSensorAvailable = false;
            SensorSource = "NONE";
            LastValidSampleUtc = DateTime.MinValue;
            IsThrottlingMetricAvailable = false;
            InitializeLhm();
        }

        /// <summary>
        /// Sigurna inicijalizacija LibreHardwareMonitor.Hardware.Computer putem refleksije.
        /// Učitava se isključivo ako je LibreHardwareMonitorLib već učitan ili postoji pored aplikacije,
        /// čime WIS.HwTest.exe ne sadrži ugrađen WinRing0 drajver koji okida Windows Defender.
        /// </summary>
        private void InitializeLhm()
        {
            try
            {
                Assembly lhmAsm = null;
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    try
                    {
                        if (string.Equals(loaded[i].GetName().Name, "LibreHardwareMonitorLib", StringComparison.OrdinalIgnoreCase))
                        {
                            lhmAsm = loaded[i];
                            break;
                        }
                    }
                    catch { }
                }

                if (lhmAsm == null)
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(baseDir))
                    {
                        string candidatePath = Path.Combine(baseDir, "LibreHardwareMonitorLib.dll");
                        if (File.Exists(candidatePath))
                        {
                            lhmAsm = Assembly.LoadFrom(candidatePath);
                        }
                    }
                }

                if (lhmAsm == null)
                {
                    IsLhmActive = false;
                    return;
                }

                Type compType = lhmAsm.GetType("LibreHardwareMonitor.Hardware.Computer", false);
                Type hwType = lhmAsm.GetType("LibreHardwareMonitor.Hardware.IHardware", false);
                Type sensorType = lhmAsm.GetType("LibreHardwareMonitor.Hardware.ISensor", false);
                if (compType == null || hwType == null || sensorType == null)
                {
                    IsLhmActive = false;
                    return;
                }

                object comp = Activator.CreateInstance(compType);
                SetBoolProperty(compType, comp, "IsCpuEnabled", true);
                SetBoolProperty(compType, comp, "IsGpuEnabled", true);
                SetBoolProperty(compType, comp, "IsMotherboardEnabled", true);
                SetBoolProperty(compType, comp, "IsStorageEnabled", true);
                SetBoolProperty(compType, comp, "IsControllerEnabled", true);

                _miComputerOpen = compType.GetMethod("Open", Type.EmptyTypes);
                _miComputerClose = compType.GetMethod("Close", Type.EmptyTypes);
                _piComputerHardware = compType.GetProperty("Hardware");

                _piHwType = hwType.GetProperty("HardwareType");
                _piHwSensors = hwType.GetProperty("Sensors");
                _piHwSubHardware = hwType.GetProperty("SubHardware");
                _miHwUpdate = hwType.GetMethod("Update", Type.EmptyTypes);

                _piSensorName = sensorType.GetProperty("Name");
                _piSensorType = sensorType.GetProperty("SensorType");
                _piSensorValue = sensorType.GetProperty("Value");

                if (_miComputerOpen == null || _piComputerHardware == null || _piHwType == null || _piHwSensors == null ||
                    _piSensorName == null || _piSensorType == null || _piSensorValue == null)
                {
                    IsLhmActive = false;
                    return;
                }

                _miComputerOpen.Invoke(comp, null);
                _computer = comp;
                IsLhmActive = true;
            }
            catch (Exception)
            {
                IsLhmActive = false;
                if (_computer != null)
                {
                    try
                    {
                        if (_miComputerClose != null) _miComputerClose.Invoke(_computer, null);
                    }
                    catch { }
                    _computer = null;
                }
            }
        }

        private static void SetBoolProperty(Type t, object target, string propName, bool val)
        {
            PropertyInfo pi = t.GetProperty(propName);
            if (pi != null && pi.CanWrite)
            {
                pi.SetValue(target, val, null);
            }
        }

        /// <summary>
        /// Osvežava telemetrijske podatke o temperaturama i ventilatorima.
        /// </summary>
        public void Update()
        {
            lock (_syncLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                if (IsLhmActive && _computer != null)
                {
                    try
                    {
                        UpdateFromLhm();
                        return;
                    }
                    catch (Exception)
                    {
                        IsLhmActive = false;
                    }
                }

                UpdateFromWmiFallback();
            }
        }

        /// <summary>
        /// Očitavanje preko LibreHardwareMonitorLib biblioteke (putem refleksije).
        /// </summary>
        private void UpdateFromLhm()
        {
            List<object> allHardware = new List<object>();
            IEnumerable topHw = _piComputerHardware.GetValue(_computer, null) as IEnumerable;
            if (topHw != null)
            {
                foreach (object hw in topHw)
                {
                    CollectHardwareRecursive(hw, allHardware);
                }
            }

            foreach (object hw in allHardware)
            {
                try
                {
                    if (_miHwUpdate != null) _miHwUpdate.Invoke(hw, null);
                }
                catch { }
            }

            // Privremene promenljive za CPU
            float cpuPackage = 0f;
            float cpuMaxCore = 0f;
            List<float> cpuCores = new List<float>();
            float cpuAny = 0f;
            float cpuLoadTotal = 0f;
            float explicitThrottlePercent = -1f;
            List<object> primaryCoreClockSensors = new List<object>();
            List<object> effectiveCoreClockSensors = new List<object>();

            // Privremene promenljive za GPU
            float gpuCore = 0f;
            float gpuHotspot = 0f;
            float gpuAny = 0f;

            // Privremene promenljive za Motherboard / SuperIO
            float mbSystem = 0f;
            float mbAny = 0f;

            // Privremene promenljive za SSD
            float ssdMax = 0f;

            // Privremene promenljive za Fan
            bool foundFan = false;
            int maxFan = 0;
            int firstPositiveFan = 0;

            foreach (object hw in allHardware)
            {
                object hwTypeVal = _piHwType.GetValue(hw, null);
                string hwTypeName = hwTypeVal != null ? hwTypeVal.ToString() : string.Empty;
                IEnumerable sensors = _piHwSensors.GetValue(hw, null) as IEnumerable;

                // CPU obrada
                if (hwTypeName == "Cpu")
                {
                    if (sensors != null)
                    {
                        foreach (object sensor in sensors)
                        {
                            string name = (_piSensorName.GetValue(sensor, null) as string) ?? string.Empty;
                            object stVal = _piSensorType.GetValue(sensor, null);
                            string stName = stVal != null ? stVal.ToString() : string.Empty;
                            float? sVal = _piSensorValue.GetValue(sensor, null) as float?;

                            if (stName == "Temperature" && sVal.HasValue)
                            {
                                float val = sVal.Value;
                                if (val > 0.5f && val < 130.0f)
                                {
                                    if (name.IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        cpuPackage = val;
                                    }
                                    else if (name.IndexOf("core max", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             name.IndexOf("tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             name.IndexOf("ccd", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (val > cpuMaxCore) cpuMaxCore = val;
                                    }
                                    else if (name.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        cpuCores.Add(val);
                                    }

                                    if (cpuAny <= 0f) cpuAny = val;
                                }
                            }
                            else if (stName == "Clock" && sVal.HasValue)
                            {
                                if (name.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    name.IndexOf("bus", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    if (name.IndexOf("effective", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        effectiveCoreClockSensors.Add(sensor);
                                    }
                                    else
                                    {
                                        primaryCoreClockSensors.Add(sensor);
                                    }
                                }
                            }
                            else if (stName == "Load" && sVal.HasValue)
                            {
                                if (name.IndexOf("total", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    cpuLoadTotal = sVal.Value;
                                }
                            }

                            // Eksplicitni indikator termalnog / energetskog usporenja (throttling) ako postoji
                            if (sVal.HasValue &&
                                (name.IndexOf("throttl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("prochot", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                if (explicitThrottlePercent < 0f) explicitThrottlePercent = 0f;
                                float tVal = sVal.Value;
                                if (tVal > 0f)
                                {
                                    float pct = tVal <= 1.0f ? (tVal * 100f) : (tVal > 100f ? 100f : tVal);
                                    if (pct > explicitThrottlePercent) explicitThrottlePercent = pct;
                                }
                            }
                        }
                    }
                }
                // Motherboard i SuperIO obrada
                else if (hwTypeName == "Motherboard" || hwTypeName == "SuperIO")
                {
                    if (sensors != null)
                    {
                        foreach (object sensor in sensors)
                        {
                            object stVal = _piSensorType.GetValue(sensor, null);
                            string stName = stVal != null ? stVal.ToString() : string.Empty;
                            float? sVal = _piSensorValue.GetValue(sensor, null) as float?;

                            if (stName == "Temperature" && sVal.HasValue)
                            {
                                float val = sVal.Value;
                                if (val > 0f && val < 120f)
                                {
                                    string name = (_piSensorName.GetValue(sensor, null) as string) ?? string.Empty;
                                    // Ignorišemo CPU PECI senzore na SuperIO čipu ako tražimo temperaturu matične ploče
                                    bool isCpuMirror = name.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       name.IndexOf("peci", StringComparison.OrdinalIgnoreCase) >= 0;

                                    if (!isCpuMirror &&
                                        (name.IndexOf("system", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("motherboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("mainboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("systin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("chipset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("pch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("vrm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("temperature #1", StringComparison.OrdinalIgnoreCase) >= 0))
                                    {
                                        if (mbSystem <= 0f || val > mbSystem)
                                        {
                                            mbSystem = val;
                                        }
                                    }

                                    if (!isCpuMirror && mbAny <= 0f)
                                    {
                                        mbAny = val;
                                    }
                                }
                            }
                        }
                    }
                }
                // GPU obrada
                else if (hwTypeName == "GpuNvidia" || hwTypeName == "GpuAmd" || hwTypeName == "GpuIntel")
                {
                    if (sensors != null)
                    {
                        foreach (object sensor in sensors)
                        {
                            object stVal = _piSensorType.GetValue(sensor, null);
                            string stName = stVal != null ? stVal.ToString() : string.Empty;
                            float? sVal = _piSensorValue.GetValue(sensor, null) as float?;

                            if (stName == "Temperature" && sVal.HasValue)
                            {
                                float val = sVal.Value;
                                if (val > 0f && val < 135f)
                                {
                                    string name = (_piSensorName.GetValue(sensor, null) as string) ?? string.Empty;
                                    if (name.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (val > gpuCore) gpuCore = val;
                                    }
                                    else if (name.IndexOf("hot spot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             name.IndexOf("hotspot", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (val > gpuHotspot) gpuHotspot = val;
                                    }

                                    if (val > gpuAny) gpuAny = val;
                                }
                            }
                        }
                    }
                }
                // Storage (SSD / NVMe / HDD) obrada
                else if (hwTypeName == "Storage")
                {
                    if (sensors != null)
                    {
                        foreach (object sensor in sensors)
                        {
                            object stVal = _piSensorType.GetValue(sensor, null);
                            string stName = stVal != null ? stVal.ToString() : string.Empty;
                            float? sVal = _piSensorValue.GetValue(sensor, null) as float?;

                            if (stName == "Temperature" && sVal.HasValue)
                            {
                                float val = sVal.Value;
                                if (val > 0f && val < 125f)
                                {
                                    if (val > ssdMax) ssdMax = val;
                                }
                            }
                        }
                    }
                }

                // Ventilatori (Fan senzori mogu biti na matičnoj ploči, GPU-u, kontroleru ili hlađenju)
                if (sensors != null)
                {
                    foreach (object sensor in sensors)
                    {
                        object stVal = _piSensorType.GetValue(sensor, null);
                        string stName = stVal != null ? stVal.ToString() : string.Empty;
                        if (stName == "Fan")
                        {
                            foundFan = true;
                            float? sVal = _piSensorValue.GetValue(sensor, null) as float?;
                            if (sVal.HasValue)
                            {
                                float val = sVal.Value;
                                if (val >= 0f && val < 50000f)
                                {
                                    int rpm = (int)Math.Round(val);
                                    if (rpm > maxFan) maxFan = rpm;
                                    if (firstPositiveFan == 0 && rpm > 0) firstPositiveFan = rpm;
                                }
                            }
                        }
                    }
                }
            }

            // Izbor CPU temperature po prioritetu: Package -> Core Max -> Prosek jezgara -> Any -> WMI fallback
            float sampleCpu = 0f;
            string sampleSource = "NONE";

            if (cpuPackage > 0.5f && cpuPackage < 130.0f)
            {
                sampleCpu = (float)Math.Round(cpuPackage, 1);
                sampleSource = "LHM_PACKAGE";
            }
            else if (cpuMaxCore > 0.5f && cpuMaxCore < 130.0f)
            {
                sampleCpu = (float)Math.Round(cpuMaxCore, 1);
                sampleSource = "LHM_CORE_MAX";
            }
            else if (cpuCores.Count > 0)
            {
                float sum = 0f;
                for (int i = 0; i < cpuCores.Count; i++) sum += cpuCores[i];
                float avgCoreTemp = sum / cpuCores.Count;
                if (avgCoreTemp > 0.5f && avgCoreTemp < 130.0f)
                {
                    sampleCpu = (float)Math.Round(avgCoreTemp, 1);
                    sampleSource = "LHM_CORE_AVG";
                }
            }
            else if (cpuAny > 0.5f && cpuAny < 130.0f)
            {
                sampleCpu = (float)Math.Round(cpuAny, 1);
                sampleSource = "LHM_CPU";
            }

            if (sampleCpu <= 0.5f)
            {
                // Dopunski WMI fallback za CPU ako LHM nema temperaturni senzor
                float wmiCpu = ReadCpuTempWmi();
                if (wmiCpu > 0.5f && wmiCpu < 130.0f)
                {
                    sampleCpu = (float)Math.Round(wmiCpu, 1);
                    sampleSource = "WMI_ACPI";
                }
            }

            ApplyCpuTemperatureSample(sampleCpu, sampleSource);

            // Izbor GPU temperature po prioritetu: Core -> Hotspot -> Any
            if (gpuCore > 0f)
            {
                GpuTemperature = (float)Math.Round(gpuCore, 1);
            }
            else if (gpuHotspot > 0f)
            {
                GpuTemperature = (float)Math.Round(gpuHotspot, 1);
            }
            else if (gpuAny > 0f)
            {
                GpuTemperature = (float)Math.Round(gpuAny, 1);
            }

            // Izbor temperature matične ploče: System/Chipset -> Any -> WMI ACPI fallback
            if (mbSystem > 0f)
            {
                MotherboardTemperature = (float)Math.Round(mbSystem, 1);
            }
            else if (mbAny > 0f)
            {
                MotherboardTemperature = (float)Math.Round(mbAny, 1);
            }
            else
            {
                float wmiMb = ReadMotherboardTempWmi();
                if (wmiMb > 0f)
                {
                    MotherboardTemperature = (float)Math.Round(wmiMb, 1);
                }
            }

            // Izbor SSD temperature
            if (ssdMax > 0f)
            {
                SsdTemperature = (float)Math.Round(ssdMax, 1);
            }
            else
            {
                // Dopunski WMI fallback za SSD SMART
                float wmiSsd = ReadSsdTempWmi();
                if (wmiSsd > 0f)
                {
                    SsdTemperature = (float)Math.Round(wmiSsd, 1);
                }
            }

            // Obrada taktova procesorskih jezgara (CoreClocks)
            List<object> selectedClockSensors = primaryCoreClockSensors.Count > 0 ? primaryCoreClockSensors : effectiveCoreClockSensors;
            float avgCoreClock = 0f;
            int validClockCount = 0;

            if (selectedClockSensors.Count > 0)
            {
                for (int i = 0; i < selectedClockSensors.Count; i++)
                {
                    object clkSensor = selectedClockSensors[i];
                    float? clkVal = _piSensorValue.GetValue(clkSensor, null) as float?;
                    if (clkVal.HasValue)
                    {
                        float mhz = clkVal.Value;
                        if (mhz > 0f && mhz < 12000f)
                        {
                            float roundedMhz = (float)Math.Round(mhz, 0);
                            string rawSensorName = (_piSensorName.GetValue(clkSensor, null) as string) ?? string.Empty;
                            string coreLabel = NormalizeCoreSensorName(rawSensorName, i + 1);
                            UpdateCoreClockEntry(i, coreLabel, roundedMhz);

                            if (roundedMhz > _historicalPeakClockMHz)
                            {
                                _historicalPeakClockMHz = roundedMhz;
                            }

                            avgCoreClock += roundedMhz;
                            validClockCount++;
                        }
                    }
                }
            }

            if (validClockCount > 0)
            {
                avgCoreClock = avgCoreClock / validClockCount;
            }
            else
            {
                // Ako LHM nije vratio senzore takta jezgara, koristi WMI fallback
                UpdateCoreClocksFromWmiFallback(out avgCoreClock);
            }

            // Izračunavanje procenta Throttling-a (0% - 100%)
            ThrottlingPercentage = ComputeThrottlingPercentage(explicitThrottlePercent, CpuTemperature, cpuLoadTotal, avgCoreClock, _historicalPeakClockMHz, EnsureBaseMaxClockMHz());

            // Izbor Fan RPM i statusa prisustva
            HasFanData = foundFan;
            FanRpm = maxFan > 0 ? maxFan : (firstPositiveFan > 0 ? firstPositiveFan : 0);

            if (!HasFanData)
            {
                int wmiFanRpm;
                bool wmiHasFan;
                ReadFanWmi(out wmiFanRpm, out wmiHasFan);
                if (wmiHasFan)
                {
                    HasFanData = true;
                    FanRpm = wmiFanRpm;
                }
            }
        }

        private void ApplyCpuTemperatureSample(float sampleTemp, string source)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (sampleTemp > 0.5f && sampleTemp < 130.0f)
            {
                CpuTemperature = sampleTemp;
                LastValidSampleUtc = nowUtc;
                SensorSource = !string.IsNullOrEmpty(source) ? source : "UNKNOWN";
                IsSensorAvailable = true;
            }
            else
            {
                bool hasRecentSample = (LastValidSampleUtc != DateTime.MinValue) &&
                                       ((nowUtc - LastValidSampleUtc).TotalSeconds < 15.0) &&
                                       (CpuTemperature > 0.5f && CpuTemperature < 130.0f);
                if (hasRecentSample)
                {
                    IsSensorAvailable = true;
                }
                else
                {
                    CpuTemperature = 0f;
                    IsSensorAvailable = false;
                    SensorSource = "NONE";
                }
            }
        }

        /// <summary>
        /// Normalizuje naziv senzora takta jezgra (npr. "CPU Core #1" -> "Core #1").
        /// </summary>
        private string NormalizeCoreSensorName(string rawName, int fallbackIndex)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return string.Format("Core #{0}", fallbackIndex);
            }

            string cleaned = rawName.Trim();
            if (cleaned.StartsWith("CPU ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(4).Trim();
            }

            int effIdx = cleaned.IndexOf("(Effective)", StringComparison.OrdinalIgnoreCase);
            if (effIdx > 0)
            {
                cleaned = cleaned.Substring(0, effIdx).Trim();
            }

            return cleaned;
        }

        /// <summary>
        /// Ažurira ili dodaje unos za procesorsko jezgro uz praćenje MinMHz i MaxMHz ekstrema.
        /// </summary>
        private void UpdateCoreClockEntry(int index, string coreName, float currentMHz)
        {
            CoreClockSensorInfo existing = null;
            for (int i = 0; i < _coreClocks.Count; i++)
            {
                if (_coreClocks[i].CoreIndex == index)
                {
                    existing = _coreClocks[i];
                    break;
                }
            }

            if (existing == null)
            {
                CoreClockSensorInfo info = new CoreClockSensorInfo();
                info.CoreIndex = index;
                info.CoreName = coreName;
                info.CurrentMHz = currentMHz;
                info.MinMHz = currentMHz;
                info.MaxMHz = currentMHz;
                _coreClocks.Add(info);
            }
            else
            {
                existing.CoreName = coreName;
                existing.CurrentMHz = currentMHz;
                if (existing.MinMHz <= 0f || currentMHz < existing.MinMHz)
                {
                    existing.MinMHz = currentMHz;
                }
                if (currentMHz > existing.MaxMHz)
                {
                    existing.MaxMHz = currentMHz;
                }
            }
        }

        private float EnsureBaseMaxClockMHz()
        {
            if (_baseMaxClockMHz > 500f) return _baseMaxClockMHz;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT MaxClockSpeed FROM Win32_Processor"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["MaxClockSpeed"] != null)
                            {
                                float max = Convert.ToSingle(obj["MaxClockSpeed"]);
                                if (max > _baseMaxClockMHz) _baseMaxClockMHz = max;
                            }
                        }
                    }
                }
            }
            catch { }
            return _baseMaxClockMHz;
        }

        /// <summary>
        /// Proračun procenta stvarnog termalnog / energetskog throttling-a (0% - 100%)
        /// isključivo iz eksplicitnog senzora ili izmerenog pada frekvencije ispod baznog takta.
        /// Normalno spuštanje sa single-core Turbo špica na all-core Turbo takt nije throttling.
        /// </summary>
        private float ComputeThrottlingPercentage(float explicitSensorPct, float cpuTemp, float cpuLoad, float avgClock, float peakClock, float baseMaxClock)
        {
            bool hasExplicitSensor = (explicitSensorPct >= 0f);
            bool hasValidClockDropMetric = (baseMaxClock > 500f && avgClock > 0f) || (peakClock > 500f && avgClock > 0f);
            IsThrottlingMetricAvailable = hasExplicitSensor || hasValidClockDropMetric;

            if (!IsThrottlingMetricAvailable)
            {
                return 0f;
            }

            float result = hasExplicitSensor ? explicitSensorPct : 0f;

            // Pad frekvencije merimo u odnosu na nominalni bazni takt (ili peakClock samo ako baza nije poznata i temp >= 82°C).
            // Ako je prosečni takt >= 90% baznog takta i temperatura < 90°C, procesor radi normalno (all-core boost).
            bool normalAllCoreRate = (baseMaxClock > 500f && avgClock >= (baseMaxClock * 0.90f) && cpuTemp < 90f);
            if (!normalAllCoreRate && avgClock > 100f)
            {
                float refClock = baseMaxClock > 500f ? baseMaxClock : ((cpuLoad >= 55f || cpuTemp >= 82f) ? peakClock : 0f);
                if (refClock > 500f)
                {
                    float ratio = avgClock / refClock;
                    if (ratio < 0.88f)
                    {
                        float clockDropPct = (1.0f - ratio) * 100f;
                        if (clockDropPct > result) result = clockDropPct;
                    }
                }
            }

            if (result < 0f) result = 0f;
            if (result > 100f) result = 100f;
            return (float)Math.Round(result, 1);
        }

        /// <summary>
        /// Rekurzivno sakuplja sve hardverske čvorove (roditelje i podkomponente poput SuperIO).
        /// </summary>
        private void CollectHardwareRecursive(object hardware, List<object> list)
        {
            if (hardware == null) return;
            list.Add(hardware);
            IEnumerable subHw = _piHwSubHardware != null ? (_piHwSubHardware.GetValue(hardware, null) as IEnumerable) : null;
            if (subHw != null)
            {
                foreach (object sub in subHw)
                {
                    CollectHardwareRecursive(sub, list);
                }
            }
        }

        /// <summary>
        /// Očitavanje telemetrije preko WMI / ACPI mehanizama kada LHM nije dostupan.
        /// </summary>
        private void UpdateFromWmiFallback()
        {
            float cpu = ReadCpuTempWmi();
            if (cpu > 0.5f && cpu < 130.0f)
            {
                ApplyCpuTemperatureSample((float)Math.Round(cpu, 1), "WMI_ACPI");
            }
            else
            {
                ApplyCpuTemperatureSample(0f, "NONE");
            }

            float gpu = ReadGpuTempWmi();
            if (gpu > 0f)
            {
                GpuTemperature = (float)Math.Round(gpu, 1);
            }

            float mb = ReadMotherboardTempWmi();
            if (mb > 0f)
            {
                MotherboardTemperature = (float)Math.Round(mb, 1);
            }

            float ssd = ReadSsdTempWmi();
            if (ssd > 0f)
            {
                SsdTemperature = (float)Math.Round(ssd, 1);
            }

            float avgClock;
            UpdateCoreClocksFromWmiFallback(out avgClock);
            ThrottlingPercentage = ComputeThrottlingPercentage(-1f, CpuTemperature, 0f, avgClock, _historicalPeakClockMHz, _baseMaxClockMHz);

            int fanRpm;
            bool hasFan;
            ReadFanWmi(out fanRpm, out hasFan);
            FanRpm = fanRpm;
            HasFanData = hasFan;
        }

        /// <summary>
        /// Očitavanje taktova jezgara preko WMI (Win32_Processor i Win32_PerfFormattedData_Counters_ProcessorInformation)
        /// kada LHM drajver nije aktivan ili ne vraća senzore takta.
        /// </summary>
        private void UpdateCoreClocksFromWmiFallback(out float averageClockMHz)
        {
            averageClockMHz = 0f;
            float baseCurrentClock = 0f;
            float baseMaxClock = 0f;
            int coreCount = Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 1;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT CurrentClockSpeed, MaxClockSpeed, NumberOfCores FROM Win32_Processor"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["CurrentClockSpeed"] != null)
                            {
                                float cur = Convert.ToSingle(obj["CurrentClockSpeed"]);
                                if (cur > baseCurrentClock) baseCurrentClock = cur;
                            }
                            if (obj["MaxClockSpeed"] != null)
                            {
                                float max = Convert.ToSingle(obj["MaxClockSpeed"]);
                                if (max > baseMaxClock) baseMaxClock = max;
                            }
                            if (obj["NumberOfCores"] != null)
                            {
                                int cores = Convert.ToInt32(obj["NumberOfCores"]);
                                if (cores > 0) coreCount = cores;
                            }
                        }
                    }
                }
            }
            catch { }

            if (baseMaxClock > 500f && baseMaxClock > _baseMaxClockMHz)
            {
                _baseMaxClockMHz = baseMaxClock;
            }

            if (baseMaxClock > _historicalPeakClockMHz)
            {
                _historicalPeakClockMHz = baseMaxClock;
            }
            if (baseCurrentClock > _historicalPeakClockMHz)
            {
                _historicalPeakClockMHz = baseCurrentClock;
            }

            bool populatedFromPerfCounters = false;
            try
            {
                using (ManagementObjectSearcher perfSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, ProcessorFrequency, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation"))
                {
                    using (ManagementObjectCollection perfColl = perfSearcher.Get())
                    {
                        int idx = 0;
                        float sumClock = 0f;
                        foreach (ManagementObject obj in perfColl)
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString() : string.Empty;
                            if (string.IsNullOrEmpty(name) || name.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                continue;
                            }

                            float freq = obj["ProcessorFrequency"] != null ? Convert.ToSingle(obj["ProcessorFrequency"]) : baseMaxClock;
                            float perfPct = obj["PercentProcessorPerformance"] != null ? Convert.ToSingle(obj["PercentProcessorPerformance"]) : 100f;
                            if (freq <= 0f) freq = baseCurrentClock > 0f ? baseCurrentClock : baseMaxClock;
                            if (perfPct <= 0f) perfPct = 100f;

                            float effectiveMhz = (float)Math.Round(freq * (perfPct / 100f), 0);
                            if (effectiveMhz > 0f && idx < 64)
                            {
                                UpdateCoreClockEntry(idx, string.Format("Core #{0}", idx + 1), effectiveMhz);
                                if (effectiveMhz > _historicalPeakClockMHz)
                                {
                                    _historicalPeakClockMHz = effectiveMhz;
                                }
                                sumClock += effectiveMhz;
                                idx++;
                            }
                        }

                        if (idx > 0)
                        {
                            averageClockMHz = sumClock / idx;
                            populatedFromPerfCounters = true;
                        }
                    }
                }
            }
            catch { }

            if (!populatedFromPerfCounters)
            {
                float realClock = baseCurrentClock > 0f ? baseCurrentClock : (baseMaxClock > 0f ? baseMaxClock : 0f);
                float fallbackClock = realClock > 0f ? realClock : 2400f;
                if (coreCount > 32) coreCount = 32;
                for (int i = 0; i < coreCount; i++)
                {
                    UpdateCoreClockEntry(i, string.Format("Core #{0}", i + 1), fallbackClock);
                }
                averageClockMHz = realClock;
            }
        }

        /// <summary>
        /// Očitavanje temperature matične ploče preko WMI / ACPI termalnih zona.
        /// Koristi drugu termalnu zonu ukoliko postoji, ili primarnu ACPI zonu.
        /// </summary>
        private float ReadMotherboardTempWmi()
        {
            float firstZoneCelsius = 0f;
            int zoneIndex = 0;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["CurrentTemperature"] != null)
                            {
                                float raw = Convert.ToSingle(obj["CurrentTemperature"]);
                                if (raw > 0f)
                                {
                                    float celsius = (raw - 2732.0f) / 10.0f;
                                    if (celsius > 0f && celsius < 120f)
                                    {
                                        if (zoneIndex == 0)
                                        {
                                            firstZoneCelsius = celsius;
                                        }
                                        else
                                        {
                                            return celsius;
                                        }
                                        zoneIndex++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (firstZoneCelsius > 0f)
            {
                return firstZoneCelsius;
            }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["Temperature"] != null)
                            {
                                float raw = Convert.ToSingle(obj["Temperature"]);
                                if (raw > 0f)
                                {
                                    float celsius = raw > 1000f ? ((raw - 2732.0f) / 10.0f) : (raw > 200f ? (raw - 273.15f) : raw);
                                    if (celsius > 0f && celsius < 120f)
                                    {
                                        return celsius;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return 0f;
        }

        /// <summary>
        /// Očitavanje temperature procesora preko WMI / ACPI zona.
        /// Primarni: root\wmi:MSAcpi_ThermalZoneTemperature ((CurrentTemperature - 2732.0f) / 10.0f)
        /// Sekundarni: root\cimv2:Win32_PerfFormattedData_Counters_ThermalZoneInformation / Win32_PerfRawData_Counters_ThermalZoneInformation
        /// Tercijarni: root\cimv2:Win32_TemperatureProbe i OEM WMI (Lenovo_SensorData / HP_ThermalSensor)
        /// </summary>
        private float ReadCpuTempWmi()
        {
            // 1. Pokušaj preko root\wmi: MSAcpi_ThermalZoneTemperature
            try
            {
                float bestAcpi = 0f;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["CurrentTemperature"] != null)
                            {
                                float celsius = NormalizeWmiTemperatureValue(Convert.ToSingle(obj["CurrentTemperature"]));
                                if (celsius > bestAcpi) bestAcpi = celsius;
                            }
                        }
                    }
                }
                if (bestAcpi > 0f) return bestAcpi;
            }
            catch { }

            // 2. Fallback preko root\cimv2: ThermalZoneInformation (Formatted pa Raw)
            float tzTemp;
            if (TryReadWmiThermalZoneClass("Win32_PerfFormattedData_Counters_ThermalZoneInformation", out tzTemp))
            {
                return tzTemp;
            }
            if (TryReadWmiThermalZoneClass("Win32_PerfRawData_Counters_ThermalZoneInformation", out tzTemp))
            {
                return tzTemp;
            }

            // 3. Dodatni fallback preko root\cimv2: Win32_TemperatureProbe
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT CurrentReading FROM Win32_TemperatureProbe"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["CurrentReading"] != null)
                            {
                                float celsius = NormalizeWmiTemperatureValue(Convert.ToSingle(obj["CurrentReading"]));
                                if (celsius > 0f) return celsius;
                            }
                        }
                    }
                }
            }
            catch { }

            // 4. OEM WMI fallback (Lenovo_SensorData / HP_ThermalSensor u root\WMI)
            float oemTemp;
            if (TryReadOemWmiTemp(@"root\wmi", "Lenovo_SensorData", new string[] { "CurrentTemperature", "Temperature", "ThermalData", "Value" }, out oemTemp))
            {
                return oemTemp;
            }
            if (TryReadOemWmiTemp(@"root\wmi", "HP_ThermalSensor", new string[] { "CurrentReading", "Temperature", "Value" }, out oemTemp))
            {
                return oemTemp;
            }

            return 0f;
        }

        private static float NormalizeWmiTemperatureValue(float raw)
        {
            if (raw <= 0f) return 0f;
            float celsius;
            if (raw > 2000f)
            {
                // Desetinke Kelvina (npr. 3182 = 45.0 C)
                celsius = (raw - 2732.0f) / 10.0f;
            }
            else if (raw > 200f && raw < 450f)
            {
                // Kelvini (npr. 318 = 44.85 C)
                celsius = raw - 273.15f;
            }
            else
            {
                celsius = raw;
            }
            return (celsius > 5f && celsius < 125f) ? celsius : 0f;
        }

        private static bool TryReadWmiThermalZoneClass(string className, out float bestCelsius)
        {
            bestCelsius = 0f;
            try
            {
                string query = "SELECT Temperature, HighPrecisionTemperature FROM " + className;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", query))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            object hpObj = null;
                            try { hpObj = obj["HighPrecisionTemperature"]; } catch { }
                            if (hpObj != null)
                            {
                                float hpCelsius = NormalizeWmiTemperatureValue(Convert.ToSingle(hpObj));
                                if (hpCelsius > bestCelsius) bestCelsius = hpCelsius;
                            }

                            object tempObj = null;
                            try { tempObj = obj["Temperature"]; } catch { }
                            if (tempObj != null)
                            {
                                float tCelsius = NormalizeWmiTemperatureValue(Convert.ToSingle(tempObj));
                                if (tCelsius > bestCelsius) bestCelsius = tCelsius;
                            }
                        }
                    }
                }
            }
            catch { }
            return bestCelsius > 0f;
        }

        private static bool TryReadOemWmiTemp(string scopePath, string className, string[] candidateProps, out float bestCelsius)
        {
            bestCelsius = 0f;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scopePath, "SELECT * FROM " + className))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            for (int i = 0; i < candidateProps.Length; i++)
                            {
                                object val = null;
                                try { val = obj[candidateProps[i]]; } catch { }
                                if (val != null)
                                {
                                    float c;
                                    try
                                    {
                                        c = NormalizeWmiTemperatureValue(Convert.ToSingle(val));
                                    }
                                    catch { c = 0f; }
                                    if (c > bestCelsius) bestCelsius = c;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return bestCelsius > 0f;
        }

        /// <summary>
        /// Očitavanje temperature grafičke kartice preko WMI (fallback).
        /// Standardni Windows WMI nema standardizovan senzor za GPU bez specijalizovanih OEM provajdera.
        /// </summary>
        private float ReadGpuTempWmi()
        {
            return 0f;
        }

        /// <summary>
        /// Očitavanje temperature SSD/HDD diska preko SMART podataka iz WMI.
        /// Primarni: root\wmi:MSStorageDriver_ATAPISmartData (VendorSpecific SMART tabela, ID 194 / 190)
        /// Sekundarni: root\Microsoft\Windows\Storage:MSFT_PhysicalDisk (Temperature)
        /// </summary>
        private float ReadSsdTempWmi()
        {
            // 1. root\wmi: MSStorageDriver_ATAPISmartData
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT VendorSpecific FROM MSStorageDriver_ATAPISmartData"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            byte[] data = obj["VendorSpecific"] as byte[];
                            if (data != null && data.Length >= 512)
                            {
                                // SMART atributi počinju na ofsetu 2, svaki atribut ima 12 bajtova
                                for (int i = 2; i + 12 <= data.Length; i += 12)
                                {
                                    byte attrId = data[i];
                                    // 194 = Temperature Celsius, 190 = Airflow Temperature
                                    if (attrId == 194 || attrId == 190)
                                    {
                                        byte temp = data[i + 5]; // Bajt 5 sadrži sirovu vrednost trenutne temperature
                                        if (temp > 0 && temp < 120)
                                        {
                                            return (float)temp;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback za Windows 10/11 NVMe/Storage Spaces: root\Microsoft\Windows\Storage: MSFT_PhysicalDisk
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT Temperature FROM MSFT_PhysicalDisk"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject obj in coll)
                        {
                            if (obj["Temperature"] != null)
                            {
                                float temp = Convert.ToSingle(obj["Temperature"]);
                                if (temp > 0f && temp < 120f)
                                {
                                    return temp;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return 0f;
        }

        /// <summary>
        /// Očitavanje brzine ventilatora preko WMI (root\cimv2: Win32_Fan).
        /// Vraća 0 i hasFanData = false ako platforma ne podržava WMI očitavanje ventilatora.
        /// </summary>
        private void ReadFanWmi(out int fanRpm, out bool hasFanData)
        {
            fanRpm = 0;
            hasFanData = false;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DesiredSpeed FROM Win32_Fan"))
                {
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        if (coll != null && coll.Count > 0)
                        {
                            foreach (ManagementObject obj in coll)
                            {
                                hasFanData = true;
                                if (obj["DesiredSpeed"] != null)
                                {
                                    int speed = Convert.ToInt32(obj["DesiredSpeed"]);
                                    if (speed > fanRpm)
                                    {
                                        fanRpm = speed;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            if (disposing)
            {
                lock (_syncLock)
                {
                    if (_computer != null)
                    {
                        try
                        {
                            if (_miComputerClose != null) _miComputerClose.Invoke(_computer, null);
                        }
                        catch { }
                        _computer = null;
                    }
                }
            }

            _isDisposed = true;
        }
    }
}
