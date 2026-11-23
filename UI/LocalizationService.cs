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
            try { WIS.HwTest.Core.HwTestLocalization.SetLanguage(_currentLanguage); } catch { }
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
                try { WIS.HwTest.Core.HwTestLocalization.SetLanguage(normalized); } catch { }

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
            _srDict["AppTitle"] = "WIS v3.4.3 - Windows Installation Assistant";
            _srDict["CompanyBrand"] = "WIS SERVICE";
            _srDict["WorkTimeTitle"] = "VREME RADA";
            _srDict["CpuLabel"] = "⚙ CPU:";
            _srDict["RamLabel"] = "💾 RAM:";
            _srDict["DiskLabel"] = "🖴 DISK:";

            // Tabovi
            _srDict["TabOverview"] = "Pregled";
            _srDict["TabHardware"] = "Informacije";
            _srDict["TabDrivers"] = "Drajveri";
            _srDict["TabDiagnostics"] = "HW Monitor";
            _srDict["TabNetwork"] = "Mreža";
            _srDict["TabApps"] = "Programi";
            _srDict["TabLogs"] = "Dnevnik rada";
            _srDict["TabAutopilot"] = "Autopilot";
            _srDict["TabHwTest"] = "Hardverski testovi";

            // Dugmad i kontrole
            _srDict["BtnStartService"] = "▶ ZAPOČNI INSTALACIJU";
            _srDict["BtnPause"] = "⏸ Pauza";
            _srDict["BtnResume"] = "▶ Nastavi";
            _srDict["BtnSkip"] = "⏭ Preskoči";
            _srDict["BtnDelayRestart"] = "⏱ Odloži restart";
            _srDict["BtnRefreshHardware"] = "🔄 Osveži informacije";
            _srDict["BtnRerunApps"] = "🔄 Ponovi instalaciju programa";
            _srDict["AppRetryBtn"] = "↻ Retry";
            _srDict["AppRetryTooltip"] = "Ponovo pokreni instalaciju ovog programa";
            _srDict["BtnRunStressTest"] = "🔥 Pokreni 30s termalni test";
            _srDict["BtnCancelStressTest"] = "⏹ Prekini test";
            _srDict["BtnCleanAndShutdown"] = "🔄 OČISTI I RESTARTUJ RAČUNAR";
            _srDict["BtnCompletionSummary"] = "📋 Završni karton";
            _srDict["BtnPrintLabel"] = "🖨 Štampaj nalepnicu";
            _srDict["LabelNoTarget"] = "Izaberite štampač.";
            _srDict["LabelNoPrinterHint"] = "USB štampač nije pronađen. Proverite kabl i da je štampač uključen, pa kliknite „Osveži“. Ako se ni tada ne pojavi, štampač je verovatno u USB „Vendor Class“ režimu — prebacite ga u „Printer Class“ ili instalirajte Star TSP700II drajver.";
            _srDict["LabelStarNotFoundHint"] = "Star TSP700II nije prepoznat. Proverite da li je izabran pravi štampač.";
            _srDict["LabelSearching"] = "Tražim USB štampač...";
            _srDict["LabelEmulation"] = "Emulacija štampača";
            _srDict["LabelPrinterTarget"] = "Štampač";
            _srDict["TooltipPrintLabel"] = "Štampa nalepnice sa specifikacijom uređaja na USB štampaču Star TSP700II";
            _srDict["LabelWindowTitle"] = "Štampa nalepnice sa specifikacijom";
            _srDict["LabelWindowHeader"] = "🖨 Nalepnica (Star TSP700II, 80 mm)";
            _srDict["LabelCopies"] = "Broj kopija";
            _srDict["BtnPrintLabelNow"] = "🖨 Štampaj";
            _srDict["BtnRefreshLabel"] = "↻ Osveži";
            _srDict["BtnCloseLabel"] = "Zatvori";
            _srDict["LabelSending"] = "Šaljem na {0}...";
            _srDict["LabelPrinted"] = "Poslato na {0} (kopija: {1}).";
            _srDict["LabelRenderError"] = "Greška pri pripremi nalepnice: ";
            _srDict["BtnExitWithoutClean"] = "✕ Izađi bez čišćenja";
            _srDict["BtnStayInApp"] = "📋 OSTANI U PROGRAMU";
            _srDict["TooltipStayInApp"] = "Zatvara ovaj prozor i omogućava detaljan pregled tabova, hardvera i logova. Dugme za čišćenje i restart ostaje u donjoj traci.";
            _srDict["BtnRunHwTest"] = "🔧 Hardverski test";
            _srDict["TooltipRunHwTest"] = "Ručno ispitivanje kamere (QR+IR), zvuka, tastature, touchpada, touchscreen-a i USB portova";
            _srDict["HwTestFinished"] = "Hardverski test završen: {0} ({1})";

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
            _srDict["Phase5Title"] = "5. Hardverski test";
            _srDict["Phase5Subtitle"] = "Stres + Periferije";
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
            _srDict["TooltipRunStressTest"] = "Pokreće intenzivni multi-core stres test uz zaštitni prekid na 95°C / 97°C";
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
            _srDict["HwLhmActive"] = "● Nativni WMI/ACPI senzori aktivni";
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

            _srDict["RbtProgramsPrefix"] = "  •  Programi: ";
            _srDict["RbtDurationHeader"] = "TRAJANJE";
            _srDict["RbtExtraPeripheralsHeader"] = "DODATNE PERIFERIJE";
            _srDict["RbtDeviceCardHeader"] = "KARTON UREĐAJA (SERVISHUB)";
            _srDict["RbtQrPlaceholder"] = "QR kod će se pojaviti kada se otkrije ServisHub...";
            _srDict["RbtWarningsHeader"] = "UPOZORENJA SA KOJIMA JE PROCES ZAVRŠEN";
            _srDict["RbtNoWarningsTitle"] = "✓ Proces završen bez upozorenja";
            _srDict["RbtNoWarningsDesc"] = "Temperature, Autopilot, drajveri, programi i hardver su u normalnom stanju.";
            _srDict["RbtControlledReboot5s"] = "Kontrolisani međurestart za 5 sekundi...";
            _srDict["RbtRebootDelayedMsg"] = "Međurestart odložen od strane servisera — preostalo {0:D2}:{1:D2} (do {2})";
            _srDict["RbtRebootDelayedSubPhase"] = "Međurestart odložen (do {0})";
            _srDict["RbtControlledRebootCountdown"] = "Kontrolisani međurestart za {0}s... (Kliknite '+10m Restart' za odlaganje)";
            _srDict["RbtCleanupTitle"] = "ČIŠĆENJE I RESTART...";
            _srDict["RbtCleanupSummary"] = "Čišćenje servisnih tragova i restartovanje računara...";
            _srDict["HwTEmergencySkipConfirmBody"] = "UPOZORENJE: Preostali obavezni interaktivni testovi ({0}):\n{1}\n\nVanredno preskakanje je dozvoljeno ISKLJUČIVO ako uređaj ima fizički kvar ili ne može proći test:\n• Svi neurađeni testovi biće označeni kao SKIP.\n• Uređaj NEĆE dobiti status READY na ServiceHub-u (biće označen kao NEEDS_SERVICE).\n• Razlog preskakanja je obavezan i upisuje se u izveštaj.\n\nDa li ste sigurni da želite da nastavite vanredno preskakanje?";
            _srDict["HwTManualBiosBody"] = "Direktan ulazak u UEFI firmware (/r /fw) nije uspeo na ovom računaru.\n\nRačunar će se sada restartovati — pritisnite BIOS taster odmah pri paljenju:\nHP: F10 | Lenovo: F1 | Dell: F2";
            _srDict["HwTPendingInteractiveSubPhase"] = "Čekaju obavezni interaktivni testovi ({0})...";
            _srDict["HwTAutoDonePendingInteractive"] = "⚠️ Automatski proces je završen — preostalo {0} obaveznih interaktivnih testova u Fazi 5.";
            _srDict["HwTModuleUnavailable"] = "Ugrađeni modul za hardversko testiranje nije dostupan.";
            _srDict["HwTTitle"] = "WIS Hardverski testovi";
            _srDict["HwTRemainingInteractiveSubPhase"] = "Preostalo interaktivnih testova: {0}";
            _srDict["HwTTestingStoppedRemaining"] = "⚠️ Testiranje prekinuto — preostalo još {0} obaveznih interaktivnih testova.";
            _srDict["HwTEmergencySkipDialogTitle"] = "Obavezan razlog vanrednog preskakanja (Faza 5)";
            _srDict["HwTEmergencySkipHeader"] = "⚠️ VANREDNO PRESKAKANJE OBAVEZNIH TESTOVA";
            _srDict["HwTEmergencySkipDesc"] = "Unesite jasan servisni razlog zašto se preostali interaktivni testovi preskaču (npr. neispravan ekran, kvar tastature, čeka zamenu dela). Uređaj će na ServiceHub-u biti označen kao NEEDS_SERVICE:";
            _srDict["HwTEmergencySkipValidation"] = "Obavezan unos (minimalno 5 karaktera).";
            _srDict["HwTCancel"] = "Odustani";
            _srDict["HwTEmergencySkipConfirmBtn"] = "Potvrdi vanredno preskakanje";
            _srDict["HwTEmergencySkipConfirmTitle"] = "WIS — Vanredno preskakanje Faze 5";
            _srDict["HwTEmergencySkipNotReady"] = "Vanredno preskakanje nije moglo biti izvršeno jer test modul nije spreman.";
            _srDict["HwTErrorTitle"] = "WIS Greška";
            _srDict["HwTBacklitConfirmed"] = "Potvrđeno u Fazi 5 (Test tastature)";
            _srDict["HwTBacklitNone"] = "Model nema pozadinsko osvetljenje (potvrđeno u Fazi 5)";
            _srDict["HwTBacklitFaulty"] = "Svetleća tastatura prisutna (neispravna u Fazi 5)";
            _srDict["HwTBiosRestartSubPhase"] = "Restart u UEFI BIOS ({0})...";
            _srDict["HwTBiosRestartLiveStatus"] = "Restartujem u UEFI BIOS — WIS nastavlja automatski po povratku...";
            _srDict["HwTManualBiosTitle"] = "Ručni ulazak u BIOS";
            _srDict["HwTNotStartedWarning"] = "⚠️ Hardverski testovi nisu pokrenuti! Kliknite 'Ponovi Fazu 5' ili pokrenite testove pre završetka.";
            _srDict["HwTPhase5TimeoutWarning"] = "⚠️ Faza 5 zaustavljena (timeout automatskog testa). Ponovite Fazu 5 ili dovršite interaktivne testove.";
            _srDict["XamlPendingBannerTitle"] = "⚠️ OBAVEZNI INTERAKTIVNI TESTOVI NISU ZAVRŠENI";
            _srDict["XamlPendingBannerDetail"] = "Automatski deo je završen. Završite preostale testove da bi se kreirao izveštaj i završni karton.";
            _srDict["XamlBtnBannerResumeTests"] = "▶ Nastavi testove";
            _srDict["XamlBtnEmergencySkip"] = "Vanredno preskoči...";
            _srDict["XamlOverviewDeviceInfoTitle"] = "INFORMACIJE O UREĐAJU";
            _srDict["XamlOverviewManufacturerModel"] = "PROIZVOĐAČ / MODEL";
            _srDict["XamlOverviewDiskLabel"] = "Disk";
            _srDict["XamlOverviewScreenResolution"] = "Rezolucija ekrana";
            _srDict["XamlOverviewBatteryWear"] = "Battery wear";
            _srDict["XamlOverviewHwMonitorTitle"] = "HARDVER MONITOR";
            _srDict["XamlOverviewLive"] = "● Uživo";
            _srDict["XamlOverviewCpuLoad"] = "CPU opterećenje";
            _srDict["XamlOverviewRamUsage"] = "RAM zauzeće";
            _srDict["XamlOverviewCpuTemp"] = "CPU TEMP";
            _srDict["XamlOverviewGpuTemp"] = "GPU TEMP";
            _srDict["XamlOverviewSsdTemp"] = "SSD TEMP";
            _srDict["XamlOverviewMbTemp"] = "MB TEMP";
            _srDict["XamlOverviewAlertsTitle"] = "UPOZORENJA";
            _srDict["XamlOverviewNoAlerts"] = "✓ Nema aktivnih upozorenja";
            _srDict["XamlOverviewNoAlertsDesc"] = "Temperature, Autopilot, drajveri i programi su u normalnom stanju.";
            _srDict["XamlGlobalProgressTitle"] = "UKUPAN NAPREDAK PROCESA";
            _srDict["XamlElapsedPrefix"] = "Traje ";
            _srDict["XamlEtaPrefix"] = "ETA ";
            _srDict["XamlActiveSteps"] = "AKTIVNI KORACI";
            _srDict["XamlHwMonitorDetailTitle"] = "DETALJAN HARDVER MONITOR";
            _srDict["XamlHwMonitorDiskSsd"] = "DISK / SSD";
            _srDict["XamlHwMonitorPower"] = "NAPAJANJE";
            _srDict["XamlHwMonitorSensorsTitle"] = "DETALJNI SENZORI (MIN / TRENUTNO / MAX)";
            _srDict["XamlHwMonitorSensorParam"] = "Senzor / Parametar";
            _srDict["XamlHwMonitorCurrent"] = "Trenutno";
            _srDict["XamlHwMonitorMin"] = "Min";
            _srDict["XamlHwMonitorMax"] = "Max";
            _srDict["XamlHwMonitorSubsystemsTitle"] = "PODSISTEMI — NAPAJANJE, SSD S.M.A.R.T. I MEMORIJA";
            _srDict["XamlHwMonitorMemoryCommit"] = "Memorija (RAM / Commit):";
            _srDict["XamlHwMonitorSsdSmartTbw"] = "SSD S.M.A.R.T. / TBW:";
            _srDict["XamlHwMonitorReallocWarnings"] = "Realocirani sektori / Upozorenja:";
            _srDict["XamlHwMonitorBatteryHealthCycles"] = "Baterija zdravlje / Ciklusi:";
            _srDict["XamlHwMonitorNetThroughput"] = "Mrežni protok:";
            _srDict["XamlFooterCopyright"] = "Razvija i održava IG-Systems. Sva prava zadržana";
            _srDict["XamlBtnFailedPhaseRecheck"] = "Proveri ponovo i nastavi";
            _srDict["XamlBtnFailedPhaseRetry"] = "Ponovi Fazu";
            _srDict["XamlBtnFailedPhaseSkip"] = "Preskoči na sledeću fazu";
            _srDict["XamlPendingOverlayBadge"] = "OBAVEZNA SERVISNA AKCIJA — FAZA 5";
            _srDict["XamlPendingOverlayTitle"] = "⚠️ AUTOMATSKI PROCES JE ZAVRŠEN — OBAVEZNI INTERAKTIVNI TESTOVI";
            _srDict["XamlPendingOverlaySubtitle"] = "Automatski deo procesa (drajveri, optimizacije, programi i 30s stres test) je završen. Krajnji izveštaj za ServiceHub i Završni karton neće biti kreirani dok se svi obavezni interaktivni testovi hardvera ne završe.";
            _srDict["XamlPendingOverlayCount"] = "Preostali obavezni testovi (0):";
            _srDict["XamlBtnPendingStartTests"] = "▶ Pokreni interaktivne testove";
            _srDict["XamlBtnPendingBrowseTabs"] = "Pregledaj tabove";
            _srDict["WinMsgHwTestModuleMissing"] = "Modul za hardversko testiranje (WIS.HwTest.exe) nije pronađen!";
            _srDict["WinMsgTitleError"] = "WIS Greška";
            _srDict["WinHwTestIncompleteSkip"] = "NEPOTPUNO (SKIP)";
            _srDict["WinDevInfoTitle"] = "INFORMACIJE O UREĐAJU";
            _srDict["WinDevInfoModelLabel"] = "PROIZVOĐAČ / MODEL";
            _srDict["WinPeripheralsTitle"] = "DODATNE PERIFERIJE";
            _srDict["WinHwMonTitle"] = "HARDVER MONITOR";
            _srDict["WinHwMonLive"] = "● Uživo";
            _srDict["WinHwMonCpuLoad"] = "CPU opterećenje";
            _srDict["WinHwMonRamUsage"] = "RAM zauzeće";
            _srDict["WinWarningsTitle"] = "UPOZORENJA";
            _srDict["WinWarningsNone"] = "✓ Nema aktivnih upozorenja";
            _srDict["WinWarningsNormal"] = "Temperature, Autopilot, drajveri i programi su u normalnom stanju.";
            _srDict["WinServiceHubCardTitle"] = "KARTON UREĐAJA (SERVISHUB)";
            _srDict["WinQrPlaceholder"] = "QR kod će se pojaviti kada se otkrije ServisHub...";
            _srDict["WinRecoverySkipBtn"] = "Preskoči na sledeću fazu";
            _srDict["WinRecoveryRetryPhaseFmt"] = "↻ Ponovi Fazu {0}";
            _srDict["WinRecoveryRetryPhaseDefault"] = "Ponovi Fazu";
            _srDict["WinRecoverySkipTooltipFmt"] = "Preskoči Fazu {0} i nastavi na Fazu {1}";
            _srDict["WinPendingBannerTitle"] = "⚠️ OBAVEZNI INTERAKTIVNI TESTOVI NISU ZAVRŠENI";
            _srDict["WinPendingBannerDetail"] = "Automatski deo je završen. Završite preostale testove da bi se kreirao izveštaj i završni karton.";
            _srDict["WinPendingBtnResume"] = "▶ Nastavi testove";
            _srDict["WinPendingBtnEmergencySkip"] = "Vanredno preskoči...";
            _srDict["WinPendingOverlayBadge"] = "OBAVEZNA SERVISNA AKCIJA — FAZA 5";
            _srDict["WinPendingOverlayTitle"] = "⚠️ AUTOMATSKI PROCES JE ZAVRŠEN — OBAVEZNI INTERAKTIVNI TESTOVI";
            _srDict["WinPendingOverlaySubtitle"] = "Automatski deo procesa (drajveri, optimizacije, programi i 30s stres test) je završen. Krajnji izveštaj za ServiceHub i Završni karton neće biti kreirani dok se svi obavezni interaktivni testovi hardvera ne završe.";
            _srDict["WinPendingOverlayCountTitle"] = "Preostali obavezni testovi (0):";
            _srDict["WinPendingOverlayCountFmt"] = "Preostalo {0} obaveznih interaktivnih testova:";
            _srDict["WinPendingBtnStartTests"] = "▶ Pokreni interaktivne testove";
            _srDict["WinPendingBtnBrowseTabs"] = "Pregledaj tabove";
            _srDict["WinPendingBannerTitleFmt"] = "⚠️ OBAVEZNI INTERAKTIVNI TESTOVI NISU ZAVRŠENI (preostalo: {0})";
            _srDict["WinPendingBannerDetailFmt"] = "Preostalo: {0} — Krajnji izveštaj i Završni karton se kreiraju tek nakon završetka testova.";
            _srDict["WinRcCheckingApps"] = "Proveravam prisustvo ključnih aplikacija na disku...";
            _srDict["WinRcStillMissingFmt"] = "Još uvek nedostaju: {0}. Pokrenite pojedinačni Retry na kartici Programi.";
            _srDict["WinRcMissingMsgFmt"] = "Sledeći ključni programi još uvek nisu pronađeni na disku:\n\n{0}\n\nPokrenite pojedinačni ↻ Retry na kartici 'Programi' ili 'Ponovi Fazu 4'.";
            _srDict["WinRcMissingMsgTitle"] = "WIS Reality Check — Nedostaju programi";
            _srDict["WinSkipMissingAppsHdr"] = "\n\nNedostaju sledeći programi na disku:\n- ";
            _srDict["WinSkipPhaseTargetFmt"] = "Fazu {0}";
            _srDict["WinSkipTargetFinish"] = "završetak servisa";
            _srDict["WinSkipConfirmMsgFmt"] = "Da li ste sigurni da želite da preskočite neuspelu Fazu {0} i pređete na {1}?{2}";
            _srDict["WinSkipConfirmTitle"] = "WIS Servis — Potvrda preskakanja faze";
            _srDict["WinGlobalProgressTitle"] = "UKUPAN NAPREDAK PROCESA";
            _srDict["WinTimingElapsedPrefix"] = "Traje ";
            _srDict["WinTimingEtaPrefix"] = "ETA ";
            _srDict["WinActiveStepsTitle"] = "AKTIVNI KORACI";
            _srDict["WinFooterCopyright"] = "Razvija i održava IG-Systems. Sva prava zadržana";
            _srDict["WinHwMonHeader"] = "HW MONITOR  •  SISTEMSKA TELEMETRIJA UŽIVO";
            _srDict["WinHwMonBtnReset"] = "↺ Reset Min/Max";
            _srDict["WinHwMonBtnStartStress"] = "▶ Start Stres";
            _srDict["WinHwMonBtnStopStress"] = "⏹ Stop Stres";
            _srDict["WinHwMonKpiCpu"] = "CPU / PROCESOR";
            _srDict["WinHwMonKpiRam"] = "RAM MEMORIJA";
            _srDict["WinHwMonKpiGpu"] = "GRAFIKA (GPU)";
            _srDict["WinHwMonKpiDisk"] = "DISK / SSD I/O";
            _srDict["WinHwMonKpiPower"] = "NAPAJANJE / BATERIJA";
            _srDict["WinHwMonGraphTitle"] = "TERMALNA KRIVA & CPU OPTEREĆENJE (5 MIN / 1 Hz)";
            _srDict["WinHwMonSensorsTitle"] = "DETALJNI SENZORI (MIN / TRENUTNO / MAX)";
            _srDict["WinHwMonColMax"] = "MAX";
            _srDict["WinHwMonColMin"] = "MIN";
            _srDict["WinHwMonColCurrent"] = "TRENUTNO";
            _srDict["WinHwMonColSensor"] = "SENZOR";
            _srDict["WinHwMonClocksTitle"] = "TAKTOVI PROCESORSKIH JEZGARA";
            _srDict["WinHwMonColCore"] = "JEZGRO";
            _srDict["WinHwMonSubsysTitle"] = "PODSISTEMI & DIJAGNOSTIKA";
            _srDict["WinHwMonSubNet"] = "Mreža (RX / TX)";
            _srDict["WinHwMonSubDisk"] = "Disk I/O";
            _srDict["WinHwMonSubPower"] = "Napajanje";
            _srDict["WinHwMonSubVolt"] = "Režim / Napon";
            _srDict["WinHwMonSubThermal"] = "Term. ocena";
            _srDict["WinMasterPhaseStatusFmt"] = "Master Faza {0}";
            _srDict["WinPhase6BlockedSub"] = "Čekaju obavezni interaktivni testovi...";
            _srDict["WinPhase6BlockedLive"] = "⚠️ Faza 6 je blokirana dok se ne završe svi obavezni interaktivni testovi hardvera.";
            _srDict["WinPhase1Name"] = "Faza 1: Mreža i povezivanje";
            _srDict["WinPhase1Sub"] = "Povezivanje na servisnu mrežu...";
            _srDict["WinPhase1Live"] = "Konfigurisanje mrežnih interfejsa i rutiranja...";
            _srDict["WinPhase2Name"] = "Faza 2: Drajveri i zakrpe";
            _srDict["WinPhase2Sub"] = "Instalacija PnP drajvera...";
            _srDict["WinPhase2Live"] = "Skeniranje i instalacija nedostajućih drajvera...";
            _srDict["WinPhase3Name"] = "Faza 3: Optimizacija i debloat";
            _srDict["WinPhase3Sub"] = "Sistemske optimizacije...";
            _srDict["WinPhase3Live"] = "Primenjivanje debloat i brending skripti...";
            _srDict["WinPhase4Name"] = "Faza 4: Instalacija programa";
            _srDict["WinPhase4Sub"] = "Instalacija programskih paketa...";
            _srDict["WinPhase4Live"] = "Instalacija i verifikacija servisnih programa...";
            _srDict["WinPhase5Name"] = "Faza 5: Hardverski test (Stres + Periferije)";
            _srDict["WinPhase5Sub"] = "Automatska provera, 30s stres i testovi periferija...";
            _srDict["WinPhase5Live"] = "Izvršavanje integrisanih hardverskih testova u tabu Hardverski testovi...";
            _srDict["WinPhase6Name"] = "Faza 6: Završetak i rezime";
            _srDict["WinPhase6Sub"] = "Finalizacija i dijagnostika...";
            _srDict["WinPhase6Live"] = "Generisanje servisnog izveštaja i verifikacija...";
            _srDict["WinPhaseDefaultNameFmt"] = "Faza {0}";
            _srDict["WinPhaseDefaultSub"] = "Izvršavanje faze...";
            _srDict["WinPhaseDefaultLiveFmt"] = "Rad na Fazi {0}...";
            _srDict["WinPhaseScriptMissingFmt"] = "Greška: {0} nedostaje na sistemu!";
            _srDict["WinPhaseTimeoutLiveFmt"] = "Faza {0} prekoračila timeout. Koristite traku za oporavak dole ('Ponovi Fazu {0}' ili 'Preskoči na sledeću fazu').";
            _srDict["WinPhaseErrorLiveFmt"] = "Greška u Fazi {0} (Kod {1}). Koristite traku za oporavak dole ('Proveri ponovo i nastavi', 'Ponovi Fazu {0}' ili 'Preskoči na sledeću fazu').";
            _srDict["WinStressAutoStatus"] = "Automatski 30s stres test procesora (Faza 5)...";
            _srDict["HwKbUnconfirmed"] = "Nije potvrđeno (provera u Fazi 5)";
            _srDict["Step_P5_AutoDiag"] = "Automatska provera sistema";
            _srDict["Step_P5_Stress"] = "Stres test (Disk, RAM, CPU 30s, GPU 3D)";
            _srDict["Step_P5_Peripherals"] = "Interaktivni testovi periferija";
            _srDict["Step_P6_WuSecondPass"] = "Dvoprolazni Update i WU barijera";
            _srDict["Step_P6_Report"] = "Servisni karton i flotni izveštaj";
            _srDict["Step_P6_Security"] = "Vraćanje bezbednosnih polisa i Anti-Leak";
            _srDict["Step_P6_Finalize"] = "Završna verifikacija za isporuku";
            _srDict["MdlPhase2Title"] = "1. Update";
            _srDict["MdlPhase2Subtitle"] = "Drajveri i zakrpe";
            _srDict["MdlPhase3Title"] = "2. Optimizacija";
            _srDict["MdlPhase3Subtitle"] = "Debloat i brendiranje";
            _srDict["MdlPhase4Title"] = "3. Programi";
            _srDict["MdlPhase4Subtitle"] = "Paketi i OEM licenca";
            _srDict["MdlPhase5Title"] = "4. Hardverski test";
            _srDict["MdlPhase5Subtitle"] = "Stres + Periferije";
            _srDict["MdlPhase6Title"] = "5. Završetak";
            _srDict["MdlPhase6Subtitle"] = "Dijagnostika i rezime";
            _srDict["MdlDefaultBenchmarkSource"] = "Podrazumevani WIS v3 benchmark";
            _srDict["MdlTimingTooltip"] = "Trajanje i benchmark ETA procena";
            _srDict["MdlInProgress"] = "U toku...";
            _srDict["MdlAlertCritBadge"] = "KRITIČNO";
            _srDict["MdlAlertWarnBadge"] = "UPOZORENJE";
            _srDict["MdlAlertInfoBadge"] = "INFO";
            _srDict["MdlAppDetailQueue"] = "U redu čekanja";
            _srDict["MdlAppDetailSkippedDone"] = "Već instalirano";
            _srDict["MdlAppDetailSkippedUser"] = "Preskočeno na zahtev servisera";
            _srDict["MdlAppDetailOk"] = "Instalacija uspešno verifikovana";
            _srDict["MdlAppDetailRunning"] = "Instalacija paketa u toku...";
            _srDict["MdlAppDetailWarn"] = "Instalacija nije potvrđena";
            _srDict["MdlAppDetailError"] = "Greška tokom instalacije";
            _srDict["MdlAppNotApplicableDesktop"] = "Nije primenjivo na desktopu";
            _srDict["MdlAppNameLicense"] = "OEM Licenca";
            _srDict["MdlAppNameShortcuts"] = "Servisne Prečice";
            _srDict["MdlAppsSummary"] = "{0} od {1} instalirano • {2} u toku • {3} preskočeno • {4} grešaka";
            _srDict["MdlAppStatusOk"] = "✓ OK";
            _srDict["MdlAppStatusRunning"] = "⚙ RAD";
            _srDict["MdlAppStatusWarn"] = "⚠ UPOZ.";
            _srDict["MdlAppStatusError"] = "✗ GREŠKA";
            _srDict["MdlAppStatusSkippedDone"] = "» PRESKOČENO";
            _srDict["MdlAppStatusSkipped"] = "⏭ PRESKOČENO";
            _srDict["MdlAppStatusWait"] = "⏳ ČEKA";
            _srDict["MdlUnknownDevice"] = "Nepoznat uređaj";
            _srDict["MdlUnknown"] = "Nepoznato";
            _srDict["MdlDriverError"] = "Greška drajvera";
            _srDict["MdlDrvLoadingSummary"] = "Učitavanje statusa drajvera...";
            _srDict["MdlLoading"] = "Učitavanje...";
            _srDict["MdlChecking"] = "Provera...";
            _srDict["MdlDetecting"] = "Detekcija...";
            _srDict["MdlReading"] = "Očitavanje...";
            _srDict["MdlConnecting"] = "Povezivanje...";
            _srDict["MdlNotStarted"] = "Nije pokrenuto";
            _srDict["MdlNotAvailable"] = "Nije dostupno";
            _srDict["MdlNotConnected"] = "Nije povezano";
            _srDict["MdlNotLaunched"] = "Nije pokrenut";
            _srDict["MdlApNoneNotAssigned"] = "Nema (Nije dodeljen)";
            _srDict["MdlApNotJoined"] = "Nije pridružen (NO)";
            _srDict["MdlApNotEnrolled"] = "Nije upisan (NO)";
            _srDict["MdlApCloudWaiting"] = "Čeka se provera...";
            _srDict["MdlApCloudConnecting"] = "Povezivanje sa ztd.dds.microsoft.com...";
            _srDict["MdlHwLoadingCpu"] = "Učitavanje CPU...";
            _srDict["MdlHwLoadingRam"] = "Učitavanje RAM...";
            _srDict["MdlHwDetectingSlots"] = "Detekcija slotova...";
            _srDict["MdlHwLoadingStorage"] = "Učitavanje SSD/Diska...";
            _srDict["MdlHwStorageHoursNa"] = "N/A (Zahteva S.M.A.R.T.)";
            _srDict["MdlHwStorageTempNa"] = "N/A (Senzor nedostupan)";
            _srDict["MdlHwLoadingGpu"] = "Učitavanje GPU...";
            _srDict["MdlHwDetectingDisplay"] = "Detekcija displeja...";
            _srDict["MdlHwLoadingMb"] = "Učitavanje ploče...";
            _srDict["MdlHwDetectingIr"] = "Detekcija IR...";
            _srDict["MdlHwDetectingSensor"] = "Detekcija senzora...";
            _srDict["MdlRamSoldered"] = "Integrisan (LPDDR / Soldered)";
            _srDict["MdlPeriphWwanFallback"] = "4G LTE / 5G kartica";
            _srDict["MdlPeriphEsimFallback"] = "Podržan eSIM (eUICC)";
            _srDict["MdlPeriphBluetooth"] = "Bluetooth";
            _srDict["MdlPeriphBacklitKb"] = "Svetleća tastatura";
            _srDict["MdlPeriphIrCam"] = "IR kamera (Hello)";
            _srDict["MdlPeriphFingerprint"] = "Fingerprint reader";
            _srDict["MdlPeriphTouchscreen"] = "Touchscreen";
            _srDict["MdlPeriphSmartCard"] = "SmartCard čitač";
            _srDict["MdlPeriphSmartCardDetail"] = "Detektovan čitač pametnih kartica";
            _srDict["MdlPeriphNfc"] = "NFC";
            _srDict["MdlPeriphNfcDetail"] = "Detektovan NFC / Proximity uređaj";
            _srDict["MdlPeriphEmptyNone"] = "Nema detektovanih dodatnih periferija";
            _srDict["MdlPeriphEmptyDetecting"] = "Detekcija periferija...";
            _srDict["MdlLoadingConfig"] = "Učitavanje konfiguracije...";
            _srDict["MdlDetectingCpu"] = "Detekcija CPU-a...";
            _srDict["MdlDetectingRam"] = "Detekcija RAM-a...";
            _srDict["MdlDetectingDisk"] = "Detekcija SSD/NVMe diska...";
            _srDict["MdlHubConnecting"] = "Povezivanje sa ServisHub serverom...";
            _srDict["MdlQrScanPrompt"] = "Skeniraj telefonom za karton uređaja";
            _srDict["MdlQrUnavailable"] = "QR generator nedostupan";
            _srDict["MdlInitPhaseName"] = "Faza 1: Mreža i povezivanje";
            _srDict["MdlInitSubPhase"] = "Inicijalizacija mrežnih servisa...";
            _srDict["MdlInitLiveStatus"] = "Sistem priprema konfiguraciju servisa...";
            _srDict["MdlInitGlobalSummary"] = "0 / 28 koraka";
            _srDict["MdlUsbBannerText"] = "✓ USB FLEŠ DISK JE BEZBEDNO ODVOJEN (Možete slobodno izvaditi fleš disk)";
            _srDict["MdlBgWuCompleted"] = "✓ ZAVRŠENO";
            _srDict["MdlBgWuSkipped"] = "⏭ PRESKOČENO";
            _srDict["MdlBgWuWaitingMsi"] = "⏳ ČEKA MSI";
            _srDict["MdlBgWuDownloading"] = "⬇ PREUZIMANJE";
            _srDict["MdlBgWuInstalling"] = "⚙ INSTALACIJA";
            _srDict["MdlBgWuHarvesting"] = "⬆ NAS KEŠ";
            _srDict["MdlBgWuWarn"] = "⚠ UPOZORENJE";
            _srDict["MdlBgWuInProgress"] = "🔄 U TOKU";
            _srDict["MdlDiskWearInit"] = "100% Zdravlje";
            _srDict["MdlDiskWearRemaining"] = "{0}% Preostalo zdravlje";
            _srDict["MdlSmartHealthyInit"] = "Healthy (Ispravan)";
            _srDict["MdlBatteryHealthInit"] = "Fiksno napajanje (Desktop)";
            _srDict["MdlBatteryStatusInit"] = "AC Napajanje / Desktop";
            _srDict["MdlBatteryFactoryCap"] = "{0}% Fabrički kapacitet";
            _srDict["MdlDeviceTypeLaptop"] = "Laptop računar";
            _srDict["MdlDeviceTypeDesktop"] = "Desktop (Fiksno napajanje)";
            _srDict["MdlPwrReadingTitle"] = "Očitavanje napajanja...";
            _srDict["MdlPwrReadingTooltip"] = "Očitavanje stanja baterije i punjača...";
            _srDict["MdlPwrFixedAc"] = "Fiksno AC napajanje";
            _srDict["MdlPwrDesktopAc"] = "Desktop (AC)";
            _srDict["MdlPwrDesktopTooltip"] = "Desktop računar bez sistemske baterije (stalno AC napajanje).";
            _srDict["MdlPwrChargerConnected"] = "Punjač priključen • {0}%";
            _srDict["MdlPwrFullAc"] = "Puna (AC)";
            _srDict["MdlPwrChargingAc"] = "Punjenje (AC)";
            _srDict["MdlPwrAc"] = "AC napajanje";
            _srDict["MdlPwrOnBattery"] = "Na bateriji • {0}%";
            _srDict["MdlPwrDischargingDc"] = "Pražnjenje (DC)";
            _srDict["MdlPwrTtPower"] = " | Snaga: {0:0.0} W";
            _srDict["MdlPwrTtCurrent"] = " | Struja: {0:0.00} A";
            _srDict["MdlPwrTtVoltage"] = " | Napon baterije: {0:0.00} V";
            _srDict["MdlPwrTtRemaining"] = " | Preostalo: {0}";
            _srDict["MdlDetectingGpu"] = "Detekcija GPU-a...";
            _srDict["MdlDetectingMb"] = "Detekcija matične ploče...";
            _srDict["MdlDetectingBios"] = "Detekcija BIOS-a...";
            _srDict["MdlCheckingTpm"] = "Provera TPM čipa...";
            _srDict["MdlPnpAllOkInit"] = "✓ Svi sistemski drajveri su ispravno instalirani";
            _srDict["MdlPnpAttentionCount"] = "⚠ {0} uređaja zahteva pažnju";
            _srDict["MdlPnpAllOk"] = "✓ Svi sistemski drajveri su ispravno instalirani (0 problema)";
            _srDict["MdlSensorsActive"] = "● Senzori aktivni";
            _srDict["MdlLegendThrottleNa"] = "● Throttling: Nije dostupno";
            _srDict["MdlKpiTempReading"] = "Temp: Očitavanje...";
            _srDict["MdlKpiRamFreeNa"] = "Slobodno: N/A";
            _srDict["MdlKpiDiskIoReading"] = "R/W: Očitavanje...";
            _srDict["MdlKpiPowerVoltageReading"] = "Napon: Očitavanje...";
            _srDict["MdlKpiPowerSubInit"] = "Zdravlje baterije";
            _srDict["MdlKpiTempMinMax"] = "Temp {0:0}°C • Min {1:0}° / Max {2:0}°";
            _srDict["MdlKpiPowerHealthCycles"] = "Zdravlje {0} • Ciklusi: {1}";
            _srDict["MdlSensorHdrTemp"] = "TEMPERATURA";
            _srDict["MdlSensorTempCpu"] = "CPU Package / ACPI";
            _srDict["MdlSensorTempGpu"] = "GPU Jezgro";
            _srDict["MdlSensorTempMb"] = "Matična ploča (MB)";
            _srDict["MdlSensorTempSsd"] = "NVMe / SSD Disk";
            _srDict["MdlSensorHdrLoad"] = "OPTEREĆENJE I STABILNOST";
            _srDict["MdlSensorLoadCpu"] = "CPU Ukupno opterećenje";
            _srDict["MdlSensorLoadRam"] = "RAM Zauzeće memorije";
            _srDict["MdlSensorLoadThrottle"] = "CPU Thermal Throttling";
            _srDict["MdlSensorIoDisk"] = "Disk I/O aktivnost";
            _srDict["MdlSensorIoNet"] = "Mrežni protok (RX + TX)";
            _srDict["MdlSensorHdrPower"] = "HLAĐENJE I NAPAJANJE";
            _srDict["MdlSensorFanRpm"] = "Ventilator (Fan RPM)";
            _srDict["MdlSensorPwrWatts"] = "Snaga baterije / punjača";
            _srDict["MdlSensorPwrAmps"] = "Struja (A)";
            _srDict["MdlSensorPwrVolts"] = "Napon baterije (V)";
            _srDict["MdlStressReady"] = "Spremno za testiranje hlađenja i stabilnosti";
            _srDict["MdlThermalNotTested"] = "Nije testirano";
            _srDict["MdlThermalNotTestedDesc"] = "Pokrenite test za procenu termalne paste i hladnjaka.";
            _srDict["MdlThermalRecStart"] = "Preporuka: Kliknite [▶ Start Stres] za dijagnozu.";
            _srDict["MdlNetDetectingNic"] = "Detekcija mrežnih kartica...";
            _srDict["MdlNetDetecting"] = "Detekcija mreže...";
            _srDict["MdlNetDistSource"] = "Lokalni skladišni keš (NAS / USB)";
            _srDict["MdlNetNasChecking"] = "Provera veze ka NAS-u...";
            _srDict["MdlAutoStartMsg"] = "Automatski start za 15s... (Kliknite za trenutni start ili prebacite tab za pauzu)";
            _srDict["MdlIpcConnected"] = "IPC Povezan";
            _srDict["MdlPhasesHideSteps"] = "▲ Sakrij korake";
            _srDict["MdlPhasesShowSteps"] = "▼ Prikaži korake";
            _srDict["MdlPhasesHideTooltip"] = "Skupi prikaz koraka faza";
            _srDict["MdlPhasesShowTooltip"] = "Prikaži sve korake faza";
            _srDict["MdlCoolingCritical"] = "Hlađenje: KRITIČNO — predaja je blokirana.";
            _srDict["MdlCoolingUnknown"] = "Hlađenje: NEPROVERENO — obavezna ručna provera ventilatora i hladnjaka.";
            _srDict["MdlCoolingPass"] = "Hlađenje: test završen bez termalnog upozorenja.";
            _srDict["MdlCoolingWarn"] = "Hlađenje: UPOZORENJE — potrebna servisna provera.";
            _srDict["MdlCoolingFail"] = "Hlađenje: test nije uspešno završen — potrebna ručna provera.";
            _srDict["MdlShutdownCountdown"] = "Automatsko gašenje računara za: {0}s (WIS Service)";
            _srDict["MdlReadyForDelivery"] = "Računar je spreman za isporuku.";
            _srDict["MdlProgressDoneSteps"] = "{0} / {0} koraka • Završeno";
            _srDict["MdlProgressDone100"] = "100% • Završeno";
            _srDict["MdlProgressReadySteps"] = "0 / {0} koraka • Spremno";
            _srDict["MdlProgressInProgressSteps"] = "{0} / {1} koraka • {2}";
            _srDict["MdlBarReadyLabel"] = "SPREMNO";
            _srDict["MdlBarReadyTitle"] = "Čekanje na početak instalacije";
            _srDict["MdlBarReadyDetail"] = "Kliknite 'ZAPOČNI INSTALACIJU' ili sačekajte automatski start.";
            _srDict["MdlBarDoneLabel"] = "ZAVRŠENO";
            _srDict["MdlBarDoneTitle"] = "Svi koraci su uspešno završeni";
            _srDict["MdlBarDoneDetail"] = "Računar je pripremljen i spreman za predaju.";
            _srDict["MdlBarStepRunning"] = "Izvršavanje koraka u toku...";
            _srDict["MdlBarPhaseFallback"] = "FAZA";
            _srDict["MdlBarInProgressFallback"] = "U TOKU";
            _srDict["MdlBarBgWuLabel"] = "POZADINSKI WU";
            _srDict["MdlBarBgWuTitle"] = "Pozadinski Windows Update";
            _srDict["MdlBarDriversLabel"] = "DRAJVERI";
            _srDict["MdlBarDriversTitle"] = "Grupna instalacija drajvera";
            _srDict["MdlBarDriverLabel"] = "DRAJVER";
            _srDict["MdlBarDriverTitle"] = "Instalacija drajvera";
            _srDict["MdlCompletionBadgeOk"] = "✓ SERVIS USPEŠNO ZAVRŠEN";
            _srDict["MdlCompletionBadgeCrit"] = "⚠ SERVIS ZAVRŠEN SA KRITIČNIM UPOZORENJIMA";
            _srDict["MdlCompletionBadgeWarn"] = "⚠ SERVIS ZAVRŠEN SA UPOZORENJIMA";
            _srDict["MdlCompletionAlertsOk"] = "Proces je završen čisto — uređaj je spreman za predaju klijentu.";
            _srDict["MdlCompletionAlertsCrit"] = "Završeno sa {0} aktivnih upozorenja (uključujući kritična) — kliknite na stavku za pregled.";
            _srDict["MdlCompletionAlertsWarn"] = "Završeno sa {0} upozorenja — proverite stavke pre predaje klijentu.";
            _srDict["MdlAlertThermalEmergency"] = "Emergency Abort aktiviran";
            _srDict["MdlAlertThermalCritDetail"] = "Detektovana kritična temperatura hardvera";
            _srDict["MdlAlertThermalCritTitle"] = "Računar se pregrejava";
            _srDict["MdlAlertThermalWarnDetail"] = "Povišena radna temperatura";
            _srDict["MdlAlertThermalWarnTitle"] = "Povišena temperatura / trotlovanje";
            _srDict["MdlAlertApLockedTitle"] = "Detektovan Windows Autopilot";
            _srDict["MdlAlertApMdmTitle"] = "Detektovan Autopilot / MDM";
            _srDict["MdlAlertApCheckTab"] = "Proverite Autopilot karticu za detalje";
            _srDict["MdlAlertDrvOneMissing"] = "1 uređaj bez drajvera";
            _srDict["MdlAlertDrvManyErrors"] = "{0} uređaja sa greškom";
            _srDict["MdlAlertDrvStepDetail"] = "Korak instalacije drajvera prijavio je grešku ili upozorenje.";
            _srDict["MdlAlertDrvTitle"] = "Nisu instalirani svi drajveri";
            _srDict["MdlAlertAppsFailedPrefix"] = "Neuspelo: ";
            _srDict["MdlAlertAppsTitle"] = "Neki program nije instaliran";
            _srDict["MdlAlertHwTestFallback"] = "Proverite rezultate hardverskih testova.";
            _srDict["MdlAlertHwTestTitle"] = "Hardverski test prijavio problem";
            _srDict["MdlAlertWeakChargerTitle"] = "Slab punjač — baterija se prazni";
            _srDict["MdlAlertWeakChargerDetail"] = "Punjač priključen, a baterija gubi -{0:0.0} W ({1}%)";
            _srDict["MdlAlertLowBatTitle"] = "Nizak nivo baterije";
            _srDict["MdlAlertLowBatDetail"] = "Preostalo samo {0}% — priključite punjač!";
            _srDict["MdlAlertBatWearTitle"] = "Visoka istrošenost baterije";
            _srDict["MdlAlertSmartDetail"] = "Zdravlje: {0}% • SMART: {1}";
            _srDict["MdlAlertSmartTitle"] = "Upozorenje za zdravlje diska (SMART)";
            _srDict["MdlAlertOtherStepsTitle"] = "Upozorenje u koracima instalacije";
            _srDict["HwRamSolderedNoSlots"] = "Integrisana na ploči (Bez slotova)";
            _srDict["HwRamSolderedOnboard"] = "Integrisana na matičnoj ploči";
            _srDict["HwRamOneModule"] = "1 modul ({0})";
            _srDict["HwRamDetectError"] = "Greška pri detekciji RAM";
            _srDict["HwDiskHealth100"] = "100% Zdravlje";
            _srDict["HwDiskHoursNaSmart"] = "N/A (Zahteva S.M.A.R.T.)";
            _srDict["HwDiskTempNaSensor"] = "N/A (Senzor nedostupan)";
            _srDict["HwDiskDetectError"] = "Greška pri detekciji diska";
            _srDict["HwBatteryWearFmt"] = "{0:0.0}% Istrošenost ({1:N0} / {2:N0} mWh)";
            _srDict["HwBatteryChargingAc"] = "Punjenje (AC Adapter)";
            _srDict["HwBatteryOnBatteryDc"] = "Na bateriji (DC)";
            _srDict["HwBatteryAcFull"] = "AC Mreža / Puna";
            _srDict["HwBatteryAcOrBatt"] = "AC Mreža / Baterija";
            _srDict["HwBatteryDetected"] = "Detektovana baterija";
            _srDict["HwBatteryCapNaFixedAc"] = "N/A (Fiksno AC napajanje)";
            _srDict["HwBatteryDesktopAc"] = "Desktop AC Napajanje";
            _srDict["HwBatteryAcPower"] = "AC Napajanje";
            _srDict["HwSecureBootOn"] = "Omogućen (SecureBoot ON)";
            _srDict["HwSecureBootOff"] = "Onemogućen (SecureBoot OFF)";
            _srDict["HwMbStandard"] = "Standardna matična ploča";
            _srDict["HwMbDetectError"] = "Greška pri detekciji ploče";
            _srDict["HwKbDesktopNone"] = "Desktop sistem (bez ugrađenog pozadinskog osvetljenja)";
            _srDict["HwKbConfirmedNone"] = "Potvrđeno u Fazi 5 (Model nema pozadinsko osvetljenje)";
            _srDict["HwEsimSupportedPnp"] = "Podržan eSIM (PnP: {0})";
            _srDict["HwEsimSupportedWwan"] = "Podržan eSIM (WWAN modul {0})";
            _srDict["HwEsimSupportedEid"] = "Podržan eSIM (EID detektovan)";
            _srDict["HwEsimSupportedEuiccSlot"] = "Podržan eSIM (eUICC slot)";
            _srDict["HwEsimSupportedEmbeddedSlot"] = "Podržan eSIM (Embedded SIM slot)";
            _srDict["HwEsimSupportedWwanSvcEid"] = "Podržan eSIM (WwanSvc EID)";
            _srDict["HwEsimSupportedWwanSvcEmbedded"] = "Podržan eSIM (WwanSvc EmbeddedSim)";
            _srDict["WinThermalLiveContinue"] = "⚠️ {0} — instalacija nastavlja dalje.";
            _srDict["WinThermalStressNoteText"] = "⚠️ NAPOMENA: CPU {0:F1}°C (>= 95°C) — sačuvano u izveštaju!";
            _srDict["WinThermalOverheatTitle"] = "⚠️ NAPOMENA: PREGREVANJE CPU ({0:F1}°C >= 95°C)";
            _srDict["WinThermalOverheatDesc"] = "Procesor je dostigao {0:F1}°C tokom stres testa (opterećenje je bezbedno zaustavljeno). Pipeline je nastavljen i napomena je zabeležena u izveštaju.";
            _srDict["WinThermalServiceNote"] = "NAPOMENA ZA SERVIS: Očistiti hladnjak/ventilator i zameniti termalnu pastu pre predaje računara kupcu.";
            _srDict["WinPhase4MissingApps"] = "Greška u Fazi 4: Nedostaju ključni programi ({0}). Pokrenite ↻ Retry na kartici Programi ili koristite traku za oporavak dole.";
            _srDict["WinAppFileNotFound"] = "Fajl nije pronađen";
            _srDict["WinPhase2BadDevices"] = "Upozorenje: Pronađeno {0} neispravnih uređaja u Upravljaču uređaja.";
            _srDict["WinFinalMissingApps"] = "Greška: Nedostaju ključni programi ({0}). Pokrenite ↻ Retry na kartici Programi ili koristite traku za oporavak dole.";
            _srDict["WinCompletionTitleOverheat"] = "⚠️ INSTALACIJA ZAVRŠENA (NAPOMENA: PREGREVANJE CPU)";
            _srDict["WinCompletionTitleDone"] = "INSTALACIJA ZAVRŠENA";
            _srDict["WinThermalOverheatFallback"] = "NAPOMENA: Procesor se pregrevao tokom stres testa ({0:F1}°C >= 95°C) — potreban servis hlađenja / zamena termalne paste.";
            _srDict["WinCompletionLiveThermalNote"] = "⚠️ Instalacija završena uz termalnu napomenu (sačuvano u izveštaju).";
            _srDict["WinThermalServiceNoteShort"] = "NAPOMENA ZA SERVIS: Očistiti hladnjak/ventilator i zameniti termalnu pastu.";
            _srDict["WinCompletionSensorUnknown"] = "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.";
            _srDict["WinThermalSensorUnknownTitle"] = "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)";
            _srDict["WinThermalSensorUnknownDesc"] = "Hlađenje nije automatski potvrđeno; obavezna ručna provera. Potrebna je ručna provera hladnjaka i ventilatora.";
            _srDict["WinThermalSensorUnknownRec"] = "Preporuka: Proverite rad kulera i ventilaciju u BIOS/UEFI ili fizički pre isporuke računara.";
            _srDict["WinCompletionSummaryWarn"] = "Svi programi, drajveri i optimizacije su kompletirani. UPOZORENJE: Maksimalna CPU temperatura na stres testu je dostigla {0:F1}°C (preporučen preventivni servis hlađenja).";
            _srDict["WinCompletionSummaryOk"] = "Svi programi, drajveri, stres test i sistemske optimizacije su kompletirani.";
            _srDict["WinIpcEof"] = "IPC Završen (EOF)";
            _srDict["WinErrorCodeFmt"] = "Kôd greške {0}";
            _srDict["WinAutoStartPaused"] = "Automatski start pauziran. Omogućen ručni pregled.";
            _srDict["WinRebootDelayedLive"] = "Međurestart odložen za 10 minuta (novi termin: {0}).";
            _srDict["WinRebootDelayedDialogBody"] = "Restart računara je stvarno odložen za 10 minuta.\nNovi termin restarta: {0} (ukupno odloženo: {1} min).";
            _srDict["WinRebootDelayedDialogTitle"] = "WIS Servis — Odlaganje restarta";
            _srDict["WinRebootDelayPersistErrBody"] = "Restart je odložen u memoriji do {0}, ali upis u deploy-state.json nije uspeo: {1}";
            _srDict["WinUnknownError"] = "Nepoznata greška";
            _srDict["WinServiceWarningTitle"] = "WIS Servis — Upozorenje";
            _srDict["WinAppQueued"] = "U redu čekanja";
            _srDict["WinAppPsStartFailed"] = "Nije moguće pokrenuti powershell.exe";
            _srDict["WinAppLaunchErrorPrefix"] = "Greška pri pokretanju: ";
            _srDict["WinAppRetryVerified"] = "Instalacija uspešno verifikovana";
            _srDict["WinAppRetryFailedExit"] = "Ponovna instalacija nije uspela (ExitCode {0})";
            _srDict["WinAppRetryTimeout"] = "Ponovna instalacija prekoračila timeout od 15 min";
            _srDict["WinAppRetryRemaining"] = "Program {0} uspešno instaliran. Još nedostaju za Retry: {1}.";
            _srDict["WinApScanningTitle"] = "PROVERA AUTOPILOTA U TOKU...";
            _srDict["WinApScanningDesc"] = "Sistem vrši duboku forenziku lokalnog registra i proveru Microsoft ZTD oblaka...";
            _srDict["WinApScanningRec"] = "Molimo sačekajte završetak bezbednosne provere...";
            _srDict["WinApWorkerException"] = "Izuzetak na pozadinskoj niti: ";
            _srDict["WinNetWifiType"] = "Wi-Fi (Bežična mreža)";
            _srDict["WinNetWifiActive"] = "✓ Wi-Fi konekcija aktivna";
            _srDict["WinNetLanType"] = "Ethernet (LAN kabl)";
            _srDict["WinNetLanActive"] = "✓ Ethernet (LAN) aktivan";
            _srDict["WinNetNoConnection"] = "⚠ Nema mrežne veze";
            _srDict["WinRamAvailableFmt"] = "Slobodno: {0:0.0} GB / {1:0.0} GB";
            _srDict["WinRamCommitFmt"] = "Commit: {0:0.0} / {1:0.0} GB";
            _srDict["WinPwrAcVoltageNa"] = "AC Adapter (Napon N/A)";
            _srDict["WinPwrBattVoltageNa"] = "Baterija (Napon N/A)";
            _srDict["WinPwrBattSubFmt"] = "Baterija: {0}% ({1})";
            _srDict["WinPwrCharging"] = "Punjenje";
            _srDict["WinPwrAcGrid"] = "AC mreža";
            _srDict["WinPwrDischarging"] = "Pražnjenje";
            _srDict["WinPwrDesktopAc"] = "Desktop / AC napajanje";
            _srDict["WinDrvConnectingHub"] = "Povezivanje sa ServisHub...";
            _srDict["WinDrvNotFoundOem"] = "Nije pronađen na OEM katalogu";
            _srDict["WinDrvCachingElapsed"] = "ServiceHub preuzima OEM paket na keš ({0}s)...";
            _srDict["WinDrvCaching"] = "ServiceHub preuzima OEM paket na keš...";
            _srDict["WinDrvInvalidUrl"] = "Neispravan download URL";
            _srDict["WinDrvDownloadingOem"] = "Preuzimanje zvaničnog OEM drajvera...";
            _srDict["WinDrvInstallOk"] = "✓ Uspešno instalirano!";
            _srDict["WinDrvAllInstalledSummary"] = "✅ Svi drajveri su uspešno instalirani! (0 problematičnih uređaja)";
            _srDict["WinDrvMissingSummary"] = "⚠ Pronađeno je {0} uređaja bez odgovarajućeg drajvera ili sa greškom!";
            _srDict["WinDrvInstallErrCode"] = "Greška instalacije (kod {0})";
            _srDict["WinDrvConnectHubErr"] = "Greška pri povezivanju sa ServisHub";
            _srDict["WinDrvBatchStart"] = "Započinjem rešavanje {0} nedostajućih drajvera...";
            _srDict["WinDrvBatchProgress"] = "Instalacija {0}/{1}: {2}...";
            _srDict["WinDrvBatchDone"] = "Grupna instalacija drajvera završena ({0}/{1} uspešno).";
            _srDict["WinDrvKeyNoneFound"] = "Nema pronađenih ključnih uređaja";
            _srDict["WinDrvKeyCheckStart"] = "Započinjem proveru 0/{0} ključnih drajvera...";
            _srDict["WinDrvKeyCheckProgress"] = "Provera {0}/{1}: {2}...";
            _srDict["WinDrvKeyUpdatesFound"] = "Pronađeno {0} ažuriranja ključnih drajvera!";
            _srDict["WinDrvKeyAllUpToDate"] = "Svi ključni drajveri su ažurni!";
            _srDict["WinErrCode1"] = "Kod 1: Uređaj nije konfigurisan";
            _srDict["WinErrCode3"] = "Kod 3: Drajver za ovaj uređaj je oštećen";
            _srDict["WinErrCode10"] = "Kod 10: Uređaj ne može da se pokrene";
            _srDict["WinErrCode12"] = "Kod 12: Uređaj ne može da pronađe slobodne resurse";
            _srDict["WinErrCode14"] = "Kod 14: Potreban je restart računara da bi uređaj radio";
            _srDict["WinErrCode18"] = "Kod 18: Reinstalirajte drajvere za ovaj uređaj";
            _srDict["WinErrCode22"] = "Kod 22: Uređaj je onemogućen (Disabled)";
            _srDict["WinErrCode24"] = "Kod 24: Uređaj nije prisutan ili ne radi ispravno";
            _srDict["WinErrCode28"] = "Kod 28: Drajver nije instaliran (Missing Driver)";
            _srDict["WinErrCode29"] = "Kod 29: Uređaj je onemogućen u BIOS-u";
            _srDict["WinErrCode31"] = "Kod 31: Windows ne može da učita drajvere za ovaj uređaj";
            _srDict["WinErrCode32"] = "Kod 32: Drajver (servis) za ovaj uređaj je onemogućen";
            _srDict["WinErrCode37"] = "Kod 37: Windows ne može da inicijalizuje drajver uređaja";
            _srDict["WinErrCode39"] = "Kod 39: Windows ne može da učita drajver (oštećen ili nedostaje)";
            _srDict["WinErrCode43"] = "Kod 43: Windows je zaustavio uređaj zbog prijavljenog problema";
            _srDict["WinErrCodeDefault"] = "Kod {0}: Problem u konfiguraciji uređaja";
            _srDict["WinCatDisplay"] = "Grafički adapteri";
            _srDict["WinCatNet"] = "Mrežni adapteri";
            _srDict["WinCatAudio"] = "Zvučne i audio komponente";
            _srDict["WinCatDisk"] = "Diskovi i skladište";
            _srDict["WinCatCamera"] = "Kamere i biometrija";
            _srDict["WinCatInput"] = "Tastature i pokazivači";
            _srDict["WinCatUsb"] = "USB i kontroleri";
            _srDict["WinCatSystem"] = "Sistemski uređaji & Čipset";
            _srDict["WinCatOther"] = "Ostali uređaji";
            _srDict["WinLegendThrottleNa"] = "Throttling (Nije dostupno)";
            _srDict["WinFan0RpmQuiet"] = "0 RPM (Ugašen/Tihi rad)";
            _srDict["WinFanPassiveBios"] = "Pasivno / BIOS kontrola";
            _srDict["WinStressRunningStatus"] = "Kontinuirani multi-core stres test u toku...";
            _srDict["WinStressRunningTitle"] = "Stres test u toku...";
            _srDict["WinStressRunningDesc"] = "Merenje temperature i taktova pod 100% opterećenjem svih jezgara...";
            _srDict["WinStressRunningRec"] = "Kliknite na 'Stop Stres' kada završite proveru hlađenja.";
            _srDict["WinStressStoppedByTech"] = "Stres test zaustavljen od strane servisera.";
            _srDict["WinStressActiveTemp"] = "Stres aktivno ({0:F0}s) | CPU: {1:F1}°C | Max: {2:F1}°C";
            _srDict["WinStressActiveNoSensor"] = "Stres aktivno ({0:F0}s) | CPU senzor: N/A";
            _srDict["WinStressCritTitle"] = "🔴 HITNO POTREBNO ČIŠĆENJE I ZAMENA PASTE";
            _srDict["WinStressCritAbortDesc"] = "Kritično pregrevanje! Test je automatski prekinut na {0:F1}°C (prag 95°C / 97°C) radi zaštite hardvera.";
            _srDict["WinStressCritTempDesc"] = "Temperatura je dostigla kritičnih {0:F1}°C! Hladnjak je zapušen ili je pasta sasušena.";
            _srDict["WinStressCritRec"] = "Preporuka: Obavezno rastavljanje računara, čišćenje ventilatora i rebara hladnjaka, i nanošenje nove visokokvalitetne termalne paste.";
            _srDict["WinStressNoSensorDesc"] = "CPU je opterećen 100%, ali senzor temperature nije dostupan (0.0°C). Hlađenje nije moguće automatski potvrditi — obavezna ručna provera ventilatora i izduva!";
            _srDict["WinStressNoSensorRec"] = "Preporuka: Očitavanja senzora nisu dostupna preko WMI/ACPI/OEM. Proverite temperaturu u BIOS/UEFI-ju i fizički proverite rad kulera pre predaje računara.";
            _srDict["WinStressWarnTitle"] = "🟡 PREPORUČUJE SE PREVENTIVNI SERVIS";
            _srDict["WinStressWarnDesc"] = "Maksimalna temperatura je dostigla {0:F1}°C pod punim opterećenjem.";
            _srDict["WinStressWarnRec"] = "Preporuka: Termalna pasta počinje da gubi elastičnost ili ima naslaga prašine. Preporučuje se servis u narednom periodu.";
            _srDict["WinStressOkTitle"] = "🟢 ODLIČNO HLAĐENJE";
            _srDict["WinStressOkDesc"] = "Maksimalna temperatura od samo {0:F1}°C pod punim opterećenjem!";
            _srDict["WinStressOkRec"] = "Preporuka: Hlađenje i termalna pasta su u vrhunskom fabričkom stanju. Nije potrebno servisiranje.";
            _srDict["WinStressAbortStatus"] = "PREKID: Zaštitni prag dostignut ({0:F1}°C)!";
            _srDict["WinStressStoppedTemp"] = "Stres test zaustavljen nakon {0}s (Max CPU: {1:F1}°C)";
            _srDict["WinStressStoppedNoSensor"] = "Stres test zaustavljen nakon {0}s (Senzor: N/A)";
            _srDict["WinStressDoneTemp"] = "Test uspešno završen ({0}s | Max CPU: {1:F1}°C)";
            _srDict["WinStressDoneNoSensor"] = "Test završen ({0}s | Senzor temperature nije dostupan)";
            _srDict["WinThermalOverheatNoteFmt"] = "NAPOMENA: Procesor je dostigao kritičnu temperaturu ({0:F1}°C >= 95°C) tokom stres testa — potrebno čišćenje hladnjaka i zamena termalne paste.";
            _srDict["WinIpcDisconnected"] = "IPC Diskonektovan";
            _srDict["WinAppPhase4ScriptMissing"] = "Phase4-Apps.ps1 nije pronađena";
            _srDict["WinNasConnected"] = "Povezan (192.168.170.254)";
            _srDict["WinNasOffline"] = "Lokalno (Offline)";
            _srDict["WinDrvInstallingPnputil"] = "Instalacija drajvera (pnputil)...";
            _srDict["WinDrvUpdateAvailableFmt"] = "Dostupna nova verzija: {0}";
            _srDict["WinDrvLatestBadge"] = "✓ Najnoviji drajver";
            _srDict["WinDrvUpgradingBadge"] = "Nadogradnja u toku...";
            _srDict["WinDrvInstalledOkDesc"] = "Ispravno instaliran";
            _srDict["WinCpuTempWmiNa"] = "N/A (WMI/ACPI nedostupan)";
            _srDict["WinGpuTempIgpuNa"] = "N/A (Integrisana / Idle)";
            _srDict["WinStorageTempNa"] = "N/A (Senzor nedostupan)";
            _srDict["WinThermalSensorNative"] = "● Nativni senzor ({0}, 1 Hz)";
            _srDict["WinThermalSensorNa"] = "● Termalni senzori nisu dostupni (N/A)";

            // ==========================================
            // ENGLESKI JEZIK (EN - English)
            // ==========================================
            _enDict["AppTitle"] = "WIS v3.4.3 - Windows Installation Assistant";
            _enDict["CompanyBrand"] = "WIS SERVICE CENTER";
            _enDict["WorkTimeTitle"] = "Elapsed Time";
            _enDict["CpuLabel"] = "⚙ CPU:";
            _enDict["RamLabel"] = "💾 RAM:";
            _enDict["DiskLabel"] = "🖴 Disk:";

            _enDict["TabOverview"] = "Overview";
            _enDict["TabHardware"] = "Hardware Info";
            _enDict["TabDrivers"] = "Drivers";
            _enDict["TabDiagnostics"] = "HW Monitor";
            _enDict["TabNetwork"] = "Network";
            _enDict["TabApps"] = "Applications";
            _enDict["TabLogs"] = "System Log";
            _enDict["TabAutopilot"] = "Autopilot";
            _enDict["TabHwTest"] = "Hardware Tests";

            _enDict["BtnStartService"] = "▶ Start Automated Service";
            _enDict["BtnPause"] = "⏸ Pause";
            _enDict["BtnResume"] = "▶ Resume";
            _enDict["BtnSkip"] = "⏭ Skip Current";
            _enDict["BtnDelayRestart"] = "⏱ Delay Reboot";
            _enDict["BtnRefreshHardware"] = "🔄 Refresh Hardware";
            _enDict["BtnRerunApps"] = "🔄 Reinstall Apps";
            _enDict["AppRetryBtn"] = "↻ Retry";
            _enDict["AppRetryTooltip"] = "Retry installing this application";
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
            _zhDict["AppTitle"] = "WIS v3.4.3 - 视窗系统自动化安装助手";
            _zhDict["CompanyBrand"] = "WIS SERVICE 电脑维修中心";
            _zhDict["WorkTimeTitle"] = "运行时间";
            _zhDict["CpuLabel"] = "⚙ 处理器:";
            _zhDict["RamLabel"] = "💾 内存:";
            _zhDict["DiskLabel"] = "🖴 硬盘:";

            // Tabovi
            _zhDict["TabOverview"] = "概览";
            _zhDict["TabHardware"] = "硬件信息";
            _zhDict["TabDrivers"] = "驱动程序";
            _zhDict["TabDiagnostics"] = "硬件监控";
            _zhDict["TabNetwork"] = "网络连接";
            _zhDict["TabApps"] = "常用软件";
            _zhDict["TabLogs"] = "运行日志";
            _zhDict["TabAutopilot"] = "Autopilot";
            _zhDict["TabHwTest"] = "硬件测试";

            // Dugmad i kontrole
            _zhDict["BtnStartService"] = "▶ 开始安装";
            _zhDict["BtnPause"] = "⏸ 暂停";
            _zhDict["BtnResume"] = "▶ 继续";
            _zhDict["BtnSkip"] = "⏭ 跳过";
            _zhDict["BtnDelayRestart"] = "⏱ 延后重启";
            _zhDict["BtnRefreshHardware"] = "🔄 刷新硬件信息";
            _zhDict["BtnRerunApps"] = "🔄 重新安装软件";
            _zhDict["AppRetryBtn"] = "↻ 重试";
            _zhDict["AppRetryTooltip"] = "重试安装此应用程序";
            _zhDict["BtnRunStressTest"] = "🔥 启动30秒压力测试";
            _zhDict["BtnCancelStressTest"] = "⏹ 终止测试";
            _zhDict["BtnCleanAndShutdown"] = "🔄 清理并重启电脑";
            _zhDict["BtnCompletionSummary"] = "📋 完成报告卡";
            _zhDict["BtnPrintLabel"] = "🖨 打印标签";
            _zhDict["LabelNoTarget"] = "请选择打印机。";
            _zhDict["LabelNoPrinterHint"] = "未找到 USB 打印机。请检查数据线并确认打印机已开机，然后点击“刷新”。如果仍未出现，打印机可能处于 USB “Vendor Class” 模式 — 请切换到 “Printer Class” 或安装 Star TSP700II 驱动程序。";
            _zhDict["LabelStarNotFoundHint"] = "未识别到 Star TSP700II。请确认选择了正确的打印机。";
            _zhDict["LabelSearching"] = "正在查找 USB 打印机...";
            _zhDict["LabelEmulation"] = "打印机仿真模式";
            _zhDict["LabelPrinterTarget"] = "打印机";
            _zhDict["TooltipPrintLabel"] = "在 USB Star TSP700II 打印机上打印设备规格标签";
            _zhDict["LabelWindowTitle"] = "打印设备规格标签";
            _zhDict["LabelWindowHeader"] = "🖨 标签 (Star TSP700II, 80 毫米)";
            _zhDict["LabelCopies"] = "份数";
            _zhDict["BtnPrintLabelNow"] = "🖨 打印";
            _zhDict["BtnRefreshLabel"] = "↻ 刷新";
            _zhDict["BtnCloseLabel"] = "关闭";
            _zhDict["LabelSending"] = "正在发送到 {0}...";
            _zhDict["LabelPrinted"] = "已发送到 {0} (份数: {1})。";
            _zhDict["LabelRenderError"] = "准备标签时出错: ";
            _zhDict["BtnExitWithoutClean"] = "✕ 退出 (不清理)";
            _zhDict["BtnStayInApp"] = "📋 留在程序中";
            _zhDict["TooltipStayInApp"] = "关闭此窗口，以便详细查看各选项卡、硬件和日志。清理并重启按钮仍保留在底部栏。";
            _zhDict["BtnRunHwTest"] = "🔧 硬件测试";
            _zhDict["TooltipRunHwTest"] = "手动测试摄像头 (QR+IR)、声音、键盘、触摸板、触摸屏及 USB 接口";
            _zhDict["HwTestFinished"] = "硬件测试完成: {0} ({1})";

            // Jezičke oznake
            _zhDict["LangSrb"] = "🇷🇸 SRB";
            _zhDict["LangZh"] = "🇨🇳 中文";
            _zhDict["LanguageSelectTooltipSrb"] = "切换界面为塞尔维亚语";
            _zhDict["LanguageSelectTooltipZh"] = "切换界面为简体中文";

            // TAB 1: 概览 (Overview)
            _zhDict["ServiceReadyBadge"] = "⚡ 服务就绪";
            _zhDict["ServiceReadyDesc"] = "电脑已就绪，可以开始安装操作系统和软件。";
            _zhDict["ComputerModelLabel"] = "计算机型号:";
            _zhDict["SerialNumberLabel"] = "序列号 (SN):";
            _zhDict["ProcessorLabel"] = "中央处理器 (CPU):";
            _zhDict["MemoryLabel"] = "运行内存 (RAM):";
            _zhDict["DiskHealthLabel"] = "硬盘健康状态:";
            _zhDict["SmartStatusLabel"] = "SMART 健康状态:";
            _zhDict["AutopilotStatusLabel"] = "Autopilot 状态:";
            _zhDict["InstallationFlowTitle"] = "安装流程与阶段";
            _zhDict["UsbFreeTitle"] = "U盘已空闲！";
            _zhDict["UsbFreeDesc"] = "安装U盘已安全断开——可直接拔出，用于下一台电脑。";
            _zhDict["RecentActivityTitle"] = "最近活动记录";
            _zhDict["Phase1Title"] = "1. 网络";
            _zhDict["Phase1Subtitle"] = "Wi-Fi 与路由";
            _zhDict["Phase2Title"] = "2. 系统更新";
            _zhDict["Phase2Subtitle"] = "驱动与补丁";
            _zhDict["Phase3Title"] = "3. 系统优化";
            _zhDict["Phase3Subtitle"] = "精简与个性化";
            _zhDict["Phase4Title"] = "4. 软件安装";
            _zhDict["Phase4Subtitle"] = "软件包与 OEM 授权";
            _zhDict["Phase5Title"] = "5. 硬件测试";
            _zhDict["Phase5Subtitle"] = "压力测试与外设";
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
            _zhDict["HwRamSpeedLabel"] = "内存频率:";
            _zhDict["HwRamConfigLabel"] = "内存插槽配置:";
            _zhDict["HwRamSlotsLabel"] = "插槽使用情况:";
            _zhDict["HwRamSoldered"] = "板载集成内存 (Soldered)";
            _zhDict["HwRamSodimm"] = "标准插槽模块 (SO-DIMM)";
            _zhDict["HwStorageCardTitle"] = "💾 存储设备与健康 (SSD)";
            _zhDict["HwStoragePrimaryLabel"] = "主系统磁盘:";
            _zhDict["HwStorageSmartLabel"] = "SMART 健康状态:";
            _zhDict["HwStorageHoursLabel"] = "写入量 / 通电时间:";
            _zhDict["HwStorageTempLabel"] = "硬盘工作温度:";
            _zhDict["HwStorageCapacityLabel"] = "磁盘容量:";
            _zhDict["HwStorageMediaTypeLabel"] = "介质类型:";
            _zhDict["HwStorageInterfaceLabel"] = "磁盘接口:";
            _zhDict["HwBatteryCardTitle"] = "🔋 电池与电源管理";
            _zhDict["HwBatteryWearLabel"] = "磨损度 (Wear Level):";
            _zhDict["HwBatteryCapacityLabel"] = "电池容量 (当前 / 设计):";
            _zhDict["HwBatteryCyclesLabel"] = "充电循环次数:";
            _zhDict["HwBatterySourceLabel"] = "供电来源:";
            _zhDict["HwBatteryHealthLabel"] = "电池健康度:";
            _zhDict["HwBatteryDesignedCapLabel"] = "设计容量:";
            _zhDict["HwBatteryFullCapLabel"] = "满充容量:";
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
            _zhDict["HwBacklitKeyboardLabel"] = "键盘背光:";
            _zhDict["HwFingerprintLabel"] = "指纹识别器:";

            // TAB 3: 驱动程序 (Drivers)
            _zhDict["DriverInterventionRequired"] = "需要处理驱动";
            _zhDict["DriverClassLabel"] = "设备类别:";
            _zhDict["DriverManufacturerLabel"] = "制造商:";
            _zhDict["BtnCopyHwid"] = "📋 复制硬件ID";
            _zhDict["TooltipCopyHwid"] = "复制完整硬件ID到Windows剪贴板";
            _zhDict["BtnInstallOfficialDriver"] = "⬇ 安装官方驱动";
            _zhDict["TooltipInstallDriver"] = "自动从ServisHub或微软目录下载并部署官方OEM驱动";
            _zhDict["DriverInstalledSuccess"] = "✓ 驱动程序安装成功！";
            _zhDict["DriverAllGood"] = "设备管理器中的所有设备均工作正常。";
            _zhDict["DriverFilterPlaceholder"] = "按名称、类别、厂商或硬件ID过滤...";
            _zhDict["BtnCheckKeyDrivers"] = "🔍 检查关键驱动";
            _zhDict["TooltipCheckKeyDrivers"] = "通过ServisHub检查显卡、网络、声卡与芯片组的最新官方驱动";
            _zhDict["BtnResolveAllDrivers"] = "⚡ 一键修复缺失驱动";
            _zhDict["TooltipResolveAllDrivers"] = "自动为所有异常设备下载并安装官方驱动程序";
            _zhDict["BtnRefreshDriverTree"] = "🔄 刷新设备树";
            _zhDict["TooltipRefreshDriverTree"] = "重新读取系统中的所有即插即用设备与驱动";
            _zhDict["BtnOpenDeviceManager"] = "🛠️ 打开设备管理器";
            _zhDict["TooltipOpenDeviceManager"] = "打开系统自带的设备管理器 (devmgmt.msc)";
            _zhDict["DriversInstalledByCategoryTitle"] = "按类别显示已安装的设备与驱动程序";
            _zhDict["DriversTotalDetected"] = "检测到的设备总数:";
            _zhDict["DeviceUnitCount"] = "个设备";
            _zhDict["DriverVersionLabel"] = "驱动程序版本:";
            _zhDict["DriverDateLabel"] = "发布日期:";
            _zhDict["BtnDriverCheck"] = "🔍 检查";
            _zhDict["DriverCheckingStatus"] = "🔍 正在检查...";
            _zhDict["DriverNewVersionAvailable"] = "发现新版本:";
            _zhDict["BtnDriverUpgrade"] = "⬇ 立即升级";
            _zhDict["DriverUpToDate"] = "✓ 已是最新驱动";
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
            _zhDict["DriverSearchCatalog"] = "在目录中搜索";

            // TAB 4: 系统诊断 (Diagnostics)
            _zhDict["DiagGraphTitle"] = "📈 5 分钟温度与稳定性曲线 (1 Hz)";
            _zhDict["BtnStartStress"] = "▶ 开始压力测试";
            _zhDict["BtnStopStress"] = "⏹ 停止压力测试";
            _zhDict["BtnResetMinMax"] = "↺ 重置最小/最大值";
            _zhDict["CoreClocksTitle"] = "⚡ 处理器核心频率 (CORE CLOCKS)";
            _zhDict["ColCoreName"] = "核心";
            _zhDict["ColCurrentClock"] = "当前频率";
            _zhDict["ColMinClock"] = "最低频率";
            _zhDict["ColMaxClock"] = "最高频率";
            _zhDict["ColClockRatio"] = "负载 / 倍频";
            _zhDict["DiagSmartTitle"] = "🖴 S.M.A.R.T. 与 SSD 健康";
            _zhDict["DiagWearPercentLabel"] = "剩余寿命 (Wear %):";
            _zhDict["DiagSmartStatusLabel"] = "SMART 运行状态:";
            _zhDict["DiagTotalTbwLabel"] = "累计写入量 (TBW):";
            _zhDict["DiagReallocatedSectorsLabel"] = "重映射扇区:";
            _zhDict["DiagCriticalWarningsLabel"] = "严重警告:";
            _zhDict["DiagBatteryTitle"] = "🔋 电池与电源";
            _zhDict["DiagBatteryHealthLabel"] = "电池健康度:";
            _zhDict["DiagBatteryCyclesLabel"] = "循环充电次数:";
            _zhDict["DiagBatteryPowerStatusLabel"] = "电源状态:";
            _zhDict["DiagDeviceTypeLabel"] = "设备类型:";
            _zhDict["DiagCpuRamTitle"] = "⚡ 处理器与运行内存状态";
            _zhDict["DiagCpuLabel"] = "中央处理器 (CPU):";
            _zhDict["DiagCpuUsageLabel"] = "CPU 占用率:";
            _zhDict["DiagRamLabel"] = "运行内存 (RAM):";
            _zhDict["DiagRamUsageLabel"] = "内存占用率:";
            _zhDict["DiagGpuDisplayTitle"] = "🎮 显卡与显示屏";
            _zhDict["DiagGpuLabel"] = "图形显卡 (GPU):";
            _zhDict["DiagVramLabel"] = "独立显存 (VRAM):";
            _zhDict["DiagDisplayResLabel"] = "屏幕与分辨率:";
            _zhDict["DiagRefreshDpiLabel"] = "刷新率与 DPI:";
            _zhDict["DiagDeviceManagerTitle"] = "🛠 设备管理器 (PnP状态)";
            _zhDict["DiagAllDevicesOkText"] = "所有硬件模块在 Windows 设备管理器中均无报错，工作正常。";
            _zhDict["DiagMotherboardBiosTpmTitle"] = "🖧 主板、BIOS 与 TPM";
            _zhDict["DiagMotherboardLabel"] = "主板型号:";
            _zhDict["DiagBiosVersionLabel"] = "BIOS 版本:";
            _zhDict["DiagBiosDateLabel"] = "BIOS 发布日期:";
            _zhDict["DiagTpmChipLabel"] = "TPM 安全芯片:";
            _zhDict["DiagSecureBootLabel"] = "安全启动 (Secure Boot):";
            _zhDict["ThermalStatusTitle"] = "🌡️ 温度状态与传感器";
            _zhDict["ThermalCurrentTempLabel"] = "当前温度:";
            _zhDict["ThermalMaxRecordedLabel"] = "记录的最高温度:";
            _zhDict["ThermalCpuLoadLabel"] = "处理器负载:";
            _zhDict["ThermalCpuTempLabel"] = "处理器温度 (CPU):";
            _zhDict["ThermalGpuTempLabel"] = "显卡温度 (GPU):";
            _zhDict["ThermalSsdTempLabel"] = "存储设备 (SSD / NVMe):";
            _zhDict["ThermalFanSpeedLabel"] = "散热风扇转速 (Fan):";
            _zhDict["ThermalStressTitle"] = "🔥 温度压力测试 (30 秒)";
            _zhDict["TooltipRunStressTest"] = "启动高负载多核压力测试，温度达到 95°C / 97°C 时自动中止";
            _zhDict["TooltipCancelStressTest"] = "立即终止当前的温度压力测试";
            _zhDict["ThermalThresholdNormal"] = "正常";
            _zhDict["ThermalThresholdElevated"] = "偏高";
            _zhDict["ThermalThresholdOverheating"] = "过热";

            // TAB 5: 网络连接 (Network)
            _zhDict["NetNetworkAdaptersTitle"] = "网络适配器与连接状态";
            _zhDict["NetAdapterNameLabel"] = "适配器名称:";
            _zhDict["NetConnectionStatusConnected"] = "已连接";
            _zhDict["NetConnectionStatusDisconnected"] = "未连接";
            _zhDict["NetSubnetMaskLabel"] = "子网掩码 (Subnet Mask):";
            _zhDict["NetGatewayLabel"] = "默认网关 (Gateway):";
            _zhDict["NetDnsServersLabel"] = "DNS 服务器:";
            _zhDict["NetMacAddressLabel"] = "MAC 地址:";
            _zhDict["NetLinkSpeedLabel"] = "链路速率:";
            _zhDict["NetWifiTitle"] = "📶 WI-FI 连接";
            _zhDict["NetCurrentSsidLabel"] = "当前 SSID:";
            _zhDict["NetConnectionStatusLabel"] = "网络连接状态:";
            _zhDict["NetServiceWifiAutoText"] = "维修站 Wi-Fi 自动连接";
            _zhDict["NetIpConfigTitle"] = "🖥 IP 配置";
            _zhDict["NetIpAddressLabel"] = "IP 地址:";
            _zhDict["NetAdapterTypeLabel"] = "适配器类型:";
            _zhDict["NetNasServerTitle"] = "🗄 维修站 NAS 服务器";
            _zhDict["NetNasConnectionStatusLabel"] = "NAS 连接状态:";
            _zhDict["NetNasPingLatencyLabel"] = "缓存服务器 Ping 延迟:";
            _zhDict["NetTrafficDownloadTitle"] = "📥 流量传输与下载数据";
            _zhDict["NetTotalDownloadedPackagesLabel"] = "已下载软件包总数:";
            _zhDict["NetDistributionSourceLabel"] = "分发来源:";
            _zhDict["NetDistributionSourceValue"] = "本地维修站 NAS 软件库";

            // TAB 6: 常用软件 (Apps)
            _zhDict["StandardAppsTitle"] = "标准服务软件包 (第 4 阶段)";
            _zhDict["AppsInstallStatusTitle"] = "软件安装状态";
            _zhDict["AppsRerunInstallBtn"] = "重新安装";
            _zhDict["AppStatusWait"] = "等待中";
            _zhDict["AppStatusInstalling"] = "正在安装中...";
            _zhDict["AppStatusSuccess"] = "安装成功";
            _zhDict["AppStatusError"] = "错误";
            _zhDict["AppStatusNotNeeded"] = "无需安装";
            _zhDict["AppDetailWait"] = "等待第 4 阶段开始";
            _zhDict["AppDetailRunning"] = "正在安装软件包...";
            _zhDict["AppDetailSuccess"] = "安装已验证";
            _zhDict["AppDetailError"] = "安装过程中出错";
            _zhDict["AppBadgeWait"] = "⏳ 等待";
            _zhDict["AppBadgeRunning"] = "⚙ 进行中";
            _zhDict["AppBadgeSuccess"] = "✓ 完成";
            _zhDict["AppBadgeError"] = "✗ 错误";
            _zhDict["AppBadgeSkipped"] = "⏭ 已跳过";

            // TAB 7: 运行日志 (Logs)
            _zhDict["LogServiceJournalTitle"] = "服务运行日志";
            _zhDict["LogBufferTitle"] = "详细环形缓冲日志 (最多 500 行)";
            _zhDict["LogAutoScrollCheck"] = "自动滚动到最新记录";
            _zhDict["LogSearchLabel"] = "搜索日志:";
            _zhDict["LogCopyAllBtn"] = "📋 复制全部";
            _zhDict["LogClearBtn"] = "🗑 清空显示";
            _zhDict["LogFilterLevelLabel"] = "过滤级别:";
            _zhDict["LogFilterAll"] = "全部";
            _zhDict["LogFilterInfo"] = "信息 (Info)";
            _zhDict["LogFilterWarning"] = "警告 (Warning)";
            _zhDict["LogFilterError"] = "错误 (Error)";
            _zhDict["LogColTime"] = "时间";
            _zhDict["LogColLevel"] = "级别";
            _zhDict["LogColTag"] = "标签";
            _zhDict["LogColMessage"] = "消息";

            // TAB 8: 自动巡检 (Autopilot)
            _zhDict["ApSystemInspectionTitle"] = "Autopilot 系统检查";
            _zhDict["ApBatteryHealthWearTitle"] = "电池健康与损耗状态";
            _zhDict["ApDesignedCapacityLabel"] = "出厂设计容量:";
            _zhDict["ApFullCapacityLabel"] = "完全充电容量:";
            _zhDict["ApWearLevelLabel"] = "电池损耗率:";
            _zhDict["ApCycleCountLabel"] = "循环充电次数:";
            _zhDict["ApPowerStatusLabel"] = "电源工作状态:";
            _zhDict["ApTpm2SecurityTitle"] = "TPM 2.0 安全芯片";
            _zhDict["ApSecureBootStatusTitle"] = "Secure Boot 安全启动";
            _zhDict["ApUefiCsmModeTitle"] = "UEFI / CSM 固件模式";
            _zhDict["BtnRescanAutopilot"] = "🔄 重新检查";
            _zhDict["AutopilotUpdatedFormat"] = "更新时间:";
            _zhDict["ApHardwareFingerprintTitle"] = "🖥 硬件指纹 (Hardware Hash)";
            _zhDict["ApManufacturerLabel"] = "制造商:";
            _zhDict["ApDeviceModelLabel"] = "设备型号:";
            _zhDict["ApSerialNumberLabel"] = "序列号 (SN):";
            _zhDict["ApSmbiosUuidLabel"] = "SMBIOS UUID:";
            _zhDict["ApTpmStatusLabel"] = "TPM 状态:";
            _zhDict["ApLocalForensicsTitle"] = "🔍 本地残留检查";
            _zhDict["ApRegistryTracesLabel"] = "注册表残留:";
            _zhDict["ApTenantDomainLabel"] = "租户域名:";
            _zhDict["ApTenantGuidLabel"] = "租户 GUID:";
            _zhDict["ApAssignedNameLabel"] = "分配的计算机名:";
            _zhDict["ApEntraIdStatusLabel"] = "Entra ID (Azure AD):";
            _zhDict["ApIntuneMdmLabel"] = "Intune MDM 注册:";
            _zhDict["ApOrgCertsLabel"] = "组织证书:";
            _zhDict["ApZtdCloudTitle"] = "☁ 微软云端 ZTD 验证";
            _zhDict["ApOnlineZtdQueryLabel"] = "在线 ZTD 查询:";
            _zhDict["ApCloudStatusLabel"] = "云端状态:";
            _zhDict["ApServerResponseCodeLabel"] = "服务器响应码:";
            _zhDict["ApRawResponseLabel"] = "原始响应 (Raw):";
            _zhDict["ApZtdDescText"] = "微软 ZTD 服务 (ztd.dds.microsoft.com) 无需管理员账户，直接核验设备的硬件哈希。";

            // Završni ekran i rekapitulacija
            _zhDict["TotalServiceDuration"] = "服务总用时:";
            _zhDict["InstalledAppsLabel"] = "已安装应用程序:";
            _zhDict["BatteryPowerStatusLabel"] = "电池 / 电源状态:";
            _zhDict["ServiceFinishedTitle"] = "✓ 服务成功完成";
            _zhDict["ServiceFinishedDesc"] = "电脑已完成全部配置与优化。";
            _zhDict["SelectFinalActionDesc"] = "请选择服务完成后的后续操作：";
            _zhDict["CompletionDefaultTitle"] = "服务完成 - 计算机已准备交付客户";
            _zhDict["CompletionDefaultSummary"] = "所有组件、补丁和应用程序均已成功安装并通过验证。";

            // Reality Check i fazni statusi
            _zhDict["RealityCheckMissingApps"] = "磁盘上缺少关键应用程序";
            _zhDict["RealityCheckBadDevices"] = "设备管理器中检测到存在驱动异常的设备";
            _zhDict["RealityCheckSuccessApps"] = "所有关键应用程序 (Chrome, Acrobat, Office, WinRAR) 均已验证安装。";
            _zhDict["RealityCheckSuccessDrivers"] = "设备管理器中所有设备均已正确识别。";

            // Hardver i specifikacije ekrana (v2.14.0 - HW-03 / HW-07)
            _zhDict["HwDisplayModelLabel"] = "屏幕 / 面板型号:";
            _zhDict["HwDisplayBrightnessLabel"] = "亮度 (尼特):";
            _zhDict["HwDisplayColorGamutLabel"] = "色域 (Gamut):";
            _zhDict["HwSpeakersLabel"] = "音频输出 (扬声器):";
            _zhDict["HwMicrophoneLabel"] = "音频输入 (麦克风):";

            // Dinamički periferni statusi (v2.14.0 - HW-04..HW-07 / I18N-01)
            _zhDict["HwIrYes"] = "👁️ 有 (Windows Hello 人脸识别)";
            _zhDict["HwIrNo"] = "否 (标准 RGB 摄像头)";
            _zhDict["HwFpYes"] = "👆 有 ({0})";
            _zhDict["HwFpNo"] = "未检测到";
            _zhDict["HwKbYes"] = "💡 有 (键盘背光)";
            _zhDict["HwKbNo"] = "标准键盘 (无背光)";
            _zhDict["HwKbCheckManual"] = "标准键盘 / 请手动检查 (Fn+空格)";
            _zhDict["HwTouchYes"] = "🖐️ 支持 ({0} 个触点)";
            _zhDict["HwTouchYesSimple"] = "🖐️ 支持 (触摸屏)";
            _zhDict["HwTouchNo"] = "未检测到 (普通显示屏)";
            _zhDict["HwCamNo"] = "未安装摄像头";
            _zhDict["HwCamDetecting"] = "摄像头检测中...";
            _zhDict["HwLhmActive"] = "● 原生 WMI/ACPI 传感器已启用";
            _zhDict["HwWmiFallback"] = "● WMI/ACPI 备用模式";
            _zhDict["HwAudioSpeakersDefault"] = "标准音频播放设备";
            _zhDict["HwMicYes"] = "🎤 有 ({0})";
            _zhDict["HwMicNo"] = "未检测到麦克风";
            _zhDict["BtnPauseService"] = "⏸ 暂停";
            _zhDict["BtnResumeService"] = "▶ 继续";

            // Autopilot Dinamički Matrix Statusi (v2.14.0 - I18N-01)
            _zhDict["ApMatrixTitle_Scanning"] = "正在检查 AUTOPILOT...";
            _zhDict["ApMatrixDesc_Scanning"] = "系统正在深度检查本地注册表，并联网核验微软 ZTD 云端状态...";
            _zhDict["ApMatrixRec_Scanning"] = "请耐心等待系统安全检查完成...";
            _zhDict["ApMatrixTitle_Error"] = "检查出错";
            _zhDict["ApMatrixTitle_ScriptNotFound"] = "未找到检查脚本";
            _zhDict["ApMatrixDesc_ScriptNotFound"] = "在磁盘上未找到 Provera_autopilota.ps1 脚本。";
            _zhDict["ApMatrixRec_ScriptNotFound"] = "请确保 Provera_autopilota.ps1 位于根目录或 C:\\setup 中。";
            _zhDict["ApMatrixTitle_ScanError"] = "扫描出错";
            _zhDict["ApMatrixDesc_ScanError"] = "Autopilot 检查脚本未返回有效报告。";
            _zhDict["ApMatrixRec_ScanError"] = "请点击“重新检查”或检查网络连接。";
            _zhDict["ApMatrixTitle_ParseError"] = "报告解析错误";
            _zhDict["ApMatrixDesc_ParseError"] = "Autopilot 检查结果的 JSON 格式无效。";
            _zhDict["ApMatrixTitle_LockedTenant"] = "🔴 已被企业租户锁定 (TENANT LOCK)";
            _zhDict["ApMatrixDesc_LockedTenant"] = "该设备已在微软云端注册 ({0})。开机设置 (OOBE) 需要使用所属企业账户登录！";
            _zhDict["ApMatrixRec_LockedTenant"] = "必须联系所属企业或销售商，在 Intune 中注销该设备。";
            _zhDict["ApMatrixTitle_ReleasedLocalTraces"] = "🟡 云端已解除绑定，但存在本地残留";
            _zhDict["ApMatrixDesc_ReleasedLocalTraces"] = "设备已从 Autopilot 数据库中解除，但磁盘上仍残留原组织的配置。";
            _zhDict["ApMatrixRec_ReleasedLocalTraces"] = "建议执行全新安装 (Clean Install) 或重置 OOBE，以清除本地证书。";
            _zhDict["ApMatrixTitle_HiddenLock"] = "🔴 隐藏风险 - 云端仍被锁定 (磁盘已清空)";
            _zhDict["ApMatrixDesc_HiddenLock"] = "本地系统无任何残留，但该电脑的硬件哈希仍绑定在微软数据库中！";
            _zhDict["ApMatrixRec_HiddenLock"] = "在租户所有者通过 Microsoft Intune 门户解除绑定之前，该电脑不得转售。";
            _zhDict["ApMatrixTitle_Clean"] = "🟢 100% 干净 - 设备完全未锁定";
            _zhDict["ApMatrixDesc_Clean"] = "硬件指纹未在微软云端注册，本地也没有 MDM/Autopilot 残留。";
            _zhDict["ApMatrixRec_Clean"] = "设备完全干净，可供任何用户放心使用。";
            _zhDict["ApMatrixTitle_UnverifiedClean"] = "🔵 有条件干净 - 无本地残留 (云端未确认)";
            _zhDict["ApMatrixDesc_UnverifiedClean"] = "本地系统无残留，但在快速安装期间 Windows OOBE 模块未收到微软云端的明确答复。";
            _zhDict["ApMatrixRec_UnverifiedClean"] = "如设备来自企业渠道，建议在销售前联网进行 OOBE 验证。";
            _zhDict["ApLogUnverifiedCleanInfo"] = "本地注册表干净；微软 ZTD 云端状态尚未明确确认。";
            _zhDict["ApMatrixTitle_UnverifiedLocalTraces"] = "🟡 警告 - 存在本地残留 (云端状态未确认)";
            _zhDict["ApMatrixDesc_UnverifiedLocalTraces"] = "检测到组织的注册表残留，但微软 ZTD 云端未返回明确确认。";
            _zhDict["ApMatrixRec_UnverifiedLocalTraces"] = "请连接稳定的互联网，然后点击“重新检查”。";
            _zhDict["ApLogUnverifiedLocalTracesWarn"] = "检测到本地残留痕迹；ZTD 云端验证仍在等待确认。";
            _zhDict["ApMatrixTitle_OfflineLocalTraces"] = "🟡 离线 - 检测到本地残留";
            _zhDict["ApMatrixDesc_OfflineLocalTraces"] = "无法联网核验云端，且检测到组织的注册表残留。";
            _zhDict["ApMatrixRec_OfflineLocalTraces"] = "请连接互联网，然后点击“重新检查”进行在线验证。";
            _zhDict["ApMatrixTitle_OfflineCleanLocal"] = "🔵 有条件干净 - 无本地残留 (离线)";
            _zhDict["ApMatrixDesc_OfflineCleanLocal"] = "本地系统无残留，但电脑处于离线状态，云端状态仍未知。";
            _zhDict["ApMatrixRec_OfflineCleanLocal"] = "为确保万无一失，请将电脑接入网络后重新测试。";

            // Autopilot Forenzika i ZTD Tekstovi (v2.14.0 - I18N-01)
            _zhDict["ApTpmReadyActive"] = "已就绪 (TPM 2.0 已启用)";
            _zhDict["ApTpmPresentNotReady"] = "已检测到 (未就绪)";
            _zhDict["ApTpmMissing"] = "未检测到 / 无 TPM";
            _zhDict["ApTracesKeysFound"] = "发现残留 ({0} 个注册表项)";
            _zhDict["ApTracesRegistryFound"] = "在注册表中发现残留";
            _zhDict["ApTracesClean"] = "干净 (无注册表残留)";
            _zhDict["ApCloudOnlineChecked"] = "已完成在线验证";
            _zhDict["ApCloudOffline"] = "离线 / 未验证";
            _zhDict["ApCloudRegistered"] = "已在微软云端注册";
            _zhDict["ApCloudFree"] = "未锁定 (未在云端注册)";
            _zhDict["ApCloudNotVerified"] = "ZTD 云端未验证 (离线)";

            // Autopilot Log Poruke (v2.14.0 - I18N-01)
            _zhDict["ApLogScanStart"] = "正在启动微软 Autopilot 状态检查...";
            _zhDict["ApLogLockedWarn"] = "警告：电脑已被 Autopilot 锁定: {0}";
            _zhDict["ApLogCleanSuccess"] = "验证完成：电脑未被 Autopilot 锁定。";
            _zhDict["ApLogTracesWarn"] = "Autopilot 云端已解除，但本地仍有残留。";
            _zhDict["ApLogHiddenLockCrit"] = "隐藏风险：磁盘虽已清空，硬件仍在 ZTD 云端被锁定。";
            _zhDict["ApLogOfflineTracesWarn"] = "离线检查：发现本地注册表残留。";
            _zhDict["ApLogOfflineCleanInfo"] = "离线检查：本地注册表干净，建议联网再次验证。";
            _zhDict["ApLogScriptNotFound"] = "未找到 Provera_autopilota.ps1。";
            _zhDict["ApLogWorkerException"] = "Autopilot 后台线程异常: {0}";
            _zhDict["ApLogInvalidJson"] = "脚本返回的 JSON 格式无效。";
            _zhDict["BgWuTitle"] = "后台 WINDOWS 更新";
            _zhDict["BgWuDefaultDesc"] = "第 2 步之后启动 — 在后台并行下载并安装系统更新";

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

            _zhDict["RbtProgramsPrefix"] = "  •  软件: ";
            _zhDict["RbtDurationHeader"] = "耗时";
            _zhDict["RbtExtraPeripheralsHeader"] = "附加外设";
            _zhDict["RbtDeviceCardHeader"] = "设备档案 (SERVISHUB)";
            _zhDict["RbtQrPlaceholder"] = "检测到 ServisHub 后将显示二维码...";
            _zhDict["RbtWarningsHeader"] = "流程完成时的警告";
            _zhDict["RbtNoWarningsTitle"] = "✓ 流程已完成，无警告";
            _zhDict["RbtNoWarningsDesc"] = "温度、Autopilot、驱动程序、软件和硬件均处于正常状态。";
            _zhDict["RbtControlledReboot5s"] = "5 秒后将执行受控中间重启...";
            _zhDict["RbtRebootDelayedMsg"] = "技术员已推迟中间重启 — 剩余 {0:D2}:{1:D2} (至 {2})";
            _zhDict["RbtRebootDelayedSubPhase"] = "中间重启已推迟 (至 {0})";
            _zhDict["RbtControlledRebootCountdown"] = "{0} 秒后将执行受控中间重启... (点击“+10m 重启”可推迟)";
            _zhDict["RbtCleanupTitle"] = "正在清理并重启...";
            _zhDict["RbtCleanupSummary"] = "正在清理服务痕迹并重启计算机...";
            _zhDict["HwTEmergencySkipConfirmBody"] = "警告：仍有以下必填交互式测试未完成 ({0})：\n{1}\n\n仅当设备存在硬件故障或无法通过测试时，才允许紧急跳过：\n• 所有未完成的测试将被标记为 SKIP。\n• 设备在 ServiceHub 上将不会获得 READY 状态（将标记为 NEEDS_SERVICE）。\n• 必须填写跳过原因并记入报告。\n\n确定要继续执行紧急跳过吗？";
            _zhDict["HwTManualBiosBody"] = "此计算机无法直接进入 UEFI 固件 (/r /fw)。\n\n计算机现在将重启 — 请在开机时立即按下 BIOS 快捷键：\nHP: F10 | Lenovo: F1 | Dell: F2";
            _zhDict["HwTPendingInteractiveSubPhase"] = "等待必填交互式测试 ({0})...";
            _zhDict["HwTAutoDonePendingInteractive"] = "⚠️ 自动流程已完成 — 阶段 5 仍剩 {0} 项必填交互式测试。";
            _zhDict["HwTModuleUnavailable"] = "内置硬件测试模块不可用。";
            _zhDict["HwTTitle"] = "WIS 硬件测试";
            _zhDict["HwTRemainingInteractiveSubPhase"] = "剩余交互式测试: {0}";
            _zhDict["HwTTestingStoppedRemaining"] = "⚠️ 测试已中断 — 仍剩 {0} 项必填交互式测试。";
            _zhDict["HwTEmergencySkipDialogTitle"] = "阶段 5 紧急跳过必填原因";
            _zhDict["HwTEmergencySkipHeader"] = "⚠️ 紧急跳过必填测试";
            _zhDict["HwTEmergencySkipDesc"] = "请输入跳过剩余交互式测试的明确维修原因（例如：屏幕损坏、键盘故障、等待更换配件）。设备在 ServiceHub 上将被标记为 NEEDS_SERVICE：";
            _zhDict["HwTEmergencySkipValidation"] = "必须输入原因（至少 5 个字符）。";
            _zhDict["HwTCancel"] = "取消";
            _zhDict["HwTEmergencySkipConfirmBtn"] = "确认紧急跳过";
            _zhDict["HwTEmergencySkipConfirmTitle"] = "WIS — 阶段 5 紧急跳过";
            _zhDict["HwTEmergencySkipNotReady"] = "测试模块尚未就绪，无法执行紧急跳过。";
            _zhDict["HwTErrorTitle"] = "WIS 错误";
            _zhDict["HwTBacklitConfirmed"] = "已在阶段 5 确认（键盘测试）";
            _zhDict["HwTBacklitNone"] = "该型号无背光键盘（已在阶段 5 确认）";
            _zhDict["HwTBacklitFaulty"] = "配备背光键盘（阶段 5 检测到故障）";
            _zhDict["HwTBiosRestartSubPhase"] = "正在重启进入 UEFI BIOS ({0})...";
            _zhDict["HwTBiosRestartLiveStatus"] = "正在重启进入 UEFI BIOS — 返回后 WIS 将自动继续...";
            _zhDict["HwTManualBiosTitle"] = "手动进入 BIOS";
            _zhDict["HwTNotStartedWarning"] = "⚠️ 硬件测试尚未启动！请点击“重试阶段 5”或在完成前运行测试。";
            _zhDict["HwTPhase5TimeoutWarning"] = "⚠️ 阶段 5 已停止（自动测试超时）。请重试阶段 5 或完成交互式测试。";
            _zhDict["XamlPendingBannerTitle"] = "⚠️ 必做交互式测试尚未完成";
            _zhDict["XamlPendingBannerDetail"] = "自动部分已完成。请完成其余测试，以便生成报告和最终设备卡。";
            _zhDict["XamlBtnBannerResumeTests"] = "▶ 继续测试";
            _zhDict["XamlBtnEmergencySkip"] = "紧急跳过...";
            _zhDict["XamlOverviewDeviceInfoTitle"] = "设备信息";
            _zhDict["XamlOverviewManufacturerModel"] = "制造商 / 型号";
            _zhDict["XamlOverviewDiskLabel"] = "磁盘";
            _zhDict["XamlOverviewScreenResolution"] = "屏幕分辨率";
            _zhDict["XamlOverviewBatteryWear"] = "电池损耗";
            _zhDict["XamlOverviewHwMonitorTitle"] = "硬件监控";
            _zhDict["XamlOverviewLive"] = "● 实时";
            _zhDict["XamlOverviewCpuLoad"] = "CPU 负载";
            _zhDict["XamlOverviewRamUsage"] = "内存占用";
            _zhDict["XamlOverviewCpuTemp"] = "CPU 温度";
            _zhDict["XamlOverviewGpuTemp"] = "GPU 温度";
            _zhDict["XamlOverviewSsdTemp"] = "SSD 温度";
            _zhDict["XamlOverviewMbTemp"] = "主板温度";
            _zhDict["XamlOverviewAlertsTitle"] = "警告";
            _zhDict["XamlOverviewNoAlerts"] = "✓ 无活动警告";
            _zhDict["XamlOverviewNoAlertsDesc"] = "温度、Autopilot、驱动程序和程序均处于正常状态。";
            _zhDict["XamlGlobalProgressTitle"] = "流程总进度";
            _zhDict["XamlElapsedPrefix"] = "用时 ";
            _zhDict["XamlEtaPrefix"] = "预计剩余 ";
            _zhDict["XamlActiveSteps"] = "活动步骤";
            _zhDict["XamlHwMonitorDetailTitle"] = "详细硬件监控";
            _zhDict["XamlHwMonitorDiskSsd"] = "磁盘 / SSD";
            _zhDict["XamlHwMonitorPower"] = "电源";
            _zhDict["XamlHwMonitorSensorsTitle"] = "详细传感器 (最小值 / 当前值 / 最大值)";
            _zhDict["XamlHwMonitorSensorParam"] = "传感器 / 参数";
            _zhDict["XamlHwMonitorCurrent"] = "当前值";
            _zhDict["XamlHwMonitorMin"] = "最小值";
            _zhDict["XamlHwMonitorMax"] = "最大值";
            _zhDict["XamlHwMonitorSubsystemsTitle"] = "子系统 — 电源、SSD S.M.A.R.T. 和内存";
            _zhDict["XamlHwMonitorMemoryCommit"] = "内存 (RAM / 已提交):";
            _zhDict["XamlHwMonitorSsdSmartTbw"] = "SSD S.M.A.R.T. / TBW:";
            _zhDict["XamlHwMonitorReallocWarnings"] = "重新分配扇区 / 警告:";
            _zhDict["XamlHwMonitorBatteryHealthCycles"] = "电池健康 / 循环次数:";
            _zhDict["XamlHwMonitorNetThroughput"] = "网络吞吐量:";
            _zhDict["XamlFooterCopyright"] = "由 IG-Systems 开发和维护。保留所有权利";
            _zhDict["XamlBtnFailedPhaseRecheck"] = "重新检查并继续";
            _zhDict["XamlBtnFailedPhaseRetry"] = "重试阶段";
            _zhDict["XamlBtnFailedPhaseSkip"] = "跳到下一阶段";
            _zhDict["XamlPendingOverlayBadge"] = "必须的维修操作 — 第 5 阶段";
            _zhDict["XamlPendingOverlayTitle"] = "⚠️ 自动流程已完成 — 必做交互式测试";
            _zhDict["XamlPendingOverlaySubtitle"] = "流程的自动部分 (驱动程序、优化、程序和 30 秒压力测试) 已完成。在所有必做的硬件交互式测试完成之前，不会生成 ServiceHub 最终报告和最终设备卡。";
            _zhDict["XamlPendingOverlayCount"] = "剩余必做测试 (0):";
            _zhDict["XamlBtnPendingStartTests"] = "▶ 启动交互式测试";
            _zhDict["XamlBtnPendingBrowseTabs"] = "浏览标签页";
            _zhDict["WinMsgHwTestModuleMissing"] = "未找到硬件测试模块 (WIS.HwTest.exe)！";
            _zhDict["WinMsgTitleError"] = "WIS 错误";
            _zhDict["WinHwTestIncompleteSkip"] = "未完成 (已跳过)";
            _zhDict["WinDevInfoTitle"] = "设备信息";
            _zhDict["WinDevInfoModelLabel"] = "制造商 / 型号";
            _zhDict["WinPeripheralsTitle"] = "附加外设";
            _zhDict["WinHwMonTitle"] = "硬件监控";
            _zhDict["WinHwMonLive"] = "● 实时";
            _zhDict["WinHwMonCpuLoad"] = "CPU 负载";
            _zhDict["WinHwMonRamUsage"] = "内存占用";
            _zhDict["WinWarningsTitle"] = "警告";
            _zhDict["WinWarningsNone"] = "✓ 无活动警告";
            _zhDict["WinWarningsNormal"] = "温度、Autopilot、驱动程序和软件均处于正常状态。";
            _zhDict["WinServiceHubCardTitle"] = "设备档案卡 (SERVISHUB)";
            _zhDict["WinQrPlaceholder"] = "检测到 ServisHub 后将显示二维码...";
            _zhDict["WinRecoverySkipBtn"] = "跳至下一阶段";
            _zhDict["WinRecoveryRetryPhaseFmt"] = "↻ 重试第 {0} 阶段";
            _zhDict["WinRecoveryRetryPhaseDefault"] = "重试阶段";
            _zhDict["WinRecoverySkipTooltipFmt"] = "跳过第 {0} 阶段并继续第 {1} 阶段";
            _zhDict["WinPendingBannerTitle"] = "⚠️ 必做交互式测试尚未完成";
            _zhDict["WinPendingBannerDetail"] = "自动流程已完成。请完成剩余测试以生成报告和完成卡片。";
            _zhDict["WinPendingBtnResume"] = "▶ 继续测试";
            _zhDict["WinPendingBtnEmergencySkip"] = "紧急跳过...";
            _zhDict["WinPendingOverlayBadge"] = "必须执行的售后操作 — 第 5 阶段";
            _zhDict["WinPendingOverlayTitle"] = "⚠️ 自动安装与测试流程已完成 — 请执行必做交互式硬件测试";
            _zhDict["WinPendingOverlaySubtitle"] = "驱动、系统优化、软件安装及 30 秒压力测试已全部完成。在完成所有必做交互式硬件测试前，不会生成 ServiceHub 最终报告和完成卡片。";
            _zhDict["WinPendingOverlayCountTitle"] = "剩余必做测试 (0)：";
            _zhDict["WinPendingOverlayCountFmt"] = "剩余 {0} 项必做交互式测试：";
            _zhDict["WinPendingBtnStartTests"] = "▶ 开始交互式测试";
            _zhDict["WinPendingBtnBrowseTabs"] = "浏览标签页";
            _zhDict["WinPendingBannerTitleFmt"] = "⚠️ 仍有 {0} 项必做交互式测试未完成 — 最终报告和完成卡片已锁定";
            _zhDict["WinPendingBannerDetailFmt"] = "待测项目：{0} — 完成测试后才会生成最终报告和完成卡片。";
            _zhDict["WinRcCheckingApps"] = "正在检查磁盘上的关键应用程序...";
            _zhDict["WinRcStillMissingFmt"] = "仍然缺少：{0}。请在“程序”标签页中点击单项重试。";
            _zhDict["WinRcMissingMsgFmt"] = "磁盘上仍未找到以下关键程序：\n\n{0}\n\n请在“程序”标签页点击单项 ↻ 重试，或点击“重试第 4 阶段”。";
            _zhDict["WinRcMissingMsgTitle"] = "WIS 实际检查 — 缺少程序";
            _zhDict["WinSkipMissingAppsHdr"] = "\n\n磁盘上缺少以下程序：\n- ";
            _zhDict["WinSkipPhaseTargetFmt"] = "第 {0} 阶段";
            _zhDict["WinSkipTargetFinish"] = "服务结束";
            _zhDict["WinSkipConfirmMsgFmt"] = "确定要跳过失败的第 {0} 阶段并转到{1}吗？{2}";
            _zhDict["WinSkipConfirmTitle"] = "WIS 维修服务 — 确认跳过阶段";
            _zhDict["WinGlobalProgressTitle"] = "总体流程进度";
            _zhDict["WinTimingElapsedPrefix"] = "耗时 ";
            _zhDict["WinTimingEtaPrefix"] = "预计剩余 ";
            _zhDict["WinActiveStepsTitle"] = "活动步骤";
            _zhDict["WinFooterCopyright"] = "由 IG-Systems 开发与维护。保留所有权利";
            _zhDict["WinHwMonHeader"] = "硬件监控  •  实时系统遥测";
            _zhDict["WinHwMonBtnReset"] = "↺ 重置最小/最大";
            _zhDict["WinHwMonBtnStartStress"] = "▶ 开始压力测试";
            _zhDict["WinHwMonBtnStopStress"] = "⏹ 停止压力测试";
            _zhDict["WinHwMonKpiCpu"] = "CPU / 处理器";
            _zhDict["WinHwMonKpiRam"] = "内存 (RAM)";
            _zhDict["WinHwMonKpiGpu"] = "显卡 (GPU)";
            _zhDict["WinHwMonKpiDisk"] = "磁盘 / SSD I/O";
            _zhDict["WinHwMonKpiPower"] = "电源 / 电池";
            _zhDict["WinHwMonGraphTitle"] = "温度曲线与 CPU 负载 (5 分钟 / 1 Hz)";
            _zhDict["WinHwMonSensorsTitle"] = "详细传感器 (最小 / 当前 / 最大)";
            _zhDict["WinHwMonColMax"] = "最大";
            _zhDict["WinHwMonColMin"] = "最小";
            _zhDict["WinHwMonColCurrent"] = "当前";
            _zhDict["WinHwMonColSensor"] = "传感器";
            _zhDict["WinHwMonClocksTitle"] = "处理器核心频率";
            _zhDict["WinHwMonColCore"] = "核心";
            _zhDict["WinHwMonSubsysTitle"] = "子系统与诊断";
            _zhDict["WinHwMonSubNet"] = "网络 (接收 / 发送)";
            _zhDict["WinHwMonSubDisk"] = "磁盘 I/O";
            _zhDict["WinHwMonSubPower"] = "电源";
            _zhDict["WinHwMonSubVolt"] = "模式 / 电压";
            _zhDict["WinHwMonSubThermal"] = "温度评估";
            _zhDict["WinMasterPhaseStatusFmt"] = "主控第 {0} 阶段";
            _zhDict["WinPhase6BlockedSub"] = "正在等待必做交互式测试...";
            _zhDict["WinPhase6BlockedLive"] = "⚠️ 在完成所有必做交互式硬件测试之前，第 6 阶段已被锁定。";
            _zhDict["WinPhase1Name"] = "第 1 阶段：网络与连接";
            _zhDict["WinPhase1Sub"] = "正在连接维修服务网络...";
            _zhDict["WinPhase1Live"] = "正在配置网络接口和路由...";
            _zhDict["WinPhase2Name"] = "第 2 阶段：驱动程序与补丁";
            _zhDict["WinPhase2Sub"] = "正在安装 PnP 驱动程序...";
            _zhDict["WinPhase2Live"] = "正在扫描并安装缺失的驱动程序...";
            _zhDict["WinPhase3Name"] = "第 3 阶段：系统优化与精简";
            _zhDict["WinPhase3Sub"] = "正在执行系统优化...";
            _zhDict["WinPhase3Live"] = "正在应用精简和品牌化脚本...";
            _zhDict["WinPhase4Name"] = "第 4 阶段：软件安装";
            _zhDict["WinPhase4Sub"] = "正在安装软件包...";
            _zhDict["WinPhase4Live"] = "正在安装并验证维修软件...";
            _zhDict["WinPhase5Name"] = "第 5 阶段：硬件测试（压力测试 + 外设）";
            _zhDict["WinPhase5Sub"] = "正在执行自动检测、30 秒压力测试及外设测试...";
            _zhDict["WinPhase5Live"] = "正在“硬件测试”标签页中执行集成硬件测试...";
            _zhDict["WinPhase6Name"] = "第 6 阶段：完成与总结";
            _zhDict["WinPhase6Sub"] = "正在完成收尾与诊断...";
            _zhDict["WinPhase6Live"] = "正在生成维修报告并验证...";
            _zhDict["WinPhaseDefaultNameFmt"] = "第 {0} 阶段";
            _zhDict["WinPhaseDefaultSub"] = "正在执行阶段...";
            _zhDict["WinPhaseDefaultLiveFmt"] = "正在执行第 {0} 阶段...";
            _zhDict["WinPhaseScriptMissingFmt"] = "错误：系统上缺少 {0}！";
            _zhDict["WinPhaseTimeoutLiveFmt"] = "第 {0} 阶段已超时。请使用下方恢复栏（“重试第 {0} 阶段”或“跳至下一阶段”）。";
            _zhDict["WinPhaseErrorLiveFmt"] = "第 {0} 阶段出错（代码 {1}）。请使用下方恢复栏（“重新检查并继续”、“重试第 {0} 阶段”或“跳至下一阶段”）。";
            _zhDict["WinStressAutoStatus"] = "自动 30 秒处理器压力测试（第 5 阶段）...";
            _zhDict["HwKbUnconfirmed"] = "未确认 (阶段5检测)";
            _zhDict["Step_P5_AutoDiag"] = "自动系统检测";
            _zhDict["Step_P5_Stress"] = "压力测试 (磁盘、内存、CPU 30秒、GPU 3D)";
            _zhDict["Step_P5_Peripherals"] = "外设交互测试";
            _zhDict["Step_P6_WuSecondPass"] = "双轮更新与 WU 屏蔽";
            _zhDict["Step_P6_Report"] = "服务档案与批量报告";
            _zhDict["Step_P6_Security"] = "恢复安全策略与防泄漏清理";
            _zhDict["Step_P6_Finalize"] = "交付前最终验证";
            _zhDict["MdlPhase2Title"] = "1. 驱动更新";
            _zhDict["MdlPhase2Subtitle"] = "驱动与补丁";
            _zhDict["MdlPhase3Title"] = "2. 系统优化";
            _zhDict["MdlPhase3Subtitle"] = "精简与个性化";
            _zhDict["MdlPhase4Title"] = "3. 软件安装";
            _zhDict["MdlPhase4Subtitle"] = "软件包与 OEM 授权";
            _zhDict["MdlPhase5Title"] = "4. 硬件测试";
            _zhDict["MdlPhase5Subtitle"] = "压力与外设";
            _zhDict["MdlPhase6Title"] = "5. 完成交付";
            _zhDict["MdlPhase6Subtitle"] = "诊断与总览";
            _zhDict["MdlDefaultBenchmarkSource"] = "默认 WIS v3 基准";
            _zhDict["MdlTimingTooltip"] = "耗时与基准预计剩余时间";
            _zhDict["MdlInProgress"] = "进行中...";
            _zhDict["MdlAlertCritBadge"] = "严重";
            _zhDict["MdlAlertWarnBadge"] = "警告";
            _zhDict["MdlAlertInfoBadge"] = "信息";
            _zhDict["MdlAppDetailQueue"] = "排队等待中";
            _zhDict["MdlAppDetailSkippedDone"] = "已安装";
            _zhDict["MdlAppDetailSkippedUser"] = "应工程师要求已跳过";
            _zhDict["MdlAppDetailOk"] = "安装验证成功";
            _zhDict["MdlAppDetailRunning"] = "正在安装软件包...";
            _zhDict["MdlAppDetailWarn"] = "安装未确认";
            _zhDict["MdlAppDetailError"] = "安装过程中出错";
            _zhDict["MdlAppNotApplicableDesktop"] = "不适用于台式机";
            _zhDict["MdlAppNameLicense"] = "OEM 授权";
            _zhDict["MdlAppNameShortcuts"] = "服务快捷方式";
            _zhDict["MdlAppsSummary"] = "已安装 {0}/{1} • 进行中 {2} • 已跳过 {3} • 错误 {4}";
            _zhDict["MdlAppStatusOk"] = "✓ 完成";
            _zhDict["MdlAppStatusRunning"] = "⚙ 正在进行";
            _zhDict["MdlAppStatusWarn"] = "⚠ 未完成";
            _zhDict["MdlAppStatusError"] = "✗ 错误";
            _zhDict["MdlAppStatusSkippedDone"] = "» 已跳过";
            _zhDict["MdlAppStatusSkipped"] = "⏭ 已跳过";
            _zhDict["MdlAppStatusWait"] = "⏳ 等待中";
            _zhDict["MdlUnknownDevice"] = "未知设备";
            _zhDict["MdlUnknown"] = "未知";
            _zhDict["MdlDriverError"] = "驱动错误";
            _zhDict["MdlDrvLoadingSummary"] = "正在加载驱动状态...";
            _zhDict["MdlLoading"] = "加载中...";
            _zhDict["MdlChecking"] = "检测中...";
            _zhDict["MdlDetecting"] = "检测中...";
            _zhDict["MdlReading"] = "读取中...";
            _zhDict["MdlConnecting"] = "连接中...";
            _zhDict["MdlNotStarted"] = "未启动";
            _zhDict["MdlNotAvailable"] = "不可用";
            _zhDict["MdlNotConnected"] = "未连接";
            _zhDict["MdlNotLaunched"] = "未运行";
            _zhDict["MdlApNoneNotAssigned"] = "无 (未分配)";
            _zhDict["MdlApNotJoined"] = "未加入 (NO)";
            _zhDict["MdlApNotEnrolled"] = "未注册 (NO)";
            _zhDict["MdlApCloudWaiting"] = "等待检查...";
            _zhDict["MdlApCloudConnecting"] = "正在连接 ztd.dds.microsoft.com...";
            _zhDict["MdlHwLoadingCpu"] = "正在读取 CPU...";
            _zhDict["MdlHwLoadingRam"] = "正在读取内存...";
            _zhDict["MdlHwDetectingSlots"] = "正在检测插槽...";
            _zhDict["MdlHwLoadingStorage"] = "正在读取硬盘...";
            _zhDict["MdlHwStorageHoursNa"] = "N/A (需要 S.M.A.R.T.)";
            _zhDict["MdlHwStorageTempNa"] = "N/A (传感器不可用)";
            _zhDict["MdlHwLoadingGpu"] = "正在读取显卡...";
            _zhDict["MdlHwDetectingDisplay"] = "正在检测显示器...";
            _zhDict["MdlHwLoadingMb"] = "正在读取主板...";
            _zhDict["MdlHwDetectingIr"] = "正在检测红外摄像头...";
            _zhDict["MdlHwDetectingSensor"] = "正在检测传感器...";
            _zhDict["MdlRamSoldered"] = "板载集成 (LPDDR / 焊接)";
            _zhDict["MdlPeriphWwanFallback"] = "4G LTE / 5G 网卡";
            _zhDict["MdlPeriphEsimFallback"] = "支持 eSIM (eUICC)";
            _zhDict["MdlPeriphBluetooth"] = "蓝牙";
            _zhDict["MdlPeriphBacklitKb"] = "背光键盘";
            _zhDict["MdlPeriphIrCam"] = "红外摄像头 (Hello)";
            _zhDict["MdlPeriphFingerprint"] = "指纹识别器";
            _zhDict["MdlPeriphTouchscreen"] = "触摸屏";
            _zhDict["MdlPeriphSmartCard"] = "智能卡读卡器";
            _zhDict["MdlPeriphSmartCardDetail"] = "检测到智能卡读卡器";
            _zhDict["MdlPeriphNfc"] = "NFC";
            _zhDict["MdlPeriphNfcDetail"] = "检测到 NFC / 近场通信设备";
            _zhDict["MdlPeriphEmptyNone"] = "未检测到其他外设";
            _zhDict["MdlPeriphEmptyDetecting"] = "正在检测外设...";
            _zhDict["MdlLoadingConfig"] = "正在加载配置...";
            _zhDict["MdlDetectingCpu"] = "正在检测 CPU...";
            _zhDict["MdlDetectingRam"] = "正在检测内存...";
            _zhDict["MdlDetectingDisk"] = "正在检测 SSD/NVMe 硬盘...";
            _zhDict["MdlHubConnecting"] = "正在连接 ServiceHub 服务器...";
            _zhDict["MdlQrScanPrompt"] = "手机扫码查看设备档案";
            _zhDict["MdlQrUnavailable"] = "二维码生成器不可用";
            _zhDict["MdlInitPhaseName"] = "阶段 1: 网络与连接";
            _zhDict["MdlInitSubPhase"] = "正在初始化网络服务...";
            _zhDict["MdlInitLiveStatus"] = "系统正在准备服务配置...";
            _zhDict["MdlInitGlobalSummary"] = "0 / 28 步骤";
            _zhDict["MdlUsbBannerText"] = "✓ U盘已安全弹出 (现在可以安全拔出U盘)";
            _zhDict["MdlBgWuCompleted"] = "✓ 已完成";
            _zhDict["MdlBgWuSkipped"] = "⏭ 已跳过";
            _zhDict["MdlBgWuWaitingMsi"] = "⏳ 等待 MSI";
            _zhDict["MdlBgWuDownloading"] = "⬇ 下载中";
            _zhDict["MdlBgWuInstalling"] = "⚙ 安装中";
            _zhDict["MdlBgWuHarvesting"] = "⬆ 缓存至NAS";
            _zhDict["MdlBgWuWarn"] = "⚠ 警告";
            _zhDict["MdlBgWuInProgress"] = "🔄 搜索中";
            _zhDict["MdlDiskWearInit"] = "100% 健康度";
            _zhDict["MdlDiskWearRemaining"] = "剩余健康度 {0}%";
            _zhDict["MdlSmartHealthyInit"] = "健康 (正常)";
            _zhDict["MdlBatteryHealthInit"] = "固定电源 (台式机)";
            _zhDict["MdlBatteryStatusInit"] = "交流电源 / 台式机";
            _zhDict["MdlBatteryFactoryCap"] = "{0}% 出厂容量";
            _zhDict["MdlDeviceTypeLaptop"] = "笔记本电脑";
            _zhDict["MdlDeviceTypeDesktop"] = "台式机 (固定电源)";
            _zhDict["MdlPwrReadingTitle"] = "正在读取电源状态...";
            _zhDict["MdlPwrReadingTooltip"] = "正在读取电池与充电器状态...";
            _zhDict["MdlPwrFixedAc"] = "固定交流电源";
            _zhDict["MdlPwrDesktopAc"] = "台式机 (AC)";
            _zhDict["MdlPwrDesktopTooltip"] = "无系统电池的台式机 (持续交流供电)。";
            _zhDict["MdlPwrChargerConnected"] = "已接通电源 • {0}%";
            _zhDict["MdlPwrFullAc"] = "已充满 (AC)";
            _zhDict["MdlPwrChargingAc"] = "充电中 (AC)";
            _zhDict["MdlPwrAc"] = "交流供电";
            _zhDict["MdlPwrOnBattery"] = "使用电池 • {0}%";
            _zhDict["MdlPwrDischargingDc"] = "放电中 (DC)";
            _zhDict["MdlPwrTtPower"] = " | 功率: {0:0.0} W";
            _zhDict["MdlPwrTtCurrent"] = " | 电流: {0:0.00} A";
            _zhDict["MdlPwrTtVoltage"] = " | 电池电压: {0:0.00} V";
            _zhDict["MdlPwrTtRemaining"] = " | 预计剩余: {0}";
            _zhDict["MdlDetectingGpu"] = "正在检测显卡...";
            _zhDict["MdlDetectingMb"] = "正在检测主板...";
            _zhDict["MdlDetectingBios"] = "正在检测 BIOS...";
            _zhDict["MdlCheckingTpm"] = "正在检测 TPM 芯片...";
            _zhDict["MdlPnpAllOkInit"] = "✓ 所有系统驱动均已正确安装";
            _zhDict["MdlPnpAttentionCount"] = "⚠ {0} 个设备需要注意";
            _zhDict["MdlPnpAllOk"] = "✓ 所有系统驱动均已正确安装 (0 个问题)";
            _zhDict["MdlSensorsActive"] = "● 传感器已激活";
            _zhDict["MdlLegendThrottleNa"] = "● 降频: 不可用";
            _zhDict["MdlKpiTempReading"] = "温度: 读取中...";
            _zhDict["MdlKpiRamFreeNa"] = "可用: N/A";
            _zhDict["MdlKpiDiskIoReading"] = "读写: 读取中...";
            _zhDict["MdlKpiPowerVoltageReading"] = "电压: 读取中...";
            _zhDict["MdlKpiPowerSubInit"] = "电池健康度";
            _zhDict["MdlKpiTempMinMax"] = "温度 {0:0}°C • 最低 {1:0}° / 最高 {2:0}°";
            _zhDict["MdlKpiPowerHealthCycles"] = "健康度 {0} • 循环次数: {1}";
            _zhDict["MdlSensorHdrTemp"] = "温度";
            _zhDict["MdlSensorTempCpu"] = "CPU 封装 / ACPI";
            _zhDict["MdlSensorTempGpu"] = "GPU 核心";
            _zhDict["MdlSensorTempMb"] = "主板 (MB)";
            _zhDict["MdlSensorTempSsd"] = "NVMe / SSD 硬盘";
            _zhDict["MdlSensorHdrLoad"] = "负载与稳定性";
            _zhDict["MdlSensorLoadCpu"] = "CPU 总负载";
            _zhDict["MdlSensorLoadRam"] = "内存占用率";
            _zhDict["MdlSensorLoadThrottle"] = "CPU 过热降频";
            _zhDict["MdlSensorIoDisk"] = "磁盘 I/O 活动";
            _zhDict["MdlSensorIoNet"] = "网络流量 (接收 + 发送)";
            _zhDict["MdlSensorHdrPower"] = "散热与供电";
            _zhDict["MdlSensorFanRpm"] = "风扇转速 (RPM)";
            _zhDict["MdlSensorPwrWatts"] = "电池 / 充电器功率";
            _zhDict["MdlSensorPwrAmps"] = "电流 (A)";
            _zhDict["MdlSensorPwrVolts"] = "电池电压 (V)";
            _zhDict["MdlStressReady"] = "准备进行散热与稳定性测试";
            _zhDict["MdlThermalNotTested"] = "尚未测试";
            _zhDict["MdlThermalNotTestedDesc"] = "运行测试以评估导热硅脂与散热器性能。";
            _zhDict["MdlThermalRecStart"] = "建议: 点击 [▶ 开始压力测试] 进行诊断。";
            _zhDict["MdlNetDetectingNic"] = "正在检测网卡...";
            _zhDict["MdlNetDetecting"] = "正在检测网络...";
            _zhDict["MdlNetDistSource"] = "本地存储缓存 (NAS / USB)";
            _zhDict["MdlNetNasChecking"] = "正在检查 NAS 连接...";
            _zhDict["MdlAutoStartMsg"] = "15秒后自动开始... (点击立即开始或切换标签页暂停)";
            _zhDict["MdlIpcConnected"] = "IPC 已连接";
            _zhDict["MdlPhasesHideSteps"] = "▲ 收起步骤";
            _zhDict["MdlPhasesShowSteps"] = "▼ 展开步骤";
            _zhDict["MdlPhasesHideTooltip"] = "收起各阶段详细步骤";
            _zhDict["MdlPhasesShowTooltip"] = "显示所有阶段详细步骤";
            _zhDict["MdlCoolingCritical"] = "散热: 严重异常 — 已阻止交付。";
            _zhDict["MdlCoolingUnknown"] = "散热: 未验证 — 必须人工检查风扇和散热器。";
            _zhDict["MdlCoolingPass"] = "散热: 测试完成，无温度警告。";
            _zhDict["MdlCoolingWarn"] = "散热: 警告 — 需要服务检查。";
            _zhDict["MdlCoolingFail"] = "散热: 测试未成功完成 — 需要人工检查。";
            _zhDict["MdlShutdownCountdown"] = "自动关机倒计时: {0} 秒 (WIS Service)";
            _zhDict["MdlReadyForDelivery"] = "计算机已准备就绪交付。";
            _zhDict["MdlProgressDoneSteps"] = "{0} / {0} 步骤 • 已完成";
            _zhDict["MdlProgressDone100"] = "100% • 已完成";
            _zhDict["MdlProgressReadySteps"] = "0 / {0} 步骤 • 就绪";
            _zhDict["MdlProgressInProgressSteps"] = "{0} / {1} 步骤 • {2}";
            _zhDict["MdlBarReadyLabel"] = "就绪";
            _zhDict["MdlBarReadyTitle"] = "等待开始安装";
            _zhDict["MdlBarReadyDetail"] = "点击“开始安装”或等待自动启动。";
            _zhDict["MdlBarDoneLabel"] = "已完成";
            _zhDict["MdlBarDoneTitle"] = "所有步骤已成功完成";
            _zhDict["MdlBarDoneDetail"] = "计算机已准备就绪，可以交付。";
            _zhDict["MdlBarStepRunning"] = "正在执行步骤...";
            _zhDict["MdlBarPhaseFallback"] = "阶段";
            _zhDict["MdlBarInProgressFallback"] = "进行中";
            _zhDict["MdlBarBgWuLabel"] = "后台 WU";
            _zhDict["MdlBarBgWuTitle"] = "后台 Windows 更新";
            _zhDict["MdlBarDriversLabel"] = "驱动";
            _zhDict["MdlBarDriversTitle"] = "批量安装驱动程序";
            _zhDict["MdlBarDriverLabel"] = "驱动";
            _zhDict["MdlBarDriverTitle"] = "安装驱动程序";
            _zhDict["MdlCompletionBadgeOk"] = "✓ 服务已完成";
            _zhDict["MdlCompletionBadgeCrit"] = "⚠ 完成（存在严重警告）";
            _zhDict["MdlCompletionBadgeWarn"] = "⚠ 完成（存在警告）";
            _zhDict["MdlCompletionAlertsOk"] = "所有硬件、驱动与软件验证通过 — 设备已可交付客户。";
            _zhDict["MdlCompletionAlertsCrit"] = "完成时发现 {0} 项警告（含严重警告）— 点击条目可查看详情。";
            _zhDict["MdlCompletionAlertsWarn"] = "完成时发现 {0} 项警告 — 交付前请检查。";
            _zhDict["MdlAlertThermalEmergency"] = "已触发紧急中止";
            _zhDict["MdlAlertThermalCritDetail"] = "检测到硬件温度严重过高";
            _zhDict["MdlAlertThermalCritTitle"] = "计算机过热";
            _zhDict["MdlAlertThermalWarnDetail"] = "工作温度偏高";
            _zhDict["MdlAlertThermalWarnTitle"] = "温度偏高 / 降频";
            _zhDict["MdlAlertApLockedTitle"] = "检测到 Windows Autopilot";
            _zhDict["MdlAlertApMdmTitle"] = "检测到 Autopilot / MDM";
            _zhDict["MdlAlertApCheckTab"] = "请查看 Autopilot 标签页了解详情";
            _zhDict["MdlAlertDrvOneMissing"] = "1 个设备缺少驱动";
            _zhDict["MdlAlertDrvManyErrors"] = "{0} 个设备存在错误";
            _zhDict["MdlAlertDrvStepDetail"] = "驱动安装步骤报告了错误或警告。";
            _zhDict["MdlAlertDrvTitle"] = "部分驱动未安装";
            _zhDict["MdlAlertAppsFailedPrefix"] = "失败: ";
            _zhDict["MdlAlertAppsTitle"] = "部分软件未安装";
            _zhDict["MdlAlertHwTestFallback"] = "请检查硬件测试结果。";
            _zhDict["MdlAlertHwTestTitle"] = "硬件测试报告异常";
            _zhDict["MdlAlertWeakChargerTitle"] = "充电器功率不足 — 电池正在掉电";
            _zhDict["MdlAlertWeakChargerDetail"] = "已接通充电器，但电池仍在掉电 -{0:0.0} W ({1}%)";
            _zhDict["MdlAlertLowBatTitle"] = "电池电量低";
            _zhDict["MdlAlertLowBatDetail"] = "仅剩 {0}% — 请接通电源！";
            _zhDict["MdlAlertBatWearTitle"] = "电池损耗严重";
            _zhDict["MdlAlertSmartDetail"] = "健康度: {0}% • SMART: {1}";
            _zhDict["MdlAlertSmartTitle"] = "磁盘健康警告 (SMART)";
            _zhDict["MdlAlertOtherStepsTitle"] = "安装步骤警告";
            _zhDict["HwRamSolderedNoSlots"] = "板载内存 (无插槽)";
            _zhDict["HwRamSolderedOnboard"] = "板载焊接内存";
            _zhDict["HwRamOneModule"] = "1 条内存 ({0})";
            _zhDict["HwRamDetectError"] = "RAM 检测出错";
            _zhDict["HwDiskHealth100"] = "100% 健康";
            _zhDict["HwDiskHoursNaSmart"] = "N/A (需要 S.M.A.R.T.)";
            _zhDict["HwDiskTempNaSensor"] = "N/A (传感器不可用)";
            _zhDict["HwDiskDetectError"] = "硬盘检测出错";
            _zhDict["HwBatteryWearFmt"] = "{0:0.0}% 损耗 ({1:N0} / {2:N0} mWh)";
            _zhDict["HwBatteryChargingAc"] = "正在充电 (AC 适配器)";
            _zhDict["HwBatteryOnBatteryDc"] = "使用电池 (DC)";
            _zhDict["HwBatteryAcFull"] = "交流电源 / 已充满";
            _zhDict["HwBatteryAcOrBatt"] = "交流电源 / 电池";
            _zhDict["HwBatteryDetected"] = "已检测到电池";
            _zhDict["HwBatteryCapNaFixedAc"] = "N/A (固定交流供电)";
            _zhDict["HwBatteryDesktopAc"] = "台式机交流供电";
            _zhDict["HwBatteryAcPower"] = "交流供电";
            _zhDict["HwSecureBootOn"] = "已启用 (SecureBoot ON)";
            _zhDict["HwSecureBootOff"] = "已禁用 (SecureBoot OFF)";
            _zhDict["HwMbStandard"] = "标准主板";
            _zhDict["HwMbDetectError"] = "主板检测出错";
            _zhDict["HwKbDesktopNone"] = "台式机系统 (无内置背光)";
            _zhDict["HwKbConfirmedNone"] = "已在阶段 5 确认 (该型号无背光)";
            _zhDict["HwEsimSupportedPnp"] = "支持 eSIM (PnP: {0})";
            _zhDict["HwEsimSupportedWwan"] = "支持 eSIM (WWAN 模块 {0})";
            _zhDict["HwEsimSupportedEid"] = "支持 eSIM (已检测到 EID)";
            _zhDict["HwEsimSupportedEuiccSlot"] = "支持 eSIM (eUICC 卡槽)";
            _zhDict["HwEsimSupportedEmbeddedSlot"] = "支持 eSIM (嵌入式 SIM 卡槽)";
            _zhDict["HwEsimSupportedWwanSvcEid"] = "支持 eSIM (WwanSvc EID)";
            _zhDict["HwEsimSupportedWwanSvcEmbedded"] = "支持 eSIM (WwanSvc EmbeddedSim)";
            _zhDict["WinThermalLiveContinue"] = "⚠️ {0} — 安装继续进行。";
            _zhDict["WinThermalStressNoteText"] = "⚠️ 注意：CPU {0:F1}°C (>= 95°C) — 已记录至报告！";
            _zhDict["WinThermalOverheatTitle"] = "⚠️ 注意：CPU 过热 ({0:F1}°C >= 95°C)";
            _zhDict["WinThermalOverheatDesc"] = "处理器在压力测试期间达到 {0:F1}°C（已安全停止负载）。流程已继续并将此备注记录在报告中。";
            _zhDict["WinThermalServiceNote"] = "维修备注：在交付客户前请清理散热器/风扇并更换导热硅脂。";
            _zhDict["WinPhase4MissingApps"] = "阶段 4 错误：缺少关键软件 ({0})。请在“软件”选项卡点击 ↻ 重试，或使用下方恢复栏。";
            _zhDict["WinAppFileNotFound"] = "未找到文件";
            _zhDict["WinPhase2BadDevices"] = "警告：设备管理器中发现 {0} 个异常设备。";
            _zhDict["WinFinalMissingApps"] = "错误：缺少关键软件 ({0})。请在“软件”选项卡点击 ↻ 重试，或使用下方恢复栏。";
            _zhDict["WinCompletionTitleOverheat"] = "⚠️ 安装完成（注意：CPU 过热）";
            _zhDict["WinCompletionTitleDone"] = "安装已完成";
            _zhDict["WinThermalOverheatFallback"] = "注意：处理器在压力测试期间过热 ({0:F1}°C >= 95°C) — 需要检修散热系统 / 更换导热硅脂。";
            _zhDict["WinCompletionLiveThermalNote"] = "⚠️ 安装已完成，附带温控备注（已保存至报告）。";
            _zhDict["WinThermalServiceNoteShort"] = "维修备注：请清理散热器/风扇并更换导热硅脂。";
            _zhDict["WinCompletionSensorUnknown"] = "⚠️ 安装成功。散热诊断：未能自动确认散热状态；必须进行人工检查。";
            _zhDict["WinThermalSensorUnknownTitle"] = "⚠️ 温度传感器不可用 (UNKNOWN)";
            _zhDict["WinThermalSensorUnknownDesc"] = "未能自动确认散热状态；必须进行人工检查。需要手动检查散热器和风扇。";
            _zhDict["WinThermalSensorUnknownRec"] = "建议：在交付计算机前，请在 BIOS/UEFI 中或物理检查风扇运行及通风情况。";
            _zhDict["WinCompletionSummaryWarn"] = "所有软件、驱动程序和优化已完成。警告：压力测试期间 CPU 最高温度达到 {0:F1}°C（建议进行预防性散热维护）。";
            _zhDict["WinCompletionSummaryOk"] = "所有软件、驱动程序、压力测试和系统优化已完成。";
            _zhDict["WinIpcEof"] = "IPC 已结束 (EOF)";
            _zhDict["WinErrorCodeFmt"] = "错误代码 {0}";
            _zhDict["WinAutoStartPaused"] = "自动启动已暂停。已启用手动检查。";
            _zhDict["WinRebootDelayedLive"] = "中间重启已推迟 10 分钟（新时间：{0}）。";
            _zhDict["WinRebootDelayedDialogBody"] = "计算机重启已推迟 10 分钟。\n新重启时间：{0}（累计推迟：{1} 分钟）。";
            _zhDict["WinRebootDelayedDialogTitle"] = "WIS 服务 — 推迟重启";
            _zhDict["WinRebootDelayPersistErrBody"] = "重启已在内存中推迟至 {0}，但写入 deploy-state.json 失败：{1}";
            _zhDict["WinUnknownError"] = "未知错误";
            _zhDict["WinServiceWarningTitle"] = "WIS 服务 — 警告";
            _zhDict["WinAppQueued"] = "排队等待中";
            _zhDict["WinAppPsStartFailed"] = "无法启动 powershell.exe";
            _zhDict["WinAppLaunchErrorPrefix"] = "启动错误：";
            _zhDict["WinAppRetryVerified"] = "安装已成功验证";
            _zhDict["WinAppRetryFailedExit"] = "重新安装失败 (退出码 {0})";
            _zhDict["WinAppRetryTimeout"] = "重新安装超时（超过 15 分钟）";
            _zhDict["WinAppRetryRemaining"] = "软件 {0} 已成功安装。仍需重试：{1}。";
            _zhDict["WinApScanningTitle"] = "正在检查 AUTOPILOT...";
            _zhDict["WinApScanningDesc"] = "系统正在深入检测本地注册表并查询 Microsoft ZTD 云端...";
            _zhDict["WinApScanningRec"] = "请等待安全检查完成...";
            _zhDict["WinApWorkerException"] = "后台线程异常：";
            _zhDict["WinNetWifiType"] = "Wi-Fi（无线网络）";
            _zhDict["WinNetWifiActive"] = "✓ Wi-Fi 连接已激活";
            _zhDict["WinNetLanType"] = "以太网（有线网络）";
            _zhDict["WinNetLanActive"] = "✓ 以太网 (LAN) 已激活";
            _zhDict["WinNetNoConnection"] = "⚠ 无网络连接";
            _zhDict["WinRamAvailableFmt"] = "可用：{0:0.0} GB / {1:0.0} GB";
            _zhDict["WinRamCommitFmt"] = "已提交：{0:0.0} / {1:0.0} GB";
            _zhDict["WinPwrAcVoltageNa"] = "交流电源适配器（电压 N/A）";
            _zhDict["WinPwrBattVoltageNa"] = "电池（电压 N/A）";
            _zhDict["WinPwrBattSubFmt"] = "电池：{0}% ({1})";
            _zhDict["WinPwrCharging"] = "充电中";
            _zhDict["WinPwrAcGrid"] = "交流供电";
            _zhDict["WinPwrDischarging"] = "放电中";
            _zhDict["WinPwrDesktopAc"] = "台式机 / 交流供电";
            _zhDict["WinDrvConnectingHub"] = "正在连接 ServisHub...";
            _zhDict["WinDrvNotFoundOem"] = "未在 OEM 目录中找到";
            _zhDict["WinDrvCachingElapsed"] = "ServiceHub 正在缓存 OEM 驱动程序包 ({0}s)...";
            _zhDict["WinDrvCaching"] = "ServiceHub 正在缓存 OEM 驱动程序包...";
            _zhDict["WinDrvInvalidUrl"] = "无效的下载 URL";
            _zhDict["WinDrvDownloadingOem"] = "正在下载官方 OEM 驱动程序...";
            _zhDict["WinDrvInstallOk"] = "✓ 安装成功！";
            _zhDict["WinDrvAllInstalledSummary"] = "✅ 所有驱动程序已成功安装！（0 个异常设备）";
            _zhDict["WinDrvMissingSummary"] = "⚠ 发现 {0} 个缺少驱动程序或存在错误的设备！";
            _zhDict["WinDrvInstallErrCode"] = "安装错误（代码 {0}）";
            _zhDict["WinDrvConnectHubErr"] = "连接 ServisHub 时出错";
            _zhDict["WinDrvBatchStart"] = "正在开始处理 {0} 个缺失的驱动程序...";
            _zhDict["WinDrvBatchProgress"] = "正在安装 {0}/{1}: {2}...";
            _zhDict["WinDrvBatchDone"] = "批量驱动程序安装完成（成功 {0}/{1}）。";
            _zhDict["WinDrvKeyNoneFound"] = "未发现关键设备";
            _zhDict["WinDrvKeyCheckStart"] = "正在开始检查 0/{0} 个关键驱动程序...";
            _zhDict["WinDrvKeyCheckProgress"] = "正在检查 {0}/{1}: {2}...";
            _zhDict["WinDrvKeyUpdatesFound"] = "发现 {0} 个关键驱动程序更新！";
            _zhDict["WinDrvKeyAllUpToDate"] = "所有关键驱动程序均为最新版本！";
            _zhDict["WinErrCode1"] = "代码 1：设备未正确配置";
            _zhDict["WinErrCode3"] = "代码 3：此设备的驱动程序已损坏";
            _zhDict["WinErrCode10"] = "代码 10：设备无法启动";
            _zhDict["WinErrCode12"] = "代码 12：设备无法找到可用资源";
            _zhDict["WinErrCode14"] = "代码 14：需要重启计算机才能使设备正常工作";
            _zhDict["WinErrCode18"] = "代码 18：请重新安装此设备的驱动程序";
            _zhDict["WinErrCode22"] = "代码 22：设备已被禁用 (Disabled)";
            _zhDict["WinErrCode24"] = "代码 24：设备不存在或无法正常工作";
            _zhDict["WinErrCode28"] = "代码 28：未安装驱动程序 (Missing Driver)";
            _zhDict["WinErrCode29"] = "代码 29：设备在 BIOS 中被禁用";
            _zhDict["WinErrCode31"] = "代码 31：Windows 无法加载此设备的驱动程序";
            _zhDict["WinErrCode32"] = "代码 32：此设备的驱动程序服务已被禁用";
            _zhDict["WinErrCode37"] = "代码 37：Windows 无法初始化设备驱动程序";
            _zhDict["WinErrCode39"] = "代码 39：Windows 无法加载驱动程序（已损坏或丢失）";
            _zhDict["WinErrCode43"] = "代码 43：由于设备报告问题，Windows 已将其停止";
            _zhDict["WinErrCodeDefault"] = "代码 {0}：设备配置存在问题";
            _zhDict["WinCatDisplay"] = "显示适配器";
            _zhDict["WinCatNet"] = "网络适配器";
            _zhDict["WinCatAudio"] = "声音与音频组件";
            _zhDict["WinCatDisk"] = "磁盘与存储设备";
            _zhDict["WinCatCamera"] = "摄像头与生物识别";
            _zhDict["WinCatInput"] = "键盘与指点设备";
            _zhDict["WinCatUsb"] = "USB 与控制器";
            _zhDict["WinCatSystem"] = "系统设备与芯片组";
            _zhDict["WinCatOther"] = "其他设备";
            _zhDict["WinLegendThrottleNa"] = "降频节流 (不可用)";
            _zhDict["WinFan0RpmQuiet"] = "0 RPM（停转/静音模式）";
            _zhDict["WinFanPassiveBios"] = "被动散热 / BIOS 控制";
            _zhDict["WinStressRunningStatus"] = "正在进行连续多核压力测试...";
            _zhDict["WinStressRunningTitle"] = "压力测试进行中...";
            _zhDict["WinStressRunningDesc"] = "正在 100% 全核负载下测量温度与频率...";
            _zhDict["WinStressRunningRec"] = "散热检查完成后请点击“停止压力测试”。";
            _zhDict["WinStressStoppedByTech"] = "技术员已停止压力测试。";
            _zhDict["WinStressActiveTemp"] = "压力测试中 ({0:F0}s) | CPU: {1:F1}°C | 最高: {2:F1}°C";
            _zhDict["WinStressActiveNoSensor"] = "压力测试中 ({0:F0}s) | CPU 传感器: N/A";
            _zhDict["WinStressCritTitle"] = "🔴 急需清理散热并更换导热硅脂";
            _zhDict["WinStressCritAbortDesc"] = "严重过热！为保护硬件，测试已在 {0:F1}°C 时自动中止（阈值 95°C / 97°C）。";
            _zhDict["WinStressCritTempDesc"] = "温度已达到危险的 {0:F1}°C！散热器可能堵塞或导热硅脂已干涸。";
            _zhDict["WinStressCritRec"] = "建议：必须拆机清理风扇与散热鳍片，并重新涂抹高品质导热硅脂。";
            _zhDict["WinStressNoSensorDesc"] = "CPU 已处于 100% 负载，但温度传感器不可用 (0.0°C)。无法自动确认散热状态 — 必须人工检查风扇和出风口！";
            _zhDict["WinStressNoSensorRec"] = "建议：无法通过 WMI/ACPI/OEM 读取传感器数据。交付前请在 BIOS/UEFI 中查看温度并物理检查风扇运行状况。";
            _zhDict["WinStressWarnTitle"] = "🟡 建议进行预防性散热维护";
            _zhDict["WinStressWarnDesc"] = "满载下的最高温度达到了 {0:F1}°C。";
            _zhDict["WinStressWarnRec"] = "建议：导热硅脂开始老化或存在积灰，建议近期安排清理维护。";
            _zhDict["WinStressOkTitle"] = "🟢 散热表现优秀";
            _zhDict["WinStressOkDesc"] = "满载下最高温度仅为 {0:F1}°C！";
            _zhDict["WinStressOkRec"] = "建议：散热系统与导热硅脂处于极佳的出厂状态，无需维护。";
            _zhDict["WinStressAbortStatus"] = "已中止：触发过热保护阈值 ({0:F1}°C)！";
            _zhDict["WinStressStoppedTemp"] = "压力测试在 {0} 秒后停止 (CPU 最高: {1:F1}°C)";
            _zhDict["WinStressStoppedNoSensor"] = "压力测试在 {0} 秒后停止 (传感器: N/A)";
            _zhDict["WinStressDoneTemp"] = "测试成功完成 ({0}s | CPU 最高: {1:F1}°C)";
            _zhDict["WinStressDoneNoSensor"] = "测试已完成 ({0}s | 温度传感器不可用)";
            _zhDict["WinThermalOverheatNoteFmt"] = "注意：压力测试期间处理器温度达到临界值（{0:F1}°C >= 95°C）— 需要清理散热器并更换导热硅脂。";
            _zhDict["WinIpcDisconnected"] = "IPC 已断开";
            _zhDict["WinAppPhase4ScriptMissing"] = "未找到 Phase4-Apps.ps1";
            _zhDict["WinNasConnected"] = "已连接 (192.168.170.254)";
            _zhDict["WinNasOffline"] = "本地（离线）";
            _zhDict["WinDrvInstallingPnputil"] = "正在安装驱动 (pnputil)...";
            _zhDict["WinDrvUpdateAvailableFmt"] = "发现新版本: {0}";
            _zhDict["WinDrvLatestBadge"] = "✓ 已是最新驱动";
            _zhDict["WinDrvUpgradingBadge"] = "正在升级...";
            _zhDict["WinDrvInstalledOkDesc"] = "已正确安装";
            _zhDict["WinCpuTempWmiNa"] = "N/A（WMI/ACPI 不可用）";
            _zhDict["WinGpuTempIgpuNa"] = "N/A（核显 / 空闲）";
            _zhDict["WinStorageTempNa"] = "N/A（传感器不可用）";
            _zhDict["WinThermalSensorNative"] = "● 原生传感器 ({0}, 1 Hz)";
            _zhDict["WinThermalSensorNa"] = "● 温度传感器不可用 (N/A)";
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
