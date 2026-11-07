using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WIS.UI
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            if (execute == null) throw new ArgumentNullException("execute");
            _execute = execute;
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute != null ? (Predicate<object>)(_ => canExecute()) : null)
        {
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }

    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public class PhaseItem : BaseViewModel
    {
        private int _number;
        private string _title;
        private string _subtitle;
        private string _status = "Pending"; // "Pending", "Active", "Completed"

        public int Number
        {
            get { return _number; }
            set { SetProperty(ref _number, value); }
        }

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }

        public string Subtitle
        {
            get { return _subtitle; }
            set { SetProperty(ref _subtitle, value); }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged("IsPending");
                    OnPropertyChanged("IsActive");
                    OnPropertyChanged("IsCompleted");
                    OnPropertyChanged("BadgeBackground");
                    OnPropertyChanged("BadgeForeground");
                    OnPropertyChanged("BorderBrush");
                    OnPropertyChanged("IconText");
                }
            }
        }

        public bool IsPending { get { return string.Equals(Status, "Pending", StringComparison.OrdinalIgnoreCase); } }
        public bool IsActive { get { return string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase); } }
        public bool IsCompleted { get { return string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase); } }

        public Brush BadgeBackground
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(46, 204, 113); // #2ecc71
                if (IsActive) return CreateFrozenBrush(0, 122, 204);     // #007acc
                return CreateFrozenBrush(50, 50, 55);                    // #323237
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                if (IsCompleted || IsActive) return Brushes.White;
                return CreateFrozenBrush(150, 150, 150);
            }
        }

        public Brush BorderBrush
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(39, 174, 96);
                if (IsActive) return CreateFrozenBrush(78, 201, 176); // #4ec9b0
                return CreateFrozenBrush(70, 70, 75);
            }
        }

        public string IconText
        {
            get
            {
                if (IsCompleted) return "✓";
                if (IsActive) return "▶";
                return Number.ToString();
            }
        }
    }

    public class AppItem : BaseViewModel
    {
        private string _id;
        private string _name;
        private string _status = "WAIT"; // "WAIT", "RUNNING", "OK", "ERROR", "SKIPPED"
        private string _detail = "U redu čekanja";
        private string _iconGlyph = "📦";

        public string Id
        {
            get { return _id; }
            set
            {
                if (SetProperty(ref _id, value))
                {
                    if (string.IsNullOrEmpty(_iconGlyph) || _iconGlyph == "📦")
                    {
                        _iconGlyph = GetGlyphForAppId(value);
                        OnPropertyChanged("IconGlyph");
                    }
                }
            }
        }

        public string Name
        {
            get { return _name; }
            set { SetProperty(ref _name, value); }
        }

        public string IconGlyph
        {
            get { return _iconGlyph; }
            set { SetProperty(ref _iconGlyph, value); }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                string norm = (value ?? "WAIT").ToUpperInvariant().Trim();
                if (norm == "COMPLETED" || norm == "SUCCESS" || norm == "INSTALLED") norm = "OK";
                else if (norm == "FAILED") norm = "ERROR";
                else if (norm == "SKIPPED") norm = "SKIPPED";
                else if (norm == "PENDING" || norm == "QUEUED") norm = "WAIT";

                if (SetProperty(ref _status, norm))
                {
                    OnPropertyChanged("BadgeBackground");
                    OnPropertyChanged("BadgeForeground");
                    OnPropertyChanged("StatusDisplay");
                    OnPropertyChanged("IconText");
                    OnPropertyChanged("IsRunning");
                    OnPropertyChanged("IsCompleted");
                    OnPropertyChanged("IsError");
                }
            }
        }

        public string Detail
        {
            get { return _detail; }
            set { SetProperty(ref _detail, value); }
        }

        public string StatusDisplay { get { return Status; } }

        public bool IsRunning { get { return Status == "RUNNING"; } }
        public bool IsCompleted { get { return Status == "OK"; } }
        public bool IsError { get { return Status == "ERROR"; } }

        public Brush BadgeBackground
        {
            get
            {
                switch (Status)
                {
                    case "OK":
                        return CreateFrozenBrush(39, 174, 96); // Green #27ae60
                    case "RUNNING":
                        return CreateFrozenBrush(243, 156, 18); // Orange #f39c12
                    case "ERROR":
                        return CreateFrozenBrush(231, 76, 60); // Red #e74c3c
                    case "SKIPPED":
                        return CreateFrozenBrush(52, 73, 94); // Plavo-siva #34495E
                    case "WAIT":
                    default:
                        return CreateFrozenBrush(65, 65, 70); // Dark Gray
                }
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                switch (Status)
                {
                    case "OK":
                    case "RUNNING":
                    case "ERROR":
                    case "SKIPPED":
                        return Brushes.White;
                    default:
                        return CreateFrozenBrush(180, 180, 180);
                }
            }
        }

        public string IconText
        {
            get
            {
                bool isZh = LocalizationService.Instance.IsChinese;
                switch (Status)
                {
                    case "OK": return isZh ? "✓ 完成" : "✓ OK";
                    case "RUNNING": return isZh ? "⚙ 正在进行" : "⚙ RAD";
                    case "ERROR": return isZh ? "✗ 错误" : "✗ GRESKA";
                    case "SKIPPED": return isZh ? "⏭ 已跳过" : "⏭ PRESKOČENO";
                    case "WAIT":
                    default: return isZh ? "⏳ 等待中" : "⏳ ČEKA";
                }
            }
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged("IconText");
        }

        public static string GetGlyphForAppId(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "📦";
            string idLower = appId.ToLowerInvariant();
            if (idLower.Contains("dotnet")) return "⚡";
            if (idLower.Contains("chrome")) return "🌐";
            if (idLower.Contains("winrar") || idLower.Contains("7zip") || idLower.Contains("zip")) return "🗜️";
            if (idLower.Contains("acrobat") || idLower.Contains("adobe") || idLower.Contains("pdf")) return "📄";
            if (idLower.Contains("office") || idLower.Contains("word") || idLower.Contains("excel")) return "📊";
            if (idLower.Contains("battery")) return "🔋";
            if (idLower.Contains("license") || idLower.Contains("licenc")) return "🔑";
            if (idLower.Contains("shortcut") || idLower.Contains("precic")) return "🔗";
            return "📦";
        }
    }

    public class LogEntry : BaseViewModel
    {
        public string Time { get; set; }
        public string Level { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }

        public LogEntry()
        {
            Time = DateTime.Now.ToString("HH:mm:ss");
            Level = "INFO";
            Tag = "[Sistem]";
            Message = "";
        }

        public Brush LevelBrush
        {
            get
            {
                string l = (Level ?? "").ToUpperInvariant().Trim();
                switch (l)
                {
                    case "SUCCESS":
                    case "OK":
                        return CreateFrozenBrush(46, 204, 113);
                    case "WARN":
                    case "WARNING":
                        return CreateFrozenBrush(241, 196, 15);
                    case "ERROR":
                    case "CRITICAL":
                    case "FAIL":
                        return CreateFrozenBrush(231, 76, 60);
                    case "DEBUG":
                        return CreateFrozenBrush(140, 140, 140);
                    case "INFO":
                    default:
                        return CreateFrozenBrush(78, 201, 176); // Cyan / #4ec9b0
                }
            }
        }

        public string FullDisplay
        {
            get { return string.Format("{0} [{1}] {2} {3}", Time, Level, Tag, Message); }
        }
    }

    public class PnpProblemDevice : BaseViewModel
    {
        private string _name = "Nepoznat uređaj";
        private string _deviceId = "";
        private int _errorCode = 0;
        private string _errorStatus = "Greška drajvera";

        public string Name { get { return _name; } set { SetProperty(ref _name, value); } }
        public string DeviceId { get { return _deviceId; } set { SetProperty(ref _deviceId, value); } }
        public int ErrorCode { get { return _errorCode; } set { SetProperty(ref _errorCode, value); } }
        public string ErrorStatus { get { return _errorStatus; } set { SetProperty(ref _errorStatus, value); } }

        public string DisplayText
        {
            get { return string.Format("{0} [{1}]", _name, _errorStatus); }
        }
    }

    public class AutopilotViewModel : BaseViewModel
    {
        private string _matrixCode = "PENDING";
        private string _statusLevel = "INFO";
        private string _statusTitle = "PROVERA AUTOPILOTA U TOKU...";
        private string _statusDescription = "Sistem vrši duboku forenziku registra, sertifikata i proveru Microsoft ZTD oblaka.";
        private string _recommendedAction = "Sačekajte završetak automatske analize...";
        private Brush _statusBrush = CreateFrozenBrush(140, 140, 140);

        // Hardverski otisak
        private string _hwManufacturer = "Učitavanje...";
        private string _hwModel = "Učitavanje...";
        private string _hwSerialNumber = "Učitavanje...";
        private string _hwSmbiosUuid = "Učitavanje...";
        private bool _tpmPresent = false;
        private bool _tpmReady = false;
        private string _tpmStatusText = "Provera...";
        private Brush _tpmStatusBrush = CreateFrozenBrush(140, 140, 140);

        // Lokalna forenzika
        private bool _hasLocalTraces = false;
        private string _localTracesText = "Čisto (Nema registarskih tragova)";
        private Brush _localTracesBrush = CreateFrozenBrush(46, 204, 113);
        private string _tenantDomain = "Nema (Nije dodeljen)";
        private string _tenantId = "N/A";
        private string _assignedDeviceName = "N/A";
        private string _azureAdStatus = "Nije pridružen (NO)";
        private string _mdmStatus = "Nije upisan (NO)";
        private int _orgCertificatesCount = 0;

        // Cloud ZTD
        private bool _cloudChecked = false;
        private bool _isRegistered = false;
        private bool _isLocked = false;
        private string _cloudCheckedText = "Čeka se provera...";
        private string _cloudStatusText = "Povezivanje sa ztd.dds.microsoft.com...";
        private Brush _cloudStatusBrush = CreateFrozenBrush(140, 140, 140);
        private string _ztdErrorCode = "N/A";
        private string _profileTenantDomain = "";
        private string _profileTenantId = "";
        private string _rawStatusMessage = "N/A";

        // UI Stanje
        private bool _isAlertActive = false;
        private bool _isScanning = false;
        private string _lastScanTime = "Nije pokrenuto";
        private ICommand _rescanCommand;

        public bool IsAlertActive { get { return _isAlertActive; } set { SetProperty(ref _isAlertActive, value); } }

        public string MatrixCode
        {
            get { return _matrixCode; }
            set
            {
                if (SetProperty(ref _matrixCode, value))
                {
                    UpdateStatusBrushes();
                }
            }
        }

        public string StatusLevel
        {
            get { return _statusLevel; }
            set
            {
                if (SetProperty(ref _statusLevel, value))
                {
                    UpdateStatusBrushes();
                }
            }
        }

        public string StatusTitle { get { return _statusTitle; } set { SetProperty(ref _statusTitle, value); } }
        public string StatusDescription { get { return _statusDescription; } set { SetProperty(ref _statusDescription, value); } }
        public string RecommendedAction { get { return _recommendedAction; } set { SetProperty(ref _recommendedAction, value); } }
        public Brush StatusBrush { get { return _statusBrush; } set { SetProperty(ref _statusBrush, value); } }

        public string HwManufacturer { get { return _hwManufacturer; } set { SetProperty(ref _hwManufacturer, value); } }
        public string HwModel { get { return _hwModel; } set { SetProperty(ref _hwModel, value); } }
        public string HwSerialNumber { get { return _hwSerialNumber; } set { SetProperty(ref _hwSerialNumber, value); } }
        public string HwSmbiosUuid { get { return _hwSmbiosUuid; } set { SetProperty(ref _hwSmbiosUuid, value); } }

        public bool TpmPresent
        {
            get { return _tpmPresent; }
            set
            {
                if (SetProperty(ref _tpmPresent, value))
                {
                    UpdateTpmDisplay();
                }
            }
        }

        public bool TpmReady
        {
            get { return _tpmReady; }
            set
            {
                if (SetProperty(ref _tpmReady, value))
                {
                    UpdateTpmDisplay();
                }
            }
        }

        public string TpmStatusText { get { return _tpmStatusText; } set { SetProperty(ref _tpmStatusText, value); } }
        public Brush TpmStatusBrush { get { return _tpmStatusBrush; } set { SetProperty(ref _tpmStatusBrush, value); } }

        public bool HasLocalTraces
        {
            get { return _hasLocalTraces; }
            set
            {
                if (SetProperty(ref _hasLocalTraces, value))
                {
                    LocalTracesText = value ? "PRONAĐENI TRAGOVI U REGISTRU" : "Čisto (Nema registarskih zapisa)";
                    LocalTracesBrush = value ? CreateFrozenBrush(231, 76, 60) : CreateFrozenBrush(46, 204, 113);
                }
            }
        }

        public string LocalTracesText { get { return _localTracesText; } set { SetProperty(ref _localTracesText, value); } }
        public Brush LocalTracesBrush { get { return _localTracesBrush; } set { SetProperty(ref _localTracesBrush, value); } }
        public string TenantDomain { get { return _tenantDomain; } set { SetProperty(ref _tenantDomain, value); } }
        public string TenantId { get { return _tenantId; } set { SetProperty(ref _tenantId, value); } }
        public string AssignedDeviceName { get { return _assignedDeviceName; } set { SetProperty(ref _assignedDeviceName, value); } }
        public string AzureAdStatus { get { return _azureAdStatus; } set { SetProperty(ref _azureAdStatus, value); } }
        public string MdmStatus { get { return _mdmStatus; } set { SetProperty(ref _mdmStatus, value); } }
        public int OrgCertificatesCount { get { return _orgCertificatesCount; } set { SetProperty(ref _orgCertificatesCount, value); } }

        public bool CloudChecked
        {
            get { return _cloudChecked; }
            set
            {
                if (SetProperty(ref _cloudChecked, value))
                {
                    CloudCheckedText = value ? "Završena online provera" : "Offline / Nije provereno";
                }
            }
        }

        public bool IsRegistered
        {
            get { return _isRegistered; }
            set
            {
                if (SetProperty(ref _isRegistered, value))
                {
                    UpdateCloudDisplay();
                }
            }
        }

        public bool IsLocked
        {
            get { return _isLocked; }
            set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    UpdateAlertState();
                }
            }
        }

        public string CloudCheckedText { get { return _cloudCheckedText; } set { SetProperty(ref _cloudCheckedText, value); } }
        public string CloudStatusText { get { return _cloudStatusText; } set { SetProperty(ref _cloudStatusText, value); } }
        public Brush CloudStatusBrush { get { return _cloudStatusBrush; } set { SetProperty(ref _cloudStatusBrush, value); } }
        public string ZtdErrorCode { get { return _ztdErrorCode; } set { SetProperty(ref _ztdErrorCode, value); } }
        public string ProfileTenantDomain { get { return _profileTenantDomain; } set { SetProperty(ref _profileTenantDomain, value); } }
        public string ProfileTenantId { get { return _profileTenantId; } set { SetProperty(ref _profileTenantId, value); } }
        public string RawStatusMessage { get { return _rawStatusMessage; } set { SetProperty(ref _rawStatusMessage, value); } }

        public bool IsScanning { get { return _isScanning; } set { SetProperty(ref _isScanning, value); } }
        public string LastScanTime { get { return _lastScanTime; } set { SetProperty(ref _lastScanTime, value); } }
        public ICommand RescanCommand { get { return _rescanCommand; } set { SetProperty(ref _rescanCommand, value); } }

        public AutopilotViewModel()
        {
            UpdateTpmDisplay();
            UpdateStatusBrushes();
            UpdateAlertState();
        }

        public void RefreshLocalization()
        {
            UpdateTpmDisplay();
            UpdateCloudDisplay();
            UpdateTracesDisplay();
            UpdateMatrixDisplay();
        }

        public void UpdateTracesDisplay()
        {
            if (_hasLocalTraces)
            {
                LocalTracesText = LocalizationService.Instance.GetString("ApTracesRegistryFound", "PRONAĐENI TRAGOVI U REGISTRU");
            }
            else
            {
                LocalTracesText = LocalizationService.Instance.GetString("ApTracesClean", "Čisto (Nema registarskih tragova)");
            }
        }

        public void UpdateMatrixDisplay()
        {
            string code = (_matrixCode ?? "").ToUpperInvariant().Trim();
            switch (code)
            {
                case "LOCKED_TENANT":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_LockedTenant", "🔴 ZAKLJUČAN U ORGANIZACIJI (TENANT LOCK)");
                    StatusDescription = string.Format(LocalizationService.Instance.GetString("ApMatrixDesc_LockedTenant", "Uređaj je registrovan u Microsoft oblaku ({0}). OOBE zahteva nalog korporacije!"), _tenantDomain);
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_LockedTenant", "OBAVEZNO KONTAKTIRATI KORPORACIJU ILI PRODAVCA ZA DEREGISTRACIJU IZ INTUNE-A.");
                    break;
                case "RELEASED_LOCAL_TRACES":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_ReleasedLocalTraces", "🟡 OSLOBOĐEN U OBLAKU, LOKALNI TRAGOVI");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_ReleasedLocalTraces", "Uređaj je oslobođen iz Autopilot baze, ali na disku postoje ostaci prethodne organizacije.");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_ReleasedLocalTraces", "Preporučuje se čista instalacija (Clean Install / OOBE reset) radi brisanja lokalnih sertifikata.");
                    break;
                case "HIDDEN_LOCK":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_HiddenLock", "🔴 SKRIVENA OPASNOST - ZAKLJUČAN U OBLAKU (DISK OBRISAN)");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_HiddenLock", "Lokalni sistem nema tragova, ali je hardverski hash ovog računara i dalje vezan u Microsoft bazi!");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_HiddenLock", "Računar ne sme ići u dalju prodaju dok ga vlasnik tenanta ne oslobodi iz Microsoft Intune portala.");
                    break;
                case "CLEAN":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_Clean", "🟢 100% ČISTO - UREĐAJ JE POTPUNO SLOBODAN");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_Clean", "Hardverski otisak nije registrovan u Microsoft oblaku. Nema lokalnih MDM/Autopilot tragova.");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_Clean", "Uređaj je u potpunosti čist i bezbedan za rad bilo kog korisnika.");
                    break;
                case "OFFLINE_LOCAL_TRACES":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_OfflineLocalTraces", "🟡 OFFLINE - DETEKTOVANI LOKALNI TRAGOVI");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_OfflineLocalTraces", "Nema internet konekcije za proveru oblaka, a pronađeni su registarski tragovi organizacije.");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_OfflineLocalTraces", "Povežite internet i kliknite 'Osveži Proveru' za online proveru.");
                    break;
                case "OFFLINE_CLEAN_LOCAL":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_OfflineCleanLocal", "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OFFLINE)");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_OfflineCleanLocal", "Lokalni sistem nema tragova, ali je računar bio OFFLINE. Status u oblaku ostaje nepoznat.");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_OfflineCleanLocal", "Za stopostotnu sigurnost, povežite računar na mrežu i ponovite test.");
                    break;
                case "PENDING":
                case "SCANNING":
                    StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_Scanning", "PROVERA AUTOPILOTA U TOKU...");
                    StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_Scanning", "Sistem vrši duboku forenziku lokalnog registra i proveru Microsoft ZTD oblaka...");
                    RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_Scanning", "Molimo sačekajte završetak bezbednosne provere...");
                    break;
            }
        }

        private void UpdateTpmDisplay()
        {
            if (_tpmPresent && _tpmReady)
            {
                TpmStatusText = LocalizationService.Instance.GetString("ApTpmReadyActive", "Spreman (TPM 2.0 aktivan)");
                TpmStatusBrush = CreateFrozenBrush(46, 204, 113);
            }
            else if (_tpmPresent)
            {
                TpmStatusText = LocalizationService.Instance.GetString("ApTpmPresentNotReady", "Prisutan (Nije spreman)");
                TpmStatusBrush = CreateFrozenBrush(241, 196, 15);
            }
            else
            {
                TpmStatusText = LocalizationService.Instance.GetString("ApTpmMissing", "Nije detektovan / Nema TPM");
                TpmStatusBrush = CreateFrozenBrush(231, 76, 60);
            }
        }

        private void UpdateCloudDisplay()
        {
            if (_cloudChecked)
            {
                CloudCheckedText = LocalizationService.Instance.GetString("ApCloudOnlineChecked", "Završena online provera");
                if (_isRegistered)
                {
                    CloudStatusText = LocalizationService.Instance.GetString("ApCloudRegistered", "REGISTROVAN U MICROSOFT OBLAKU");
                    CloudStatusBrush = CreateFrozenBrush(231, 76, 60);
                }
                else
                {
                    CloudStatusText = LocalizationService.Instance.GetString("ApCloudFree", "Slobodan (Nije registrovan u oblaku)");
                    CloudStatusBrush = CreateFrozenBrush(46, 204, 113);
                }
            }
            else
            {
                CloudCheckedText = LocalizationService.Instance.GetString("ApCloudOffline", "Offline / Nije provereno");
                CloudStatusText = LocalizationService.Instance.GetString("ApCloudNotVerified", "ZTD Oblak nije proveren (Offline)");
                CloudStatusBrush = CreateFrozenBrush(241, 196, 15);
            }
        }

        public void UpdateAlertState()
        {
            string lvl = (_statusLevel ?? "").ToUpperInvariant().Trim();
            string code = (_matrixCode ?? "").ToUpperInvariant().Trim();
            if (_isLocked || lvl == "CRITICAL" || code == "LOCKED_TENANT" || code == "HIDDEN_LOCK")
            {
                IsAlertActive = true;
            }
            else
            {
                IsAlertActive = false;
            }
        }

        private void UpdateStatusBrushes()
        {
            string lvl = (_statusLevel ?? "").ToUpperInvariant().Trim();
            switch (lvl)
            {
                case "CRITICAL":
                case "ERROR":
                    StatusBrush = CreateFrozenBrush(231, 76, 60);
                    break;
                case "WARNING":
                case "WARN":
                    StatusBrush = CreateFrozenBrush(241, 196, 15);
                    break;
                case "OK":
                case "SUCCESS":
                    StatusBrush = CreateFrozenBrush(46, 204, 113);
                    break;
                default:
                    StatusBrush = CreateFrozenBrush(0, 122, 204);
                    break;
            }
            UpdateAlertState();
        }
    }

    public class HardwareInfoViewModel : BaseViewModel
    {
        // CPU
        private string _cpuModel = "Učitavanje CPU...";
        private string _cpuCoresThreads = "N/A";
        private string _cpuClock = "N/A";
        private string _cpuSocket = "N/A";

        public string CpuModel { get { return _cpuModel; } set { SetProperty(ref _cpuModel, value); } }
        public string CpuCoresThreads { get { return _cpuCoresThreads; } set { SetProperty(ref _cpuCoresThreads, value); } }
        public string CpuClock { get { return _cpuClock; } set { SetProperty(ref _cpuClock, value); } }
        public string CpuSocket { get { return _cpuSocket; } set { SetProperty(ref _cpuSocket, value); } }

        // RAM
        private string _ramSummary = "Učitavanje RAM...";
        private string _ramDetails = "N/A";
        private string _ramSpeed = "N/A";
        private string _ramType = "N/A";
        private bool _isRamSoldered = false;
        private string _ramSolderedBadge = "Standardni moduli (SO-DIMM)";

        public string RamSummary { get { return _ramSummary; } set { SetProperty(ref _ramSummary, value); } }
        public string RamDetails { get { return _ramDetails; } set { SetProperty(ref _ramDetails, value); } }
        public string RamSpeed { get { return _ramSpeed; } set { SetProperty(ref _ramSpeed, value); } }
        public string RamType { get { return _ramType; } set { SetProperty(ref _ramType, value); } }
        public bool IsRamSoldered
        {
            get { return _isRamSoldered; }
            set
            {
                if (SetProperty(ref _isRamSoldered, value))
                {
                    RefreshLocalization();
                }
            }
        }
        public string RamSolderedBadge { get { return _ramSolderedBadge; } set { SetProperty(ref _ramSolderedBadge, value); } }

        public void RefreshLocalization()
        {
            bool isZh = LocalizationService.Instance.IsChinese;
            if (_isRamSoldered)
            {
                RamSolderedBadge = isZh ? "板载集成 (LPDDR / 焊接)" : "Integrisan (LPDDR / Soldered)";
            }
            else
            {
                RamSolderedBadge = isZh ? "标准模块 (SO-DIMM)" : "Standardni moduli (SO-DIMM)";
            }

            if (_hasIrCamera)
            {
                IrCameraStatus = LocalizationService.Instance.GetString("HwIrYes", "👁️ Da (Windows Hello Face)");
            }
            else if (_irCameraStatus != "Detekcija IR..." && _irCameraStatus != "检测中...")
            {
                IrCameraStatus = LocalizationService.Instance.GetString("HwIrNo", "Ne (Standardna RGB kamera)");
            }

            if (_hasFingerprint)
            {
                string devName = !string.IsNullOrEmpty(_fingerprintDeviceName) ? _fingerprintDeviceName : "Sensor";
                FingerprintSensor = string.Format(LocalizationService.Instance.GetString("HwFpYes", "👆 Da ({0})"), devName);
            }
            else if (_fingerprintSensor != "Detekcija senzora..." && _fingerprintSensor != "检测中...")
            {
                FingerprintSensor = LocalizationService.Instance.GetString("HwFpNo", "Nije detektovan");
            }

            if (_hasBacklitKeyboard)
            {
                BacklitKeyboardStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
            }
            else if (_backlitIsManual)
            {
                BacklitKeyboardStatus = LocalizationService.Instance.GetString("HwKbCheckManual", "Standardna / Proveri ručno (Fn+Space)");
            }
            else if (_backlitKeyboardStatus != "Detekcija..." && _backlitKeyboardStatus != "检测中...")
            {
                BacklitKeyboardStatus = LocalizationService.Instance.GetString("HwKbNo", "Standardna (Bez osvetljenja)");
            }

            if (_hasTouchscreen)
            {
                TouchscreenStatus = LocalizationService.Instance.GetString("HwTouchYesSimple", "🖐️ Podržan (Touchscreen)");
            }
            else if (_touchscreenStatus != "Detekcija..." && _touchscreenStatus != "检测中...")
            {
                TouchscreenStatus = LocalizationService.Instance.GetString("HwTouchNo", "Nije detektovan (Standardni displej)");
            }

            if (!string.IsNullOrEmpty(_speakersDeviceName))
            {
                AudioSpeakers = "🔊 " + _speakersDeviceName;
            }
            else if (_audioSpeakers != "Detekcija..." && _audioSpeakers != "检测中...")
            {
                AudioSpeakers = LocalizationService.Instance.GetString("HwAudioSpeakersDefault", "Standardni Audio uređaj");
            }

            if (!string.IsNullOrEmpty(_micDeviceName))
            {
                MicrophoneStatus = string.Format(LocalizationService.Instance.GetString("HwMicYes", "🎤 Da ({0})"), _micDeviceName);
            }
            else if (_microphoneStatus != "Detekcija..." && _microphoneStatus != "检测中...")
            {
                MicrophoneStatus = LocalizationService.Instance.GetString("HwMicNo", "Nije detektovan");
            }
        }
        private string _ramSlotsSummary = "Detekcija slotova...";
        public string RamSlotsSummary { get { return _ramSlotsSummary; } set { SetProperty(ref _ramSlotsSummary, value); } }
        public int RamTotalSlots { get; set; }
        public int RamUsedSlots { get; set; }
        public int RamFreeSlots { get; set; }

        // SSD / Skladište
        private string _storageModel = "Učitavanje SSD/Diska...";
        private string _storageType = "NVMe / SATA";
        private string _storageHealth = "100%";
        private string _storageHours = "N/A (Zahteva S.M.A.R.T.)";
        private string _storageTemp = "N/A (Senzor nedostupan)";
        private SolidColorBrush _storageHealthBrush = CreateFrozenBrush(46, 204, 113);

        public string StorageModel { get { return _storageModel; } set { SetProperty(ref _storageModel, value); } }
        public string StorageType { get { return _storageType; } set { SetProperty(ref _storageType, value); } }
        public string StorageHealth { get { return _storageHealth; } set { SetProperty(ref _storageHealth, value); } }
        public string StorageHours { get { return _storageHours; } set { SetProperty(ref _storageHours, value); } }
        public string StorageTemp { get { return _storageTemp; } set { SetProperty(ref _storageTemp, value); } }
        public SolidColorBrush StorageHealthBrush { get { return _storageHealthBrush; } set { SetProperty(ref _storageHealthBrush, value); } }

        // Baterija / Napajanje
        private string _batteryWear = "N/A";
        private string _batteryCapacity = "N/A";
        private string _batteryCycles = "N/A";
        private string _batteryPowerStatus = "N/A";
        private SolidColorBrush _batteryHealthBrush = CreateFrozenBrush(46, 204, 113);

        public string BatteryWear { get { return _batteryWear; } set { SetProperty(ref _batteryWear, value); } }
        public string BatteryCapacity { get { return _batteryCapacity; } set { SetProperty(ref _batteryCapacity, value); } }
        public string BatteryCycles { get { return _batteryCycles; } set { SetProperty(ref _batteryCycles, value); } }
        public string BatteryPowerStatus { get { return _batteryPowerStatus; } set { SetProperty(ref _batteryPowerStatus, value); } }
        public SolidColorBrush BatteryHealthBrush { get { return _batteryHealthBrush; } set { SetProperty(ref _batteryHealthBrush, value); } }

        // GPU & Displej
        private string _gpuPrimary = "Učitavanje GPU...";
        private string _gpuSecondary = "";
        private bool _hasDualGpu = false;
        private string _displaySpec = "Detekcija displeja...";

        public string GpuPrimary { get { return _gpuPrimary; } set { SetProperty(ref _gpuPrimary, value); } }
        public string GpuSecondary { get { return _gpuSecondary; } set { SetProperty(ref _gpuSecondary, value); } }
        public bool HasDualGpu { get { return _hasDualGpu; } set { SetProperty(ref _hasDualGpu, value); } }
        public string DisplaySpec { get { return _displaySpec; } set { SetProperty(ref _displaySpec, value); } }

        private string _gpuPrimaryVram = "Učitavanje...";
        public string GpuPrimaryVram { get { return _gpuPrimaryVram; } set { SetProperty(ref _gpuPrimaryVram, value); } }
        private string _gpuSecondaryVram = "";
        public string GpuSecondaryVram { get { return _gpuSecondaryVram; } set { SetProperty(ref _gpuSecondaryVram, value); } }

        private bool _hasTouchscreen = false;
        public bool HasTouchscreen { get { return _hasTouchscreen; } set { SetProperty(ref _hasTouchscreen, value); } }
        private string _touchscreenStatus = "Detekcija...";
        public string TouchscreenStatus { get { return _touchscreenStatus; } set { SetProperty(ref _touchscreenStatus, value); } }
        private SolidColorBrush _touchscreenBrush = CreateFrozenBrush(136, 136, 136);
        public SolidColorBrush TouchscreenBrush { get { return _touchscreenBrush; } set { SetProperty(ref _touchscreenBrush, value); } }

        // Matična ploča & BIOS
        private string _motherboardModel = "Učitavanje ploče...";
        private string _motherboardManufacturer = "N/A";
        private string _serialNumber = "N/A";
        private string _biosVersionDate = "N/A";
        private string _secureBootStatus = "N/A";

        public string MotherboardModel { get { return _motherboardModel; } set { SetProperty(ref _motherboardModel, value); } }
        public string MotherboardManufacturer { get { return _motherboardManufacturer; } set { SetProperty(ref _motherboardManufacturer, value); } }
        public string SerialNumber { get { return _serialNumber; } set { SetProperty(ref _serialNumber, value); } }
        public string BiosVersionDate { get { return _biosVersionDate; } set { SetProperty(ref _biosVersionDate, value); } }
        public string SecureBootStatus { get { return _secureBootStatus; } set { SetProperty(ref _secureBootStatus, value); } }

        // Periferije & Biometrija (v2.7.2)
        private string _webcamModel = "Učitavanje kamere...";
        private bool _hasWebcam = false;
        private SolidColorBrush _webcamBrush = CreateFrozenBrush(136, 136, 136);

        public string WebcamModel { get { return _webcamModel; } set { SetProperty(ref _webcamModel, value); } }
        public bool HasWebcam { get { return _hasWebcam; } set { SetProperty(ref _hasWebcam, value); } }
        public SolidColorBrush WebcamBrush { get { return _webcamBrush; } set { SetProperty(ref _webcamBrush, value); } }

        private string _irCameraStatus = "Detekcija IR...";
        private bool _hasIrCamera = false;
        private SolidColorBrush _irCameraBrush = CreateFrozenBrush(136, 136, 136);

        public string IrCameraStatus { get { return _irCameraStatus; } set { SetProperty(ref _irCameraStatus, value); } }
        public bool HasIrCamera { get { return _hasIrCamera; } set { SetProperty(ref _hasIrCamera, value); } }
        public SolidColorBrush IrCameraBrush { get { return _irCameraBrush; } set { SetProperty(ref _irCameraBrush, value); } }

        private string _backlitKeyboardStatus = "Detekcija...";
        private bool _hasBacklitKeyboard = false;
        private SolidColorBrush _backlitKeyboardBrush = CreateFrozenBrush(136, 136, 136);

        public string BacklitKeyboardStatus { get { return _backlitKeyboardStatus; } set { SetProperty(ref _backlitKeyboardStatus, value); } }
        public bool HasBacklitKeyboard { get { return _hasBacklitKeyboard; } set { SetProperty(ref _hasBacklitKeyboard, value); } }
        public SolidColorBrush BacklitKeyboardBrush { get { return _backlitKeyboardBrush; } set { SetProperty(ref _backlitKeyboardBrush, value); } }

        private string _fingerprintSensor = "Detekcija senzora...";
        private bool _hasFingerprint = false;
        private SolidColorBrush _fingerprintBrush = CreateFrozenBrush(136, 136, 136);

        public string FingerprintSensor { get { return _fingerprintSensor; } set { SetProperty(ref _fingerprintSensor, value); } }
        public bool HasFingerprint { get { return _hasFingerprint; } set { SetProperty(ref _hasFingerprint, value); } }
        public SolidColorBrush FingerprintBrush { get { return _fingerprintBrush; } set { SetProperty(ref _fingerprintBrush, value); } }

        // Model panela, osvetljenje i raspon boja (v2.14.0 - HW-03)
        private string _displayModel = "N/A";
        private string _displayNits = "N/A";
        private string _displayColorGamut = "N/A";

        public string DisplayModel { get { return _displayModel; } set { SetProperty(ref _displayModel, value); } }
        public string DisplayNits { get { return _displayNits; } set { SetProperty(ref _displayNits, value); } }
        public string DisplayColorGamut { get { return _displayColorGamut; } set { SetProperty(ref _displayColorGamut, value); } }

        // Audio zvučnici i mikrofon (v2.14.0 - HW-07)
        private string _audioSpeakers = "Detekcija...";
        private SolidColorBrush _audioSpeakersBrush = CreateFrozenBrush(136, 136, 136);
        private string _microphoneStatus = "Detekcija...";
        private SolidColorBrush _microphoneBrush = CreateFrozenBrush(136, 136, 136);

        public string AudioSpeakers { get { return _audioSpeakers; } set { SetProperty(ref _audioSpeakers, value); } }
        public SolidColorBrush AudioSpeakersBrush { get { return _audioSpeakersBrush; } set { SetProperty(ref _audioSpeakersBrush, value); } }
        public string MicrophoneStatus { get { return _microphoneStatus; } set { SetProperty(ref _microphoneStatus, value); } }
        public SolidColorBrush MicrophoneBrush { get { return _microphoneBrush; } set { SetProperty(ref _microphoneBrush, value); } }

        // Perzistentni identifikatori hardvera za reaktivno prevođenje (v2.14.0 - I18N-01)
        private bool _backlitIsManual = false;
        public bool BacklitIsManual { get { return _backlitIsManual; } set { SetProperty(ref _backlitIsManual, value); } }

        private string _fingerprintDeviceName = "";
        public string FingerprintDeviceName { get { return _fingerprintDeviceName; } set { SetProperty(ref _fingerprintDeviceName, value); } }

        private string _speakersDeviceName = "";
        public string SpeakersDeviceName { get { return _speakersDeviceName; } set { SetProperty(ref _speakersDeviceName, value); } }

        private string _micDeviceName = "";
        public string MicDeviceName { get { return _micDeviceName; } set { SetProperty(ref _micDeviceName, value); } }
    }

    // =========================================================================
    // DRAJVERI I HARDVERSKI UREĐAJI (v2.7.3 - TASK-113)
    // =========================================================================

    public class DriverItemViewModel : BaseViewModel
    {
        private string _name = "Nepoznat uređaj";
        private string _manufacturer = "Nepoznato";
        private string _driverVersion = "N/A";
        private string _driverDate = "N/A";
        private string _deviceId = "";
        private string _hardwareId = "";
        private string _pnpClass = "Other";
        private uint _errorCode = 0;
        private string _errorDescription = "";
        private bool _hasError = false;
        private SolidColorBrush _statusBrush = CreateFrozenBrush(63, 185, 80);

        public string Name { get { return _name; } set { SetProperty(ref _name, value); } }
        public string Manufacturer { get { return _manufacturer; } set { SetProperty(ref _manufacturer, value); } }
        public string DriverVersion { get { return _driverVersion; } set { SetProperty(ref _driverVersion, value); } }
        public string DriverDate { get { return _driverDate; } set { SetProperty(ref _driverDate, value); } }
        public string DeviceId { get { return _deviceId; } set { SetProperty(ref _deviceId, value); } }
        public string HardwareId { get { return _hardwareId; } set { SetProperty(ref _hardwareId, value); } }
        public string PnpClass { get { return _pnpClass; } set { SetProperty(ref _pnpClass, value); } }
        public uint ErrorCode { get { return _errorCode; } set { SetProperty(ref _errorCode, value); } }
        public string ErrorDescription { get { return _errorDescription; } set { SetProperty(ref _errorDescription, value); } }
        public bool HasError { get { return _hasError; } set { SetProperty(ref _hasError, value); } }
        public SolidColorBrush StatusBrush { get { return _statusBrush; } set { SetProperty(ref _statusBrush, value); } }

        // Faza 2B (GWADT-TASK-115) - Dinamička instalacija pojedinačnih drajvera
        private bool _canInstall = true;
        private bool _isInstalling = false;
        private int _installProgressPercent = 0;
        private string _installSpeedText = "";
        private string _installStatusText = "";
        private bool _isResolved = false;
        private ICommand _installDriverCommand;
        private ICommand _windowsUpdateFallbackCommand;

        public bool CanInstall
        {
            get { return _canInstall; }
            set
            {
                if (SetProperty(ref _canInstall, value))
                {
                    OnPropertyChanged("ShowInstallButton");
                }
            }
        }

        public bool IsInstalling
        {
            get { return _isInstalling; }
            set
            {
                if (SetProperty(ref _isInstalling, value))
                {
                    OnPropertyChanged("ShowInstallButton");
                    OnPropertyChanged("ShowProgressBar");
                }
            }
        }

        public int InstallProgressPercent
        {
            get { return _installProgressPercent; }
            set { SetProperty(ref _installProgressPercent, value); }
        }

        public string InstallSpeedText
        {
            get { return _installSpeedText; }
            set { SetProperty(ref _installSpeedText, value); }
        }

        public string InstallStatusText
        {
            get { return _installStatusText; }
            set { SetProperty(ref _installStatusText, value); }
        }

        public bool IsResolved
        {
            get { return _isResolved; }
            set
            {
                if (SetProperty(ref _isResolved, value))
                {
                    OnPropertyChanged("ShowInstallButton");
                    OnPropertyChanged("ShowResolvedBadge");
                    OnPropertyChanged("CardBorderBrush");
                }
            }
        }

        public bool ShowInstallButton
        {
            get { return _canInstall && !_isInstalling && !_isResolved; }
        }

        public bool ShowProgressBar
        {
            get { return _isInstalling; }
        }

        public bool ShowResolvedBadge
        {
            get { return _isResolved; }
        }

        public SolidColorBrush CardBorderBrush
        {
            get
            {
                if (_isResolved) return CreateFrozenBrush(35, 134, 54); // #238636 zeleni okvir
                return CreateFrozenBrush(62, 62, 66); // #3E3E42 standardni sivi okvir
            }
        }

        public ICommand InstallDriverCommand
        {
            get { return _installDriverCommand; }
            set { SetProperty(ref _installDriverCommand, value); }
        }

        public ICommand WindowsUpdateFallbackCommand
        {
            get { return _windowsUpdateFallbackCommand; }
            set { SetProperty(ref _windowsUpdateFallbackCommand, value); }
        }

        // Faza 2 (GWADT-TASK-117) - Provera i nadogradnja zvaničnih OEM drajvera ključnih komponenti
        private bool _isKeyDevice = false;
        private bool _isCheckingUpdate = false;
        private bool _hasUpdateAvailable = false;
        private bool _isUpToDate = false;
        private string _latestVersion = "";
        private string _latestDate = "";
        private string _updateBadgeText = "";
        private string _targetDriverId = "";
        private ICommand _checkUpdateCommand;
        private ICommand _upgradeDriverCommand;

        public bool IsKeyDevice
        {
            get { return _isKeyDevice; }
            set
            {
                if (SetProperty(ref _isKeyDevice, value))
                {
                    NotifyUpdateStateChanged();
                }
            }
        }

        public bool IsCheckingUpdate
        {
            get { return _isCheckingUpdate; }
            set
            {
                if (SetProperty(ref _isCheckingUpdate, value))
                {
                    NotifyUpdateStateChanged();
                }
            }
        }

        public bool HasUpdateAvailable
        {
            get { return _hasUpdateAvailable; }
            set
            {
                if (SetProperty(ref _hasUpdateAvailable, value))
                {
                    NotifyUpdateStateChanged();
                }
            }
        }

        public bool IsUpToDate
        {
            get { return _isUpToDate; }
            set
            {
                if (SetProperty(ref _isUpToDate, value))
                {
                    NotifyUpdateStateChanged();
                }
            }
        }

        public string LatestVersion
        {
            get { return _latestVersion; }
            set { SetProperty(ref _latestVersion, value); }
        }

        public string LatestDate
        {
            get { return _latestDate; }
            set { SetProperty(ref _latestDate, value); }
        }

        public string UpdateBadgeText
        {
            get { return _updateBadgeText; }
            set { SetProperty(ref _updateBadgeText, value); }
        }

        public string TargetDriverId
        {
            get { return _targetDriverId; }
            set { SetProperty(ref _targetDriverId, value); }
        }

        public ICommand CheckUpdateCommand
        {
            get { return _checkUpdateCommand; }
            set { SetProperty(ref _checkUpdateCommand, value); }
        }

        public ICommand UpgradeDriverCommand
        {
            get { return _upgradeDriverCommand; }
            set { SetProperty(ref _upgradeDriverCommand, value); }
        }

        private void NotifyUpdateStateChanged()
        {
            OnPropertyChanged("ShowCheckUpdateButton");
            OnPropertyChanged("ShowCheckingUpdateBadge");
            OnPropertyChanged("ShowUpdateAvailableBadge");
            OnPropertyChanged("ShowUpToDateBadge");
        }

        public bool ShowCheckUpdateButton
        {
            get { return _isKeyDevice && !_hasUpdateAvailable && !_isUpToDate && !_isCheckingUpdate; }
        }

        public bool ShowCheckingUpdateBadge
        {
            get { return _isKeyDevice && _isCheckingUpdate; }
        }

        public bool ShowUpdateAvailableBadge
        {
            get { return _isKeyDevice && _hasUpdateAvailable && !_isCheckingUpdate; }
        }

        public bool ShowUpToDateBadge
        {
            get { return _isKeyDevice && _isUpToDate && !_isCheckingUpdate && !_hasUpdateAvailable; }
        }
    }

    public class DriverCategoryViewModel : BaseViewModel
    {
        private string _categoryName = "";
        private string _categoryIcon = "📦";
        private string _categoryCode = "";
        private ObservableCollection<DriverItemViewModel> _devices;
        private bool _isExpanded = true;
        private bool _isVisible = true;

        public string CategoryName { get { return _categoryName; } set { SetProperty(ref _categoryName, value); } }
        public string CategoryIcon { get { return _categoryIcon; } set { SetProperty(ref _categoryIcon, value); } }
        public string CategoryCode { get { return _categoryCode; } set { SetProperty(ref _categoryCode, value); } }
        public ObservableCollection<DriverItemViewModel> Devices
        {
            get { return _devices; }
            set
            {
                if (SetProperty(ref _devices, value))
                {
                    OnPropertyChanged("DeviceCount");
                }
            }
        }
        public int DeviceCount { get { return _devices != null ? _devices.Count : 0; } }
        public bool IsExpanded { get { return _isExpanded; } set { SetProperty(ref _isExpanded, value); } }
        public bool IsVisible { get { return _isVisible; } set { SetProperty(ref _isVisible, value); } }

        public List<DriverItemViewModel> AllDevices { get; set; }

        public DriverCategoryViewModel()
        {
            AllDevices = new List<DriverItemViewModel>();
            _devices = new ObservableCollection<DriverItemViewModel>();
        }

        public void NotifyDeviceCountChanged()
        {
            OnPropertyChanged("DeviceCount");
        }
    }

    public class DriversTabViewModel : BaseViewModel
    {
        private ObservableCollection<DriverItemViewModel> _missingDevices;
        private ObservableCollection<DriverCategoryViewModel> _categories;
        private bool _hasMissingDevices = false;
        private string _missingSummaryText = "Učitavanje statusa drajvera...";
        private SolidColorBrush _missingSummaryBrush = CreateFrozenBrush(140, 140, 140);
        private bool _isScanning = false;
        private string _searchQuery = "";
        private int _totalDevicesCount = 0;
        private int _missingDevicesCount = 0;

        // Faza 2B (GWADT-TASK-115) - Grupna instalacija nedostajućih drajvera
        private bool _isBatchResolving = false;
        private int _batchProgressPercent = 0;
        private string _batchStatusText = "";

        public ObservableCollection<DriverItemViewModel> MissingDevices
        {
            get { return _missingDevices; }
            set { SetProperty(ref _missingDevices, value); }
        }

        public ObservableCollection<DriverCategoryViewModel> Categories
        {
            get { return _categories; }
            set { SetProperty(ref _categories, value); }
        }

        public bool HasMissingDevices
        {
            get { return _hasMissingDevices; }
            set
            {
                if (SetProperty(ref _hasMissingDevices, value))
                {
                    OnPropertyChanged("HasNoMissingDevices");
                }
            }
        }

        public bool HasNoMissingDevices
        {
            get { return !_hasMissingDevices; }
        }

        public string MissingSummaryText
        {
            get { return _missingSummaryText; }
            set { SetProperty(ref _missingSummaryText, value); }
        }

        public SolidColorBrush MissingSummaryBrush
        {
            get { return _missingSummaryBrush; }
            set { SetProperty(ref _missingSummaryBrush, value); }
        }

        public bool IsScanning
        {
            get { return _isScanning; }
            set { SetProperty(ref _isScanning, value); }
        }

        public string SearchQuery
        {
            get { return _searchQuery; }
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    ApplySearchFilter(value);
                }
            }
        }

        public int TotalDevicesCount
        {
            get { return _totalDevicesCount; }
            set { SetProperty(ref _totalDevicesCount, value); }
        }

        public int MissingDevicesCount
        {
            get { return _missingDevicesCount; }
            set { SetProperty(ref _missingDevicesCount, value); }
        }

        public bool IsBatchResolving
        {
            get { return _isBatchResolving; }
            set { SetProperty(ref _isBatchResolving, value); }
        }

        public int BatchProgressPercent
        {
            get { return _batchProgressPercent; }
            set { SetProperty(ref _batchProgressPercent, value); }
        }

        public string BatchStatusText
        {
            get { return _batchStatusText; }
            set { SetProperty(ref _batchStatusText, value); }
        }

        public ICommand RefreshDriversCommand { get; set; }
        public ICommand OpenDeviceManagerCommand { get; set; }
        public ICommand CopyHwidCommand { get; set; }
        public ICommand ResolveAllDriversCommand { get; set; }
        public ICommand InstallDriverCommand { get; set; }

        // Faza 2 (GWADT-TASK-117) - Provera ključnih drajvera
        private bool _isCheckingKeyDrivers = false;
        private string _keyDriversCheckStatus = "";
        private ICommand _checkKeyDriversCommand;

        public bool IsCheckingKeyDrivers
        {
            get { return _isCheckingKeyDrivers; }
            set { SetProperty(ref _isCheckingKeyDrivers, value); }
        }

        public string KeyDriversCheckStatus
        {
            get { return _keyDriversCheckStatus; }
            set { SetProperty(ref _keyDriversCheckStatus, value); }
        }

        public ICommand CheckKeyDriversCommand
        {
            get { return _checkKeyDriversCommand; }
            set { SetProperty(ref _checkKeyDriversCommand, value); }
        }

        public DriversTabViewModel()
        {
            _missingDevices = new ObservableCollection<DriverItemViewModel>();
            _categories = new ObservableCollection<DriverCategoryViewModel>();
        }

        public void ApplySearchFilter(string query)
        {
            if (Categories == null) return;

            string q = (query ?? "").Trim().ToLowerInvariant();

            foreach (var cat in Categories)
            {
                if (cat.AllDevices == null) continue;

                if (string.IsNullOrEmpty(q))
                {
                    cat.Devices.Clear();
                    foreach (var d in cat.AllDevices)
                    {
                        cat.Devices.Add(d);
                    }
                    cat.IsVisible = cat.Devices.Count > 0;
                    cat.NotifyDeviceCountChanged();
                }
                else
                {
                    cat.Devices.Clear();
                    foreach (var d in cat.AllDevices)
                    {
                        bool match = (d.Name != null && d.Name.ToLowerInvariant().Contains(q)) ||
                                     (d.Manufacturer != null && d.Manufacturer.ToLowerInvariant().Contains(q)) ||
                                     (d.HardwareId != null && d.HardwareId.ToLowerInvariant().Contains(q)) ||
                                     (d.DeviceId != null && d.DeviceId.ToLowerInvariant().Contains(q)) ||
                                     (d.PnpClass != null && d.PnpClass.ToLowerInvariant().Contains(q)) ||
                                     (d.DriverVersion != null && d.DriverVersion.ToLowerInvariant().Contains(q));
                        if (match)
                        {
                            cat.Devices.Add(d);
                        }
                    }
                    cat.IsVisible = cat.Devices.Count > 0;
                    cat.NotifyDeviceCountChanged();
                    if (cat.Devices.Count > 0)
                    {
                        cat.IsExpanded = true;
                    }
                }
            }
        }
    }

    public class CoreClockItem : BaseViewModel
    {
        private int _coreIndex;
        private string _coreName = "Core #1";
        private string _currentClockText = "0 MHz";
        private string _minClockText = "0 MHz";
        private string _maxClockText = "0 MHz";
        private string _ratioText = "x 0.0";
        private double _clockRatioPercent = 0.0;

        public int CoreIndex
        {
            get { return _coreIndex; }
            set { SetProperty(ref _coreIndex, value); }
        }

        public string CoreName
        {
            get { return _coreName; }
            set { SetProperty(ref _coreName, value); }
        }

        public string CurrentClockText
        {
            get { return _currentClockText; }
            set { SetProperty(ref _currentClockText, value); }
        }

        public string MinClockText
        {
            get { return _minClockText; }
            set { SetProperty(ref _minClockText, value); }
        }

        public string MaxClockText
        {
            get { return _maxClockText; }
            set { SetProperty(ref _maxClockText, value); }
        }

        public string RatioText
        {
            get { return _ratioText; }
            set { SetProperty(ref _ratioText, value); }
        }

        public double ClockRatioPercent
        {
            get { return _clockRatioPercent; }
            set { SetProperty(ref _clockRatioPercent, Math.Max(0.0, Math.Min(100.0, value))); }
        }
    }

    public class MainViewModel : BaseViewModel
    {
        // ------------------ HEADER ------------------
        private string _computerModel = "Učitavanje konfiguracije...";
        private string _serialNumber = "N/A";
        private string _cpuInfo = "Detekcija CPU-a...";
        private string _ramInfo = "Detekcija RAM-a...";
        private string _diskInfo = "Detekcija SSD/NVMe diska...";
        private string _durationString = "00:00:00";
        private TimeSpan _elapsedDuration = TimeSpan.Zero;
        private string _appVersionText = "WIS UI v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public string ComputerModel { get { return _computerModel; } set { SetProperty(ref _computerModel, value); } }
        public string SerialNumber { get { return _serialNumber; } set { SetProperty(ref _serialNumber, value); } }
        public string CpuInfo { get { return _cpuInfo; } set { SetProperty(ref _cpuInfo, value); } }
        public string RamInfo { get { return _ramInfo; } set { SetProperty(ref _ramInfo, value); } }
        public string DiskInfo { get { return _diskInfo; } set { SetProperty(ref _diskInfo, value); } }
        public string DurationString { get { return _durationString; } set { SetProperty(ref _durationString, value); } }
        public string AppVersionText { get { return _appVersionText; } set { SetProperty(ref _appVersionText, value); } }

        // ------------------ TAB: HARDVER I SISTEM (v2.7.1) ------------------
        private HardwareInfoViewModel _hardwareInfo;
        public HardwareInfoViewModel HardwareInfo
        {
            get { return _hardwareInfo; }
            set { SetProperty(ref _hardwareInfo, value); }
        }

        // ------------------ TAB: DRAJVERI (v2.7.3) ------------------
        private DriversTabViewModel _drivers = new DriversTabViewModel();
        public DriversTabViewModel Drivers
        {
            get { return _drivers; }
            set { SetProperty(ref _drivers, value); }
        }

        // ------------------ TAB 1: PREGLED ------------------
        private int _currentPhase = 1;
        private string _phaseName = "Faza 1: Mreža i povezivanje";
        private string _subPhase = "Inicijalizacija mrežnih servisa...";
        private string _liveStatus = "Sistem priprema konfiguraciju servisa...";
        private int _progress = 0;
        private bool _usbFree = false;
        private string _usbBannerText = "✓ USB FLEŠ DISK JE BEZBEDNO ODVOJEN (Možete slobodno izvaditi fleš disk)";

        public ObservableCollection<PhaseItem> Phases { get; private set; }
        public ObservableCollection<LogEntry> RecentLogs { get; private set; }

        public int CurrentPhase
        {
            get { return _currentPhase; }
            set
            {
                if (SetProperty(ref _currentPhase, value))
                {
                    UpdatePhases();
                }
            }
        }

        public string PhaseName { get { return _phaseName; } set { SetProperty(ref _phaseName, value); } }
        public string SubPhase { get { return _subPhase; } set { SetProperty(ref _subPhase, value); } }
        public string LiveStatus { get { return _liveStatus; } set { SetProperty(ref _liveStatus, value); } }
        public int Progress { get { return _progress; } set { SetProperty(ref _progress, Math.Max(0, Math.Min(100, value))); } }
        public bool UsbFree { get { return _usbFree; } set { SetProperty(ref _usbFree, value); } }
        public string UsbBannerText { get { return _usbBannerText; } set { SetProperty(ref _usbBannerText, value); } }

        // ------------------ TAB 2: DIJAGNOSTIKA ------------------
        private int _diskWear = 100;
        private string _diskWearText = "100% Zdravlje";
        private string _diskTbw = "N/A";
        private long _diskPowerOnHours = -1;
        private string _smartStatus = "Healthy (Ispravan)";
        private int _reallocatedSectors = 0;
        private int _criticalWarnings = 0;
        private Brush _smartStatusBrush = CreateFrozenBrush(46, 204, 113);

        private int _batteryHealth = 0;
        private string _batteryHealthText = "Fiksno napajanje (Desktop)";
        private string _batteryCycles = "N/A";
        private string _batteryStatus = "AC Napajanje / Desktop";
        private bool _isLaptop = false;
        private Brush _batteryHealthBrush = CreateFrozenBrush(100, 100, 100);

        private string _cpuDetail = "Detekcija CPU-a...";
        private string _cpuUsage = "Očitavanje...";
        private string _ramDetail = "Detekcija RAM-a...";
        private string _ramUsage = "Očitavanje...";

        public int DiskWear
        {
            get { return _diskWear; }
            set
            {
                SetProperty(ref _diskWear, value);
                DiskWearText = string.Format("{0}% Preostalo zdravlje", value);
                SmartStatusBrush = value >= 80 ? CreateFrozenBrush(46, 204, 113) :
                                   value >= 50 ? CreateFrozenBrush(241, 196, 15) :
                                                 CreateFrozenBrush(231, 76, 60);
            }
        }

        public string DiskWearText { get { return _diskWearText; } set { SetProperty(ref _diskWearText, value); } }
        public string DiskTbw { get { return _diskTbw; } set { SetProperty(ref _diskTbw, value); } }
        public long DiskPowerOnHours { get { return _diskPowerOnHours; } set { SetProperty(ref _diskPowerOnHours, value); } }
        public string SmartStatus { get { return _smartStatus; } set { SetProperty(ref _smartStatus, value); } }
        public int ReallocatedSectors { get { return _reallocatedSectors; } set { SetProperty(ref _reallocatedSectors, value); } }
        public int CriticalWarnings { get { return _criticalWarnings; } set { SetProperty(ref _criticalWarnings, value); } }
        public Brush SmartStatusBrush { get { return _smartStatusBrush; } set { SetProperty(ref _smartStatusBrush, value); } }

        public int BatteryHealth
        {
            get { return _batteryHealth; }
            set
            {
                SetProperty(ref _batteryHealth, value);
                BatteryHealthText = string.Format("{0}% Fabrički kapacitet", value);
                BatteryHealthBrush = value >= 75 ? CreateFrozenBrush(46, 204, 113) :
                                     value >= 50 ? CreateFrozenBrush(241, 196, 15) :
                                                   CreateFrozenBrush(231, 76, 60);
            }
        }

        public string BatteryHealthText { get { return _batteryHealthText; } set { SetProperty(ref _batteryHealthText, value); } }
        public string BatteryCycles { get { return _batteryCycles; } set { SetProperty(ref _batteryCycles, value); } }
        public string BatteryStatus { get { return _batteryStatus; } set { SetProperty(ref _batteryStatus, value); } }
        public bool IsLaptop
        {
            get { return _isLaptop; }
            set
            {
                if (SetProperty(ref _isLaptop, value))
                {
                    OnPropertyChanged("DeviceTypeText");
                    if (!_isLaptop)
                    {
                        UpdateAppStatus("BatteryBar", "SKIPPED", "Nije primenjivo na desktopu");
                    }
                }
            }
        }
        public string DeviceTypeText { get { return IsLaptop ? "Laptop računar" : "Desktop (Fiksno napajanje)"; } }
        public Brush BatteryHealthBrush { get { return _batteryHealthBrush; } set { SetProperty(ref _batteryHealthBrush, value); } }

        public string CpuDetail { get { return _cpuDetail; } set { SetProperty(ref _cpuDetail, value); } }
        public string CpuUsage { get { return _cpuUsage; } set { SetProperty(ref _cpuUsage, value); } }
        public string RamDetail { get { return _ramDetail; } set { SetProperty(ref _ramDetail, value); } }
        public string RamUsage { get { return _ramUsage; } set { SetProperty(ref _ramUsage, value); } }

        // Grafička karta & Ekran
        private string _gpuName = "Detekcija GPU-a...";
        private string _gpuVram = "N/A";
        private string _gpuDriverVersion = "N/A";
        private string _displayResolution = "Detekcija displeja...";
        private string _displayRefreshRate = "60 Hz";
        private string _displayDpiScale = "100%";

        public string GpuName { get { return _gpuName; } set { SetProperty(ref _gpuName, value); } }
        public string GpuVram { get { return _gpuVram; } set { SetProperty(ref _gpuVram, value); } }
        public string GpuDriverVersion { get { return _gpuDriverVersion; } set { SetProperty(ref _gpuDriverVersion, value); } }
        public string DisplayResolution { get { return _displayResolution; } set { SetProperty(ref _displayResolution, value); } }
        public string DisplayRefreshRate { get { return _displayRefreshRate; } set { SetProperty(ref _displayRefreshRate, value); } }
        public string DisplayDpiScale { get { return _displayDpiScale; } set { SetProperty(ref _displayDpiScale, value); } }

        // Matična ploča & BIOS & TPM
        private string _motherboardModel = "Detekcija matične ploče...";
        private string _biosVersion = "Detekcija BIOS-a...";
        private string _biosReleaseDate = "N/A";
        private string _secureBootStatus = "Nepoznato";
        private string _tpmStatusSummary = "Provera TPM čipa...";

        public string MotherboardModel { get { return _motherboardModel; } set { SetProperty(ref _motherboardModel, value); } }
        public string BiosVersion { get { return _biosVersion; } set { SetProperty(ref _biosVersion, value); } }
        public string BiosReleaseDate { get { return _biosReleaseDate; } set { SetProperty(ref _biosReleaseDate, value); } }
        public string SecureBootStatus { get { return _secureBootStatus; } set { SetProperty(ref _secureBootStatus, value); } }
        public string TpmStatusSummary { get { return _tpmStatusSummary; } set { SetProperty(ref _tpmStatusSummary, value); } }

        // PnP Problematic Devices
        public ObservableCollection<PnpProblemDevice> PnpProblemDevices { get; private set; }
        private bool _hasPnpProblems = false;
        private string _pnpSummaryText = "✓ Svi sistemski drajveri su ispravno instalirani";
        private Brush _pnpStatusBrush = CreateFrozenBrush(46, 204, 113);

        public bool HasPnpProblems { get { return _hasPnpProblems; } set { SetProperty(ref _hasPnpProblems, value); } }
        public string PnpSummaryText { get { return _pnpSummaryText; } set { SetProperty(ref _pnpSummaryText, value); } }
        public Brush PnpStatusBrush { get { return _pnpStatusBrush; } set { SetProperty(ref _pnpStatusBrush, value); } }

        public void UpdatePnpStatus()
        {
            if (PnpProblemDevices.Count > 0)
            {
                HasPnpProblems = true;
                PnpSummaryText = string.Format("⚠ {0} uređaja zahteva pažnju", PnpProblemDevices.Count);
                PnpStatusBrush = CreateFrozenBrush(241, 196, 15);
            }
            else
            {
                HasPnpProblems = false;
                PnpSummaryText = "✓ Svi sistemski drajveri su ispravno instalirani (0 problema)";
                PnpStatusBrush = CreateFrozenBrush(46, 204, 113);
            }
        }

        // Termalna telemetrija, 5-minutni grafikon i taktovi jezgara (v2.15.0 - WIS-TASK-131)
        private float _cpuTemp = 0.0f;
        private string _cpuTempText = "Očitavanje...";
        private Brush _cpuTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _gpuTemp = 0.0f;
        private string _gpuTempText = "Očitavanje...";
        private Brush _gpuTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _mbTemp = 0.0f;
        private string _mbTempText = "Očitavanje...";
        private Brush _mbTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _ssdTemp = 0.0f;
        private string _ssdTempText = "Očitavanje...";
        private Brush _ssdTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _throttlingPercent = 0.0f;
        private string _throttlingText = "Nije dostupno";
        private Brush _throttlingBrush = CreateFrozenBrush(140, 140, 140);

        private int _fanRpm = 0;
        private string _fanSpeedText = "Očitavanje...";
        private Brush _fanSpeedBrush = CreateFrozenBrush(78, 201, 176);

        private string _thermalSensorStatus = "● Senzori aktivni";
        private Brush _thermalSensorStatusBrush = CreateFrozenBrush(78, 201, 176);

        private PointCollection _cpuGraphPoints = new PointCollection();
        private PointCollection _gpuGraphPoints = new PointCollection();
        private PointCollection _mbGraphPoints = new PointCollection();
        private PointCollection _ssdGraphPoints = new PointCollection();
        private PointCollection _throttleGraphPoints = new PointCollection();

        private string _legendCpuText = "● CPU: N/A";
        private string _legendGpuText = "● GPU: N/A";
        private string _legendMbText = "● MB: N/A";
        private string _legendSsdText = "● SSD: N/A";
        private string _legendThrottleText = "● Throttling: Nije dostupno";

        private ICommand _resetCoreClocksCommand;

        public ObservableCollection<CoreClockItem> CoreClocks { get; private set; }

        public float CpuTemp { get { return _cpuTemp; } set { SetProperty(ref _cpuTemp, value); } }
        public string CpuTempText { get { return _cpuTempText; } set { SetProperty(ref _cpuTempText, value); } }
        public Brush CpuTempBrush { get { return _cpuTempBrush; } set { SetProperty(ref _cpuTempBrush, value); } }

        public float GpuTemp { get { return _gpuTemp; } set { SetProperty(ref _gpuTemp, value); } }
        public string GpuTempText { get { return _gpuTempText; } set { SetProperty(ref _gpuTempText, value); } }
        public Brush GpuTempBrush { get { return _gpuTempBrush; } set { SetProperty(ref _gpuTempBrush, value); } }

        public float MbTemp { get { return _mbTemp; } set { SetProperty(ref _mbTemp, value); } }
        public string MbTempText { get { return _mbTempText; } set { SetProperty(ref _mbTempText, value); } }
        public Brush MbTempBrush { get { return _mbTempBrush; } set { SetProperty(ref _mbTempBrush, value); } }

        public float SsdTemp { get { return _ssdTemp; } set { SetProperty(ref _ssdTemp, value); } }
        public string SsdTempText { get { return _ssdTempText; } set { SetProperty(ref _ssdTempText, value); } }
        public Brush SsdTempBrush { get { return _ssdTempBrush; } set { SetProperty(ref _ssdTempBrush, value); } }

        public float ThrottlingPercent { get { return _throttlingPercent; } set { SetProperty(ref _throttlingPercent, value); } }
        public string ThrottlingText { get { return _throttlingText; } set { SetProperty(ref _throttlingText, value); } }
        public Brush ThrottlingBrush { get { return _throttlingBrush; } set { SetProperty(ref _throttlingBrush, value); } }

        public int FanRpm { get { return _fanRpm; } set { SetProperty(ref _fanRpm, value); } }
        public string FanSpeedText { get { return _fanSpeedText; } set { SetProperty(ref _fanSpeedText, value); } }
        public Brush FanSpeedBrush { get { return _fanSpeedBrush; } set { SetProperty(ref _fanSpeedBrush, value); } }

        public string ThermalSensorStatus { get { return _thermalSensorStatus; } set { SetProperty(ref _thermalSensorStatus, value); } }
        public Brush ThermalSensorStatusBrush { get { return _thermalSensorStatusBrush; } set { SetProperty(ref _thermalSensorStatusBrush, value); } }

        public PointCollection CpuGraphPoints { get { return _cpuGraphPoints; } set { SetProperty(ref _cpuGraphPoints, value); } }
        public PointCollection GpuGraphPoints { get { return _gpuGraphPoints; } set { SetProperty(ref _gpuGraphPoints, value); } }
        public PointCollection MbGraphPoints { get { return _mbGraphPoints; } set { SetProperty(ref _mbGraphPoints, value); } }
        public PointCollection SsdGraphPoints { get { return _ssdGraphPoints; } set { SetProperty(ref _ssdGraphPoints, value); } }
        public PointCollection ThrottleGraphPoints { get { return _throttleGraphPoints; } set { SetProperty(ref _throttleGraphPoints, value); } }

        public string LegendCpuText { get { return _legendCpuText; } set { SetProperty(ref _legendCpuText, value); } }
        public string LegendGpuText { get { return _legendGpuText; } set { SetProperty(ref _legendGpuText, value); } }
        public string LegendMbText { get { return _legendMbText; } set { SetProperty(ref _legendMbText, value); } }
        public string LegendSsdText { get { return _legendSsdText; } set { SetProperty(ref _legendSsdText, value); } }
        public string LegendThrottleText { get { return _legendThrottleText; } set { SetProperty(ref _legendThrottleText, value); } }

        public ICommand ResetCoreClocksCommand
        {
            get { return _resetCoreClocksCommand; }
            set { SetProperty(ref _resetCoreClocksCommand, value); }
        }

        // Multi-Core Stres Test
        private bool _isStressTestRunning = false;
        private int _stressTestProgress = 0;
        private string _stressTestTimeText = "0 / 30s";
        private string _stressTestStatusText = "Spremno za testiranje hlađenja i stabilnosti";
        private string _thermalVerdictTitle = "Nije testirano";
        private string _thermalVerdictDescription = "Pokrenite test za procenu termalne paste i hladnjaka.";
        private Brush _thermalVerdictBrush = CreateFrozenBrush(136, 136, 136); // #888888
        private string _thermalRecommendation = "Preporuka: Kliknite [▶ Start Stres] za dijagnozu.";
        private bool _emergencyAbortTriggered = false;
        private float _maxTempReached = 0.0f;
        private ICommand _runThermalStressCommand;
        private ICommand _cancelThermalStressCommand;

        public bool IsStressTestRunning
        {
            get { return _isStressTestRunning; }
            set
            {
                if (SetProperty(ref _isStressTestRunning, value))
                {
                    OnPropertyChanged("IsNotStressTestRunning");
                }
            }
        }

        public bool IsNotStressTestRunning
        {
            get { return !_isStressTestRunning; }
        }

        public int StressTestProgress
        {
            get { return _stressTestProgress; }
            set { SetProperty(ref _stressTestProgress, Math.Max(0, Math.Min(100, value))); }
        }

        public string StressTestTimeText { get { return _stressTestTimeText; } set { SetProperty(ref _stressTestTimeText, value); } }
        public string StressTestStatusText { get { return _stressTestStatusText; } set { SetProperty(ref _stressTestStatusText, value); } }
        public string ThermalVerdictTitle { get { return _thermalVerdictTitle; } set { SetProperty(ref _thermalVerdictTitle, value); } }
        public string ThermalVerdictDescription { get { return _thermalVerdictDescription; } set { SetProperty(ref _thermalVerdictDescription, value); } }
        public Brush ThermalVerdictBrush { get { return _thermalVerdictBrush; } set { SetProperty(ref _thermalVerdictBrush, value); } }
        public string ThermalRecommendation { get { return _thermalRecommendation; } set { SetProperty(ref _thermalRecommendation, value); } }
        public bool EmergencyAbortTriggered { get { return _emergencyAbortTriggered; } set { SetProperty(ref _emergencyAbortTriggered, value); } }
        public float MaxTempReached { get { return _maxTempReached; } set { SetProperty(ref _maxTempReached, value); } }

        public ICommand RunThermalStressCommand
        {
            get { return _runThermalStressCommand; }
            set { SetProperty(ref _runThermalStressCommand, value); }
        }

        public ICommand CancelThermalStressCommand
        {
            get { return _cancelThermalStressCommand; }
            set { SetProperty(ref _cancelThermalStressCommand, value); }
        }

        public static SolidColorBrush GetThermalBrush(float temp, float warnThreshold, float critThreshold)
        {
            if (temp >= critThreshold)
            {
                return CreateFrozenBrush(231, 76, 60); // #E74C3C
            }
            if (temp >= warnThreshold)
            {
                return CreateFrozenBrush(241, 196, 15); // #F1C40F
            }
            return CreateFrozenBrush(78, 201, 176); // #4EC9B0
        }

        // ------------------ TAB 6: AUTOPILOT ------------------
        public AutopilotViewModel Autopilot { get; private set; }

        // ------------------ TAB 3: MREŽA ------------------
        private string _wifiSsid = "Nije povezano";
        private string _ipAddress = "127.0.0.1";
        private string _connectionType = "Detekcija mrežnih kartica...";
        private string _connectionStatusText = "Detekcija mreže...";
        private Brush _connectionStatusBrush = CreateFrozenBrush(136, 136, 136); // #888888
        private string _distributionSourceText = "Lokalni skladišni keš (NAS / USB)";
        private string _nasLatency = "Provera veze ka NAS-u...";
        private string _nasStatus = "Povezivanje...";
        private string _downloadedMb = "0 MB";
        private Brush _networkStatusBrush = CreateFrozenBrush(46, 204, 113);

        public string WifiSsid { get { return _wifiSsid; } set { SetProperty(ref _wifiSsid, value); } }
        public string IpAddress { get { return _ipAddress; } set { SetProperty(ref _ipAddress, value); } }
        public string ConnectionType { get { return _connectionType; } set { SetProperty(ref _connectionType, value); } }
        public string ConnectionStatusText { get { return _connectionStatusText; } set { SetProperty(ref _connectionStatusText, value); } }
        public Brush ConnectionStatusBrush { get { return _connectionStatusBrush; } set { SetProperty(ref _connectionStatusBrush, value); } }
        public string DistributionSourceText { get { return _distributionSourceText; } set { SetProperty(ref _distributionSourceText, value); } }
        public string NasLatency { get { return _nasLatency; } set { SetProperty(ref _nasLatency, value); } }
        public string NasStatus { get { return _nasStatus; } set { SetProperty(ref _nasStatus, value); } }
        public string DownloadedMb { get { return _downloadedMb; } set { SetProperty(ref _downloadedMb, value); } }
        public Brush NetworkStatusBrush { get { return _networkStatusBrush; } set { SetProperty(ref _networkStatusBrush, value); } }

        // ------------------ PRED-START KARTICA (SERVIS SPREMAN) ------------------
        private bool _isServiceReadyVisible = true;
        private string _autoStartMessage = "Automatski start za 15s... (Kliknite za trenutni start ili prebacite tab za pauzu)";

        public bool IsServiceReadyVisible
        {
            get { return _isServiceReadyVisible; }
            set { SetProperty(ref _isServiceReadyVisible, value); }
        }

        public string AutoStartMessage
        {
            get { return _autoStartMessage; }
            set { SetProperty(ref _autoStartMessage, value); }
        }

        public ICommand StartServiceCommand { get; set; }

        // ------------------ TAB 4: APLIKACIJE ------------------
        public ObservableCollection<AppItem> Apps { get; private set; }

        private string _appsSummaryText = "0 / 8 Aplikacija instalirano";
        private int _appsTotalCount = 0;
        private int _appsInstalledCount = 0;
        private int _appsRunningCount = 0;
        private int _appsSkippedCount = 0;
        private int _appsErrorCount = 0;

        public string AppsSummaryText { get { return _appsSummaryText; } set { SetProperty(ref _appsSummaryText, value); } }
        public int AppsTotalCount { get { return _appsTotalCount; } set { SetProperty(ref _appsTotalCount, value); } }
        public int AppsInstalledCount { get { return _appsInstalledCount; } set { SetProperty(ref _appsInstalledCount, value); } }
        public int AppsRunningCount { get { return _appsRunningCount; } set { SetProperty(ref _appsRunningCount, value); } }
        public int AppsSkippedCount { get { return _appsSkippedCount; } set { SetProperty(ref _appsSkippedCount, value); } }
        public int AppsErrorCount { get { return _appsErrorCount; } set { SetProperty(ref _appsErrorCount, value); } }

        // ------------------ TAB 5: LOG ------------------
        public ObservableCollection<LogEntry> AllLogs { get; private set; }
        private const int MaxLogCount = 500;

        private bool _autoScroll = true;
        private string _logFilter = "";

        public bool AutoScroll { get { return _autoScroll; } set { SetProperty(ref _autoScroll, value); } }
        public string LogFilter { get { return _logFilter; } set { SetProperty(ref _logFilter, value); } }

        // ------------------ ACTION BAR & IPC ------------------
        private bool _isPaused = false;
        private string _pauseButtonText = "⏸ Pauza";
        private bool _ipcConnected = true;
        private string _ipcStatusText = "IPC Povezan";

        public bool IsPaused
        {
            get { return _isPaused; }
            set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    PauseButtonText = value ? "▶ Nastavi" : "⏸ Pauza";
                }
            }
        }

        public string PauseButtonText { get { return _pauseButtonText; } set { SetProperty(ref _pauseButtonText, value); } }
        public bool IpcConnected { get { return _ipcConnected; } set { SetProperty(ref _ipcConnected, value); } }
        public string IpcStatusText { get { return _ipcStatusText; } set { SetProperty(ref _ipcStatusText, value); } }

        // ------------------ COMMANDS ------------------
        public ICommand PauseCommand { get; set; }
        public ICommand SkipCommand { get; set; }
        public ICommand DelayRestartCommand { get; set; }
        public ICommand CancelShutdownCommand { get; set; }
        public ICommand ShutdownNowCommand { get; set; }
        public ICommand CleanAndShutdownCommand { get; set; }
        public ICommand ExitWithoutCleanCommand { get; set; }
        public ICommand ShowCompletionSummaryCommand { get; set; }
        public ICommand RerunPhase4Command { get; set; }
        public ICommand RefreshHardwareCommand { get; set; }
        public ICommand SwitchLanguageCommand { get; set; }

        // ------------------ LOCALIZATION SUPPORT ------------------
        public LocalizationService Loc
        {
            get { return LocalizationService.Instance; }
        }

        public string CurrentLanguage
        {
            get { return LocalizationService.Instance.CurrentLanguage; }
        }

        public SolidColorBrush SerbianButtonBrush
        {
            get
            {
                return LocalizationService.Instance.IsSerbian
                    ? new SolidColorBrush(Color.FromRgb(0, 122, 204))
                    : new SolidColorBrush(Color.FromRgb(37, 37, 38));
            }
        }

        public SolidColorBrush ChineseButtonBrush
        {
            get
            {
                return LocalizationService.Instance.IsChinese
                    ? new SolidColorBrush(Color.FromRgb(0, 122, 204))
                    : new SolidColorBrush(Color.FromRgb(37, 37, 38));
            }
        }

        public SolidColorBrush SerbianButtonForeground
        {
            get
            {
                return LocalizationService.Instance.IsSerbian
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(170, 170, 170));
            }
        }

        public SolidColorBrush ChineseButtonForeground
        {
            get
            {
                return LocalizationService.Instance.IsChinese
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(170, 170, 170));
            }
        }

        public string WindowTitle
        {
            get { return LocalizationService.Instance["AppTitle"]; }
        }

        public string TabOverviewHeader { get { return LocalizationService.Instance["TabOverview"]; } }
        public string TabHardwareHeader { get { return LocalizationService.Instance["TabHardware"]; } }
        public string TabDriversHeader { get { return LocalizationService.Instance["TabDrivers"]; } }
        public string TabDiagnosticsHeader { get { return LocalizationService.Instance["TabDiagnostics"]; } }
        public string TabNetworkHeader { get { return LocalizationService.Instance["TabNetwork"]; } }
        public string TabAppsHeader { get { return LocalizationService.Instance["TabApps"]; } }
        public string TabLogsHeader { get { return LocalizationService.Instance["TabLogs"]; } }
        public string TabAutopilotHeader { get { return LocalizationService.Instance["TabAutopilot"]; } }

        // ------------------ COMPLETION PANEL ------------------
        private bool _isCompleted = false;
        private bool _isServiceFinished = false;
        private bool _criticalStressFailure = false;
        private string _deploymentStatus = "RUNNING";
        private string _stressResultStatus = "UNKNOWN";
        private string _stressResultReason = "";
        private string _completionTitle = "INSTALACIJA ZAVRŠENA";
        private string _completionSummary = "Instalacioni tok je završen. Rezultat dijagnostike hlađenja prikazan je odvojeno.";
        private int _shutdownCountdown = 0;
        private bool _shutdownTimerActive = false;
        private string _countdownText = "Računar je spreman za isporuku.";

        public bool IsCompleted
        {
            get { return _isCompleted; }
            set
            {
                if (SetProperty(ref _isCompleted, value))
                {
                    _shutdownTimerActive = false;
                }
            }
        }

        public bool CriticalStressFailure
        {
            get { return _criticalStressFailure; }
            set
            {
                if (SetProperty(ref _criticalStressFailure, value) && value)
                {
                    _shutdownTimerActive = false;
                    OnPropertyChanged("ShutdownTimerActive");
                }
            }
        }

        public string DeploymentStatus
        {
            get { return _deploymentStatus; }
            set { SetProperty(ref _deploymentStatus, value); }
        }

        public string StressResultStatus
        {
            get { return _stressResultStatus; }
            set
            {
                if (SetProperty(ref _stressResultStatus, value))
                {
                    OnPropertyChanged("IsDiagnosticsUnavailable");
                    OnPropertyChanged("DiagnosticsSummary");
                }
            }
        }

        public string StressResultReason
        {
            get { return _stressResultReason; }
            set
            {
                if (SetProperty(ref _stressResultReason, value))
                {
                    OnPropertyChanged("IsDiagnosticsUnavailable");
                    OnPropertyChanged("DiagnosticsSummary");
                }
            }
        }

        public string DiagnosticsSummary
        {
            get
            {
                if (string.Equals(_stressResultStatus, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase))
                    return "Hlađenje: KRITIČNO — predaja je blokirana.";
                if (IsDiagnosticsUnavailable)
                    return "Hlađenje: NEPROVERENO — obavezna ručna provera ventilatora i hladnjaka.";
                if (string.Equals(_stressResultStatus, "PASS", StringComparison.OrdinalIgnoreCase))
                    return "Hlađenje: test završen bez termalnog upozorenja.";
                if (string.Equals(_stressResultStatus, "WARN", StringComparison.OrdinalIgnoreCase))
                    return "Hlađenje: UPOZORENJE — potrebna servisna provera.";
                return "Hlađenje: test nije uspešno završen — potrebna ručna provera.";
            }
        }

        public bool IsDiagnosticsUnavailable
        {
            get
            {
                return string.Equals(_stressResultStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(_stressResultReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsServiceFinished
        {
            get { return _isServiceFinished; }
            set
            {
                if (SetProperty(ref _isServiceFinished, value))
                {
                    OnPropertyChanged("IsNotServiceFinished");
                }
            }
        }

        public bool IsNotServiceFinished
        {
            get { return !_isServiceFinished; }
        }

        public string CompletionTitle { get { return _completionTitle; } set { SetProperty(ref _completionTitle, value); } }
        public string CompletionSummary { get { return _completionSummary; } set { SetProperty(ref _completionSummary, value); } }
        public int ShutdownCountdown
        {
            get { return _shutdownCountdown; }
            set
            {
                if (SetProperty(ref _shutdownCountdown, value))
                {
                    bool isZh = LocalizationService.Instance.IsChinese;
                    CountdownText = isZh
                        ? string.Format("自动关机倒计时: {0} 秒 (WIS Service)", value)
                        : string.Format("Automatsko gašenje računara za: {0}s (WIS Service)", value);
                }
            }
        }
        public bool ShutdownTimerActive
        {
            get { return _shutdownTimerActive; }
            set
            {
                bool safeValue = _criticalStressFailure ? false : value;
                SetProperty(ref _shutdownTimerActive, safeValue);
            }
        }
        public string CountdownText { get { return _countdownText; } set { SetProperty(ref _countdownText, value); } }

        // ------------------ INICIJALIZACIJA ------------------
        public MainViewModel()
        {
            Phases = new ObservableCollection<PhaseItem>();
            RecentLogs = new ObservableCollection<LogEntry>();
            Apps = new ObservableCollection<AppItem>();
            AllLogs = new ObservableCollection<LogEntry>();
            PnpProblemDevices = new ObservableCollection<PnpProblemDevice>();
            CoreClocks = new ObservableCollection<CoreClockItem>();
            Autopilot = new AutopilotViewModel();
            HardwareInfo = new HardwareInfoViewModel();
            Drivers = new DriversTabViewModel();

            SwitchLanguageCommand = new RelayCommand(p =>
            {
                string lang = p != null ? p.ToString() : "sr";
                LocalizationService.Instance.SetLanguage(lang);
                RefreshLocalization();
            });

            LocalizationService.Instance.PropertyChanged += (s, e) =>
            {
                RefreshLocalization();
            };

            // Inicijalizacija 6 faza
            Phases.Add(new PhaseItem { Number = 1, Title = "1. Mreža", Subtitle = "Wi-Fi i ruting", Status = "Active" });
            Phases.Add(new PhaseItem { Number = 2, Title = "2. Update", Subtitle = "Drajveri i zakrpe", Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 3, Title = "3. Optimizacija", Subtitle = "Debloat i brendiranje", Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 4, Title = "4. Programi", Subtitle = "Paketi i OEM licenca", Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 5, Title = "5. Stres", Subtitle = "Stabilnost (30s)", Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 6, Title = "6. Završetak", Subtitle = "Dijagnostika i rezime", Status = "Pending" });

            // Inicijalizacija standardnih WIS aplikacija
            InitializeDefaultApps();
            UpdatePhases();
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged("Loc");
            OnPropertyChanged("CurrentLanguage");
            OnPropertyChanged("SerbianButtonBrush");
            OnPropertyChanged("ChineseButtonBrush");
            OnPropertyChanged("SerbianButtonForeground");
            OnPropertyChanged("ChineseButtonForeground");
            OnPropertyChanged("WindowTitle");
            OnPropertyChanged("TabOverviewHeader");
            OnPropertyChanged("TabHardwareHeader");
            OnPropertyChanged("TabDriversHeader");
            OnPropertyChanged("TabDiagnosticsHeader");
            OnPropertyChanged("TabNetworkHeader");
            OnPropertyChanged("TabAppsHeader");
            OnPropertyChanged("TabLogsHeader");
            OnPropertyChanged("TabAutopilotHeader");
            if (_isPaused)
            {
                PauseButtonText = LocalizationService.Instance["BtnResume"];
            }
            else
            {
                PauseButtonText = LocalizationService.Instance["BtnPause"];
            }

            if (HardwareInfo != null)
            {
                HardwareInfo.RefreshLocalization();
            }

            if (Autopilot != null)
            {
                Autopilot.RefreshLocalization();
            }

            if (Apps != null)
            {
                foreach (var appItem in Apps)
                {
                    appItem.RefreshLocalization();
                }
            }

            bool isZh = LocalizationService.Instance.IsChinese;
            if (_completionTitle == "INSTALACIJA ZAVRŠENA" || _completionTitle == "服务完成 - 计算机已准备交付客户")
            {
                CompletionTitle = LocalizationService.Instance["CompletionDefaultTitle"];
            }
            if (_completionSummary == "Instalacioni tok je završen. Rezultat dijagnostike hlađenja prikazan je odvojeno." || _completionSummary == "所有硬件组件、系统补丁及应用程序均已成功部署并通过验证。")
            {
                CompletionSummary = LocalizationService.Instance["CompletionDefaultSummary"];
            }

            if (_shutdownCountdown > 0)
            {
                CountdownText = isZh
                    ? string.Format("自动关机倒计时: {0} 秒 (WIS Service)", _shutdownCountdown)
                    : string.Format("Automatsko gašenje računara za: {0}s (WIS Service)", _shutdownCountdown);
            }
            else if (_countdownText == "Računar je spreman za isporuku." || _countdownText == "计算机已准备就绪交付。")
            {
                CountdownText = isZh ? "计算机已准备就绪交付。" : "Računar je spreman za isporuku.";
            }

            UpdateAppsSummary();
            UpdatePhases();
            OnPropertyChanged(string.Empty);
        }

        private void InitializeDefaultApps()
        {
            Action initAction = delegate
            {
                Apps.Clear();
                Apps.Add(new AppItem { Id = "DotNet", Name = ".NET 8 Runtime", IconGlyph = "⚡", Status = "WAIT", Detail = "Čeka početak faze 4" });
                Apps.Add(new AppItem { Id = "Chrome", Name = "Google Chrome", IconGlyph = "🌐", Status = "WAIT", Detail = "Čeka početak faze 4" });
                Apps.Add(new AppItem { Id = "WinRAR", Name = "WinRAR x64", IconGlyph = "🗜️", Status = "WAIT", Detail = "Čeka početak faze 4" });
                Apps.Add(new AppItem { Id = "Acrobat", Name = "Adobe Acrobat Pro", IconGlyph = "📄", Status = "WAIT", Detail = "Čeka početak faze 4" });
                Apps.Add(new AppItem { Id = "Office", Name = "Microsoft Office 2024", IconGlyph = "📊", Status = "WAIT", Detail = "Čeka početak faze 4" });

                if (!_isLaptop || _batteryHealth <= 0)
                {
                    Apps.Add(new AppItem { Id = "BatteryBar", Name = "BatteryBar", IconGlyph = "🔋", Status = "SKIPPED", Detail = "Nije primenjivo na desktopu" });
                }
                else
                {
                    Apps.Add(new AppItem { Id = "BatteryBar", Name = "BatteryBar", IconGlyph = "🔋", Status = "WAIT", Detail = "Čeka početak faze 4" });
                }

                Apps.Add(new AppItem { Id = "License", Name = "OEM Licenca", IconGlyph = "🔑", Status = "WAIT", Detail = "Čeka početak faze 4" });
                Apps.Add(new AppItem { Id = "Shortcuts", Name = "Servisne Prečice", IconGlyph = "🔗", Status = "WAIT", Detail = "Čeka početak faze 4" });
                UpdateAppsSummary();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(initAction);
            }
            else
            {
                initAction();
            }
        }

        public void UpdatePhases()
        {
            Action updateAction = delegate
            {
                bool isZh = LocalizationService.Instance.IsChinese;
                for (int i = 0; i < Phases.Count; i++)
                {
                    int pNum = Phases[i].Number;
                    switch (pNum)
                    {
                        case 1:
                            Phases[i].Title = isZh ? "1. 网络" : "1. Mreža";
                            Phases[i].Subtitle = isZh ? "Wi-Fi 与路由" : "Wi-Fi i ruting";
                            break;
                        case 2:
                            Phases[i].Title = isZh ? "2. 驱动更新" : "2. Update";
                            Phases[i].Subtitle = isZh ? "驱动与补丁" : "Drajveri i zakrpe";
                            break;
                        case 3:
                            Phases[i].Title = isZh ? "3. 系统优化" : "3. Optimizacija";
                            Phases[i].Subtitle = isZh ? "精简与个性化" : "Debloat i brendiranje";
                            break;
                        case 4:
                            Phases[i].Title = isZh ? "4. 软件安装" : "4. Programi";
                            Phases[i].Subtitle = isZh ? "软件包与 OEM 授权" : "Paketi i OEM licenca";
                            break;
                        case 5:
                            Phases[i].Title = isZh ? "5. 压力测试" : "5. Stres";
                            Phases[i].Subtitle = isZh ? "稳定性验证 (30秒)" : "Stabilnost (30s)";
                            break;
                        case 6:
                            Phases[i].Title = isZh ? "6. 完成交付" : "6. Završetak";
                            Phases[i].Subtitle = isZh ? "诊断与总览" : "Dijagnostika i rezime";
                            break;
                    }

                    if (pNum < _currentPhase)
                    {
                        Phases[i].Status = "Completed";
                    }
                    else if (pNum == _currentPhase)
                    {
                        Phases[i].Status = "Active";
                    }
                    else
                    {
                        Phases[i].Status = "Pending";
                    }
                }
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(updateAction);
            }
            else
            {
                updateAction();
            }
        }

        public void AddLog(string level, string tag, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant(),
                Tag = string.IsNullOrWhiteSpace(tag) ? "[WIS]" : (tag.StartsWith("[") ? tag : string.Format("[{0}]", tag)),
                Message = message ?? ""
            };

            Action addAction = delegate
            {
                AllLogs.Add(entry);
                while (AllLogs.Count > MaxLogCount)
                {
                    AllLogs.RemoveAt(0);
                }

                RecentLogs.Add(entry);
                while (RecentLogs.Count > 5)
                {
                    RecentLogs.RemoveAt(0);
                }
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(addAction);
            }
            else
            {
                addAction();
            }
        }

        public void SetPnpProblemDevices(IEnumerable<PnpProblemDevice> devices)
        {
            Action setAction = delegate
            {
                PnpProblemDevices.Clear();
                if (devices != null)
                {
                    foreach (var d in devices)
                    {
                        PnpProblemDevices.Add(d);
                    }
                }
                UpdatePnpStatus();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(setAction);
            }
            else
            {
                setAction();
            }
        }

        public void UpdateAppStatus(string id, string status, string detail = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Action updateAction = delegate
            {
                foreach (var app in Apps)
                {
                    if (string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(app.Name, id, StringComparison.OrdinalIgnoreCase))
                    {
                        app.Status = status;
                        if (!string.IsNullOrEmpty(detail))
                        {
                            app.Detail = detail;
                        }
                        else
                        {
                            if (app.Status == "OK") app.Detail = "Instalacija uspešno verifikovana";
                            else if (app.Status == "RUNNING") app.Detail = "Instalacija paketa u toku...";
                            else if (app.Status == "ERROR") app.Detail = "Greška tokom instalacije";
                        }
                        break;
                    }
                }
                UpdateAppsSummary();
            };

            var appInst = Application.Current;
            if (appInst != null && appInst.Dispatcher != null && !appInst.Dispatcher.CheckAccess())
            {
                appInst.Dispatcher.BeginInvoke(updateAction);
            }
            else
            {
                updateAction();
            }
        }

        public void UpdateAppsSummary()
        {
            int total = Apps.Count;
            int completed = 0;
            int running = 0;
            int skipped = 0;
            int error = 0;
            int waiting = 0;

            foreach (var a in Apps)
            {
                switch (a.Status)
                {
                    case "OK": completed++; break;
                    case "RUNNING": running++; break;
                    case "SKIPPED": skipped++; break;
                    case "ERROR": error++; break;
                    default: waiting++; break;
                }
            }

            AppsTotalCount = total;
            AppsInstalledCount = completed;
            AppsRunningCount = running;
            AppsSkippedCount = skipped;
            AppsErrorCount = error;

            if (LocalizationService.Instance.IsChinese)
            {
                AppsSummaryText = string.Format("已安装 {0}/{1} • 进行中 {2} • 已跳过 {3} • 错误 {4}", completed, total, running, skipped, error);
            }
            else
            {
                AppsSummaryText = string.Format("{0} od {1} instalirano • {2} u toku • {3} preskočeno • {4} grešaka", completed, total, running, skipped, error);
            }
        }

        public void SetInitialElapsedDuration(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            _elapsedDuration = elapsed;
            DurationString = string.Format("{0:00}:{1:00}:{2:00}", (int)_elapsedDuration.TotalHours, _elapsedDuration.Minutes, _elapsedDuration.Seconds);
        }

        public void TickDuration()
        {
            _elapsedDuration = _elapsedDuration.Add(TimeSpan.FromSeconds(1));
            DurationString = string.Format("{0:00}:{1:00}:{2:00}", (int)_elapsedDuration.TotalHours, _elapsedDuration.Minutes, _elapsedDuration.Seconds);
        }
    }

    // Alias klase radi potpune kompatibilnosti sa specifikacijom
    public class MainWindowViewModel : MainViewModel
    {
    }
}
