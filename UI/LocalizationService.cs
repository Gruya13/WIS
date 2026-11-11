using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace WIS.UI
{
    /// <summary>
    /// Servis za dvojezičnu podršku (Srpski / Kineski) u letu (Runtime Switch).
    /// Striktno C# 5.0 kompatibilan (.NET Framework 4.0 / 4.8 MSBuild).
    /// </summary>
    public class LocalizationService : INotifyPropertyChanged
    {
        private static LocalizationService _instance;
        private static readonly object _syncLock = new object();

        private string _currentLanguage;
        private readonly Dictionary<string, string> _srDict;
        private readonly Dictionary<string, string> _enDict;
        private readonly Dictionary<string, string> _zhDict;

        public static LocalizationService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_syncLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new LocalizationService();
                        }
                    }
                }
                return _instance;
            }
        }

        private LocalizationService()
        {
            _srDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _enDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _zhDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            InitializeDictionaries();
            _currentLanguage = LoadSavedLanguage();
            ApplyLanguageToResources(_currentLanguage);
        }

        public string CurrentLanguage
        {
            get { return _currentLanguage; }
            set { SetLanguage(value); }
        }

        public bool IsSerbian
        {
            get { return string.Equals(_currentLanguage, "sr", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsEnglish
        {
            get { return string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsChinese
        {
            get { return string.Equals(_currentLanguage, "zh", StringComparison.OrdinalIgnoreCase); }
        }

        public string this[string key]
        {
            get { return GetString(key); }
        }

        public string GetString(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            if (IsChinese)
            {
                string zhVal;
                if (_zhDict.TryGetValue(key, out zhVal))
                {
                    return zhVal;
                }
            }
            else if (IsEnglish)
            {
                string enVal;
                if (_enDict.TryGetValue(key, out enVal))
                {
                    return enVal;
                }
            }

            string srVal;
            if (_srDict.TryGetValue(key, out srVal))
            {
                return srVal;
            }

            return key;
        }

        public string GetString(string key, string fallback)
        {
            string val = GetString(key);
            return string.IsNullOrEmpty(val) || val == key ? fallback : val;
        }

        public static string Get(string key, string fallback = "")
        {
            if (Instance == null) return fallback;
            string val = Instance.GetString(key);
            return string.IsNullOrEmpty(val) || val == key ? fallback : val;
        }

        public static event Action LanguageChanged;

        public void SetLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode)) langCode = "sr";

            string normalized = langCode.Trim().ToLowerInvariant();
            if (normalized == "zh" || normalized == "zh-cn" || normalized == "chinese")
            {
                normalized = "zh";
            }
            else if (normalized == "en" || normalized == "en-us" || normalized == "english")
            {
                normalized = "en";
            }
            else
            {
                normalized = "sr";
            }

            if (_currentLanguage != normalized)
            {
                _currentLanguage = normalized;
                SaveLanguage(normalized);
                ApplyLanguageToResources(normalized);

                OnPropertyChanged(string.Empty);
                OnPropertyChanged("Item[]");
                OnPropertyChanged("CurrentLanguage");
                OnPropertyChanged("IsSerbian");
                OnPropertyChanged("IsEnglish");
                OnPropertyChanged("IsChinese");

                if (LanguageChanged != null)
                {
                    try { LanguageChanged(); } catch { }
                }
            }
        }

        private void ApplyLanguageToResources(string lang)
        {
            try
            {
                Application app = Application.Current;
                if (app != null && app.Resources != null)
                {
                    Dictionary<string, string> dict = (lang == "zh") ? _zhDict : ((lang == "en") ? _enDict : _srDict);
                    foreach (KeyValuePair<string, string> entry in dict)
                    {
                        app.Resources["Loc_" + entry.Key] = entry.Value;
                    }
                }
            }
            catch { }
        }

        private static string LoadSavedLanguage()
        {
            // 1. Provera konfiguracionog fajla C:\setup\wis-config.json
            try
            {
                string cfgPath = @"C:\setup\wis-config.json";
                if (File.Exists(cfgPath))
                {
                    string json = File.ReadAllText(cfgPath, Encoding.UTF8);
                    Match m = Regex.Match(json, @"""language""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
                    {
                        string val = m.Groups[1].Value.Trim().ToLowerInvariant();
                        if (val == "zh" || val == "zh-cn" || val == "chinese") return "zh";
                        return "sr";
                    }
                }
            }
            catch { }

            // 2. Provera Registra (HKLM pa HKCU)
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WIS"))
                {
                    if (key != null)
                    {
                        object obj = key.GetValue("Language");
                        if (obj != null)
                        {
                            string val = obj.ToString().Trim().ToLowerInvariant();
                            if (val == "zh" || val == "zh-cn" || val == "chinese") return "zh";
                            return "sr";
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\WIS"))
                {
                    if (key != null)
                    {
                        object obj = key.GetValue("Language");
                        if (obj != null)
                        {
                            string val = obj.ToString().Trim().ToLowerInvariant();
                            if (val == "zh" || val == "zh-cn" || val == "chinese") return "zh";
                            return "sr";
                        }
                    }
                }
            }
            catch { }

            return "sr";
        }

        private static void SaveLanguage(string lang)
        {
            // 1. Snimanje u C:\setup\wis-config.json
            try
            {
                string dir = @"C:\setup";
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string cfgPath = Path.Combine(dir, "wis-config.json");
                string json = string.Format("{{\r\n  \"language\": \"{0}\",\r\n  \"updated_at\": \"{1}\"\r\n}}",
                    lang, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                File.WriteAllText(cfgPath, json, Encoding.UTF8);
            }
            catch { }

            // 2. Snimanje u Registar
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WIS"))
                {
                    if (key != null)
                    {
                        key.SetValue("Language", lang);
                    }
                }
            }
            catch
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\WIS"))
                    {
                        if (key != null)
                        {
                            key.SetValue("Language", lang);
                        }
                    }
                }
                catch { }
            }
        }

        private void InitializeDictionaries()
        {
            // ==========================================
            // SRPSKI JEZIK (SRB)
            // ==========================================
            _srDict["AppTitle"] = "WIS v2.18.0 - Windows Installation Assistant";
            _srDict["CompanyBrand"] = "WIS SERVICE";
            _srDict["WorkTimeTitle"] = "VREME RADA";
            _srDict["CpuLabel"] = "⚙ CPU:";
            _srDict["RamLabel"] = "💾 RAM:";
            _srDict["DiskLabel"] = "🖴 DISK:";

            // Tabovi
            _srDict["TabOverview"] = "📊 Pregled";
            _srDict["TabHardware"] = "💻 Informacije";
            _srDict["TabDrivers"] = "⚙ Drajveri";
            _srDict["TabDiagnostics"] = "🔍 Dijagnostika";
            _srDict["TabNetwork"] = "🌐 Mreža";
            _srDict["TabApps"] = "📦 Programi";
            _srDict["TabLogs"] = "📜 Dnevnik rada";
            _srDict["TabAutopilot"] = "🛡️ Autopilot";

            // Dugmad i kontrole
            _srDict["BtnStartService"] = "▶ ZAPOČNI INSTALACIJU";
            _srDict["BtnPause"] = "⏸ Pauza";
            _srDict["BtnResume"] = "▶ Nastavi";
            _srDict["BtnSkip"] = "⏭ Preskoči";
            _srDict["BtnDelayRestart"] = "⏱ Odloži restart";
            _srDict["BtnRefreshHardware"] = "🔄 Osveži informacije";
            _srDict["BtnRerunApps"] = "🔄 Ponovi instalaciju programa";
            _srDict["BtnRunStressTest"] = "🔥 Pokreni 30s termalni test";
            _srDict["BtnCancelStressTest"] = "⏹ Prekini test";
            _srDict["BtnCleanAndShutdown"] = "🔴 OČISTI I UGASI RAČUNAR";
            _srDict["BtnCompletionSummary"] = "📋 Završni karton";
            _srDict["BtnExitWithoutClean"] = "✕ Izađi bez čišćenja";
            _srDict["BtnStayInApp"] = "📋 OSTANI U PROGRAMU";
            _srDict["TooltipStayInApp"] = "Zatvara ovaj prozor i omogućava detaljan pregled tabova, hardvera i logova. Dugme za gašenje i čišćenje ostaje u donjoj traci.";

            // Jezičke oznake
            _srDict["LangSrb"] = "🇷🇸 SRB";
            _srDict["LangZh"] = "🇨🇳 中文";
            _srDict["LanguageSelectTooltipSrb"] = "Prebaci interfejs na srpski jezik";
            _srDict["LanguageSelectTooltipZh"] = "切换界面为简体中文";

            // TAB 1: Pregled (Overview)
            _srDict["ServiceReadyBadge"] = "⚡ SERVIS SPREMAN";
            _srDict["ServiceReadyDesc"] = "Računar je spreman za početak instalacije operativnog sistema i programa.";
            _srDict["ComputerModelLabel"] = "Model računara:";
            _srDict["SerialNumberLabel"] = "Serijski broj:";
            _srDict["ProcessorLabel"] = "Procesor (CPU):";
            _srDict["MemoryLabel"] = "Radna memorija (RAM):";
            _srDict["DiskHealthLabel"] = "Zdravstveni status diska:";
            _srDict["SmartStatusLabel"] = "SMART Zdravlje:";
            _srDict["AutopilotStatusLabel"] = "Autopilot Status:";
            _srDict["InstallationFlowTitle"] = "TOK INSTALACIJE I FAZE";
            _srDict["UsbFreeTitle"] = "USB FLEŠ DISK JE SLOBODAN!";
            _srDict["UsbFreeDesc"] = "Instalacioni USB disk je bezbedno odvojen – možete ga slobodno izvaditi iz računara i upotrebiti na sledećoj mašini.";
            _srDict["RecentActivityTitle"] = "POSLEDNJE ZABELEŽENE AKTIVNOSTI";
            _srDict["Phase1Title"] = "1. Mreža";
            _srDict["Phase1Subtitle"] = "Wi-Fi i ruting";
            _srDict["Phase2Title"] = "2. Update";
            _srDict["Phase2Subtitle"] = "Drajveri i zakrpe";
            _srDict["Phase3Title"] = "3. Optimizacija";
            _srDict["Phase3Subtitle"] = "Debloat i brendiranje";
            _srDict["Phase4Title"] = "4. Programi";
            _srDict["Phase4Subtitle"] = "Paketi i OEM licenca";
            _srDict["Phase5Title"] = "5. Stres";
            _srDict["Phase5Subtitle"] = "Stabilnost (30s)";
            _srDict["Phase6Title"] = "6. Završetak";
            _srDict["Phase6Subtitle"] = "Dijagnostika i rezime";
            _srDict["PhaseStatusPending"] = "Na čekanju";
            _srDict["PhaseStatusActive"] = "U toku";
            _srDict["PhaseStatusCompleted"] = "Završeno";

            // TAB 2: Informacije o sistemu i hardveru (Hardware)
            _srDict["HardwareOverviewTitle"] = "PREGLED HARDVERSKIH KOMPONENTI";
            _srDict["HwCpuCardTitle"] = "🔲 PROCESOR (CPU)";
            _srDict["HwCpuModelLabel"] = "Model procesora:";
            _srDict["HwCpuCoresThreadsLabel"] = "Jezgra i niti:";
            _srDict["HwCpuClockLabel"] = "Radni takt:";
            _srDict["HwCpuSocketLabel"] = "Ležište (Socket):";
            _srDict["HwRamCardTitle"] = "🧠 RADNA MEMORIJA (RAM)";
            _srDict["HwRamTotalLabel"] = "Ukupno memorije:";
            _srDict["HwRamTypeLabel"] = "Tip i generacija:";
            _srDict["HwRamSpeedLabel"] = "Brzina magistrale:";
            _srDict["HwRamConfigLabel"] = "Konfiguracija modula:";
            _srDict["HwRamSlotsLabel"] = "Memorijski slotovi:";
            _srDict["HwRamSoldered"] = "Zalemljena memorija (Integrisana)";
            _srDict["HwRamSodimm"] = "Standardni moduli (SO-DIMM)";
            _srDict["HwStorageCardTitle"] = "💾 SKLADIŠTE & SMART (SSD)";
            _srDict["HwStoragePrimaryLabel"] = "Primarni disk:";
            _srDict["HwStorageSmartLabel"] = "SMART Zdravlje:";
            _srDict["HwStorageHoursLabel"] = "Upis / Radni sati:";
            _srDict["HwStorageTempLabel"] = "Temperatura diska:";
            _srDict["HwStorageCapacityLabel"] = "Kapacitet diska:";
            _srDict["HwStorageMediaTypeLabel"] = "Tip medijuma:";
            _srDict["HwStorageInterfaceLabel"] = "Interfejs diska:";
            _srDict["HwBatteryCardTitle"] = "🔋 BATERIJA & NAPAJANJE";
            _srDict["HwBatteryWearLabel"] = "Istrošenost (Wear Level):";
            _srDict["HwBatteryCapacityLabel"] = "Kapacitet (Trenutni / Dizajn):";
            _srDict["HwBatteryCyclesLabel"] = "Ciklusi punjenja:";
            _srDict["HwBatterySourceLabel"] = "Izvor napajanja:";
            _srDict["HwBatteryHealthLabel"] = "Zdravlje baterije:";
            _srDict["HwBatteryDesignedCapLabel"] = "Dizajnirani kapacitet:";
            _srDict["HwBatteryFullCapLabel"] = "Puni kapacitet:";
            _srDict["HwGpuCardTitle"] = "🎮 GRAFIKA & DISPLEJ";
            _srDict["HwGpuPrimaryLabel"] = "Primarna grafika:";
            _srDict["HwGpuPrimaryVramLabel"] = "VRAM memorija:";
            _srDict["HwGpuSecondaryLabel"] = "Sekundarna grafika:";
            _srDict["HwGpuSecondaryVramLabel"] = "Sekundarni VRAM:";
            _srDict["HwDisplaySpecLabel"] = "Rezolucija i osvežavanje:";
            _srDict["HwTouchscreenLabel"] = "Ekran na dodir (Touch):";
            _srDict["HwMotherboardCardTitle"] = "🖥️ MATIČNA PLOČA & BIOS";
            _srDict["HwMbModelLabel"] = "Model i proizvođač:";
            _srDict["HwMbManufacturerLabel"] = "Proizvođač ploče:";
            _srDict["HwSerialNumberLabel"] = "Serijski broj uređaja:";
            _srDict["HwBiosVersionDateLabel"] = "BIOS Verzija i datum:";
            _srDict["HwSecureBootLabel"] = "SecureBoot status:";
            _srDict["HwChassisTypeLabel"] = "Tip šasije (Chassis):";
            _srDict["HwPeripheralsCardTitle"] = "📷 PERIFERIJE & BIOMETRIJA (Windows Hello)";
            _srDict["HwWebcamLabel"] = "Veb kamera (Webcam):";
            _srDict["HwIrCameraLabel"] = "IR kamera (Windows Hello):";
            _srDict["HwBacklitKeyboardLabel"] = "Svetleća tastatura:";
            _srDict["HwFingerprintLabel"] = "Čitač otiska prsta:";

            // TAB 3: Drajveri i uređaji (Drivers)
            _srDict["DriverInterventionRequired"] = "ZAHTEVA INTERVENCIJU";
            _srDict["DriverClassLabel"] = "Klasa:";
            _srDict["DriverManufacturerLabel"] = "Proizvođač:";
            _srDict["BtnCopyHwid"] = "📋 Kopiraj HWID";
            _srDict["TooltipCopyHwid"] = "Kopira puni Hardware ID u Windows Clipboard";
            _srDict["BtnInstallOfficialDriver"] = "⬇ Instaliraj zvanični drajver";
            _srDict["TooltipInstallDriver"] = "Automatski preuzima i instalira zvanični OEM drajver sa ServisHub / Microsoft kataloga";
            _srDict["DriverInstalledSuccess"] = "✓ Drajver uspešno instaliran!";
            _srDict["DriverAllGood"] = "Svi uređaji u Upravljaču uređaja funkcionišu ispravno.";
            _srDict["DriverFilterPlaceholder"] = "Filtriraj po nazivu, klasi, proizvođaču ili HWID...";
            _srDict["BtnCheckKeyDrivers"] = "🔍 Proveri ključne drajvere";
            _srDict["TooltipCheckKeyDrivers"] = "Proverava dostupnost novijih zvaničnih OEM drajvera za GPU, mrežu, zvuk i čipset preko ServisHub-a";
            _srDict["BtnResolveAllDrivers"] = "⚡ Reši sve nedostajuće drajvere";
            _srDict["TooltipResolveAllDrivers"] = "Automatski pokreće instalaciju zvaničnih drajvera za sve problematične uređaje";
            _srDict["BtnRefreshDriverTree"] = "🔄 Osveži stablo";
            _srDict["TooltipRefreshDriverTree"] = "Ponovo očitava sve PnP uređaje i drajvere sa sistema";
            _srDict["BtnOpenDeviceManager"] = "🛠️ Otvori Device Manager";
            _srDict["TooltipOpenDeviceManager"] = "Otvara nativni Windows Device Manager (devmgmt.msc)";
            _srDict["DriversInstalledByCategoryTitle"] = "INSTALIRANI UREĐAJI I DRAJVERI PO KATEGORIJAMA";
            _srDict["DriversTotalDetected"] = "Ukupno detektovanih uređaja:";
            _srDict["DeviceUnitCount"] = "uređaja";
            _srDict["DriverVersionLabel"] = "Verzija drajvera:";
            _srDict["DriverDateLabel"] = "Datum:";
            _srDict["BtnDriverCheck"] = "🔍 Proveri";
            _srDict["DriverCheckingStatus"] = "🔍 Provera...";
            _srDict["DriverNewVersionAvailable"] = "Dostupna nova verzija:";
            _srDict["BtnDriverUpgrade"] = "⬇ Nadogradi";
            _srDict["DriverUpToDate"] = "✓ Najnoviji drajver";
            _srDict["BtnHwidShort"] = "📋 HWID";
            _srDict["DriverInstalling"] = "Instalacija u toku...";
            _srDict["DriverCatNetwork"] = "Mreža i Wi-Fi";
            _srDict["DriverCatGraphics"] = "Grafika i čipset";
            _srDict["DriverCatAudio"] = "Zvuk i audio";
            _srDict["DriverCatStorage"] = "Skladište i kontroleri";
            _srDict["DriverCatBluetooth"] = "Bluetooth uređaji";
            _srDict["DriverCatBiometrics"] = "Biometrija i kamere";
            _srDict["DriverCatSystem"] = "Sistemski uređaji";
            _srDict["DriverCatOther"] = "Ostali uređaji";
            _srDict["DriverSearchCatalog"] = "Pretraži katalog";

            // TAB 4: Dijagnostika i senzori (Diagnostics)
            _srDict["DiagGraphTitle"] = "📈 5-MINUTNI GRAFIKON TEMPERATURA I STABILNOSTI (1 Hz)";
            _srDict["BtnStartStress"] = "▶ Start Stres";
            _srDict["BtnStopStress"] = "⏹ Stop Stres";
            _srDict["BtnResetMinMax"] = "↺ Reset Min/Max";
            _srDict["CoreClocksTitle"] = "⚡ TAKTOVI PROCESORSKIH JEZGARA (CORE CLOCKS)";
            _srDict["ColCoreName"] = "Jezgro";
            _srDict["ColCurrentClock"] = "Trenutni Takt";
            _srDict["ColMinClock"] = "Min Takt";
            _srDict["ColMaxClock"] = "Max Takt";
            _srDict["ColClockRatio"] = "Opterećenje / Odnos";
            _srDict["DiagSmartTitle"] = "🖴 S.M.A.R.T. & SSD ZDRAVLJE";
            _srDict["DiagWearPercentLabel"] = "Preostalo zdravlje (Wear %):";
            _srDict["DiagSmartStatusLabel"] = "SMART Status:";
            _srDict["DiagTotalTbwLabel"] = "Ukupno upisano (TBW):";
            _srDict["DiagReallocatedSectorsLabel"] = "Realocirani sektori:";
            _srDict["DiagCriticalWarningsLabel"] = "Kritična upozorenja:";
            _srDict["DiagBatteryTitle"] = "🔋 BATERIJA & NAPAJANJE";
            _srDict["DiagBatteryHealthLabel"] = "Zdravlje baterije:";
            _srDict["DiagBatteryCyclesLabel"] = "Ciklusi punjenja:";
            _srDict["DiagBatteryPowerStatusLabel"] = "Status napajanja:";
            _srDict["DiagDeviceTypeLabel"] = "Tip uređaja:";
            _srDict["DiagCpuRamTitle"] = "⚡ PROCESOR I RADNA MEMORIJA";
            _srDict["DiagCpuLabel"] = "Procesor (CPU):";
            _srDict["DiagCpuUsageLabel"] = "Opterećenje CPU:";
            _srDict["DiagRamLabel"] = "Radna memorija (RAM):";
            _srDict["DiagRamUsageLabel"] = "Zauzeće RAM-a:";
            _srDict["DiagGpuDisplayTitle"] = "🎮 GRAFIČKA KARTA & DISPLEJ";
            _srDict["DiagGpuLabel"] = "Grafička karta (GPU):";
            _srDict["DiagVramLabel"] = "Video memorija (VRAM):";
            _srDict["DiagDisplayResLabel"] = "Ekran i Rezolucija:";
            _srDict["DiagRefreshDpiLabel"] = "Frekvencija i DPI:";
            _srDict["DiagDeviceManagerTitle"] = "🛠 UPRAVLJAČ UREĐAJA (PNP)";
            _srDict["DiagAllDevicesOkText"] = "Svi hardverski moduli funkcionišu bez prijavljenih grešaka u Windows Device Manager-u.";
            _srDict["DiagMotherboardBiosTpmTitle"] = "🖧 MATIČNA PLOČA, BIOS & TPM";
            _srDict["DiagMotherboardLabel"] = "Matična ploča:";
            _srDict["DiagBiosVersionLabel"] = "BIOS Verzija:";
            _srDict["DiagBiosDateLabel"] = "Datum izdanja BIOS-a:";
            _srDict["DiagTpmChipLabel"] = "TPM Sigurnosni Čip:";
            _srDict["DiagSecureBootLabel"] = "SecureBoot Status:";
            _srDict["ThermalStatusTitle"] = "🌡️ TERMALNI STATUS & SENZORI";
            _srDict["ThermalCurrentTempLabel"] = "Trenutna temperatura:";
            _srDict["ThermalMaxRecordedLabel"] = "Maksimalna zabeležena:";
            _srDict["ThermalCpuLoadLabel"] = "Opterećenje procesora:";
            _srDict["ThermalCpuTempLabel"] = "Temperatura procesora (CPU):";
            _srDict["ThermalGpuTempLabel"] = "Grafička karta (GPU):";
            _srDict["ThermalSsdTempLabel"] = "Skladište (SSD / NVMe):";
            _srDict["ThermalFanSpeedLabel"] = "Brzina ventilatora (Fan):";
            _srDict["ThermalStressTitle"] = "🔥 TERMALNI STRES TEST (30 SEKUNDI)";
            _srDict["TooltipRunStressTest"] = "Pokreće intenzivni multi-core stres test uz automatski Emergency Abort na 98°C";
            _srDict["TooltipCancelStressTest"] = "Prekida trenutni termalni stres test";
            _srDict["ThermalThresholdNormal"] = "Normalno";
            _srDict["ThermalThresholdElevated"] = "Povišeno";
            _srDict["ThermalThresholdOverheating"] = "Pregrevanje";

            // TAB 5: Mreža i povezivanje (Network)
            _srDict["NetNetworkAdaptersTitle"] = "Mrežni adapteri i povezivanje";
            _srDict["NetAdapterNameLabel"] = "Naziv adaptera:";
            _srDict["NetConnectionStatusConnected"] = "Povezano";
            _srDict["NetConnectionStatusDisconnected"] = "Nije povezano";
            _srDict["NetSubnetMaskLabel"] = "Podmrežna maska:";
            _srDict["NetGatewayLabel"] = "Podrazumevani prolaz (Gateway):";
            _srDict["NetDnsServersLabel"] = "DNS serveri:";
            _srDict["NetMacAddressLabel"] = "MAC adresa:";
            _srDict["NetLinkSpeedLabel"] = "Brzina linka:";
            _srDict["NetWifiTitle"] = "📶 WI-FI VEZA";
            _srDict["NetCurrentSsidLabel"] = "Trenutni SSID:";
            _srDict["NetConnectionStatusLabel"] = "Status povezivanja:";
            _srDict["NetServiceWifiAutoText"] = "Servisna Wi-Fi automatizacija";
            _srDict["NetIpConfigTitle"] = "🖥 IP KONFIGURACIJA";
            _srDict["NetIpAddressLabel"] = "IP Adresa:";
            _srDict["NetAdapterTypeLabel"] = "Tip Adaptera:";
            _srDict["NetNasServerTitle"] = "🗄 SERVISNI NAS SERVER";
            _srDict["NetNasConnectionStatusLabel"] = "Status veze ka NAS-u:";
            _srDict["NetNasPingLatencyLabel"] = "Ping latencija ka kešu:";
            _srDict["NetTrafficDownloadTitle"] = "📥 PROTOK I PREUZETI PAKETI";
            _srDict["NetTotalDownloadedPackagesLabel"] = "Ukupno preuzeto paketa:";
            _srDict["NetDistributionSourceLabel"] = "Izvor distribucije:";
            _srDict["NetDistributionSourceValue"] = "Lokalni servisni NAS repozitorijum";

            // TAB 6: Programi i aplikacije (Apps)
            _srDict["StandardAppsTitle"] = "STANDARDNI SERVISNI PAKETI (FAZA 4)";
            _srDict["AppsInstallStatusTitle"] = "Status instalacije programa";
            _srDict["AppsRerunInstallBtn"] = "Ponovi instalaciju";
            _srDict["AppStatusWait"] = "Čeka redosled";
            _srDict["AppStatusInstalling"] = "Instalira se...";
            _srDict["AppStatusSuccess"] = "Uspešno instalirano";
            _srDict["AppStatusError"] = "Greška";
            _srDict["AppStatusNotNeeded"] = "Nije potrebno";
            _srDict["AppDetailWait"] = "Čeka početak faze 4";
            _srDict["AppDetailRunning"] = "Instalacija paketa u toku...";
            _srDict["AppDetailSuccess"] = "Instalacija uspešno verifikovana";
            _srDict["AppDetailError"] = "Greška tokom instalacije";
            _srDict["AppBadgeWait"] = "⏳ ČEKA";
            _srDict["AppBadgeRunning"] = "⚙ RAD";
            _srDict["AppBadgeSuccess"] = "✓ OK";
            _srDict["AppBadgeError"] = "✗ GRESKA";
            _srDict["AppBadgeSkipped"] = "⏭ PRESKOČENO";

            // TAB 7: Servisni dnevnik (Logs)
            _srDict["LogServiceJournalTitle"] = "Servisni dnevnik rada";
            _srDict["LogBufferTitle"] = "DETALJAN RING-BUFFER LOG (MAX 500 LINIJA)";
            _srDict["LogAutoScrollCheck"] = "Prati najnovije (vrh)";
            _srDict["LogSearchLabel"] = "Pretraga dnevnika:";
            _srDict["LogCopyAllBtn"] = "📋 Kopiraj sve";
            _srDict["LogClearBtn"] = "🗑 Očisti prikaz";
            _srDict["LogFilterLevelLabel"] = "Nivo filtriranja:";
            _srDict["LogFilterAll"] = "Sve";
            _srDict["LogFilterInfo"] = "Info";
            _srDict["LogFilterWarning"] = "Upozorenje";
            _srDict["LogFilterError"] = "Greška";
            _srDict["LogColTime"] = "Vreme";
            _srDict["LogColLevel"] = "Nivo";
            _srDict["LogColTag"] = "Tag";
            _srDict["LogColMessage"] = "Poruka";

            // TAB 8: Autopilot (Autopilot)
            _srDict["ApSystemInspectionTitle"] = "Autopilot inspekcija sistema";
            _srDict["ApBatteryHealthWearTitle"] = "Zdravlje baterije i habanje";
            _srDict["ApDesignedCapacityLabel"] = "Dizajnirani kapacitet:";
            _srDict["ApFullCapacityLabel"] = "Puni kapacitet:";
            _srDict["ApWearLevelLabel"] = "Nivo habanja:";
            _srDict["ApCycleCountLabel"] = "Broj ciklusa:";
            _srDict["ApPowerStatusLabel"] = "Status napajanja:";
            _srDict["ApTpm2SecurityTitle"] = "TPM 2.0 bezbednost";
            _srDict["ApSecureBootStatusTitle"] = "Secure Boot status";
            _srDict["ApUefiCsmModeTitle"] = "UEFI / CSM režim";
            _srDict["BtnRescanAutopilot"] = "🔄 Osveži Proveru";
            _srDict["AutopilotUpdatedFormat"] = "Ažurirano:";
            _srDict["ApHardwareFingerprintTitle"] = "🖥 HARDVERSKI OTISAK";
            _srDict["ApManufacturerLabel"] = "Proizvođač:";
            _srDict["ApDeviceModelLabel"] = "Model uređaja:";
            _srDict["ApSerialNumberLabel"] = "Serijski broj:";
            _srDict["ApSmbiosUuidLabel"] = "SMBIOS UUID:";
            _srDict["ApTpmStatusLabel"] = "TPM 2.0 Status:";
            _srDict["ApLocalForensicsTitle"] = "🔍 LOKALNA FORENZIKA";
            _srDict["ApRegistryTracesLabel"] = "Tragovi u registru:";
            _srDict["ApTenantDomainLabel"] = "Tenant domen:";
            _srDict["ApTenantGuidLabel"] = "Tenant GUID:";
            _srDict["ApAssignedNameLabel"] = "Dodeljeno ime:";
            _srDict["ApEntraIdStatusLabel"] = "Entra ID (Azure AD):";
            _srDict["ApIntuneMdmLabel"] = "Intune MDM upis:";
            _srDict["ApOrgCertsLabel"] = "Org. sertifikati:";
            _srDict["ApZtdCloudTitle"] = "☁ ZTD MICROSOFT OBLAK";
            _srDict["ApOnlineZtdQueryLabel"] = "Online ZTD upit:";
            _srDict["ApCloudStatusLabel"] = "Status u oblaku:";
            _srDict["ApServerResponseCodeLabel"] = "Server kod odgovora:";
            _srDict["ApRawResponseLabel"] = "Sirov odgovor (Raw):";
            _srDict["ApZtdDescText"] = "Microsoft ZTD servis (ztd.dds.microsoft.com) direktno verifikuje hardverski hash uređaja bez potrebe za administratorskim nalogom.";

            // Završni ekran i rekapitulacija
            _srDict["TotalServiceDuration"] = "Ukupno trajanje servisa:";
            _srDict["InstalledAppsLabel"] = "Instalirane aplikacije:";
            _srDict["BatteryPowerStatusLabel"] = "Status baterije / napajanja:";
            _srDict["ServiceFinishedTitle"] = "✓ SERVIS USPEŠNO ZAVRŠEN";
            _srDict["ServiceFinishedDesc"] = "Računar je u potpunosti pripremljen i optimizovan.";
            _srDict["SelectFinalActionDesc"] = "Izaberite željeni postupak za završetak servisa:";
            _srDict["CompletionDefaultTitle"] = "SERVIS ZAVRŠEN - RAČUNAR JE SPREMAN ZA KLIJENTA";
            _srDict["CompletionDefaultSummary"] = "Sve komponente, zakrpe i aplikacije su uspešno instalirane i verifikovane.";

            // Reality Check i fazni statusi
            _srDict["RealityCheckMissingApps"] = "Nedostaju ključni programi na disku";
            _srDict["RealityCheckBadDevices"] = "Detektovani uređaji sa greškom u Upravljaču uređaja";
            _srDict["RealityCheckSuccessApps"] = "Sve ključne aplikacije (Chrome, Acrobat, Office, WinRAR) su verifikovane.";
            _srDict["RealityCheckSuccessDrivers"] = "Svi uređaji u Upravljaču uređaja su ispravno prepoznati.";

            // Hardver i specifikacije ekrana (v2.14.0 - HW-03 / HW-07)
            _srDict["HwDisplayModelLabel"] = "Model ekrana / panela:";
            _srDict["HwDisplayBrightnessLabel"] = "Osvetljenje (Nits):";
            _srDict["HwDisplayColorGamutLabel"] = "Raspon boja (Gamut):";
            _srDict["HwSpeakersLabel"] = "Zvučnici (Audio izlaz):";
            _srDict["HwMicrophoneLabel"] = "Mikrofon (Audio ulaz):";

            // Dinamički periferni statusi (v2.14.0 - HW-04..HW-07 / I18N-01)
            _srDict["HwIrYes"] = "👁️ Da (Windows Hello Face)";
            _srDict["HwIrNo"] = "Ne (Standardna RGB kamera)";
            _srDict["HwFpYes"] = "👆 Da ({0})";
            _srDict["HwFpNo"] = "Nije detektovan";
            _srDict["HwKbYes"] = "💡 Da (Pozadinsko osvetljenje)";
            _srDict["HwKbNo"] = "Standardna (Bez osvetljenja)";
            _srDict["HwKbCheckManual"] = "Standardna / Proveri ručno (Fn+Space)";
            _srDict["HwTouchYes"] = "🖐️ Podržan ({0} tačaka dodira)";
            _srDict["HwTouchYesSimple"] = "🖐️ Podržan (Touchscreen)";
            _srDict["HwTouchNo"] = "Nije detektovan (Standardni displej)";
            _srDict["HwCamNo"] = "Nije ugradjena";
            _srDict["HwCamDetecting"] = "Detekcija kamere...";
            _srDict["HwLhmActive"] = "● LibreHardwareMonitor aktivan";
            _srDict["HwWmiFallback"] = "● WMI/ACPI Fallback režim";
            _srDict["HwAudioSpeakersDefault"] = "Standardni Audio uređaj";
            _srDict["HwMicYes"] = "🎤 Da ({0})";
            _srDict["HwMicNo"] = "Nije detektovan";
            _srDict["BtnPauseService"] = "⏸ PAUZIRAJ";
            _srDict["BtnResumeService"] = "▶ NASTAVI";

            // Autopilot Dinamički Matrix Statusi (v2.14.0 - I18N-01)
            _srDict["ApMatrixTitle_Scanning"] = "PROVERA AUTOPILOTA U TOKU...";
            _srDict["ApMatrixDesc_Scanning"] = "Sistem vrši duboku forenziku lokalnog registra i proveru Microsoft ZTD oblaka...";
            _srDict["ApMatrixRec_Scanning"] = "Molimo sačekajte završetak bezbednosne provere...";
            _srDict["ApMatrixTitle_Error"] = "GREŠKA PRI PROVERI";
            _srDict["ApMatrixTitle_ScriptNotFound"] = "SKRIPTA NIJE PRONAĐENA";
            _srDict["ApMatrixDesc_ScriptNotFound"] = "Skripta Provera_autopilota.ps1 nije pronađena na disku.";
            _srDict["ApMatrixRec_ScriptNotFound"] = "Uverite se da se Provera_autopilota.ps1 nalazi u korenskom folderu ili C:\\setup.";
            _srDict["ApMatrixTitle_ScanError"] = "GREŠKA PRI SKENIRANJU";
            _srDict["ApMatrixDesc_ScanError"] = "Skripta za Autopilot proveru nije vratila validan izveštaj.";
            _srDict["ApMatrixRec_ScanError"] = "Kliknite na 'Osveži Proveru' ili proverite mrežnu vezu.";
            _srDict["ApMatrixTitle_ParseError"] = "GREŠKA U PARSIRANJU";
            _srDict["ApMatrixDesc_ParseError"] = "Neispravan JSON format rezultata autopilota.";
            _srDict["ApMatrixTitle_LockedTenant"] = "🔴 ZAKLJUČAN U ORGANIZACIJI (TENANT LOCK)";
            _srDict["ApMatrixDesc_LockedTenant"] = "Uređaj je registrovan u Microsoft oblaku ({0}). OOBE zahteva nalog korporacije!";
            _srDict["ApMatrixRec_LockedTenant"] = "OBAVEZNO KONTAKTIRATI KORPORACIJU ILI PRODAVCA ZA DEREGISTRACIJU IZ INTUNE-A.";
            _srDict["ApMatrixTitle_ReleasedLocalTraces"] = "🟡 OSLOBOĐEN U OBLAKU, LOKALNI TRAGOVI";
            _srDict["ApMatrixDesc_ReleasedLocalTraces"] = "Uređaj je oslobođen iz Autopilot baze, ali na disku postoje ostaci prethodne organizacije.";
            _srDict["ApMatrixRec_ReleasedLocalTraces"] = "Preporučuje se čista instalacija (Clean Install / OOBE reset) radi brisanja lokalnih sertifikata.";
            _srDict["ApMatrixTitle_HiddenLock"] = "🔴 SKRIVENA OPASNOST - ZAKLJUČAN U OBLAKU (DISK OBRISAN)";
            _srDict["ApMatrixDesc_HiddenLock"] = "Lokalni sistem nema tragova, ali je hardverski hash ovog računara i dalje vezan u Microsoft bazi!";
            _srDict["ApMatrixRec_HiddenLock"] = "Računar ne sme ići u dalju prodaju dok ga vlasnik tenanta ne oslobodi iz Microsoft Intune portala.";
            _srDict["ApMatrixTitle_Clean"] = "🟢 100% ČISTO - UREĐAJ JE POTPUNO SLOBODAN";
            _srDict["ApMatrixDesc_Clean"] = "Hardverski otisak nije registrovan u Microsoft oblaku. Nema lokalnih MDM/Autopilot tragova.";
            _srDict["ApMatrixRec_Clean"] = "Uređaj je u potpunosti čist i bezbedan za rad bilo kog korisnika.";
            _srDict["ApMatrixTitle_UnverifiedClean"] = "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OBLAK NIJE POTVRDIO)";
            _srDict["ApMatrixDesc_UnverifiedClean"] = "Lokalni sistem nema tragova, ali tokom brze instalacije Windows OOBE modul nije dobio eksplicitan odgovor iz Microsoft oblaka.";
            _srDict["ApMatrixRec_UnverifiedClean"] = "Ako je uređaj iz korporativnog izvora, preporučuje se mrežna OOBE provera pre prodaje.";
            _srDict["ApLogUnverifiedCleanInfo"] = "Lokalni registar čist; status u ZTD oblaku nije eksplicitno potvrđen.";
            _srDict["ApMatrixTitle_UnverifiedLocalTraces"] = "🟡 UPOZORENJE - LOKALNI TRAGOVI (STATUS U OBLAKU NIJE POTVRĐEN)";
            _srDict["ApMatrixDesc_UnverifiedLocalTraces"] = "Pronađeni su registarski tragovi organizacije, ali Microsoft ZTD oblak nije vratio eksplicitnu potvrdu.";
            _srDict["ApMatrixRec_UnverifiedLocalTraces"] = "Povežite stabilnu internet vezu i kliknite 'Osveži Proveru'.";
            _srDict["ApLogUnverifiedLocalTracesWarn"] = "Pronađeni lokalni tragovi; status u ZTD oblaku čeka potvrdu.";
            _srDict["ApMatrixTitle_OfflineLocalTraces"] = "🟡 OFFLINE - DETEKTOVANI LOKALNI TRAGOVI";
            _srDict["ApMatrixDesc_OfflineLocalTraces"] = "Nema internet konekcije za proveru oblaka, a pronađeni su registarski tragovi organizacije.";
            _srDict["ApMatrixRec_OfflineLocalTraces"] = "Povežite internet i kliknite 'Osveži Proveru' za online proveru.";
            _srDict["ApMatrixTitle_OfflineCleanLocal"] = "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OFFLINE)";
            _srDict["ApMatrixDesc_OfflineCleanLocal"] = "Lokalni sistem nema tragova, ali je računar bio OFFLINE. Status u oblaku ostaje nepoznat.";
            _srDict["ApMatrixRec_OfflineCleanLocal"] = "Za stopostotnu sigurnost, povežite računar na mrežu i ponovite test.";

            // Autopilot Forenzika i ZTD Tekstovi (v2.14.0 - I18N-01)
            _srDict["ApTpmReadyActive"] = "Spreman (TPM 2.0 aktivan)";
            _srDict["ApTpmPresentNotReady"] = "Prisutan (Nije spreman)";
            _srDict["ApTpmMissing"] = "Nije detektovan / Nema TPM";
            _srDict["ApTracesKeysFound"] = "PRONAĐENI TRAGOVI ({0} ključeva)";
            _srDict["ApTracesRegistryFound"] = "PRONAĐENI TRAGOVI U REGISTRU";
            _srDict["ApTracesClean"] = "Čisto (Nema registarskih tragova)";
            _srDict["ApCloudOnlineChecked"] = "Završena online provera";
            _srDict["ApCloudOffline"] = "Offline / Nije provereno";
            _srDict["ApCloudRegistered"] = "REGISTROVAN U MICROSOFT OBLAKU";
            _srDict["ApCloudFree"] = "Slobodan (Nije registrovan u oblaku)";
            _srDict["ApCloudNotVerified"] = "ZTD Oblak nije proveren (Offline)";

            // Autopilot Log Poruke (v2.14.0 - I18N-01)
            _srDict["ApLogScanStart"] = "Pokretanje bezbednosne provere Microsoft Autopilot statusa...";
            _srDict["ApLogLockedWarn"] = "UPOZORENJE: Računar je Autopilot zaključan: {0}";
            _srDict["ApLogCleanSuccess"] = "Verifikacija završena: Računar je slobodan od Autopilot zaključavanja.";
            _srDict["ApLogTracesWarn"] = "Autopilot je oslobođen u oblaku, ali postoje lokalni tragovi.";
            _srDict["ApLogHiddenLockCrit"] = "SKRIVENA OPASNOST: Hardver je vezan u ZTD oblaku iako je disk čist.";
            _srDict["ApLogOfflineTracesWarn"] = "Offline provera: pronađeni lokalni registarski tragovi.";
            _srDict["ApLogOfflineCleanInfo"] = "Offline provera: lokalni registar je čist, preporučuje se mrežna verifikacija.";
            _srDict["ApLogScriptNotFound"] = "Provera_autopilota.ps1 nije pronađena.";
            _srDict["ApLogWorkerException"] = "Izuzetak u Autopilot radniku: {0}";
            _srDict["ApLogInvalidJson"] = "Neispravan JSON format skripte.";
            _srDict["BgWuTitle"] = "POZADINSKI WINDOWS UPDATE";
            _srDict["BgWuDefaultDesc"] = "Pokrenut nakon Koraka 2 — paralelno preuzimanje i instalacija zakrpa u pozadini";

            // Koraci i stanja koraka (WIS-TASK-181)
            _srDict["PhaseStepsTitle"] = "Koraci";
            _srDict["StepStatusWait"] = "ČEKA";
            _srDict["StepStatusRunning"] = "U TOKU";
            _srDict["StepStatusOk"] = "URAĐENO";
            _srDict["StepStatusSkippedDone"] = "PRESKOČENO (već urađeno)";
            _srDict["StepStatusSkippedUser"] = "PRESKOČENO (korisnik)";
            _srDict["StepStatusWarn"] = "UPOZORENJE";
            _srDict["StepStatusError"] = "GREŠKA";

            _srDict["Step_P2_Identify"] = "Identifikacija modela";
            _srDict["Step_P2_NasCache"] = "TrueNAS drajveri";
            _srDict["Step_P2_Vmd"] = "Intel VMD / RST";
            _srDict["Step_P2_Pnp"] = "PnP drajveri";
            _srDict["Step_P2_Diag"] = "Dijagnostika uređaja";
            _srDict["Step_P2_WuDrivers"] = "Windows Update drajveri";

            _srDict["Step_P3_Debloat"] = "Debloat aplikacija";
            _srDict["Step_P3_Telemetry"] = "Gašenje telemetrije";
            _srDict["Step_P3_Oem"] = "OEM brending i regija";
            _srDict["Step_P3_VcRedist"] = "Visual C++ Runtime";
            _srDict["Step_P3_Scaling"] = "Optimalno skaliranje ekrana";
            _srDict["Step_P3_UsbFree"] = "Signal USB slobodan";

            _srDict["Step_P4_Runtimes"] = ".NET Runtimes";
            _srDict["Step_P4_WinRAR"] = "WinRAR";
            _srDict["Step_P4_Chrome"] = "Google Chrome";
            _srDict["Step_P4_Acrobat"] = "Adobe Acrobat";
            _srDict["Step_P4_Office"] = "Microsoft Office";
            _srDict["Step_P4_BatteryBar"] = "BatteryBar";
            _srDict["Step_P4_Shortcuts"] = "Prečice na radnoj površini";
            _srDict["Step_P4_PdfDefault"] = "Podrazumevani PDF pregledač";
            _srDict["Step_P4_Activation"] = "Aktivacija Windows/Office";

            // ==========================================
            // ENGLESKI JEZIK (EN - English)
            // ==========================================
            _enDict["AppTitle"] = "WIS v2.19.0 - Windows Installation Assistant";
            _enDict["CompanyBrand"] = "WIS SERVICE CENTER";
            _enDict["WorkTimeTitle"] = "Elapsed Time";
            _enDict["CpuLabel"] = "⚙ CPU:";
            _enDict["RamLabel"] = "💾 RAM:";
            _enDict["DiskLabel"] = "🖴 Disk:";

            _enDict["TabOverview"] = "📊 Overview";
            _enDict["TabHardware"] = "💻 Hardware Info";
            _enDict["TabDrivers"] = "⚙ Drivers";
            _enDict["TabDiagnostics"] = "🔍 Diagnostics";
            _enDict["TabNetwork"] = "🌐 Network";
            _enDict["TabApps"] = "📦 Applications";
            _enDict["TabLogs"] = "📜 System Log";
            _enDict["TabAutopilot"] = "🛡️ Autopilot";

            _enDict["BtnStartService"] = "▶ Start Automated Service";
            _enDict["BtnPause"] = "⏸ Pause";
            _enDict["BtnResume"] = "▶ Resume";
            _enDict["BtnSkip"] = "⏭ Skip Current";
            _enDict["BtnDelayRestart"] = "⏱ Delay Reboot";
            _enDict["BtnRefreshHardware"] = "🔄 Refresh Hardware";
            _enDict["BtnRerunApps"] = "🔄 Reinstall Apps";
            _enDict["BtnRunStressTest"] = "🔥 Start 30s Stress Test";

            _enDict["InstallationFlowTitle"] = "INSTALLATION & OPTIMIZATION FLOW";
            _enDict["CurrentPhaseTitle"] = "CURRENT ACTIVITY & PHASE PROGRESS";
            _enDict["LivePhaseTitle"] = "ACTIVE TASK & SCRIPT";
            _enDict["RecentLogsTitle"] = "RECENT ACTIVITY (LAST 5 LOGS)";
            _enDict["HardwareSummaryTitle"] = "HARDWARE SPECIFICATIONS & HEALTH";
            _enDict["UsbFreeNotice"] = "✓ USB Flash Drive is safe to disconnect";

            _enDict["LogServiceJournalTitle"] = "Service Operation Log";
            _enDict["LogBufferTitle"] = "DETAILED RING-BUFFER LOG (MAX 500 LINES)";
            _enDict["LogAutoScrollCheck"] = "Follow newest (top)";
            _enDict["LogSearchLabel"] = "Search log:";
            _enDict["LogCopyAllBtn"] = "📋 Copy All";
            _enDict["LogClearBtn"] = "🗑 Clear View";
            _enDict["LogFilterLevelLabel"] = "Filter Level:";
            _enDict["LogFilterAll"] = "All";
            _enDict["LogFilterInfo"] = "Info";
            _enDict["LogFilterWarning"] = "Warning";
            _enDict["LogFilterError"] = "Error";
            _enDict["LogColTime"] = "Time";
            _enDict["LogColLevel"] = "Level";
            _enDict["LogColTag"] = "Tag";
            _enDict["LogColMessage"] = "Message";

            _enDict["PhaseStepsTitle"] = "Steps";
            _enDict["StepStatusWait"] = "WAIT";
            _enDict["StepStatusRunning"] = "RUNNING";
            _enDict["StepStatusOk"] = "DONE";
            _enDict["StepStatusSkippedDone"] = "SKIPPED (already done)";
            _enDict["StepStatusSkippedUser"] = "SKIPPED (user)";
            _enDict["StepStatusWarn"] = "WARNING";
            _enDict["StepStatusError"] = "ERROR";

            _enDict["Step_P2_Identify"] = "Model identification";
            _enDict["Step_P2_NasCache"] = "TrueNAS model drivers";
            _enDict["Step_P2_Vmd"] = "Intel VMD / RST";
            _enDict["Step_P2_Pnp"] = "PnP driver install";
            _enDict["Step_P2_Diag"] = "Device diagnostics";
            _enDict["Step_P2_WuDrivers"] = "Windows Update drivers";

            _enDict["Step_P3_Debloat"] = "App debloat";
            _enDict["Step_P3_Telemetry"] = "Disable telemetry";
            _enDict["Step_P3_Oem"] = "OEM branding & region";
            _enDict["Step_P3_VcRedist"] = "Visual C++ Runtime";
            _enDict["Step_P3_Scaling"] = "Display scaling";
            _enDict["Step_P3_UsbFree"] = "USB free signal";

            _enDict["Step_P4_Runtimes"] = ".NET Runtimes";
            _enDict["Step_P4_WinRAR"] = "WinRAR";
            _enDict["Step_P4_Chrome"] = "Google Chrome";
            _enDict["Step_P4_Acrobat"] = "Adobe Acrobat";
            _enDict["Step_P4_Office"] = "Microsoft Office";
            _enDict["Step_P4_BatteryBar"] = "BatteryBar";
            _enDict["Step_P4_Shortcuts"] = "Desktop shortcuts";
            _enDict["Step_P4_PdfDefault"] = "Default PDF viewer";
            _enDict["Step_P4_Activation"] = "Windows/Office activation";

            // ==========================================
            // KINESKI JEZIK (ZH - 简体中文)
            // ==========================================
            _zhDict["AppTitle"] = "WIS v2.18.0 - 视窗系统自动化安装助手";
            _zhDict["CompanyBrand"] = "WIS SERVICE 电脑维修中心";
            _zhDict["WorkTimeTitle"] = "运行时间";
            _zhDict["CpuLabel"] = "⚙ 处理器:";
            _zhDict["RamLabel"] = "💾 内存:";
            _zhDict["DiskLabel"] = "🖴 硬盘:";

            // Tabovi
            _zhDict["TabOverview"] = "📊 概览";
            _zhDict["TabHardware"] = "💻 硬件信息";
            _zhDict["TabDrivers"] = "⚙ 驱动程序";
            _zhDict["TabDiagnostics"] = "🔍 系统诊断";
            _zhDict["TabNetwork"] = "🌐 网络连接";
            _zhDict["TabApps"] = "📦 常用软件";
            _zhDict["TabLogs"] = "📜 运行日志";
            _zhDict["TabAutopilot"] = "🛡️ 自动巡检";

            // Dugmad i kontrole
            _zhDict["BtnStartService"] = "▶ 开始自动化安装";
            _zhDict["BtnPause"] = "⏸ 暂停";
            _zhDict["BtnResume"] = "▶ 继续";
            _zhDict["BtnSkip"] = "⏭ 跳过当前";
            _zhDict["BtnDelayRestart"] = "⏱ 延迟重启";
            _zhDict["BtnRefreshHardware"] = "🔄 刷新硬件信息";
            _zhDict["BtnRerunApps"] = "🔄 重新安装软件";
            _zhDict["BtnRunStressTest"] = "🔥 启动30秒压力测试";
            _zhDict["BtnCancelStressTest"] = "⏹ 终止测试";
            _zhDict["BtnCleanAndShutdown"] = "🔴 清理并安全关机";
            _zhDict["BtnCompletionSummary"] = "📋 查看维修服务卡";
            _zhDict["BtnExitWithoutClean"] = "✕ 退出 (不清理)";
            _zhDict["BtnStayInApp"] = "📋 留在程序主界面";
            _zhDict["TooltipStayInApp"] = "关闭此结算窗口，允许仔细查看各项选项卡、硬件信息与日志。关机与清理按钮将保留在底部操作栏。";

            // Jezičke oznake
            _zhDict["LangSrb"] = "🇷🇸 SRB";
            _zhDict["LangZh"] = "🇨🇳 中文";
            _zhDict["LanguageSelectTooltipSrb"] = "切换界面为塞尔维亚语";
            _zhDict["LanguageSelectTooltipZh"] = "切换界面为简体中文";

            // TAB 1: 概览 (Overview)
            _zhDict["ServiceReadyBadge"] = "⚡ 系统就绪";
            _zhDict["ServiceReadyDesc"] = "计算机已就绪，准备开始部署操作系统、驱动及常用软件。";
            _zhDict["ComputerModelLabel"] = "计算机型号:";
            _zhDict["SerialNumberLabel"] = "序列号 (SN):";
            _zhDict["ProcessorLabel"] = "中央处理器 (CPU):";
            _zhDict["MemoryLabel"] = "运行内存 (RAM):";
            _zhDict["DiskHealthLabel"] = "硬盘健康状态:";
            _zhDict["SmartStatusLabel"] = "SMART 健康状态:";
            _zhDict["AutopilotStatusLabel"] = "Autopilot 巡检状态:";
            _zhDict["InstallationFlowTitle"] = "安装流程与阶段";
            _zhDict["UsbFreeTitle"] = "U盘已安全释放！";
            _zhDict["UsbFreeDesc"] = "安装U盘已安全弹出——您可以放心拔出并在下一台机器上使用。";
            _zhDict["RecentActivityTitle"] = "最近活动记录";
            _zhDict["Phase1Title"] = "1. 网络";
            _zhDict["Phase1Subtitle"] = "Wi-Fi 与路由";
            _zhDict["Phase2Title"] = "2. 驱动更新";
            _zhDict["Phase2Subtitle"] = "驱动与补丁";
            _zhDict["Phase3Title"] = "3. 系统优化";
            _zhDict["Phase3Subtitle"] = "精简与个性化";
            _zhDict["Phase4Title"] = "4. 软件安装";
            _zhDict["Phase4Subtitle"] = "软件包与 OEM 授权";
            _zhDict["Phase5Title"] = "5. 压力测试";
            _zhDict["Phase5Subtitle"] = "稳定性验证 (30秒)";
            _zhDict["Phase6Title"] = "6. 完成交付";
            _zhDict["Phase6Subtitle"] = "诊断与总览";
            _zhDict["PhaseStatusPending"] = "等待中";
            _zhDict["PhaseStatusActive"] = "进行中";
            _zhDict["PhaseStatusCompleted"] = "已完成";

            // TAB 2: 硬件信息 (Hardware)
            _zhDict["HardwareOverviewTitle"] = "硬件配置概览";
            _zhDict["HwCpuCardTitle"] = "🔲 中央处理器 (CPU)";
            _zhDict["HwCpuModelLabel"] = "处理器型号:";
            _zhDict["HwCpuCoresThreadsLabel"] = "核心与线程:";
            _zhDict["HwCpuClockLabel"] = "工作主频:";
            _zhDict["HwCpuSocketLabel"] = "插槽类型 (Socket):";
            _zhDict["HwRamCardTitle"] = "🧠 运行内存 (RAM)";
            _zhDict["HwRamTotalLabel"] = "内存总容量:";
            _zhDict["HwRamTypeLabel"] = "内存类型与代数:";
            _zhDict["HwRamSpeedLabel"] = "主频与频率:";
            _zhDict["HwRamConfigLabel"] = "内存插槽配置:";
            _zhDict["HwRamSlotsLabel"] = "插槽使用情况:";
            _zhDict["HwRamSoldered"] = "板载集成内存 (Soldered)";
            _zhDict["HwRamSodimm"] = "标准插槽模块 (SO-DIMM)";
            _zhDict["HwStorageCardTitle"] = "💾 存储设备与健康 (SSD)";
            _zhDict["HwStoragePrimaryLabel"] = "主系统磁盘:";
            _zhDict["HwStorageSmartLabel"] = "SMART 健康状态:";
            _zhDict["HwStorageHoursLabel"] = "写入量与通电时间:";
            _zhDict["HwStorageTempLabel"] = "硬盘工作温度:";
            _zhDict["HwStorageCapacityLabel"] = "磁盘容量:";
            _zhDict["HwStorageMediaTypeLabel"] = "介质类型:";
            _zhDict["HwStorageInterfaceLabel"] = "磁盘接口:";
            _zhDict["HwBatteryCardTitle"] = "🔋 电池与电源管理";
            _zhDict["HwBatteryWearLabel"] = "电池损耗率 (Wear Level):";
            _zhDict["HwBatteryCapacityLabel"] = "电池容量 (当前 / 设计):";
            _zhDict["HwBatteryCyclesLabel"] = "循环充放电次数:";
            _zhDict["HwBatterySourceLabel"] = "当前供电来源:";
            _zhDict["HwBatteryHealthLabel"] = "电池健康度:";
            _zhDict["HwBatteryDesignedCapLabel"] = "出厂设计容量:";
            _zhDict["HwBatteryFullCapLabel"] = "完全充电容量:";
            _zhDict["HwGpuCardTitle"] = "🎮 显示适配器与屏幕";
            _zhDict["HwGpuPrimaryLabel"] = "主显卡型号:";
            _zhDict["HwGpuPrimaryVramLabel"] = "显存容量 (VRAM):";
            _zhDict["HwGpuSecondaryLabel"] = "副显卡型号:";
            _zhDict["HwGpuSecondaryVramLabel"] = "副显卡显存:";
            _zhDict["HwDisplaySpecLabel"] = "分辨率与刷新率:";
            _zhDict["HwTouchscreenLabel"] = "触摸屏支持 (Touch):";
            _zhDict["HwMotherboardCardTitle"] = "🖥️ 主板与 BIOS 固件";
            _zhDict["HwMbModelLabel"] = "主板型号与品牌:";
            _zhDict["HwMbManufacturerLabel"] = "主板制造商:";
            _zhDict["HwSerialNumberLabel"] = "设备序列号 (SN):";
            _zhDict["HwBiosVersionDateLabel"] = "BIOS 版本与发布日期:";
            _zhDict["HwSecureBootLabel"] = "安全启动状态 (Secure Boot):";
            _zhDict["HwChassisTypeLabel"] = "机箱与外观类型:";
            _zhDict["HwPeripheralsCardTitle"] = "📷 外设与生物识别 (Windows Hello)";
            _zhDict["HwWebcamLabel"] = "摄像头 (Webcam):";
            _zhDict["HwIrCameraLabel"] = "红外摄像头 (Windows Hello):";
            _zhDict["HwBacklitKeyboardLabel"] = "键盘背光支持:";
            _zhDict["HwFingerprintLabel"] = "指纹识别传感器:";

            // TAB 3: 驱动程序 (Drivers)
            _zhDict["DriverInterventionRequired"] = "需要驱动干预";
            _zhDict["DriverClassLabel"] = "设备类别:";
            _zhDict["DriverManufacturerLabel"] = "制造商:";
            _zhDict["BtnCopyHwid"] = "📋 复制硬件ID";
            _zhDict["TooltipCopyHwid"] = "复制完整硬件ID到Windows剪贴板";
            _zhDict["BtnInstallOfficialDriver"] = "⬇ 安装官方驱动";
            _zhDict["TooltipInstallDriver"] = "自动从ServisHub或微软目录下载并部署官方OEM驱动";
            _zhDict["DriverInstalledSuccess"] = "✓ 驱动程序安装成功！";
            _zhDict["DriverAllGood"] = "设备管理器中的所有硬件设备均正常工作 (0个错误)。";
            _zhDict["DriverFilterPlaceholder"] = "按名称、类别、厂商或硬件ID过滤...";
            _zhDict["BtnCheckKeyDrivers"] = "🔍 检查关键驱动";
            _zhDict["TooltipCheckKeyDrivers"] = "通过ServisHub检查显卡、网络、声卡与芯片组的最新官方驱动";
            _zhDict["BtnResolveAllDrivers"] = "⚡ 一键修复缺失驱动";
            _zhDict["TooltipResolveAllDrivers"] = "自动为所有异常设备下载并安装官方驱动程序";
            _zhDict["BtnRefreshDriverTree"] = "🔄 刷新设备树";
            _zhDict["TooltipRefreshDriverTree"] = "重新读取系统中的所有即插即用设备与驱动";
            _zhDict["BtnOpenDeviceManager"] = "🛠️ 打开设备管理器";
            _zhDict["TooltipOpenDeviceManager"] = "打开系统自带的设备管理器 (devmgmt.msc)";
            _zhDict["DriversInstalledByCategoryTitle"] = "已安装设备与驱动程序分类树";
            _zhDict["DriversTotalDetected"] = "检测到的设备总数:";
            _zhDict["DeviceUnitCount"] = "个设备";
            _zhDict["DriverVersionLabel"] = "驱动程序版本:";
            _zhDict["DriverDateLabel"] = "发布日期:";
            _zhDict["BtnDriverCheck"] = "🔍 检查";
            _zhDict["DriverCheckingStatus"] = "🔍 正在检查...";
            _zhDict["DriverNewVersionAvailable"] = "发现新版本:";
            _zhDict["BtnDriverUpgrade"] = "⬇ 立即升级";
            _zhDict["DriverUpToDate"] = "✓ 已是最新版本";
            _zhDict["BtnHwidShort"] = "📋 硬件ID";
            _zhDict["DriverInstalling"] = "驱动正在安装中...";
            _zhDict["DriverCatNetwork"] = "网络与无线适配器";
            _zhDict["DriverCatGraphics"] = "图形显卡与芯片组";
            _zhDict["DriverCatAudio"] = "声卡与音频设备";
            _zhDict["DriverCatStorage"] = "存储控制器与硬盘";
            _zhDict["DriverCatBluetooth"] = "蓝牙适配器";
            _zhDict["DriverCatBiometrics"] = "生物识别与摄像头";
            _zhDict["DriverCatSystem"] = "系统基础设备";
            _zhDict["DriverCatOther"] = "其他外设与接口";
            _zhDict["DriverSearchCatalog"] = "在微软驱动目录中搜索";

            // TAB 4: 系统诊断 (Diagnostics)
            _zhDict["DiagGraphTitle"] = "📈 5分钟温度与稳定性实时趋势图 (1 Hz)";
            _zhDict["BtnStartStress"] = "▶ 启动压力测试";
            _zhDict["BtnStopStress"] = "⏹ 停止测试";
            _zhDict["BtnResetMinMax"] = "↺ 重置极值";
            _zhDict["CoreClocksTitle"] = "⚡ 处理器核心频率监控 (CORE CLOCKS)";
            _zhDict["ColCoreName"] = "核心";
            _zhDict["ColCurrentClock"] = "当前频率";
            _zhDict["ColMinClock"] = "最低频率";
            _zhDict["ColMaxClock"] = "最高频率";
            _zhDict["ColClockRatio"] = "频率负载比";
            _zhDict["DiagSmartTitle"] = "🖴 S.M.A.R.T. 与固态硬盘健康";
            _zhDict["DiagWearPercentLabel"] = "健康寿命余量 (Wear %):";
            _zhDict["DiagSmartStatusLabel"] = "SMART 运行状态:";
            _zhDict["DiagTotalTbwLabel"] = "累计总写入量 (TBW):";
            _zhDict["DiagReallocatedSectorsLabel"] = "重映射扇区计数:";
            _zhDict["DiagCriticalWarningsLabel"] = "严重警告指示:";
            _zhDict["DiagBatteryTitle"] = "🔋 电池与电源状态";
            _zhDict["DiagBatteryHealthLabel"] = "电池健康度:";
            _zhDict["DiagBatteryCyclesLabel"] = "循环充电次数:";
            _zhDict["DiagBatteryPowerStatusLabel"] = "电源工作状态:";
            _zhDict["DiagDeviceTypeLabel"] = "设备机型类型:";
            _zhDict["DiagCpuRamTitle"] = "⚡ 处理器与运行内存状态";
            _zhDict["DiagCpuLabel"] = "中央处理器 (CPU):";
            _zhDict["DiagCpuUsageLabel"] = "处理器占用率:";
            _zhDict["DiagRamLabel"] = "运行内存 (RAM):";
            _zhDict["DiagRamUsageLabel"] = "内存已用占用率:";
            _zhDict["DiagGpuDisplayTitle"] = "🎮 显示适配器与屏幕状态";
            _zhDict["DiagGpuLabel"] = "图形显卡 (GPU):";
            _zhDict["DiagVramLabel"] = "独立显存 (VRAM):";
            _zhDict["DiagDisplayResLabel"] = "显示屏与分辨率:";
            _zhDict["DiagRefreshDpiLabel"] = "刷新率与缩放比例:";
            _zhDict["DiagDeviceManagerTitle"] = "🛠 设备管理器 (PnP状态)";
            _zhDict["DiagAllDevicesOkText"] = "所有硬件模块均正常工作，Windows设备管理器中无故障设备。";
            _zhDict["DiagMotherboardBiosTpmTitle"] = "🖧 主板、BIOS 与安全芯片";
            _zhDict["DiagMotherboardLabel"] = "主板型号:";
            _zhDict["DiagBiosVersionLabel"] = "BIOS 版本:";
            _zhDict["DiagBiosDateLabel"] = "BIOS 发布日期:";
            _zhDict["DiagTpmChipLabel"] = "TPM 2.0 安全芯片:";
            _zhDict["DiagSecureBootLabel"] = "安全启动 (Secure Boot):";
            _zhDict["ThermalStatusTitle"] = "🌡️ 硬件温度与传感器";
            _zhDict["ThermalCurrentTempLabel"] = "当前核心温度:";
            _zhDict["ThermalMaxRecordedLabel"] = "历史最高温度:";
            _zhDict["ThermalCpuLoadLabel"] = "处理器负载率:";
            _zhDict["ThermalCpuTempLabel"] = "处理器核心温度 (CPU):";
            _zhDict["ThermalGpuTempLabel"] = "图形显卡核心温度 (GPU):";
            _zhDict["ThermalSsdTempLabel"] = "固态硬盘工作温度 (SSD):";
            _zhDict["ThermalFanSpeedLabel"] = "散热风扇转速 (Fan):";
            _zhDict["ThermalStressTitle"] = "🔥 处理器温度压力测试 (30秒)";
            _zhDict["TooltipRunStressTest"] = "启动高负载多核压力测试，超过98°C自动熔断中止";
            _zhDict["TooltipCancelStressTest"] = "立即终止当前的温度压力测试";
            _zhDict["ThermalThresholdNormal"] = "运行正常";
            _zhDict["ThermalThresholdElevated"] = "温度偏高";
            _zhDict["ThermalThresholdOverheating"] = "严重过热";

            // TAB 5: 网络连接 (Network)
            _zhDict["NetNetworkAdaptersTitle"] = "网络适配器与连接状态";
            _zhDict["NetAdapterNameLabel"] = "网卡适配器名称:";
            _zhDict["NetConnectionStatusConnected"] = "已连接";
            _zhDict["NetConnectionStatusDisconnected"] = "未连接";
            _zhDict["NetSubnetMaskLabel"] = "子网掩码 (Subnet Mask):";
            _zhDict["NetGatewayLabel"] = "默认网关 (Gateway):";
            _zhDict["NetDnsServersLabel"] = "DNS 域名解析服务器:";
            _zhDict["NetMacAddressLabel"] = "物理地址 (MAC):";
            _zhDict["NetLinkSpeedLabel"] = "链路协商速率:";
            _zhDict["NetWifiTitle"] = "📶 无线网络连接 (Wi-Fi)";
            _zhDict["NetCurrentSsidLabel"] = "当前无线网络 (SSID):";
            _zhDict["NetConnectionStatusLabel"] = "网络连接状态:";
            _zhDict["NetServiceWifiAutoText"] = "维修中心自动化 Wi-Fi";
            _zhDict["NetIpConfigTitle"] = "🖥 IP 地址与网络配置";
            _zhDict["NetIpAddressLabel"] = "IPv4 地址:";
            _zhDict["NetAdapterTypeLabel"] = "网络适配器类型:";
            _zhDict["NetNasServerTitle"] = "🗄 维修站 NAS 服务器";
            _zhDict["NetNasConnectionStatusLabel"] = "NAS 仓库连接状态:";
            _zhDict["NetNasPingLatencyLabel"] = "缓存服务器延迟 (Ping):";
            _zhDict["NetTrafficDownloadTitle"] = "📥 流量传输与下载数据";
            _zhDict["NetTotalDownloadedPackagesLabel"] = "下载软件包总数/流量:";
            _zhDict["NetDistributionSourceLabel"] = "分发服务器来源:";
            _zhDict["NetDistributionSourceValue"] = "本地维修服务站 NAS 软件库";

            // TAB 6: 常用软件 (Apps)
            _zhDict["StandardAppsTitle"] = "标准服务软件包 (第4阶段)";
            _zhDict["AppsInstallStatusTitle"] = "软件部署安装状态";
            _zhDict["AppsRerunInstallBtn"] = "重新运行安装";
            _zhDict["AppStatusWait"] = "排队等待中";
            _zhDict["AppStatusInstalling"] = "正在安装中...";
            _zhDict["AppStatusSuccess"] = "安装成功已验证";
            _zhDict["AppStatusError"] = "安装失败";
            _zhDict["AppStatusNotNeeded"] = "无需安装/已跳过";
            _zhDict["AppDetailWait"] = "等待第4阶段部署开始";
            _zhDict["AppDetailRunning"] = "正在部署软件包...";
            _zhDict["AppDetailSuccess"] = "应用程序安装与校验成功";
            _zhDict["AppDetailError"] = "安装过程中发生异常";
            _zhDict["AppBadgeWait"] = "⏳ 等待";
            _zhDict["AppBadgeRunning"] = "⚙ 进行中";
            _zhDict["AppBadgeSuccess"] = "✓ 完成";
            _zhDict["AppBadgeError"] = "✗ 错误";
            _zhDict["AppBadgeSkipped"] = "⏭ 已跳过";

            // TAB 7: 运行日志 (Logs)
            _zhDict["LogServiceJournalTitle"] = "系统服务与部署日志";
            _zhDict["LogBufferTitle"] = "实时环形日志缓冲区 (最大500行)";
            _zhDict["LogAutoScrollCheck"] = "自动滚动至最新记录";
            _zhDict["LogSearchLabel"] = "搜索日志内容:";
            _zhDict["LogCopyAllBtn"] = "📋 复制全部日志";
            _zhDict["LogClearBtn"] = "🗑 清空当前显示";
            _zhDict["LogFilterLevelLabel"] = "日志过滤级别:";
            _zhDict["LogFilterAll"] = "全部";
            _zhDict["LogFilterInfo"] = "信息 (Info)";
            _zhDict["LogFilterWarning"] = "警告 (Warning)";
            _zhDict["LogFilterError"] = "错误 (Error)";
            _zhDict["LogColTime"] = "时间戳";
            _zhDict["LogColLevel"] = "日志级别";
            _zhDict["LogColTag"] = "模块标签";
            _zhDict["LogColMessage"] = "日志详情";

            // TAB 8: 自动巡检 (Autopilot)
            _zhDict["ApSystemInspectionTitle"] = "Autopilot 系统自动化巡检";
            _zhDict["ApBatteryHealthWearTitle"] = "电池健康与损耗状态";
            _zhDict["ApDesignedCapacityLabel"] = "出厂设计容量:";
            _zhDict["ApFullCapacityLabel"] = "完全充电容量:";
            _zhDict["ApWearLevelLabel"] = "电池损耗率:";
            _zhDict["ApCycleCountLabel"] = "循环充电次数:";
            _zhDict["ApPowerStatusLabel"] = "电源工作状态:";
            _zhDict["ApTpm2SecurityTitle"] = "TPM 2.0 安全芯片";
            _zhDict["ApSecureBootStatusTitle"] = "Secure Boot 安全启动";
            _zhDict["ApUefiCsmModeTitle"] = "UEFI / CSM 固件模式";
            _zhDict["BtnRescanAutopilot"] = "🔄 重新运行巡检";
            _zhDict["AutopilotUpdatedFormat"] = "更新于:";
            _zhDict["ApHardwareFingerprintTitle"] = "🖥 硬件指纹标识 (Hardware Hash)";
            _zhDict["ApManufacturerLabel"] = "设备制造商:";
            _zhDict["ApDeviceModelLabel"] = "设备具体型号:";
            _zhDict["ApSerialNumberLabel"] = "硬件序列号 (SN):";
            _zhDict["ApSmbiosUuidLabel"] = "SMBIOS 全球唯一标识 (UUID):";
            _zhDict["ApTpmStatusLabel"] = "TPM 2.0 安全芯片状态:";
            _zhDict["ApLocalForensicsTitle"] = "🔍 本地残留取证分析";
            _zhDict["ApRegistryTracesLabel"] = "注册表企业配置痕迹:";
            _zhDict["ApTenantDomainLabel"] = "租户组织域名:";
            _zhDict["ApTenantGuidLabel"] = "租户唯一识别号 (GUID):";
            _zhDict["ApAssignedNameLabel"] = "企业预分配计算机名:";
            _zhDict["ApEntraIdStatusLabel"] = "Entra ID (Azure AD) 绑定:";
            _zhDict["ApIntuneMdmLabel"] = "Intune MDM 设备托管注册:";
            _zhDict["ApOrgCertsLabel"] = "企业根安全证书数量:";
            _zhDict["ApZtdCloudTitle"] = "☁ 微软云端 ZTD 验证";
            _zhDict["ApOnlineZtdQueryLabel"] = "云端联网查询 (Online Query):";
            _zhDict["ApCloudStatusLabel"] = "云端锁定状态:";
            _zhDict["ApServerResponseCodeLabel"] = "服务器状态返回码:";
            _zhDict["ApRawResponseLabel"] = "原始响应报文 (Raw):";
            _zhDict["ApZtdDescText"] = "微软 ZTD 零信任服务 (ztd.dds.microsoft.com) 直接通过硬件哈希云端校验设备所属组织，无需管理员凭据。";

            // Završni ekran i rekapitulacija
            _zhDict["TotalServiceDuration"] = "服务总耗时:";
            _zhDict["InstalledAppsLabel"] = "已安装应用程序:";
            _zhDict["BatteryPowerStatusLabel"] = "电池 / 电源状态:";
            _zhDict["ServiceFinishedTitle"] = "✓ 安装与配置圆满完成";
            _zhDict["ServiceFinishedDesc"] = "系统与应用软件已成功部署并完成性能深度优化。";
            _zhDict["SelectFinalActionDesc"] = "请选择服务完成后的后续操作：";
            _zhDict["CompletionDefaultTitle"] = "服务完成 - 计算机已准备交付客户";
            _zhDict["CompletionDefaultSummary"] = "所有硬件组件、系统补丁及应用程序均已成功部署并通过验证。";

            // Reality Check i fazni statusi
            _zhDict["RealityCheckMissingApps"] = "磁盘上缺少关键应用程序";
            _zhDict["RealityCheckBadDevices"] = "设备管理器中检测到存在驱动异常的设备";
            _zhDict["RealityCheckSuccessApps"] = "所有关键应用程序 (Chrome, Acrobat, Office, WinRAR) 均已物理验证。";
            _zhDict["RealityCheckSuccessDrivers"] = "设备管理器中的所有硬件设备均正常工作 (0个错误)。";

            // Hardver i specifikacije ekrana (v2.14.0 - HW-03 / HW-07)
            _zhDict["HwDisplayModelLabel"] = "屏幕面板型号:";
            _zhDict["HwDisplayBrightnessLabel"] = "屏幕亮度 (尼特):";
            _zhDict["HwDisplayColorGamutLabel"] = "色彩覆盖率 (色域):";
            _zhDict["HwSpeakersLabel"] = "音频输出 (扬声器):";
            _zhDict["HwMicrophoneLabel"] = "音频输入 (麦克风):";

            // Dinamički periferni statusi (v2.14.0 - HW-04..HW-07 / I18N-01)
            _zhDict["HwIrYes"] = "👁️ 是 (Windows Hello 人脸识别)";
            _zhDict["HwIrNo"] = "否 (标准 RGB 摄像头)";
            _zhDict["HwFpYes"] = "👆 是 ({0})";
            _zhDict["HwFpNo"] = "未检测到指纹识别器";
            _zhDict["HwKbYes"] = "💡 是 (键盘背光)";
            _zhDict["HwKbNo"] = "标准键盘 (无背光)";
            _zhDict["HwKbCheckManual"] = "标准键盘 / 请手动检查 (Fn+空格)";
            _zhDict["HwTouchYes"] = "🖐️ 支持 ({0} 点触控)";
            _zhDict["HwTouchYesSimple"] = "🖐️ 支持触控 (Touchscreen)";
            _zhDict["HwTouchNo"] = "未检测到 (普通显示屏)";
            _zhDict["HwCamNo"] = "未安装摄像头";
            _zhDict["HwCamDetecting"] = "摄像头检测中...";
            _zhDict["HwLhmActive"] = "● LibreHardwareMonitor 驱动已就绪";
            _zhDict["HwWmiFallback"] = "● WMI/ACPI 备用模式";
            _zhDict["HwAudioSpeakersDefault"] = "标准音频播放设备";
            _zhDict["HwMicYes"] = "🎤 是 ({0})";
            _zhDict["HwMicNo"] = "未检测到麦克风";
            _zhDict["BtnPauseService"] = "⏸ 暂停服务";
            _zhDict["BtnResumeService"] = "▶ 继续服务";

            // Autopilot Dinamički Matrix Statusi (v2.14.0 - I18N-01)
            _zhDict["ApMatrixTitle_Scanning"] = "AUTOPILOT 巡检进行中...";
            _zhDict["ApMatrixDesc_Scanning"] = "系统正在对本地注册表进行深度取证，并联网验证微软 ZTD 零信任云端...";
            _zhDict["ApMatrixRec_Scanning"] = "请耐心等待系统安全检查完成...";
            _zhDict["ApMatrixTitle_Error"] = "巡检过程发生错误";
            _zhDict["ApMatrixTitle_ScriptNotFound"] = "未找到巡检脚本";
            _zhDict["ApMatrixDesc_ScriptNotFound"] = "在磁盘上未找到 Provera_autopilota.ps1 脚本。";
            _zhDict["ApMatrixRec_ScriptNotFound"] = "请确保 Provera_autopilota.ps1 位于根目录或 C:\\setup 中。";
            _zhDict["ApMatrixTitle_ScanError"] = "扫描执行错误";
            _zhDict["ApMatrixDesc_ScanError"] = "Autopilot 检查脚本未返回有效检测报告。";
            _zhDict["ApMatrixRec_ScanError"] = "请点击“重新运行巡检”或检查网络连接状态。";
            _zhDict["ApMatrixTitle_ParseError"] = "报告解析错误";
            _zhDict["ApMatrixDesc_ParseError"] = "Autopilot 巡检结果的 JSON 格式无效。";
            _zhDict["ApMatrixTitle_LockedTenant"] = "🔴 已被企业租户锁定 (TENANT LOCK)";
            _zhDict["ApMatrixDesc_LockedTenant"] = "该设备已在微软云端注册 ({0})。首次开机必须使用所属企业账户登录！";
            _zhDict["ApMatrixRec_LockedTenant"] = "必须联系所属企业 IT 部门或供应商在 Intune 门户中解除设备注销绑定。";
            _zhDict["ApMatrixTitle_ReleasedLocalTraces"] = "🟡 云端已释放绑定，但存在本地企业残留";
            _zhDict["ApMatrixDesc_ReleasedLocalTraces"] = "设备已从微软 Autopilot 数据库解除，但本地磁盘仍残留前企业配置。";
            _zhDict["ApMatrixRec_ReleasedLocalTraces"] = "建议执行完全纯净安装 (Clean Install) 或重置 OOBE 以清除本地企业根证书。";
            _zhDict["ApMatrixTitle_HiddenLock"] = "🔴 隐蔽风险 - 磁盘已清除但在云端仍锁定";
            _zhDict["ApMatrixDesc_HiddenLock"] = "本地系统无任何痕迹，但此计算机的硬件指纹哈希仍在微软企业数据库中绑定！";
            _zhDict["ApMatrixRec_HiddenLock"] = "在企业管理员从 Intune 彻底解绑前，此设备不可交付或对外销售。";
            _zhDict["ApMatrixTitle_Clean"] = "🟢 100% 干净 - 设备完全自由安全";
            _zhDict["ApMatrixDesc_Clean"] = "硬件哈希未在微软云端注册，本地亦无任何 MDM/Autopilot 残留痕迹。";
            _zhDict["ApMatrixRec_Clean"] = "设备处于绝对干净状态，任何个人或组织均可安全无阻使用。";
            _zhDict["ApMatrixTitle_UnverifiedClean"] = "🔵 条件性安全 - 本地无残留 (云端尚未确认)";
            _zhDict["ApMatrixDesc_UnverifiedClean"] = "本地系统无企业残留，但在无人值守安装中 Windows OOBE 模块未从微软云端获取明确回复。";
            _zhDict["ApMatrixRec_UnverifiedClean"] = "如设备来自企业批量采购，建议在销售前通过 OOBE 联网验证。";
            _zhDict["ApLogUnverifiedCleanInfo"] = "本地注册表干净；微软 ZTD 云端状态尚未明确确认。";
            _zhDict["ApMatrixTitle_UnverifiedLocalTraces"] = "🟡 警告 - 存在本地痕迹 (云端尚未确认)";
            _zhDict["ApMatrixDesc_UnverifiedLocalTraces"] = "检测到企业注册表配置，但微软 ZTD 零信任云端未返回明确状态。";
            _zhDict["ApMatrixRec_UnverifiedLocalTraces"] = "请连接稳定互联网并点击“重新运行巡检”。";
            _zhDict["ApLogUnverifiedLocalTracesWarn"] = "检测到本地残留痕迹；ZTD 云端验证仍在等待确认。";
            _zhDict["ApMatrixTitle_OfflineLocalTraces"] = "🟡 离线状态 - 检测到本地企业注册残留";
            _zhDict["ApMatrixDesc_OfflineLocalTraces"] = "当前无互联网连接以验证云端，且本地注册表中检测到企业配置痕迹。";
            _zhDict["ApMatrixRec_OfflineLocalTraces"] = "请连接互联网并点击“重新运行巡检”以完成云端真实性验证。";
            _zhDict["ApMatrixTitle_OfflineCleanLocal"] = "🔵 条件性干净 - 本地无残留 (当前离线)";
            _zhDict["ApMatrixDesc_OfflineCleanLocal"] = "本地注册表干净，但计算机处于离线状态，云端锁定状态尚无法确认。";
            _zhDict["ApMatrixRec_OfflineCleanLocal"] = "为确保 100% 安全，请将计算机接入网络并重新进行验证测试。";

            // Autopilot Forenzika i ZTD Tekstovi (v2.14.0 - I18N-01)
            _zhDict["ApTpmReadyActive"] = "已就绪 (TPM 2.0 已激活)";
            _zhDict["ApTpmPresentNotReady"] = "存在芯片 (但未完全就绪)";
            _zhDict["ApTpmMissing"] = "未检测到 / 无 TPM 芯片";
            _zhDict["ApTracesKeysFound"] = "发现残留配置 ({0} 个注册表项)";
            _zhDict["ApTracesRegistryFound"] = "在系统注册表中发现企业残留";
            _zhDict["ApTracesClean"] = "干净 (无任何企业注册表痕迹)";
            _zhDict["ApCloudOnlineChecked"] = "已完成云端在线联网验证";
            _zhDict["ApCloudOffline"] = "离线 / 未经云端验证";
            _zhDict["ApCloudRegistered"] = "已在微软企业云端注册绑定";
            _zhDict["ApCloudFree"] = "自由无锁 (未在云端注册)";
            _zhDict["ApCloudNotVerified"] = "ZTD 微软云端未验证 (离线状态)";

            // Autopilot Log Poruke (v2.14.0 - I18N-01)
            _zhDict["ApLogScanStart"] = "正在启动微软 Autopilot 状态安全巡检...";
            _zhDict["ApLogLockedWarn"] = "警告：计算机已被企业 Autopilot 锁定: {0}";
            _zhDict["ApLogCleanSuccess"] = "验证完成：此计算机完全未被 Autopilot 绑定。";
            _zhDict["ApLogTracesWarn"] = "Autopilot 云端已释放，但本地仍存在企业配置残留。";
            _zhDict["ApLogHiddenLockCrit"] = "隐蔽风险：尽管磁盘已清空，硬件指纹在 ZTD 云端仍被锁定。";
            _zhDict["ApLogOfflineTracesWarn"] = "离线检查：发现本地企业注册残留痕迹。";
            _zhDict["ApLogOfflineCleanInfo"] = "离线检查：本地注册表干净，建议联网进行云端二次确认。";
            _zhDict["ApLogScriptNotFound"] = "未在系统中找到 Provera_autopilota.ps1 脚本。";
            _zhDict["ApLogWorkerException"] = "Autopilot 巡检后台线程异常: {0}";
            _zhDict["ApLogInvalidJson"] = "巡检脚本返回的 JSON 格式无效。";
            _zhDict["BgWuTitle"] = "后台 WINDOWS 更新";
            _zhDict["BgWuDefaultDesc"] = "阶段 2 后已启动 — 正在后台并行下载与安装系统更新";

            // Koraci i stanja koraka (WIS-TASK-181)
            _zhDict["PhaseStepsTitle"] = "步骤";
            _zhDict["StepStatusWait"] = "等待中";
            _zhDict["StepStatusRunning"] = "进行中";
            _zhDict["StepStatusOk"] = "已完成";
            _zhDict["StepStatusSkippedDone"] = "已跳过（已完成）";
            _zhDict["StepStatusSkippedUser"] = "已跳过（用户要求）";
            _zhDict["StepStatusWarn"] = "警告";
            _zhDict["StepStatusError"] = "错误";

            _zhDict["Step_P2_Identify"] = "机型识别";
            _zhDict["Step_P2_NasCache"] = "TrueNAS驱动";
            _zhDict["Step_P2_Vmd"] = "Intel VMD / RST";
            _zhDict["Step_P2_Pnp"] = "PnP驱动安装";
            _zhDict["Step_P2_Diag"] = "设备诊断";
            _zhDict["Step_P2_WuDrivers"] = "Windows Update 驱动";

            _zhDict["Step_P3_Debloat"] = "系统精简";
            _zhDict["Step_P3_Telemetry"] = "关闭遥测";
            _zhDict["Step_P3_Oem"] = "OEM品牌与区域";
            _zhDict["Step_P3_VcRedist"] = "Visual C++ 运行库";
            _zhDict["Step_P3_Scaling"] = "屏幕缩放优化";
            _zhDict["Step_P3_UsbFree"] = "USB就绪信号";

            _zhDict["Step_P4_Runtimes"] = ".NET 运行库";
            _zhDict["Step_P4_WinRAR"] = "WinRAR";
            _zhDict["Step_P4_Chrome"] = "Google Chrome";
            _zhDict["Step_P4_Acrobat"] = "Adobe Acrobat";
            _zhDict["Step_P4_Office"] = "Microsoft Office";
            _zhDict["Step_P4_BatteryBar"] = "BatteryBar";
            _zhDict["Step_P4_Shortcuts"] = "桌面快捷方式";
            _zhDict["Step_P4_PdfDefault"] = "默认PDF查看器";
            _zhDict["Step_P4_Activation"] = "Windows/Office 激活";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propName));
            }
        }
    }
}
