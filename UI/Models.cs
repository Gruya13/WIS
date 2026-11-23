using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    public class StepItem : BaseViewModel
    {
        private string _id;
        private string _title;
        private string _status = "WAIT"; // WAIT, RUNNING, OK, SKIPPED_DONE, SKIPPED_USER, WARN, ERROR
        private string _reason;

        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }

        public string Reason
        {
            get { return _reason; }
            set
            {
                if (SetProperty(ref _reason, value))
                {
                    OnPropertyChanged("HasReason");
                }
            }
        }

        public bool HasReason
        {
            get { return !string.IsNullOrWhiteSpace(_reason); }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                string norm = (value ?? "WAIT").Trim().ToUpperInvariant();
                if (norm == "SUCCESS" || norm == "COMPLETED" || norm == "DONE") norm = "OK";
                else if (norm == "FAILED") norm = "ERROR";
                else if (norm == "WARNING") norm = "WARN";
                else if (norm == "PENDING" || norm == "QUEUED") norm = "WAIT";

                if (SetProperty(ref _status, norm))
                {
                    OnPropertyChanged("IsRunning");
                    OnPropertyChanged("IsCompleted");
                    OnPropertyChanged("IsSkipped");
                    OnPropertyChanged("IsWarning");
                    OnPropertyChanged("IsError");
                    OnPropertyChanged("BadgeBackground");
                    OnPropertyChanged("BadgeForeground");
                    OnPropertyChanged("BorderBrush");
                    OnPropertyChanged("IconText");
                }
            }
        }

        public bool IsRunning { get { return string.Equals(Status, "RUNNING", StringComparison.OrdinalIgnoreCase); } }
        public bool IsCompleted { get { return string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase); } }
        public bool IsSkipped { get { return string.Equals(Status, "SKIPPED_DONE", StringComparison.OrdinalIgnoreCase) || string.Equals(Status, "SKIPPED_USER", StringComparison.OrdinalIgnoreCase) || string.Equals(Status, "SKIPPED", StringComparison.OrdinalIgnoreCase); } }
        public bool IsWarning { get { return string.Equals(Status, "WARN", StringComparison.OrdinalIgnoreCase); } }
        public bool IsError { get { return string.Equals(Status, "ERROR", StringComparison.OrdinalIgnoreCase); } }

        public Brush BadgeBackground
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(46, 204, 113);     // #2ecc71
                if (IsSkipped) return CreateFrozenBrush(62, 107, 84);        // #3e6b54 sivo-zeleni bedž
                if (IsWarning) return CreateFrozenBrush(230, 126, 34);       // #e67e22 narandžasto
                if (IsError) return CreateFrozenBrush(231, 76, 60);          // #e74c3c crveno
                if (IsRunning) return CreateFrozenBrush(0, 122, 204);        // #007acc plavo
                return CreateFrozenBrush(50, 50, 55);                         // #323237 sivo
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                if (IsCompleted || IsRunning || IsSkipped || IsWarning || IsError) return Brushes.White;
                return CreateFrozenBrush(150, 150, 150);
            }
        }

        public Brush BorderBrush
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(39, 174, 96);       // #27ae60
                if (IsSkipped) return CreateFrozenBrush(46, 204, 113);       // #2ecc71
                if (IsWarning) return CreateFrozenBrush(211, 84, 0);         // #d35400
                if (IsError) return CreateFrozenBrush(192, 57, 43);          // #c0392b
                if (IsRunning) return CreateFrozenBrush(78, 201, 176);       // #4ec9b0
                return CreateFrozenBrush(70, 70, 75);                        // #46464b
            }
        }

        public string IconText
        {
            get
            {
                if (IsCompleted) return "✓";
                if (IsSkipped) return "»";
                if (IsWarning) return "⚠";
                if (IsError) return "✗";
                if (IsRunning) return "▶";
                return "·";
            }
        }
    }

    public class PhaseItem : BaseViewModel
    {
        private int _number;
        private int _displayNumber;
        private string _title;
        private string _subtitle;
        private string _status = "Pending"; // "Pending", "Active", "Completed", "Skipped", "Warning"
        private ObservableCollection<StepItem> _steps;
        private bool _isExpanded = true;
        private bool _showStepsInOverview = false;

        public PhaseItem()
        {
            _steps = new ObservableCollection<StepItem>();
            _steps.CollectionChanged += delegate
            {
                OnPropertyChanged("HasSteps");
            };
        }

        public int Number
        {
            get { return _number; }
            set
            {
                if (SetProperty(ref _number, value))
                {
                    OnPropertyChanged("IconText");
                }
            }
        }

        public int DisplayNumber
        {
            get { return _displayNumber; }
            set
            {
                if (SetProperty(ref _displayNumber, value))
                {
                    OnPropertyChanged("IconText");
                }
            }
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

        public ObservableCollection<StepItem> Steps
        {
            get { return _steps; }
            set
            {
                if (SetProperty(ref _steps, value))
                {
                    OnPropertyChanged("HasSteps");
                }
            }
        }

        public bool HasSteps
        {
            get { return _steps != null && _steps.Count > 0; }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { SetProperty(ref _isExpanded, true); }
        }

        public bool ShowStepsInOverview
        {
            get { return _showStepsInOverview; }
            set { SetProperty(ref _showStepsInOverview, value); }
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
                    OnPropertyChanged("IsSkipped");
                    OnPropertyChanged("IsWarning");
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
        public bool IsSkipped { get { return string.Equals(Status, "Skipped", StringComparison.OrdinalIgnoreCase); } }
        public bool IsWarning { get { return string.Equals(Status, "Warning", StringComparison.OrdinalIgnoreCase); } }

        public Brush BadgeBackground
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(46, 204, 113); // #2ecc71
                if (IsSkipped) return CreateFrozenBrush(62, 107, 84);     // #3e6b54 zeleno-sivi bedž
                if (IsWarning) return CreateFrozenBrush(230, 126, 34);    // #e67e22 narandžasto
                if (IsActive) return CreateFrozenBrush(0, 122, 204);     // #007acc
                return CreateFrozenBrush(50, 50, 55);                    // #323237
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                if (IsCompleted || IsActive || IsSkipped || IsWarning) return Brushes.White;
                return CreateFrozenBrush(150, 150, 150);
            }
        }

        public Brush BorderBrush
        {
            get
            {
                if (IsCompleted) return CreateFrozenBrush(39, 174, 96);
                if (IsSkipped) return CreateFrozenBrush(46, 204, 113);
                if (IsWarning) return CreateFrozenBrush(230, 126, 34);
                if (IsActive) return CreateFrozenBrush(78, 201, 176); // #4ec9b0
                return CreateFrozenBrush(70, 70, 75);
            }
        }

        public string IconText
        {
            get
            {
                if (IsCompleted) return "✓";
                if (IsSkipped) return "»";
                if (IsWarning) return "⚠";
                if (IsActive) return "▶";
                return (_displayNumber > 0 ? _displayNumber : Number).ToString();
            }
        }
    }

    public class BenchmarkBaseline
    {
        public double FleetP50TotalSeconds { get; set; }
        public Dictionary<int, double> PhaseAveragesSeconds { get; private set; }
        public Dictionary<string, double> ModelAveragesSeconds { get; private set; }
        public string SourceLabel { get; set; }

        public BenchmarkBaseline()
        {
            PhaseAveragesSeconds = new Dictionary<int, double>();
            ModelAveragesSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            SourceLabel = LocalizationService.Get("MdlDefaultBenchmarkSource", "Podrazumevani WIS v3 benchmark");
        }
    }

    public class ActiveStepProgressItem : BaseViewModel
    {
        private string _id;
        private int _phaseNumber;
        private string _phaseLabel;
        private string _title;
        private string _detail;
        private int _progress;
        private bool _isIndeterminate;
        private string _elapsedText = "00:00";
        private string _etaText = "--:--";
        private string _timingTooltip = LocalizationService.Get("MdlTimingTooltip", "Trajanje i benchmark ETA procena");
        private Brush _barBrush = CreateFrozenBrush(78, 201, 176);
        private Brush _badgeBrush = CreateFrozenBrush(0, 122, 204);

        public string Id { get { return _id; } set { SetProperty(ref _id, value); } }
        public int PhaseNumber { get { return _phaseNumber; } set { SetProperty(ref _phaseNumber, value); } }
        public string PhaseLabel { get { return _phaseLabel; } set { SetProperty(ref _phaseLabel, value); } }
        public string Title { get { return _title; } set { SetProperty(ref _title, value); } }
        public string ElapsedText { get { return _elapsedText; } set { SetProperty(ref _elapsedText, value); } }
        public string EtaText { get { return _etaText; } set { SetProperty(ref _etaText, value); } }
        public string TimingTooltip { get { return _timingTooltip; } set { SetProperty(ref _timingTooltip, value); } }
        public string Detail
        {
            get { return _detail; }
            set
            {
                if (SetProperty(ref _detail, value))
                {
                    OnPropertyChanged("DetailSuffix");
                }
            }
        }
        public string DetailSuffix { get { return string.IsNullOrWhiteSpace(_detail) ? "" : (" - " + _detail.Trim()); } }

        public int Progress
        {
            get { return _progress; }
            set
            {
                if (SetProperty(ref _progress, Math.Max(0, Math.Min(100, value))))
                {
                    OnPropertyChanged("PercentText");
                }
            }
        }

        public bool IsIndeterminate
        {
            get { return _isIndeterminate; }
            set
            {
                if (SetProperty(ref _isIndeterminate, value))
                {
                    OnPropertyChanged("PercentText");
                }
            }
        }

        public Brush BarBrush { get { return _barBrush; } set { SetProperty(ref _barBrush, value); } }
        public Brush BadgeBrush { get { return _badgeBrush; } set { SetProperty(ref _badgeBrush, value); } }

        public string PercentText
        {
            get
            {
                return _isIndeterminate ? LocalizationService.Get("MdlInProgress", "U toku...") : string.Format("{0}%", _progress);
            }
        }
    }

    public class OverviewAlertItem : BaseViewModel
    {
        public string Key { get; set; }
        public string Severity { get; set; }
        public string BadgeText { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string TargetTab { get; set; }
        public Brush AccentBrush { get; set; }
        public Brush BackgroundBrush { get; set; }
        public Brush BorderBrush { get; set; }
        public Brush TitleBrush { get; set; }

        public static OverviewAlertItem Create(string key, string severity, string title, string detail, string targetTab)
        {
            string normSev = string.IsNullOrEmpty(severity) ? "WARNING" : severity.Trim().ToUpperInvariant();
            Brush accent;
            Brush bg;
            Brush border;
            Brush titleBrush;
            string badge;

            if (normSev == "CRITICAL")
            {
                badge = LocalizationService.Get("MdlAlertCritBadge", "KRITIČNO");
                accent = CreateFrozenBrush(231, 76, 60);
                bg = CreateFrozenBrush(45, 27, 30);
                border = CreateFrozenBrush(110, 43, 43);
                titleBrush = CreateFrozenBrush(255, 138, 128);
            }
            else if (normSev == "WARNING")
            {
                badge = LocalizationService.Get("MdlAlertWarnBadge", "UPOZORENJE");
                accent = CreateFrozenBrush(243, 156, 18);
                bg = CreateFrozenBrush(44, 36, 24);
                border = CreateFrozenBrush(107, 81, 34);
                titleBrush = CreateFrozenBrush(245, 208, 114);
            }
            else
            {
                badge = LocalizationService.Get("MdlAlertInfoBadge", "INFO");
                accent = CreateFrozenBrush(56, 189, 248);
                bg = CreateFrozenBrush(25, 36, 46);
                border = CreateFrozenBrush(45, 78, 102);
                titleBrush = CreateFrozenBrush(156, 220, 254);
            }

            return new OverviewAlertItem
            {
                Key = key ?? "",
                Severity = normSev,
                BadgeText = badge,
                Title = title ?? "",
                Detail = detail ?? "",
                TargetTab = string.IsNullOrEmpty(targetTab) ? "overview" : targetTab,
                AccentBrush = accent,
                BackgroundBrush = bg,
                BorderBrush = border,
                TitleBrush = titleBrush
            };
        }
    }

    public class AppItem : BaseViewModel
    {
        private string _id;
        private string _name;
        private string _status = "WAIT"; // "WAIT", "RUNNING", "OK", "WARN", "ERROR", "SKIPPED"
        private string _detail = LocalizationService.Get("MdlAppDetailQueue", "U redu čekanja");
        private string _detailLocKey = "MdlAppDetailQueue";
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
                else if (norm == "FAILED" || norm == "FAIL" || norm == "ERROR") norm = "ERROR";
                else if (norm == "WARNING" || norm == "WARN") norm = "WARN";
                else if (norm == "SKIPPED" || norm == "SKIPPED_DONE" || norm == "SKIPPED_USER") { }
                else if (norm == "PENDING" || norm == "QUEUED") norm = "WAIT";

                if (SetProperty(ref _status, norm))
                {
                    bool isQueueDefault = string.IsNullOrEmpty(_detail) || _detailLocKey == "MdlAppDetailQueue" || _detail == "U redu čekanja";
                    if (norm == "SKIPPED_DONE" && isQueueDefault)
                    {
                        SetLocalizedDetail("MdlAppDetailSkippedDone", "Već instalirano");
                    }
                    else if (norm == "SKIPPED_USER" && isQueueDefault)
                    {
                        SetLocalizedDetail("MdlAppDetailSkippedUser", "Preskočeno na zahtev servisera");
                    }
                    OnPropertyChanged("BadgeBackground");
                    OnPropertyChanged("BadgeForeground");
                    OnPropertyChanged("StatusDisplay");
                    OnPropertyChanged("IconText");
                    OnPropertyChanged("IsRunning");
                    OnPropertyChanged("IsCompleted");
                    OnPropertyChanged("IsSkipped");
                    OnPropertyChanged("IsWarning");
                    OnPropertyChanged("IsError");
                    OnPropertyChanged("ShowRetryButton");
                    OnPropertyChanged("CanRetry");
                }
            }
        }

        public string Detail
        {
            get { return _detail; }
            set
            {
                _detailLocKey = null;
                SetProperty(ref _detail, value);
            }
        }

        public void SetLocalizedDetail(string key, string fallback)
        {
            _detailLocKey = key;
            SetProperty(ref _detail, LocalizationService.Get(key, fallback), "Detail");
        }

        public string StatusDisplay { get { return Status; } }

        public bool IsRunning { get { return Status == "RUNNING"; } }
        public bool IsCompleted { get { return Status == "OK"; } }
        public bool IsSkipped { get { return Status == "SKIPPED" || Status == "SKIPPED_DONE" || Status == "SKIPPED_USER"; } }
        public bool IsWarning { get { return Status == "WARN"; } }
        public bool IsError { get { return Status == "ERROR"; } }
        public bool ShowRetryButton { get { return Status == "ERROR" || Status == "WARN"; } }
        public bool CanRetry { get { return (Status == "ERROR" || Status == "WARN") && !IsRunning; } }

        public string RetryButtonText
        {
            get { return LocalizationService.Get("AppRetryBtn", "↻ Retry"); }
        }

        public string RetryButtonTooltip
        {
            get { return LocalizationService.Get("AppRetryTooltip", "Ponovo pokreni instalaciju ovog programa"); }
        }

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
                    case "WARN":
                        return CreateFrozenBrush(210, 153, 34); // Amber #D29922
                    case "ERROR":
                        return CreateFrozenBrush(231, 76, 60); // Red #e74c3c
                    case "SKIPPED_DONE":
                        return CreateFrozenBrush(62, 107, 84); // Sivo-zelena #3e6b54
                    case "SKIPPED_USER":
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
                    case "WARN":
                    case "ERROR":
                    case "SKIPPED":
                    case "SKIPPED_DONE":
                    case "SKIPPED_USER":
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
                switch (Status)
                {
                    case "OK": return LocalizationService.Get("MdlAppStatusOk", "✓ OK");
                    case "RUNNING": return LocalizationService.Get("MdlAppStatusRunning", "⚙ RAD");
                    case "WARN": return LocalizationService.Get("MdlAppStatusWarn", "⚠ UPOZ.");
                    case "ERROR": return LocalizationService.Get("MdlAppStatusError", "✗ GREŠKA");
                    case "SKIPPED_DONE": return LocalizationService.Get("MdlAppStatusSkippedDone", "» PRESKOČENO");
                    case "SKIPPED_USER":
                    case "SKIPPED": return LocalizationService.Get("MdlAppStatusSkipped", "⏭ PRESKOČENO");
                    case "WAIT":
                    default: return LocalizationService.Get("MdlAppStatusWait", "⏳ ČEKA");
                }
            }
        }

        public void RefreshLocalization()
        {
            if (!string.IsNullOrEmpty(_detailLocKey))
            {
                SetProperty(ref _detail, LocalizationService.Get(_detailLocKey, _detail), "Detail");
            }
            OnPropertyChanged("IconText");
            OnPropertyChanged("RetryButtonText");
            OnPropertyChanged("RetryButtonTooltip");
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
        private string _name = LocalizationService.Get("MdlUnknownDevice", "Nepoznat uređaj");
        private string _deviceId = "";
        private int _errorCode = 0;
        private string _errorStatus = LocalizationService.Get("MdlDriverError", "Greška drajvera");

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
        private string _statusTitle = LocalizationService.Get("ApMatrixTitle_Scanning", "PROVERA AUTOPILOTA U TOKU...");
        private string _statusDescription = LocalizationService.Get("ApMatrixDesc_Scanning", "Sistem vrši duboku forenziku registra, sertifikata i proveru Microsoft ZTD oblaka.");
        private string _recommendedAction = LocalizationService.Get("ApMatrixRec_Scanning", "Sačekajte završetak automatske analize...");
        private Brush _statusBrush = CreateFrozenBrush(140, 140, 140);

        // Hardverski otisak
        private string _hwManufacturer = LocalizationService.Get("MdlLoading", "Učitavanje...");
        private string _hwModel = LocalizationService.Get("MdlLoading", "Učitavanje...");
        private string _hwSerialNumber = LocalizationService.Get("MdlLoading", "Učitavanje...");
        private string _hwSmbiosUuid = LocalizationService.Get("MdlLoading", "Učitavanje...");
        private bool _tpmPresent = false;
        private bool _tpmReady = false;
        private string _tpmStatusText = LocalizationService.Get("MdlChecking", "Provera...");
        private Brush _tpmStatusBrush = CreateFrozenBrush(140, 140, 140);

        // Lokalna forenzika
        private bool _hasLocalTraces = false;
        private string _localTracesText = LocalizationService.Get("ApTracesClean", "Čisto (Nema registarskih tragova)");
        private Brush _localTracesBrush = CreateFrozenBrush(46, 204, 113);
        private string _tenantDomain = LocalizationService.Get("MdlApNoneNotAssigned", "Nema (Nije dodeljen)");
        private string _tenantId = "N/A";
        private string _assignedDeviceName = "N/A";
        private string _azureAdStatus = LocalizationService.Get("MdlApNotJoined", "Nije pridružen (NO)");
        private string _mdmStatus = LocalizationService.Get("MdlApNotEnrolled", "Nije upisan (NO)");
        private int _orgCertificatesCount = 0;

        // Cloud ZTD
        private bool _cloudChecked = false;
        private bool _isRegistered = false;
        private bool _isLocked = false;
        private string _cloudCheckedText = LocalizationService.Get("MdlApCloudWaiting", "Čeka se provera...");
        private string _cloudStatusText = LocalizationService.Get("MdlApCloudConnecting", "Povezivanje sa ztd.dds.microsoft.com...");
        private Brush _cloudStatusBrush = CreateFrozenBrush(140, 140, 140);
        private string _ztdErrorCode = "N/A";
        private string _profileTenantDomain = "";
        private string _profileTenantId = "";
        private string _rawStatusMessage = "N/A";

        // UI Stanje
        private bool _isAlertActive = false;
        private bool _isScanning = false;
        private string _lastScanTime = LocalizationService.Get("MdlNotStarted", "Nije pokrenuto");
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
                    UpdateTracesDisplay();
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
                    CloudCheckedText = value
                        ? LocalizationService.Get("ApCloudOnlineChecked", "Završena online provera")
                        : LocalizationService.Get("ApCloudOffline", "Offline / Nije provereno");
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

    public class PeripheralBadgeItem : BaseViewModel
    {
        public string Label { get; set; }
        public string Detail { get; set; }
        public Brush AccentBrush { get; set; }
        public Brush BorderBrush { get; set; }
        public Brush BackgroundBrush { get; set; }
    }

    public class HardwareInfoViewModel : BaseViewModel
    {
        // CPU
        private string _cpuModel = LocalizationService.Get("MdlHwLoadingCpu", "Učitavanje CPU...");
        private string _cpuCoresThreads = "N/A";
        private string _cpuClock = "N/A";
        private string _cpuSocket = "N/A";

        public string CpuModel { get { return _cpuModel; } set { SetProperty(ref _cpuModel, value); } }
        public string CpuCoresThreads { get { return _cpuCoresThreads; } set { SetProperty(ref _cpuCoresThreads, value); } }
        public string CpuClock { get { return _cpuClock; } set { SetProperty(ref _cpuClock, value); } }
        public string CpuSocket { get { return _cpuSocket; } set { SetProperty(ref _cpuSocket, value); } }

        // RAM
        private string _ramSummary = LocalizationService.Get("MdlHwLoadingRam", "Učitavanje RAM...");
        private string _ramDetails = "N/A";
        private string _ramSpeed = "N/A";
        private string _ramType = "N/A";
        private bool _isRamSoldered = false;
        private string _ramSolderedBadge = LocalizationService.Get("HwRamSodimm", "Standardni moduli (SO-DIMM)");

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
            if (_isRamSoldered)
            {
                RamSolderedBadge = LocalizationService.Get("MdlRamSoldered", "Integrisan (LPDDR / Soldered)");
            }
            else
            {
                RamSolderedBadge = LocalizationService.Get("HwRamSodimm", "Standardni moduli (SO-DIMM)");
            }

            if (_hasIrCamera)
            {
                IrCameraStatus = LocalizationService.Instance.GetString("HwIrYes", "👁️ Da (Windows Hello Face)");
            }
            else if (_irCameraStatus != "Detekcija IR..." && _irCameraStatus != "检测中..." && _irCameraStatus != LocalizationService.Get("MdlHwDetectingIr", "Detekcija IR..."))
            {
                IrCameraStatus = LocalizationService.Instance.GetString("HwIrNo", "Ne (Standardna RGB kamera)");
            }

            if (_hasFingerprint)
            {
                string devName = !string.IsNullOrEmpty(_fingerprintDeviceName) ? _fingerprintDeviceName : "Sensor";
                FingerprintSensor = string.Format(LocalizationService.Instance.GetString("HwFpYes", "👆 Da ({0})"), devName);
            }
            else if (_fingerprintSensor != "Detekcija senzora..." && _fingerprintSensor != "检测中..." && _fingerprintSensor != LocalizationService.Get("MdlHwDetectingSensor", "Detekcija senzora..."))
            {
                FingerprintSensor = LocalizationService.Instance.GetString("HwFpNo", "Nije detektovan");
            }

            if (_hasBacklitKeyboard || _backlitKeyboardConfirmed == true)
            {
                BacklitKeyboardStatus = LocalizationService.Instance.GetString("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
            }
            else if (_backlitKeyboardConfirmed == false)
            {
                BacklitKeyboardStatus = LocalizationService.Instance.GetString("HwKbNo", "Standardna (Bez osvetljenja)");
            }
            else if (_backlitIsManual || (_backlitKeyboardStatus != "Detekcija..." && _backlitKeyboardStatus != "检测中..." && _backlitKeyboardStatus != LocalizationService.Get("MdlDetecting", "Detekcija...")))
            {
                BacklitKeyboardStatus = LocalizationService.Get("HwKbUnconfirmed", "Nije potvrđeno (provera u Fazi 5)");
            }

            if (_hasTouchscreen)
            {
                TouchscreenStatus = LocalizationService.Instance.GetString("HwTouchYesSimple", "🖐️ Podržan (Touchscreen)");
            }
            else if (_touchscreenStatus != "Detekcija..." && _touchscreenStatus != "检测中..." && _touchscreenStatus != LocalizationService.Get("MdlDetecting", "Detekcija..."))
            {
                TouchscreenStatus = LocalizationService.Instance.GetString("HwTouchNo", "Nije detektovan (Standardni displej)");
            }

            if (!string.IsNullOrEmpty(_speakersDeviceName))
            {
                AudioSpeakers = "🔊 " + _speakersDeviceName;
            }
            else if (_audioSpeakers != "Detekcija..." && _audioSpeakers != "检测中..." && _audioSpeakers != LocalizationService.Get("MdlDetecting", "Detekcija..."))
            {
                AudioSpeakers = LocalizationService.Instance.GetString("HwAudioSpeakersDefault", "Standardni Audio uređaj");
            }

            if (!string.IsNullOrEmpty(_micDeviceName))
            {
                MicrophoneStatus = string.Format(LocalizationService.Instance.GetString("HwMicYes", "🎤 Da ({0})"), _micDeviceName);
            }
            else if (_microphoneStatus != "Detekcija..." && _microphoneStatus != "检测中..." && _microphoneStatus != LocalizationService.Get("MdlDetecting", "Detekcija..."))
            {
                MicrophoneStatus = LocalizationService.Instance.GetString("HwMicNo", "Nije detektovan");
            }

            OnPropertyChanged("OverviewResolution");
            OnPropertyChanged("OverviewResolutionTooltip");
            RebuildOverviewPeripherals(false);
        }
        private string _ramSlotsSummary = LocalizationService.Get("MdlHwDetectingSlots", "Detekcija slotova...");
        public string RamSlotsSummary { get { return _ramSlotsSummary; } set { SetProperty(ref _ramSlotsSummary, value); } }
        public int RamTotalSlots { get; set; }
        public int RamUsedSlots { get; set; }
        public int RamFreeSlots { get; set; }

        // SSD / Skladište
        private string _storageModel = LocalizationService.Get("MdlHwLoadingStorage", "Učitavanje SSD/Diska...");
        private string _storageType = "NVMe / SATA";
        private string _storageHealth = "100%";
        private string _storageHours = LocalizationService.Get("MdlHwStorageHoursNa", "N/A (Zahteva S.M.A.R.T.)");
        private string _storageTemp = LocalizationService.Get("MdlHwStorageTempNa", "N/A (Senzor nedostupan)");
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
        private string _gpuPrimary = LocalizationService.Get("MdlHwLoadingGpu", "Učitavanje GPU...");
        private string _gpuSecondary = "";
        private bool _hasDualGpu = false;
        private string _displaySpec = LocalizationService.Get("MdlHwDetectingDisplay", "Detekcija displeja...");

        public string GpuPrimary
        {
            get { return _gpuPrimary; }
            set { if (SetProperty(ref _gpuPrimary, value)) NotifyOverviewGpuChanged(); }
        }
        public string GpuSecondary
        {
            get { return _gpuSecondary; }
            set { if (SetProperty(ref _gpuSecondary, value)) NotifyOverviewGpuChanged(); }
        }
        public bool HasDualGpu
        {
            get { return _hasDualGpu; }
            set { if (SetProperty(ref _hasDualGpu, value)) NotifyOverviewGpuChanged(); }
        }
        public string DisplaySpec
        {
            get { return _displaySpec; }
            set
            {
                if (SetProperty(ref _displaySpec, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }

        private string _gpuPrimaryVram = LocalizationService.Get("MdlLoading", "Učitavanje...");
        public string GpuPrimaryVram
        {
            get { return _gpuPrimaryVram; }
            set { if (SetProperty(ref _gpuPrimaryVram, value)) NotifyOverviewGpuChanged(); }
        }
        private string _gpuSecondaryVram = "";
        public string GpuSecondaryVram
        {
            get { return _gpuSecondaryVram; }
            set { if (SetProperty(ref _gpuSecondaryVram, value)) NotifyOverviewGpuChanged(); }
        }

        private bool _hasTouchscreen = false;
        public bool HasTouchscreen
        {
            get { return _hasTouchscreen; }
            set { if (SetProperty(ref _hasTouchscreen, value)) RebuildOverviewPeripherals(false); }
        }
        private string _touchscreenStatus = LocalizationService.Get("MdlDetecting", "Detekcija...");
        public string TouchscreenStatus { get { return _touchscreenStatus; } set { SetProperty(ref _touchscreenStatus, value); } }
        private SolidColorBrush _touchscreenBrush = CreateFrozenBrush(136, 136, 136);
        public SolidColorBrush TouchscreenBrush { get { return _touchscreenBrush; } set { SetProperty(ref _touchscreenBrush, value); } }

        // Matična ploča & BIOS
        private string _motherboardModel = LocalizationService.Get("MdlHwLoadingMb", "Učitavanje ploče...");
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
        private string _webcamModel = LocalizationService.Get("HwCamDetecting", "Detekcija kamere...");
        private bool _hasWebcam = false;
        private SolidColorBrush _webcamBrush = CreateFrozenBrush(136, 136, 136);

        public string WebcamModel { get { return _webcamModel; } set { SetProperty(ref _webcamModel, value); } }
        public bool HasWebcam
        {
            get { return _hasWebcam; }
            set { if (SetProperty(ref _hasWebcam, value)) RebuildOverviewPeripherals(false); }
        }
        public SolidColorBrush WebcamBrush { get { return _webcamBrush; } set { SetProperty(ref _webcamBrush, value); } }

        private string _irCameraStatus = LocalizationService.Get("MdlHwDetectingIr", "Detekcija IR...");
        private bool _hasIrCamera = false;
        private SolidColorBrush _irCameraBrush = CreateFrozenBrush(136, 136, 136);

        public string IrCameraStatus { get { return _irCameraStatus; } set { SetProperty(ref _irCameraStatus, value); } }
        public bool HasIrCamera
        {
            get { return _hasIrCamera; }
            set { if (SetProperty(ref _hasIrCamera, value)) RebuildOverviewPeripherals(false); }
        }
        public SolidColorBrush IrCameraBrush { get { return _irCameraBrush; } set { SetProperty(ref _irCameraBrush, value); } }

        private string _backlitKeyboardStatus = LocalizationService.Get("MdlDetecting", "Detekcija...");
        private bool _hasBacklitKeyboard = false;
        private bool? _backlitKeyboardConfirmed = null;
        private string _backlitKeyboardReason = "";
        private bool _hasAuthoritativeBacklitVerdict = false;
        private SolidColorBrush _backlitKeyboardBrush = CreateFrozenBrush(136, 136, 136);

        public string BacklitKeyboardStatus { get { return _backlitKeyboardStatus; } set { SetProperty(ref _backlitKeyboardStatus, value); } }
        public bool HasBacklitKeyboard
        {
            get { return _hasBacklitKeyboard; }
            set { if (SetProperty(ref _hasBacklitKeyboard, value)) RebuildOverviewPeripherals(false); }
        }
        public bool? BacklitKeyboardConfirmed
        {
            get { return _backlitKeyboardConfirmed; }
            set { SetProperty(ref _backlitKeyboardConfirmed, value); }
        }
        public string BacklitKeyboardReason
        {
            get { return _backlitKeyboardReason; }
            set { SetProperty(ref _backlitKeyboardReason, value); }
        }
        public bool HasAuthoritativeBacklitVerdict
        {
            get { return _hasAuthoritativeBacklitVerdict; }
            set { SetProperty(ref _hasAuthoritativeBacklitVerdict, value); }
        }
        public SolidColorBrush BacklitKeyboardBrush { get { return _backlitKeyboardBrush; } set { SetProperty(ref _backlitKeyboardBrush, value); } }

        /// <summary>
        /// Primenjuje autoritativnu odluku za pozadinsko osvetljenje tastature (iz Faze 5 HwTest-a ili OEM BOM-a).
        /// </summary>
        public void ApplyBacklitKeyboardVerdict(bool? confirmed, string sourceReason)
        {
            ApplyBacklitKeyboardState(confirmed, sourceReason, confirmed.HasValue);
        }

        internal void ApplyDetectedBacklitKeyboard(bool? confirmed, string sourceReason)
        {
            if (_hasAuthoritativeBacklitVerdict && _backlitKeyboardConfirmed.HasValue)
            {
                return;
            }
            ApplyBacklitKeyboardState(confirmed, sourceReason, false);
        }

        private void ApplyBacklitKeyboardState(bool? confirmed, string sourceReason, bool isAuthoritative)
        {
            _backlitKeyboardConfirmed = confirmed;
            _backlitKeyboardReason = sourceReason ?? "";
            if (isAuthoritative)
            {
                _hasAuthoritativeBacklitVerdict = true;
            }
            else if (!confirmed.HasValue)
            {
                _hasAuthoritativeBacklitVerdict = false;
            }

            if (confirmed == true)
            {
                _hasBacklitKeyboard = true;
                _backlitIsManual = false;
                BacklitKeyboardStatus = LocalizationService.Get("HwKbYes", "💡 Da (Pozadinsko osvetljenje)");
                BacklitKeyboardBrush = CreateFrozenBrush(78, 201, 176);
            }
            else if (confirmed == false)
            {
                _hasBacklitKeyboard = false;
                _backlitIsManual = false;
                BacklitKeyboardStatus = LocalizationService.Get("HwKbNo", "Standardna (Bez osvetljenja)");
                BacklitKeyboardBrush = CreateFrozenBrush(136, 136, 136);
            }
            else
            {
                _hasBacklitKeyboard = false;
                _backlitIsManual = true;
                BacklitKeyboardStatus = LocalizationService.Get("HwKbUnconfirmed", "Nije potvrđeno (provera u Fazi 5)");
                BacklitKeyboardBrush = CreateFrozenBrush(241, 196, 15);
            }

            OnPropertyChanged("HasBacklitKeyboard");
            OnPropertyChanged("BacklitIsManual");
            OnPropertyChanged("BacklitKeyboardConfirmed");
            OnPropertyChanged("BacklitKeyboardReason");
            OnPropertyChanged("HasAuthoritativeBacklitVerdict");
            RebuildOverviewPeripherals(false);
        }

        private string _fingerprintSensor = LocalizationService.Get("MdlHwDetectingSensor", "Detekcija senzora...");
        private bool _hasFingerprint = false;
        private SolidColorBrush _fingerprintBrush = CreateFrozenBrush(136, 136, 136);

        public string FingerprintSensor { get { return _fingerprintSensor; } set { SetProperty(ref _fingerprintSensor, value); } }
        public bool HasFingerprint
        {
            get { return _hasFingerprint; }
            set { if (SetProperty(ref _hasFingerprint, value)) RebuildOverviewPeripherals(false); }
        }
        public SolidColorBrush FingerprintBrush { get { return _fingerprintBrush; } set { SetProperty(ref _fingerprintBrush, value); } }

        // Model panela, osvetljenje i raspon boja (v2.14.0 - HW-03)
        private string _displayModel = "N/A";
        private string _displayNits = "N/A";
        private string _displayColorGamut = "N/A";

        public string DisplayModel
        {
            get { return _displayModel; }
            set
            {
                if (SetProperty(ref _displayModel, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }
        public string DisplayNits { get { return _displayNits; } set { SetProperty(ref _displayNits, value); } }
        public string DisplayColorGamut { get { return _displayColorGamut; } set { SetProperty(ref _displayColorGamut, value); } }

        // Strukturisani podaci o ekranu za nalepnicu i Pregled (WIS-TASK-188 / WIS-TASK-261)
        private int _displayWidth = 0;
        private int _displayHeight = 0;
        private int _displayRefreshHz = 0;
        private string _displayPanelType = "";
        private double _displayDiagonalInches = 0.0;

        public int DisplayWidth
        {
            get { return _displayWidth; }
            set
            {
                if (SetProperty(ref _displayWidth, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }
        public int DisplayHeight
        {
            get { return _displayHeight; }
            set
            {
                if (SetProperty(ref _displayHeight, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }
        public int DisplayRefreshHz
        {
            get { return _displayRefreshHz; }
            set
            {
                if (SetProperty(ref _displayRefreshHz, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }
        public string DisplayPanelType
        {
            get { return _displayPanelType; }
            set
            {
                if (SetProperty(ref _displayPanelType, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }
        public double DisplayDiagonalInches
        {
            get { return _displayDiagonalInches; }
            set
            {
                if (SetProperty(ref _displayDiagonalInches, value))
                {
                    OnPropertyChanged("OverviewResolution");
                    OnPropertyChanged("OverviewResolutionTooltip");
                }
            }
        }

        private static string FormatDiagonalInches(double inches)
        {
            if (inches < 8.0 || inches > 55.0) return "";
            double rounded = Math.Round(inches, 1);
            if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}\"", Math.Round(rounded));
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}\"", rounded);
        }

        private static string FormatShortPanelType(string rawPanel)
        {
            if (string.IsNullOrWhiteSpace(rawPanel)) return "";
            string up = rawPanel.Trim().ToUpperInvariant();
            if (up.Contains("OLED") || up.Contains("AMOLED")) return "OLED";
            if (up.Contains("IPS")) return "IPS";
            if (up.Contains("VA")) return "VA";
            if (up.Contains("TN")) return "TN";
            return rawPanel.Trim();
        }

        public string OverviewResolution
        {
            get
            {
                if (_displayWidth > 0 && _displayHeight > 0)
                {
                    var parts = new List<string>();
                    string diagText = FormatDiagonalInches(_displayDiagonalInches);
                    if (!string.IsNullOrEmpty(diagText))
                    {
                        parts.Add(diagText);
                    }

                    string resClass = LabelPrintService.ClassifyResolution(_displayWidth, _displayHeight);
                    if (!string.IsNullOrEmpty(resClass))
                    {
                        parts.Add(resClass);
                    }

                    string panelShort = FormatShortPanelType(_displayPanelType);
                    if (!string.IsNullOrEmpty(panelShort))
                    {
                        parts.Add(panelShort);
                    }

                    string main = string.Join(" ", parts.ToArray());
                    if (_displayRefreshHz > 0)
                    {
                        return string.Format("{0} @ {1} Hz", main, _displayRefreshHz);
                    }
                    return main;
                }
                if (!string.IsNullOrWhiteSpace(_displaySpec))
                {
                    return _displaySpec;
                }
                return LocalizationService.Get("MdlDetecting", "Detekcija...");
            }
        }

        public string OverviewResolutionTooltip
        {
            get
            {
                if (_displayWidth > 0 && _displayHeight > 0)
                {
                    var parts = new List<string>();
                    string diagText = FormatDiagonalInches(_displayDiagonalInches);
                    string resClass = LabelPrintService.ClassifyResolution(_displayWidth, _displayHeight);
                    string head = string.IsNullOrEmpty(diagText) ? resClass : string.Format("{0} {1}", diagText, resClass);
                    parts.Add(string.Format("{0} ({1}×{2})", head, _displayWidth, _displayHeight));

                    string panelShort = FormatShortPanelType(_displayPanelType);
                    if (!string.IsNullOrEmpty(panelShort)) parts.Add(panelShort);
                    if (_displayRefreshHz > 0) parts.Add(string.Format("{0} Hz", _displayRefreshHz));

                    if (!string.IsNullOrWhiteSpace(_displayModel) &&
                        !string.Equals(_displayModel, "Standardni panel", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(_displayModel, "Detekcija...", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(_displayModel, "N/A", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(_displayModel.Trim());
                    }

                    return string.Join(" • ", parts.ToArray());
                }
                return OverviewResolution;
            }
        }

        public string OverviewGpu
        {
            get
            {
                string mainGpu;
                string igpu;
                ResolveOverviewGpus(out mainGpu, out igpu);
                return mainGpu;
            }
        }

        public string OverviewIgpu
        {
            get
            {
                string mainGpu;
                string igpu;
                ResolveOverviewGpus(out mainGpu, out igpu);
                return igpu;
            }
        }

        public bool HasOverviewIgpu
        {
            get { return !string.IsNullOrWhiteSpace(OverviewIgpu); }
        }

        private void NotifyOverviewGpuChanged()
        {
            OnPropertyChanged("OverviewGpu");
            OnPropertyChanged("OverviewIgpu");
            OnPropertyChanged("HasOverviewIgpu");
        }

        private void ResolveOverviewGpus(out string mainGpu, out string igpu)
        {
            string g1 = (_gpuPrimary ?? "").Trim();
            string g2 = (_gpuSecondary ?? "").Trim();
            bool hasValidSecond = _hasDualGpu &&
                !string.IsNullOrWhiteSpace(g2) &&
                !g2.StartsWith("Nema", StringComparison.OrdinalIgnoreCase);

            if (hasValidSecond)
            {
                if (IsIntegratedGpuName(g1) && !IsIntegratedGpuName(g2))
                {
                    mainGpu = FormatGpuWithVram(g2, _gpuSecondaryVram);
                    igpu = g1;
                    return;
                }
                mainGpu = FormatGpuWithVram(g1, _gpuPrimaryVram);
                igpu = g2;
                return;
            }

            mainGpu = FormatGpuWithVram(g1, _gpuPrimaryVram);
            igpu = "";
        }

        private static bool IsIntegratedGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string s = name.ToUpperInvariant();
            if (s.Contains("NVIDIA") || s.Contains("GEFORCE") || s.Contains("RTX") || s.Contains("GTX") ||
                s.Contains("QUADRO") || s.Contains("RX ") || s.Contains("ARC A"))
            {
                return false;
            }
            return s.Contains("INTEL") || s.Contains("UHD") || s.Contains("IRIS") ||
                   s.Contains("HD GRAPHICS") || s.Contains("RADEON(TM) GRAPHICS") ||
                   s.Contains("RADEON GRAPHICS") || s.Contains("VEGA");
        }

        private static string FormatGpuWithVram(string name, string vram)
        {
            string n = (name ?? "").Trim();
            if (string.IsNullOrEmpty(n)) return LocalizationService.Get("MdlDetecting", "Detekcija...");
            string v = (vram ?? "").Trim();
            if (string.IsNullOrEmpty(v) ||
                string.Equals(v, "N/A", StringComparison.OrdinalIgnoreCase) ||
                v.IndexOf("Učitavanje", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("Detekcija", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("加载", StringComparison.OrdinalIgnoreCase) >= 0 ||
                v.IndexOf("检测", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return n;
            }
            return string.Format("{0} ({1})", n, v);
        }

        // Audio zvučnici i mikrofon (v2.14.0 - HW-07)
        private string _audioSpeakers = LocalizationService.Get("MdlDetecting", "Detekcija...");
        private SolidColorBrush _audioSpeakersBrush = CreateFrozenBrush(136, 136, 136);
        private string _microphoneStatus = LocalizationService.Get("MdlDetecting", "Detekcija...");
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

        // Dodatne periferije i Wi-Fi verzija za karticu Pregleda (WIS-TASK-261 / V343-07)
        private string _wifiAdapterName = "";
        private string _wifiStandardLabel = "";
        private bool _hasWifi = false;
        private bool _hasBluetooth = false;
        private string _bluetoothInfo = "";
        private string _bluetoothVersionLabel = "";
        private bool _hasWwan = false;
        private string _wwanInfo = "";
        private string _wwanAdapterName = "";
        private bool _hasEsim = false;
        private string _esimInfo = "";
        private bool _hasSmartCard = false;
        private bool _hasNfc = false;
        private bool _hasThunderbolt = false;
        private bool _peripheralsDetectionCompleted = false;
        private string _overviewPeripheralsEmptyText = LocalizationService.Get("MdlPeriphEmptyDetecting", "Detekcija periferija...");
        private readonly ObservableCollection<PeripheralBadgeItem> _overviewPeripherals = new ObservableCollection<PeripheralBadgeItem>();

        public string WifiAdapterName { get { return _wifiAdapterName; } set { SetProperty(ref _wifiAdapterName, value); } }
        public string WifiStandardLabel
        {
            get { return _wifiStandardLabel; }
            set { if (SetProperty(ref _wifiStandardLabel, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasWifi
        {
            get { return _hasWifi; }
            set { if (SetProperty(ref _hasWifi, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasBluetooth
        {
            get { return _hasBluetooth; }
            set { if (SetProperty(ref _hasBluetooth, value)) RebuildOverviewPeripherals(false); }
        }
        public string BluetoothInfo { get { return _bluetoothInfo; } set { SetProperty(ref _bluetoothInfo, value); } }
        public string BluetoothVersionLabel
        {
            get { return _bluetoothVersionLabel; }
            set { if (SetProperty(ref _bluetoothVersionLabel, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasWwan
        {
            get { return _hasWwan; }
            set { if (SetProperty(ref _hasWwan, value)) RebuildOverviewPeripherals(false); }
        }
        public string WwanInfo
        {
            get { return _wwanInfo; }
            set { if (SetProperty(ref _wwanInfo, value)) RebuildOverviewPeripherals(false); }
        }
        public string WwanAdapterName { get { return _wwanAdapterName; } set { SetProperty(ref _wwanAdapterName, value); } }
        public bool HasEsim
        {
            get { return _hasEsim; }
            set { if (SetProperty(ref _hasEsim, value)) RebuildOverviewPeripherals(false); }
        }
        public string EsimInfo
        {
            get { return _esimInfo; }
            set { if (SetProperty(ref _esimInfo, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasSmartCard
        {
            get { return _hasSmartCard; }
            set { if (SetProperty(ref _hasSmartCard, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasNfc
        {
            get { return _hasNfc; }
            set { if (SetProperty(ref _hasNfc, value)) RebuildOverviewPeripherals(false); }
        }
        public bool HasThunderbolt
        {
            get { return _hasThunderbolt; }
            set { if (SetProperty(ref _hasThunderbolt, value)) RebuildOverviewPeripherals(false); }
        }

        public ObservableCollection<PeripheralBadgeItem> OverviewPeripherals { get { return _overviewPeripherals; } }
        public bool HasOverviewPeripherals { get { return _overviewPeripherals.Count > 0; } }
        public bool ShowOverviewPeripheralsEmpty { get { return _overviewPeripherals.Count == 0; } }
        public string OverviewPeripheralsEmptyText
        {
            get { return _overviewPeripheralsEmptyText; }
            private set { SetProperty(ref _overviewPeripheralsEmptyText, value); }
        }

        public void RebuildOverviewPeripherals(bool detectionCompleted = false)
        {
            if (detectionCompleted)
            {
                _peripheralsDetectionCompleted = true;
            }

            _overviewPeripherals.Clear();

            Brush neutralAccent = CreateFrozenBrush(156, 220, 254);
            Brush neutralBorder = CreateFrozenBrush(62, 62, 66);
            Brush neutralBg = CreateFrozenBrush(37, 37, 40);

            if (_hasWifi)
            {
                string wifiLabel = !string.IsNullOrWhiteSpace(_wifiStandardLabel)
                    ? _wifiStandardLabel
                    : (!string.IsNullOrWhiteSpace(_wifiAdapterName) ? _wifiAdapterName : "Wi-Fi");
                AddPeripheralBadge(wifiLabel, _wifiAdapterName, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasWwan)
            {
                string wwanLabel = !string.IsNullOrWhiteSpace(_wwanInfo) ? _wwanInfo : LocalizationService.Get("MdlPeriphWwanFallback", "4G LTE / 5G kartica");
                string wwanDetail = !string.IsNullOrWhiteSpace(_wwanAdapterName) ? _wwanAdapterName : wwanLabel;
                AddPeripheralBadge(wwanLabel, wwanDetail, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasEsim)
            {
                string esimDetail = !string.IsNullOrWhiteSpace(_esimInfo) ? _esimInfo : LocalizationService.Get("MdlPeriphEsimFallback", "Podržan eSIM (eUICC)");
                AddPeripheralBadge("eSIM", esimDetail, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasBluetooth)
            {
                string btLabel = !string.IsNullOrWhiteSpace(_bluetoothVersionLabel) ? _bluetoothVersionLabel : LocalizationService.Get("MdlPeriphBluetooth", "Bluetooth");
                AddPeripheralBadge(btLabel, _bluetoothInfo, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasBacklitKeyboard)
            {
                string kbDetail = !string.IsNullOrWhiteSpace(_backlitKeyboardReason) ? _backlitKeyboardReason : _backlitKeyboardStatus;
                AddPeripheralBadge(LocalizationService.Get("MdlPeriphBacklitKb", "Svetleća tastatura"), kbDetail, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasIrCamera)
            {
                AddPeripheralBadge(LocalizationService.Get("MdlPeriphIrCam", "IR kamera (Hello)"), _irCameraStatus, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasFingerprint)
            {
                string fpDetail = !string.IsNullOrWhiteSpace(_fingerprintDeviceName) ? _fingerprintDeviceName : _fingerprintSensor;
                AddPeripheralBadge(LocalizationService.Get("MdlPeriphFingerprint", "Fingerprint reader"), fpDetail, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasTouchscreen)
            {
                AddPeripheralBadge(LocalizationService.Get("MdlPeriphTouchscreen", "Touchscreen"), _touchscreenStatus, neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasSmartCard)
            {
                AddPeripheralBadge(
                    LocalizationService.Get("MdlPeriphSmartCard", "SmartCard čitač"),
                    LocalizationService.Get("MdlPeriphSmartCardDetail", "Detektovan čitač pametnih kartica"),
                    neutralAccent, neutralBorder, neutralBg);
            }
            if (_hasNfc)
            {
                AddPeripheralBadge(
                    LocalizationService.Get("MdlPeriphNfc", "NFC"),
                    LocalizationService.Get("MdlPeriphNfcDetail", "Detektovan NFC / Proximity uređaj"),
                    neutralAccent, neutralBorder, neutralBg);
            }

            if (_peripheralsDetectionCompleted && _overviewPeripherals.Count == 0)
            {
                OverviewPeripheralsEmptyText = LocalizationService.Get("MdlPeriphEmptyNone", "Nema detektovanih dodatnih periferija");
            }
            else if (_overviewPeripherals.Count == 0)
            {
                OverviewPeripheralsEmptyText = LocalizationService.Get("MdlPeriphEmptyDetecting", "Detekcija periferija...");
            }

            OnPropertyChanged("HasOverviewPeripherals");
            OnPropertyChanged("ShowOverviewPeripheralsEmpty");
        }

        private void AddPeripheralBadge(string label, string detail, Brush accent, Brush border, Brush bg)
        {
            _overviewPeripherals.Add(new PeripheralBadgeItem
            {
                Label = label,
                Detail = !string.IsNullOrWhiteSpace(detail) ? detail : label,
                AccentBrush = accent,
                BorderBrush = border,
                BackgroundBrush = bg
            });
        }
    }

    // =========================================================================
    // DRAJVERI I HARDVERSKI UREĐAJI (v2.7.3 - TASK-113)
    // =========================================================================

    public class DriverItemViewModel : BaseViewModel
    {
        private string _name = LocalizationService.Get("MdlUnknownDevice", "Nepoznat uređaj");
        private string _manufacturer = LocalizationService.Get("MdlUnknown", "Nepoznato");
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
        private string _missingSummaryText = LocalizationService.Get("MdlDrvLoadingSummary", "Učitavanje statusa drajvera...");
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

    public class HwMonitorSensorRow : BaseViewModel
    {
        private string _key;
        private string _name;
        private bool _isCategoryHeader;
        private string _currentText = "N/A";
        private string _minText = "—";
        private string _maxText = "—";
        private Brush _valueBrush = CreateFrozenBrush(78, 201, 176);
        private Brush _rowBackground = Brushes.Transparent;
        private bool _hasValue = false;
        private double _minNumeric = 0.0;
        private double _maxNumeric = 0.0;
        private double _currentNumeric = 0.0;

        public string Key { get { return _key; } set { SetProperty(ref _key, value); } }
        public string Name { get { return _name; } set { SetProperty(ref _name, value); } }
        public bool IsCategoryHeader { get { return _isCategoryHeader; } set { SetProperty(ref _isCategoryHeader, value); } }
        public string CurrentText { get { return _currentText; } set { SetProperty(ref _currentText, value); } }
        public string MinText { get { return _minText; } set { SetProperty(ref _minText, value); } }
        public string MaxText { get { return _maxText; } set { SetProperty(ref _maxText, value); } }
        public Brush ValueBrush { get { return _valueBrush; } set { SetProperty(ref _valueBrush, value); } }
        public Brush RowBackground { get { return _rowBackground; } set { SetProperty(ref _rowBackground, value); } }
        public bool HasValue { get { return _hasValue; } set { SetProperty(ref _hasValue, value); } }
        public double MinNumeric { get { return _minNumeric; } set { SetProperty(ref _minNumeric, value); } }
        public double MaxNumeric { get { return _maxNumeric; } set { SetProperty(ref _maxNumeric, value); } }
        public double CurrentNumeric { get { return _currentNumeric; } set { SetProperty(ref _currentNumeric, value); } }

        public Brush NameForeground
        {
            get
            {
                return _isCategoryHeader
                    ? CreateFrozenBrush(78, 201, 176)
                    : CreateFrozenBrush(224, 224, 224);
            }
        }

        public FontWeight NameFontWeight
        {
            get { return _isCategoryHeader ? FontWeights.Bold : FontWeights.Normal; }
        }

        public static HwMonitorSensorRow CreateHeader(string key, string title)
        {
            return new HwMonitorSensorRow
            {
                Key = key,
                Name = title,
                IsCategoryHeader = true,
                CurrentText = "",
                MinText = "",
                MaxText = "",
                ValueBrush = CreateFrozenBrush(136, 136, 136),
                RowBackground = CreateFrozenBrush(29, 34, 42)
            };
        }

        public static HwMonitorSensorRow CreateSensor(string key, string name, string initialText)
        {
            return new HwMonitorSensorRow
            {
                Key = key,
                Name = name,
                IsCategoryHeader = false,
                CurrentText = string.IsNullOrEmpty(initialText) ? "N/A" : initialText,
                MinText = "—",
                MaxText = "—",
                ValueBrush = CreateFrozenBrush(150, 150, 150),
                RowBackground = Brushes.Transparent
            };
        }

        public void UpdateSample(double value, string format, Brush valueBrush)
        {
            if (_isCategoryHeader || double.IsNaN(value) || double.IsInfinity(value)) return;
            CurrentNumeric = value;
            CurrentText = string.Format(format, value);
            if (valueBrush != null) ValueBrush = valueBrush;

            if (!HasValue)
            {
                MinNumeric = value;
                MaxNumeric = value;
                HasValue = true;
            }
            else
            {
                if (value < MinNumeric) MinNumeric = value;
                if (value > MaxNumeric) MaxNumeric = value;
            }
            MinText = string.Format(format, MinNumeric);
            MaxText = string.Format(format, MaxNumeric);
        }

        public void SetUnavailable(string label)
        {
            if (_isCategoryHeader) return;
            string txt = string.IsNullOrEmpty(label) ? "N/A" : label;
            CurrentText = txt;
            ValueBrush = CreateFrozenBrush(140, 140, 140);
            if (!HasValue)
            {
                MinText = "—";
                MaxText = "—";
            }
        }

        public void ResetExtremes()
        {
            if (_isCategoryHeader || !HasValue) return;
            MinNumeric = CurrentNumeric;
            MaxNumeric = CurrentNumeric;
            MinText = CurrentText;
            MaxText = CurrentText;
        }
    }

    public class MainViewModel : BaseViewModel
    {
        // ------------------ HEADER ------------------
        private string _computerModel = LocalizationService.Get("MdlLoadingConfig", "Učitavanje konfiguracije...");
        private string _serialNumber = "N/A";
        private string _cpuInfo = LocalizationService.Get("MdlDetectingCpu", "Detekcija CPU-a...");
        private string _ramInfo = LocalizationService.Get("MdlDetectingRam", "Detekcija RAM-a...");
        private string _diskInfo = LocalizationService.Get("MdlDetectingDisk", "Detekcija SSD/NVMe diska...");
        private string _durationString = "00:00:00";
        private TimeSpan _elapsedDuration = TimeSpan.Zero;
        private string _appVersionText = "WIS UI v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        private string _wisVersionFooterText = "WIS v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public string ComputerModel { get { return _computerModel; } set { SetProperty(ref _computerModel, value); } }
        public string SerialNumber
        {
            get { return _serialNumber; }
            set
            {
                if (SetProperty(ref _serialNumber, value))
                {
                    RefreshDeviceCardQr();
                }
            }
        }
        public string CpuInfo { get { return _cpuInfo; } set { SetProperty(ref _cpuInfo, value); } }
        public string RamInfo { get { return _ramInfo; } set { SetProperty(ref _ramInfo, value); } }
        public string DiskInfo { get { return _diskInfo; } set { SetProperty(ref _diskInfo, value); } }
        public string DurationString { get { return _durationString; } set { SetProperty(ref _durationString, value); } }
        public string AppVersionText { get { return _appVersionText; } set { SetProperty(ref _appVersionText, value); } }
        public string WisVersionFooterText { get { return _wisVersionFooterText; } set { SetProperty(ref _wisVersionFooterText, value); } }

        // ------------------ SREDNJI PANEL: QR KOD KARTONA UREĐAJA (SERVISHUB) ------------------
        private string _deviceCardHubUrl = "";
        private string _deviceCardUrl = "";
        private string _deviceCardStatusText = LocalizationService.Get("MdlHubConnecting", "Povezivanje sa ServisHub serverom...");
        private BitmapSource _deviceCardQrImage;
        private bool _hasDeviceCardQr = false;

        public string DeviceCardUrl { get { return _deviceCardUrl; } private set { SetProperty(ref _deviceCardUrl, value); } }
        public string DeviceCardStatusText { get { return _deviceCardStatusText; } private set { SetProperty(ref _deviceCardStatusText, value); } }
        public BitmapSource DeviceCardQrImage { get { return _deviceCardQrImage; } private set { SetProperty(ref _deviceCardQrImage, value); } }
        public bool HasDeviceCardQr
        {
            get { return _hasDeviceCardQr; }
            private set
            {
                if (SetProperty(ref _hasDeviceCardQr, value))
                {
                    OnPropertyChanged("ShowDeviceCardQrPlaceholder");
                }
            }
        }
        public bool ShowDeviceCardQrPlaceholder { get { return !_hasDeviceCardQr; } }

        public void UpdateDeviceCardHubUrl(string hubUrl)
        {
            string normalized = (hubUrl ?? "").Trim().TrimEnd('/');
            _deviceCardHubUrl = normalized;
            RefreshDeviceCardQr();
        }

        public void UpdateServiceHubConnection(string hubUrl, string hubVersion, bool isConnected)
        {
            ServiceHubConnected = isConnected;
            ServiceHubIndicatorBrush = isConnected
                ? CreateFrozenBrush(46, 204, 113)
                : CreateFrozenBrush(136, 136, 136);

            if (!string.IsNullOrWhiteSpace(hubVersion))
            {
                string cleanVer = hubVersion.Trim();
                if (!cleanVer.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    cleanVer = "v" + cleanVer;
                }
                ServiceHubVersion = cleanVer;
            }

            if (!string.IsNullOrEmpty(ServiceHubVersion))
            {
                ServiceHubFooterText = "ServiceHUB " + ServiceHubVersion;
            }
            else
            {
                ServiceHubFooterText = "ServiceHUB";
            }

            if (isConnected && !string.IsNullOrEmpty(hubUrl))
            {
                UpdateDeviceCardHubUrl(hubUrl);
            }
        }

        public void RefreshDeviceCardQr()
        {
            if (string.IsNullOrEmpty(_deviceCardHubUrl))
            {
                DeviceCardUrl = "";
                DeviceCardQrImage = null;
                HasDeviceCardQr = false;
                DeviceCardStatusText = LocalizationService.Get("MdlHubConnecting", "Povezivanje sa ServisHub serverom...");
                return;
            }

            string deviceId = null;
            if (!IsPlaceholderSerial(_serialNumber))
            {
                deviceId = _serialNumber.Trim();
            }
            else if (_hardwareInfo != null && !IsPlaceholderSerial(_hardwareInfo.SerialNumber))
            {
                deviceId = _hardwareInfo.SerialNumber.Trim();
            }
            else if (Autopilot != null && !IsPlaceholderSerial(Autopilot.HwSerialNumber))
            {
                deviceId = Autopilot.HwSerialNumber.Trim();
            }
            else
            {
                deviceId = Environment.MachineName;
            }

            string url = _deviceCardHubUrl + "/device/" + Uri.EscapeDataString(deviceId);
            if (string.Equals(url, _deviceCardUrl, StringComparison.Ordinal) && _deviceCardQrImage != null)
            {
                DeviceCardStatusText = LocalizationService.Get("MdlQrScanPrompt", "Skeniraj telefonom za karton uređaja");
                return;
            }

            BitmapSource bmp = GenerateQrBitmapSafe(url, 320);
            DeviceCardUrl = url;
            DeviceCardQrImage = bmp;
            HasDeviceCardQr = (bmp != null);
            DeviceCardStatusText = (bmp != null)
                ? LocalizationService.Get("MdlQrScanPrompt", "Skeniraj telefonom za karton uređaja")
                : LocalizationService.Get("MdlQrUnavailable", "QR generator nedostupan");
        }

        private static bool IsPlaceholderSerial(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return true;
            string s = val.Trim();
            if (s.Length < 2) return true;
            if (string.Equals(s, "N/A", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "Učitavanje...", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "Default string", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "System Serial Number", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "123456789", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static BitmapSource GenerateQrBitmapSafe(string content, int size)
        {
            if (string.IsNullOrEmpty(content)) return null;
            try
            {
                return GenerateQrBitmapWithZxing(content, size);
            }
            catch
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static BitmapSource GenerateQrBitmapWithZxing(string content, int size)
        {
            var writer = new ZXing.QrCode.QRCodeWriter();
            var hints = new Dictionary<ZXing.EncodeHintType, object>();
            hints[ZXing.EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M;
            hints[ZXing.EncodeHintType.MARGIN] = 2;
            hints[ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8";

            ZXing.Common.BitMatrix matrix = writer.encode(content, ZXing.BarcodeFormat.QR_CODE, size, size, hints);
            int w = matrix.Width;
            int h = matrix.Height;
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];

            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte c = matrix[x, y] ? (byte)0 : (byte)255;
                    int idx = rowOffset + (x * 4);
                    pixels[idx] = c;
                    pixels[idx + 1] = c;
                    pixels[idx + 2] = c;
                    pixels[idx + 3] = 255;
                }
            }

            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        // ------------------ TAB: HARDVER I SISTEM (v2.7.1) ------------------
        private HardwareInfoViewModel _hardwareInfo;
        public HardwareInfoViewModel HardwareInfo
        {
            get { return _hardwareInfo; }
            set
            {
                if (SetProperty(ref _hardwareInfo, value))
                {
                    RefreshDeviceCardQr();
                }
            }
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
        private string _phaseName = LocalizationService.Get("MdlInitPhaseName", "Faza 1: Mreža i povezivanje");
        private string _subPhase = LocalizationService.Get("MdlInitSubPhase", "Inicijalizacija mrežnih servisa...");
        private string _liveStatus = LocalizationService.Get("MdlInitLiveStatus", "Sistem priprema konfiguraciju servisa...");
        private int _progress = 0;
        private int _globalProgress = 0;
        private string _globalProgressSummary = LocalizationService.Get("MdlInitGlobalSummary", "0 / 28 koraka");
        private bool _usbFree = false;
        private string _usbBannerText = LocalizationService.Get("MdlUsbBannerText", "✓ USB FLEŠ DISK JE BEZBEDNO ODVOJEN (Možete slobodno izvaditi fleš disk)");

        public ObservableCollection<PhaseItem> Phases { get; private set; }
        public ObservableCollection<ActiveStepProgressItem> ActiveStepBars { get; private set; }
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

        public string PhaseName
        {
            get { return _phaseName; }
            set
            {
                if (SetProperty(ref _phaseName, value))
                {
                    RefreshProgressBars();
                }
            }
        }

        public string SubPhase
        {
            get { return _subPhase; }
            set
            {
                if (SetProperty(ref _subPhase, value))
                {
                    RefreshProgressBars();
                }
            }
        }

        public string LiveStatus
        {
            get { return _liveStatus; }
            set
            {
                if (SetProperty(ref _liveStatus, value))
                {
                    RefreshProgressBars();
                }
            }
        }

        public int Progress
        {
            get { return _progress; }
            set
            {
                if (SetProperty(ref _progress, Math.Max(0, Math.Min(100, value))))
                {
                    RefreshProgressBars();
                }
            }
        }

        public int GlobalProgress
        {
            get { return _globalProgress; }
            set { SetProperty(ref _globalProgress, Math.Max(0, Math.Min(100, value))); }
        }

        public string GlobalProgressSummary
        {
            get { return _globalProgressSummary; }
            set { SetProperty(ref _globalProgressSummary, value); }
        }

        private string _globalElapsedText = "00:00";
        private string _globalEtaText = "~24:00";
        private string _benchmarkSourceTooltip = "Procena ETA: Podrazumevani WIS v3 benchmark";
        private BenchmarkBaseline _benchmarkBaseline = null;
        private readonly Dictionary<string, DateTime> _activeBarStartUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public string GlobalElapsedText { get { return _globalElapsedText; } set { SetProperty(ref _globalElapsedText, value); } }
        public string GlobalEtaText { get { return _globalEtaText; } set { SetProperty(ref _globalEtaText, value); } }
        public string BenchmarkSourceTooltip { get { return _benchmarkSourceTooltip; } set { SetProperty(ref _benchmarkSourceTooltip, value); } }

        private static readonly Dictionary<int, double> DefaultPhaseBaselineSec = new Dictionary<int, double>
        {
            { 2, 300.0 },
            { 3, 60.0 },
            { 4, 660.0 },
            { 5, 180.0 },
            { 6, 240.0 }
        };

        private static readonly Dictionary<string, double> DefaultStepBaselineSec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "P2.Identify", 15.0 },
            { "P2.NasCache", 90.0 },
            { "P2.Vmd", 20.0 },
            { "P2.Pnp", 60.0 },
            { "P2.Diag", 15.0 },
            { "P2.WuDrivers", 100.0 },
            { "P3.Debloat", 18.0 },
            { "P3.Telemetry", 8.0 },
            { "P3.Oem", 8.0 },
            { "P3.Scaling", 6.0 },
            { "P3.VcRedist", 15.0 },
            { "P3.UsbFree", 5.0 },
            { "P4.Runtimes", 45.0 },
            { "P4.WinRAR", 15.0 },
            { "P4.Chrome", 35.0 },
            { "P4.Acrobat", 150.0 },
            { "P4.Office", 320.0 },
            { "P4.BatteryBar", 15.0 },
            { "P4.Shortcuts", 10.0 },
            { "P4.DefaultPdf", 10.0 },
            { "P4.License", 60.0 },
            { "P5.AutoDiag", 30.0 },
            { "P5.Stress", 60.0 },
            { "P5.Interactive", 90.0 },
            { "P6.WuSecondPass", 90.0 },
            { "P6.Report", 30.0 },
            { "P6.AntiLeak", 30.0 },
            { "P6.FinalVerify", 90.0 },
            { "bg_wu", 480.0 },
            { "drv_batch", 180.0 },
            { "current", 120.0 }
        };

        public static string FormatCompactDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                return "--:--";
            }
            int total = (int)Math.Floor(seconds);
            int hrs = total / 3600;
            int mins = (total % 3600) / 60;
            int secs = total % 60;
            if (hrs > 0)
            {
                return string.Format("{0}:{1:00}:{2:00}", hrs, mins, secs);
            }
            return string.Format("{0:00}:{1:00}", mins, secs);
        }

        public void ApplyBenchmarkBaseline(BenchmarkBaseline baseline)
        {
            if (baseline == null) return;
            Action apply = delegate
            {
                _benchmarkBaseline = baseline;
                UpdateLiveTimersAndEta();
                RebuildOverviewAlerts();
            };
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(apply);
            }
            else
            {
                apply();
            }
        }

        private double GetModelScaleFactor(out string matchedModelLabel)
        {
            matchedModelLabel = null;
            if (_benchmarkBaseline == null || _benchmarkBaseline.ModelAveragesSeconds == null || _benchmarkBaseline.ModelAveragesSeconds.Count == 0)
            {
                return 1.0;
            }
            string model = (_computerModel ?? "").Trim();
            if (string.IsNullOrEmpty(model))
            {
                return 1.0;
            }

            double modelAvg = 0.0;
            if (_benchmarkBaseline.ModelAveragesSeconds.TryGetValue(model, out modelAvg))
            {
                matchedModelLabel = model;
            }
            else
            {
                foreach (KeyValuePair<string, double> kv in _benchmarkBaseline.ModelAveragesSeconds)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.Length < 4) continue;
                    if (model.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        kv.Key.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedModelLabel = kv.Key;
                        modelAvg = kv.Value;
                        break;
                    }
                }
            }

            if (matchedModelLabel == null || modelAvg < 120.0 || modelAvg > 5400.0)
            {
                matchedModelLabel = null;
                return 1.0;
            }

            double baseTotal = _benchmarkBaseline.FleetP50TotalSeconds > 120.0 ? _benchmarkBaseline.FleetP50TotalSeconds : 1440.0;
            double ratio = modelAvg / baseTotal;
            if (ratio < 0.45) ratio = 0.45;
            if (ratio > 2.5) ratio = 2.5;
            return ratio;
        }

        private double GetPhaseExpectedSeconds(int phaseNum)
        {
            double baseVal = 120.0;
            double defVal;
            if (DefaultPhaseBaselineSec.TryGetValue(phaseNum, out defVal))
            {
                baseVal = defVal;
            }
            if (_benchmarkBaseline != null && _benchmarkBaseline.PhaseAveragesSeconds != null)
            {
                double hubVal;
                if (_benchmarkBaseline.PhaseAveragesSeconds.TryGetValue(phaseNum, out hubVal) && hubVal >= 15.0 && hubVal <= 2400.0)
                {
                    baseVal = hubVal;
                }
            }
            string unused;
            return baseVal * GetModelScaleFactor(out unused);
        }

        private double EstimateStepExpectedSeconds(string stepId, int phaseNum)
        {
            if (string.Equals(stepId, "bg_wu", StringComparison.OrdinalIgnoreCase)) return 480.0;
            if (string.Equals(stepId, "drv_batch", StringComparison.OrdinalIgnoreCase)) return 180.0;
            if (!string.IsNullOrEmpty(stepId) && stepId.StartsWith("drv_", StringComparison.OrdinalIgnoreCase)) return 45.0;
            if (string.Equals(stepId, "current", StringComparison.OrdinalIgnoreCase)) return GetPhaseExpectedSeconds(phaseNum);

            double phaseExpected = GetPhaseExpectedSeconds(phaseNum);
            double stepDef;
            if (!string.IsNullOrEmpty(stepId) && DefaultStepBaselineSec.TryGetValue(stepId, out stepDef))
            {
                string prefix = string.Format("P{0}.", phaseNum);
                double sumPhase = 0.0;
                foreach (KeyValuePair<string, double> kv in DefaultStepBaselineSec)
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        sumPhase += kv.Value;
                    }
                }
                if (sumPhase > 0.0)
                {
                    return (stepDef / sumPhase) * phaseExpected;
                }
                return stepDef;
            }

            if (Phases != null)
            {
                for (int i = 0; i < Phases.Count; i++)
                {
                    if (Phases[i] != null && Phases[i].Number == phaseNum && Phases[i].Steps != null && Phases[i].Steps.Count > 0)
                    {
                        return phaseExpected / Phases[i].Steps.Count;
                    }
                }
            }
            return 60.0;
        }

        private int ComputeStepEtaSeconds(ActiveStepProgressItem bar, int elapsedSec)
        {
            if (bar == null) return -1;
            if (string.Equals(bar.Id, "ready", StringComparison.OrdinalIgnoreCase)) return -1;
            if (string.Equals(bar.Id, "done", StringComparison.OrdinalIgnoreCase) || bar.Progress >= 100) return 0;

            double expected = EstimateStepExpectedSeconds(bar.Id, bar.PhaseNumber);
            double benchRem = Math.Max(0.0, expected - elapsedSec);
            if (!bar.IsIndeterminate && bar.Progress >= 10 && bar.Progress < 100 && elapsedSec >= 5)
            {
                double progEta = elapsedSec * (100.0 - bar.Progress) / (double)bar.Progress;
                double wProg = Math.Min(0.75, bar.Progress / 100.0);
                return (int)Math.Max(0, Math.Round(wProg * progEta + (1.0 - wProg) * benchRem));
            }

            if (elapsedSec < expected)
            {
                return (int)Math.Round(expected - elapsedSec);
            }
            return -2;
        }

        public void UpdateLiveTimersAndEta()
        {
            try
            {
                GlobalElapsedText = FormatCompactDuration(_elapsedDuration.TotalSeconds);

                string matchedModel;
                GetModelScaleFactor(out matchedModel);
                string baseSource = (_benchmarkBaseline != null && !string.IsNullOrEmpty(_benchmarkBaseline.SourceLabel))
                    ? _benchmarkBaseline.SourceLabel
                    : "Podrazumevani WIS v3 benchmark";
                BenchmarkSourceTooltip = !string.IsNullOrEmpty(matchedModel)
                    ? string.Format("Procena ETA: {0} • model {1}", baseSource, matchedModel)
                    : string.Format("Procena ETA: {0}", baseSource);

                DateTime nowUtc = DateTime.UtcNow;
                double runningStepsRemSum = 0.0;

                if (ActiveStepBars != null)
                {
                    for (int i = 0; i < ActiveStepBars.Count; i++)
                    {
                        ActiveStepProgressItem bar = ActiveStepBars[i];
                        if (bar == null) continue;
                        string barKey = bar.Id ?? ("bar_" + i);

                        if (string.Equals(barKey, "done", StringComparison.OrdinalIgnoreCase))
                        {
                            bar.ElapsedText = GlobalElapsedText;
                            bar.EtaText = "00:00";
                            bar.TimingTooltip = BenchmarkSourceTooltip;
                            continue;
                        }
                        if (string.Equals(barKey, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            bar.ElapsedText = "00:00";
                            bar.EtaText = "--:--";
                            bar.TimingTooltip = BenchmarkSourceTooltip;
                            continue;
                        }

                        DateTime startUtc;
                        if (!_activeBarStartUtc.TryGetValue(barKey, out startUtc))
                        {
                            startUtc = nowUtc;
                            _activeBarStartUtc[barKey] = startUtc;
                        }

                        int elapsedSec = Math.Max(0, (int)(nowUtc - startUtc).TotalSeconds);
                        int etaSec = ComputeStepEtaSeconds(bar, elapsedSec);

                        bar.ElapsedText = FormatCompactDuration(elapsedSec);
                        if (etaSec == -1) bar.EtaText = "--:--";
                        else if (etaSec == -2) bar.EtaText = "< 00:30";
                        else if (etaSec == 0) bar.EtaText = "00:00";
                        else bar.EtaText = "~" + FormatCompactDuration(etaSec);
                        bar.TimingTooltip = BenchmarkSourceTooltip;

                        bool isBgParallel = string.Equals(barKey, "bg_wu", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(barKey, "drv_batch", StringComparison.OrdinalIgnoreCase) ||
                                            barKey.StartsWith("drv_", StringComparison.OrdinalIgnoreCase);
                        if (!isBgParallel && etaSec > 0)
                        {
                            runningStepsRemSum += etaSec;
                        }
                    }
                }

                if (_isServiceFinished || _isCompleted)
                {
                    GlobalEtaText = "00:00";
                }
                else
                {
                    double waitRemSum = 0.0;
                    double totalBenchmarkSum = 0.0;
                    bool hasRunningPhaseSteps = false;
                    if (Phases != null)
                    {
                        for (int i = 0; i < Phases.Count; i++)
                        {
                            PhaseItem ph = Phases[i];
                            if (ph == null || ph.Steps == null) continue;
                            for (int j = 0; j < ph.Steps.Count; j++)
                            {
                                StepItem st = ph.Steps[j];
                                if (st == null) continue;
                                double expSt = EstimateStepExpectedSeconds(st.Id, ph.Number);
                                totalBenchmarkSum += expSt;
                                if (st.IsRunning)
                                {
                                    hasRunningPhaseSteps = true;
                                }
                                else if (!st.IsCompleted && !st.IsSkipped && !st.IsWarning && !st.IsError)
                                {
                                    waitRemSum += expSt;
                                }
                            }
                        }
                    }

                    double globalRemSec = hasRunningPhaseSteps
                        ? (runningStepsRemSum + waitRemSum)
                        : (totalBenchmarkSum > 0.0
                            ? totalBenchmarkSum * (Math.Max(0, 100 - _globalProgress) / 100.0)
                            : waitRemSum);

                    if (globalRemSec <= 0.0 && _globalProgress < 100)
                    {
                        GlobalEtaText = "< 00:30";
                    }
                    else
                    {
                        GlobalEtaText = "~" + FormatCompactDuration(globalRemSec);
                    }
                }
            }
            catch { }
        }

        public bool UsbFree { get { return _usbFree; } set { SetProperty(ref _usbFree, value); } }
        public string UsbBannerText { get { return _usbBannerText; } set { SetProperty(ref _usbBannerText, value); } }

        // Pozadinski Windows Update indikator (pokreće se odmah nakon Koraka 2)
        private bool _isBackgroundWuVisible = false;
        private string _backgroundWuStatus = "IDLE";
        private string _backgroundWuTextSr = "Pozadinski Windows Update: Pokretanje nakon Koraka 2...";
        private string _backgroundWuTextZh = "后台 Windows 更新: 阶段 2 后正在启动...";
        private int _backgroundWuProgress = -1;

        public bool IsBackgroundWuVisible
        {
            get { return _isBackgroundWuVisible; }
            set { SetProperty(ref _isBackgroundWuVisible, value); }
        }

        public string BackgroundWuStatus
        {
            get { return _backgroundWuStatus; }
            set
            {
                if (SetProperty(ref _backgroundWuStatus, (value ?? "IDLE").ToUpperInvariant().Trim()))
                {
                    OnPropertyChanged("BackgroundWuBadgeText");
                    OnPropertyChanged("BackgroundWuBadgeBrush");
                    OnPropertyChanged("BackgroundWuBorderBrush");
                }
            }
        }

        public string BackgroundWuDisplayText
        {
            get
            {
                return LocalizationService.Instance.IsChinese
                    ? (!string.IsNullOrEmpty(_backgroundWuTextZh) ? _backgroundWuTextZh : _backgroundWuTextSr)
                    : _backgroundWuTextSr;
            }
        }

        public string BackgroundWuBadgeText
        {
            get
            {
                switch (_backgroundWuStatus)
                {
                    case "COMPLETED":
                        return LocalizationService.Get("MdlBgWuCompleted", "✓ ZAVRŠENO");
                    case "SKIPPED":
                        return LocalizationService.Get("MdlBgWuSkipped", "⏭ PRESKOČENO");
                    case "WAITING_MSI":
                        return LocalizationService.Get("MdlBgWuWaitingMsi", "⏳ ČEKA MSI");
                    case "DOWNLOADING":
                        return LocalizationService.Get("MdlBgWuDownloading", "⬇ PREUZIMANJE");
                    case "INSTALLING":
                        return LocalizationService.Get("MdlBgWuInstalling", "⚙ INSTALACIJA");
                    case "HARVESTING":
                        return LocalizationService.Get("MdlBgWuHarvesting", "⬆ NAS KEŠ");
                    case "WARN":
                        return LocalizationService.Get("MdlBgWuWarn", "⚠ UPOZORENJE");
                    case "STARTING":
                    case "SEARCHING":
                    default:
                        return LocalizationService.Get("MdlBgWuInProgress", "🔄 U TOKU");
                }
            }
        }

        public Brush BackgroundWuBadgeBrush
        {
            get
            {
                switch (_backgroundWuStatus)
                {
                    case "COMPLETED":
                        return CreateFrozenBrush(39, 174, 96);   // Zelena #27AE60
                    case "SKIPPED":
                        return CreateFrozenBrush(52, 73, 94);    // Sivo-plava #34495E
                    case "WAITING_MSI":
                    case "WARN":
                        return CreateFrozenBrush(243, 156, 18);  // Narandžasta #F39C12
                    case "DOWNLOADING":
                    case "INSTALLING":
                    case "HARVESTING":
                    case "SEARCHING":
                    case "STARTING":
                    default:
                        return CreateFrozenBrush(0, 122, 204);   // Plava #007ACC
                }
            }
        }

        public Brush BackgroundWuBorderBrush
        {
            get
            {
                switch (_backgroundWuStatus)
                {
                    case "COMPLETED":
                        return CreateFrozenBrush(46, 204, 113);  // #2ECC71
                    case "WAITING_MSI":
                    case "WARN":
                        return CreateFrozenBrush(241, 196, 15);  // #F1C40F
                    default:
                        return CreateFrozenBrush(78, 201, 176);  // #4EC9B0
                }
            }
        }

        public void UpdateBackgroundWuStatus(string status, string textSr, string textZh)
        {
            UpdateBackgroundWuStatus(status, textSr, textZh, -1);
        }

        public void UpdateBackgroundWuStatus(string status, string textSr, string textZh, int progressPercent)
        {
            Action upd = delegate
            {
                IsBackgroundWuVisible = true;
                BackgroundWuStatus = string.IsNullOrEmpty(status) ? "SEARCHING" : status;
                _backgroundWuProgress = progressPercent;
                if (!string.IsNullOrEmpty(textSr)) _backgroundWuTextSr = textSr;
                if (!string.IsNullOrEmpty(textZh)) _backgroundWuTextZh = textZh;
                else if (!string.IsNullOrEmpty(textSr)) _backgroundWuTextZh = textSr;
                OnPropertyChanged("BackgroundWuDisplayText");
                OnPropertyChanged("BackgroundWuBadgeText");
                OnPropertyChanged("BackgroundWuBadgeBrush");
                OnPropertyChanged("BackgroundWuBorderBrush");
                RefreshProgressBars();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(upd);
            }
            else
            {
                upd();
            }
        }

        // ------------------ TAB 2: DIJAGNOSTIKA ------------------
        private int _diskWear = 100;
        private string _diskWearText = LocalizationService.Get("MdlDiskWearInit", "100% Zdravlje");
        private string _diskTbw = "N/A";
        private long _diskPowerOnHours = -1;
        private string _smartStatus = LocalizationService.Get("MdlSmartHealthyInit", "Healthy (Ispravan)");
        private int _reallocatedSectors = 0;
        private int _criticalWarnings = 0;
        private Brush _smartStatusBrush = CreateFrozenBrush(46, 204, 113);

        private int _batteryHealth = 0;
        private string _batteryHealthText = LocalizationService.Get("MdlBatteryHealthInit", "Fiksno napajanje (Desktop)");
        private string _batteryCycles = "N/A";
        private string _batteryStatus = LocalizationService.Get("MdlBatteryStatusInit", "AC Napajanje / Desktop");
        private bool _isLaptop = false;
        private Brush _batteryHealthBrush = CreateFrozenBrush(100, 100, 100);

        private string _cpuDetail = LocalizationService.Get("MdlDetectingCpu", "Detekcija CPU-a...");
        private string _cpuUsage = LocalizationService.Get("MdlReading", "Očitavanje...");
        private string _ramDetail = LocalizationService.Get("MdlDetectingRam", "Detekcija RAM-a...");
        private string _ramUsage = LocalizationService.Get("MdlReading", "Očitavanje...");

        public int DiskWear
        {
            get { return _diskWear; }
            set
            {
                SetProperty(ref _diskWear, value);
                DiskWearText = string.Format(LocalizationService.Get("MdlDiskWearRemaining", "{0}% Preostalo zdravlje"), value);
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
                BatteryHealthText = string.Format(LocalizationService.Get("MdlBatteryFactoryCap", "{0}% Fabrički kapacitet"), value);
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
                        UpdateAppStatus("BatteryBar", "SKIPPED", LocalizationService.Get("MdlAppNotApplicableDesktop", "Nije primenjivo na desktopu"));
                    }
                }
            }
        }
        public string DeviceTypeText
        {
            get
            {
                return IsLaptop
                    ? LocalizationService.Get("MdlDeviceTypeLaptop", "Laptop računar")
                    : LocalizationService.Get("MdlDeviceTypeDesktop", "Desktop (Fiksno napajanje)");
            }
        }
        public Brush BatteryHealthBrush { get { return _batteryHealthBrush; } set { SetProperty(ref _batteryHealthBrush, value); } }

        public string CpuDetail { get { return _cpuDetail; } set { SetProperty(ref _cpuDetail, value); } }
        public string CpuUsage { get { return _cpuUsage; } set { SetProperty(ref _cpuUsage, value); } }
        public string RamDetail { get { return _ramDetail; } set { SetProperty(ref _ramDetail, value); } }
        public string RamUsage { get { return _ramUsage; } set { SetProperty(ref _ramUsage, value); } }

        // Pregled — Hardver monitor (CPU/RAM opterećenje, kratke temperature, baterija/punjač uživo)
        private double _cpuUsagePercent = 0.0;
        private string _cpuUsageShortText = "0%";
        private Brush _cpuUsageBrush = CreateFrozenBrush(78, 201, 176);
        private double _ramUsagePercent = 0.0;
        private string _ramUsageShortText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _ramUsageBrush = CreateFrozenBrush(78, 201, 176);

        private string _cpuTempShortText = "N/A";
        private string _gpuTempShortText = "N/A";
        private string _ssdTempShortText = "N/A";
        private string _mbTempShortText = "N/A";

        private bool _hasBatteryLive = false;
        private bool _isOnAcPower = true;
        private bool _isBatteryCharging = false;
        private bool _isBatteryDischarging = false;
        private int _batteryChargePercent = 0;
        private double _batteryRateWatts = 0.0;
        private double _batteryCurrentAmps = 0.0;
        private string _powerStatusTitle = LocalizationService.Get("MdlPwrReadingTitle", "Očitavanje napajanja...");
        private string _powerDrawText = "—";
        private string _powerMonitorTooltip = LocalizationService.Get("MdlPwrReadingTooltip", "Očitavanje stanja baterije i punjača...");
        private bool _hasPowerSample = false;
        private double _lastPowerVoltageV = 0.0;
        private int _lastPowerLifeRemainingSec = 0;
        private Brush _powerStatusBrush = CreateFrozenBrush(204, 204, 204);
        private Brush _powerDrawBrush = CreateFrozenBrush(78, 201, 176);
        private Brush _batteryLevelBrush = CreateFrozenBrush(46, 204, 113);

        public double CpuUsagePercent { get { return _cpuUsagePercent; } set { SetProperty(ref _cpuUsagePercent, value); } }
        public string CpuUsageShortText { get { return _cpuUsageShortText; } set { SetProperty(ref _cpuUsageShortText, value); } }
        public Brush CpuUsageBrush { get { return _cpuUsageBrush; } set { SetProperty(ref _cpuUsageBrush, value); } }

        public double RamUsagePercent { get { return _ramUsagePercent; } set { SetProperty(ref _ramUsagePercent, value); } }
        public string RamUsageShortText { get { return _ramUsageShortText; } set { SetProperty(ref _ramUsageShortText, value); } }
        public Brush RamUsageBrush { get { return _ramUsageBrush; } set { SetProperty(ref _ramUsageBrush, value); } }

        public string CpuTempShortText { get { return _cpuTempShortText; } set { SetProperty(ref _cpuTempShortText, value); } }
        public string GpuTempShortText { get { return _gpuTempShortText; } set { SetProperty(ref _gpuTempShortText, value); } }
        public string SsdTempShortText { get { return _ssdTempShortText; } set { SetProperty(ref _ssdTempShortText, value); } }
        public string MbTempShortText { get { return _mbTempShortText; } set { SetProperty(ref _mbTempShortText, value); } }

        public bool HasBatteryLive { get { return _hasBatteryLive; } set { SetProperty(ref _hasBatteryLive, value); } }
        public bool IsOnAcPower { get { return _isOnAcPower; } set { SetProperty(ref _isOnAcPower, value); } }
        public bool IsBatteryCharging { get { return _isBatteryCharging; } set { SetProperty(ref _isBatteryCharging, value); } }
        public bool IsBatteryDischarging { get { return _isBatteryDischarging; } set { SetProperty(ref _isBatteryDischarging, value); } }
        public int BatteryChargePercent { get { return _batteryChargePercent; } set { SetProperty(ref _batteryChargePercent, value); } }
        public double BatteryRateWatts { get { return _batteryRateWatts; } set { SetProperty(ref _batteryRateWatts, value); } }
        public double BatteryCurrentAmps { get { return _batteryCurrentAmps; } set { SetProperty(ref _batteryCurrentAmps, value); } }
        public string PowerStatusTitle { get { return _powerStatusTitle; } set { SetProperty(ref _powerStatusTitle, value); } }
        public string PowerDrawText { get { return _powerDrawText; } set { SetProperty(ref _powerDrawText, value); } }
        public string PowerMonitorTooltip { get { return _powerMonitorTooltip; } set { SetProperty(ref _powerMonitorTooltip, value); } }
        public Brush PowerStatusBrush { get { return _powerStatusBrush; } set { SetProperty(ref _powerStatusBrush, value); } }
        public Brush PowerDrawBrush { get { return _powerDrawBrush; } set { SetProperty(ref _powerDrawBrush, value); } }
        public Brush BatteryLevelBrush { get { return _batteryLevelBrush; } set { SetProperty(ref _batteryLevelBrush, value); } }

        public void UpdateCpuRamMonitor(int cpuPct, double ramUsedGb, double ramTotalGb, int ramPct)
        {
            if (cpuPct < 0) cpuPct = 0;
            if (cpuPct > 100) cpuPct = 100;
            if (ramPct < 0) ramPct = 0;
            if (ramPct > 100) ramPct = 100;

            CpuUsagePercent = cpuPct;
            CpuUsageShortText = string.Format("{0}%", cpuPct);
            CpuUsageBrush = cpuPct >= 85
                ? CreateFrozenBrush(231, 76, 60)
                : (cpuPct >= 65 ? CreateFrozenBrush(241, 196, 15) : CreateFrozenBrush(78, 201, 176));

            RamUsagePercent = ramPct;
            RamUsageShortText = ramTotalGb > 0.1
                ? string.Format("{0:0.0} / {1:0.0} GB ({2}%)", ramUsedGb, ramTotalGb, ramPct)
                : string.Format("{0}%", ramPct);
            RamUsageBrush = ramPct >= 90
                ? CreateFrozenBrush(231, 76, 60)
                : (ramPct >= 75 ? CreateFrozenBrush(241, 196, 15) : CreateFrozenBrush(78, 201, 176));
        }

        public void UpdatePowerMonitor(
            bool hasBattery,
            bool isOnAc,
            bool isCharging,
            bool isDischarging,
            int chargePercent,
            double watts,
            double amps,
            double voltageV,
            int lifeRemainingSec)
        {
            if (chargePercent < 0) chargePercent = 0;
            if (chargePercent > 100) chargePercent = 100;
            if (watts < 0.0) watts = 0.0;
            if (amps < 0.0) amps = 0.0;

            HasBatteryLive = hasBattery;
            IsOnAcPower = isOnAc;
            IsBatteryCharging = isCharging;
            IsBatteryDischarging = isDischarging;
            BatteryChargePercent = chargePercent;
            BatteryRateWatts = watts;
            BatteryCurrentAmps = amps;
            _hasPowerSample = true;
            _lastPowerVoltageV = voltageV;
            _lastPowerLifeRemainingSec = lifeRemainingSec;

            if (!hasBattery)
            {
                PowerStatusTitle = LocalizationService.Get("MdlPwrFixedAc", "Fiksno AC napajanje");
                PowerDrawText = LocalizationService.Get("MdlPwrDesktopAc", "Desktop (AC)");
                PowerStatusBrush = CreateFrozenBrush(180, 180, 180);
                PowerDrawBrush = CreateFrozenBrush(78, 201, 176);
                BatteryLevelBrush = CreateFrozenBrush(100, 100, 100);
                PowerMonitorTooltip = LocalizationService.Get("MdlPwrDesktopTooltip", "Desktop računar bez sistemske baterije (stalno AC napajanje).");
                return;
            }

            BatteryLevelBrush = chargePercent <= 20
                ? CreateFrozenBrush(231, 76, 60)
                : (chargePercent <= 45 ? CreateFrozenBrush(243, 156, 18) : CreateFrozenBrush(46, 204, 113));

            string remText = "";
            if (!isOnAc && lifeRemainingSec > 60 && lifeRemainingSec < 86400 * 2)
            {
                int remMins = lifeRemainingSec / 60;
                int remHours = remMins / 60;
                int remMinPart = remMins % 60;
                remText = remHours > 0
                    ? string.Format("~{0}h {1:00}m", remHours, remMinPart)
                    : string.Format("~{0}m", remMinPart);
            }

            if (isOnAc)
            {
                PowerStatusTitle = string.Format(LocalizationService.Get("MdlPwrChargerConnected", "Punjač priključen • {0}%"), chargePercent);
                PowerStatusBrush = CreateFrozenBrush(78, 201, 176);
                if (isDischarging && watts >= 1.0)
                {
                    PowerDrawText = amps > 0.05
                        ? string.Format("-{0:0.0} W ({1:0.00} A)", watts, amps)
                        : string.Format("-{0:0.0} W", watts);
                    PowerDrawBrush = CreateFrozenBrush(231, 76, 60);
                }
                else if (watts >= 0.5)
                {
                    PowerDrawText = amps > 0.05
                        ? string.Format("+{0:0.0} W ({1:0.00} A)", watts, amps)
                        : string.Format("+{0:0.0} W", watts);
                    PowerDrawBrush = CreateFrozenBrush(46, 204, 113);
                }
                else if (chargePercent >= 98)
                {
                    PowerDrawText = LocalizationService.Get("MdlPwrFullAc", "Puna (AC)");
                    PowerDrawBrush = CreateFrozenBrush(46, 204, 113);
                }
                else
                {
                    PowerDrawText = isCharging
                        ? LocalizationService.Get("MdlPwrChargingAc", "Punjenje (AC)")
                        : LocalizationService.Get("MdlPwrAc", "AC napajanje");
                    PowerDrawBrush = CreateFrozenBrush(78, 201, 176);
                }
            }
            else
            {
                PowerStatusTitle = string.Format(LocalizationService.Get("MdlPwrOnBattery", "Na bateriji • {0}%"), chargePercent);
                PowerStatusBrush = chargePercent <= 20
                    ? CreateFrozenBrush(231, 76, 60)
                    : CreateFrozenBrush(243, 156, 18);

                if (watts >= 0.5)
                {
                    if (!string.IsNullOrEmpty(remText))
                    {
                        PowerDrawText = string.Format("-{0:0.0} W • {1}", watts, remText);
                    }
                    else if (amps > 0.05)
                    {
                        PowerDrawText = string.Format("-{0:0.0} W ({1:0.00} A)", watts, amps);
                    }
                    else
                    {
                        PowerDrawText = string.Format("-{0:0.0} W", watts);
                    }
                }
                else if (!string.IsNullOrEmpty(remText))
                {
                    PowerDrawText = remText;
                }
                else
                {
                    PowerDrawText = LocalizationService.Get("MdlPwrDischargingDc", "Pražnjenje (DC)");
                }

                PowerDrawBrush = chargePercent <= 20
                    ? CreateFrozenBrush(231, 76, 60)
                    : CreateFrozenBrush(243, 156, 18);
            }

            var tt = new System.Text.StringBuilder();
            tt.Append(PowerStatusTitle);
            if (watts >= 0.5)
            {
                tt.AppendFormat(LocalizationService.Get("MdlPwrTtPower", " | Snaga: {0:0.0} W"), watts);
            }
            if (amps > 0.05)
            {
                tt.AppendFormat(LocalizationService.Get("MdlPwrTtCurrent", " | Struja: {0:0.00} A"), amps);
            }
            if (voltageV > 1.0)
            {
                tt.AppendFormat(LocalizationService.Get("MdlPwrTtVoltage", " | Napon baterije: {0:0.00} V"), voltageV);
            }
            if (!string.IsNullOrEmpty(remText))
            {
                tt.AppendFormat(LocalizationService.Get("MdlPwrTtRemaining", " | Preostalo: {0}"), remText);
            }
            PowerMonitorTooltip = tt.ToString();
        }

        // Grafička karta & Ekran
        private string _gpuName = LocalizationService.Get("MdlDetectingGpu", "Detekcija GPU-a...");
        private string _gpuVram = "N/A";
        private string _gpuDriverVersion = "N/A";
        private string _displayResolution = LocalizationService.Get("MdlHwDetectingDisplay", "Detekcija displeja...");
        private string _displayRefreshRate = "60 Hz";
        private string _displayDpiScale = "100%";

        public string GpuName { get { return _gpuName; } set { SetProperty(ref _gpuName, value); } }
        public string GpuVram { get { return _gpuVram; } set { SetProperty(ref _gpuVram, value); } }
        public string GpuDriverVersion { get { return _gpuDriverVersion; } set { SetProperty(ref _gpuDriverVersion, value); } }
        public string DisplayResolution { get { return _displayResolution; } set { SetProperty(ref _displayResolution, value); } }
        public string DisplayRefreshRate { get { return _displayRefreshRate; } set { SetProperty(ref _displayRefreshRate, value); } }
        public string DisplayDpiScale { get { return _displayDpiScale; } set { SetProperty(ref _displayDpiScale, value); } }

        // Matična ploča & BIOS & TPM
        private string _motherboardModel = LocalizationService.Get("MdlDetectingMb", "Detekcija matične ploče...");
        private string _biosVersion = LocalizationService.Get("MdlDetectingBios", "Detekcija BIOS-a...");
        private string _biosReleaseDate = "N/A";
        private string _secureBootStatus = LocalizationService.Get("MdlUnknown", "Nepoznato");
        private string _tpmStatusSummary = LocalizationService.Get("MdlCheckingTpm", "Provera TPM čipa...");

        public string MotherboardModel { get { return _motherboardModel; } set { SetProperty(ref _motherboardModel, value); } }
        public string BiosVersion { get { return _biosVersion; } set { SetProperty(ref _biosVersion, value); } }
        public string BiosReleaseDate { get { return _biosReleaseDate; } set { SetProperty(ref _biosReleaseDate, value); } }
        public string SecureBootStatus { get { return _secureBootStatus; } set { SetProperty(ref _secureBootStatus, value); } }
        public string TpmStatusSummary { get { return _tpmStatusSummary; } set { SetProperty(ref _tpmStatusSummary, value); } }

        // PnP Problematic Devices
        public ObservableCollection<PnpProblemDevice> PnpProblemDevices { get; private set; }
        private bool _hasPnpProblems = false;
        private string _pnpSummaryText = LocalizationService.Get("MdlPnpAllOkInit", "✓ Svi sistemski drajveri su ispravno instalirani");
        private Brush _pnpStatusBrush = CreateFrozenBrush(46, 204, 113);

        public bool HasPnpProblems { get { return _hasPnpProblems; } set { SetProperty(ref _hasPnpProblems, value); } }
        public string PnpSummaryText { get { return _pnpSummaryText; } set { SetProperty(ref _pnpSummaryText, value); } }
        public Brush PnpStatusBrush { get { return _pnpStatusBrush; } set { SetProperty(ref _pnpStatusBrush, value); } }

        public void UpdatePnpStatus()
        {
            if (PnpProblemDevices.Count > 0)
            {
                HasPnpProblems = true;
                PnpSummaryText = string.Format(LocalizationService.Get("MdlPnpAttentionCount", "⚠ {0} uređaja zahteva pažnju"), PnpProblemDevices.Count);
                PnpStatusBrush = CreateFrozenBrush(241, 196, 15);
            }
            else
            {
                HasPnpProblems = false;
                PnpSummaryText = LocalizationService.Get("MdlPnpAllOk", "✓ Svi sistemski drajveri su ispravno instalirani (0 problema)");
                PnpStatusBrush = CreateFrozenBrush(46, 204, 113);
            }
            RebuildOverviewAlerts();
        }

        // Termalna telemetrija, 5-minutni grafikon i taktovi jezgara (v2.15.0 - WIS-TASK-131)
        private float _cpuTemp = 0.0f;
        private string _cpuTempText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _cpuTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _gpuTemp = 0.0f;
        private string _gpuTempText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _gpuTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _mbTemp = 0.0f;
        private string _mbTempText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _mbTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _ssdTemp = 0.0f;
        private string _ssdTempText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _ssdTempBrush = CreateFrozenBrush(78, 201, 176);

        private float _throttlingPercent = 0.0f;
        private string _throttlingText = LocalizationService.Get("MdlNotAvailable", "Nije dostupno");
        private Brush _throttlingBrush = CreateFrozenBrush(140, 140, 140);

        private int _fanRpm = 0;
        private string _fanSpeedText = LocalizationService.Get("MdlReading", "Očitavanje...");
        private Brush _fanSpeedBrush = CreateFrozenBrush(78, 201, 176);

        private string _thermalSensorStatus = LocalizationService.Get("MdlSensorsActive", "● Senzori aktivni");
        private Brush _thermalSensorStatusBrush = CreateFrozenBrush(78, 201, 176);

        private PointCollection _cpuGraphPoints = new PointCollection();
        private PointCollection _gpuGraphPoints = new PointCollection();
        private PointCollection _mbGraphPoints = new PointCollection();
        private PointCollection _ssdGraphPoints = new PointCollection();
        private PointCollection _throttleGraphPoints = new PointCollection();
        private PointCollection _cpuLoadGraphPoints = new PointCollection();

        private string _legendCpuText = "● CPU: N/A";
        private string _legendGpuText = "● GPU: N/A";
        private string _legendMbText = "● MB: N/A";
        private string _legendSsdText = "● SSD: N/A";
        private string _legendThrottleText = LocalizationService.Get("MdlLegendThrottleNa", "● Throttling: Nije dostupno");
        private string _legendCpuLoadText = "● CPU Load: 0%";

        private string _hwCpuAvgClockText = "—";
        private string _hwCpuKpiSubText = LocalizationService.Get("MdlKpiTempReading", "Temp: Očitavanje...");
        private string _hwRamAvailableText = LocalizationService.Get("MdlKpiRamFreeNa", "Slobodno: N/A");
        private string _hwRamCommitText = "Commit: N/A";
        private string _hwGpuKpiSubText = "VRAM / Senzor: N/A";
        private string _hwDiskIoText = LocalizationService.Get("MdlKpiDiskIoReading", "R/W: Očitavanje...");
        private double _hwDiskActivePercent = 0.0;
        private string _hwPowerVoltageText = LocalizationService.Get("MdlKpiPowerVoltageReading", "Napon: Očitavanje...");
        private string _hwPowerSubText = LocalizationService.Get("MdlKpiPowerSubInit", "Zdravlje baterije");
        private string _hwSystemStatsText = "Procesi: -- • Uptime: 00:00:00";
        private string _hwNetRateText = "RX 0.0 MB/s • TX 0.0 MB/s";

        private ICommand _resetCoreClocksCommand;

        public ObservableCollection<CoreClockItem> CoreClocks { get; private set; }
        public ObservableCollection<HwMonitorSensorRow> HwMonitorSensors { get; private set; }

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
        public PointCollection CpuLoadGraphPoints { get { return _cpuLoadGraphPoints; } set { SetProperty(ref _cpuLoadGraphPoints, value); } }

        public string LegendCpuText { get { return _legendCpuText; } set { SetProperty(ref _legendCpuText, value); } }
        public string LegendGpuText { get { return _legendGpuText; } set { SetProperty(ref _legendGpuText, value); } }
        public string LegendMbText { get { return _legendMbText; } set { SetProperty(ref _legendMbText, value); } }
        public string LegendSsdText { get { return _legendSsdText; } set { SetProperty(ref _legendSsdText, value); } }
        public string LegendThrottleText { get { return _legendThrottleText; } set { SetProperty(ref _legendThrottleText, value); } }
        public string LegendCpuLoadText { get { return _legendCpuLoadText; } set { SetProperty(ref _legendCpuLoadText, value); } }

        public string HwCpuAvgClockText { get { return _hwCpuAvgClockText; } set { SetProperty(ref _hwCpuAvgClockText, value); } }
        public string HwCpuKpiSubText { get { return _hwCpuKpiSubText; } set { SetProperty(ref _hwCpuKpiSubText, value); } }
        public string HwRamAvailableText { get { return _hwRamAvailableText; } set { SetProperty(ref _hwRamAvailableText, value); } }
        public string HwRamCommitText { get { return _hwRamCommitText; } set { SetProperty(ref _hwRamCommitText, value); } }
        public string HwGpuKpiSubText { get { return _hwGpuKpiSubText; } set { SetProperty(ref _hwGpuKpiSubText, value); } }
        public string HwDiskIoText { get { return _hwDiskIoText; } set { SetProperty(ref _hwDiskIoText, value); } }
        public double HwDiskActivePercent { get { return _hwDiskActivePercent; } set { SetProperty(ref _hwDiskActivePercent, Math.Max(0.0, Math.Min(100.0, value))); } }
        public string HwPowerVoltageText { get { return _hwPowerVoltageText; } set { SetProperty(ref _hwPowerVoltageText, value); } }
        public string HwPowerSubText { get { return _hwPowerSubText; } set { SetProperty(ref _hwPowerSubText, value); } }
        public string HwSystemStatsText { get { return _hwSystemStatsText; } set { SetProperty(ref _hwSystemStatsText, value); } }
        public string HwNetRateText { get { return _hwNetRateText; } set { SetProperty(ref _hwNetRateText, value); } }

        private static string GetHwSensorLabel(string key)
        {
            switch (key)
            {
                case "hdr_temp": return LocalizationService.Get("MdlSensorHdrTemp", "TEMPERATURA");
                case "temp_cpu": return LocalizationService.Get("MdlSensorTempCpu", "CPU Package / ACPI");
                case "temp_gpu": return LocalizationService.Get("MdlSensorTempGpu", "GPU Jezgro");
                case "temp_mb": return LocalizationService.Get("MdlSensorTempMb", "Matična ploča (MB)");
                case "temp_ssd": return LocalizationService.Get("MdlSensorTempSsd", "NVMe / SSD Disk");
                case "hdr_load": return LocalizationService.Get("MdlSensorHdrLoad", "OPTEREĆENJE I STABILNOST");
                case "load_cpu": return LocalizationService.Get("MdlSensorLoadCpu", "CPU Ukupno opterećenje");
                case "load_ram": return LocalizationService.Get("MdlSensorLoadRam", "RAM Zauzeće memorije");
                case "load_throttle": return LocalizationService.Get("MdlSensorLoadThrottle", "CPU Thermal Throttling");
                case "io_disk": return LocalizationService.Get("MdlSensorIoDisk", "Disk I/O aktivnost");
                case "io_net": return LocalizationService.Get("MdlSensorIoNet", "Mrežni protok (RX + TX)");
                case "hdr_power": return LocalizationService.Get("MdlSensorHdrPower", "HLAĐENJE I NAPAJANJE");
                case "fan_rpm": return LocalizationService.Get("MdlSensorFanRpm", "Ventilator (Fan RPM)");
                case "pwr_watts": return LocalizationService.Get("MdlSensorPwrWatts", "Snaga baterije / punjača");
                case "pwr_amps": return LocalizationService.Get("MdlSensorPwrAmps", "Struja (A)");
                case "pwr_volts": return LocalizationService.Get("MdlSensorPwrVolts", "Napon baterije (V)");
                default: return key;
            }
        }

        private void InitializeHwMonitorSensors()
        {
            if (HwMonitorSensors == null) return;
            HwMonitorSensors.Clear();
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateHeader("hdr_temp", GetHwSensorLabel("hdr_temp")));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("temp_cpu", GetHwSensorLabel("temp_cpu"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("temp_gpu", GetHwSensorLabel("temp_gpu"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("temp_mb", GetHwSensorLabel("temp_mb"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("temp_ssd", GetHwSensorLabel("temp_ssd"), "N/A"));

            HwMonitorSensors.Add(HwMonitorSensorRow.CreateHeader("hdr_load", GetHwSensorLabel("hdr_load")));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("load_cpu", GetHwSensorLabel("load_cpu"), "0 %"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("load_ram", GetHwSensorLabel("load_ram"), "0 %"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("load_throttle", GetHwSensorLabel("load_throttle"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("io_disk", GetHwSensorLabel("io_disk"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("io_net", GetHwSensorLabel("io_net"), "0.0 MB/s"));

            HwMonitorSensors.Add(HwMonitorSensorRow.CreateHeader("hdr_power", GetHwSensorLabel("hdr_power")));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("fan_rpm", GetHwSensorLabel("fan_rpm"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("pwr_watts", GetHwSensorLabel("pwr_watts"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("pwr_amps", GetHwSensorLabel("pwr_amps"), "N/A"));
            HwMonitorSensors.Add(HwMonitorSensorRow.CreateSensor("pwr_volts", GetHwSensorLabel("pwr_volts"), "N/A"));
        }

        public void RefreshHwMonitorSensorLabels()
        {
            if (HwMonitorSensors == null) return;
            for (int i = 0; i < HwMonitorSensors.Count; i++)
            {
                HwMonitorSensorRow row = HwMonitorSensors[i];
                if (row != null && !string.IsNullOrEmpty(row.Key))
                {
                    row.Name = GetHwSensorLabel(row.Key);
                }
            }
        }

        private HwMonitorSensorRow FindHwSensorRow(string key)
        {
            if (HwMonitorSensors == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < HwMonitorSensors.Count; i++)
            {
                if (string.Equals(HwMonitorSensors[i].Key, key, StringComparison.Ordinal))
                {
                    return HwMonitorSensors[i];
                }
            }
            return null;
        }

        public void UpdateHwMonitorSensor(string key, double value, string format, Brush valueBrush)
        {
            HwMonitorSensorRow row = FindHwSensorRow(key);
            if (row != null)
            {
                row.UpdateSample(value, format, valueBrush);
            }
        }

        public void SetHwMonitorSensorUnavailable(string key, string label)
        {
            HwMonitorSensorRow row = FindHwSensorRow(key);
            if (row != null)
            {
                row.SetUnavailable(label);
            }
        }

        public void ResetAllHwMonitorExtremes()
        {
            if (HwMonitorSensors == null) return;
            for (int i = 0; i < HwMonitorSensors.Count; i++)
            {
                HwMonitorSensors[i].ResetExtremes();
            }
        }

        public void RefreshHwMonitorKpiSummaries()
        {
            HwMonitorSensorRow cpuRow = FindHwSensorRow("temp_cpu");
            if (cpuRow != null && cpuRow.HasValue)
            {
                HwCpuKpiSubText = string.Format(LocalizationService.Get("MdlKpiTempMinMax", "Temp {0:0}°C • Min {1:0}° / Max {2:0}°"), cpuRow.CurrentNumeric, cpuRow.MinNumeric, cpuRow.MaxNumeric);
            }
            else
            {
                HwCpuKpiSubText = "Temp: " + CpuTempShortText;
            }

            HwMonitorSensorRow gpuRow = FindHwSensorRow("temp_gpu");
            string vramLabel = (HardwareInfo != null && !string.IsNullOrWhiteSpace(HardwareInfo.GpuPrimaryVram))
                ? HardwareInfo.GpuPrimaryVram
                : (!string.IsNullOrWhiteSpace(GpuVram) ? GpuVram : "Shared");
            if (gpuRow != null && gpuRow.HasValue)
            {
                HwGpuKpiSubText = string.Format("VRAM {0} • Max {1:0}°C", vramLabel, gpuRow.MaxNumeric);
            }
            else
            {
                HwGpuKpiSubText = string.Format("VRAM {0} • Driver {1}", vramLabel, GpuDriverVersion);
            }

            HwPowerSubText = string.Format(LocalizationService.Get("MdlKpiPowerHealthCycles", "Zdravlje {0} • Ciklusi: {1}"), BatteryHealthText, BatteryCycles);
        }

        public ICommand ResetCoreClocksCommand
        {
            get { return _resetCoreClocksCommand; }
            set { SetProperty(ref _resetCoreClocksCommand, value); }
        }

        // Multi-Core Stres Test
        private bool _isStressTestRunning = false;
        private int _stressTestProgress = 0;
        private string _stressTestTimeText = "0 / 30s";
        private string _stressTestStatusText = LocalizationService.Get("MdlStressReady", "Spremno za testiranje hlađenja i stabilnosti");
        private string _thermalVerdictTitle = LocalizationService.Get("MdlThermalNotTested", "Nije testirano");
        private string _thermalVerdictDescription = LocalizationService.Get("MdlThermalNotTestedDesc", "Pokrenite test za procenu termalne paste i hladnjaka.");
        private Brush _thermalVerdictBrush = CreateFrozenBrush(136, 136, 136); // #888888
        private string _thermalRecommendation = LocalizationService.Get("MdlThermalRecStart", "Preporuka: Kliknite [▶ Start Stres] za dijagnozu.");
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
        private string _wifiSsid = LocalizationService.Get("MdlNotConnected", "Nije povezano");
        private string _ipAddress = "127.0.0.1";
        private string _connectionType = LocalizationService.Get("MdlNetDetectingNic", "Detekcija mrežnih kartica...");
        private string _connectionStatusText = LocalizationService.Get("MdlNetDetecting", "Detekcija mreže...");
        private Brush _connectionStatusBrush = CreateFrozenBrush(136, 136, 136); // #888888
        private string _distributionSourceText = LocalizationService.Get("MdlNetDistSource", "Lokalni skladišni keš (NAS / USB)");
        private string _nasLatency = LocalizationService.Get("MdlNetNasChecking", "Provera veze ka NAS-u...");
        private string _nasStatus = LocalizationService.Get("MdlConnecting", "Povezivanje...");
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
        private string _autoStartMessage = LocalizationService.Get("MdlAutoStartMsg", "Automatski start za 15s... (Kliknite za trenutni start ili prebacite tab za pauzu)");

        public bool IsServiceReadyVisible
        {
            get { return _isServiceReadyVisible; }
            set
            {
                if (SetProperty(ref _isServiceReadyVisible, value))
                {
                    RefreshProgressBars();
                }
            }
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
        private string _pauseButtonText = LocalizationService.Get("BtnPause", "⏸ Pauza");
        private bool _ipcConnected = true;
        private string _ipcStatusText = LocalizationService.Get("MdlIpcConnected", "IPC Povezan");

        public bool IsPaused
        {
            get { return _isPaused; }
            set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    PauseButtonText = value
                        ? LocalizationService.Get("BtnResume", "▶ Nastavi")
                        : LocalizationService.Get("BtnPause", "⏸ Pauza");
                }
            }
        }

        public string PauseButtonText { get { return _pauseButtonText; } set { SetProperty(ref _pauseButtonText, value); } }
        public bool IpcConnected { get { return _ipcConnected; } set { SetProperty(ref _ipcConnected, value); } }
        public string IpcStatusText { get { return _ipcStatusText; } set { SetProperty(ref _ipcStatusText, value); } }

        private bool _serviceHubConnected = false;
        private string _serviceHubVersion = "";
        private string _serviceHubFooterText = "ServiceHUB";
        private SolidColorBrush _serviceHubIndicatorBrush = CreateFrozenBrush(136, 136, 136);

        public bool ServiceHubConnected { get { return _serviceHubConnected; } set { SetProperty(ref _serviceHubConnected, value); } }
        public string ServiceHubVersion { get { return _serviceHubVersion; } set { SetProperty(ref _serviceHubVersion, value); } }
        public string ServiceHubFooterText { get { return _serviceHubFooterText; } set { SetProperty(ref _serviceHubFooterText, value); } }
        public SolidColorBrush ServiceHubIndicatorBrush { get { return _serviceHubIndicatorBrush; } set { SetProperty(ref _serviceHubIndicatorBrush, value); } }

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
        public ICommand RetryAppCommand { get; set; }
        public ICommand RefreshHardwareCommand { get; set; }
        public ICommand SwitchLanguageCommand { get; set; }
        public ICommand RunHwTestCommand { get; set; }
        public ICommand TogglePhasesExpandedCommand { get; set; }

        // ------------------ SKUPLJANJE / ŠIRENJE KARTICE FAZA ------------------
        private bool _isPhasesExpanded = false;
        public bool IsPhasesExpanded
        {
            get { return _isPhasesExpanded; }
            set
            {
                if (SetProperty(ref _isPhasesExpanded, value))
                {
                    if (Phases != null)
                    {
                        for (int i = 0; i < Phases.Count; i++)
                        {
                            if (Phases[i] != null)
                            {
                                Phases[i].ShowStepsInOverview = value;
                            }
                        }
                    }
                    OnPropertyChanged("PhasesToggleGlyph");
                    OnPropertyChanged("PhasesToggleText");
                    OnPropertyChanged("PhasesToggleToolTip");
                }
            }
        }

        public string PhasesToggleGlyph
        {
            get { return _isPhasesExpanded ? "\u25B2" : "\u25BC"; }
        }

        public string PhasesToggleText
        {
            get
            {
                return _isPhasesExpanded
                    ? LocalizationService.Get("MdlPhasesHideSteps", "\u25B2 Sakrij korake")
                    : LocalizationService.Get("MdlPhasesShowSteps", "\u25BC Prikaži korake");
            }
        }

        public string PhasesToggleToolTip
        {
            get
            {
                return _isPhasesExpanded
                    ? LocalizationService.Get("MdlPhasesHideTooltip", "Skupi prikaz koraka faza")
                    : LocalizationService.Get("MdlPhasesShowTooltip", "Prikaži sve korake faza");
            }
        }

        // ------------------ HARDWARE TEST (WIS-TASK-189) ------------------
        private bool _isHwTestFinished = false;
        private string _hwTestOverallStatus = LocalizationService.Get("MdlNotLaunched", "Nije pokrenut");
        private string _hwTestSummaryText = "";
        private SolidColorBrush _hwTestBadgeBrush = CreateFrozenBrush(108, 117, 125);
        private Dictionary<string, object> _hwTestFullResult = null;

        public bool IsHwTestFinished { get { return _isHwTestFinished; } set { SetProperty(ref _isHwTestFinished, value); } }
        public string HwTestOverallStatus { get { return _hwTestOverallStatus; } set { SetProperty(ref _hwTestOverallStatus, value); } }
        public string HwTestSummaryText { get { return _hwTestSummaryText; } set { SetProperty(ref _hwTestSummaryText, value); } }
        public SolidColorBrush HwTestBadgeBrush { get { return _hwTestBadgeBrush; } set { SetProperty(ref _hwTestBadgeBrush, value); } }
        public Dictionary<string, object> HwTestFullResult { get { return _hwTestFullResult; } set { SetProperty(ref _hwTestFullResult, value); } }

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
        public string TabHwTestHeader { get { return LocalizationService.Instance["TabHwTest"]; } }

        // ------------------ COMPLETION PANEL ------------------
        private bool _isCompleted = false;
        private bool _isServiceFinished = false;
        private bool _criticalStressFailure = false;
        private string _deploymentStatus = "RUNNING";
        private string _stressResultStatus = "UNKNOWN";
        private string _stressResultReason = "";
        private string _completionTitle = LocalizationService.Get("CompletionDefaultTitle", "INSTALACIJA ZAVRŠENA");
        private string _completionSummary = LocalizationService.Get("CompletionDefaultSummary", "Instalacioni tok je završen. Rezultat dijagnostike hlađenja prikazan je odvojeno.");
        private int _shutdownCountdown = 0;
        private bool _shutdownTimerActive = false;
        private string _countdownText = LocalizationService.Get("MdlReadyForDelivery", "Računar je spreman za isporuku.");

        public bool IsCompleted
        {
            get { return _isCompleted; }
            set
            {
                if (SetProperty(ref _isCompleted, value))
                {
                    if (value)
                    {
                        RebuildOverviewAlerts();
                    }
                    _shutdownTimerActive = false;
                    RefreshProgressBars();
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
                    return LocalizationService.Get("MdlCoolingCritical", "Hlađenje: KRITIČNO — predaja je blokirana.");
                if (IsDiagnosticsUnavailable)
                    return LocalizationService.Get("MdlCoolingUnknown", "Hlađenje: NEPROVERENO — obavezna ručna provera ventilatora i hladnjaka.");
                if (string.Equals(_stressResultStatus, "PASS", StringComparison.OrdinalIgnoreCase))
                    return LocalizationService.Get("MdlCoolingPass", "Hlađenje: test završen bez termalnog upozorenja.");
                if (string.Equals(_stressResultStatus, "WARN", StringComparison.OrdinalIgnoreCase))
                    return LocalizationService.Get("MdlCoolingWarn", "Hlađenje: UPOZORENJE — potrebna servisna provera.");
                return LocalizationService.Get("MdlCoolingFail", "Hlađenje: test nije uspešno završen — potrebna ručna provera.");
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
                    if (value)
                    {
                        UpdatePhases();
                    }
                    else
                    {
                        RefreshProgressBars();
                    }
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
                    CountdownText = string.Format(
                        LocalizationService.Get("MdlShutdownCountdown", "Automatsko gašenje računara za: {0}s (WIS Service)"),
                        value);
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
            ActiveStepBars = new ObservableCollection<ActiveStepProgressItem>();
            OverviewAlerts = new ObservableCollection<OverviewAlertItem>();
            RecentLogs = new ObservableCollection<LogEntry>();
            Apps = new ObservableCollection<AppItem>();
            AllLogs = new ObservableCollection<LogEntry>();
            PnpProblemDevices = new ObservableCollection<PnpProblemDevice>();
            CoreClocks = new ObservableCollection<CoreClockItem>();
            HwMonitorSensors = new ObservableCollection<HwMonitorSensorRow>();
            InitializeHwMonitorSensors();
            Autopilot = new AutopilotViewModel();
            Autopilot.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "IsAlertActive" ||
                    e.PropertyName == "IsLocked" ||
                    e.PropertyName == "IsRegistered" ||
                    e.PropertyName == "TenantDomain" ||
                    e.PropertyName == "ProfileTenantDomain" ||
                    e.PropertyName == "StatusTitle" ||
                    e.PropertyName == "StatusLevel")
                {
                    RebuildOverviewAlerts();
                }
            };
            HardwareInfo = new HardwareInfoViewModel();
            Drivers = new DriversTabViewModel();
            Drivers.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "IsBatchResolving" ||
                    e.PropertyName == "BatchProgressPercent" ||
                    e.PropertyName == "BatchStatusText")
                {
                    RefreshProgressBars();
                }
                else if (e.PropertyName == "HasMissingDevices" ||
                         e.PropertyName == "MissingDevicesCount")
                {
                    RebuildOverviewAlerts();
                }
            };

            SwitchLanguageCommand = new RelayCommand(p =>
            {
                string lang = p != null ? p.ToString() : "sr";
                LocalizationService.Instance.SetLanguage(lang);
                RefreshLocalization();
            });

            TogglePhasesExpandedCommand = new RelayCommand(p =>
            {
                IsPhasesExpanded = !IsPhasesExpanded;
            });

            LocalizationService.Instance.PropertyChanged += (s, e) =>
            {
                RefreshLocalization();
            };

            // Inicijalizacija 5 vidljivih faza (interni Number 2..6 ostaje nepromenjen zbog IPC/PS faza)
            Phases.Add(new PhaseItem { Number = 2, DisplayNumber = 1, Title = LocalizationService.Get("MdlPhase2Title", "1. Update"), Subtitle = LocalizationService.Get("MdlPhase2Subtitle", "Drajveri i zakrpe"), Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 3, DisplayNumber = 2, Title = LocalizationService.Get("MdlPhase3Title", "2. Optimizacija"), Subtitle = LocalizationService.Get("MdlPhase3Subtitle", "Debloat i brendiranje"), Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 4, DisplayNumber = 3, Title = LocalizationService.Get("MdlPhase4Title", "3. Programi"), Subtitle = LocalizationService.Get("MdlPhase4Subtitle", "Paketi i OEM licenca"), Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 5, DisplayNumber = 4, Title = LocalizationService.Get("MdlPhase5Title", "4. Hardverski test"), Subtitle = LocalizationService.Get("MdlPhase5Subtitle", "Stres + Periferije"), Status = "Pending" });
            Phases.Add(new PhaseItem { Number = 6, DisplayNumber = 5, Title = LocalizationService.Get("MdlPhase6Title", "5. Završetak"), Subtitle = LocalizationService.Get("MdlPhase6Subtitle", "Dijagnostika i rezime"), Status = "Pending" });
            InitializeDefaultPhaseSteps();

            // Inicijalizacija standardnih WIS aplikacija
            InitializeDefaultApps();
            UpdatePhases();
            RefreshProgressBars();
            RebuildOverviewAlerts();
        }

        public static string GetStepTitleKey(string stepId)
        {
            if (string.IsNullOrEmpty(stepId)) return null;
            switch (stepId.Trim())
            {
                case "P2.Identify": return "Step_P2_Identify";
                case "P2.NasCache": return "Step_P2_NasCache";
                case "P2.Vmd": return "Step_P2_Vmd";
                case "P2.Pnp": return "Step_P2_Pnp";
                case "P2.Diag": return "Step_P2_Diag";
                case "P2.WuDrivers": return "Step_P2_WuDrivers";
                case "P3.Debloat": return "Step_P3_Debloat";
                case "P3.Telemetry": return "Step_P3_Telemetry";
                case "P3.Oem": return "Step_P3_Oem";
                case "P3.Scaling": return "Step_P3_Scaling";
                case "P3.VcRedist": return "Step_P3_VcRedist";
                case "P3.UsbFree": return "Step_P3_UsbFree";
                case "P4.Runtimes": return "Step_P4_Runtimes";
                case "P4.WinRAR": return "Step_P4_WinRAR";
                case "P4.Chrome": return "Step_P4_Chrome";
                case "P4.Acrobat": return "Step_P4_Acrobat";
                case "P4.Office": return "Step_P4_Office";
                case "P4.BatteryBar": return "Step_P4_BatteryBar";
                case "P4.Shortcuts": return "Step_P4_Shortcuts";
                case "P4.PdfDefault": return "Step_P4_PdfDefault";
                case "P4.Activation": return "Step_P4_Activation";
                case "P5.AutoDiag": return "Step_P5_AutoDiag";
                case "P5.Stress": return "Step_P5_Stress";
                case "P5.Peripherals": return "Step_P5_Peripherals";
                case "P6.WuSecondPass": return "Step_P6_WuSecondPass";
                case "P6.Report": return "Step_P6_Report";
                case "P6.Security": return "Step_P6_Security";
                case "P6.Finalize": return "Step_P6_Finalize";
                default: return null;
            }
        }

        private void InitializeDefaultPhaseSteps()
        {
            for (int i = 0; i < Phases.Count; i++)
            {
                PhaseItem phase = Phases[i];
                if (phase == null || phase.Steps == null) continue;
                phase.IsExpanded = true;
                phase.ShowStepsInOverview = _isPhasesExpanded;

                switch (phase.Number)
                {
                    case 2:
                        phase.Steps.Add(new StepItem { Id = "P2.Identify", Title = LocalizationService.Get("Step_P2_Identify", "Identifikacija računara i hardvera"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P2.NasCache", Title = LocalizationService.Get("Step_P2_NasCache", "TrueNAS model drajveri i pre-patch zakrpe"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P2.Vmd", Title = LocalizationService.Get("Step_P2_Vmd", "Intel VMD / RST skladišni drajveri"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P2.Pnp", Title = LocalizationService.Get("Step_P2_Pnp", "PnP instalacija lokalnih drajvera"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P2.Diag", Title = LocalizationService.Get("Step_P2_Diag", "Dijagnostika preostalih uređaja"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P2.WuDrivers", Title = LocalizationService.Get("Step_P2_WuDrivers", "Hardverski drajveri preko Windows Update-a"), Status = "WAIT" });
                        break;
                    case 3:
                        phase.Steps.Add(new StepItem { Id = "P3.Debloat", Title = LocalizationService.Get("Step_P3_Debloat", "Debloat neželjenih aplikacija"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P3.Telemetry", Title = LocalizationService.Get("Step_P3_Telemetry", "Gašenje telemetrije i praćenja"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P3.Oem", Title = LocalizationService.Get("Step_P3_Oem", "OEM brending i regionalna podešavanja"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P3.Scaling", Title = LocalizationService.Get("Step_P3_Scaling", "Optimalno DPI skaliranje ekrana"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P3.VcRedist", Title = LocalizationService.Get("Step_P3_VcRedist", "Visual C++ Runtime biblioteke"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P3.UsbFree", Title = LocalizationService.Get("Step_P3_UsbFree", "Signal za oslobađanje USB medijuma"), Status = "WAIT" });
                        break;
                    case 4:
                        phase.Steps.Add(new StepItem { Id = "P4.Runtimes", Title = LocalizationService.Get("Step_P4_Runtimes", "Sistemski runtime paketi (VC++ / .NET)"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.WinRAR", Title = LocalizationService.Get("Step_P4_WinRAR", "WinRAR arhiver"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.Chrome", Title = LocalizationService.Get("Step_P4_Chrome", "Google Chrome pregledač"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.Acrobat", Title = LocalizationService.Get("Step_P4_Acrobat", "Adobe Acrobat Pro"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.Office", Title = LocalizationService.Get("Step_P4_Office", "Microsoft Office 2024"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.BatteryBar", Title = LocalizationService.Get("Step_P4_BatteryBar", "BatteryBar indikator baterije"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.Shortcuts", Title = LocalizationService.Get("Step_P4_Shortcuts", "Radne prečice na radnoj površini"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.PdfDefault", Title = LocalizationService.Get("Step_P4_PdfDefault", "Podrazumevani PDF pregledač"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P4.Activation", Title = LocalizationService.Get("Step_P4_Activation", "Aktivacija Windows i Office paketa"), Status = "WAIT" });
                        break;
                    case 5:
                        phase.Steps.Add(new StepItem { Id = "P5.AutoDiag", Title = LocalizationService.Get("Step_P5_AutoDiag", "Automatska provera sistema"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P5.Stress", Title = LocalizationService.Get("Step_P5_Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P5.Peripherals", Title = LocalizationService.Get("Step_P5_Peripherals", "Interaktivni testovi periferija"), Status = "WAIT" });
                        break;
                    case 6:
                        phase.Steps.Add(new StepItem { Id = "P6.WuSecondPass", Title = LocalizationService.Get("Step_P6_WuSecondPass", "Dvoprolazni Update i WU barijera"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P6.Report", Title = LocalizationService.Get("Step_P6_Report", "Servisni karton i flotni izveštaj"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P6.Security", Title = LocalizationService.Get("Step_P6_Security", "Vraćanje bezbednosnih polisa i Anti-Leak"), Status = "WAIT" });
                        phase.Steps.Add(new StepItem { Id = "P6.Finalize", Title = LocalizationService.Get("Step_P6_Finalize", "Završna verifikacija za isporuku"), Status = "WAIT" });
                        break;
                }
            }
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
            OnPropertyChanged("TabHwTestHeader");
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
                    if (appItem == null) continue;
                    if (string.Equals(appItem.Id, "License", StringComparison.OrdinalIgnoreCase))
                    {
                        appItem.Name = LocalizationService.Get("MdlAppNameLicense", "OEM Licenca");
                    }
                    else if (string.Equals(appItem.Id, "Shortcuts", StringComparison.OrdinalIgnoreCase))
                    {
                        appItem.Name = LocalizationService.Get("MdlAppNameShortcuts", "Servisne Prečice");
                    }
                    appItem.RefreshLocalization();
                }
            }

            if (Phases != null)
            {
                for (int i = 0; i < Phases.Count; i++)
                {
                    PhaseItem ph = Phases[i];
                    if (ph == null || ph.Steps == null) continue;
                    for (int j = 0; j < ph.Steps.Count; j++)
                    {
                        StepItem st = ph.Steps[j];
                        if (st == null) continue;
                        string stepKey = GetStepTitleKey(st.Id);
                        if (!string.IsNullOrEmpty(stepKey))
                        {
                            st.Title = LocalizationService.Get(stepKey, st.Title);
                        }
                    }
                }
            }

            if (_completionTitle == "INSTALACIJA ZAVRŠENA" || _completionTitle == "服务完成 - 计算机已准备交付客户")
            {
                CompletionTitle = LocalizationService.Instance["CompletionDefaultTitle"];
            }
            if (_completionSummary == "Instalacioni tok je završen. Rezultat dijagnostike hlađenja prikazan je odvojeno." || _completionSummary == "所有硬件组件、系统补丁及应用程序均已成功部署并通过验证。")
            {
                CompletionSummary = LocalizationService.Instance["CompletionDefaultSummary"];
            }
            OnPropertyChanged("CompletionStatusBadgeText");
            OnPropertyChanged("CompletionAlertsSummaryText");
            OnPropertyChanged("DiagnosticsSummary");
            OnPropertyChanged("DeviceTypeText");

            if (_shutdownCountdown > 0)
            {
                CountdownText = string.Format(
                    LocalizationService.Get("MdlShutdownCountdown", "Automatsko gašenje računara za: {0}s (WIS Service)"),
                    _shutdownCountdown);
            }
            else if (_countdownText == "Računar je spreman za isporuku." || _countdownText == "计算机已准备就绪交付。")
            {
                CountdownText = LocalizationService.Get("MdlReadyForDelivery", "Računar je spreman za isporuku.");
            }

            if (_hasPowerSample)
            {
                UpdatePowerMonitor(
                    _hasBatteryLive,
                    _isOnAcPower,
                    _isBatteryCharging,
                    _isBatteryDischarging,
                    _batteryChargePercent,
                    _batteryRateWatts,
                    _batteryCurrentAmps,
                    _lastPowerVoltageV,
                    _lastPowerLifeRemainingSec);
            }
            else
            {
                PowerStatusTitle = LocalizationService.Get("MdlPwrReadingTitle", "Očitavanje napajanja...");
                PowerMonitorTooltip = LocalizationService.Get("MdlPwrReadingTooltip", "Očitavanje stanja baterije i punjača...");
            }

            RefreshDeviceCardQr();
            RefreshHwMonitorSensorLabels();
            RefreshHwMonitorKpiSummaries();
            UpdateAppsSummary();
            UpdatePhases();
            _lastOverviewAlertsSignature = null;
            UpdatePnpStatus();
            OnPropertyChanged("PhasesToggleGlyph");
            OnPropertyChanged("PhasesToggleText");
            OnPropertyChanged("PhasesToggleToolTip");
            OnPropertyChanged("BackgroundWuDisplayText");
            OnPropertyChanged("BackgroundWuBadgeText");
            OnPropertyChanged(string.Empty);
        }

        private void InitializeDefaultApps()
        {
            Action initAction = delegate
            {
                Apps.Clear();
                string queueTxt = LocalizationService.Get("MdlAppDetailQueue", "U redu čekanja");
                Apps.Add(new AppItem { Id = "DotNet", Name = ".NET 8 Runtime", IconGlyph = "⚡", Status = "WAIT", Detail = queueTxt });
                Apps.Add(new AppItem { Id = "Chrome", Name = "Google Chrome", IconGlyph = "🌐", Status = "WAIT", Detail = queueTxt });
                Apps.Add(new AppItem { Id = "WinRAR", Name = "WinRAR x64", IconGlyph = "🗜️", Status = "WAIT", Detail = queueTxt });
                Apps.Add(new AppItem { Id = "Acrobat", Name = "Adobe Acrobat Pro", IconGlyph = "📄", Status = "WAIT", Detail = queueTxt });
                Apps.Add(new AppItem { Id = "Office", Name = "Microsoft Office 2024", IconGlyph = "📊", Status = "WAIT", Detail = queueTxt });

                if (!_isLaptop || _batteryHealth <= 0)
                {
                    var batApp = new AppItem { Id = "BatteryBar", Name = "BatteryBar", IconGlyph = "🔋", Status = "SKIPPED" };
                    batApp.SetLocalizedDetail("MdlAppNotApplicableDesktop", "Nije primenjivo na desktopu");
                    Apps.Add(batApp);
                }
                else
                {
                    var batApp = new AppItem { Id = "BatteryBar", Name = "BatteryBar", IconGlyph = "🔋", Status = "WAIT" };
                    batApp.SetLocalizedDetail("MdlAppDetailQueue", "U redu čekanja");
                    Apps.Add(batApp);
                }

                Apps.Add(new AppItem { Id = "License", Name = LocalizationService.Get("MdlAppNameLicense", "OEM Licenca"), IconGlyph = "🔑", Status = "WAIT", Detail = queueTxt });
                Apps.Add(new AppItem { Id = "Shortcuts", Name = LocalizationService.Get("MdlAppNameShortcuts", "Servisne Prečice"), IconGlyph = "🔗", Status = "WAIT", Detail = queueTxt });
                for (int i = 0; i < Apps.Count; i++)
                {
                    if (Apps[i].Status == "WAIT") Apps[i].SetLocalizedDetail("MdlAppDetailQueue", "U redu čekanja");
                }
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
                for (int i = 0; i < Phases.Count; i++)
                {
                    int pNum = Phases[i].Number;
                    switch (pNum)
                    {
                        case 2:
                            Phases[i].Title = LocalizationService.Get("MdlPhase2Title", "1. Update");
                            Phases[i].Subtitle = LocalizationService.Get("MdlPhase2Subtitle", "Drajveri i zakrpe");
                            break;
                        case 3:
                            Phases[i].Title = LocalizationService.Get("MdlPhase3Title", "2. Optimizacija");
                            Phases[i].Subtitle = LocalizationService.Get("MdlPhase3Subtitle", "Debloat i brendiranje");
                            break;
                        case 4:
                            Phases[i].Title = LocalizationService.Get("MdlPhase4Title", "3. Programi");
                            Phases[i].Subtitle = LocalizationService.Get("MdlPhase4Subtitle", "Paketi i OEM licenca");
                            break;
                        case 5:
                            Phases[i].Title = LocalizationService.Get("MdlPhase5Title", "4. Hardverski test");
                            Phases[i].Subtitle = LocalizationService.Get("MdlPhase5Subtitle", "Stres + Periferije");
                            break;
                        case 6:
                            Phases[i].Title = LocalizationService.Get("MdlPhase6Title", "5. Završetak");
                            Phases[i].Subtitle = LocalizationService.Get("MdlPhase6Subtitle", "Dijagnostika i rezime");
                            break;
                    }

                    if (pNum < _currentPhase || (_isServiceFinished && pNum == 6))
                    {
                        Phases[i].Status = "Completed";
                        if (Phases[i].Steps != null)
                        {
                            for (int sIdx = 0; sIdx < Phases[i].Steps.Count; sIdx++)
                            {
                                StepItem st = Phases[i].Steps[sIdx];
                                if (st != null && string.Equals(st.Status, "WAIT", StringComparison.OrdinalIgnoreCase))
                                {
                                    st.Status = "OK";
                                }
                            }
                        }
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
                RefreshProgressBars();
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

        public void RefreshProgressBars()
        {
            Action refreshAction = delegate
            {
                if (Phases == null || ActiveStepBars == null) return;

                int totalSteps = 0;
                int doneSteps = 0;
                int runningSteps = 0;

                for (int i = 0; i < Phases.Count; i++)
                {
                    PhaseItem ph = Phases[i];
                    if (ph == null || ph.Steps == null) continue;
                    for (int j = 0; j < ph.Steps.Count; j++)
                    {
                        StepItem st = ph.Steps[j];
                        if (st == null) continue;
                        totalSteps++;
                        if (st.IsCompleted || st.IsSkipped || st.IsWarning || st.IsError)
                        {
                            doneSteps++;
                        }
                        else if (st.IsRunning)
                        {
                            runningSteps++;
                        }
                    }
                }

                if (_isServiceFinished || _isCompleted)
                {
                    GlobalProgress = 100;
                    GlobalProgressSummary = totalSteps > 0
                        ? string.Format(LocalizationService.Get("MdlProgressDoneSteps", "{0} / {0} koraka • Završeno"), totalSteps)
                        : LocalizationService.Get("MdlProgressDone100", "100% • Završeno");
                }
                else if (_isServiceReadyVisible)
                {
                    GlobalProgress = 0;
                    GlobalProgressSummary = string.Format(LocalizationService.Get("MdlProgressReadySteps", "0 / {0} koraka • Spremno"), totalSteps);
                }
                else
                {
                    double stepRatio = (_progress > 0 && _progress < 100) ? (_progress / 100.0) : 0.45;
                    double weight = doneSteps + (runningSteps * stepRatio);
                    int computed = totalSteps > 0
                        ? Math.Min(99, (int)Math.Round((weight / totalSteps) * 100.0))
                        : _progress;
                    if (computed == 0 && _progress > 0)
                    {
                        computed = Math.Min(99, Math.Max(1, _progress / Math.Max(1, Phases.Count)));
                    }
                    GlobalProgress = computed;
                    GlobalProgressSummary = string.Format(LocalizationService.Get("MdlProgressInProgressSteps", "{0} / {1} koraka • {2}"), doneSteps, totalSteps, _phaseName);
                }

                List<ActiveStepProgressItem> nextBars = new List<ActiveStepProgressItem>();
                if (_isServiceReadyVisible)
                {
                    nextBars.Add(new ActiveStepProgressItem
                    {
                        Id = "ready",
                        PhaseNumber = 0,
                        PhaseLabel = LocalizationService.Get("MdlBarReadyLabel", "SPREMNO"),
                        Title = LocalizationService.Get("MdlBarReadyTitle", "Čekanje na početak instalacije"),
                        Detail = !string.IsNullOrWhiteSpace(_liveStatus) ? _liveStatus : LocalizationService.Get("MdlBarReadyDetail", "Kliknite 'ZAPOČNI INSTALACIJU' ili sačekajte automatski start."),
                        Progress = 0,
                        IsIndeterminate = false,
                        BarBrush = CreateFrozenBrush(0, 122, 204),
                        BadgeBrush = CreateFrozenBrush(62, 62, 66)
                    });
                }
                else if (_isServiceFinished || _isCompleted)
                {
                    nextBars.Add(new ActiveStepProgressItem
                    {
                        Id = "done",
                        PhaseNumber = 6,
                        PhaseLabel = LocalizationService.Get("MdlBarDoneLabel", "ZAVRŠENO"),
                        Title = LocalizationService.Get("MdlBarDoneTitle", "Svi koraci su uspešno završeni"),
                        Detail = !string.IsNullOrWhiteSpace(_liveStatus) ? _liveStatus : LocalizationService.Get("MdlBarDoneDetail", "Računar je pripremljen i spreman za predaju."),
                        Progress = 100,
                        IsIndeterminate = false,
                        BarBrush = CreateFrozenBrush(46, 204, 113),
                        BadgeBrush = CreateFrozenBrush(35, 134, 54)
                    });
                }
                else
                {
                    int runIdx = 0;
                    for (int i = 0; i < Phases.Count; i++)
                    {
                        PhaseItem ph = Phases[i];
                        if (ph == null || ph.Steps == null) continue;
                        for (int j = 0; j < ph.Steps.Count; j++)
                        {
                            StepItem st = ph.Steps[j];
                            if (st == null || !st.IsRunning) continue;

                            bool isPrimary = (runIdx == 0);
                            int stepPct = (isPrimary && _progress > 0 && _progress < 100) ? _progress : 0;
                            bool indet = !(isPrimary && _progress > 0 && _progress < 100);
                            string detailTxt = !string.IsNullOrWhiteSpace(st.Reason)
                                ? st.Reason
                                : (isPrimary ? _liveStatus : LocalizationService.Get("MdlBarStepRunning", "Izvršavanje koraka u toku..."));

                            nextBars.Add(new ActiveStepProgressItem
                            {
                                Id = !string.IsNullOrEmpty(st.Id) ? st.Id : ("run_" + runIdx),
                                PhaseNumber = ph.Number,
                                PhaseLabel = !string.IsNullOrEmpty(ph.Title) ? ph.Title.ToUpperInvariant() : LocalizationService.Get("MdlBarPhaseFallback", "FAZA"),
                                Title = !string.IsNullOrEmpty(st.Title) ? st.Title : st.Id,
                                Detail = detailTxt,
                                Progress = stepPct,
                                IsIndeterminate = indet,
                                BarBrush = CreateFrozenBrush(78, 201, 176),
                                BadgeBrush = CreateFrozenBrush(0, 122, 204)
                            });
                            runIdx++;
                        }
                    }

                    if (nextBars.Count == 0)
                    {
                        nextBars.Add(new ActiveStepProgressItem
                        {
                            Id = "current",
                            PhaseNumber = _currentPhase,
                            PhaseLabel = !string.IsNullOrWhiteSpace(_phaseName) ? _phaseName.ToUpperInvariant() : LocalizationService.Get("MdlBarInProgressFallback", "U TOKU"),
                            Title = !string.IsNullOrWhiteSpace(_subPhase) ? _subPhase : _phaseName,
                            Detail = _liveStatus,
                            Progress = _progress,
                            IsIndeterminate = (_progress <= 0),
                            BarBrush = CreateFrozenBrush(78, 201, 176),
                            BadgeBrush = CreateFrozenBrush(0, 122, 204)
                        });
                    }

                    string wuNorm = (_backgroundWuStatus ?? "").Trim().ToUpperInvariant();
                    bool wuActive = _isBackgroundWuVisible && (
                        wuNorm == "STARTING" || wuNorm == "SEARCHING" || wuNorm == "DOWNLOADING" ||
                        wuNorm == "WAITING_MSI" || wuNorm == "INSTALLING" || wuNorm == "HARVESTING");
                    if (wuActive)
                    {
                        nextBars.Add(new ActiveStepProgressItem
                        {
                            Id = "bg_wu",
                            PhaseNumber = 0,
                            PhaseLabel = LocalizationService.Get("MdlBarBgWuLabel", "POZADINSKI WU"),
                            Title = LocalizationService.Get("MdlBarBgWuTitle", "Pozadinski Windows Update"),
                            Detail = BackgroundWuDisplayText,
                            Progress = _backgroundWuProgress >= 0 ? _backgroundWuProgress : 0,
                            IsIndeterminate = (_backgroundWuProgress < 0),
                            BarBrush = CreateFrozenBrush(56, 189, 248),
                            BadgeBrush = BackgroundWuBadgeBrush
                        });
                    }

                    if (Drivers != null && Drivers.IsBatchResolving)
                    {
                        nextBars.Add(new ActiveStepProgressItem
                        {
                            Id = "drv_batch",
                            PhaseNumber = 0,
                            PhaseLabel = LocalizationService.Get("MdlBarDriversLabel", "DRAJVERI"),
                            Title = LocalizationService.Get("MdlBarDriversTitle", "Grupna instalacija drajvera"),
                            Detail = Drivers.BatchStatusText,
                            Progress = Drivers.BatchProgressPercent,
                            IsIndeterminate = (Drivers.BatchProgressPercent <= 0),
                            BarBrush = CreateFrozenBrush(163, 113, 247),
                            BadgeBrush = CreateFrozenBrush(137, 87, 229)
                        });
                    }

                    if (Drivers != null && Drivers.MissingDevices != null)
                    {
                        for (int d = 0; d < Drivers.MissingDevices.Count; d++)
                        {
                            DriverItemViewModel dev = Drivers.MissingDevices[d];
                            if (dev == null || !dev.IsInstalling) continue;
                            nextBars.Add(new ActiveStepProgressItem
                            {
                                Id = "drv_" + d,
                                PhaseNumber = 0,
                                PhaseLabel = LocalizationService.Get("MdlBarDriverLabel", "DRAJVER"),
                                Title = !string.IsNullOrWhiteSpace(dev.Name) ? dev.Name : LocalizationService.Get("MdlBarDriverTitle", "Instalacija drajvera"),
                                Detail = dev.InstallStatusText,
                                Progress = dev.InstallProgressPercent,
                                IsIndeterminate = (dev.InstallProgressPercent <= 0),
                                BarBrush = CreateFrozenBrush(163, 113, 247),
                                BadgeBrush = CreateFrozenBrush(137, 87, 229)
                            });
                        }
                    }
                }

                var activeIdsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime syncUtc = DateTime.UtcNow;
                for (int k = 0; k < nextBars.Count; k++)
                {
                    string barId = nextBars[k].Id ?? ("bar_" + k);
                    activeIdsSet.Add(barId);
                    if (!_activeBarStartUtc.ContainsKey(barId))
                    {
                        _activeBarStartUtc[barId] = syncUtc;
                    }
                }
                var staleIds = new List<string>();
                foreach (string existingId in _activeBarStartUtc.Keys)
                {
                    if (!activeIdsSet.Contains(existingId))
                    {
                        staleIds.Add(existingId);
                    }
                }
                for (int sIdx = 0; sIdx < staleIds.Count; sIdx++)
                {
                    _activeBarStartUtc.Remove(staleIds[sIdx]);
                }

                bool sameIds = (ActiveStepBars.Count == nextBars.Count);
                if (sameIds)
                {
                    for (int k = 0; k < nextBars.Count; k++)
                    {
                        if (!string.Equals(ActiveStepBars[k].Id, nextBars[k].Id, StringComparison.Ordinal))
                        {
                            sameIds = false;
                            break;
                        }
                    }
                }

                if (sameIds)
                {
                    for (int k = 0; k < nextBars.Count; k++)
                    {
                        ActiveStepProgressItem cur = ActiveStepBars[k];
                        ActiveStepProgressItem nxt = nextBars[k];
                        cur.PhaseNumber = nxt.PhaseNumber;
                        cur.PhaseLabel = nxt.PhaseLabel;
                        cur.Title = nxt.Title;
                        cur.Detail = nxt.Detail;
                        cur.Progress = nxt.Progress;
                        cur.IsIndeterminate = nxt.IsIndeterminate;
                        cur.BarBrush = nxt.BarBrush;
                        cur.BadgeBrush = nxt.BadgeBrush;
                    }
                }
                else
                {
                    ActiveStepBars.Clear();
                    for (int k = 0; k < nextBars.Count; k++)
                    {
                        ActiveStepBars.Add(nextBars[k]);
                    }
                }

                UpdateLiveTimersAndEta();
                RebuildOverviewAlerts();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(refreshAction);
            }
            else
            {
                refreshAction();
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
                AllLogs.Insert(0, entry);
                while (AllLogs.Count > MaxLogCount)
                {
                    AllLogs.RemoveAt(AllLogs.Count - 1);
                }

                RecentLogs.Insert(0, entry);
                while (RecentLogs.Count > 5)
                {
                    RecentLogs.RemoveAt(RecentLogs.Count - 1);
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
                            if (app.Status == "OK") app.SetLocalizedDetail("MdlAppDetailOk", "Instalacija uspešno verifikovana");
                            else if (app.Status == "RUNNING") app.SetLocalizedDetail("MdlAppDetailRunning", "Instalacija paketa u toku...");
                            else if (app.Status == "WARN") app.SetLocalizedDetail("MdlAppDetailWarn", "Instalacija nije potvrđena");
                            else if (app.Status == "ERROR") app.SetLocalizedDetail("MdlAppDetailError", "Greška tokom instalacije");
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
                    case "SKIPPED":
                    case "SKIPPED_DONE":
                    case "SKIPPED_USER":
                        skipped++; break;
                    case "WARN":
                    case "ERROR": error++; break;
                    default: waiting++; break;
                }
            }

            AppsTotalCount = total;
            AppsInstalledCount = completed;
            AppsRunningCount = running;
            AppsSkippedCount = skipped;
            AppsErrorCount = error;

            AppsSummaryText = string.Format(
                LocalizationService.Get("MdlAppsSummary", "{0} od {1} instalirano • {2} u toku • {3} preskočeno • {4} grešaka"),
                completed, total, running, skipped, error);
            RebuildOverviewAlerts();
        }

        // ------------------ PREGLED — AKTIVNA UPOZORENJA ------------------
        public ObservableCollection<OverviewAlertItem> OverviewAlerts { get; private set; }
        private Brush _overviewAlertsBadgeBrush = CreateFrozenBrush(35, 134, 54);
        private Brush _completionCardBorderBrush = CreateFrozenBrush(46, 204, 113);
        private Brush _completionStatusBadgeBrush = CreateFrozenBrush(46, 204, 113);
        private Brush _completionStatusBadgeForeground = CreateFrozenBrush(18, 18, 20);
        private bool _hasCriticalOverviewAlerts = false;
        private string _lastOverviewAlertsSignature = null;

        public Brush CompletionCardBorderBrush
        {
            get { return _completionCardBorderBrush; }
            set { SetProperty(ref _completionCardBorderBrush, value); }
        }

        public Brush CompletionStatusBadgeBrush
        {
            get { return _completionStatusBadgeBrush; }
            set { SetProperty(ref _completionStatusBadgeBrush, value); }
        }

        public Brush CompletionStatusBadgeForeground
        {
            get { return _completionStatusBadgeForeground; }
            set { SetProperty(ref _completionStatusBadgeForeground, value); }
        }

        public string CompletionStatusBadgeText
        {
            get
            {
                if (OverviewAlerts == null || OverviewAlerts.Count == 0)
                {
                    return LocalizationService.Get("MdlCompletionBadgeOk", "✓ SERVIS USPEŠNO ZAVRŠEN");
                }
                if (_hasCriticalOverviewAlerts)
                {
                    return LocalizationService.Get("MdlCompletionBadgeCrit", "⚠ SERVIS ZAVRŠEN SA KRITIČNIM UPOZORENJIMA");
                }
                return LocalizationService.Get("MdlCompletionBadgeWarn", "⚠ SERVIS ZAVRŠEN SA UPOZORENJIMA");
            }
        }

        public string CompletionAlertsSummaryText
        {
            get
            {
                int count = OverviewAlerts != null ? OverviewAlerts.Count : 0;
                if (count == 0)
                {
                    return LocalizationService.Get("MdlCompletionAlertsOk", "Proces je završen čisto — uređaj je spreman za predaju klijentu.");
                }
                if (_hasCriticalOverviewAlerts)
                {
                    return string.Format(
                        LocalizationService.Get("MdlCompletionAlertsCrit", "Završeno sa {0} aktivnih upozorenja (uključujući kritična) — kliknite na stavku za pregled."),
                        count);
                }
                return string.Format(
                    LocalizationService.Get("MdlCompletionAlertsWarn", "Završeno sa {0} upozorenja — proverite stavke pre predaje klijentu."),
                    count);
            }
        }

        public bool HasOverviewAlerts
        {
            get { return OverviewAlerts != null && OverviewAlerts.Count > 0; }
        }

        public bool ShowNoOverviewAlerts
        {
            get { return OverviewAlerts == null || OverviewAlerts.Count == 0; }
        }

        public string OverviewAlertsCountText
        {
            get { return OverviewAlerts != null ? OverviewAlerts.Count.ToString() : "0"; }
        }

        public Brush OverviewAlertsBadgeBrush
        {
            get { return _overviewAlertsBadgeBrush; }
            set { SetProperty(ref _overviewAlertsBadgeBrush, value); }
        }

        private static string FormatCompactNamesList(List<string> names, int maxShow)
        {
            if (names == null || names.Count == 0) return "";
            int take = Math.Min(maxShow, names.Count);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            if (names.Count > take)
            {
                sb.AppendFormat(" (+{0})", names.Count - take);
            }
            return sb.ToString();
        }

        public void RebuildOverviewAlerts()
        {
            if (OverviewAlerts == null) return;

            var items = new List<OverviewAlertItem>();

            // 1. Termalno upozorenje / pregrevanje i trotlovanje
            if (_emergencyAbortTriggered || _cpuTemp >= 88.0f || _gpuTemp >= 88.0f || _ssdTemp >= 72.0f)
            {
                var parts = new List<string>();
                if (_cpuTemp > 0.5f) parts.Add(string.Format("CPU {0:F0}°C", _cpuTemp));
                if (_gpuTemp > 0.5f) parts.Add(string.Format("GPU {0:F0}°C", _gpuTemp));
                if (_ssdTemp > 0.5f) parts.Add(string.Format("SSD {0:F0}°C", _ssdTemp));
                if (_throttlingPercent >= 15.0f) parts.Add(string.Format("Throttling {0:F0}%", _throttlingPercent));
                if (_emergencyAbortTriggered) parts.Add(LocalizationService.Get("MdlAlertThermalEmergency", "Emergency Abort aktiviran"));
                string detail = parts.Count > 0 ? string.Join(" • ", parts.ToArray()) : LocalizationService.Get("MdlAlertThermalCritDetail", "Detektovana kritična temperatura hardvera");
                items.Add(OverviewAlertItem.Create("thermal", "CRITICAL", LocalizationService.Get("MdlAlertThermalCritTitle", "Računar se pregrejava"), detail, "diagnostics"));
            }
            else if (_cpuTemp >= 80.0f || _gpuTemp >= 82.0f || _ssdTemp >= 62.0f || _throttlingPercent >= 15.0f)
            {
                var parts = new List<string>();
                if (_cpuTemp >= 80.0f) parts.Add(string.Format("CPU {0:F0}°C", _cpuTemp));
                if (_gpuTemp >= 82.0f) parts.Add(string.Format("GPU {0:F0}°C", _gpuTemp));
                if (_ssdTemp >= 62.0f) parts.Add(string.Format("SSD {0:F0}°C", _ssdTemp));
                if (_throttlingPercent >= 15.0f) parts.Add(string.Format("Throttling {0:F0}%", _throttlingPercent));
                string detail = parts.Count > 0 ? string.Join(" • ", parts.ToArray()) : LocalizationService.Get("MdlAlertThermalWarnDetail", "Povišena radna temperatura");
                items.Add(OverviewAlertItem.Create("thermal", "WARNING", LocalizationService.Get("MdlAlertThermalWarnTitle", "Povišena temperatura / trotlovanje"), detail, "diagnostics"));
            }

            // 2. Windows Autopilot / MDM
            if (Autopilot != null && (Autopilot.IsAlertActive || Autopilot.IsLocked || Autopilot.IsRegistered))
            {
                string sev = (Autopilot.IsLocked ||
                    string.Equals(Autopilot.StatusLevel, "CRITICAL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Autopilot.StatusLevel, "ERROR", StringComparison.OrdinalIgnoreCase))
                    ? "CRITICAL"
                    : "WARNING";
                string title = Autopilot.IsLocked
                    ? LocalizationService.Get("MdlAlertApLockedTitle", "Detektovan Windows Autopilot")
                    : LocalizationService.Get("MdlAlertApMdmTitle", "Detektovan Autopilot / MDM");
                string tenant = !string.IsNullOrWhiteSpace(Autopilot.ProfileTenantDomain)
                    ? Autopilot.ProfileTenantDomain
                    : Autopilot.TenantDomain;
                if (string.IsNullOrWhiteSpace(tenant) ||
                    tenant.StartsWith("Nema", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tenant, LocalizationService.Get("MdlApNoneNotAssigned", "Nema (Nije dodeljen)"), StringComparison.OrdinalIgnoreCase) ||
                    tenant == "N/A")
                {
                    tenant = !string.IsNullOrWhiteSpace(Autopilot.StatusTitle)
                        ? Autopilot.StatusTitle
                        : LocalizationService.Get("MdlAlertApCheckTab", "Proverite Autopilot karticu za detalje");
                }
                else
                {
                    tenant = "Tenant: " + tenant;
                }
                items.Add(OverviewAlertItem.Create("autopilot", sev, title, tenant, "autopilot"));
            }

            // 3. Nedostajući ili problematični drajveri
            int drvMissing = 0;
            if (Drivers != null)
            {
                int listCount = Drivers.MissingDevices != null ? Drivers.MissingDevices.Count : 0;
                drvMissing = Math.Max(Drivers.MissingDevicesCount, listCount);
            }
            int pnpCount = PnpProblemDevices != null ? PnpProblemDevices.Count : 0;
            int totalDrvProblems = Math.Max(drvMissing, pnpCount);

            bool phase2Done = false;
            bool phase2StepProblem = false;
            if (Phases != null)
            {
                for (int i = 0; i < Phases.Count; i++)
                {
                    PhaseItem ph = Phases[i];
                    if (ph == null || ph.Number != 2) continue;
                    string stNorm = (ph.Status ?? "").Trim().ToUpperInvariant();
                    if (stNorm == "COMPLETED" || stNorm == "OK") phase2Done = true;
                    if (ph.Steps != null)
                    {
                        for (int j = 0; j < ph.Steps.Count; j++)
                        {
                            StepItem st = ph.Steps[j];
                            if (st != null && (st.IsError || st.IsWarning))
                            {
                                if (string.Equals(st.Id, "P2.Pnp", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(st.Id, "P2.Diag", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(st.Id, "P2.Vmd", StringComparison.OrdinalIgnoreCase))
                                {
                                    phase2StepProblem = true;
                                }
                            }
                        }
                    }
                }
            }

            if (totalDrvProblems > 0 || phase2StepProblem)
            {
                var devNames = new List<string>();
                if (Drivers != null && Drivers.MissingDevices != null)
                {
                    for (int i = 0; i < Drivers.MissingDevices.Count; i++)
                    {
                        DriverItemViewModel dev = Drivers.MissingDevices[i];
                        if (dev != null && !string.IsNullOrWhiteSpace(dev.Name) && !devNames.Contains(dev.Name))
                        {
                            devNames.Add(dev.Name);
                        }
                    }
                }
                if (devNames.Count == 0 && PnpProblemDevices != null)
                {
                    for (int i = 0; i < PnpProblemDevices.Count; i++)
                    {
                        PnpProblemDevice pnp = PnpProblemDevices[i];
                        if (pnp != null && !string.IsNullOrWhiteSpace(pnp.Name) && !devNames.Contains(pnp.Name))
                        {
                            devNames.Add(pnp.Name);
                        }
                    }
                }

                string sev = phase2Done ? "CRITICAL" : "WARNING";
                string detail;
                if (totalDrvProblems > 0)
                {
                    string prefix = totalDrvProblems == 1
                        ? LocalizationService.Get("MdlAlertDrvOneMissing", "1 uređaj bez drajvera")
                        : string.Format(LocalizationService.Get("MdlAlertDrvManyErrors", "{0} uređaja sa greškom"), totalDrvProblems);
                    string namesText = FormatCompactNamesList(devNames, 2);
                    detail = !string.IsNullOrEmpty(namesText) ? string.Format("{0} ({1})", prefix, namesText) : prefix;
                }
                else
                {
                    detail = LocalizationService.Get("MdlAlertDrvStepDetail", "Korak instalacije drajvera prijavio je grešku ili upozorenje.");
                }
                items.Add(OverviewAlertItem.Create("drivers", sev, LocalizationService.Get("MdlAlertDrvTitle", "Nisu instalirani svi drajveri"), detail, "drivers"));
            }

            // 4. Neinstalirani ili neuspeli programi (Apps ERROR/WARN + Phase 4 ERROR/WARN koraci)
            var failedAppNames = new List<string>();
            bool hasAppCritical = false;
            if (Apps != null)
            {
                for (int i = 0; i < Apps.Count; i++)
                {
                    AppItem appItem = Apps[i];
                    if (appItem == null) continue;
                    string appSt = (appItem.Status ?? "").Trim().ToUpperInvariant();
                    if (appSt == "ERROR" || appSt == "FAILED" || appSt == "WARN" || appSt == "WARNING")
                    {
                        if (appSt == "ERROR" || appSt == "FAILED") hasAppCritical = true;
                        string n = !string.IsNullOrWhiteSpace(appItem.Name) ? appItem.Name : appItem.Id;
                        if (!string.IsNullOrWhiteSpace(n) && !failedAppNames.Contains(n))
                        {
                            failedAppNames.Add(n);
                        }
                    }
                }
            }
            if (Phases != null)
            {
                for (int i = 0; i < Phases.Count; i++)
                {
                    PhaseItem ph = Phases[i];
                    if (ph == null || ph.Number != 4 || ph.Steps == null) continue;
                    for (int j = 0; j < ph.Steps.Count; j++)
                    {
                        StepItem st = ph.Steps[j];
                        if (st == null || (!st.IsError && !st.IsWarning)) continue;
                        if (st.IsError) hasAppCritical = true;
                        string n = !string.IsNullOrWhiteSpace(st.Title) ? st.Title : st.Id;
                        if (!string.IsNullOrWhiteSpace(n) && !failedAppNames.Contains(n))
                        {
                            failedAppNames.Add(n);
                        }
                    }
                }
            }

            if (failedAppNames.Count > 0)
            {
                string detail = LocalizationService.Get("MdlAlertAppsFailedPrefix", "Neuspelo: ") + FormatCompactNamesList(failedAppNames, 3);
                items.Add(OverviewAlertItem.Create("apps", hasAppCritical ? "CRITICAL" : "WARNING", LocalizationService.Get("MdlAlertAppsTitle", "Neki program nije instaliran"), detail, "apps"));
            }

            // 5. Hardverski testovi (HwTest) i Stres test
            bool hwTestAlert = _criticalStressFailure;
            bool hwTestCrit = _criticalStressFailure;
            if (_isHwTestFinished && !string.IsNullOrWhiteSpace(_hwTestOverallStatus))
            {
                if (_hwTestOverallStatus.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hwTestAlert = true;
                    hwTestCrit = true;
                }
                else if (_hwTestOverallStatus.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hwTestAlert = true;
                }
            }
            if (hwTestAlert)
            {
                string detail = !string.IsNullOrWhiteSpace(_hwTestSummaryText)
                    ? _hwTestSummaryText
                    : (!string.IsNullOrWhiteSpace(_thermalVerdictTitle) ? _thermalVerdictTitle : LocalizationService.Get("MdlAlertHwTestFallback", "Proverite rezultate hardverskih testova."));
                items.Add(OverviewAlertItem.Create("hwtest", hwTestCrit ? "CRITICAL" : "WARNING", LocalizationService.Get("MdlAlertHwTestTitle", "Hardverski test prijavio problem"), detail, "hwtest"));
            }

            // 6. Baterija i punjač
            if (_hasBatteryLive && _isOnAcPower && _isBatteryDischarging && _batteryRateWatts >= 3.0)
            {
                items.Add(OverviewAlertItem.Create(
                    "weak_charger",
                    "WARNING",
                    LocalizationService.Get("MdlAlertWeakChargerTitle", "Slab punjač — baterija se prazni"),
                    string.Format(LocalizationService.Get("MdlAlertWeakChargerDetail", "Punjač priključen, a baterija gubi -{0:0.0} W ({1}%)"), _batteryRateWatts, _batteryChargePercent),
                    "hardware"));
            }
            if (_hasBatteryLive && !_isOnAcPower && _batteryChargePercent > 0 && _batteryChargePercent <= 20)
            {
                items.Add(OverviewAlertItem.Create(
                    "low_battery",
                    "CRITICAL",
                    LocalizationService.Get("MdlAlertLowBatTitle", "Nizak nivo baterije"),
                    string.Format(LocalizationService.Get("MdlAlertLowBatDetail", "Preostalo samo {0}% — priključite punjač!"), _batteryChargePercent),
                    "hardware"));
            }
            if (_isLaptop && _batteryHealth > 0 && _batteryHealth < 55)
            {
                string detail = (HardwareInfo != null && !string.IsNullOrWhiteSpace(HardwareInfo.BatteryWear))
                    ? HardwareInfo.BatteryWear
                    : _batteryHealthText;
                items.Add(OverviewAlertItem.Create("battery_wear", "WARNING", LocalizationService.Get("MdlAlertBatWearTitle", "Visoka istrošenost baterije"), detail, "hardware"));
            }

            // 7. SMART diska
            if ((_diskWear > 0 && _diskWear < 70) || _reallocatedSectors > 0 || _criticalWarnings > 0)
            {
                string sev = ((_diskWear > 0 && _diskWear < 50) || _criticalWarnings > 0) ? "CRITICAL" : "WARNING";
                string detail = string.Format(LocalizationService.Get("MdlAlertSmartDetail", "Zdravlje: {0}% • SMART: {1}"), _diskWear, _smartStatus);
                items.Add(OverviewAlertItem.Create("smart_disk", sev, LocalizationService.Get("MdlAlertSmartTitle", "Upozorenje za zdravlje diska (SMART)"), detail, "hardware"));
            }

            // 8. Ostale greške/upozorenja u koracima faza
            var otherStepNames = new List<string>();
            bool hasOtherStepCrit = false;
            if (Phases != null)
            {
                for (int i = 0; i < Phases.Count; i++)
                {
                    PhaseItem ph = Phases[i];
                    if (ph == null || ph.Steps == null) continue;
                    if (ph.Number == 4) continue; // već obuhvaćeno pod stavkom 4
                    for (int j = 0; j < ph.Steps.Count; j++)
                    {
                        StepItem st = ph.Steps[j];
                        if (st == null || (!st.IsError && !st.IsWarning)) continue;
                        if (ph.Number == 2 && (
                            string.Equals(st.Id, "P2.Pnp", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(st.Id, "P2.Diag", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(st.Id, "P2.Vmd", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        if (st.IsError) hasOtherStepCrit = true;
                        string n = !string.IsNullOrWhiteSpace(st.Title) ? st.Title : st.Id;
                        if (!string.IsNullOrWhiteSpace(n) && !otherStepNames.Contains(n))
                        {
                            otherStepNames.Add(n);
                        }
                    }
                }
            }
            if (otherStepNames.Count > 0)
            {
                items.Add(OverviewAlertItem.Create(
                    "other_steps",
                    hasOtherStepCrit ? "CRITICAL" : "WARNING",
                    LocalizationService.Get("MdlAlertOtherStepsTitle", "Upozorenje u koracima instalacije"),
                    FormatCompactNamesList(otherStepNames, 2),
                    "logs"));
            }

            // Sortiranje: CRITICAL uvek pre WARNING/INFO uz očuvan redosled unutar istog nivoa
            var sorted = new List<OverviewAlertItem>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Severity == "CRITICAL") sorted.Add(items[i]);
            }
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Severity != "CRITICAL") sorted.Add(items[i]);
            }

            var sigSb = new System.Text.StringBuilder();
            bool anyCritical = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                OverviewAlertItem it = sorted[i];
                if (it.Severity == "CRITICAL") anyCritical = true;
                sigSb.Append(it.Key).Append('|').Append(it.Severity).Append('|').Append(it.Title).Append('|').Append(it.Detail).Append(';');
            }
            string sig = sigSb.ToString();
            if (string.Equals(sig, _lastOverviewAlertsSignature, StringComparison.Ordinal))
            {
                return;
            }
            _lastOverviewAlertsSignature = sig;

            Action applyAction = delegate
            {
                OverviewAlerts.Clear();
                for (int i = 0; i < sorted.Count; i++)
                {
                    OverviewAlerts.Add(sorted[i]);
                }

                _hasCriticalOverviewAlerts = anyCritical;

                if (sorted.Count == 0)
                {
                    OverviewAlertsBadgeBrush = CreateFrozenBrush(35, 134, 54);
                    CompletionCardBorderBrush = CreateFrozenBrush(46, 204, 113);
                    CompletionStatusBadgeBrush = CreateFrozenBrush(46, 204, 113);
                    CompletionStatusBadgeForeground = CreateFrozenBrush(18, 18, 20);
                }
                else if (anyCritical)
                {
                    OverviewAlertsBadgeBrush = CreateFrozenBrush(218, 54, 51);
                    CompletionCardBorderBrush = CreateFrozenBrush(231, 76, 60);
                    CompletionStatusBadgeBrush = CreateFrozenBrush(218, 54, 51);
                    CompletionStatusBadgeForeground = Brushes.White;
                }
                else
                {
                    OverviewAlertsBadgeBrush = CreateFrozenBrush(210, 153, 34);
                    CompletionCardBorderBrush = CreateFrozenBrush(210, 153, 34);
                    CompletionStatusBadgeBrush = CreateFrozenBrush(210, 153, 34);
                    CompletionStatusBadgeForeground = CreateFrozenBrush(18, 18, 20);
                }

                OnPropertyChanged("HasOverviewAlerts");
                OnPropertyChanged("ShowNoOverviewAlerts");
                OnPropertyChanged("OverviewAlertsCountText");
                OnPropertyChanged("CompletionStatusBadgeText");
                OnPropertyChanged("CompletionAlertsSummaryText");
            };

            var appInst = Application.Current;
            if (appInst != null && appInst.Dispatcher != null && !appInst.Dispatcher.CheckAccess())
            {
                appInst.Dispatcher.BeginInvoke(applyAction);
            }
            else
            {
                applyAction();
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
            UpdateLiveTimersAndEta();
        }

        public void TickDuration()
        {
            _elapsedDuration = _elapsedDuration.Add(TimeSpan.FromSeconds(1));
            DurationString = string.Format("{0:00}:{1:00}:{2:00}", (int)_elapsedDuration.TotalHours, _elapsedDuration.Minutes, _elapsedDuration.Seconds);
            UpdateLiveTimersAndEta();
            RebuildOverviewAlerts();
        }
    }

    // Alias klase radi potpune kompatibilnosti sa specifikacijom
    public class MainWindowViewModel : MainViewModel
    {
    }
}
