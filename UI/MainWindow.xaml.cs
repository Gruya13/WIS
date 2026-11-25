using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WIS.UI
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; private set; }

        private TelemetryService _telemetryService;
        private readonly DispatcherTimer _durationTimer;
        private readonly object _stdoutLock = new object();

        private ThermalSensorEngine _thermalEngine;
        private System.Timers.Timer _thermalTimer;
        private int _thermalUpdateInFlight = 0;
        private volatile bool _postRebootFinalizePending = false;
        private DateTime _lastDriverStageFileWriteUtc = DateTime.MinValue;
        private CancellationTokenSource _stressCts;

        private const int GraphHistoryLength = 300;
        private readonly float[] _cpuHistory = new float[GraphHistoryLength];
        private readonly float[] _gpuHistory = new float[GraphHistoryLength];
        private readonly float[] _mbHistory = new float[GraphHistoryLength];
        private readonly float[] _ssdHistory = new float[GraphHistoryLength];
        private readonly float[] _throttleHistory = new float[GraphHistoryLength];
        private readonly float[] _cpuLoadHistory = new float[GraphHistoryLength];
        private bool _graphBuffersInitialized = false;
        private readonly object _graphLock = new object();
        private string _initialStartTimeIso = null;

        private System.Timers.Timer _diagMetricsTimer;
        private ulong _prevIdleTime = 0;
        private ulong _prevKernelTime = 0;
        private ulong _prevUserTime = 0;
        private bool _hasPrevCpuTimes = false;
        private long _prevNetBytes = -1;
        private long _prevNetSentBytes = -1;
        private long _accumulatedNetBytes = 0;
        private int _diagNetCounter = 2;
        private int _diagPowerCounter = 2;
        private int _diagSysCounter = 4;
        private int _diagMetricsGuard = 0;
        private int _nasPingCounter = 0;
        private bool _isNasPingRunning = false;
        private bool _diskCountersInitAttempted = false;
        private System.Diagnostics.PerformanceCounter _diskReadCounter = null;
        private System.Diagnostics.PerformanceCounter _diskWriteCounter = null;
        private System.Diagnostics.PerformanceCounter _diskTimeCounter = null;

        public RelayCommand PauseCommand { get; private set; }
        public RelayCommand SkipCommand { get; private set; }
        public RelayCommand DelayRestartCommand { get; private set; }
        public RelayCommand CancelShutdownCommand { get; private set; }
        public RelayCommand ShutdownNowCommand { get; private set; }
        public RelayCommand StartServiceCommand { get; private set; }
        public RelayCommand CleanAndShutdownCommand { get; private set; }
        public RelayCommand ExitWithoutCleanCommand { get; private set; }
        public RelayCommand ShowCompletionSummaryCommand { get; private set; }
        public RelayCommand RerunPhase4Command { get; private set; }
        public RelayCommand RetryAppCommand { get; private set; }

        private bool _isMasterMode = false;
        private DispatcherTimer _autoStartTimer;
        private int _autoStartCountdown = 15;
        private bool _taskRunnerStarted = false;
        private Process _currentPhaseProcess = null;
        private int _failedPhase = 0;
        private int _phase3AutoRetryCount = 0;
        private string _lastProcessedLog = "";

        private int _lastStressActualDurationSec = 0;
        private float _lastStressMaxTemp = 0.0f;
        private float _lastStressMaxThrottling = 0.0f;
        private bool _lastStressEmergencyAbort = false;
        private bool _lastStressManualCancel = false;
        private bool _lastStressWasExecuted = false;
        private bool _criticalStressFailure = false;
        private string _thermalOverheatNote = "";
        private string _lastStressStatus = "UNKNOWN";
        private string _lastStressReason = "NOT_RUN";

        // Nalaz 14 (Paket C2): Realno odlaganje restarta i perzistencija u deploy-state.json
        private readonly object _restartScheduleLock = new object();
        private bool _restartPending = false;
        private DateTime _restartDueAtUtc = DateTime.MinValue;
        private int _delayedRestartMinutesTotal = 0;

        // U2 (Paket C2): C# Master vlasništvo nad životnim ciklusom Screen Capture worker procesa
        private readonly object _screenCaptureLock = new object();
        private Process _screenCaptureProcess = null;
        private DateTime _lastScreenWorkerCheckUtc = DateTime.MinValue;
        private DateTime _lastBgWuFileWriteUtc = DateTime.MinValue;
        private DeviceChangeMonitor _deviceMonitor = null;
        private bool _autopilotRetriedAfterNetwork = false;
        private readonly object _appRetryLock = new object();
        private bool _isSingleAppRetryRunning = false;
        private bool _pendingPostRetryShortcutsRefresh = false;
        private bool _pendingPostRetryLicenseRefresh = false;
        private volatile bool _phase6ScriptCompletedSuccessfully = false;
        private volatile int _bgWuPendingDriverCount = 0;
        private volatile bool _bgWuInstallingDriver = false;
        private volatile bool _phase6AllowMissingAppsNeedsAttention = false;

        // BUG-02: Čuvar maksimizovanog stanja glavnog prozora uz dozvoljen Minimize
        private bool _isEnforcingMaximize = false;
        private bool _pendingForceRelayout = false;
        private DispatcherTimer _maximizeDebounceTimer = null;
        private System.Windows.Interop.HwndSource _windowHwndSource = null;
        private System.Windows.Interop.HwndSourceHook _windowHwndHook = null;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int SPI_SETWORKAREA = 0x002F;

        // BUG-03: Kontekstualna traka za oporavak kada je neka faza pala (_failedPhase > 0)
        private System.Windows.Controls.StackPanel _failedPhaseRecoveryPanel = null;
        private System.Windows.Controls.Button _btnFailedPhaseRecheck = null;
        private System.Windows.Controls.Button _btnFailedPhaseRetry = null;
        private System.Windows.Controls.Button _btnFailedPhaseSkip = null;
        private bool _recoveryBarEventsWired = false;

        // V343-03 (Odluke 3A & 4A): Modalni overlay i gornja traka za obavezne interaktivne testove
        private System.Windows.Controls.Border _pendingInteractiveOverlay = null;
        private System.Windows.Controls.Border _pendingInteractiveBanner = null;
        private System.Windows.Controls.TextBlock _pendingOverlayBadgeTb = null;
        private System.Windows.Controls.TextBlock _pendingOverlayTitleTb = null;
        private System.Windows.Controls.TextBlock _pendingOverlaySubtitleTb = null;
        private System.Windows.Controls.TextBlock _pendingOverlayCountTb = null;
        private System.Windows.Controls.TextBlock _pendingOverlayListTb = null;
        private System.Windows.Controls.Button _btnPendingStartTests = null;
        private System.Windows.Controls.Button _btnPendingBrowseTabs = null;
        private System.Windows.Controls.Button _btnPendingEmergencySkip = null;
        private System.Windows.Controls.TextBlock _pendingBannerTitleTb = null;
        private System.Windows.Controls.TextBlock _pendingBannerDetailTb = null;
        private System.Windows.Controls.Button _btnBannerResumeTests = null;
        private System.Windows.Controls.Button _btnBannerEmergencySkip = null;
        private bool _pendingInteractiveUiEventsWired = false;
        private List<string> _lastPendingInteractiveNames = new List<string>();

        public MainWindow()
        {
            InitializeComponent();

            ViewModel = new MainViewModel();

            // Inicijalizacija komandi i povezivanje na ViewModel
            PauseCommand = new RelayCommand(ExecutePause);
            SkipCommand = new RelayCommand(ExecuteSkip);
            DelayRestartCommand = new RelayCommand(ExecuteDelayRestart);
            CancelShutdownCommand = new RelayCommand(ExecuteCancelShutdown);
            ShutdownNowCommand = new RelayCommand(ExecuteShutdownNow);
            StartServiceCommand = new RelayCommand(ExecuteStartService);
            CleanAndShutdownCommand = new RelayCommand(ExecuteCleanAndShutdown);
            ExitWithoutCleanCommand = new RelayCommand(ExecuteExitWithoutClean);
            ShowCompletionSummaryCommand = new RelayCommand(ExecuteShowCompletionSummary);
            RerunPhase4Command = new RelayCommand(ExecuteRerunPhase4);
            RetryAppCommand = new RelayCommand(ExecuteRetryApp);

            ViewModel.PauseCommand = PauseCommand;
            ViewModel.SkipCommand = SkipCommand;
            ViewModel.DelayRestartCommand = DelayRestartCommand;
            ViewModel.CancelShutdownCommand = CancelShutdownCommand;
            ViewModel.ShutdownNowCommand = ShutdownNowCommand;
            ViewModel.StartServiceCommand = StartServiceCommand;
            ViewModel.CleanAndShutdownCommand = CleanAndShutdownCommand;
            ViewModel.ExitWithoutCleanCommand = ExitWithoutCleanCommand;
            ViewModel.ShowCompletionSummaryCommand = ShowCompletionSummaryCommand;
            ViewModel.RerunPhase4Command = RerunPhase4Command;
            ViewModel.RetryAppCommand = RetryAppCommand;
            ViewModel.Autopilot.RescanCommand = new RelayCommand(ExecuteAutopilotRescan);
            ViewModel.Drivers.RefreshDriversCommand = new RelayCommand(ExecuteRefreshDrivers);
            ViewModel.Drivers.OpenDeviceManagerCommand = new RelayCommand(ExecuteOpenDeviceManager);
            ViewModel.Drivers.CopyHwidCommand = new RelayCommand(ExecuteCopyHwid);
            ViewModel.Drivers.InstallDriverCommand = new RelayCommand(ExecuteInstallDriver);
            ViewModel.Drivers.ResolveAllDriversCommand = new RelayCommand(ExecuteResolveAllDrivers);
            ViewModel.Drivers.CheckKeyDriversCommand = new RelayCommand(ExecuteCheckKeyDrivers);
            ViewModel.RefreshHardwareCommand = new RelayCommand(ExecuteRefreshHardware);
            ViewModel.RunThermalStressCommand = new RelayCommand(_ => StartThermalStressTest(), _ => !ViewModel.IsStressTestRunning);
            ViewModel.CancelThermalStressCommand = new RelayCommand(_ => CancelThermalStressTest(), _ => ViewModel.IsStressTestRunning);
            ViewModel.ResetCoreClocksCommand = new RelayCommand(_ => ResetCoreClockExtremes());
            ViewModel.RunHwTestCommand = new RelayCommand(ExecuteRunHwTest);

            DataContext = ViewModel;
            // BUG-02: Osiguraj CanMinimize i kod starog BAML-a (dozvoljen Minimize u taskbar, zabranjeno ručno smanjivanje na 880x640)
            this.ResizeMode = ResizeMode.CanMinimize;

            EnsureBackgroundWuVisualElements();
            EnsurePendingInteractiveTestsUi();
            EnsureFailedPhaseRecoveryBar();
            UpdateFailedPhaseRecoveryBar();
            Loaded += (s, e) => ApplyCompactViewportLayoutIfNeeded();
            SizeChanged += (s, e) => ApplyCompactViewportLayoutIfNeeded();

            // BUG-02: Čuvar maksimizovanog stanja pri promeni rezolucije, DPI-ja ili povratku iz Normal stanja
            _maximizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _maximizeDebounceTimer.Tick += OnMaximizeDebounceTimerTick;
            Loaded += (s, e) => EnsureWindowMaximized(false);
            StateChanged += MainWindow_StateChanged;
            Activated += MainWindow_Activated;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;

            // WIS-TASK-188: [Printer] konfiguracija se čita odmah (pre brisanja Wifi-Servis.ini u Fazi 6)
            try { LabelPrinterConfig.Preload(); } catch { }
            try { WIS.HwTest.Interop.NativeMethods.SuppressLocationPromptPopups(); } catch { }
            EnsureCompletionOverlayResponsiveLayout();
            EnsureLabelPrintButtons();
            EnsureHwTestButtons();
            EnsureEmbeddedHwTestTab();

            // Inicijalizacija termalnog senzorskog motora (v2.15.0 - WIS-TASK-131)
            try
            {
                _thermalEngine = new ThermalSensorEngine();
            }
            catch { }

            _thermalTimer = new System.Timers.Timer(1000);
            _thermalTimer.AutoReset = true;
            _thermalTimer.Elapsed += (s, e) => RunThermalUpdateBackground();
            _thermalTimer.Start();
            ThreadPool.QueueUserWorkItem(delegate { RunThermalUpdateBackground(); });

            // Reaktivno praćenje promene jezika (v2.14.0 - I18N-01)
            LocalizationService.LanguageChanged += OnLanguageChanged;

            // Inicijalizacija i pokretanje ServisHub Telemetrije
            _telemetryService = new TelemetryService(ViewModel, Dispatcher);
            _telemetryService.Start();
            if (!string.IsNullOrEmpty(_telemetryService.HubUrl))
            {
                ViewModel.UpdateDeviceCardHubUrl(_telemetryService.HubUrl);
            }

            Closing += Window_Closing;
            Closed += (s, e) =>
            {
                LocalizationService.LanguageChanged -= OnLanguageChanged;
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged; } catch { }

                if (_maximizeDebounceTimer != null)
                {
                    _maximizeDebounceTimer.Stop();
                    _maximizeDebounceTimer.Tick -= OnMaximizeDebounceTimerTick;
                    _maximizeDebounceTimer = null;
                }
                if (_windowHwndSource != null && _windowHwndHook != null)
                {
                    try { _windowHwndSource.RemoveHook(_windowHwndHook); } catch { }
                    _windowHwndHook = null;
                    _windowHwndSource = null;
                }

                StopScreenCaptureWorker();

                if (_telemetryService != null)
                {
                    _telemetryService.Dispose();
                    _telemetryService = null;
                }
                if (_thermalEngine != null)
                {
                    _thermalEngine.Dispose();
                    _thermalEngine = null;
                }
                if (_diagMetricsTimer != null)
                {
                    _diagMetricsTimer.Stop();
                    _diagMetricsTimer.Dispose();
                    _diagMetricsTimer = null;
                }
                if (_deviceMonitor != null)
                {
                    _deviceMonitor.Dispose();
                    _deviceMonitor = null;
                }
            };

            Loaded += (s, e) =>
            {
                StartMetricsTimer();
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    IntPtr hwnd = helper.Handle;
                    _deviceMonitor = new DeviceChangeMonitor(this, ViewModel);
                    _deviceMonitor.Start(hwnd);
                }
                catch (Exception ex)
                {
                    ViewModel.AddLog("WARN", "Hardware", "Nije moguće pokrenuti DeviceChangeMonitor: " + ex.Message);
                }
            };

            // Inicijalizacija kumulativnog tajmera preko sesija (PRD 3.5)
            InitializeCumulativeTimer();

            // Timer za trajanje (Duration) i odbrojavanje gašenja (svake 1s)
            _durationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _durationTimer.Tick += OnDurationTick;
            _durationTimer.Start();

            // Povezivanje AutoScroll događaja (WIS-TASK-181: Najnoviji zapisi na vrhu)
            ViewModel.AllLogs.CollectionChanged += (s, e) =>
            {
                if (LogListView == null) return;

                if (ViewModel.AutoScroll)
                {
                    if (ViewModel.AllLogs.Count > 0)
                    {
                        LogListView.ScrollIntoView(ViewModel.AllLogs[0]);
                    }
                }
                else
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
                    {
                        ScrollViewer sv = FindVisualChild<ScrollViewer>(LogListView);
                        if (sv != null && sv.VerticalOffset > 0)
                        {
                            int count = e.NewItems != null ? e.NewItems.Count : 1;
                            sv.ScrollToVerticalOffset(sv.VerticalOffset + count);
                        }
                    }
                }
            };

            // Provera argumenata komandne linije (Dual-Mode: Master Task Runner vs Slave IPC)
            string[] args = Environment.GetCommandLineArgs();
            bool hasSlaveOrSession = false;
            bool hasMasterOrFirstLogon = false;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a.Contains("--slave") || a.Contains("--session"))
                {
                    hasSlaveOrSession = true;
                }
                if (a.Contains("--master") || a.Contains("--firstlogon"))
                {
                    hasMasterOrFirstLogon = true;
                }
            }

            // Uvek pokreni tolerantni IPC listener u pozadini
            StartIpcListener();

            if (!hasSlaveOrSession && (args.Length <= 1 || hasMasterOrFirstLogon))
            {
                _isMasterMode = true;
                ViewModel.AddLog("INFO", "Sistem", "WIS Korisnički interfejs uspešno pokrenut u MASTER (Task Runner) režimu.");
                ViewModel.AddLog("INFO", "Sistem", "Mrežna veza i komunikacija sa ServisHub-om već verifikovani od strane Launchera.");

                // U2: Pokreni pozadinski Screen Capture worker u vlasništvu C# Master procesa
                ThreadPool.QueueUserWorkItem(delegate
                {
                    StartScreenCaptureWorkerIfNeeded();
                });

                // Provera deploy-state.json za automatski nastavak nakon međurestarta
                int savedPhase = GetSavedDeployStatePhase();
                if (savedPhase >= 4 && savedPhase <= 6)
                {
                    if (savedPhase >= 5)
                    {
                        HydrateAppsStateOnResume();
                    }
                    ViewModel.IsServiceReadyVisible = false;
                    ViewModel.AddLog("INFO", "Sistem", string.Format("Detektovano stanje nakon međurestarta: automatski nastavljam Fazu {0}...", savedPhase));
                    ViewModel.CurrentPhase = savedPhase;
                    StartPhaseExecution(savedPhase);
                }
                else
                {
                    ViewModel.IsServiceReadyVisible = false;
                    ViewModel.AddLog("INFO", "Sistem", "Pokrećem automatsko izvršavanje Faze 2...");
                    StartPhaseExecution(2);
                }
            }
            else
            {
                _isMasterMode = false;
                ViewModel.IsServiceReadyVisible = false;
                ViewModel.AddLog("INFO", "Sistem", "WIS Korisnički interfejs pokrenut u SLAVE (IPC Listener) režimu.");
                ViewModel.AddLog("INFO", "IPC", "Čekanje na JSON telemetriju sa standardnog ulaza...");
            }

            // Automatsko pokretanje bezbednosne provere Microsoft Autopilot statusa u pozadini (Instant start)
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    StartAutopilotScan();
                }
                catch { }
            });

            // Win32 prevencija Sleep-a i gašenja ekrana na AC napajanju (kernel32.dll)
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch { }

            // Asinhrona detekcija hardvera za tab 'Informacije' (v2.7.1)
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DetectHardware();
                }
                catch { }
            });

            // Asinhrona detekcija drajvera za tab 'Drajveri' (v2.7.3)
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DetectDrivers();
                }
                catch { }
            });
        }

        private void OnLanguageChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshDynamicLocalizations();
            }
            else
            {
                Dispatcher.BeginInvoke((Action)RefreshDynamicLocalizations);
            }
        }

        private void RefreshDynamicLocalizations()
        {
            if (ViewModel == null) return;
            ViewModel.RefreshLocalization();
            RefreshPendingInteractiveTestsLocalization();
        }

        #region DWM Immersive Dark Title Bar

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            EnableDarkTitleBar();
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                if (helper.Handle != IntPtr.Zero)
                {
                    _windowHwndSource = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
                    if (_windowHwndSource != null)
                    {
                        _windowHwndHook = new System.Windows.Interop.HwndSourceHook(WindowMaximizedGuardHook);
                        _windowHwndSource.AddHook(_windowHwndHook);
                    }
                }
            }
            catch { }
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch { }
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1;
                // Windows 10 (1903+) & Windows 11 DWMWA_USE_IMMERSIVE_DARK_MODE (20)
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
                // Fallback za Windows 10 (1809) DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 (19)
                DwmSetWindowAttribute(hwnd, 19, ref darkMode, sizeof(int));

                // Windows 11 (Build 22000+) DWMWA_CAPTION_COLOR (35) - BGR format: 0x00262525 za #252526
                int captionColor = 0x00262525;
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                // Windows 11 DWMWA_TEXT_COLOR (36) - beli tekst
                int textColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SM_DIGITIZER = 94;
        public const int SM_MAXIMUMTOUCHES = 95;

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToUInt64()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        #endregion

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) return (T)child;
                T result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void OnDurationTick(object sender, EventArgs e)
        {
            ViewModel.TickDuration();
            PollBackgroundWuStatusFile();

            if (ViewModel.ShutdownTimerActive && !ViewModel.CriticalStressFailure)
            {
                if (ViewModel.ShutdownCountdown > 0)
                {
                    ViewModel.ShutdownCountdown--;
                }
                else
                {
                    ViewModel.ShutdownTimerActive = false;
                    DateTime dueUtc = GetScheduledRestartDueUtc();
                    if (dueUtc <= DateTime.UtcNow)
                    {
                        ExecuteShutdownNow(null);
                    }
                }
            }

            if (_isMasterMode &&
                !_criticalStressFailure &&
                !ViewModel.IsServiceFinished &&
                ViewModel.CurrentPhase >= 1 &&
                ViewModel.CurrentPhase <= 5 &&
                (DateTime.UtcNow - _lastScreenWorkerCheckUtc).TotalSeconds >= 5.0)
            {
                _lastScreenWorkerCheckUtc = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    StartScreenCaptureWorkerIfNeeded();
                });
            }
        }

        private void GetBackgroundWuDriverState(out int pendingDrivers, out bool installingDriver)
        {
            if (_lastBgWuFileWriteUtc == DateTime.MinValue)
            {
                PollBackgroundWuStatusFile();
            }
            pendingDrivers = _bgWuPendingDriverCount;
            installingDriver = _bgWuInstallingDriver;
        }

        private void PollBackgroundWuStatusFile()
        {
            PollDriverStageStatusFile();
            try
            {
                string wuStatusPath = @"C:\setup\wu-bg-status.json";
                if (!File.Exists(wuStatusPath))
                {
                    return;
                }

                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(wuStatusPath);
                if (lastWriteUtc <= _lastBgWuFileWriteUtc)
                {
                    return;
                }

                string json = File.ReadAllText(wuStatusPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var root = JsonHelper.Parse(json);
                if (root == null || root.IsNull)
                {
                    return;
                }

                _lastBgWuFileWriteUtc = lastWriteUtc;

                string status = root.ContainsKey("status") ? root["status"].AsString("RUNNING") : "RUNNING";
                string textSr = root.ContainsKey("phase_text") ? root["phase_text"].AsString("") : "";
                string textZh = root.ContainsKey("phase_text_zh") ? root["phase_text_zh"].AsString("") : "";
                int foundCount = root.ContainsKey("found_count") ? root["found_count"].AsInt(0) : 0;
                int downloadedCount = root.ContainsKey("downloaded_count") ? root["downloaded_count"].AsInt(0) : 0;
                int installedCount = root.ContainsKey("installed_count") ? root["installed_count"].AsInt(0) : 0;
                int pendingDriverCount = root.ContainsKey("pending_driver_count") ? root["pending_driver_count"].AsInt(0) : 0;
                bool installingDriver = root.ContainsKey("installing_driver") ? root["installing_driver"].AsBool(false) : false;

                string normStatus = (status ?? "RUNNING").Trim().ToUpperInvariant();
                if (normStatus == "COMPLETED" || normStatus == "SKIPPED")
                {
                    pendingDriverCount = 0;
                    installingDriver = false;
                }

                bool prevHadDrivers = (_bgWuPendingDriverCount > 0 || _bgWuInstallingDriver);
                _bgWuPendingDriverCount = Math.Max(0, pendingDriverCount);
                _bgWuInstallingDriver = installingDriver;
                bool nowHasDrivers = (_bgWuPendingDriverCount > 0 || _bgWuInstallingDriver);

                int wuPct = -1;
                if (normStatus == "STARTING")
                {
                    textSr = "Pozadinski WU: Pokretanje i priprema kataloga...";
                    textZh = "后台更新: 正在启动并准备更新目录...";
                    wuPct = 5;
                }
                else if (normStatus == "SEARCHING")
                {
                    textSr = "Pozadinski WU: Pretraga kataloga...";
                    textZh = "后台更新: 正在搜索更新目录...";
                    wuPct = -1;
                }
                else if (normStatus == "DOWNLOADING")
                {
                    textSr = string.Format("Pozadinski WU: Preuzimanje ({0}/{1})...", downloadedCount, foundCount);
                    textZh = string.Format("后台更新: 正在下载 ({0}/{1})...", downloadedCount, foundCount);
                    if (foundCount > 0) wuPct = Math.Max(5, (downloadedCount * 50) / foundCount);
                }
                else if (normStatus == "WAITING_MSI")
                {
                    textSr = string.Format("Pozadinski WU: Preuzeto ({0}/{1}) — čeka MSI instalator...", downloadedCount, foundCount);
                    textZh = string.Format("后台更新: 已下载 ({0}/{1}) — 等待 MSI 安装程序...", downloadedCount, foundCount);
                    wuPct = 50;
                }
                else if (normStatus == "INSTALLING")
                {
                    textSr = string.Format("Pozadinski WU: Instalacija ({0}/{1})...", installedCount, foundCount);
                    textZh = string.Format("后台更新: 正在安装 ({0}/{1})...", installedCount, foundCount);
                    if (foundCount > 0) wuPct = Math.Min(95, 50 + (installedCount * 45) / foundCount);
                }
                else if (normStatus == "HARVESTING")
                {
                    textSr = "Pozadinski WU: Arhiviranje novih zakrpa na NAS...";
                    textZh = "后台更新: 正在将新补丁归档到 NAS...";
                    wuPct = 95;
                }
                else if (normStatus == "COMPLETED")
                {
                    wuPct = 100;
                    if (foundCount <= 0)
                    {
                        textSr = "Pozadinski WU: Sistem je ažuran (0 zakrpa)";
                        textZh = "后台更新: 系统已是最新 (0 个补丁)";
                    }
                    else
                    {
                        textSr = string.Format("Pozadinski WU: Završeno ({0}/{1} instalirano)", installedCount, foundCount);
                        textZh = string.Format("后台更新: 已完成 (已安装 {0}/{1})", installedCount, foundCount);
                    }
                }
                else if (normStatus == "SKIPPED")
                {
                    textSr = "Pozadinski WU: Preskočeno (nema internet veze)";
                    textZh = "后台更新: 已跳过 (无网络连接)";
                    wuPct = 100;
                }

                if (ViewModel != null)
                {
                    ViewModel.UpdateBackgroundWuStatus(status, textSr, textZh, wuPct);
                }

                if (prevHadDrivers != nowHasDrivers || normStatus == "COMPLETED" || normStatus == "SKIPPED")
                {
                    NotifyHwTestDriverStateChanged(!nowHasDrivers);
                }
                else
                {
                    NotifyHwTestDriverStateChanged(false);
                }
            }
            catch { }
        }

        private static DateTime GetSystemBootTimeUtc()
        {
            try
            {
                ulong uptimeMs = GetTickCount64();
                return DateTime.UtcNow.AddMilliseconds(-(double)uptimeMs);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool IsDriverPowerShellWorkerAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p == null || p.HasExited) return false;
                    string pname = p.ProcessName ?? string.Empty;
                    return pname.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           pname.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TryKillPowerShellWorker(int pid)
        {
            if (pid <= 0) return;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p == null || p.HasExited) return;
                    string pname = p.ProcessName ?? string.Empty;
                    if (pname.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pname.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Kill();
                    }
                }
            }
            catch { }
        }

        private static bool IsDriverStageStatusAlive(string normState, int workerPid, string updatedAtIso, DateTime fileWriteUtc)
        {
            if (normState != "SEARCHING" && normState != "DOWNLOADING" && normState != "INSTALLING")
            {
                return true;
            }

            DateTime updatedUtc = DateTime.MinValue;
            DateTime parsed;
            if (!string.IsNullOrWhiteSpace(updatedAtIso) &&
                DateTime.TryParse(updatedAtIso.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
            {
                updatedUtc = (parsed.Kind == DateTimeKind.Utc) ? parsed : parsed.ToUniversalTime();
            }
            if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = fileWriteUtc;
            }

            DateTime bootUtc = GetSystemBootTimeUtc();
            if (bootUtc != DateTime.MinValue && updatedUtc != DateTime.MinValue && updatedUtc < bootUtc.AddSeconds(-5.0))
            {
                return false;
            }

            double ageSec = (updatedUtc != DateTime.MinValue) ? (DateTime.UtcNow - updatedUtc).TotalSeconds : 0.0;
            if (workerPid > 0)
            {
                if (!IsDriverPowerShellWorkerAlive(workerPid) && ageSec >= 5.0)
                {
                    return false;
                }
                return true;
            }

            double maxStaleSec = (normState == "INSTALLING") ? 1800.0 : 1200.0;
            if (updatedUtc == DateTime.MinValue || ageSec > maxStaleSec)
            {
                return false;
            }
            return true;
        }

        private void ForceResetStuckDriverStageStatus(string reason)
        {
            try
            {
                string dir = @"C:\setup";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string statusPath = Path.Combine(dir, "driver-stage-status.json");
                string tmpPath = Path.Combine(dir, "driver-stage-status.tmp");
                var payload = new Dictionary<string, object>
                {
                    { "state", "FAILED" },
                    { "worker_pid", 0 },
                    { "hub_staged_count", 0 },
                    { "wu_downloaded_count", 0 },
                    { "total_count", 0 },
                    { "current_item", reason ?? "Prekinuto / zastarelo" },
                    { "updated_at_utc", DateTime.UtcNow.ToString("o") }
                };
                string json = JsonHelper.Serialize(payload);
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                if (File.Exists(statusPath)) File.Delete(statusPath);
                File.Move(tmpPath, statusPath);
                try { _lastDriverStageFileWriteUtc = File.GetLastWriteTimeUtc(statusPath); } catch { }
            }
            catch
            {
                try
                {
                    var fallbackPayload = new Dictionary<string, object>
                    {
                        { "state", "FAILED" },
                        { "worker_pid", 0 },
                        { "current_item", reason ?? "Prekinuto / zastarelo" },
                        { "updated_at_utc", DateTime.UtcNow.ToString("o") }
                    };
                    File.WriteAllText(@"C:\setup\driver-stage-status.json", JsonHelper.Serialize(fallbackPayload), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private void PollDriverStageStatusFile()
        {
            try
            {
                string stagePath = @"C:\setup\driver-stage-status.json";
                if (!File.Exists(stagePath)) return;

                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(stagePath);
                if (lastWriteUtc <= _lastDriverStageFileWriteUtc) return;

                string json = File.ReadAllText(stagePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                var root = JsonHelper.Parse(json);
                if (root == null || root.IsNull) return;

                _lastDriverStageFileWriteUtc = lastWriteUtc;

                string state = root.ContainsKey("state") ? root["state"].AsString("IDLE") : "IDLE";
                string normState = (state ?? "IDLE").Trim().ToUpperInvariant();
                int workerPid = root.ContainsKey("worker_pid") ? root["worker_pid"].AsInt(0) : 0;
                string updatedAtIso = root.ContainsKey("updated_at_utc") ? root["updated_at_utc"].AsString("") : "";
                int hubStaged = root.ContainsKey("hub_staged_count") ? root["hub_staged_count"].AsInt(0) : 0;
                int wuDownloaded = root.ContainsKey("wu_downloaded_count") ? root["wu_downloaded_count"].AsInt(0) : 0;
                int totalCount = root.ContainsKey("total_count") ? root["total_count"].AsInt(0) : 0;
                string currentItem = root.ContainsKey("current_item") ? root["current_item"].AsString("") : "";

                if (!IsDriverStageStatusAlive(normState, workerPid, updatedAtIso, lastWriteUtc))
                {
                    ForceResetStuckDriverStageStatus("Zastareo/prekinut pozadinski driver status");
                    normState = "FAILED";
                }

                int doneCount = hubStaged + wuDownloaded;
                int pct = -1;
                string textSr = "";
                string textZh = "";

                if (normState == "IDLE")
                {
                    return;
                }
                else if (normState == "SEARCHING")
                {
                    textSr = string.IsNullOrWhiteSpace(currentItem)
                        ? "Pozadinski drajveri: Pretraga ServiceHub / Windows Update kataloga..."
                        : string.Format("Pozadinski drajveri: Pretraga ({0})...", currentItem);
                    textZh = string.IsNullOrWhiteSpace(currentItem)
                        ? "后台驱动: 正在搜索 ServiceHub / Windows Update 目录..."
                        : string.Format("后台驱动: 正在搜索 ({0})...", currentItem);
                    pct = 10;
                }
                else if (normState == "DOWNLOADING")
                {
                    textSr = totalCount > 0
                        ? string.Format("Pozadinski drajveri: Preuzimanje ({0}/{1}){2}...", doneCount, totalCount, string.IsNullOrWhiteSpace(currentItem) ? "" : " — " + currentItem)
                        : string.Format("Pozadinski drajveri: Preuzimanje ({0}){1}...", doneCount, string.IsNullOrWhiteSpace(currentItem) ? "" : " — " + currentItem);
                    textZh = totalCount > 0
                        ? string.Format("后台驱动: 正在下载 ({0}/{1}){2}...", doneCount, totalCount, string.IsNullOrWhiteSpace(currentItem) ? "" : " — " + currentItem)
                        : string.Format("后台驱动: 正在下载 ({0}){1}...", doneCount, string.IsNullOrWhiteSpace(currentItem) ? "" : " — " + currentItem);
                    pct = (totalCount > 0) ? Math.Min(90, Math.Max(15, (doneCount * 90) / totalCount)) : 35;
                }
                else if (normState == "READY_TO_INSTALL")
                {
                    textSr = string.Format("Pozadinski drajveri: Spremno za instalaciju ({0} paketa)", doneCount);
                    textZh = string.Format("后台驱动: 准备安装 ({0} 个驱动包)", doneCount);
                    pct = 95;
                }
                else if (normState == "NONE_NEEDED")
                {
                    textSr = "Pozadinski drajveri: Nisu potrebni dodatni drajveri";
                    textZh = "后台驱动: 无需额外驱动";
                    pct = 100;
                }
                else if (normState == "INSTALLING")
                {
                    textSr = string.IsNullOrWhiteSpace(currentItem)
                        ? "Instalacija preuzetih drajvera (Faza 4B)..."
                        : string.Format("Instalacija drajvera (4B): {0}...", currentItem);
                    textZh = string.IsNullOrWhiteSpace(currentItem)
                        ? "正在安装已下载的驱动 (阶段 4B)..."
                        : string.Format("正在安装驱动 (4B): {0}...", currentItem);
                    pct = 80;
                }
                else if (normState == "DONE")
                {
                    textSr = string.Format("Drajveri spremni (preuzeto/instalirano: {0})", doneCount);
                    textZh = string.Format("驱动已就绪 (已下载/安装: {0})", doneCount);
                    pct = 100;
                }
                else if (normState == "FAILED")
                {
                    textSr = "Pozadinski drajveri: Preuzimanje završeno uz upozorenje";
                    textZh = "后台驱动: 下载完成但有警告";
                    pct = 100;
                }

                if (ViewModel != null && !string.IsNullOrEmpty(textSr))
                {
                    ViewModel.UpdateBackgroundWuStatus(normState, textSr, textZh, pct);
                }
            }
            catch { }
        }

        // =====================================================================
        // WIS-TASK-188: Dugme „Štampaj nalepnicu“ u završnom prozoru i gornjoj
        // traci. Ubrizgava se iz koda (kao EnsureBackgroundWuVisualElements),
        // jer Linux Build-UI.py ponovo koristi postojeći BAML i ne prevodi XAML.
        // Dugme se umeće ispred svakog „Očisti i ugasi“ dugmeta, pa je vidljivo
        // isključivo kada je servis završen (roditeljski panel je vezan za
        // IsCompleted / IsServiceFinished).
        // =====================================================================
        private const string LabelPrintButtonTag = "WisLabelPrintButton";
        private bool _completionLayoutAdapted = false;

        private void EnsureLabelPrintButtons()
        {
            EnsureCompletionOverlayResponsiveLayout();
            try
            {
                if (!LabelPrinterConfig.Current.Enabled) return; // [Printer] Enabled=0 isključuje funkciju

                List<Button> anchors = new List<Button>();
                CollectButtonsByCommandPath(this, "CleanAndShutdownCommand", anchors);

                foreach (Button anchor in anchors)
                {
                    Panel parent = anchor.Parent as Panel;
                    if (parent == null) continue;

                    bool exists = false;
                    foreach (object child in parent.Children)
                    {
                        FrameworkElement fe = child as FrameworkElement;
                        if (fe != null && string.Equals(Convert.ToString(fe.Tag), LabelPrintButtonTag, StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (exists) continue;

                    bool isLarge = anchor.FontSize >= 13;
                    Button printBtn = new Button
                    {
                        Tag = LabelPrintButtonTag,
                        Background = MainViewModel.CreateFrozenBrush(31, 111, 235),
                        Foreground = Brushes.White,
                        BorderBrush = MainViewModel.CreateFrozenBrush(56, 139, 253),
                        BorderThickness = new Thickness(1),
                        FontWeight = FontWeights.Bold,
                        FontSize = isLarge ? 13 : 11,
                        Padding = isLarge ? new Thickness(16, 10, 16, 10) : new Thickness(14, 6, 14, 6),
                        Margin = isLarge ? new Thickness(6, 5, 6, 5) : new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    printBtn.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Loc[BtnPrintLabel]"));
                    printBtn.ToolTip = LocalizationService.Get("TooltipPrintLabel", "Štampa nalepnice sa specifikacijom uređaja na USB štampaču Star TSP700II");
                    printBtn.Click += (s, e) => ShowLabelPrintDialog();

                    int idx = parent.Children.IndexOf(anchor);
                    parent.Children.Insert(idx < 0 ? 0 : idx, printBtn);
                }
            }
            catch (Exception ex)
            {
                try { ViewModel.AddLog("WARN", "LabelPrint", "Dugme za štampu nalepnice nije ubačeno: " + ex.Message); } catch { }
            }
        }

        private static void CollectButtonsByCommandPath(DependencyObject node, string commandPath, List<Button> result)
        {
            if (node == null) return;
            Button btn = node as Button;
            if (btn != null)
            {
                System.Windows.Data.BindingExpression be = System.Windows.Data.BindingOperations.GetBindingExpression(btn, Button.CommandProperty);
                if (be != null && be.ParentBinding != null && be.ParentBinding.Path != null &&
                    string.Equals(be.ParentBinding.Path.Path, commandPath, StringComparison.Ordinal))
                {
                    result.Add(btn);
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null) CollectButtonsByCommandPath(dep, commandPath, result);
            }
        }

        private void ShowLabelPrintDialog()
        {
            try
            {
                LabelPrintWindow dlg = new LabelPrintWindow(this, ViewModel);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "LabelPrint", "Greška pri otvaranju dijaloga za štampu nalepnice: " + ex.Message);
            }
        }

        // =====================================================================
        // WIS-TASK-189: Dugme „Hardverski test“ u završnom prozoru i traci
        // =====================================================================
        private const string HwTestButtonTag = "WisHwTestButton";

        private void EnsureHwTestButtons()
        {
            EnsureCompletionOverlayResponsiveLayout();
            try
            {
                List<Button> anchors = new List<Button>();
                CollectButtonsByCommandPath(this, "CleanAndShutdownCommand", anchors);

                foreach (Button anchor in anchors)
                {
                    Panel parent = anchor.Parent as Panel;
                    if (parent == null) continue;

                    bool exists = false;
                    foreach (object child in parent.Children)
                    {
                        FrameworkElement fe = child as FrameworkElement;
                        if (fe != null && string.Equals(Convert.ToString(fe.Tag), HwTestButtonTag, StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (exists) continue;

                    bool isLarge = anchor.FontSize >= 13;
                    Button hwBtn = new Button
                    {
                        Tag = HwTestButtonTag,
                        Background = MainViewModel.CreateFrozenBrush(108, 117, 125),
                        Foreground = Brushes.White,
                        BorderBrush = MainViewModel.CreateFrozenBrush(140, 145, 155),
                        BorderThickness = new Thickness(1),
                        FontWeight = FontWeights.Bold,
                        FontSize = isLarge ? 13 : 11,
                        Padding = isLarge ? new Thickness(16, 10, 16, 10) : new Thickness(14, 6, 14, 6),
                        Margin = isLarge ? new Thickness(6, 5, 6, 5) : new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    hwBtn.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Loc[BtnRunHwTest]"));
                    hwBtn.ToolTip = LocalizationService.Get("TooltipRunHwTest", "Ručno ispitivanje hardverskih komponenti");
                    hwBtn.Click += (s, e) => ExecuteRunHwTest(null);

                    int idx = parent.Children.IndexOf(anchor);
                    parent.Children.Insert(idx < 0 ? 0 : idx, hwBtn);
                }
            }
            catch { }
        }

        private void ExecuteRunHwTest(object param)
        {
            EnsureEmbeddedHwTestTab();
            WIS.HwTest.Core.DriverGateState gate = EvaluateInteractiveTestsDriverGate();
            if (_embeddedHwTestShell != null && _hwTestTabItem != null && MainTabControl != null)
            {
                RefreshEmbeddedHwTestIdentity();
                if (gate != null && gate.IsUnlocked)
                {
                    _embeddedHwTestShell.RefreshHardwareProbeAfterDrivers();
                }
                _embeddedHwTestShell.RefreshDriverGateUi();
                ViewModel.ShutdownTimerActive = false;
                ViewModel.IsCompleted = false;
                MainTabControl.SelectedItem = _hwTestTabItem;
                if (gate != null && !gate.IsUnlocked)
                {
                    ViewModel.AddLog("WARN", "Hardver", string.Format(
                        "Otvoren tab Hardverski testovi u zaključanom režimu (drajveri nisu spremni): {0}",
                        gate.ReasonTitle));
                }
                else
                {
                    ViewModel.AddLog("INFO", "Hardver", "Otvoren ugrađeni tab za hardverske testove (WIS.HwTest).");
                }
                return;
            }

            if (gate != null && !gate.IsUnlocked)
            {
                MessageBox.Show(
                    this,
                    string.Format("{0}\n\n{1}", gate.ReasonTitle, gate.ReasonDetail),
                    LocalizationService.Get("HwTTitle", "WIS Hardverski testovi"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string outJson = @"C:\setup\hwtest-result.json";
            string exePath = FindHwTestExe();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                MessageBox.Show(LocalizationService.Get("WinMsgHwTestModuleMissing", "Modul za hardversko testiranje (WIS.HwTest.exe) nije pronađen!"), LocalizationService.Get("WinMsgTitleError", "WIS Greška"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Pauziraj tajmer gašenja dok traje testiranje
            bool wasShutdownActive = ViewModel.ShutdownTimerActive;
            ViewModel.ShutdownTimerActive = false;

            string model;
            string serial;
            string mfgArg;
            string skuArg;
            GetCurrentDeviceIdentity(out model, out serial, out mfgArg, out skuArg);

            string args = string.Format("--out \"{0}\"", outJson);
            if (!string.IsNullOrEmpty(model) && !IsPlaceholderIdentity(model))
                args += string.Format(" --model \"{0}\"", model.Replace("\"", ""));
            if (!string.IsNullOrEmpty(serial) && !IsPlaceholderIdentity(serial))
                args += string.Format(" --serial \"{0}\"", serial.Replace("\"", ""));
            if (!string.IsNullOrEmpty(mfgArg) && !IsPlaceholderIdentity(mfgArg))
                args += string.Format(" --manufacturer \"{0}\"", mfgArg.Replace("\"", ""));
            if (!string.IsNullOrEmpty(skuArg) && !IsPlaceholderIdentity(skuArg))
                args += string.Format(" --sku \"{0}\"", skuArg.Replace("\"", ""));

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false
            };

            ViewModel.AddLog("INFO", "Hardver", "Pokrenut WIS.HwTest.exe za ručno ispitivanje komponenti...");

            try
            {
                Process p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit();

                    if (File.Exists(outJson))
                    {
                        string json = File.ReadAllText(outJson, Encoding.UTF8);
                        var root = JsonHelper.Parse(json);
                        if (root != null && root.ContainsKey("summary"))
                        {
                            var summary = root["summary"];
                            string overall = summary["overall"].AsString("UNKNOWN");
                            int pass = summary["pass"].AsInt(0);
                            int fail = summary["fail"].AsInt(0);
                            int skip = summary["skip"].AsInt(0);
                            int na = summary["na"].AsInt(0);

                            int totalApplicable = pass + fail + skip;
                            if (totalApplicable <= 0) totalApplicable = pass + fail + skip + na;

                            bool noPendingFromFile = ReadPendingInteractiveTestsFromReportFile().Count == 0;
                            ViewModel.IsHwTestFinished = noPendingFromFile;
                            ViewModel.HwTestOverallStatus = (skip > 0 && overall == "PASS_WITH_SKIP") ? LocalizationService.Get("WinHwTestIncompleteSkip", "NEPOTPUNO (SKIP)") : overall;
                            ViewModel.HwTestSummaryText = fail > 0
                                ? string.Format("{0}/{1} PASS, {2} FAIL, {3} SKIP", pass, totalApplicable, fail, skip)
                                : string.Format("{0}/{1} PASS, {2} SKIP", pass, totalApplicable, skip);

                            if (fail > 0)
                            {
                                ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(220, 53, 69);
                            }
                            else if (skip > 0 || !noPendingFromFile)
                            {
                                ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(230, 126, 34);
                            }
                            else if (overall == "PASS" || overall == "PASS_WITH_SKIP")
                            {
                                ViewModel.HwTestBadgeBrush = MainViewModel.CreateFrozenBrush(40, 167, 69);
                            }

                            if (noPendingFromFile)
                            {
                                HidePendingInteractiveTestsWarning();
                            }

                            ViewModel.AddLog(
                                overall == "FAIL" ? "WARN" : "SUCCESS",
                                "Hardver",
                                string.Format("Hardverski test završen: {0} ({1}/{2} PASS, {3} SKIP, {4} FAIL)", overall, pass, totalApplicable, skip, fail)
                            );

                            SendIpcMessage(new Dictionary<string, object>
                            {
                                { "action", "hwtest_complete" },
                                { "hwtest_overall", overall },
                                { "hwtest_summary", ViewModel.HwTestSummaryText },
                                { "pass", pass },
                                { "fail", fail },
                                { "skip", skip },
                                { "na", na }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "Hardver", "Greška pri pokretanju WIS.HwTest.exe: " + ex.Message);
            }

            EnsureHwTestButtons();
        }

        private static string FindHwTestExe()
        {
            string[] candidates = new string[]
            {
                @"C:\setup\WIS.HwTest.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WIS.HwTest.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HwTest", "WIS.HwTest.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "UI", "HwTest", "WIS.HwTest.exe")
            };
            foreach (string p in candidates)
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void EnsureBackgroundWuVisualElements()
        {
            try
            {
                // 1. Uklanjanje suvišnih elemenata iz headera (verzija/brend red i BgWuHeaderBadge) — kompatibilno i sa postojećim BAML-om
                System.Windows.Controls.Grid rootGrid = this.Content as System.Windows.Controls.Grid;
                if (rootGrid != null && rootGrid.Children.Count > 0)
                {
                    System.Windows.Controls.Border headerBorder = rootGrid.Children[0] as System.Windows.Controls.Border;
                    if (headerBorder != null)
                    {
                        System.Windows.Controls.Grid headerGrid = headerBorder.Child as System.Windows.Controls.Grid;
                        if (headerGrid != null)
                        {
                            for (int i = headerGrid.Children.Count - 1; i >= 0; i--)
                            {
                                FrameworkElement fe = headerGrid.Children[i] as FrameworkElement;
                                if (fe == null) continue;

                                if (string.Equals(Convert.ToString(fe.Tag), "BgWuHeaderBadge", StringComparison.Ordinal))
                                {
                                    headerGrid.Children.RemoveAt(i);
                                    continue;
                                }

                                if (System.Windows.Controls.Grid.GetColumn(fe) == 0 && fe is System.Windows.Controls.StackPanel)
                                {
                                    System.Windows.Controls.StackPanel idStack = (System.Windows.Controls.StackPanel)fe;
                                    if (idStack.Children.Count > 0 && idStack.Children[0] is System.Windows.Controls.StackPanel)
                                    {
                                        System.Windows.Controls.StackPanel brandRow = (System.Windows.Controls.StackPanel)idStack.Children[0];
                                        if (brandRow.Orientation == System.Windows.Controls.Orientation.Horizontal)
                                        {
                                            idStack.Children.RemoveAt(0);
                                        }
                                    }
                                    if (idStack.Children.Count > 0 && idStack.Children[0] is System.Windows.Controls.TextBlock)
                                    {
                                        System.Windows.Controls.TextBlock modelTb = (System.Windows.Controls.TextBlock)idStack.Children[0];
                                        modelTb.Margin = new Thickness(0, 0, 0, 2);
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Kartica u Tabu 1 (Pregled) ispod glavnog Progress Bar-a i globalni watermark kroz sve kartice
                System.Windows.Controls.TabItem tab0 = null;
                if (MainTabControl != null && MainTabControl.Items.Count > 0)
                {
                    MainTabControl.Margin = new Thickness(14, 10, 14, 6);
                    MainTabControl.Background = Brushes.Transparent;
                    tab0 = MainTabControl.Items[0] as System.Windows.Controls.TabItem;
                    if (tab0 != null)
                    {
                        System.Windows.Controls.ScrollViewer sv = tab0.Content as System.Windows.Controls.ScrollViewer;
                        if (sv == null)
                        {
                            System.Windows.Controls.Grid tabGrid = tab0.Content as System.Windows.Controls.Grid;
                            if (tabGrid != null)
                            {
                                for (int c = 0; c < tabGrid.Children.Count; c++)
                                {
                                    sv = tabGrid.Children[c] as System.Windows.Controls.ScrollViewer;
                                    if (sv != null) break;
                                }
                            }
                        }
                        if (sv != null)
                        {
                            System.Windows.Controls.StackPanel overviewStack = sv.Content as System.Windows.Controls.StackPanel;
                            if (overviewStack != null)
                            {
                                // Uklanjanje SERVIS SPREMAN kartice, USB FREE banera, BgWuOverviewCard trake i kartice POSLEDNJE ZABELEŽENE AKTIVNOSTI iz Pregleda i kada se koristi stari BAML
                                for (int i = overviewStack.Children.Count - 1; i >= 0; i--)
                                {
                                    FrameworkElement fe = overviewStack.Children[i] as FrameworkElement;
                                    if (fe == null) continue;
                                    System.Windows.Data.Binding visBind = System.Windows.Data.BindingOperations.GetBinding(fe, UIElement.VisibilityProperty);
                                    bool isReadyCard = visBind != null && visBind.Path != null && string.Equals(visBind.Path.Path, "IsServiceReadyVisible", StringComparison.Ordinal);
                                    bool isUsbBanner = visBind != null && visBind.Path != null && string.Equals(visBind.Path.Path, "UsbFree", StringComparison.Ordinal);
                                    bool isBgWuCard = string.Equals(Convert.ToString(fe.Tag), "BgWuOverviewCard", StringComparison.Ordinal) ||
                                                      (visBind != null && visBind.Path != null && string.Equals(visBind.Path.Path, "IsBackgroundWuVisible", StringComparison.Ordinal));
                                    bool isRecentLogsCard = ContainsItemsSourceBinding(fe, "RecentLogs");
                                    if (isReadyCard || isUsbBanner || isBgWuCard || isRecentLogsCard)
                                    {
                                        overviewStack.Children.RemoveAt(i);
                                    }
                                }

                                // Prilagođavanje Phase tracker mreže na 5 kolona (uklonjena kartica 1. Mreža) — kompatibilno sa postojećim BAML-om
                                System.Windows.Controls.ItemsControl phasesControl = FindPhasesItemsControl(overviewStack);
                                if (phasesControl != null)
                                {
                                    var ugFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
                                    ugFactory.SetValue(System.Windows.Controls.Primitives.UniformGrid.RowsProperty, 1);
                                    ugFactory.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, 5);
                                    phasesControl.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(ugFactory);

                                    // Uklanjanje Expander zaglavlja i zaključavanje uvek vidljivih koraka i kada se koristi stari BAML
                                    phasesControl.Loaded += delegate
                                    {
                                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)delegate
                                        {
                                            ApplyAlwaysVisiblePhaseStepsVisual(phasesControl);
                                        });
                                    };
                                    phasesControl.ItemContainerGenerator.StatusChanged += delegate
                                    {
                                        if (phasesControl.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                                        {
                                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)delegate
                                            {
                                                ApplyAlwaysVisiblePhaseStepsVisual(phasesControl);
                                            });
                                        }
                                    };

                                    EnsurePhasesCollapseToggleButton(phasesControl);
                                }

                                // Srednji 2-kolonski panel na Tabu 1 (Prazne Informacije o uređaju levo + QR kod kartona uređaja desno)
                                EnsureOverviewMiddlePanel(tab0, sv, overviewStack);

                                // Premeštanje progres kartice na dno (odmah iznad footera) i prelazak na Globalni + Paralelni koraci prikaz za stari BAML
                                EnsureBottomDualProgressCard(tab0, sv, overviewStack);
                            }
                        }
                    }

                    EnsureAutopilotTabAlertVisual(MainTabControl);
                    RemoveTabByHeaderBinding(MainTabControl, "TabNetworkHeader");
                    EnsureAppsTabRetryButtons(MainTabControl);
                    EnsureHwMonitorTabLayout(MainTabControl);
                }

                // Globalni transparentni WIS watermark vidljiv u pozadini svih kartica (i za postojeći BAML)
                EnsureGlobalTabsWatermark(rootGrid, tab0);
                if (LogListView != null)
                {
                    var logBg = new SolidColorBrush(Color.FromArgb(204, 24, 24, 24));
                    logBg.Freeze();
                    LogListView.Background = logBg;
                }

                // 3. Uklanjanje dugmadi Pauza, Preskoči i Odloži restart iz donje trake (footera) i za postojeći BAML
                if (rootGrid != null)
                {
                    for (int i = 0; i < rootGrid.Children.Count; i++)
                    {
                        FrameworkElement fe = rootGrid.Children[i] as FrameworkElement;
                        if (fe != null && System.Windows.Controls.Grid.GetRow(fe) == 2)
                        {
                            RemoveElementsByVisibilityBinding(fe, "IsNotServiceFinished");
                            EnsureFooterServiceHubIndicator(fe);
                            EnsureFooterBrandingAndVersion(fe);
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureOverviewMiddlePanel(
            System.Windows.Controls.TabItem tab0,
            System.Windows.Controls.ScrollViewer sv,
            System.Windows.Controls.StackPanel overviewStack)
        {
            if (tab0 == null || sv == null || overviewStack == null) return;
            if (HasTaggedElement(tab0.Content as DependencyObject, "OverviewMiddleGrid")) return;
            if (!object.ReferenceEquals(sv.Content, overviewStack)) return;

            var boolToVis = new System.Windows.Controls.BooleanToVisibilityConverter();

            sv.Content = null;
            overviewStack.Margin = new Thickness(0);

            var wrapperBorder = new System.Windows.Controls.Border
            {
                Padding = new Thickness(6, 10, 6, 6)
            };

            var contentGrid = new System.Windows.Controls.Grid();
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto, MinHeight = 320 });

            System.Windows.Controls.Grid.SetRow(overviewStack, 0);
            contentGrid.Children.Add(overviewStack);

            var middleGrid = new System.Windows.Controls.Grid
            {
                Tag = "OverviewMiddleGrid",
                Margin = new Thickness(0, 12, 0, 0)
            };
            middleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            middleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(12) });
            middleGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 290 });

            // Levo: Informacije o uređaju (Plavi baner model + Crveni boks specifikacije + Žuti prazni boks)
            var infoCard = new System.Windows.Controls.Border
            {
                Tag = "OverviewDeviceInfoCard",
                Background = MainViewModel.CreateFrozenBrush(37, 37, 38),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var infoGrid = new System.Windows.Controls.Grid();
            infoGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            infoGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            infoGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var infoHeader = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinDevInfoTitle", "INFORMACIJE O UREĐAJU"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(infoHeader, 0);
            infoGrid.Children.Add(infoHeader);

            var modelBanner = new System.Windows.Controls.Border
            {
                Tag = "OverviewDeviceModelBanner",
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var modelBannerGrid = new System.Windows.Controls.Grid();
            modelBannerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            modelBannerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var modelLabelTb = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinDevInfoModelLabel", "PROIZVOĐAČ / MODEL"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(modelLabelTb, 0);
            modelBannerGrid.Children.Add(modelLabelTb);

            var modelValTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            modelValTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("ComputerModel"));
            modelValTb.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("ComputerModel"));
            System.Windows.Controls.Grid.SetColumn(modelValTb, 1);
            modelBannerGrid.Children.Add(modelValTb);

            modelBanner.Child = modelBannerGrid;
            System.Windows.Controls.Grid.SetRow(modelBanner, 1);
            infoGrid.Children.Add(modelBanner);

            var infoSubGrid = new System.Windows.Controls.Grid();
            infoSubGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoSubGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(10) });
            infoSubGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var specsCard = new System.Windows.Controls.Border
            {
                Tag = "OverviewDeviceSpecsCard",
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var specsCardGrid = new System.Windows.Controls.Grid();
            specsCardGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            specsCardGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var specsStack = new System.Windows.Controls.StackPanel();
            specsStack.Children.Add(CreateOverviewSpecRow("CPU", "CpuInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("RAM", "RamInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Disk", "DiskInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Rezolucija ekrana", "HardwareInfo.OverviewResolution", null, null, boolToVis, false, "HardwareInfo.OverviewResolutionTooltip"));
            specsStack.Children.Add(CreateOverviewSpecRow("GPU", "HardwareInfo.OverviewGpu", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("iGPU", "HardwareInfo.OverviewIgpu", null, "HardwareInfo.HasOverviewIgpu", boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Battery wear", "HardwareInfo.BatteryWear", "HardwareInfo.BatteryHealthBrush", null, boolToVis, true));
            System.Windows.Controls.Grid.SetRow(specsStack, 0);
            specsCardGrid.Children.Add(specsStack);

            var periphSection = new System.Windows.Controls.Border
            {
                Tag = "OverviewPeripheralsSection",
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(0, 8, 0, 0)
            };
            var periphStack = new System.Windows.Controls.StackPanel();
            periphStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinPeripheralsTitle", "DODATNE PERIFERIJE"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var periphWrap = new System.Windows.Controls.WrapPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            periphStack.Children.Add(periphWrap);

            var periphEmptyTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(119, 119, 119)
            };
            periphEmptyTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("HardwareInfo.OverviewPeripheralsEmptyText"));
            periphEmptyTb.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HardwareInfo.ShowOverviewPeripheralsEmpty") { Converter = boolToVis });
            periphStack.Children.Add(periphEmptyTb);

            periphSection.Child = periphStack;
            System.Windows.Controls.Grid.SetRow(periphSection, 1);
            specsCardGrid.Children.Add(periphSection);

            specsCard.Child = specsCardGrid;
            System.Windows.Controls.Grid.SetColumn(specsCard, 0);
            infoSubGrid.Children.Add(specsCard);

            RebuildOverviewPeripheralsHost(periphWrap);
            if (ViewModel != null && ViewModel.HardwareInfo != null && ViewModel.HardwareInfo.OverviewPeripherals != null)
            {
                ViewModel.HardwareInfo.OverviewPeripherals.CollectionChanged += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        RebuildOverviewPeripheralsHost(periphWrap);
                    });
                };
            }

            var extraGrid = new System.Windows.Controls.Grid
            {
                Tag = "OverviewDeviceExtraCard"
            };
            extraGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            extraGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(10) });
            extraGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Gornji blok: HARDVER MONITOR
            var hwMonCard = new System.Windows.Controls.Border
            {
                Tag = "OverviewHardwareMonitorCard",
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 9, 12, 9)
            };
            var hwMonStack = new System.Windows.Controls.StackPanel();

            var hwMonHdr = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
            hwMonHdr.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonTitle", "HARDVER MONITOR"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            hwMonHdr.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonLive", "● Uživo"),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            });
            hwMonStack.Children.Add(hwMonHdr);

            // Opterećenje CPU i RAM
            var usageGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
            usageGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            usageGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(10) });
            usageGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cpuStack = new System.Windows.Controls.StackPanel();
            var cpuHdr = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            cpuHdr.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonCpuLoad", "CPU opterećenje"),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136)
            });
            var cpuValTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Consolas")
            };
            cpuValTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CpuUsageShortText"));
            cpuValTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("CpuUsageBrush"));
            cpuHdr.Children.Add(cpuValTb);
            cpuStack.Children.Add(cpuHdr);

            var cpuBar = new System.Windows.Controls.ProgressBar
            {
                Maximum = 100,
                Height = 5,
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                BorderThickness = new Thickness(0)
            };
            cpuBar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding("CpuUsagePercent"));
            cpuBar.SetBinding(System.Windows.Controls.Control.ForegroundProperty, new System.Windows.Data.Binding("CpuUsageBrush"));
            cpuStack.Children.Add(cpuBar);
            System.Windows.Controls.Grid.SetColumn(cpuStack, 0);
            usageGrid.Children.Add(cpuStack);

            var ramStack = new System.Windows.Controls.StackPanel();
            var ramHdr = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            ramHdr.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonRamUsage", "RAM zauzeće"),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136)
            });
            var ramValTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Consolas"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ramValTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("RamUsageShortText"));
            ramValTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("RamUsageBrush"));
            ramValTb.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("RamUsage"));
            ramHdr.Children.Add(ramValTb);
            ramStack.Children.Add(ramHdr);

            var ramBar = new System.Windows.Controls.ProgressBar
            {
                Maximum = 100,
                Height = 5,
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                BorderThickness = new Thickness(0)
            };
            ramBar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding("RamUsagePercent"));
            ramBar.SetBinding(System.Windows.Controls.Control.ForegroundProperty, new System.Windows.Data.Binding("RamUsageBrush"));
            ramStack.Children.Add(ramBar);
            System.Windows.Controls.Grid.SetColumn(ramStack, 2);
            usageGrid.Children.Add(ramStack);

            hwMonStack.Children.Add(usageGrid);

            // 4 kompaktne ćelije za temperature
            var tempGrid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = 4,
                Margin = new Thickness(0, 0, 0, 8)
            };
            tempGrid.Children.Add(CreateMonitorTempCell("CPU TEMP", "CpuTempShortText", "CpuTempBrush", "CpuTempText", false));
            tempGrid.Children.Add(CreateMonitorTempCell("GPU TEMP", "GpuTempShortText", "GpuTempBrush", "GpuTempText", false));
            tempGrid.Children.Add(CreateMonitorTempCell("SSD TEMP", "SsdTempShortText", "SsdTempBrush", "SsdTempText", false));
            tempGrid.Children.Add(CreateMonitorTempCell("MB TEMP", "MbTempShortText", "MbTempBrush", "MbTempText", true));
            hwMonStack.Children.Add(tempGrid);

            // Stanje baterije / punjač
            var powerBox = new System.Windows.Controls.Border
            {
                BorderBrush = MainViewModel.CreateFrozenBrush(42, 42, 46),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0)
            };
            powerBox.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("PowerMonitorTooltip"));
            var powerStack = new System.Windows.Controls.StackPanel();
            var powerTop = new System.Windows.Controls.Grid();
            powerTop.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            powerTop.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var powerStatusTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            powerStatusTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("PowerStatusTitle"));
            powerStatusTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("PowerStatusBrush"));
            System.Windows.Controls.Grid.SetColumn(powerStatusTb, 0);
            powerTop.Children.Add(powerStatusTb);

            var powerDrawTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            powerDrawTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("PowerDrawText"));
            powerDrawTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("PowerDrawBrush"));
            System.Windows.Controls.Grid.SetColumn(powerDrawTb, 1);
            powerTop.Children.Add(powerDrawTb);
            powerStack.Children.Add(powerTop);

            var battBar = new System.Windows.Controls.ProgressBar
            {
                Maximum = 100,
                Height = 4,
                Margin = new Thickness(0, 5, 0, 0),
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                BorderThickness = new Thickness(0)
            };
            battBar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding("BatteryChargePercent"));
            battBar.SetBinding(System.Windows.Controls.Control.ForegroundProperty, new System.Windows.Data.Binding("BatteryLevelBrush"));
            battBar.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HasBatteryLive") { Converter = boolToVis });
            powerStack.Children.Add(battBar);

            powerBox.Child = powerStack;
            hwMonStack.Children.Add(powerBox);

            hwMonCard.Child = hwMonStack;
            System.Windows.Controls.Grid.SetRow(hwMonCard, 0);
            extraGrid.Children.Add(hwMonCard);

            // Donji blok: UPOZORENJA
            var alertsCard = new System.Windows.Controls.Border
            {
                Tag = "OverviewAlertsCard",
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 9, 12, 9)
            };
            var alertsGrid = new System.Windows.Controls.Grid();
            alertsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            alertsGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var alertsHdr = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
            alertsHdr.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinWarningsTitle", "UPOZORENJA"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var alertsBadge = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            alertsBadge.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("OverviewAlertsBadgeBrush"));
            var alertsBadgeTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            alertsBadgeTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("OverviewAlertsCountText"));
            alertsBadge.Child = alertsBadgeTb;
            alertsHdr.Children.Add(alertsBadge);
            System.Windows.Controls.Grid.SetRow(alertsHdr, 0);
            alertsGrid.Children.Add(alertsHdr);

            var alertsBodyHost = new System.Windows.Controls.Grid();
            System.Windows.Controls.Grid.SetRow(alertsBodyHost, 1);

            var alertsScroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                MaxHeight = 145
            };
            alertsScroll.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HasOverviewAlerts") { Converter = boolToVis });
            var alertsHost = new System.Windows.Controls.StackPanel();
            alertsScroll.Content = alertsHost;
            alertsBodyHost.Children.Add(alertsScroll);

            RebuildOverviewAlertsHost(alertsHost);
            if (ViewModel != null && ViewModel.OverviewAlerts != null)
            {
                ViewModel.OverviewAlerts.CollectionChanged += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        RebuildOverviewAlertsHost(alertsHost);
                    });
                };
            }

            var noAlertsBorder = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(27, 42, 33),
                BorderBrush = MainViewModel.CreateFrozenBrush(45, 90, 63),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            noAlertsBorder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowNoOverviewAlerts") { Converter = boolToVis });
            var noAlertsStack = new System.Windows.Controls.StackPanel();
            noAlertsStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinWarningsNone", "✓ Nema aktivnih upozorenja"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(46, 204, 113)
            });
            noAlertsStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinWarningsNormal", "Temperature, Autopilot, drajveri i programi su u normalnom stanju."),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(168, 197, 180),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            noAlertsBorder.Child = noAlertsStack;
            alertsBodyHost.Children.Add(noAlertsBorder);

            alertsGrid.Children.Add(alertsBodyHost);
            alertsCard.Child = alertsGrid;
            System.Windows.Controls.Grid.SetRow(alertsCard, 2);
            extraGrid.Children.Add(alertsCard);

            System.Windows.Controls.Grid.SetColumn(extraGrid, 2);
            infoSubGrid.Children.Add(extraGrid);

            System.Windows.Controls.Grid.SetRow(infoSubGrid, 2);
            infoGrid.Children.Add(infoSubGrid);

            infoCard.Child = infoGrid;
            System.Windows.Controls.Grid.SetColumn(infoCard, 0);
            middleGrid.Children.Add(infoCard);

            // Desno: QR kod kartona uređaja na ServisHub-u
            var qrCard = new System.Windows.Controls.Border
            {
                Tag = "OverviewDeviceQrCard",
                Background = MainViewModel.CreateFrozenBrush(37, 37, 38),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12)
            };
            var qrCardGrid = new System.Windows.Controls.Grid();
            qrCardGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            qrCardGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            qrCardGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var qrTitle = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinServiceHubCardTitle", "KARTON UREĐAJA (SERVISHUB)"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(qrTitle, 0);
            qrCardGrid.Children.Add(qrTitle);

            var qrViewbox = new System.Windows.Controls.Viewbox
            {
                StretchDirection = System.Windows.Controls.StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetRow(qrViewbox, 1);

            var qrHost = new System.Windows.Controls.Grid();

            var qrWhiteBorder = new System.Windows.Controls.Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };
            qrWhiteBorder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HasDeviceCardQr") { Converter = boolToVis });

            var qrImg = new System.Windows.Controls.Image
            {
                Width = 260,
                Height = 260,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(qrImg, BitmapScalingMode.NearestNeighbor);
            qrImg.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding("DeviceCardQrImage"));
            qrWhiteBorder.Child = qrImg;
            qrHost.Children.Add(qrWhiteBorder);

            var qrPlaceholder = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Width = 260,
                Height = 260,
                Padding = new Thickness(16)
            };
            qrPlaceholder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowDeviceCardQrPlaceholder") { Converter = boolToVis });
            qrPlaceholder.Child = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinQrPlaceholder", "QR kod će se pojaviti kada se otkrije ServisHub..."),
                FontSize = 12,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            qrHost.Children.Add(qrPlaceholder);
            qrViewbox.Child = qrHost;
            qrCardGrid.Children.Add(qrViewbox);

            var qrFooter = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            System.Windows.Controls.Grid.SetRow(qrFooter, 2);

            var qrStatusTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(170, 170, 170),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            qrStatusTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceCardStatusText"));
            qrFooter.Children.Add(qrStatusTb);

            var qrUrlTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            qrUrlTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceCardUrl"));
            qrUrlTb.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("DeviceCardUrl"));
            qrFooter.Children.Add(qrUrlTb);

            qrCardGrid.Children.Add(qrFooter);
            qrCard.Child = qrCardGrid;
            System.Windows.Controls.Grid.SetColumn(qrCard, 2);
            middleGrid.Children.Add(qrCard);

            System.Windows.Controls.Grid.SetRow(middleGrid, 1);
            contentGrid.Children.Add(middleGrid);

            wrapperBorder.Child = contentGrid;
            sv.Content = wrapperBorder;
        }

        private static System.Windows.Controls.Border CreateOverviewSpecRow(
            string label,
            string bindingPath,
            string foregroundBindingPath,
            string visibilityBindingPath,
            System.Windows.Controls.BooleanToVisibilityConverter boolToVis,
            bool isLast,
            string tooltipBindingPath = null)
        {
            var rowBorder = new System.Windows.Controls.Border
            {
                Padding = new Thickness(0, 6, 0, 6)
            };
            if (!isLast)
            {
                rowBorder.BorderBrush = MainViewModel.CreateFrozenBrush(42, 42, 46);
                rowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            if (!string.IsNullOrEmpty(visibilityBindingPath) && boolToVis != null)
            {
                rowBorder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(visibilityBindingPath) { Converter = boolToVis });
            }

            var rowGrid = new System.Windows.Controls.Grid();
            rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(125) });
            rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelTb = new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(labelTb, 0);
            rowGrid.Children.Add(labelTb);

            var valTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            valTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(bindingPath));
            string resolvedTooltipPath = !string.IsNullOrEmpty(tooltipBindingPath) ? tooltipBindingPath : bindingPath;
            valTb.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(resolvedTooltipPath));
            if (!string.IsNullOrEmpty(foregroundBindingPath))
            {
                valTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding(foregroundBindingPath));
            }
            System.Windows.Controls.Grid.SetColumn(valTb, 1);
            rowGrid.Children.Add(valTb);

            rowBorder.Child = rowGrid;
            return rowBorder;
        }

        private void RebuildOverviewPeripheralsHost(System.Windows.Controls.WrapPanel host)
        {
            if (host == null || ViewModel == null || ViewModel.HardwareInfo == null || ViewModel.HardwareInfo.OverviewPeripherals == null)
            {
                return;
            }
            host.Children.Clear();
            for (int i = 0; i < ViewModel.HardwareInfo.OverviewPeripherals.Count; i++)
            {
                host.Children.Add(CreatePeripheralBadgeBorder(ViewModel.HardwareInfo.OverviewPeripherals[i]));
            }
        }

        private static System.Windows.Controls.Border CreatePeripheralBadgeBorder(PeripheralBadgeItem item)
        {
            var b = new System.Windows.Controls.Border
            {
                DataContext = item,
                Background = item.BackgroundBrush,
                BorderBrush = item.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = item.Detail
            };

            var stack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dot = new System.Windows.Controls.Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = item.AccentBrush,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var lbl = new System.Windows.Controls.TextBlock
            {
                Text = item.Label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(230, 230, 230),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(dot);
            stack.Children.Add(lbl);
            b.Child = stack;
            return b;
        }

        private static System.Windows.Controls.Border CreateMonitorTempCell(
            string label,
            string shortTempBinding,
            string brushBinding,
            string fullTooltipBinding,
            bool isLast)
        {
            var cell = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(37, 37, 40),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = isLast ? new Thickness(0) : new Thickness(0, 0, 4, 0)
            };
            cell.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(fullTooltipBinding));

            var stack = new System.Windows.Controls.StackPanel();
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136)
            });

            var valTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 1, 0, 0)
            };
            valTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(shortTempBinding));
            valTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding(brushBinding));
            stack.Children.Add(valTb);

            cell.Child = stack;
            return cell;
        }

        private void RebuildOverviewAlertsHost(System.Windows.Controls.StackPanel host)
        {
            if (host == null || ViewModel == null || ViewModel.OverviewAlerts == null) return;
            host.Children.Clear();
            for (int i = 0; i < ViewModel.OverviewAlerts.Count; i++)
            {
                host.Children.Add(CreateOverviewAlertBorder(ViewModel.OverviewAlerts[i]));
            }
        }

        private System.Windows.Controls.Border CreateOverviewAlertBorder(OverviewAlertItem item)
        {
            var b = new System.Windows.Controls.Border
            {
                DataContext = item,
                Background = item.BackgroundBrush,
                BorderBrush = item.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand
            };
            b.MouseLeftButtonUp += OverviewAlertItem_MouseLeftButtonUp;

            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var stripe = new System.Windows.Controls.Border
            {
                Background = item.AccentBrush,
                CornerRadius = new CornerRadius(1.5)
            };
            System.Windows.Controls.Grid.SetColumn(stripe, 0);
            grid.Children.Add(stripe);

            var textStack = new System.Windows.Controls.StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            textStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = item.Title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = item.TitleBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = item.Detail,
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(189, 189, 189),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Detail,
                Margin = new Thickness(0, 1, 0, 0)
            });
            System.Windows.Controls.Grid.SetColumn(textStack, 2);
            grid.Children.Add(textStack);

            var pill = new System.Windows.Controls.Border
            {
                Background = item.AccentBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            pill.Child = new System.Windows.Controls.TextBlock
            {
                Text = item.BadgeText,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            System.Windows.Controls.Grid.SetColumn(pill, 3);
            grid.Children.Add(pill);

            b.Child = grid;
            return b;
        }

        private void OverviewAlertItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            OverviewAlertItem item = fe != null ? fe.DataContext as OverviewAlertItem : null;
            if (item != null)
            {
                NavigateToAlertTab(item.TargetTab);
            }
        }

        private void NavigateToAlertTab(string targetTab)
        {
            if (MainTabControl == null || string.IsNullOrEmpty(targetTab)) return;

            if (ViewModel != null && ViewModel.IsCompleted)
            {
                ViewModel.IsServiceFinished = true;
                ViewModel.IsCompleted = false;
            }

            if (string.Equals(targetTab, "hwtest", StringComparison.OrdinalIgnoreCase))
            {
                if (_hwTestTabItem != null)
                {
                    MainTabControl.SelectedItem = _hwTestTabItem;
                    return;
                }
                for (int i = 0; i < MainTabControl.Items.Count; i++)
                {
                    var ti = MainTabControl.Items[i] as System.Windows.Controls.TabItem;
                    if (ti != null && string.Equals(Convert.ToString(ti.Tag), HwTestTabTag, StringComparison.Ordinal))
                    {
                        MainTabControl.SelectedItem = ti;
                        return;
                    }
                }
            }

            string bindingPath = null;
            if (string.Equals(targetTab, "diagnostics", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabDiagnosticsHeader";
            else if (string.Equals(targetTab, "autopilot", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabAutopilotHeader";
            else if (string.Equals(targetTab, "drivers", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabDriversHeader";
            else if (string.Equals(targetTab, "apps", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabAppsHeader";
            else if (string.Equals(targetTab, "hardware", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabHardwareHeader";
            else if (string.Equals(targetTab, "logs", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabLogsHeader";
            else if (string.Equals(targetTab, "overview", StringComparison.OrdinalIgnoreCase)) bindingPath = "TabOverviewHeader";

            if (string.IsNullOrEmpty(bindingPath)) return;

            for (int i = 0; i < MainTabControl.Items.Count; i++)
            {
                var ti = MainTabControl.Items[i] as System.Windows.Controls.TabItem;
                if (ti == null) continue;

                var bindingExpr = System.Windows.Data.BindingOperations.GetBinding(
                    ti, System.Windows.Controls.HeaderedContentControl.HeaderProperty);
                if (bindingExpr != null && bindingExpr.Path != null &&
                    string.Equals(bindingExpr.Path.Path, bindingPath, StringComparison.Ordinal))
                {
                    MainTabControl.SelectedItem = ti;
                    return;
                }

                if (string.Equals(bindingPath, "TabAutopilotHeader", StringComparison.Ordinal) &&
                    (string.Equals(Convert.ToString(ti.Tag), "AutopilotTabItem", StringComparison.Ordinal) || IsAutopilotTabItem(ti)))
                {
                    MainTabControl.SelectedItem = ti;
                    return;
                }
            }
        }

        private static void RemoveElementsByVisibilityBinding(DependencyObject root, string bindingPath)
        {
            if (root == null || string.IsNullOrEmpty(bindingPath)) return;

            System.Windows.Controls.Panel panel = root as System.Windows.Controls.Panel;
            if (panel != null)
            {
                for (int i = panel.Children.Count - 1; i >= 0; i--)
                {
                    FrameworkElement childFe = panel.Children[i] as FrameworkElement;
                    if (childFe == null) continue;
                    System.Windows.Data.Binding b = System.Windows.Data.BindingOperations.GetBinding(childFe, UIElement.VisibilityProperty);
                    if (b != null && b.Path != null && string.Equals(b.Path.Path, bindingPath, StringComparison.Ordinal))
                    {
                        panel.Children.RemoveAt(i);
                    }
                    else
                    {
                        RemoveElementsByVisibilityBinding(childFe, bindingPath);
                    }
                }
                return;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null)
                {
                    RemoveElementsByVisibilityBinding(dep, bindingPath);
                }
            }
        }

        private static T FindTaggedElement<T>(DependencyObject node, string tagValue) where T : FrameworkElement
        {
            if (node == null || string.IsNullOrEmpty(tagValue)) return null;
            T selfFe = node as T;
            if (selfFe != null && string.Equals(Convert.ToString(selfFe.Tag), tagValue, StringComparison.Ordinal))
            {
                return selfFe;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null)
                {
                    T found = FindTaggedElement<T>(dep, tagValue);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static bool HasTaggedElement(DependencyObject node, string tagValue)
        {
            return FindTaggedElement<FrameworkElement>(node, tagValue) != null;
        }

        /// <summary>
        /// Prilagođava visinu srednjeg panela i kartice GlobalProgressCard na manjim ekranima
        /// (npr. 13.3" 1920x1080 @ 150% DPI ili 1366x768 gde je ActualHeight &lt; 760 DIP)
        /// kako bi GlobalProgressCard i aktivni koraci ostali u potpunosti vidljivi.
        /// </summary>
        private void ApplyCompactViewportLayoutIfNeeded()
        {
            try
            {
                if (ActualHeight <= 0) return;

                bool isCompact = ActualHeight > 0 && ActualHeight < 760.0;
                double targetScrollMaxHeight = isCompact ? 96.0 : 160.0;
                Thickness targetCardPadding = isCompact
                    ? new Thickness(14, 7, 14, 7)
                    : new Thickness(14, 10, 14, 10);
                double targetMiddleMinHeight = isCompact ? 260.0 : 320.0;

                System.Windows.Controls.Border globalCard = FindTaggedElement<System.Windows.Controls.Border>(this, "GlobalProgressCard");
                if (globalCard != null && globalCard.Padding != targetCardPadding)
                {
                    globalCard.Padding = targetCardPadding;
                }

                System.Windows.Controls.ScrollViewer activeStepsScroll = FindTaggedElement<System.Windows.Controls.ScrollViewer>(this, "ActiveStepBarsScroll");
                if (activeStepsScroll == null && globalCard != null)
                {
                    activeStepsScroll = FindVisualChild<System.Windows.Controls.ScrollViewer>(globalCard);
                }
                if (activeStepsScroll != null && Math.Abs(activeStepsScroll.MaxHeight - targetScrollMaxHeight) > 0.1)
                {
                    activeStepsScroll.MaxHeight = targetScrollMaxHeight;
                }

                System.Windows.Controls.Grid middleGrid = FindTaggedElement<System.Windows.Controls.Grid>(this, "OverviewMiddleGrid");
                if (middleGrid != null)
                {
                    System.Windows.Controls.Grid parentGrid = (LogicalTreeHelper.GetParent(middleGrid) ?? VisualTreeHelper.GetParent(middleGrid)) as System.Windows.Controls.Grid;
                    if (parentGrid != null)
                    {
                        int rowIdx = System.Windows.Controls.Grid.GetRow(middleGrid);
                        if (rowIdx >= 0 && rowIdx < parentGrid.RowDefinitions.Count &&
                            Math.Abs(parentGrid.RowDefinitions[rowIdx].MinHeight - targetMiddleMinHeight) > 0.1)
                        {
                            parentGrid.RowDefinitions[rowIdx].MinHeight = targetMiddleMinHeight;
                        }
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // BUG-02: Čuvar maksimizovanog stanja prozora (uz dozvoljen Minimize)
        // =====================================================================

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (_isEnforcingMaximize) return;
            if (this.WindowState == WindowState.Minimized) return;
            if (this.WindowState == WindowState.Normal)
            {
                Dispatcher.BeginInvoke((Action)(() => EnsureWindowMaximized(false)));
            }
        }

        private void MainWindow_Activated(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized) return;
            EnsureWindowMaximized(false);
        }

        private void OnSystemDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess())
            {
                ScheduleEnsureWindowMaximized(true);
            }
            else
            {
                Dispatcher.BeginInvoke((Action)(() => ScheduleEnsureWindowMaximized(true)));
            }
        }

        private IntPtr WindowMaximizedGuardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
                {
                    ScheduleEnsureWindowMaximized(true);
                }
                else if (msg == WM_SETTINGCHANGE)
                {
                    int spiCode = wParam.ToInt32();
                    string areaName = lParam != IntPtr.Zero ? Marshal.PtrToStringAuto(lParam) : null;
                    bool isWorkAreaOrDpiChange = (spiCode == SPI_SETWORKAREA) ||
                        (!string.IsNullOrEmpty(areaName) &&
                         (areaName.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          areaName.IndexOf("WindowMetrics", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                        (spiCode == 0 && string.IsNullOrEmpty(areaName));
                    if (isWorkAreaOrDpiChange)
                    {
                        ScheduleEnsureWindowMaximized(true);
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        private void ScheduleEnsureWindowMaximized(bool forceRelayout)
        {
            if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => ScheduleEnsureWindowMaximized(forceRelayout)));
                return;
            }

            if (forceRelayout)
            {
                _pendingForceRelayout = true;
            }
            if (_maximizeDebounceTimer != null)
            {
                _maximizeDebounceTimer.Stop();
                _maximizeDebounceTimer.Start();
            }
            else
            {
                bool force = _pendingForceRelayout;
                _pendingForceRelayout = false;
                EnsureWindowMaximized(force);
            }
        }

        private void OnMaximizeDebounceTimerTick(object sender, EventArgs e)
        {
            if (_maximizeDebounceTimer != null)
            {
                _maximizeDebounceTimer.Stop();
            }
            bool force = _pendingForceRelayout;
            _pendingForceRelayout = false;
            EnsureWindowMaximized(force);
        }

        private void EnsureWindowMaximized(bool forceRelayout)
        {
            if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => EnsureWindowMaximized(forceRelayout)));
                return;
            }

            if (_isEnforcingMaximize) return;
            if (!this.IsLoaded && !this.IsVisible) return;
            // Izričita odluka korisnika: korisnik sme da minimizuje prozor u taskbar
            if (this.WindowState == WindowState.Minimized) return;

            _isEnforcingMaximize = true;
            bool deferRelease = false;
            try
            {
                double workW = SystemParameters.WorkArea.Width;
                double workH = SystemParameters.WorkArea.Height;
                bool sizeMismatch = (workW > 100.0 && this.ActualWidth > 0 && this.ActualWidth < workW - 20.0) ||
                                    (workH > 100.0 && this.ActualHeight > 0 && this.ActualHeight < workH - 20.0);

                if (this.WindowState == WindowState.Normal)
                {
                    this.WindowState = WindowState.Maximized;
                    ApplyCompactViewportLayoutIfNeeded();
                }
                else if (this.WindowState == WindowState.Maximized && (forceRelayout || sizeMismatch))
                {
                    deferRelease = true;
                    this.WindowState = WindowState.Normal;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
                    {
                        try
                        {
                            if (this.WindowState != WindowState.Minimized)
                            {
                                this.WindowState = WindowState.Maximized;
                                ApplyCompactViewportLayoutIfNeeded();
                            }
                        }
                        catch { }
                        finally
                        {
                            _isEnforcingMaximize = false;
                        }
                    }));
                }
                else
                {
                    ApplyCompactViewportLayoutIfNeeded();
                }
            }
            catch { }
            finally
            {
                if (!deferRelease)
                {
                    _isEnforcingMaximize = false;
                }
            }
        }

        // =====================================================================
        // BUG-03 / IMP-35: Kontekstualna traka za oporavak kada faza padne
        // =====================================================================

        private void SetFailedPhase(int phase)
        {
            _failedPhase = phase > 0 ? phase : 0;
            if (_failedPhase == 0)
            {
                _pendingPostRetryShortcutsRefresh = false;
                _pendingPostRetryLicenseRefresh = false;
            }

            Action syncAction = delegate
            {
                UpdateFailedPhaseRecoveryBar();
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess())
                {
                    syncAction();
                }
                else
                {
                    Dispatcher.BeginInvoke(syncAction);
                }
            }
        }

        private static System.Windows.Controls.Button CreateRecoveryFooterButton(string tag, string content, Color bg, Color fg, Color border)
        {
            var btn = new System.Windows.Controls.Button
            {
                Tag = tag,
                Content = content,
                Background = MainViewModel.CreateFrozenBrush(bg.R, bg.G, bg.B),
                Foreground = MainViewModel.CreateFrozenBrush(fg.R, fg.G, fg.B),
                BorderBrush = MainViewModel.CreateFrozenBrush(border.R, border.G, border.B),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand
            };
            return btn;
        }

        private void EnsureFailedPhaseRecoveryBar()
        {
            try
            {
                if (_failedPhaseRecoveryPanel == null)
                {
                    _failedPhaseRecoveryPanel = FindTaggedElement<System.Windows.Controls.StackPanel>(this, "FailedPhaseRecoveryPanel");
                }

                if (_failedPhaseRecoveryPanel != null)
                {
                    if (_btnFailedPhaseRecheck == null)
                        _btnFailedPhaseRecheck = FindTaggedElement<System.Windows.Controls.Button>(_failedPhaseRecoveryPanel, "BtnFailedPhaseRecheck");
                    if (_btnFailedPhaseRetry == null)
                        _btnFailedPhaseRetry = FindTaggedElement<System.Windows.Controls.Button>(_failedPhaseRecoveryPanel, "BtnFailedPhaseRetry");
                    if (_btnFailedPhaseSkip == null)
                        _btnFailedPhaseSkip = FindTaggedElement<System.Windows.Controls.Button>(_failedPhaseRecoveryPanel, "BtnFailedPhaseSkip");
                }
                else
                {
                    System.Windows.Controls.Grid rootGrid = this.Content as System.Windows.Controls.Grid;
                    if (rootGrid == null) return;

                    System.Windows.Controls.Grid footerGrid = null;
                    for (int i = 0; i < rootGrid.Children.Count; i++)
                    {
                        FrameworkElement fe = rootGrid.Children[i] as FrameworkElement;
                        if (fe != null && System.Windows.Controls.Grid.GetRow(fe) == 2)
                        {
                            System.Windows.Controls.Border borderRoot = fe as System.Windows.Controls.Border;
                            footerGrid = borderRoot != null ? (borderRoot.Child as System.Windows.Controls.Grid) : (fe as System.Windows.Controls.Grid);
                            if (footerGrid != null) break;
                        }
                    }
                    if (footerGrid == null) return;

                    System.Windows.Controls.StackPanel rightPanel = null;
                    for (int i = 0; i < footerGrid.Children.Count; i++)
                    {
                        UIElement child = footerGrid.Children[i];
                        if (System.Windows.Controls.Grid.GetColumn(child) == 1 && child is System.Windows.Controls.StackPanel)
                        {
                            rightPanel = (System.Windows.Controls.StackPanel)child;
                            break;
                        }
                    }
                    if (rightPanel == null) return;

                    _failedPhaseRecoveryPanel = new System.Windows.Controls.StackPanel
                    {
                        Tag = "FailedPhaseRecoveryPanel",
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Visibility = Visibility.Collapsed,
                        Margin = new Thickness(0, 0, 8, 0)
                    };

                    _btnFailedPhaseRecheck = CreateRecoveryFooterButton(
                        "BtnFailedPhaseRecheck",
                        "Proveri ponovo i nastavi",
                        Color.FromRgb(31, 111, 235),
                        Colors.White,
                        Color.FromRgb(56, 139, 253));
                    _btnFailedPhaseRecheck.Margin = new Thickness(0, 0, 6, 0);

                    _btnFailedPhaseRetry = CreateRecoveryFooterButton(
                        "BtnFailedPhaseRetry",
                        "Ponovi Fazu",
                        Color.FromRgb(210, 153, 34),
                        Color.FromRgb(17, 17, 17),
                        Color.FromRgb(227, 179, 65));
                    _btnFailedPhaseRetry.Margin = new Thickness(0, 0, 6, 0);

                    _btnFailedPhaseSkip = CreateRecoveryFooterButton(
                        "BtnFailedPhaseSkip",
                        LocalizationService.Get("WinRecoverySkipBtn", "Preskoči na sledeću fazu"),
                        Color.FromRgb(33, 38, 45),
                        Colors.White,
                        Color.FromRgb(218, 54, 51));
                    _btnFailedPhaseSkip.FontWeight = FontWeights.SemiBold;

                    _failedPhaseRecoveryPanel.Children.Add(_btnFailedPhaseRecheck);
                    _failedPhaseRecoveryPanel.Children.Add(_btnFailedPhaseRetry);
                    _failedPhaseRecoveryPanel.Children.Add(_btnFailedPhaseSkip);
                    rightPanel.Children.Insert(0, _failedPhaseRecoveryPanel);
                }

                if (!_recoveryBarEventsWired && _failedPhaseRecoveryPanel != null)
                {
                    if (_btnFailedPhaseRecheck != null)
                    {
                        _btnFailedPhaseRecheck.Click += (s, e) => ExecuteFailedPhaseRecheck();
                    }
                    if (_btnFailedPhaseRetry != null)
                    {
                        _btnFailedPhaseRetry.Click += (s, e) => ExecutePause(null);
                    }
                    if (_btnFailedPhaseSkip != null)
                    {
                        _btnFailedPhaseSkip.Click += (s, e) => ExecuteSkipWithConfirmation();
                    }
                    _recoveryBarEventsWired = true;
                }
            }
            catch { }
        }

        private void UpdateFailedPhaseRecoveryBar()
        {
            try
            {
                if (_failedPhaseRecoveryPanel == null)
                {
                    EnsureFailedPhaseRecoveryBar();
                }
                if (_failedPhaseRecoveryPanel == null) return;

                bool show = _failedPhase > 0;
                _failedPhaseRecoveryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                if (_btnFailedPhaseRecheck != null)
                {
                    _btnFailedPhaseRecheck.Visibility = (_failedPhase == 4) ? Visibility.Visible : Visibility.Collapsed;
                }
                if (_btnFailedPhaseRetry != null)
                {
                    _btnFailedPhaseRetry.Content = show
                        ? string.Format(LocalizationService.Get("WinRecoveryRetryPhaseFmt", "↻ Ponovi Fazu {0}"), _failedPhase)
                        : LocalizationService.Get("WinRecoveryRetryPhaseDefault", "Ponovi Fazu");
                }
                if (_btnFailedPhaseSkip != null)
                {
                    _btnFailedPhaseSkip.Content = LocalizationService.Get("WinRecoverySkipBtn", "Preskoči na sledeću fazu");
                    _btnFailedPhaseSkip.ToolTip = show
                        ? string.Format(LocalizationService.Get("WinRecoverySkipTooltipFmt", "Preskoči Fazu {0} i nastavi na Fazu {1}"), _failedPhase, _failedPhase + 1)
                        : null;
                }

                System.Windows.Controls.TextBlock copyrightTb = FindTaggedElement<System.Windows.Controls.TextBlock>(this, "FooterCopyrightText");
                if (copyrightTb != null)
                {
                    if (show)
                    {
                        copyrightTb.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        copyrightTb.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("IsNotServiceFinished")
                        {
                            Converter = new System.Windows.Controls.BooleanToVisibilityConverter()
                        });
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // V343-03 (Odluke 3A & 4A): Ekransko upozorenje za obavezne interaktivne testove
        // =====================================================================

        private void EnsurePendingInteractiveTestsUi()
        {
            try
            {
                if (_pendingInteractiveBanner == null)
                {
                    _pendingInteractiveBanner = FindTaggedElement<System.Windows.Controls.Border>(this, "PendingInteractiveTestsBanner");
                }
                if (_pendingInteractiveOverlay == null)
                {
                    _pendingInteractiveOverlay = FindTaggedElement<System.Windows.Controls.Border>(this, "PendingInteractiveTestsOverlay");
                }

                System.Windows.Controls.Grid rootGrid = this.Content as System.Windows.Controls.Grid;
                if (_pendingInteractiveBanner == null && rootGrid != null && MainTabControl != null)
                {
                    DependencyObject tabParent = LogicalTreeHelper.GetParent(MainTabControl) ?? VisualTreeHelper.GetParent(MainTabControl);
                    System.Windows.Controls.Grid directRoot = tabParent as System.Windows.Controls.Grid;
                    if (object.ReferenceEquals(directRoot, rootGrid))
                    {
                        int tabIdx = rootGrid.Children.IndexOf(MainTabControl);
                        int tabRow = System.Windows.Controls.Grid.GetRow(MainTabControl);
                        if (tabIdx >= 0)
                        {
                            rootGrid.Children.RemoveAt(tabIdx);
                            var wrapperGrid = new System.Windows.Controls.Grid();
                            wrapperGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                            wrapperGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                            System.Windows.Controls.Grid.SetRow(wrapperGrid, tabRow);

                            _pendingInteractiveBanner = BuildPendingInteractiveTestsBannerFallback();
                            System.Windows.Controls.Grid.SetRow(_pendingInteractiveBanner, 0);
                            wrapperGrid.Children.Add(_pendingInteractiveBanner);

                            System.Windows.Controls.Grid.SetRow(MainTabControl, 1);
                            wrapperGrid.Children.Add(MainTabControl);
                            rootGrid.Children.Insert(tabIdx, wrapperGrid);
                        }
                    }
                }

                if (_pendingInteractiveOverlay == null && rootGrid != null)
                {
                    _pendingInteractiveOverlay = BuildPendingInteractiveTestsOverlayFallback();
                    System.Windows.Controls.Grid.SetRow(_pendingInteractiveOverlay, 0);
                    System.Windows.Controls.Grid.SetRowSpan(_pendingInteractiveOverlay, 3);
                    System.Windows.Controls.Panel.SetZIndex(_pendingInteractiveOverlay, 90);
                    rootGrid.Children.Add(_pendingInteractiveOverlay);
                }

                if (_pendingInteractiveOverlay != null)
                {
                    if (_pendingOverlayBadgeTb == null)
                        _pendingOverlayBadgeTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveOverlay, "PendingOverlayBadgeText");
                    if (_pendingOverlayTitleTb == null)
                        _pendingOverlayTitleTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveOverlay, "PendingOverlayTitleText");
                    if (_pendingOverlaySubtitleTb == null)
                        _pendingOverlaySubtitleTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveOverlay, "PendingOverlaySubtitleText");
                    if (_pendingOverlayCountTb == null)
                        _pendingOverlayCountTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveOverlay, "PendingOverlayCountText");
                    if (_pendingOverlayListTb == null)
                        _pendingOverlayListTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveOverlay, "PendingOverlayListText");
                    if (_btnPendingStartTests == null)
                        _btnPendingStartTests = FindTaggedElement<System.Windows.Controls.Button>(_pendingInteractiveOverlay, "BtnPendingStartTests");
                    if (_btnPendingBrowseTabs == null)
                        _btnPendingBrowseTabs = FindTaggedElement<System.Windows.Controls.Button>(_pendingInteractiveOverlay, "BtnPendingBrowseTabs");
                    if (_btnPendingEmergencySkip == null)
                        _btnPendingEmergencySkip = FindTaggedElement<System.Windows.Controls.Button>(_pendingInteractiveOverlay, "BtnPendingEmergencySkip");
                }

                if (_pendingInteractiveBanner != null)
                {
                    if (_pendingBannerTitleTb == null)
                        _pendingBannerTitleTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveBanner, "PendingBannerTitleText");
                    if (_pendingBannerDetailTb == null)
                        _pendingBannerDetailTb = FindTaggedElement<System.Windows.Controls.TextBlock>(_pendingInteractiveBanner, "PendingBannerDetailText");
                    if (_btnBannerResumeTests == null)
                        _btnBannerResumeTests = FindTaggedElement<System.Windows.Controls.Button>(_pendingInteractiveBanner, "BtnBannerResumeTests");
                    if (_btnBannerEmergencySkip == null)
                        _btnBannerEmergencySkip = FindTaggedElement<System.Windows.Controls.Button>(_pendingInteractiveBanner, "BtnBannerEmergencySkip");
                }

                if (!_pendingInteractiveUiEventsWired &&
                    (_btnPendingStartTests != null || _btnBannerResumeTests != null))
                {
                    if (_btnPendingStartTests != null)
                    {
                        _btnPendingStartTests.Click += (s, e) => StartPendingInteractiveTestsFromUi();
                    }
                    if (_btnPendingBrowseTabs != null)
                    {
                        _btnPendingBrowseTabs.Click += (s, e) => HidePendingInteractiveTestsOverlayOnly();
                    }
                    if (_btnPendingEmergencySkip != null)
                    {
                        _btnPendingEmergencySkip.Click += (s, e) => RequestEmergencySkipPhase5WithConfirmation();
                    }
                    if (_btnBannerResumeTests != null)
                    {
                        _btnBannerResumeTests.Click += (s, e) => StartPendingInteractiveTestsFromUi();
                    }
                    if (_btnBannerEmergencySkip != null)
                    {
                        _btnBannerEmergencySkip.Click += (s, e) => RequestEmergencySkipPhase5WithConfirmation();
                    }
                    _pendingInteractiveUiEventsWired = true;
                }

                RefreshPendingInteractiveTestsLocalization();
            }
            catch { }
        }

        private static System.Windows.Controls.Border BuildPendingInteractiveTestsBannerFallback()
        {
            var banner = new System.Windows.Controls.Border
            {
                Tag = "PendingInteractiveTestsBanner",
                Background = MainViewModel.CreateFrozenBrush(51, 34, 10),
                BorderBrush = MainViewModel.CreateFrozenBrush(243, 156, 18),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(14, 10, 14, 0),
                Visibility = Visibility.Collapsed
            };

            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            leftStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingBannerTitleText",
                Text = LocalizationService.Get("WinPendingBannerTitle", "⚠️ OBAVEZNI INTERAKTIVNI TESTOVI NISU ZAVRŠENI"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(251, 191, 36)
            });
            leftStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingBannerDetailText",
                Text = LocalizationService.Get("WinPendingBannerDetail", "Automatski deo je završen. Završite preostale testove da bi se kreirao izveštaj i završni karton."),
                FontSize = 11.5,
                Foreground = MainViewModel.CreateFrozenBrush(253, 230, 138),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            System.Windows.Controls.Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            var rightStack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            rightStack.Children.Add(new System.Windows.Controls.Button
            {
                Tag = "BtnBannerResumeTests",
                Content = LocalizationService.Get("WinPendingBtnResume", "▶ Nastavi testove"),
                Background = MainViewModel.CreateFrozenBrush(230, 126, 34),
                Foreground = MainViewModel.CreateFrozenBrush(17, 17, 17),
                BorderBrush = MainViewModel.CreateFrozenBrush(245, 176, 65),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            });
            rightStack.Children.Add(new System.Windows.Controls.Button
            {
                Tag = "BtnBannerEmergencySkip",
                Content = LocalizationService.Get("WinPendingBtnEmergencySkip", "Vanredno preskoči..."),
                Background = MainViewModel.CreateFrozenBrush(42, 22, 22),
                Foreground = MainViewModel.CreateFrozenBrush(245, 183, 177),
                BorderBrush = MainViewModel.CreateFrozenBrush(192, 57, 43),
                BorderThickness = new Thickness(1),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = Cursors.Hand
            });
            System.Windows.Controls.Grid.SetColumn(rightStack, 1);
            grid.Children.Add(rightStack);

            banner.Child = grid;
            return banner;
        }

        private static System.Windows.Controls.Border BuildPendingInteractiveTestsOverlayFallback()
        {
            var bgOverlay = new SolidColorBrush(Color.FromArgb(235, 18, 18, 20));
            bgOverlay.Freeze();

            var overlayRoot = new System.Windows.Controls.Border
            {
                Tag = "PendingInteractiveTestsOverlay",
                Background = bgOverlay,
                Visibility = Visibility.Collapsed
            };

            var hostGrid = new System.Windows.Controls.Grid();
            var card = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(31, 25, 18),
                BorderBrush = MainViewModel.CreateFrozenBrush(243, 156, 18),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                MinWidth = 680,
                MaxWidth = 940,
                Margin = new Thickness(24),
                Padding = new Thickness(26, 22, 26, 22),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var stack = new System.Windows.Controls.StackPanel();

            var badge = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(243, 156, 18),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            badge.Child = new System.Windows.Controls.TextBlock
            {
                Tag = "PendingOverlayBadgeText",
                Text = LocalizationService.Get("WinPendingOverlayBadge", "OBAVEZNA SERVISNA AKCIJA — FAZA 5"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(17, 17, 17)
            };
            stack.Children.Add(badge);

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingOverlayTitleText",
                Text = LocalizationService.Get("WinPendingOverlayTitle", "⚠️ AUTOMATSKI PROCES JE ZAVRŠEN — OBAVEZNI INTERAKTIVNI TESTOVI"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(255, 183, 77),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 6)
            });

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingOverlaySubtitleText",
                Text = LocalizationService.Get("WinPendingOverlaySubtitle", "Automatski deo procesa (drajveri, optimizacije, programi i 30s stres test) je završen. Krajnji izveštaj za ServiceHub i Završni karton neće biti kreirani dok se svi obavezni interaktivni testovi hardvera ne završe."),
                FontSize = 13,
                Foreground = MainViewModel.CreateFrozenBrush(242, 230, 216),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var listBox = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(22, 18, 13),
                BorderBrush = MainViewModel.CreateFrozenBrush(217, 119, 6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var listStack = new System.Windows.Controls.StackPanel();
            listStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingOverlayCountText",
                Text = LocalizationService.Get("WinPendingOverlayCountTitle", "Preostali obavezni testovi (0):"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(251, 191, 36),
                Margin = new Thickness(0, 0, 0, 8)
            });
            listStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Tag = "PendingOverlayListText",
                Text = "",
                FontSize = 12.5,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });
            listBox.Child = listStack;
            stack.Children.Add(listBox);

            var btnWrap = new System.Windows.Controls.WrapPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            btnWrap.Children.Add(new System.Windows.Controls.Button
            {
                Tag = "BtnPendingStartTests",
                Content = LocalizationService.Get("WinPendingBtnStartTests", "▶ Pokreni interaktivne testove"),
                Background = MainViewModel.CreateFrozenBrush(230, 126, 34),
                Foreground = MainViewModel.CreateFrozenBrush(17, 17, 17),
                BorderBrush = MainViewModel.CreateFrozenBrush(245, 176, 65),
                BorderThickness = new Thickness(1.5),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 11, 20, 11),
                Margin = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand
            });
            btnWrap.Children.Add(new System.Windows.Controls.Button
            {
                Tag = "BtnPendingBrowseTabs",
                Content = LocalizationService.Get("WinPendingBtnBrowseTabs", "Pregledaj tabove"),
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                Foreground = Brushes.White,
                BorderBrush = MainViewModel.CreateFrozenBrush(102, 102, 110),
                BorderThickness = new Thickness(1),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(16, 11, 16, 11),
                Margin = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand
            });
            btnWrap.Children.Add(new System.Windows.Controls.Button
            {
                Tag = "BtnPendingEmergencySkip",
                Content = LocalizationService.Get("WinPendingBtnEmergencySkip", "Vanredno preskoči..."),
                Background = MainViewModel.CreateFrozenBrush(42, 22, 22),
                Foreground = MainViewModel.CreateFrozenBrush(245, 183, 177),
                BorderBrush = MainViewModel.CreateFrozenBrush(192, 57, 43),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand
            });
            stack.Children.Add(btnWrap);

            card.Child = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Content = stack
            };
            hostGrid.Children.Add(card);
            overlayRoot.Child = hostGrid;
            return overlayRoot;
        }

        private void RefreshPendingInteractiveTestsLocalization()
        {
            int cnt = _lastPendingInteractiveNames != null ? _lastPendingInteractiveNames.Count : 0;
            string inlineList = cnt > 0 ? string.Join(", ", _lastPendingInteractiveNames.ToArray()) : "";

            if (_pendingOverlayBadgeTb != null)
            {
                _pendingOverlayBadgeTb.Text = LocalizationService.Get("WinPendingOverlayBadge", "OBAVEZNA SERVISNA AKCIJA — FAZA 5");
            }
            if (_pendingOverlayTitleTb != null)
            {
                _pendingOverlayTitleTb.Text = LocalizationService.Get("WinPendingOverlayTitle", "⚠️ AUTOMATSKI PROCES JE ZAVRŠEN — OBAVEZNI INTERAKTIVNI TESTOVI");
            }
            if (_pendingOverlaySubtitleTb != null)
            {
                _pendingOverlaySubtitleTb.Text = LocalizationService.Get("WinPendingOverlaySubtitle", "Automatski deo procesa (drajveri, optimizacije, programi i 30s stres test) je završen. Krajnji izveštaj za ServiceHub i Završni karton neće biti kreirani dok se svi obavezni interaktivni testovi hardvera ne završe.");
            }
            if (_pendingOverlayCountTb != null)
            {
                _pendingOverlayCountTb.Text = string.Format(LocalizationService.Get("WinPendingOverlayCountFmt", "Preostalo {0} obaveznih interaktivnih testova:"), cnt);
            }
            if (_pendingOverlayListTb != null)
            {
                if (cnt > 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < cnt; i++)
                    {
                        if (i > 0) sb.AppendLine();
                        sb.Append("• ").Append(_lastPendingInteractiveNames[i]);
                    }
                    _pendingOverlayListTb.Text = sb.ToString();
                }
                else
                {
                    _pendingOverlayListTb.Text = "";
                }
            }
            if (_btnPendingStartTests != null)
            {
                _btnPendingStartTests.Content = LocalizationService.Get("WinPendingBtnStartTests", "▶ Pokreni interaktivne testove");
            }
            if (_btnPendingBrowseTabs != null)
            {
                _btnPendingBrowseTabs.Content = LocalizationService.Get("WinPendingBtnBrowseTabs", "Pregledaj tabove");
            }
            if (_btnPendingEmergencySkip != null)
            {
                _btnPendingEmergencySkip.Content = LocalizationService.Get("WinPendingBtnEmergencySkip", "Vanredno preskoči...");
            }

            if (_pendingBannerTitleTb != null)
            {
                _pendingBannerTitleTb.Text = string.Format(LocalizationService.Get("WinPendingBannerTitleFmt", "⚠️ OBAVEZNI INTERAKTIVNI TESTOVI NISU ZAVRŠENI (preostalo: {0})"), cnt);
            }
            if (_pendingBannerDetailTb != null)
            {
                _pendingBannerDetailTb.Text = string.Format(LocalizationService.Get("WinPendingBannerDetailFmt", "Preostalo: {0} — Krajnji izveštaj i Završni karton se kreiraju tek nakon završetka testova."), inlineList);
            }
            if (_btnBannerResumeTests != null)
            {
                _btnBannerResumeTests.Content = LocalizationService.Get("WinPendingBtnResume", "▶ Nastavi testove");
            }
            if (_btnBannerEmergencySkip != null)
            {
                _btnBannerEmergencySkip.Content = LocalizationService.Get("WinPendingBtnEmergencySkip", "Vanredno preskoči...");
            }
        }

        private void ShowPendingInteractiveTestsWarning(List<string> pendingNames, bool showOverlay, bool playSound)
        {
            Action showAction = delegate
            {
                EnsurePendingInteractiveTestsUi();

                List<string> resolved = (pendingNames != null && pendingNames.Count > 0)
                    ? pendingNames
                    : GetPendingInteractiveHwTestNames();

                _lastPendingInteractiveNames = resolved != null ? new List<string>(resolved) : new List<string>();
                if (_lastPendingInteractiveNames.Count == 0)
                {
                    HidePendingInteractiveTestsWarning();
                    return;
                }

                RefreshPendingInteractiveTestsLocalization();

                if (_pendingInteractiveBanner != null)
                {
                    _pendingInteractiveBanner.Visibility = Visibility.Visible;
                }
                if (showOverlay && _pendingInteractiveOverlay != null)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.IsCompleted = false;
                    }
                    _pendingInteractiveOverlay.Visibility = Visibility.Visible;
                    if (playSound)
                    {
                        PlayPendingTestsAttentionSound();
                    }
                }
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess()) showAction();
                else Dispatcher.BeginInvoke(showAction);
            }
        }

        private void HidePendingInteractiveTestsOverlayOnly()
        {
            Action hideAction = delegate
            {
                if (_pendingInteractiveOverlay != null)
                {
                    _pendingInteractiveOverlay.Visibility = Visibility.Collapsed;
                }
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess()) hideAction();
                else Dispatcher.BeginInvoke(hideAction);
            }
        }

        private void HidePendingInteractiveTestsWarning()
        {
            Action hideAction = delegate
            {
                _lastPendingInteractiveNames.Clear();
                if (_pendingInteractiveOverlay != null)
                {
                    _pendingInteractiveOverlay.Visibility = Visibility.Collapsed;
                }
                if (_pendingInteractiveBanner != null)
                {
                    _pendingInteractiveBanner.Visibility = Visibility.Collapsed;
                }
            };

            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                if (Dispatcher.CheckAccess()) hideAction();
                else Dispatcher.BeginInvoke(hideAction);
            }
        }

        private void ExecuteFailedPhaseRecheck()
        {
            if (_failedPhase != 4)
            {
                UpdateFailedPhaseRecoveryBar();
                return;
            }

            Process activePhaseProc = _currentPhaseProcess;
            if (activePhaseProc != null && !activePhaseProc.HasExited && ViewModel != null && ViewModel.CurrentPhase == 4)
            {
                ViewModel.AddLog("WARN", "RealityCheck", "Faza 4 je još uvek aktivna. Sačekajte završetak pre ponovne provere.");
                return;
            }

            lock (_appRetryLock)
            {
                if (_isSingleAppRetryRunning)
                {
                    ViewModel.AddLog("WARN", "RealityCheck", "Pojedinačni Retry programa je u toku. Sačekajte završetak pre ponovne provere.");
                    return;
                }
                _isSingleAppRetryRunning = true;
            }

            if (ViewModel != null)
            {
                ViewModel.AddLog("INFO", "RealityCheck", "Ponovna provera ključnih aplikacija na disku (Faza 4)...");
                ViewModel.LiveStatus = LocalizationService.Get("WinRcCheckingApps", "Proveravam prisustvo ključnih aplikacija na disku...");
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool shouldStartPhase5 = false;
                try
                {
                    List<string> missing = GetMissingKeyApplications();
                    if (missing.Count == 0)
                    {
                        string p4Script = FindPhaseScriptPath(AppDomain.CurrentDomain.BaseDirectory, "Phase4-Apps.ps1");
                        bool needShortcuts = _pendingPostRetryShortcutsRefresh || NeedsPhase4StepRefresh("Shortcuts");
                        bool needLicense = _pendingPostRetryLicenseRefresh || NeedsPhase4StepRefresh("License");
                        if (!string.IsNullOrEmpty(p4Script) && File.Exists(p4Script))
                        {
                            if (needShortcuts)
                            {
                                RunPhase4DependentStepSync("Shortcuts", p4Script, false);
                            }
                            if (needLicense)
                            {
                                RunPhase4DependentStepSync("License", p4Script, false);
                            }
                        }

                        if (PerformPhase4RealityCheck())
                        {
                            SetFailedPhase(0);
                            if (_telemetryService != null)
                            {
                                _telemetryService.PushLog("[SUCCESS] [RealityCheck] Post-Execution Reality Check (Faza 4) USPEŠAN: Sve ključne aplikacije su verifikovane na disku.");
                            }
                            SaveDeployStatePhase(5);
                            shouldStartPhase5 = true;
                        }
                    }
                    else
                    {
                        string missingListStr = string.Join(", ", missing.ToArray());
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("WARN", "RealityCheck", "Reality Check i dalje detektuje nedostajuće programe: " + missingListStr);
                                ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinRcStillMissingFmt", "Još uvek nedostaju: {0}. Pokrenite pojedinačni Retry na kartici Programi."), missingListStr);
                            }
                            MessageBox.Show(
                                string.Format(LocalizationService.Get("WinRcMissingMsgFmt", "Sledeći ključni programi još uvek nisu pronađeni na disku:\n\n{0}\n\nPokrenite pojedinačni ↻ Retry na kartici 'Programi' ili 'Ponovi Fazu 4'."), missingListStr),
                                LocalizationService.Get("WinRcMissingMsgTitle", "WIS Reality Check — Nedostaju programi"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }));
                    }
                }
                finally
                {
                    lock (_appRetryLock)
                    {
                        _isSingleAppRetryRunning = false;
                    }
                }

                if (shouldStartPhase5)
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("SUCCESS", "TaskRunner", "Oporavak Faze 4 uspešan! Nastavljam automatski na Fazu 5 (Hardverski testovi)...");
                        }
                        StartPhaseExecution(5);
                    }));
                }
            });
        }

        private void ExecuteSkipWithConfirmation()
        {
            if (_failedPhase <= 0) return;

            int failed = _failedPhase;
            int nextP = failed + 1;
            string extraWarn = "";
            if (failed == 4)
            {
                List<string> missing = GetMissingKeyApplications();
                if (missing.Count > 0)
                {
                    extraWarn = LocalizationService.Get("WinSkipMissingAppsHdr", "\n\nNedostaju sledeći programi na disku:\n- ") + string.Join("\n- ", missing.ToArray());
                }
            }

            string targetLabel = (nextP <= 6)
                ? string.Format(LocalizationService.Get("WinSkipPhaseTargetFmt", "Fazu {0}"), nextP)
                : LocalizationService.Get("WinSkipTargetFinish", "završetak servisa");
            MessageBoxResult res = MessageBox.Show(
                string.Format(LocalizationService.Get("WinSkipConfirmMsgFmt", "Da li ste sigurni da želite da preskočite neuspelu Fazu {0} i pređete na {1}?{2}"), failed, targetLabel, extraWarn),
                LocalizationService.Get("WinSkipConfirmTitle", "WIS Servis — Potvrda preskakanja faze"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (res == MessageBoxResult.Yes)
            {
                ExecuteSkip(null);
            }
        }

        private void EnsureBottomDualProgressCard(
            System.Windows.Controls.TabItem tab0,
            System.Windows.Controls.ScrollViewer sv,
            System.Windows.Controls.StackPanel overviewStack)
        {
            if (tab0 == null || sv == null || overviewStack == null) return;

            if (HasTaggedElement(tab0.Content as DependencyObject, "GlobalProgressCard"))
            {
                return;
            }

            // Ukloni staru srednju karticu sa ProgressBar-om
            for (int i = overviewStack.Children.Count - 1; i >= 0; i--)
            {
                FrameworkElement fe = overviewStack.Children[i] as FrameworkElement;
                if (fe == null) continue;
                if (ContainsProgressBar(fe))
                {
                    overviewStack.Children.RemoveAt(i);
                }
            }

            var cardBorder = new System.Windows.Controls.Border
            {
                Tag = "GlobalProgressCard",
                Background = MainViewModel.CreateFrozenBrush(37, 37, 38),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(6, 6, 6, 0)
            };

            var rootStack = new System.Windows.Controls.StackPanel();

            var topGrid = new System.Windows.Controls.Grid();
            topGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var leftTopGrid = new System.Windows.Controls.Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            leftTopGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            leftTopGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftLabelTb = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinGlobalProgressTitle", "UKUPAN NAPREDAK PROCESA"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.Grid.SetColumn(leftLabelTb, 0);
            leftTopGrid.Children.Add(leftLabelTb);

            var summaryTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            summaryTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("GlobalProgressSummary"));
            System.Windows.Controls.Grid.SetColumn(summaryTb, 1);
            leftTopGrid.Children.Add(summaryTb);

            System.Windows.Controls.Grid.SetColumn(leftTopGrid, 0);
            topGrid.Children.Add(leftTopGrid);

            var pctTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(0, 122, 204),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pctBind = new System.Windows.Data.Binding("GlobalProgress");
            pctBind.StringFormat = "{0}%";
            pctTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, pctBind);
            System.Windows.Controls.Grid.SetColumn(pctTb, 1);
            topGrid.Children.Add(pctTb);

            rootStack.Children.Add(topGrid);

            var globalBarGrid = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 5, 0, 8)
            };
            globalBarGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            globalBarGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var globalPb = new System.Windows.Controls.ProgressBar
            {
                Height = 18,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center
            };
            Style pbStyle = TryFindResource("DetailedProgressBarStyle") as Style;
            if (pbStyle != null)
            {
                globalPb.Style = pbStyle;
            }
            globalPb.SetBinding(System.Windows.Controls.ProgressBar.ValueProperty, new System.Windows.Data.Binding("GlobalProgress"));
            System.Windows.Controls.Grid.SetColumn(globalPb, 0);
            globalBarGrid.Children.Add(globalPb);

            var globalTimingStack = new System.Windows.Controls.StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 178
            };
            globalTimingStack.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("BenchmarkSourceTooltip"));
            var consolasFont = new FontFamily("Consolas");
            globalTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinTimingElapsedPrefix", "Traje "),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var globalElapsedTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontFamily = consolasFont,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(224, 224, 224),
                VerticalAlignment = VerticalAlignment.Center
            };
            globalElapsedTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("GlobalElapsedText"));
            globalTimingStack.Children.Add(globalElapsedTb);
            globalTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "  |  ",
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(78, 78, 86),
                VerticalAlignment = VerticalAlignment.Center
            });
            globalTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinTimingEtaPrefix", "ETA "),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var globalEtaTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontFamily = consolasFont,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                VerticalAlignment = VerticalAlignment.Center
            };
            globalEtaTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("GlobalEtaText"));
            globalTimingStack.Children.Add(globalEtaTb);

            System.Windows.Controls.Grid.SetColumn(globalTimingStack, 1);
            globalBarGrid.Children.Add(globalTimingStack);
            rootStack.Children.Add(globalBarGrid);

            rootStack.Children.Add(new System.Windows.Controls.Border
            {
                Height = 1,
                Background = MainViewModel.CreateFrozenBrush(62, 62, 66),
                Margin = new Thickness(0, 0, 0, 7)
            });

            rootStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinActiveStepsTitle", "AKTIVNI KORACI"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 0, 5)
            });

            var activeStepsHost = new System.Windows.Controls.StackPanel();
            var activeStepsScroll = new System.Windows.Controls.ScrollViewer
            {
                Tag = "ActiveStepBarsScroll",
                MaxHeight = 160,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Content = activeStepsHost
            };
            rootStack.Children.Add(activeStepsScroll);

            cardBorder.Child = rootStack;

            if (object.ReferenceEquals(tab0.Content, sv))
            {
                if (object.ReferenceEquals(sv.Content, overviewStack))
                {
                    overviewStack.Margin = new Thickness(6, 10, 6, 6);
                }
                tab0.Content = null;
                var dockGrid = new System.Windows.Controls.Grid();
                dockGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                dockGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                System.Windows.Controls.Grid.SetRow(sv, 0);
                System.Windows.Controls.Grid.SetRow(cardBorder, 1);
                dockGrid.Children.Add(sv);
                dockGrid.Children.Add(cardBorder);
                tab0.Content = dockGrid;
            }
            else
            {
                overviewStack.Children.Add(cardBorder);
            }

            RebuildActiveStepsHost(activeStepsHost);
            if (ViewModel != null && ViewModel.ActiveStepBars != null)
            {
                ViewModel.ActiveStepBars.CollectionChanged += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        RebuildActiveStepsHost(activeStepsHost);
                    });
                };
            }
        }

        private void EnsureGlobalTabsWatermark(System.Windows.Controls.Grid rootGrid, System.Windows.Controls.TabItem tab0)
        {
            try
            {
                // Ukloni eventualni stari watermark iz prvog taba da se ne bi duplirao
                if (tab0 != null)
                {
                    System.Windows.Controls.Grid tab0Grid = tab0.Content as System.Windows.Controls.Grid;
                    if (tab0Grid != null)
                    {
                        for (int i = tab0Grid.Children.Count - 1; i >= 0; i--)
                        {
                            FrameworkElement fe = tab0Grid.Children[i] as FrameworkElement;
                            if (fe != null && string.Equals(Convert.ToString(fe.Tag), "OverviewWatermarkWis", StringComparison.Ordinal))
                            {
                                tab0Grid.Children.RemoveAt(i);
                            }
                        }
                    }
                }

                if (rootGrid == null || HasTaggedElement(rootGrid, "GlobalWatermarkWis")) return;

                var watermarkHost = new System.Windows.Controls.Grid
                {
                    Tag = "GlobalWatermarkWis",
                    ClipToBounds = true,
                    IsHitTestVisible = false
                };
                var watermark = new System.Windows.Controls.TextBlock
                {
                    Text = "WIS",
                    FontFamily = new FontFamily("Segoe UI Black, Arial Black, Segoe UI"),
                    FontSize = 330,
                    FontWeight = FontWeights.Black,
                    Foreground = Brushes.White,
                    Opacity = 0.065,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 40, 90),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(-15),
                    IsHitTestVisible = false
                };
                watermarkHost.Children.Add(watermark);
                System.Windows.Controls.Grid.SetRow(watermarkHost, 1);

                int insertIdx = MainTabControl != null ? rootGrid.Children.IndexOf(MainTabControl) : -1;
                rootGrid.Children.Insert(insertIdx >= 0 ? insertIdx : rootGrid.Children.Count, watermarkHost);
            }
            catch { }
        }

        private void EnsureAutopilotTabAlertVisual(System.Windows.Controls.TabControl tabControl)
        {
            if (tabControl == null || tabControl.Items == null) return;
            try
            {
                System.Windows.Controls.TabItem autopilotTab = null;
                for (int i = 0; i < tabControl.Items.Count; i++)
                {
                    System.Windows.Controls.TabItem candidate = tabControl.Items[i] as System.Windows.Controls.TabItem;
                    if (candidate != null && IsAutopilotTabItem(candidate))
                    {
                        autopilotTab = candidate;
                        break;
                    }
                }
                if (autopilotTab == null) return;

                string tagStr = Convert.ToString(autopilotTab.Tag);
                if (string.Equals(tagStr, "AutopilotTabItem", StringComparison.Ordinal) ||
                    string.Equals(tagStr, "AutopilotTabAlertFramed", StringComparison.Ordinal))
                {
                    return;
                }

                autopilotTab.Tag = "AutopilotTabAlertFramed";
                autopilotTab.SetBinding(
                    System.Windows.Controls.HeaderedContentControl.HeaderProperty,
                    new System.Windows.Data.Binding("TabAutopilotHeader"));

                var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.TabItem));
                var rootFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Grid));

                var tabBorderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "tabBorder");
                tabBorderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
                tabBorderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, Brushes.Transparent);
                tabBorderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
                tabBorderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));

                var cpFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
                cpFactory.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty, new TemplateBindingExtension(System.Windows.Controls.HeaderedContentControl.HeaderProperty));
                cpFactory.SetValue(System.Windows.Controls.ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(System.Windows.Controls.HeaderedContentControl.HeaderTemplateProperty));
                cpFactory.SetValue(System.Windows.Controls.ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(System.Windows.Controls.HeaderedContentControl.HeaderStringFormatProperty));
                cpFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cpFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                tabBorderFactory.AppendChild(cpFactory);
                rootFactory.AppendChild(tabBorderFactory);

                var alertFrameFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "alertFrame");
                alertFrameFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(4));
                alertFrameFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(231, 76, 60));
                alertFrameFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(2));
                var alertBgBrush = new SolidColorBrush(Color.FromArgb(34, 218, 54, 51));
                alertBgBrush.Freeze();
                alertFrameFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, alertBgBrush);
                alertFrameFactory.SetValue(UIElement.IsHitTestVisibleProperty, false);
                alertFrameFactory.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                alertFrameFactory.SetValue(UIElement.OpacityProperty, 1.0);
                rootFactory.AppendChild(alertFrameFactory);

                tpl.VisualTree = rootFactory;

                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, MainViewModel.CreateFrozenBrush(37, 37, 38), "tabBorder"));
                hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
                tpl.Triggers.Add(hoverTrigger);

                var selectedTrigger = new Trigger { Property = System.Windows.Controls.TabItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, MainViewModel.CreateFrozenBrush(37, 37, 38), "tabBorder"));
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(0, 122, 204), "tabBorder"));
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, MainViewModel.CreateFrozenBrush(0, 122, 204)));
                tpl.Triggers.Add(selectedTrigger);

                var alertTrigger = new DataTrigger
                {
                    Binding = new System.Windows.Data.Binding("Autopilot.IsAlertActive"),
                    Value = true
                };
                alertTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "alertFrame"));
                alertTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Brushes.Transparent, "tabBorder"));
                alertTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, MainViewModel.CreateFrozenBrush(255, 138, 128)));
                tpl.Triggers.Add(alertTrigger);

                autopilotTab.Template = tpl;

                autopilotTab.Loaded += delegate
                {
                    SyncAutopilotTabAlertPulse(autopilotTab);
                };

                if (ViewModel != null && ViewModel.Autopilot != null)
                {
                    ViewModel.Autopilot.PropertyChanged += (s, e) =>
                    {
                        if (e != null && string.Equals(e.PropertyName, "IsAlertActive", StringComparison.Ordinal))
                        {
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                SyncAutopilotTabAlertPulse(autopilotTab);
                            });
                        }
                    };
                }

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)delegate
                {
                    SyncAutopilotTabAlertPulse(autopilotTab);
                });
            }
            catch { }
        }

        private static void RemoveTabByHeaderBinding(System.Windows.Controls.TabControl tabControl, string headerBindingPath)
        {
            if (tabControl == null || string.IsNullOrEmpty(headerBindingPath)) return;
            try
            {
                for (int i = tabControl.Items.Count - 1; i >= 0; i--)
                {
                    var ti = tabControl.Items[i] as System.Windows.Controls.TabItem;
                    if (ti == null) continue;
                    var bindingExpr = System.Windows.Data.BindingOperations.GetBinding(
                        ti, System.Windows.Controls.HeaderedContentControl.HeaderProperty);
                    if (bindingExpr != null && bindingExpr.Path != null &&
                        string.Equals(bindingExpr.Path.Path, headerBindingPath, StringComparison.Ordinal))
                    {
                        tabControl.Items.RemoveAt(i);
                    }
                }
            }
            catch { }
        }

        private void EnsureAppsTabRetryButtons(System.Windows.Controls.TabControl tabControl)
        {
            if (tabControl == null) return;
            try
            {
                System.Windows.Controls.TabItem appsTab = null;
                for (int i = 0; i < tabControl.Items.Count; i++)
                {
                    var ti = tabControl.Items[i] as System.Windows.Controls.TabItem;
                    if (ti == null) continue;
                    var hdrBind = System.Windows.Data.BindingOperations.GetBinding(
                        ti, System.Windows.Controls.HeaderedContentControl.HeaderProperty);
                    if (hdrBind != null && hdrBind.Path != null &&
                        string.Equals(hdrBind.Path.Path, "TabAppsHeader", StringComparison.Ordinal))
                    {
                        appsTab = ti;
                        break;
                    }
                }
                if (appsTab == null) return;

                DependencyObject tabContent = appsTab.Content as DependencyObject;
                object expectedSource = ViewModel != null ? ViewModel.Apps : null;
                System.Windows.Controls.ItemsControl appsControl = FindItemsControlBySourceBinding(tabContent, "Apps", expectedSource);
                if (appsControl == null) return;
                if (string.Equals(Convert.ToString(appsControl.Tag), "AppsItemsControlRetryPatched", StringComparison.Ordinal))
                {
                    return;
                }

                appsControl.ItemTemplate = CreateAppsCardRetryDataTemplate();
                appsControl.Tag = "AppsItemsControlRetryPatched";
            }
            catch { }
        }

        private static DataTemplate CreateAppsCardRetryDataTemplate()
        {
            var boolToVis = new System.Windows.Controls.BooleanToVisibilityConverter();

            var cardBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            cardBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1.5));
            cardBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            cardBorder.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12));
            cardBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(4));

            var cardStyle = new Style(typeof(System.Windows.Controls.Border));
            cardStyle.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, MainViewModel.CreateFrozenBrush(30, 30, 30)));
            cardStyle.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(45, 45, 48)));

            var dtRunning = new DataTrigger { Binding = new System.Windows.Data.Binding("IsRunning"), Value = true };
            dtRunning.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(0, 122, 204)));
            dtRunning.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, MainViewModel.CreateFrozenBrush(37, 42, 48)));
            cardStyle.Triggers.Add(dtRunning);

            var dtCompleted = new DataTrigger { Binding = new System.Windows.Data.Binding("IsCompleted"), Value = true };
            dtCompleted.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(39, 174, 96)));
            cardStyle.Triggers.Add(dtCompleted);

            var dtWarning = new DataTrigger { Binding = new System.Windows.Data.Binding("IsWarning"), Value = true };
            dtWarning.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(210, 153, 34)));
            cardStyle.Triggers.Add(dtWarning);

            var dtError = new DataTrigger { Binding = new System.Windows.Data.Binding("IsError"), Value = true };
            dtError.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(231, 76, 60)));
            cardStyle.Triggers.Add(dtError);

            cardBorder.SetValue(FrameworkElement.StyleProperty, cardStyle);

            var dockFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.DockPanel));
            dockFactory.SetValue(System.Windows.Controls.DockPanel.LastChildFillProperty, true);

            // Levo: Ikona programa
            var iconFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            iconFactory.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Left);
            iconFactory.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("IconGlyph"));
            iconFactory.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 24.0);
            iconFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
            dockFactory.AppendChild(iconFactory);

            // Desno: Statusni bedz + Retry dugme
            var actionsStack = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
            actionsStack.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            actionsStack.SetValue(System.Windows.Controls.StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
            actionsStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var badgeBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            badgeBorder.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("BadgeBackground"));
            badgeBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(4));
            badgeBorder.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(9, 5, 9, 5));
            badgeBorder.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var badgeText = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            badgeText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("IconText"));
            badgeText.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            badgeText.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.Bold);
            badgeText.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("BadgeForeground"));
            badgeBorder.AppendChild(badgeText);
            actionsStack.AppendChild(badgeBorder);

            var retryTemplate = new System.Windows.Controls.ControlTemplate(typeof(Button));
            var retryBorderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            retryBorderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
            retryBorderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
            retryBorderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
            retryBorderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(4));
            retryBorderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
            var retryContentFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            retryContentFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            retryContentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            retryBorderFactory.AppendChild(retryContentFactory);
            retryTemplate.VisualTree = retryBorderFactory;

            var retryBtnStyle = new Style(typeof(Button));
            retryBtnStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, MainViewModel.CreateFrozenBrush(59, 30, 32)));
            retryBtnStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, MainViewModel.CreateFrozenBrush(231, 76, 60)));
            retryBtnStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, retryTemplate));

            var dtWarnBtn = new DataTrigger { Binding = new System.Windows.Data.Binding("IsWarning"), Value = true };
            dtWarnBtn.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, MainViewModel.CreateFrozenBrush(56, 45, 24)));
            dtWarnBtn.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, MainViewModel.CreateFrozenBrush(210, 153, 34)));
            retryBtnStyle.Triggers.Add(dtWarnBtn);

            var trHoverBtn = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trHoverBtn.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, MainViewModel.CreateFrozenBrush(0, 122, 204)));
            trHoverBtn.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, MainViewModel.CreateFrozenBrush(56, 189, 248)));
            retryBtnStyle.Triggers.Add(trHoverBtn);

            var retryBtn = new FrameworkElementFactory(typeof(Button));
            retryBtn.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowRetryButton") { Converter = boolToVis });
            retryBtn.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("RetryButtonText"));
            retryBtn.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("RetryButtonTooltip"));
            retryBtn.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding("CanRetry"));
            retryBtn.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding("DataContext.RetryAppCommand")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor,
                    typeof(System.Windows.Controls.ItemsControl),
                    1)
            });
            retryBtn.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandParameterProperty, new System.Windows.Data.Binding());
            retryBtn.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            retryBtn.SetValue(System.Windows.Controls.Control.PaddingProperty, new Thickness(9, 4, 9, 4));
            retryBtn.SetValue(System.Windows.Controls.Control.FontSizeProperty, 11.0);
            retryBtn.SetValue(System.Windows.Controls.Control.FontWeightProperty, FontWeights.SemiBold);
            retryBtn.SetValue(System.Windows.Controls.Control.ForegroundProperty, Brushes.White);
            retryBtn.SetValue(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1));
            retryBtn.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            retryBtn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            retryBtn.SetValue(FrameworkElement.StyleProperty, retryBtnStyle);
            actionsStack.AppendChild(retryBtn);
            dockFactory.AppendChild(actionsStack);

            // Sredina (poslednje dete DockPanela): Naziv, detalj i mini ProgressBar
            var infoStack = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
            infoStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            infoStack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));

            var nameText = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            nameText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            nameText.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 13.0);
            nameText.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.Bold);
            nameText.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, Brushes.White);
            nameText.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            infoStack.AppendChild(nameText);

            var detailText = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            detailText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Detail"));
            detailText.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            detailText.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(170, 170, 170));
            detailText.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
            detailText.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            infoStack.AppendChild(detailText);

            var pbStyle = new Style(typeof(System.Windows.Controls.ProgressBar));
            pbStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            var dtPbRun = new DataTrigger { Binding = new System.Windows.Data.Binding("IsRunning"), Value = true };
            dtPbRun.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
            pbStyle.Triggers.Add(dtPbRun);

            var pbFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ProgressBar));
            pbFactory.SetValue(FrameworkElement.HeightProperty, 4.0);
            pbFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
            pbFactory.SetValue(System.Windows.Controls.ProgressBar.IsIndeterminateProperty, true);
            pbFactory.SetValue(System.Windows.Controls.Control.ForegroundProperty, MainViewModel.CreateFrozenBrush(0, 122, 204));
            pbFactory.SetValue(System.Windows.Controls.Control.BackgroundProperty, MainViewModel.CreateFrozenBrush(45, 45, 48));
            pbFactory.SetValue(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0));
            pbFactory.SetValue(FrameworkElement.StyleProperty, pbStyle);
            infoStack.AppendChild(pbFactory);

            dockFactory.AppendChild(infoStack);
            cardBorder.AppendChild(dockFactory);

            var tpl = new DataTemplate();
            tpl.VisualTree = cardBorder;
            return tpl;
        }

        private void EnsureHwMonitorTabLayout(System.Windows.Controls.TabControl tabControl)
        {
            if (tabControl == null) return;
            System.Windows.Controls.TabItem diagTab = null;
            foreach (object obj in tabControl.Items)
            {
                System.Windows.Controls.TabItem ti = obj as System.Windows.Controls.TabItem;
                if (ti == null) continue;
                string tag = Convert.ToString(ti.Tag);
                if (string.Equals(tag, "HwMonitorTabRedesignedV1", StringComparison.Ordinal))
                {
                    return;
                }
                System.Windows.Data.Binding hdrBind = System.Windows.Data.BindingOperations.GetBinding(
                    ti,
                    System.Windows.Controls.HeaderedContentControl.HeaderProperty);
                if (hdrBind != null && hdrBind.Path != null &&
                    string.Equals(hdrBind.Path.Path, "TabDiagnosticsHeader", StringComparison.Ordinal))
                {
                    diagTab = ti;
                    break;
                }
            }
            if (diagTab == null) return;

            diagTab.Content = BuildDetailedHwMonitorContent();
            diagTab.Tag = "HwMonitorTabRedesignedV1";
        }

        private UIElement BuildDetailedHwMonitorContent()
        {
            var rootGrid = new System.Windows.Controls.Grid { Margin = new Thickness(14) };
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Gornja kontrolna traka
            var topCard = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(26, 26, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var topDock = new System.Windows.Controls.DockPanel { LastChildFill = true };

            var rightStatsStack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.DockPanel.SetDock(rightStatsStack, System.Windows.Controls.Dock.Right);

            var sysStatsBorder = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(36, 36, 42),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 72),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            var sysStatsText = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(200, 210, 224),
                VerticalAlignment = VerticalAlignment.Center
            };
            sysStatsText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("HwSystemStatsText"));
            sysStatsBorder.Child = sysStatsText;
            rightStatsStack.Children.Add(sysStatsBorder);

            var sensorBadge = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(24, 34, 46),
                BorderBrush = MainViewModel.CreateFrozenBrush(56, 100, 140),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            var sensorStatusTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            sensorStatusTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("ThermalSensorStatus"));
            sensorStatusTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("ThermalSensorStatusBrush"));
            sensorBadge.Child = sensorStatusTb;
            rightStatsStack.Children.Add(sensorBadge);

            topDock.Children.Add(rightStatsStack);

            var leftActions = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleTb = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonHeader", "HW MONITOR  •  SISTEMSKA TELEMETRIJA UŽIVO"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(56, 189, 248),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            leftActions.Children.Add(titleTb);

            var btnReset = CreateHwActionButton(
                LocalizationService.Get("WinHwMonBtnReset", "↺ Reset Min/Max"),
                "ResetCoreClocksCommand",
                MainViewModel.CreateFrozenBrush(45, 45, 52),
                MainViewModel.CreateFrozenBrush(82, 82, 92),
                Brushes.White);
            btnReset.Margin = new Thickness(0, 0, 8, 0);
            leftActions.Children.Add(btnReset);

            var btnStartStress = CreateHwActionButton(
                LocalizationService.Get("WinHwMonBtnStartStress", "▶ Start Stres"),
                "RunThermalStressCommand",
                MainViewModel.CreateFrozenBrush(18, 66, 48),
                MainViewModel.CreateFrozenBrush(16, 185, 129),
                MainViewModel.CreateFrozenBrush(220, 252, 231));
            btnStartStress.Margin = new Thickness(0, 0, 8, 0);
            var startVisStyle = new Style(typeof(System.Windows.Controls.Button), btnStartStress.Style);
            var dtHideStart = new DataTrigger { Binding = new System.Windows.Data.Binding("IsStressTestRunning"), Value = true };
            dtHideStart.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            startVisStyle.Triggers.Add(dtHideStart);
            btnStartStress.Style = startVisStyle;
            leftActions.Children.Add(btnStartStress);

            var btnStopStress = CreateHwActionButton(
                LocalizationService.Get("WinHwMonBtnStopStress", "⏹ Stop Stres"),
                "CancelThermalStressCommand",
                MainViewModel.CreateFrozenBrush(72, 26, 28),
                MainViewModel.CreateFrozenBrush(239, 68, 68),
                MainViewModel.CreateFrozenBrush(254, 226, 226));
            btnStopStress.Margin = new Thickness(0, 0, 8, 0);
            var stopVisStyle = new Style(typeof(System.Windows.Controls.Button), btnStopStress.Style);
            stopVisStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            var dtShowStop = new DataTrigger { Binding = new System.Windows.Data.Binding("IsStressTestRunning"), Value = true };
            dtShowStop.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
            stopVisStyle.Triggers.Add(dtShowStop);
            btnStopStress.Style = stopVisStyle;
            leftActions.Children.Add(btnStopStress);

            var stressStatusTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11.5,
                Foreground = MainViewModel.CreateFrozenBrush(203, 213, 225),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stressStatusTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("StressTestStatusText"));
            leftActions.Children.Add(stressStatusTb);

            topDock.Children.Add(leftActions);
            topCard.Child = topDock;
            System.Windows.Controls.Grid.SetRow(topCard, 0);
            rootGrid.Children.Add(topCard);

            // 2. 5 živih KPI kartica
            var kpiGrid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = 5,
                Rows = 1,
                Margin = new Thickness(0, 0, 0, 10)
            };

            kpiGrid.Children.Add(CreateHwKpiCard(
                LocalizationService.Get("WinHwMonKpiCpu", "CPU / PROCESOR"),
                "CpuTempText", "CpuTempBrush",
                "CpuUsageShortText", "HwCpuAvgClockText", "HwCpuKpiSubText",
                "CpuUsagePercent", MainViewModel.CreateFrozenBrush(56, 189, 248),
                new Thickness(0, 0, 6, 0)));

            kpiGrid.Children.Add(CreateHwKpiCard(
                LocalizationService.Get("WinHwMonKpiRam", "RAM MEMORIJA"),
                "RamUsageShortText", "RamUsageBrush",
                "HwRamAvailableText", "HwRamCommitText", "HardwareInfo.RamSummary",
                "RamUsagePercent", MainViewModel.CreateFrozenBrush(96, 165, 250),
                new Thickness(3, 0, 3, 0)));

            kpiGrid.Children.Add(CreateHwKpiCard(
                LocalizationService.Get("WinHwMonKpiGpu", "GRAFIKA (GPU)"),
                "GpuTempText", "GpuTempBrush",
                "FanSpeedText", "HardwareInfo.GpuPrimary", "HwGpuKpiSubText",
                "GpuTemp", MainViewModel.CreateFrozenBrush(192, 132, 252),
                new Thickness(3, 0, 3, 0)));

            kpiGrid.Children.Add(CreateHwKpiCard(
                LocalizationService.Get("WinHwMonKpiDisk", "DISK / SSD I/O"),
                "SsdTempText", "SsdTempBrush",
                "HwDiskIoText", "MbTempText", "ThrottlingText",
                "HwDiskActivePercent", MainViewModel.CreateFrozenBrush(16, 185, 129),
                new Thickness(3, 0, 3, 0)));

            kpiGrid.Children.Add(CreateHwKpiCard(
                LocalizationService.Get("WinHwMonKpiPower", "NAPAJANJE / BATERIJA"),
                "PowerDrawText", "PowerDrawBrush",
                "HwPowerVoltageText", "PowerStatusTitle", "HwPowerSubText",
                "BatteryChargePercent", MainViewModel.CreateFrozenBrush(245, 158, 11),
                new Thickness(6, 0, 0, 0)));

            System.Windows.Controls.Grid.SetRow(kpiGrid, 1);
            rootGrid.Children.Add(kpiGrid);

            // 3. 6-kanalni 5-minutni grafikon
            var graphCard = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(24, 24, 28),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var graphStack = new System.Windows.Controls.StackPanel();

            var legendDock = new System.Windows.Controls.DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var graphTitle = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonGraphTitle", "TERMALNA KRIVA & CPU OPTEREĆENJE (5 MIN / 1 Hz)"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(226, 232, 240),
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Controls.DockPanel.SetDock(graphTitle, System.Windows.Controls.Dock.Left);
            legendDock.Children.Add(graphTitle);

            var legendItems = new System.Windows.Controls.WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(56, 189, 248), "LegendCpuText"));
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(96, 165, 250), "LegendCpuLoadText"));
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(192, 132, 252), "LegendGpuText"));
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(245, 158, 11), "LegendMbText"));
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(16, 185, 129), "LegendSsdText"));
            legendItems.Children.Add(CreateGraphLegendPill(MainViewModel.CreateFrozenBrush(239, 68, 68), "LegendThrottleText"));
            legendDock.Children.Add(legendItems);
            graphStack.Children.Add(legendDock);

            var graphHost = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(18, 18, 22),
                BorderBrush = MainViewModel.CreateFrozenBrush(42, 42, 48),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Height = 148,
                Padding = new Thickness(4)
            };
            var viewBox = new System.Windows.Controls.Viewbox { Stretch = Stretch.Fill };
            var canvas = new System.Windows.Controls.Canvas { Width = 900, Height = 200, ClipToBounds = true };

            Brush gridStroke = MainViewModel.CreateFrozenBrush(42, 46, 56);
            double[] yTicks = new double[] { 50, 100, 150 };
            foreach (double yVal in yTicks)
            {
                var hLine = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = 900,
                    Y1 = yVal,
                    Y2 = yVal,
                    Stroke = gridStroke,
                    StrokeThickness = 1,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 4 }
                };
                canvas.Children.Add(hLine);
            }

            canvas.Children.Add(CreateHwMonitorGraphPolyline("CpuLoadGraphPoints", MainViewModel.CreateFrozenBrush(96, 165, 250), 1.6, true));
            canvas.Children.Add(CreateHwMonitorGraphPolyline("MbGraphPoints", MainViewModel.CreateFrozenBrush(245, 158, 11), 1.8, false));
            canvas.Children.Add(CreateHwMonitorGraphPolyline("SsdGraphPoints", MainViewModel.CreateFrozenBrush(16, 185, 129), 1.8, false));
            canvas.Children.Add(CreateHwMonitorGraphPolyline("GpuGraphPoints", MainViewModel.CreateFrozenBrush(192, 132, 252), 2.0, false));
            canvas.Children.Add(CreateHwMonitorGraphPolyline("ThrottleGraphPoints", MainViewModel.CreateFrozenBrush(239, 68, 68), 1.8, false));
            canvas.Children.Add(CreateHwMonitorGraphPolyline("CpuGraphPoints", MainViewModel.CreateFrozenBrush(56, 189, 248), 2.4, false));

            viewBox.Child = canvas;
            graphHost.Child = viewBox;
            graphStack.Children.Add(graphHost);
            graphCard.Child = graphStack;
            System.Windows.Controls.Grid.SetRow(graphCard, 2);
            rootGrid.Children.Add(graphCard);

            // 4. Donji 2-kolonski panel (levo senzori MIN/TRENUTNO/MAX, desno taktovi jezgara + podsistemi)
            var bottomGrid = new System.Windows.Controls.Grid();
            bottomGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(10) });
            bottomGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            // Leva kartica: tabela senzora
            var sensorsCard = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(24, 24, 28),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var sensorsLayout = new System.Windows.Controls.Grid();
            sensorsLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            sensorsLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            sensorsLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var sensorsTitle = new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonSensorsTitle", "DETALJNI SENZORI (MIN / TRENUTNO / MAX)"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(226, 232, 240),
                Margin = new Thickness(0, 0, 0, 6)
            };
            System.Windows.Controls.Grid.SetRow(sensorsTitle, 0);
            sensorsLayout.Children.Add(sensorsTitle);

            var sensorHeaderRow = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(36, 36, 42),
                BorderBrush = MainViewModel.CreateFrozenBrush(58, 58, 68),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var shDock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            shDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColMax", "MAX"), 76));
            shDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColMin", "MIN"), 76));
            shDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColCurrent", "TRENUTNO"), 82));
            shDock.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonColSensor", "SENZOR"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184)
            });
            sensorHeaderRow.Child = shDock;
            System.Windows.Controls.Grid.SetRow(sensorHeaderRow, 1);
            sensorsLayout.Children.Add(sensorHeaderRow);

            var sensorsScroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            var sensorsList = new System.Windows.Controls.ItemsControl
            {
                ItemTemplate = CreateHwMonitorSensorsTemplate()
            };
            sensorsList.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("HwMonitorSensors"));
            sensorsScroll.Content = sensorsList;
            System.Windows.Controls.Grid.SetRow(sensorsScroll, 2);
            sensorsLayout.Children.Add(sensorsScroll);

            sensorsCard.Child = sensorsLayout;
            System.Windows.Controls.Grid.SetColumn(sensorsCard, 0);
            bottomGrid.Children.Add(sensorsCard);

            // Desna kolona: Taktovi jezgara + Podsistemi
            var rightCol = new System.Windows.Controls.Grid();
            rightCol.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightCol.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(10) });
            rightCol.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var clocksCard = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(24, 24, 28),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var clocksLayout = new System.Windows.Controls.Grid();
            clocksLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            clocksLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            clocksLayout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var clocksTitleDock = new System.Windows.Controls.DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var avgClockTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(56, 189, 248)
            };
            avgClockTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("HwCpuAvgClockText"));
            System.Windows.Controls.DockPanel.SetDock(avgClockTb, System.Windows.Controls.Dock.Right);
            clocksTitleDock.Children.Add(avgClockTb);
            clocksTitleDock.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonClocksTitle", "TAKTOVI PROCESORSKIH JEZGARA"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(226, 232, 240)
            });
            System.Windows.Controls.Grid.SetRow(clocksTitleDock, 0);
            clocksLayout.Children.Add(clocksTitleDock);

            var clockHeaderRow = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(36, 36, 42),
                BorderBrush = MainViewModel.CreateFrozenBrush(58, 58, 68),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var chDock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            chDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColMax", "MAX"), 68));
            chDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColMin", "MIN"), 68));
            chDock.Children.Add(CreateFixedHeaderCol(LocalizationService.Get("WinHwMonColCurrent", "TRENUTNO"), 74));
            chDock.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonColCore", "JEZGRO"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184)
            });
            clockHeaderRow.Child = chDock;
            System.Windows.Controls.Grid.SetRow(clockHeaderRow, 1);
            clocksLayout.Children.Add(clockHeaderRow);

            var clocksScroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            var clocksList = new System.Windows.Controls.ItemsControl
            {
                ItemTemplate = CreateCoreClocksTemplate()
            };
            clocksList.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("CoreClocks"));
            clocksScroll.Content = clocksList;
            System.Windows.Controls.Grid.SetRow(clocksScroll, 2);
            clocksLayout.Children.Add(clocksScroll);

            clocksCard.Child = clocksLayout;
            System.Windows.Controls.Grid.SetRow(clocksCard, 0);
            rightCol.Children.Add(clocksCard);

            // Kartica: Podsistemi i napajanje
            var subsysCard = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(24, 24, 28),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var subsysStack = new System.Windows.Controls.StackPanel();
            subsysStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinHwMonSubsysTitle", "PODSISTEMI & DIJAGNOSTIKA"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(226, 232, 240),
                Margin = new Thickness(0, 0, 0, 6)
            });
            subsysStack.Children.Add(CreateHwSubsystemRow(LocalizationService.Get("WinHwMonSubNet", "Mreža (RX / TX)"), "HwNetRateText", MainViewModel.CreateFrozenBrush(56, 189, 248)));
            subsysStack.Children.Add(CreateHwSubsystemRow(LocalizationService.Get("WinHwMonSubDisk", "Disk I/O"), "HwDiskIoText", MainViewModel.CreateFrozenBrush(16, 185, 129)));
            subsysStack.Children.Add(CreateHwSubsystemRow(LocalizationService.Get("WinHwMonSubPower", "Napajanje"), "PowerDrawText", MainViewModel.CreateFrozenBrush(245, 158, 11)));
            subsysStack.Children.Add(CreateHwSubsystemRow(LocalizationService.Get("WinHwMonSubVolt", "Režim / Napon"), "HwPowerVoltageText", MainViewModel.CreateFrozenBrush(203, 213, 225)));
            subsysStack.Children.Add(CreateHwSubsystemRow(LocalizationService.Get("WinHwMonSubThermal", "Term. ocena"), "ThermalVerdictTitle", null, "ThermalVerdictBrush"));
            subsysCard.Child = subsysStack;
            System.Windows.Controls.Grid.SetRow(subsysCard, 2);
            rightCol.Children.Add(subsysCard);

            System.Windows.Controls.Grid.SetColumn(rightCol, 2);
            bottomGrid.Children.Add(rightCol);

            System.Windows.Controls.Grid.SetRow(bottomGrid, 3);
            rootGrid.Children.Add(bottomGrid);

            return rootGrid;
        }

        private System.Windows.Controls.Button CreateHwActionButton(string text, string commandBindingPath, Brush bgBrush, Brush borderBrush, Brush fgBrush)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = text,
                Foreground = fgBrush,
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding(commandBindingPath));
            return btn;
        }

        private System.Windows.Controls.Border CreateHwKpiCard(
            string title,
            string mainPath,
            string mainBrushPath,
            string line2Path,
            string line3Path,
            string line4Path,
            string barValuePath,
            Brush barBrush,
            Thickness margin)
        {
            var card = new System.Windows.Controls.Border
            {
                Background = MainViewModel.CreateFrozenBrush(26, 26, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(54, 54, 62),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = margin
            };
            var st = new System.Windows.Controls.StackPanel();

            st.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184)
            });

            var mainTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 2, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            mainTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(mainPath));
            mainTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding(mainBrushPath));
            st.Children.Add(mainTb);

            var l2 = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(226, 232, 240),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            l2.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(line2Path));
            st.Children.Add(l2);

            var l3 = new System.Windows.Controls.TextBlock
            {
                FontSize = 10.5,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            l3.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(line3Path));
            st.Children.Add(l3);

            var l4 = new System.Windows.Controls.TextBlock
            {
                FontSize = 10.5,
                Foreground = MainViewModel.CreateFrozenBrush(130, 145, 165),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 5)
            };
            l4.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(line4Path));
            st.Children.Add(l4);

            var pb = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 4,
                Foreground = barBrush,
                Background = MainViewModel.CreateFrozenBrush(42, 42, 48),
                BorderThickness = new Thickness(0)
            };
            pb.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding(barValuePath));
            st.Children.Add(pb);

            card.Child = st;
            return card;
        }

        private System.Windows.Controls.StackPanel CreateGraphLegendPill(Brush dotBrush, string textBindingPath)
        {
            var pill = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            pill.Children.Add(new System.Windows.Controls.Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                Background = dotBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            var tb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(203, 213, 225),
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(textBindingPath));
            pill.Children.Add(tb);
            return pill;
        }

        private System.Windows.Shapes.Polyline CreateHwMonitorGraphPolyline(string pointsBindingPath, Brush strokeBrush, double thickness, bool dashed)
        {
            var poly = new System.Windows.Shapes.Polyline
            {
                Stroke = strokeBrush,
                StrokeThickness = thickness,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
            };
            if (dashed)
            {
                poly.StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 };
            }
            poly.SetBinding(System.Windows.Shapes.Polyline.PointsProperty, new System.Windows.Data.Binding(pointsBindingPath));
            return poly;
        }

        private System.Windows.Controls.TextBlock CreateFixedHeaderCol(string label, double width)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = label,
                Width = width,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184),
                TextAlignment = TextAlignment.Right
            };
            System.Windows.Controls.DockPanel.SetDock(tb, System.Windows.Controls.Dock.Right);
            return tb;
        }

        private DataTemplate CreateHwMonitorSensorsTemplate()
        {
            var rowBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            rowBorder.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("RowBackground"));
            rowBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(38, 38, 45));
            rowBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            rowBorder.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(8, 4, 8, 4));

            var dock = new FrameworkElementFactory(typeof(System.Windows.Controls.DockPanel));
            dock.SetValue(System.Windows.Controls.DockPanel.LastChildFillProperty, true);

            var maxTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            maxTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            maxTb.SetValue(FrameworkElement.WidthProperty, 76.0);
            maxTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
            maxTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            maxTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            maxTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(248, 113, 113));
            maxTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("MaxText"));
            dock.AppendChild(maxTb);

            var minTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            minTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            minTb.SetValue(FrameworkElement.WidthProperty, 76.0);
            minTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
            minTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            minTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            minTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(148, 163, 184));
            minTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("MinText"));
            dock.AppendChild(minTb);

            var curTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            curTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            curTb.SetValue(FrameworkElement.WidthProperty, 82.0);
            curTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
            curTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            curTb.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.Bold);
            curTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            curTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("ValueBrush"));
            curTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CurrentText"));
            dock.AppendChild(curTb);

            var nameTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            nameTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
            nameTb.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("NameForeground"));
            nameTb.SetBinding(System.Windows.Controls.TextBlock.FontWeightProperty, new System.Windows.Data.Binding("NameFontWeight"));
            nameTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            dock.AppendChild(nameTb);

            rowBorder.AppendChild(dock);
            var tpl = new DataTemplate();
            tpl.VisualTree = rowBorder;
            return tpl;
        }

        private DataTemplate CreateCoreClocksTemplate()
        {
            var rowBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            rowBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, MainViewModel.CreateFrozenBrush(38, 38, 45));
            rowBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            rowBorder.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(8, 4, 8, 4));

            var dock = new FrameworkElementFactory(typeof(System.Windows.Controls.DockPanel));
            dock.SetValue(System.Windows.Controls.DockPanel.LastChildFillProperty, true);

            var maxTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            maxTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            maxTb.SetValue(FrameworkElement.WidthProperty, 68.0);
            maxTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            maxTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            maxTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            maxTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(248, 113, 113));
            maxTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("MaxClockText"));
            dock.AppendChild(maxTb);

            var minTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            minTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            minTb.SetValue(FrameworkElement.WidthProperty, 68.0);
            minTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            minTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            minTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            minTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(148, 163, 184));
            minTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("MinClockText"));
            dock.AppendChild(minTb);

            var curTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            curTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            curTb.SetValue(FrameworkElement.WidthProperty, 74.0);
            curTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            curTb.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            curTb.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.Bold);
            curTb.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right);
            curTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(56, 189, 248));
            curTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CurrentClockText"));
            dock.AppendChild(curTb);

            var leftDock = new FrameworkElementFactory(typeof(System.Windows.Controls.DockPanel));
            leftDock.SetValue(System.Windows.Controls.DockPanel.LastChildFillProperty, true);
            leftDock.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));

            var nameTb = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            nameTb.SetValue(System.Windows.Controls.DockPanel.DockProperty, System.Windows.Controls.Dock.Left);
            nameTb.SetValue(FrameworkElement.WidthProperty, 68.0);
            nameTb.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
            nameTb.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTb.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, MainViewModel.CreateFrozenBrush(226, 232, 240));
            nameTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CoreName"));
            leftDock.AppendChild(nameTb);

            var bar = new FrameworkElementFactory(typeof(System.Windows.Controls.ProgressBar));
            bar.SetValue(System.Windows.Controls.Primitives.RangeBase.MinimumProperty, 0.0);
            bar.SetValue(System.Windows.Controls.Primitives.RangeBase.MaximumProperty, 100.0);
            bar.SetValue(FrameworkElement.HeightProperty, 6.0);
            bar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bar.SetValue(System.Windows.Controls.Control.ForegroundProperty, MainViewModel.CreateFrozenBrush(56, 189, 248));
            bar.SetValue(System.Windows.Controls.Control.BackgroundProperty, MainViewModel.CreateFrozenBrush(42, 42, 48));
            bar.SetValue(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0));
            bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new System.Windows.Data.Binding("ClockRatioPercent"));
            leftDock.AppendChild(bar);

            dock.AppendChild(leftDock);
            rowBorder.AppendChild(dock);

            var tpl = new DataTemplate();
            tpl.VisualTree = rowBorder;
            return tpl;
        }

        private System.Windows.Controls.Border CreateHwSubsystemRow(string label, string valueBindingPath, Brush staticBrush, string brushBindingPath = null)
        {
            var row = new System.Windows.Controls.Border
            {
                BorderBrush = MainViewModel.CreateFrozenBrush(38, 38, 45),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 4, 0, 4)
            };
            var dock = new System.Windows.Controls.DockPanel { LastChildFill = true };
            var valTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (!string.IsNullOrEmpty(brushBindingPath))
            {
                valTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding(brushBindingPath));
            }
            else if (staticBrush != null)
            {
                valTb.Foreground = staticBrush;
            }
            valTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(valueBindingPath));
            System.Windows.Controls.DockPanel.SetDock(valTb, System.Windows.Controls.Dock.Right);
            dock.Children.Add(valTb);

            dock.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = MainViewModel.CreateFrozenBrush(148, 163, 184),
                Margin = new Thickness(0, 0, 8, 0)
            });
            row.Child = dock;
            return row;
        }

        private static bool IsAutopilotTabItem(System.Windows.Controls.TabItem ti)
        {
            if (ti == null) return false;
            string tag = Convert.ToString(ti.Tag);
            if (string.Equals(tag, "AutopilotTabItem", StringComparison.Ordinal) ||
                string.Equals(tag, "AutopilotTabAlertFramed", StringComparison.Ordinal))
            {
                return true;
            }
            System.Windows.Data.Binding hdrBind = System.Windows.Data.BindingOperations.GetBinding(
                ti,
                System.Windows.Controls.HeaderedContentControl.HeaderProperty);
            if (hdrBind != null && hdrBind.Path != null &&
                string.Equals(hdrBind.Path.Path, "TabAutopilotHeader", StringComparison.Ordinal))
            {
                return true;
            }
            DependencyObject headerDep = ti.Header as DependencyObject;
            return ContainsAutopilotHeaderElement(headerDep);
        }

        private static bool ContainsAutopilotHeaderElement(DependencyObject node)
        {
            if (node == null) return false;
            FrameworkElement fe = node as FrameworkElement;
            if (fe != null && string.Equals(fe.Name, "AlertBadge", StringComparison.Ordinal))
            {
                return true;
            }
            System.Windows.Controls.TextBlock tb = node as System.Windows.Controls.TextBlock;
            if (tb != null)
            {
                System.Windows.Data.Binding tbBind = System.Windows.Data.BindingOperations.GetBinding(tb, System.Windows.Controls.TextBlock.TextProperty);
                if (tbBind != null && tbBind.Path != null &&
                    string.Equals(tbBind.Path.Path, "TabAutopilotHeader", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null && ContainsAutopilotHeaderElement(dep))
                {
                    return true;
                }
            }
            return false;
        }

        private void SyncAutopilotTabAlertPulse(System.Windows.Controls.TabItem autopilotTab)
        {
            if (autopilotTab == null) return;
            try
            {
                autopilotTab.ApplyTemplate();
                if (autopilotTab.Template == null) return;
                System.Windows.Controls.Border alertFrame = autopilotTab.Template.FindName("alertFrame", autopilotTab) as System.Windows.Controls.Border;
                if (alertFrame == null) return;

                bool active = ViewModel != null && ViewModel.Autopilot != null && ViewModel.Autopilot.IsAlertActive;
                if (active)
                {
                    var pulseAnim = new System.Windows.Media.Animation.DoubleAnimation(
                        1.0,
                        0.18,
                        new Duration(TimeSpan.FromMilliseconds(850)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                    };
                    alertFrame.BeginAnimation(UIElement.OpacityProperty, pulseAnim);
                }
                else
                {
                    alertFrame.BeginAnimation(UIElement.OpacityProperty, null);
                    alertFrame.Opacity = 1.0;
                }
            }
            catch { }
        }

        private void EnsureFooterServiceHubIndicator(DependencyObject footerRoot)
        {
            if (footerRoot == null || HasTaggedElement(footerRoot, "FooterServiceHubStatus")) return;
            try
            {
                System.Windows.Controls.TextBlock tb = FindTextBlockByTextBinding(footerRoot, "IpcStatusText");
                if (tb == null) return;

                tb.SetBinding(
                    System.Windows.Controls.TextBlock.TextProperty,
                    new System.Windows.Data.Binding("ServiceHubFooterText"));
                tb.FontWeight = FontWeights.SemiBold;
                tb.Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204);

                System.Windows.Controls.StackPanel sp = LogicalTreeHelper.GetParent(tb) as System.Windows.Controls.StackPanel;
                if (sp != null)
                {
                    sp.Tag = "FooterServiceHubStatus";
                    for (int i = 0; i < sp.Children.Count; i++)
                    {
                        System.Windows.Shapes.Ellipse ell = sp.Children[i] as System.Windows.Shapes.Ellipse;
                        if (ell != null)
                        {
                            ell.SetBinding(
                                System.Windows.Shapes.Shape.FillProperty,
                                new System.Windows.Data.Binding("ServiceHubIndicatorBrush"));
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureFooterBrandingAndVersion(DependencyObject footerRoot)
        {
            if (footerRoot == null) return;
            try
            {
                System.Windows.Controls.Grid footerGrid = null;
                System.Windows.Controls.Border borderRoot = footerRoot as System.Windows.Controls.Border;
                if (borderRoot != null)
                {
                    footerGrid = borderRoot.Child as System.Windows.Controls.Grid;
                }
                if (footerGrid == null)
                {
                    footerGrid = footerRoot as System.Windows.Controls.Grid;
                }
                if (footerGrid == null) return;

                if (!HasTaggedElement(footerGrid, "FooterCopyrightText"))
                {
                    var copyrightTb = new System.Windows.Controls.TextBlock
                    {
                        Tag = "FooterCopyrightText",
                        Text = LocalizationService.Get("WinFooterCopyright", "Razvija i održava IG-Systems. Sva prava zadržana"),
                        FontSize = 11,
                        Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    };
                    copyrightTb.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("IsNotServiceFinished")
                    {
                        Converter = new System.Windows.Controls.BooleanToVisibilityConverter()
                    });
                    System.Windows.Controls.Grid.SetColumn(copyrightTb, 0);
                    System.Windows.Controls.Grid.SetColumnSpan(copyrightTb, Math.Max(1, footerGrid.ColumnDefinitions.Count));
                    footerGrid.Children.Add(copyrightTb);
                }

                if (!HasTaggedElement(footerGrid, "FooterWisVersionText"))
                {
                    System.Windows.Controls.StackPanel rightPanel = null;
                    for (int i = 0; i < footerGrid.Children.Count; i++)
                    {
                        UIElement child = footerGrid.Children[i];
                        if (System.Windows.Controls.Grid.GetColumn(child) == 1 && child is System.Windows.Controls.StackPanel)
                        {
                            rightPanel = (System.Windows.Controls.StackPanel)child;
                            break;
                        }
                    }
                    if (rightPanel != null)
                    {
                        var verTb = new System.Windows.Controls.TextBlock
                        {
                            Tag = "FooterWisVersionText",
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0, 0, 0)
                        };
                        verTb.SetBinding(
                            System.Windows.Controls.TextBlock.TextProperty,
                            new System.Windows.Data.Binding("WisVersionFooterText"));
                        rightPanel.Children.Add(verTb);
                    }
                }
            }
            catch { }
        }

        private static System.Windows.Controls.TextBlock FindTextBlockByTextBinding(DependencyObject node, string bindingPath)
        {
            if (node == null || string.IsNullOrEmpty(bindingPath)) return null;
            System.Windows.Controls.TextBlock tb = node as System.Windows.Controls.TextBlock;
            if (tb != null)
            {
                System.Windows.Data.Binding b = System.Windows.Data.BindingOperations.GetBinding(tb, System.Windows.Controls.TextBlock.TextProperty);
                if (b != null && b.Path != null && string.Equals(b.Path.Path, bindingPath, StringComparison.Ordinal))
                {
                    return tb;
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null)
                {
                    System.Windows.Controls.TextBlock found = FindTextBlockByTextBinding(dep, bindingPath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void RebuildActiveStepsHost(System.Windows.Controls.StackPanel host)
        {
            if (host == null || ViewModel == null || ViewModel.ActiveStepBars == null) return;
            host.Children.Clear();
            for (int i = 0; i < ViewModel.ActiveStepBars.Count; i++)
            {
                host.Children.Add(CreateActiveStepBarBorder(ViewModel.ActiveStepBars[i]));
            }
        }

        private static System.Windows.Controls.Border CreateActiveStepBarBorder(ActiveStepProgressItem item)
        {
            var b = new System.Windows.Controls.Border
            {
                DataContext = item,
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var stack = new System.Windows.Controls.StackPanel();
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var badge = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.Binding("BadgeBrush"));

            var badgeTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            badgeTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("PhaseLabel"));
            badge.Child = badgeTb;
            System.Windows.Controls.Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var titleTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            System.Windows.Controls.Grid.SetColumn(titleTb, 1);
            grid.Children.Add(titleTb);

            var detailTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(170, 170, 170),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0)
            };
            detailTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("DetailSuffix"));
            System.Windows.Controls.Grid.SetColumn(detailTb, 2);
            grid.Children.Add(detailTb);

            var pctTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            pctTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("PercentText"));
            pctTb.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("BarBrush"));
            System.Windows.Controls.Grid.SetColumn(pctTb, 3);
            grid.Children.Add(pctTb);

            stack.Children.Add(grid);

            var stepBarGrid = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 5, 0, 0)
            };
            stepBarGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stepBarGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var pb = new System.Windows.Controls.ProgressBar
            {
                Maximum = 100,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1)
            };
            pb.SetBinding(System.Windows.Controls.ProgressBar.ValueProperty, new System.Windows.Data.Binding("Progress"));
            pb.SetBinding(System.Windows.Controls.ProgressBar.IsIndeterminateProperty, new System.Windows.Data.Binding("IsIndeterminate"));
            pb.SetBinding(System.Windows.Controls.ProgressBar.ForegroundProperty, new System.Windows.Data.Binding("BarBrush"));
            System.Windows.Controls.Grid.SetColumn(pb, 0);
            stepBarGrid.Children.Add(pb);

            var stepTimingStack = new System.Windows.Controls.StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0),
                MinWidth = 178
            };
            stepTimingStack.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("TimingTooltip"));
            var stepMonoFont = new FontFamily("Consolas");
            stepTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinTimingElapsedPrefix", "Traje "),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var stepElapsedTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                FontFamily = stepMonoFont,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204),
                VerticalAlignment = VerticalAlignment.Center
            };
            stepElapsedTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("ElapsedText"));
            stepTimingStack.Children.Add(stepElapsedTb);
            stepTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "  |  ",
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(78, 78, 86),
                VerticalAlignment = VerticalAlignment.Center
            });
            stepTimingStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = LocalizationService.Get("WinTimingEtaPrefix", "ETA "),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var stepEtaTb = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                FontFamily = stepMonoFont,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                VerticalAlignment = VerticalAlignment.Center
            };
            stepEtaTb.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("EtaText"));
            stepTimingStack.Children.Add(stepEtaTb);

            System.Windows.Controls.Grid.SetColumn(stepTimingStack, 1);
            stepBarGrid.Children.Add(stepTimingStack);
            stack.Children.Add(stepBarGrid);

            b.Child = stack;
            return b;
        }

        private static bool ContainsProgressBar(DependencyObject node)
        {
            if (node == null) return false;
            if (node is System.Windows.Controls.ProgressBar) return true;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null && ContainsProgressBar(dep)) return true;
            }
            return false;
        }

        private static bool ContainsItemsSourceBinding(DependencyObject node, string bindingPath)
        {
            if (node == null || string.IsNullOrEmpty(bindingPath)) return false;
            System.Windows.Controls.ItemsControl ic = node as System.Windows.Controls.ItemsControl;
            if (ic != null)
            {
                System.Windows.Data.Binding b = System.Windows.Data.BindingOperations.GetBinding(ic, System.Windows.Controls.ItemsControl.ItemsSourceProperty);
                if (b != null && b.Path != null && string.Equals(b.Path.Path, bindingPath, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null && ContainsItemsSourceBinding(dep, bindingPath)) return true;
            }
            return false;
        }

        private void ApplyAlwaysVisiblePhaseStepsVisual(DependencyObject root)
        {
            if (root == null) return;
            try
            {
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, i);
                    System.Windows.Controls.Border cardBorder = child as System.Windows.Controls.Border;
                    if (cardBorder != null && cardBorder.DataContext is PhaseItem)
                    {
                        cardBorder.VerticalAlignment = VerticalAlignment.Stretch;
                    }

                    System.Windows.Controls.Expander exp = child as System.Windows.Controls.Expander;
                    if (exp != null)
                    {
                        exp.IsExpanded = true;
                        exp.Margin = new Thickness(0, 8, 0, 0);
                        if (!string.Equals(Convert.ToString(exp.Tag), "AlwaysVisibleSteps", StringComparison.Ordinal))
                        {
                            exp.Tag = "AlwaysVisibleSteps";
                            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Expander));
                            tpl.VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
                            exp.Template = tpl;
                            exp.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowStepsInOverview")
                            {
                                Converter = new System.Windows.Controls.BooleanToVisibilityConverter()
                            });
                        }
                        continue;
                    }

                    ApplyAlwaysVisiblePhaseStepsVisual(child);
                }
            }
            catch { }
        }

        private void EnsurePhasesCollapseToggleButton(System.Windows.Controls.ItemsControl phasesControl)
        {
            if (phasesControl == null) return;
            try
            {
                System.Windows.Controls.Border cardBorder = null;
                DependencyObject curr = LogicalTreeHelper.GetParent(phasesControl);
                while (curr != null)
                {
                    System.Windows.Controls.Border b = curr as System.Windows.Controls.Border;
                    if (b != null)
                    {
                        cardBorder = b;
                        break;
                    }
                    curr = LogicalTreeHelper.GetParent(curr);
                }
                if (cardBorder == null) return;

                // Oslobodi prostor u desnom gornjem uglu naslova kartice FAZA TRACKER da se dugme ne preklapa sa tekstom ili 5. fazom
                System.Windows.Controls.Panel phasesParentPanel = LogicalTreeHelper.GetParent(phasesControl) as System.Windows.Controls.Panel;
                if (phasesParentPanel != null && phasesParentPanel.Children.Count > 0)
                {
                    System.Windows.Controls.TextBlock titleTb = phasesParentPanel.Children[0] as System.Windows.Controls.TextBlock;
                    if (titleTb != null)
                    {
                        titleTb.Margin = new Thickness(0, 2, 144, 12);
                    }
                }

                Button toggleBtn = FindTaggedElement<Button>(cardBorder, "PhasesCollapseToggleBtn");
                if (toggleBtn == null)
                {
                    toggleBtn = new Button
                    {
                        Tag = "PhasesCollapseToggleBtn",
                        Background = MainViewModel.CreateFrozenBrush(45, 45, 48),
                        Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204),
                        BorderBrush = MainViewModel.CreateFrozenBrush(78, 78, 86),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    Style actionStyle = TryFindResource("ActionButtonStyle") as Style;
                    if (actionStyle != null)
                    {
                        toggleBtn.Style = actionStyle;
                    }
                    System.Windows.Controls.Panel.SetZIndex(toggleBtn, 10);
                    toggleBtn.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding("TogglePhasesExpandedCommand"));

                    System.Windows.Controls.Grid existingGrid = cardBorder.Child as System.Windows.Controls.Grid;
                    if (existingGrid != null)
                    {
                        existingGrid.Children.Add(toggleBtn);
                    }
                    else
                    {
                        UIElement existingChild = cardBorder.Child;
                        cardBorder.Child = null;
                        System.Windows.Controls.Grid wrapperGrid = new System.Windows.Controls.Grid();
                        if (existingChild != null)
                        {
                            wrapperGrid.Children.Add(existingChild);
                        }
                        wrapperGrid.Children.Add(toggleBtn);
                        cardBorder.Child = wrapperGrid;
                    }
                }

                toggleBtn.HorizontalAlignment = HorizontalAlignment.Right;
                toggleBtn.VerticalAlignment = VerticalAlignment.Top;
                toggleBtn.Width = double.NaN;
                toggleBtn.MinWidth = 136;
                toggleBtn.Height = 24;
                toggleBtn.Margin = new Thickness(0, -2, 0, 0);
                toggleBtn.Padding = new Thickness(10, 2, 10, 2);
                toggleBtn.FontSize = 10.5;
                toggleBtn.FontWeight = FontWeights.SemiBold;
                toggleBtn.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("PhasesToggleText"));
                toggleBtn.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("PhasesToggleToolTip"));
            }
            catch { }
        }

        private System.Windows.Controls.ItemsControl FindPhasesItemsControl(DependencyObject node)
        {
            object expectedSource = ViewModel != null ? ViewModel.Phases : null;
            return FindItemsControlBySourceBinding(node, "Phases", expectedSource);
        }

        private static System.Windows.Controls.ItemsControl FindItemsControlBySourceBinding(DependencyObject node, string bindingPath, object expectedSource)
        {
            if (node == null) return null;

            System.Windows.Controls.ItemsControl ic = node as System.Windows.Controls.ItemsControl;
            if (ic != null)
            {
                if (expectedSource != null && object.ReferenceEquals(ic.ItemsSource, expectedSource))
                {
                    return ic;
                }
                System.Windows.Data.Binding b = System.Windows.Data.BindingOperations.GetBinding(ic, System.Windows.Controls.ItemsControl.ItemsSourceProperty);
                if (b != null && b.Path != null && string.Equals(b.Path.Path, bindingPath, StringComparison.Ordinal))
                {
                    return ic;
                }
            }

            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dep = child as DependencyObject;
                if (dep != null)
                {
                    System.Windows.Controls.ItemsControl found = FindItemsControlBySourceBinding(dep, bindingPath, expectedSource);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private void InitializeCumulativeTimer()
        {
            try
            {
                string startIso = null;
                string statePath = @"C:\setup\deploy-state.json";
                if (File.Exists(statePath))
                {
                    try
                    {
                        string json = File.ReadAllText(statePath, Encoding.UTF8);
                        var root = JsonHelper.Parse(json);
                        if (root != null && !root.IsNull)
                        {
                            if (root.ContainsKey("initial_start_time")) startIso = root["initial_start_time"].AsString(null);
                            else if (root.ContainsKey("InitialStartTime")) startIso = root["InitialStartTime"].AsString(null);
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(startIso))
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WIS", false))
                        {
                            if (key != null)
                            {
                                object regVal = key.GetValue("InitialStartTime");
                                if (regVal != null) startIso = regVal.ToString();
                            }
                        }
                    }
                    catch { }
                }

                DateTime parsedStart;
                if (!string.IsNullOrEmpty(startIso) && DateTime.TryParse(startIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsedStart))
                {
                    _initialStartTimeIso = startIso;
                    TimeSpan elapsed = DateTime.Now - parsedStart.ToLocalTime();
                    if (elapsed.TotalSeconds > 0 && elapsed.TotalDays < 30)
                    {
                        ViewModel.SetInitialElapsedDuration(elapsed);
                    }
                }
                else
                {
                    _initialStartTimeIso = DateTime.Now.ToString("o");
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WIS"))
                        {
                            if (key != null)
                            {
                                key.SetValue("InitialStartTime", _initialStartTimeIso);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        #region Task Runner Mehanizam

        public void StartPhaseExecution(int phaseNumber)
        {
            if (_autoStartTimer != null && _autoStartTimer.IsEnabled)
            {
                _autoStartTimer.Stop();
            }

            ViewModel.IsServiceReadyVisible = false;
            _taskRunnerStarted = true;
            SetFailedPhase(0);
            if (phaseNumber <= 2)
            {
                _phase3AutoRetryCount = 0;
            }
            ViewModel.IsPaused = false;
            ViewModel.IpcConnected = true;
            ViewModel.IpcStatusText = string.Format(LocalizationService.Get("WinMasterPhaseStatusFmt", "Master Faza {0}"), phaseNumber);

            if (_isMasterMode && phaseNumber >= 1 && phaseNumber <= 5)
            {
                EnsureRunOnceEntry();
            }
            else if (_isMasterMode && phaseNumber >= 6)
            {
                ClearRunOnceEntries();
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ExecutePhase(phaseNumber);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", "Greška u pozadinskoj niti faze: " + ex.Message);
                    }));
                }
            });
        }

        public void ExecutePhase(int phaseNumber)
        {
            if (phaseNumber == 6 && HasUnfinishedInteractiveHwTests())
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.CurrentPhase = 5;
                        ViewModel.IsCompleted = false;
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase6BlockedSub", "Čekaju obavezni interaktivni testovi...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase6BlockedLive", "⚠️ Faza 6 je blokirana dok se ne završe svi obavezni interaktivni testovi hardvera.");
                        ViewModel.AddLog("WARN", "Hardver", "HARD GATE: Blokiran prelazak u Fazu 6 jer postoje nezavršeni obavezni interaktivni hardverski testovi.");
                    }
                    ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                }));
                return;
            }

            // IMP-44: Reality Check za ključne aplikacije PRE pokretanja Faze 6 (dok Wi-Fi/NAS mreža još nije obrisana Anti-Leak korakom)
            if (phaseNumber == 6 && !_phase6AllowMissingAppsNeedsAttention &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
            {
                List<string> missingBeforeP6 = GetMissingKeyApplications();
                if (missingBeforeP6.Count > 0)
                {
                    string missingStr = string.Join(", ", missingBeforeP6.ToArray());
                    bool operatorConfirmedNeedsAttention = false;
                    if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Action promptAction = delegate
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("WARN", "RealityCheck", "Pre-Phase 6 Reality Check: Nedostaju ključne aplikacije pre pokretanja Faze 6 (Anti-Leak čišćenja): " + missingStr);
                            }
                            MessageBoxResult mbRes = MessageBox.Show(
                                this,
                                string.Format(
                                    LocalizationService.Get(
                                        "WinPrePhase6MissingAppsConfirmBody",
                                        "UPOZORENJE PRE ZAVRŠETKA (FAZA 6):\n\n" +
                                        "Na disku nedostaju sledeći ključni programi:\n• {0}\n\n" +
                                        "Faza 6 trajno briše servisni Wi-Fi profil (Anti-Leak), nakon čega preuzimanje programa sa NAS-a više neće biti moguće!\n\n" +
                                        "• Kliknite 'Ne' (preporučeno) da ostanete pre Faze 6 i instalirate programe (↻ Retry na kartici Programi).\n" +
                                        "• Kliknite 'Da' samo ako svesno želite da završite servis bez ovih programa (uređaj će dobiti status NEEDS_ATTENTION / NEEDS_SERVICE)."),
                                    missingStr),
                                LocalizationService.Get("WinPrePhase6MissingAppsConfirmTitle", "WIS Reality Check — Nedostaju ključni programi pre Faze 6"),
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.No);
                            operatorConfirmedNeedsAttention = (mbRes == MessageBoxResult.Yes);
                        };
                        if (Dispatcher.CheckAccess()) promptAction();
                        else Dispatcher.Invoke(promptAction);
                    }

                    if (operatorConfirmedNeedsAttention)
                    {
                        _phase6AllowMissingAppsNeedsAttention = true;
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.DeploymentStatus = "NEEDS_ATTENTION";
                                ViewModel.AddLog("WARN", "RealityCheck", "Operater je potvrdio nastavak u Fazu 6 uprkos nedostajućim aplikacijama (" + missingStr + ") — status: NEEDS_ATTENTION.");
                            }
                        }));
                    }
                    else
                    {
                        Interlocked.Exchange(ref _phase6DispatchGuard, 0);
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("ERROR", "RealityCheck", "Faza 6 je zaustavljena pre Anti-Leak brisanja mreže jer nedostaju ključne aplikacije: " + missingStr);
                                ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinFinalMissingApps", "Greška: Nedostaju ključni programi ({0}). Pokrenite ↻ Retry na kartici Programi ili koristite traku za oporavak dole."), missingStr);
                                string appNotFoundTxt = LocalizationService.Get("WinAppFileNotFound", "Fajl nije pronađen");
                                foreach (string item in missingBeforeP6)
                                {
                                    if (item.Contains("Chrome")) ViewModel.UpdateAppStatus("Chrome", "FAIL", appNotFoundTxt);
                                    if (item.Contains("Acrobat")) ViewModel.UpdateAppStatus("Acrobat", "FAIL", appNotFoundTxt);
                                    if (item.Contains("Office")) ViewModel.UpdateAppStatus("Office", "FAIL", appNotFoundTxt);
                                    if (item.Contains("WinRAR")) ViewModel.UpdateAppStatus("WinRAR", "FAIL", appNotFoundTxt);
                                }
                                ViewModel.CurrentPhase = 4;
                                SetFailedPhase(4);
                            }
                        }));
                        return;
                    }
                }
            }

            // V344-03: Perzistencija faza 5 i 6 u deploy-state.json u redovnom toku
            if (phaseNumber == 5)
            {
                SaveDeployStatePhase(5);
            }
            else if (phaseNumber == 6)
            {
                SaveDeployStatePhase(6);
            }

            if (phaseNumber >= 6)
            {
                _phase6ScriptCompletedSuccessfully = false;
            }
            if (phaseNumber == 5 && _deviceMonitor != null)
            {
                _deviceMonitor.PausePolling(true);
            }
            else if (phaseNumber != 5 && _deviceMonitor != null)
            {
                _deviceMonitor.PausePolling(false);
            }

            // U2 (Paket C2): Upravljanje životnim ciklusom Screen Capture worker-a po fazama
            if (phaseNumber >= 6)
            {
                StopScreenCaptureWorker();
            }
            else if (_isMasterMode && phaseNumber >= 1 && phaseNumber <= 5)
            {
                StartScreenCaptureWorkerIfNeeded();
            }

            // 1. Postavljanje UI statusa za odgovarajuću fazu
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.CurrentPhase = phaseNumber;
                switch (phaseNumber)
                {
                    case 1:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase1Name", "Faza 1: Mreža i povezivanje");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase1Sub", "Povezivanje na servisnu mrežu...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase1Live", "Konfigurisanje mrežnih interfejsa i rutiranja...");
                        ViewModel.Progress = 10;
                        break;
                    case 2:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase2Name", "Faza 2: Drajveri i zakrpe");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase2Sub", "Instalacija PnP drajvera...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase2Live", "Skeniranje i instalacija nedostajućih drajvera...");
                        ViewModel.Progress = 25;
                        break;
                    case 3:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase3Name", "Faza 3: Optimizacija i debloat");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase3Sub", "Sistemske optimizacije...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase3Live", "Primenjivanje debloat i brending skripti...");
                        ViewModel.Progress = 50;
                        break;
                    case 4:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase4Name", "Faza 4: Instalacija programa");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase4Sub", "Instalacija programskih paketa...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase4Live", "Instalacija i verifikacija servisnih programa...");
                        ViewModel.Progress = 70;
                        break;
                    case 5:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase5Name", "Faza 5: Hardverski test (Stres + Periferije)");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase5Sub", "Automatska provera, 30s stres i testovi periferija...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase5Live", "Izvršavanje integrisanih hardverskih testova u tabu Hardverski testovi...");
                        ViewModel.Progress = 85;
                        break;
                    case 6:
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase6Name", "Faza 6: Završetak i rezime");
                        ViewModel.SubPhase = LocalizationService.Get("WinPhase6Sub", "Finalizacija i dijagnostika...");
                        ViewModel.LiveStatus = LocalizationService.Get("WinPhase6Live", "Generisanje servisnog izveštaja i verifikacija...");
                        ViewModel.Progress = 95;
                        break;
                    default:
                        ViewModel.PhaseName = string.Format(LocalizationService.Get("WinPhaseDefaultNameFmt", "Faza {0}"), phaseNumber);
                        ViewModel.SubPhase = LocalizationService.Get("WinPhaseDefaultSub", "Izvršavanje faze...");
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhaseDefaultLiveFmt", "Rad na Fazi {0}..."), phaseNumber);
                        break;
                }

                ViewModel.AddLog("INFO", "TaskRunner", string.Format("Započeto izvršavanje Faze {0}: {1}", phaseNumber, ViewModel.PhaseName));
                NotifyHwTestDriverStateChanged(phaseNumber >= 3);
            }));

            // 2. Na početku Faze 1 emitujemo STARTED Lifecycle događaj (IMP-45: bez slanja UI placeholder stringova)
            if (phaseNumber == 1 && _telemetryService != null)
            {
                string startModel = (ViewModel != null && !IsPlaceholderIdentity(ViewModel.ComputerModel)) ? ViewModel.ComputerModel.Trim() : "";
                string startSerial = (ViewModel != null && !IsPlaceholderIdentity(ViewModel.SerialNumber)) ? ViewModel.SerialNumber.Trim() : "";
                var startData = new Dictionary<string, object>
                {
                    { "phase", 1 },
                    { "phase_name", "Mreza" },
                    { "model", startModel },
                    { "serial", startSerial }
                };
                _telemetryService.SendLifecycleEvent("STARTED", startData);
            }

            // 3. Određivanje naziva skripte faze i pronalaženje njene putanje
            string fileName;
            switch (phaseNumber)
            {
                case 1:
                    fileName = "Phase1-Network.ps1";
                    break;
                case 2:
                    fileName = "Phase2-Drivers.ps1";
                    break;
                case 3:
                    fileName = "Phase3-Optimization.ps1";
                    break;
                case 4:
                    fileName = "Phase4-Apps.ps1";
                    break;
                case 5:
                    fileName = "Phase5-Stress.ps1";
                    break;
                case 6:
                    fileName = "Phase6-Finalize.ps1";
                    break;
                default:
                    fileName = string.Format("Phase{0}.ps1", phaseNumber);
                    break;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptPath = FindPhaseScriptPath(baseDir, fileName);

            // Fallback za Fazu 6 ukoliko Phase6-Finalize.ps1 još nije kreirana već postoji Phase5-Finalize.ps1
            if (phaseNumber == 6 && string.IsNullOrEmpty(scriptPath))
            {
                scriptPath = FindPhaseScriptPath(baseDir, "Phase5-Finalize.ps1");
            }

            // V350-06: Post-Reboot Finalize u Fazi 5 pre otključavanja DriverReadinessGate i starta HwTest-a
            bool postRebootFinalizeExecuted = false;
            if (phaseNumber == 5 && (_postRebootFinalizePending || GetSavedPostRebootFinalizeFlag()) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
            {
                postRebootFinalizeExecuted = true;
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.SubPhase = LocalizationService.Get("WinPostRebootFinalizeSub", "Završna aktivacija drajvera i kalibracija ekrana...");
                    ViewModel.LiveStatus = LocalizationService.Get("WinPostRebootFinalizeLive", "Primenjujem post-reboot podešavanja drajvera i osvežavam hardverski profil...");
                    ViewModel.AddLog("INFO", "TaskRunner", "V350-06: Pokrećem PostRebootFinalize pre Faze 5...");
                }));

                RunPhase2DriversStage("PostRebootFinalize", 8);

                try { DetectDisplay(); } catch { }
                try { RefreshPeripheralsAfterDrivers(); } catch { }
                if (_embeddedHwTestShell != null)
                {
                    try { _embeddedHwTestShell.RefreshHardwareProbeAfterDrivers(true); } catch { }
                }

                _postRebootFinalizePending = false;
                SaveDeployStatePhase(5, false);

                Dispatcher.Invoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.CurrentPhase = 5;
                        ViewModel.PhaseName = LocalizationService.Get("WinPhase5Name", "Faza 5: Hardverski test (Stres + Periferije)");
                    }
                }));
            }

            // WIS-TASK-278: Provera zaostalog kontrolisanog restarta (V353-01) i Code 14 restarta sa BitLocker suspendom pre ulaska u HwTestShell
            if (phaseNumber == 5 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
            {
                string pendingRebootFlag = @"C:\setup\pending-controlled-reboot.flag";
                bool hasPendingRenameRebootFlag = false;
                try
                {
                    if (File.Exists(pendingRebootFlag))
                    {
                        hasPendingRenameRebootFlag = true;
                        File.Delete(pendingRebootFlag);
                    }
                }
                catch { }

                WIS.HwTest.Core.DriverGateState code14Gate = WIS.HwTest.Core.DriverReadinessGate.EvaluateLocal();
                bool hasCode14 = (code14Gate != null && code14Gate.HasCode14PendingReboot);

                if (hasPendingRenameRebootFlag || hasCode14)
                {
                    int gateReboots = GetDeployStateInt("driver_gate_reboots", 0);
                    if (gateReboots < 2)
                    {
                        int nextReboots = gateReboots + 1;
                        SetDeployStateInt("driver_gate_reboots", nextReboots);
                        string reasonLabel = hasPendingRenameRebootFlag
                            ? "zaostalo preimenovanje lokalnog naloga"
                            : "uređaj zahteva restart radi učitavanja drajvera (Code 14)";
                        string rebootMsg = string.Format(
                            "Detektovan uslov za kontrolisani restart ({0}) pre Faze 5 — suspendujem BitLocker i pokrećem restart ({1}/2)...",
                            reasonLabel, nextReboots);
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog("[WARN] [DriverGate] " + rebootMsg);
                        }
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("WARN", "DriverGate", rebootMsg);
                                ViewModel.SubPhase = "Potreban restart za aktivaciju drajvera (Code 14)...";
                                ViewModel.LiveStatus = rebootMsg;
                            }
                        }));
                        SuspendBitLockerBeforeReboot();
                        HandleInterimReboot();
                        return;
                    }
                    else
                    {
                        string maxRebootWarn = string.Format(
                            "Code 14 / restart zahtev je i dalje aktivan, ali je limit automatskih restarta dostignut ({0}/2) — nastavljam u PreAutoDriverGate.",
                            gateReboots);
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog("[WARN] [DriverGate] " + maxRebootWarn);
                        }
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("WARN", "DriverGate", maxRebootWarn);
                            }
                        }));
                    }
                }

                if (postRebootFinalizeExecuted)
                {
                    RunPhase2DriversStageAsync("DeltaHarvest");
                    NotifyHwTestDriverStateChanged(true);
                }
            }

            // Za Fazu 5: u produkcionom toku pokrećemo ugrađeni hardverski test (stres + periferije);
            // ukoliko je postavljen WIS_STRESS_TEST_INPUT_JSON (CI / ugovorni test), nastavljamo sa Phase5-Stress.ps1.
            if (phaseNumber == 5 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIS_STRESS_TEST_INPUT_JSON")))
            {
                ExecutePhase5IntegratedHwTest();
                return;
            }

            // Za Fazu 5 (Automatski 30s Termalni i Stres Test): ako skripta ne postoji na disku, izvrši C# 30s stres test direktno i pređi na Fazu 6
            if (phaseNumber == 5 && (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath)))
            {
                RunAutomatedPhase5StressFallback();
                bool isSensorUnavailableFallback = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                                   _lastStressMaxTemp <= 0.5f);

                if (isSensorUnavailableFallback)
                {
                    _criticalStressFailure = false;
                    _lastStressEmergencyAbort = false;
                    _lastStressStatus = "UNKNOWN";
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.StressResultStatus = "UNKNOWN";
                            ViewModel.CriticalStressFailure = false;
                            ViewModel.EmergencyAbortTriggered = false;
                            ViewModel.AddLog("WARN", "TaskRunner", "Faza 5: Termalni senzori nisu dostupni (status UNKNOWN). Prelazim na Fazu 6 (Završetak)...");
                        }
                    }));
                }
                else
                {
                    if (HasCriticalThermalEvidence())
                    {
                        RecordThermalOverheatNote();
                    }
                }
                ExecutePhase(6);
                return;
            }

            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Skripta faze nije pronađena: {0}", fileName));
                    ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhaseScriptMissingFmt", "Greška: {0} nedostaje na sistemu!"), fileName);
                    SetFailedPhase(phaseNumber);
                }));
                return;
            }

            // 4. Pokretanje PowerShell radnika za traženu fazu
            try
            {
                string psArgs = (phaseNumber == 2)
                    ? string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -Stage FastNas", scriptPath)
                    : string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", scriptPath);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = psArgs,
                    WorkingDirectory = Directory.Exists(@"C:\setup") ? @"C:\setup" : (Path.GetDirectoryName(scriptPath) ?? @"C:\"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var proc = Process.Start(psi);
                _currentPhaseProcess = proc;

                proc.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string line = e.Data.Trim();
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (line.StartsWith("{") && line.EndsWith("}"))
                        {
                            ProcessIncomingJson(line);
                        }
                        else
                        {
                            ParseAndAddRawLogLine(line);
                        }
                    }));
                    if (_telemetryService != null)
                    {
                        _telemetryService.PushLog(line);
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string errLine = e.Data.Trim();
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("WARN", "PowerShell", errLine);
                    }));
                    if (_telemetryService != null)
                    {
                        _telemetryService.PushLog(errLine);
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                int timeoutMinutes = GetPhaseTimeoutMinutes(phaseNumber);
                bool exited = WaitForPhaseProcessExitWithActiveTime(proc, timeoutMinutes);

                if (!exited)
                {
                    try
                    {
                        ProcessStartInfo tkPsi = new ProcessStartInfo("taskkill.exe", string.Format("/PID {0} /T /F", proc.Id))
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using (Process tk = Process.Start(tkPsi))
                        {
                            if (tk != null) tk.WaitForExit(5000);
                        }
                    }
                    catch { }
                    try { proc.Kill(); } catch { }
                    if (phaseNumber == 5)
                    {
                        EnsureStressTestResultFile();
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            ViewModel.AddLog("WARN", "TaskRunner", "Faza 5 (Stres test) prekoračila očekivano vreme. Bezbedno nastavljam na Fazu 6...");
                        }));
                        bool isSensorUnavailableTimeout = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                                          string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                                          _lastStressMaxTemp <= 0.5f);
                        if (isSensorUnavailableTimeout)
                        {
                            _criticalStressFailure = false;
                            _lastStressEmergencyAbort = false;
                            _lastStressStatus = "UNKNOWN";
                        }
                        else
                        {
                            if (HasCriticalThermalEvidence())
                            {
                                RecordThermalOverheatNote();
                            }
                        }
                        ExecutePhase(6);
                        return;
                    }
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Faza {0} je prekinuta zbog isteka timeout-a od {1} minuta!", phaseNumber, timeoutMinutes));
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhaseTimeoutLiveFmt", "Faza {0} prekoračila timeout. Koristite traku za oporavak dole ('Ponovi Fazu {0}' ili 'Preskoči na sledeću fazu')."), phaseNumber);
                        SetFailedPhase(phaseNumber);
                    }));
                    return;
                }

                // Osiguraj pražnjenje asinhronih OutputDataReceived/ErrorDataReceived bafera do EOF-a
                try { proc.WaitForExit(); } catch { }

                int exitCode = proc.ExitCode;
                _currentPhaseProcess = null;

                if (phaseNumber == 5)
                {
                    EnsureStressTestResultFile();

                    bool isSensorUnavailable = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                               _lastStressMaxTemp <= 0.5f);

                    if (isSensorUnavailable)
                    {
                        _criticalStressFailure = false;
                        _lastStressEmergencyAbort = false;
                        _lastStressStatus = "UNKNOWN";
                    }

                    if (HasCriticalThermalEvidence())
                    {
                        RecordThermalOverheatNote();
                    }

                    if (isSensorUnavailable || string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.StressResultStatus = "UNKNOWN";
                                ViewModel.CriticalStressFailure = false;
                                ViewModel.EmergencyAbortTriggered = false;
                                ViewModel.AddLog("WARN", "TaskRunner", "Faza 5: Termalni senzori nisu dostupni (status UNKNOWN). Prelazim na Fazu 6 (Završetak)...");
                            }
                        }));
                        ExecutePhase(6);
                        return;
                    }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (exitCode == 0 && string.Equals(_lastStressStatus, "PASS", StringComparison.OrdinalIgnoreCase))
                        {
                            ViewModel.AddLog("SUCCESS", "TaskRunner", string.Format("Faza 5 (30s Termalni i stres test) uspešno završena (PASS, Max CPU: {0:F1}°C).", _lastStressMaxTemp));
                        }
                        else
                        {
                            ViewModel.AddLog("WARN", "TaskRunner", string.Format("Faza 5 završena sa upozorenjem (Status: {0}, Kod {1}). Nastavljam na Fazu 6 (Završetak)...", _lastStressStatus, exitCode));
                        }
                    }));
                    ExecutePhase(6);
                    return;
                }

                if (exitCode == 0)
                {
                    // POST-EXECUTION REALITY CHECK PRE PRELASKA
                    if (phaseNumber == 2)
                    {
                        PerformPhase2RealityCheck();
                        RefreshPeripheralsAfterDrivers();
                        NotifyHwTestDriverStateChanged(false);
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            PollBackgroundWuStatusFile();
                            ViewModel.AddLog("INFO", "TaskRunner", "Faza 2A (Brzi NAS/PnP drajveri) završena. Prelazim na Fazu 3, a WAN preuzimanje drajvera ide paralelno tokom Faze 4.");
                        }));
                    }
                    else if (phaseNumber == 4)
                    {
                        bool pass = PerformPhase4RealityCheck();
                        if (!pass)
                        {
                            return; // Faza 4 označena kao FAILED, TaskRunner ne prelazi dalje
                        }

                        WaitForBackgroundDriverDownloadIfActive(900);

                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            ViewModel.SubPhase = LocalizationService.Get("WinPhase4BSub", "Faza 4B: Lokalna instalacija preuzetih drajvera...");
                            ViewModel.LiveStatus = LocalizationService.Get("WinPhase4BLive", "Instaliram preuzete drajvere sa SSD-a pre restarta...");
                            ViewModel.AddLog("INFO", "TaskRunner", "V350-06: Pokrećem Fazu 4B (Phase2-Drivers.ps1 -Stage InstallStaged)...");
                        }));

                        try
                        {
                            RunPhase2DriversStage("InstallStaged", 25);
                        }
                        finally
                        {
                            Dispatcher.BeginInvoke((Action)(() =>
                            {
                                if (ViewModel != null)
                                {
                                    ViewModel.AddLog("INFO", "TaskRunner", "V353-01: Primenjujem odloženo preimenovanje naloga (Phase2-Drivers.ps1 -Stage ApplyDeferredRename) pre 4B restarta...");
                                }
                            }));
                            RunPhase2DriversStage("ApplyDeferredRename", 1);
                        }
                        HandleInterimReboot();
                        return;
                    }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("SUCCESS", "TaskRunner", string.Format("Faza {0} uspešno završena (ExitCode 0).", phaseNumber));
                    }));

                    // V350-06: Uklonjen automatski međurestart odmah posle Faze 3 — direktan prelaz na Fazu 4
                    if (phaseNumber == 3)
                    {
                        _phase3AutoRetryCount = 0;
                        SaveDeployStatePhase(4);
                        ExecutePhase(4);
                        return;
                    }

                    // Završetak celokupnog servisa (po završetku Faze 6)
                    if (phaseNumber >= 6)
                    {
                        _phase6ScriptCompletedSuccessfully = true;
                        HandleFinalCompletion();
                        return;
                    }

                    // Prelazak na sledeću fazu (V344-03: perzistencija Faze 5 i 6 u deploy-state.json)
                    int nextPhase = phaseNumber + 1;
                    if (nextPhase == 5 || nextPhase == 6)
                    {
                        SaveDeployStatePhase(nextPhase);
                    }
                    ExecutePhase(nextPhase);
                }
                else
                {
                    string phaseErrLine = string.Format("Faza {0} završila sa greškom (ExitCode {1}).", phaseNumber, exitCode);
                    if (_telemetryService != null)
                    {
                        _telemetryService.PushLog("[ERROR] [TaskRunner] " + phaseErrLine);
                    }

                    if (phaseNumber == 3 && _phase3AutoRetryCount < 1)
                    {
                        _phase3AutoRetryCount++;
                        string retryWarn = string.Format("Faza 3 je neočekivano prekinuta (ExitCode {0}). Pokrećem automatski ponovni pokušaj ({1}/1) — već završeni koraci biće preskočeni preko StepGuard žurnala...", exitCode, _phase3AutoRetryCount);
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog("[WARN] [TaskRunner] " + retryWarn);
                        }
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            ViewModel.AddLog("WARN", "TaskRunner", retryWarn);
                        }));
                        Thread.Sleep(2000);
                        ExecutePhase(3);
                        return;
                    }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", phaseErrLine);
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhaseErrorLiveFmt", "Greška u Fazi {0} (Kod {1}). Koristite traku za oporavak dole ('Proveri ponovo i nastavi', 'Ponovi Fazu {0}' ili 'Preskoči na sledeću fazu')."), phaseNumber, exitCode);
                        SetFailedPhase(phaseNumber);
                    }));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Izuzetak pri pokretanju Faze {0}: {1}", phaseNumber, ex.Message));
                    SetFailedPhase(phaseNumber);
                }));
            }
        }

        private string FindPhaseScriptPath(string baseDir, string fileName)
        {
            string[] searchCandidates = new string[]
            {
                Path.Combine(@"C:\setup\Phases", fileName),
                Path.Combine(@"C:\setup\Skripte\Phases", fileName),
                Path.Combine(baseDir, "Phases", fileName),
                Path.Combine(baseDir, @"..\Phases", fileName),
                Path.Combine(baseDir, @"..\..\Phases", fileName),
                Path.Combine(baseDir, @"..\..\Skripte\Phases", fileName)
            };

            for (int i = 0; i < searchCandidates.Length; i++)
            {
                try
                {
                    if (File.Exists(searchCandidates[i]))
                    {
                        return Path.GetFullPath(searchCandidates[i]);
                    }
                }
                catch { }
            }
            return null;
        }

        private void RunAutomatedPhase5StressFallback()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.IsStressTestRunning = true;
                ViewModel.EmergencyAbortTriggered = false;
                ViewModel.StressTestProgress = 0;
                ViewModel.MaxTempReached = 0.0f;
                ViewModel.StressTestTimeText = "0 / 30s";
                ViewModel.StressTestStatusText = LocalizationService.Get("WinStressAutoStatus", "Automatski 30s stres test procesora (Faza 5)...");
            }));

            _stressCts = new CancellationTokenSource();
            RunStressTestWorker(_stressCts.Token, 30);
            EnsureStressTestResultFile();
        }

        private void WriteUiStressTestResultFile(int actualDurationSec, float maxT, float maxThrot, bool abort, bool manualCancel)
        {
            try
            {
                if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                string resultPath = @"C:\setup\stress-test-result.json";

                bool sensorAvailable = (_thermalEngine != null && _thermalEngine.IsSensorAvailable && maxT > 0.5f);
                string sensorSource = (_thermalEngine != null && !string.IsNullOrEmpty(_thermalEngine.SensorSource) && sensorAvailable)
                    ? _thermalEngine.SensorSource
                    : "NONE";

                string status;
                string reason;
                if (abort || maxT >= 95.0f)
                {
                    status = "THERMAL_CRITICAL";
                    reason = abort ? "EMERGENCY_THERMAL_ABORT" : "CRITICAL_TEMP_THRESHOLD";
                }
                else if (manualCancel)
                {
                    status = "WARN";
                    reason = "MANUAL_CANCEL";
                }
                else if (!sensorAvailable || maxT <= 0.5f)
                {
                    status = "UNKNOWN";
                    reason = "SENSOR_UNAVAILABLE";
                }
                else if (maxT >= 85.0f || (maxThrot >= 25.0f && maxT >= 80.0f))
                {
                    status = "WARN";
                    reason = "HIGH_TEMP_WARN";
                }
                else
                {
                    status = "PASS";
                    reason = "COMPLETED_NORMAL";
                }

                _lastStressActualDurationSec = Math.Max(0, actualDurationSec);
                _lastStressMaxTemp = maxT;
                _lastStressMaxThrottling = maxThrot;
                _lastStressEmergencyAbort = (maxT > 0.5f) && (abort || (maxT >= 95.0f));
                _lastStressStatus = status;
                _lastStressReason = reason;
                _criticalStressFailure = false;
                string thermalNote = string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase)
                    ? string.Format(LocalizationService.Get("WinThermalOverheatNoteFmt", "NAPOMENA: Procesor je dostigao kritičnu temperaturu ({0:F1}°C >= 95°C) tokom stres testa — potrebno čišćenje hladnjaka i zamena termalne paste."), maxT > 0.5f ? maxT : 95.0f)
                    : "";
                if (!string.IsNullOrEmpty(thermalNote))
                {
                    _thermalOverheatNote = thermalNote;
                }

                var resMap = new Dictionary<string, object>
                {
                    { "status", status },
                    { "reason", reason },
                    { "emergency_abort", abort },
                    { "sensor_available", sensorAvailable || maxT > 0.5f },
                    { "sensor_source", sensorSource },
                    { "duration_sec", _lastStressActualDurationSec },
                    { "max_cpu_temp_c", Math.Round(maxT, 1) },
                    { "max_throttling_percent", Math.Round(maxThrot, 1) },
                    { "timestamp", DateTime.Now.ToString("o") }
                };
                if (!string.IsNullOrEmpty(thermalNote))
                {
                    resMap["thermal_note"] = thermalNote;
                    resMap["diagnostics_warning"] = thermalNote;
                    resMap["notes"] = new List<object> { thermalNote };
                }
                File.WriteAllText(resultPath, JsonHelper.Serialize(resMap), Encoding.UTF8);

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.StressResultStatus = status;
                        ViewModel.StressResultReason = reason;
                        ViewModel.CriticalStressFailure = false;
                    }
                }));
            }
            catch { }
        }

        private void EnsureStressTestResultFile()
        {
            try
            {
                string resultPath = @"C:\setup\stress-test-result.json";
                if (File.Exists(resultPath))
                {
                    string existingJson = File.ReadAllText(resultPath, Encoding.UTF8);
                    var root = JsonHelper.Parse(existingJson);
                    if (root != null && !root.IsNull && root.IsObject)
                    {
                        string status = root.ContainsKey("status") ? root["status"].AsString("UNKNOWN") : "UNKNOWN";
                        string reason = root.ContainsKey("reason") ? root["reason"].AsString("") : "";
                        bool abort = root.ContainsKey("emergency_abort") && root["emergency_abort"].AsBool(false);
                        int durSec = root.ContainsKey("duration_sec") ? root["duration_sec"].AsInt(0) : 0;

                        float maxT = 0f;
                        if (root.ContainsKey("max_cpu_temp_c"))
                        {
                            float.TryParse(root["max_cpu_temp_c"].AsString("0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxT);
                        }

                        float maxThrot = 0f;
                        if (root.ContainsKey("max_throttling_percent"))
                        {
                            float.TryParse(root["max_throttling_percent"].AsString("0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxThrot);
                        }

                        bool sensorAvailable = root.ContainsKey("sensor_available")
                            ? root["sensor_available"].AsBool(maxT > 0.5f)
                            : (maxT > 0.5f);
                        string sensorSource = root.ContainsKey("sensor_source")
                            ? root["sensor_source"].AsString(sensorAvailable ? "WMI_ACPI" : "NONE")
                            : (sensorAvailable ? "WMI_ACPI" : "NONE");

                        bool needsRewrite = false;
                        if (abort || maxT >= 95.0f || string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase))
                            {
                                status = "THERMAL_CRITICAL";
                                if (string.IsNullOrEmpty(reason)) reason = abort ? "EMERGENCY_THERMAL_ABORT" : "CRITICAL_TEMP_THRESHOLD";
                                needsRewrite = true;
                            }
                        }
                        else if ((!sensorAvailable || maxT <= 0.5f) &&
                                 !string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                        {
                            sensorAvailable = false;
                            sensorSource = "NONE";
                            abort = false;
                            if (!string.Equals(status, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                            {
                                status = "UNKNOWN";
                                reason = "SENSOR_UNAVAILABLE";
                                needsRewrite = true;
                            }
                        }

                        if (!root.ContainsKey("sensor_available") || !root.ContainsKey("reason") || !root.ContainsKey("sensor_source"))
                        {
                            if (string.IsNullOrEmpty(reason))
                            {
                                if (string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) && maxT > 0.5f) reason = "EMERGENCY_THERMAL_ABORT";
                                else if (string.Equals(status, "UNKNOWN", StringComparison.OrdinalIgnoreCase) || maxT <= 0.5f) reason = "SENSOR_UNAVAILABLE";
                                else if (string.Equals(status, "WARN", StringComparison.OrdinalIgnoreCase)) reason = "HIGH_TEMP_WARN";
                                else reason = "COMPLETED_NORMAL";
                            }
                            needsRewrite = true;
                        }

                        _lastStressActualDurationSec = Math.Max(0, durSec);
                        _lastStressMaxTemp = maxT;
                        _lastStressMaxThrottling = maxThrot;
                        _lastStressEmergencyAbort = abort || string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase);
                        _lastStressStatus = status;
                        _lastStressReason = reason;
                        _criticalStressFailure = false;
                        bool isThermalCritical = string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) || (abort && maxT > 0.5f);
                        string thermalNote = isThermalCritical
                            ? string.Format(LocalizationService.Get("WinThermalOverheatNoteFmt", "NAPOMENA: Procesor je dostigao kritičnu temperaturu ({0:F1}°C >= 95°C) tokom stres testa — potrebno čišćenje hladnjaka i zamena termalne paste."), maxT > 0.5f ? maxT : 95.0f)
                            : "";
                        if (!string.IsNullOrEmpty(thermalNote))
                        {
                            _thermalOverheatNote = thermalNote;
                        }

                        if (needsRewrite)
                        {
                            var updatedMap = new Dictionary<string, object>
                            {
                                { "status", status },
                                { "reason", reason },
                                { "emergency_abort", abort },
                                { "sensor_available", sensorAvailable },
                                { "sensor_source", sensorSource },
                                { "duration_sec", _lastStressActualDurationSec },
                                { "max_cpu_temp_c", Math.Round(maxT, 1) },
                                { "max_throttling_percent", Math.Round(maxThrot, 1) },
                                { "timestamp", root.ContainsKey("timestamp") ? root["timestamp"].AsString(DateTime.Now.ToString("o")) : DateTime.Now.ToString("o") }
                            };
                            if (!string.IsNullOrEmpty(thermalNote))
                            {
                                updatedMap["thermal_note"] = thermalNote;
                                updatedMap["diagnostics_warning"] = thermalNote;
                                updatedMap["notes"] = new List<object> { thermalNote };
                            }
                            File.WriteAllText(resultPath, JsonHelper.Serialize(updatedMap), Encoding.UTF8);
                        }

                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.StressResultStatus = status;
                                ViewModel.StressResultReason = reason;
                                ViewModel.CriticalStressFailure = false;
                                if (maxT > ViewModel.MaxTempReached)
                                {
                                    ViewModel.MaxTempReached = maxT;
                                }
                                ViewModel.EmergencyAbortTriggered = false;
                            }
                        }));
                        return;
                    }
                }

                // Fallback ukoliko datoteka uopšte ne postoji na disku
                if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                float fbMaxT = _lastStressWasExecuted ? _lastStressMaxTemp : (ViewModel != null ? ViewModel.MaxTempReached : 0f);
                if (fbMaxT <= 0f && _thermalEngine != null && _thermalEngine.IsSensorAvailable)
                {
                    fbMaxT = _thermalEngine.CpuTemperature;
                }
                float fbThrot = _lastStressWasExecuted
                    ? _lastStressMaxThrottling
                    : ((_thermalEngine != null && _thermalEngine.IsThrottlingMetricAvailable) ? _thermalEngine.ThrottlingPercentage : 0f);
                bool fbAbort = _lastStressEmergencyAbort || (ViewModel != null && ViewModel.EmergencyAbortTriggered);
                int fbDuration = _lastStressWasExecuted ? _lastStressActualDurationSec : 0;
                bool fbSensorAvail = (_thermalEngine != null && _thermalEngine.IsSensorAvailable && fbMaxT > 0.5f);
                string fbSensorSource = (fbSensorAvail && _thermalEngine != null && !string.IsNullOrEmpty(_thermalEngine.SensorSource))
                    ? _thermalEngine.SensorSource
                    : "NONE";

                string fbStatus;
                string fbReason;
                if (fbAbort || fbMaxT >= 95.0f)
                {
                    fbStatus = "THERMAL_CRITICAL";
                    fbReason = fbAbort ? "EMERGENCY_THERMAL_ABORT" : "CRITICAL_TEMP_THRESHOLD";
                }
                else if (!_lastStressWasExecuted || fbDuration <= 0)
                {
                    fbStatus = "UNKNOWN";
                    fbReason = "FALLBACK_NO_WORKER_RESULT";
                }
                else if (!fbSensorAvail || fbMaxT <= 0.5f)
                {
                    fbStatus = "UNKNOWN";
                    fbReason = "SENSOR_UNAVAILABLE";
                }
                else if (fbMaxT >= 85.0f || fbThrot > 5.0f)
                {
                    fbStatus = "WARN";
                    fbReason = "HIGH_TEMP_WARN";
                }
                else
                {
                    fbStatus = "PASS";
                    fbReason = "COMPLETED_NORMAL";
                }

                _lastStressStatus = fbStatus;
                _lastStressReason = fbReason;
                _lastStressMaxTemp = fbMaxT;
                _lastStressMaxThrottling = fbThrot;
                _lastStressEmergencyAbort = fbAbort || fbMaxT >= 95.0f;
                _criticalStressFailure = string.Equals(fbStatus, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase);

                var resMap = new Dictionary<string, object>
                {
                    { "status", fbStatus },
                    { "reason", fbReason },
                    { "emergency_abort", fbAbort },
                    { "sensor_available", fbSensorAvail },
                    { "sensor_source", fbSensorSource },
                    { "duration_sec", fbDuration },
                    { "max_cpu_temp_c", Math.Round(fbMaxT, 1) },
                    { "max_throttling_percent", Math.Round(fbThrot, 1) },
                    { "timestamp", DateTime.Now.ToString("o") }
                };
                File.WriteAllText(resultPath, JsonHelper.Serialize(resMap), Encoding.UTF8);

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.StressResultStatus = fbStatus;
                        ViewModel.StressResultReason = fbReason;
                        ViewModel.CriticalStressFailure = _criticalStressFailure;
                    }
                }));
            }
            catch { }
        }

        private void PerformAntiLeakWifiCleanupOnly()
        {
            try
            {
                string wifiIni = @"C:\setup\Wifi-Servis.ini";
                string serviceSsid = null;
                if (File.Exists(wifiIni))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(wifiIni, Encoding.UTF8);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (line.StartsWith("SSID=", StringComparison.OrdinalIgnoreCase))
                            {
                                serviceSsid = line.Substring(5).Trim().Trim('"', '\'');
                                break;
                            }
                        }
                    }
                    catch { }

                    try { File.Delete(wifiIni); } catch { }
                }

                if (!string.IsNullOrEmpty(serviceSsid))
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "netsh.exe",
                            Arguments = string.Format("wlan delete profile name=\"{0}\"", serviceSsid),
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using (Process p = Process.Start(psi))
                        {
                            if (p != null) p.WaitForExit(5000);
                        }
                    }
                    catch { }
                }

                if (Directory.Exists(@"C:\setup"))
                {
                    try
                    {
                        string[] xmlFiles = Directory.GetFiles(@"C:\setup", "*.xml");
                        for (int i = 0; i < xmlFiles.Length; i++)
                        {
                            string fn = Path.GetFileName(xmlFiles[i]) ?? "";
                            if (fn.IndexOf("wifi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fn.IndexOf("wlan", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { File.Delete(xmlFiles[i]); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Kriticno ocitavanje ima prioritet nad UNKNOWN i kasnijim gubitkom senzora.
        private bool HasIncompleteStressResult()
        {
            return string.Equals(_lastStressStatus, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(_lastStressStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasCriticalThermalEvidence()
        {
            return _lastStressMaxTemp >= 95.0f || _lastStressEmergencyAbort ||
                   string.Equals(_lastStressStatus, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase);
        }

        private void RecordThermalOverheatNote()
        {
            if (!HasCriticalThermalEvidence() && !HasIncompleteStressResult() &&
                (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                 _lastStressMaxTemp <= 0.5f))
            {
                _criticalStressFailure = false;
                _lastStressEmergencyAbort = false;
                _lastStressStatus = "UNKNOWN";
                _lastStressReason = "SENSOR_UNAVAILABLE";
                if (ViewModel != null)
                {
                    ViewModel.StressResultStatus = "UNKNOWN";
                    ViewModel.CriticalStressFailure = false;
                    ViewModel.EmergencyAbortTriggered = false;
                    ViewModel.AddLog("WARN", "ThermalGuard", "Ignorisan zahtev za termalni prekid: Termalni senzori nisu dostupni (status UNKNOWN). Instalacija normalno nastavlja rad.");
                }
                return;
            }

            // WIS-TASK-245: Pregrevanje procesora ne prekida pipeline — instalacija nastavlja do kraja,
            // a nalaz se čuva u UI prikazu i svim izveštajima kao napomena za servis hlađenja.
            _criticalStressFailure = false;
            _lastStressEmergencyAbort = true;
            _lastStressStatus = "THERMAL_CRITICAL";

            float displayTemp = _lastStressMaxTemp > 0.5f ? _lastStressMaxTemp : 95.0f;
            string noteMsg = string.Format(
                "NAPOMENA: Procesor se pregrevao tokom stres testa ({0:F1}°C >= 95°C) — potrebno čišćenje hladnjaka i zamena termalne paste.",
                displayTemp);
            _thermalOverheatNote = noteMsg;

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (ViewModel == null) return;

                ViewModel.CriticalStressFailure = false;
                ViewModel.EmergencyAbortTriggered = false;
                ViewModel.StressResultStatus = "THERMAL_CRITICAL";
                ViewModel.StressResultReason = _lastStressReason;
                ViewModel.MaxTempReached = displayTemp;
                ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinThermalLiveContinue", "⚠️ {0} — instalacija nastavlja dalje."), noteMsg);
                ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinThermalStressNoteText", "⚠️ NAPOMENA: CPU {0:F1}°C (>= 95°C) — sačuvano u izveštaju!"), displayTemp);
                ViewModel.ThermalVerdictTitle = string.Format(LocalizationService.Get("WinThermalOverheatTitle", "⚠️ NAPOMENA: PREGREVANJE CPU ({0:F1}°C >= 95°C)"), displayTemp);
                ViewModel.ThermalVerdictDescription = string.Format(LocalizationService.Get("WinThermalOverheatDesc", "Procesor je dostigao {0:F1}°C tokom stres testa (opterećenje je bezbedno zaustavljeno). Pipeline je nastavljen i napomena je zabeležena u izveštaju."), displayTemp);
                ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(230, 126, 34); // #E67E22 Narandžasta napomena
                ViewModel.ThermalRecommendation = LocalizationService.Get("WinThermalServiceNote", "NAPOMENA ZA SERVIS: Očistiti hladnjak/ventilator i zameniti termalnu pastu pre predaje računara kupcu.");

                ViewModel.AddLog("WARN", "ThermalGuard", noteMsg + " Pipeline nastavlja na sledeću fazu.");
            }));
        }

        private int GetPhaseTimeoutMinutes(int phase)
        {
            switch (phase)
            {
                case 1: return 15; // Mreža
                case 2: return 60; // Windows Update i drajveri
                case 3: return 20; // Optimizacija i debloat
                case 4: return 60; // Programi
                case 5: return 45; // Hardverski testovi (stres + periferije)
                case 6: return 45; // Završetak i čišćenje
                default: return 30;
            }
        }

        private static bool ExistsAnyFileOrAppPath(string[] candidatePaths, string[] appPathExeNames)
        {
            if (candidatePaths != null)
            {
                for (int i = 0; i < candidatePaths.Length; i++)
                {
                    string p = candidatePaths[i];
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        return true;
                    }
                }
            }

            if (appPathExeNames != null)
            {
                string[] regBases = new string[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\"
                };

                for (int e = 0; e < appPathExeNames.Length; e++)
                {
                    string exeName = appPathExeNames[e];
                    if (string.IsNullOrEmpty(exeName)) continue;

                    for (int r = 0; r < regBases.Length; r++)
                    {
                        try
                        {
                            using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(regBases[r] + exeName, false))
                            {
                                if (rk != null)
                                {
                                    object defVal = rk.GetValue("");
                                    string exePath = defVal != null ? defVal.ToString().Trim().Trim('"') : "";
                                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                    {
                                        return true;
                                    }
                                    object dirVal = rk.GetValue("Path");
                                    string dirPath = dirVal != null ? dirVal.ToString().Trim().Trim('"').TrimEnd(';') : "";
                                    if (!string.IsNullOrEmpty(dirPath) && File.Exists(Path.Combine(dirPath, exeName)))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    try
                    {
                        using (RegistryKey rkUser = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName, false))
                        {
                            if (rkUser != null)
                            {
                                object defVal = rkUser.GetValue("");
                                string exePath = defVal != null ? defVal.ToString().Trim().Trim('"') : "";
                                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        private List<string> GetMissingKeyApplications()
        {
            List<string> missing = new List<string>();

            // 1. Google Chrome
            string[] chromePaths = new string[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            };
            if (!ExistsAnyFileOrAppPath(chromePaths, new string[] { "chrome.exe" }))
            {
                missing.Add("Google Chrome");
            }

            // 2. Adobe Acrobat (uskladjeno sa PS Assert-InstalledApplicationsReality)
            string[] acrobatPaths = new string[]
            {
                @"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat 2020\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat 2020\Acrobat\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat Pro\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat Pro DC\Acrobat\Acrobat.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat Pro DC\Acrobat\Acrobat.exe",
                @"C:\Program Files\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe",
                @"C:\Program Files (x86)\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe"
            };
            if (!ExistsAnyFileOrAppPath(acrobatPaths, new string[] { "Acrobat.exe", "AcroRd32.exe" }))
            {
                missing.Add("Adobe Acrobat");
            }

            // 3. Microsoft Office (WINWORD / EXCEL, x64 & x86 C2R + MSI)
            string[] officePaths = new string[]
            {
                @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
                @"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
                @"C:\Program Files\Microsoft Office\Office16\WINWORD.EXE",
                @"C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE",
                @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
                @"C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
                @"C:\Program Files\Microsoft Office\Office16\EXCEL.EXE",
                @"C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE",
                @"C:\Program Files\Microsoft Office\Office15\WINWORD.EXE",
                @"C:\Program Files (x86)\Microsoft Office\Office15\WINWORD.EXE",
                @"C:\Program Files\Microsoft Office\Office15\EXCEL.EXE",
                @"C:\Program Files (x86)\Microsoft Office\Office15\EXCEL.EXE"
            };
            if (!ExistsAnyFileOrAppPath(officePaths, new string[] { "WINWORD.EXE", "EXCEL.EXE" }))
            {
                missing.Add("Microsoft Office (Word/Excel)");
            }

            // 4. WinRAR
            string[] winrarPaths = new string[]
            {
                @"C:\Program Files\WinRAR\WinRAR.exe",
                @"C:\Program Files (x86)\WinRAR\WinRAR.exe"
            };
            if (!ExistsAnyFileOrAppPath(winrarPaths, new string[] { "WinRAR.exe" }))
            {
                missing.Add("WinRAR");
            }

            return missing;
        }

        private bool PerformPhase4RealityCheck()
        {
            List<string> missing = GetMissingKeyApplications();
            if (missing.Count > 0)
            {
                string missingStr = string.Join(", ", missing.ToArray());
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "RealityCheck", "Post-Execution Reality Check (Faza 4) NEUSPEŠAN! Nedostaju ključni programi na disku: " + missingStr);
                    ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhase4MissingApps", "Greška u Fazi 4: Nedostaju ključni programi ({0}). Pokrenite ↻ Retry na kartici Programi ili koristite traku za oporavak dole."), missingStr);
                    SetFailedPhase(4);

                    string appNotFoundTxt = LocalizationService.Get("WinAppFileNotFound", "Fajl nije pronađen");
                    foreach (string item in missing)
                    {
                        if (item.Contains("Chrome")) ViewModel.UpdateAppStatus("Chrome", "FAIL", appNotFoundTxt);
                        if (item.Contains("Acrobat")) ViewModel.UpdateAppStatus("Acrobat", "FAIL", appNotFoundTxt);
                        if (item.Contains("Office")) ViewModel.UpdateAppStatus("Office", "FAIL", appNotFoundTxt);
                        if (item.Contains("WinRAR")) ViewModel.UpdateAppStatus("WinRAR", "FAIL", appNotFoundTxt);
                    }
                }));
                return false;
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.AddLog("SUCCESS", "RealityCheck", "Post-Execution Reality Check (Faza 4) USPEŠAN: Sve ključne aplikacije (Chrome, Acrobat, Office, WinRAR) su verifikovane na disku.");
            }));
            return true;
        }

        private bool CheckInstalledApplicationsReality()
        {
            return PerformPhase4RealityCheck();
        }

        private void PerformPhase2RealityCheck()
        {
            try
            {
                List<string> badDevices = new List<string>();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode != 0"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        try
                        {
                            object errObj = mo["ConfigManagerErrorCode"];
                            if (errObj != null)
                            {
                                uint errCode = Convert.ToUInt32(errObj);
                                if (errCode != 0)
                                {
                                    string devName = mo["Name"] != null ? mo["Name"].ToString() : LocalizationService.Get("MdlUnknownDevice", "Nepoznat uređaj");
                                    badDevices.Add(string.Format("{0} (Kod: {1})", devName, errCode));
                                }
                            }
                        }
                        finally
                        {
                            mo.Dispose();
                        }
                    }
                }

                if (badDevices.Count > 0)
                {
                    string badList = string.Join("; ", badDevices.ToArray());
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("WARN", "RealityCheck", string.Format("Faza 2 Reality Check UPOZORENJE: Detektovano {0} uređaja sa greškom drajvera: {1}", badDevices.Count, badList));
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinPhase2BadDevices", "Upozorenje: Pronađeno {0} neispravnih uređaja u Upravljaču uređaja."), badDevices.Count);
                    }));
                }
                else
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("SUCCESS", "RealityCheck", "Faza 2 Reality Check USPEŠAN: Svi uređaji u Upravljaču uređaja su ispravno prepoznati (0 neispravnih drajvera).");
                    }));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("WARN", "RealityCheck", "Nemoguće očitati status uređaja preko WMI: " + ex.Message);
                }));
            }
        }

        private int ComputeDeploymentDurationSeconds()
        {
            try
            {
                string startIso = null;
                string statePath = @"C:\setup\deploy-state.json";
                if (File.Exists(statePath))
                {
                    try
                    {
                        string json = File.ReadAllText(statePath, Encoding.UTF8);
                        JsonNode root = JsonHelper.Parse(json);
                        if (root != null && !root.IsNull)
                        {
                            if (root.ContainsKey("initial_start_time")) startIso = root["initial_start_time"].AsString(null);
                            else if (root.ContainsKey("InitialStartTime")) startIso = root["InitialStartTime"].AsString(null);
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(startIso))
                {
                    startIso = _initialStartTimeIso;
                }

                if (string.IsNullOrEmpty(startIso))
                {
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WIS", false))
                        {
                            if (key != null)
                            {
                                object regVal = key.GetValue("InitialStartTime");
                                if (regVal != null) startIso = regVal.ToString();
                            }
                        }
                    }
                    catch { }
                }

                DateTime parsedStart;
                if (!string.IsNullOrEmpty(startIso) &&
                    DateTime.TryParse(startIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsedStart))
                {
                    double totalSec = (DateTime.UtcNow - parsedStart.ToUniversalTime()).TotalSeconds;
                    if (totalSec > 0 && totalSec < (30.0 * 86400.0))
                    {
                        return (int)Math.Round(totalSec);
                    }
                }

                if (ViewModel != null && !string.IsNullOrEmpty(ViewModel.DurationString))
                {
                    string[] parts = ViewModel.DurationString.Trim().Split(':');
                    int hh, mm, ss;
                    if (parts.Length == 3 &&
                        int.TryParse(parts[0], out hh) &&
                        int.TryParse(parts[1], out mm) &&
                        int.TryParse(parts[2], out ss))
                    {
                        return Math.Max(0, (hh * 3600) + (mm * 60) + ss);
                    }
                }
            }
            catch { }
            return 0;
        }

        private void HandleFinalCompletion()
        {
            if (HasUnfinishedInteractiveHwTests())
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.IsCompleted = false;
                        ViewModel.IsServiceFinished = false;
                        ViewModel.CurrentPhase = 5;
                        ViewModel.AddLog("WARN", "Hardver", "HARD GATE: Blokiran završetak servisa i prikaz Završnog kartona dok se ne završe obavezni interaktivni testovi.");
                    }
                    ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                }));
                return;
            }

            HidePendingInteractiveTestsWarning();
            RefreshPeripheralsAfterDrivers();
            StopScreenCaptureWorker();
            ClearRunOnceEntries();
            EnsureStressTestResultFile();

            bool isSensorUnavailable = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                       _lastStressMaxTemp <= 0.5f);

            if (isSensorUnavailable)
            {
                _criticalStressFailure = false;
                _lastStressEmergencyAbort = false;
                _lastStressStatus = "UNKNOWN";
            }

            // RecordThermalOverheatNote beleži napomenu bez zaustavljanja toka instalacije
            // kada procesor stvarno dostigne >= 95°C (ili emergency_abort sa očitanom temperaturom).
            bool isActuallyThermalCritical = HasCriticalThermalEvidence();
            if (isActuallyThermalCritical)
            {
                RecordThermalOverheatNote();
            }

            // IMP-44: Provera ključnih aplikacija je izvršena pre pokretanja Faze 6 (pre Anti-Leak brisanja Wi-Fi mreže).
            // Ukoliko su neke aplikacije i dalje odsutne (npr. operater je svesno potvrdio završetak), ne vraćamo CurrentPhase = 4 posle Anti-Leak-a,
            // već označavamo završetak kao NEEDS_ATTENTION.
            List<string> missingApps = GetMissingKeyApplications();
            bool hasMissingAppsAtCompletion = (missingApps.Count > 0);
            if (hasMissingAppsAtCompletion)
            {
                string missingStr = string.Join(", ", missingApps.ToArray());
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        string appNotFoundTxt = LocalizationService.Get("WinAppFileNotFound", "Fajl nije pronađen");
                        foreach (string item in missingApps)
                        {
                            if (item.Contains("Chrome")) ViewModel.UpdateAppStatus("Chrome", "FAIL", appNotFoundTxt);
                            if (item.Contains("Acrobat")) ViewModel.UpdateAppStatus("Acrobat", "FAIL", appNotFoundTxt);
                            if (item.Contains("Office")) ViewModel.UpdateAppStatus("Office", "FAIL", appNotFoundTxt);
                            if (item.Contains("WinRAR")) ViewModel.UpdateAppStatus("WinRAR", "FAIL", appNotFoundTxt);
                        }
                        ViewModel.AddLog("WARN", "RealityCheck", "Završetak servisa uz upozorenje (NEEDS_ATTENTION) — nedostaju aplikacije: " + missingStr);
                    }
                }));
            }
            if (_telemetryService != null && !_phase6ScriptCompletedSuccessfully)
            {
                string diagStatus;
                if (isActuallyThermalCritical)
                {
                    diagStatus = "THERMAL_CRITICAL";
                }
                else if (isSensorUnavailable || string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    diagStatus = "SENSOR_UNAVAILABLE";
                }
                else if (string.Equals(_lastStressStatus, "WARN", StringComparison.OrdinalIgnoreCase))
                {
                    diagStatus = "WARN";
                }
                else if (string.Equals(_lastStressStatus, "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    diagStatus = "ERROR";
                }
                else if (string.Equals(_lastStressStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    diagStatus = "CANCELLED";
                }
                else if (!isSensorUnavailable && _lastStressMaxTemp > 0.5f && string.Equals(_lastStressStatus, "PASS", StringComparison.OrdinalIgnoreCase))
                {
                    diagStatus = "OK";
                }
                else
                {
                    diagStatus = "NOT_VERIFIED";
                }

                bool coolingVerified = string.Equals(diagStatus, "OK", StringComparison.OrdinalIgnoreCase);
                int durationSeconds = ComputeDeploymentDurationSeconds();
                string durationFormatted = string.Format("{0}m {1:D2}s", durationSeconds / 60, durationSeconds % 60);
                string durationClock = durationSeconds > 0
                    ? string.Format("{0:00}:{1:00}:{2:00}", durationSeconds / 3600, (durationSeconds % 3600) / 60, durationSeconds % 60)
                    : ViewModel.DurationString;

                var compData = new Dictionary<string, object>
                {
                    { "phase", 6 },
                    { "phase_name", "Zavrsetak" },
                    { "status", hasMissingAppsAtCompletion ? "NEEDS_ATTENTION" : "SUCCESS" },
                    { "stress_status", _lastStressStatus },
                    { "diagnostics_status", diagStatus },
                    { "cooling_verified", coolingVerified },
                    { "manual_check_required", !coolingVerified },
                    { "max_cpu_temp_c", _lastStressMaxTemp },
                    { "sensor_available", !isSensorUnavailable },
                    { "duration", durationClock },
                    { "duration_seconds", durationSeconds },
                    { "duration_formatted", durationFormatted },
                    { "apps_summary", ViewModel.AppsSummaryText }
                };
                if (!string.IsNullOrEmpty(_thermalOverheatNote))
                {
                    compData["thermal_note"] = _thermalOverheatNote;
                    compData["diagnostics_warning"] = _thermalOverheatNote;
                    compData["notes"] = new List<object> { _thermalOverheatNote };
                }
                _telemetryService.SendLifecycleEvent("COMPLETED", compData);
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_embeddedHwTestShell != null)
                {
                    try { _embeddedHwTestShell.SyncFinalReportBeforeCompletion(); } catch { }
                }
                ViewModel.RebuildOverviewAlerts();
                ViewModel.CurrentPhase = 6;
                ViewModel.Progress = 100;
                ViewModel.DeploymentStatus = hasMissingAppsAtCompletion ? "NEEDS_ATTENTION" : "READY";
                ViewModel.IsCompleted = true;
                ViewModel.IsServiceFinished = true;
                ViewModel.CriticalStressFailure = false;
                ViewModel.CompletionTitle = isActuallyThermalCritical
                    ? LocalizationService.Get("WinCompletionTitleOverheat", "⚠️ INSTALACIJA ZAVRŠENA (NAPOMENA: PREGREVANJE CPU)")
                    : LocalizationService.Get("WinCompletionTitleDone", "INSTALACIJA ZAVRŠENA");
                if (isActuallyThermalCritical)
                {
                    float noteTemp = _lastStressMaxTemp > 0.5f ? _lastStressMaxTemp : 95.0f;
                    string noteTxt = !string.IsNullOrEmpty(_thermalOverheatNote)
                        ? _thermalOverheatNote
                        : string.Format(LocalizationService.Get("WinThermalOverheatFallback", "NAPOMENA: Procesor se pregrevao tokom stres testa ({0:F1}°C >= 95°C) — potreban servis hlađenja / zamena termalne paste."), noteTemp);
                    ViewModel.StressResultStatus = "THERMAL_CRITICAL";
                    ViewModel.CompletionSummary = "⚠️ " + noteTxt;
                    ViewModel.LiveStatus = LocalizationService.Get("WinCompletionLiveThermalNote", "⚠️ Instalacija završena uz termalnu napomenu (sačuvano u izveštaju).");
                    ViewModel.ThermalVerdictTitle = string.Format(LocalizationService.Get("WinThermalOverheatTitle", "⚠️ NAPOMENA: PREGREVANJE CPU ({0:F1}°C >= 95°C)"), noteTemp);
                    ViewModel.ThermalVerdictDescription = noteTxt;
                    ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(230, 126, 34);
                    ViewModel.ThermalRecommendation = LocalizationService.Get("WinThermalServiceNoteShort", "NAPOMENA ZA SERVIS: Očistiti hladnjak/ventilator i zameniti termalnu pastu.");
                }
                else if (isSensorUnavailable || string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.StressResultStatus = "UNKNOWN";
                    ViewModel.CompletionSummary = LocalizationService.Get("WinCompletionSensorUnknown", "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.");
                    ViewModel.LiveStatus = LocalizationService.Get("WinCompletionSensorUnknown", "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.");
                    ViewModel.ThermalVerdictTitle = LocalizationService.Get("WinThermalSensorUnknownTitle", "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)");
                    ViewModel.ThermalVerdictDescription = LocalizationService.Get("WinThermalSensorUnknownDesc", "Hlađenje nije automatski potvrđeno; obavezna ručna provera. Potrebna je ručna provera hladnjaka i ventilatora.");
                    ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18); // #F39C12
                    ViewModel.ThermalRecommendation = LocalizationService.Get("WinThermalSensorUnknownRec", "Preporuka: Proverite rad kulera i ventilaciju u BIOS/UEFI ili fizički pre isporuke računara.");
                }
                else if (string.Equals(_lastStressStatus, "WARN", StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.CompletionSummary = string.Format(LocalizationService.Get("WinCompletionSummaryWarn", "Svi programi, drajveri i optimizacije su kompletirani. UPOZORENJE: Maksimalna CPU temperatura na stres testu je dostigla {0:F1}°C (preporučen preventivni servis hlađenja)."), _lastStressMaxTemp);
                }
                else
                {
                    ViewModel.CompletionSummary = LocalizationService.Get("WinCompletionSummaryOk", "Svi programi, drajveri, stres test i sistemske optimizacije su kompletirani.");
                }
                ViewModel.ShutdownCountdown = 0;
                ViewModel.ShutdownTimerActive = false;
                ViewModel.AddLog("SUCCESS", "Sistem", "SERVIS ZAVRŠEN! Računar uspešno pripremljen za klijenta.");

                ThreadPool.QueueUserWorkItem(delegate(object _)
                {
                    try
                    {
                        Console.Beep(523, 120);
                        Console.Beep(659, 120);
                        Console.Beep(784, 150);
                        Console.Beep(1046, 250);
                    }
                    catch
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    }
                });
                EnsureLabelPrintButtons();
                EnsureHwTestButtons();
            }));
        }

        private int GetSavedDeployStatePhase()
        {
            string statePath = @"C:\setup\deploy-state.json";
            if (File.Exists(statePath))
            {
                try
                {
                    string json = File.ReadAllText(statePath, Encoding.UTF8);
                    var root = JsonHelper.Parse(json);
                    if (root != null && !root.IsNull)
                    {
                        lock (_restartScheduleLock)
                        {
                            if (root.ContainsKey("delayed_restart_minutes"))
                            {
                                _delayedRestartMinutesTotal = root["delayed_restart_minutes"].AsInt(_delayedRestartMinutesTotal);
                            }
                            if (root.ContainsKey("restart_due_at"))
                            {
                                string dueStr = root["restart_due_at"].AsString(null);
                                DateTime parsedDue;
                                if (!string.IsNullOrEmpty(dueStr) &&
                                    DateTime.TryParse(dueStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsedDue))
                                {
                                    DateTime dueUtc = parsedDue.ToUniversalTime();
                                    if (dueUtc > DateTime.UtcNow)
                                    {
                                        _restartDueAtUtc = dueUtc;
                                        _restartPending = true;
                                    }
                                }
                            }
                        }

                        if (root.ContainsKey("post_reboot_finalize"))
                        {
                            _postRebootFinalizePending = root["post_reboot_finalize"].AsBool(false);
                        }
                        if (root.ContainsKey("phase")) return root["phase"].AsInt(1);
                        if (root.ContainsKey("Phase")) return root["Phase"].AsInt(1);
                    }
                }
                catch { }
            }
            return 1;
        }

        private DateTime GetScheduledRestartDueUtc()
        {
            lock (_restartScheduleLock)
            {
                try
                {
                    string statePath = @"C:\setup\deploy-state.json";
                    if (File.Exists(statePath))
                    {
                        string json = File.ReadAllText(statePath, Encoding.UTF8);
                        var root = JsonHelper.Parse(json);
                        if (root != null && !root.IsNull && root.ContainsKey("restart_due_at"))
                        {
                            string dueStr = root["restart_due_at"].AsString(null);
                            DateTime parsedDue;
                            if (!string.IsNullOrEmpty(dueStr) &&
                                DateTime.TryParse(dueStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsedDue))
                            {
                                DateTime fileDueUtc = parsedDue.ToUniversalTime();
                                if (fileDueUtc > _restartDueAtUtc)
                                {
                                    _restartDueAtUtc = fileDueUtc;
                                    _restartPending = fileDueUtc > DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }
                catch { }

                return _restartDueAtUtc;
            }
        }

        private static object JsonNodeToNativeObject(JsonNode node)
        {
            if (node == null || node.IsNull) return null;
            switch (node.NodeType)
            {
                case JsonNodeType.Boolean:
                case JsonNodeType.Number:
                case JsonNodeType.String:
                    return node.Value;
                case JsonNodeType.Array:
                    var list = new List<object>();
                    for (int i = 0; i < node.Elements.Count; i++)
                    {
                        list.Add(JsonNodeToNativeObject(node.Elements[i]));
                    }
                    return list;
                case JsonNodeType.Object:
                    var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, JsonNode> kvp in node.Properties)
                    {
                        dict[kvp.Key] = JsonNodeToNativeObject(kvp.Value);
                    }
                    return dict;
                default:
                    return null;
            }
        }

        private Dictionary<string, object> ReadExistingDeployStateMapLocked()
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string statePath = @"C:\setup\deploy-state.json";
            if (!File.Exists(statePath)) return map;
            try
            {
                string existingJson = File.ReadAllText(statePath, Encoding.UTF8);
                JsonNode existingRoot = JsonHelper.Parse(existingJson);
                if (existingRoot != null && existingRoot.IsObject)
                {
                    var converted = JsonNodeToNativeObject(existingRoot) as Dictionary<string, object>;
                    if (converted != null)
                    {
                        foreach (KeyValuePair<string, object> kv in converted)
                        {
                            map[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private int GetDeployStateInt(string key, int fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            lock (_restartScheduleLock)
            {
                try
                {
                    string statePath = @"C:\setup\deploy-state.json";
                    if (!File.Exists(statePath)) return fallback;
                    string json = File.ReadAllText(statePath, Encoding.UTF8);
                    JsonNode root = JsonHelper.Parse(json);
                    if (root != null && root.IsObject && root.ContainsKey(key))
                    {
                        return root[key].AsInt(fallback);
                    }
                }
                catch { }
                return fallback;
            }
        }

        private void SetDeployStateInt(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_restartScheduleLock)
            {
                try
                {
                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                    Dictionary<string, object> stateMap = ReadExistingDeployStateMapLocked();
                    stateMap[key] = value;
                    stateMap["timestamp"] = DateTime.Now.ToString("o");
                    File.WriteAllText(@"C:\setup\deploy-state.json", JsonHelper.Serialize(stateMap), Encoding.UTF8);
                }
                catch { }
            }
        }

        private bool TryPersistRestartScheduleState(out string errorMessage)
        {
            errorMessage = null;
            lock (_restartScheduleLock)
            {
                try
                {
                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                    if (string.IsNullOrEmpty(_initialStartTimeIso))
                    {
                        _initialStartTimeIso = DateTime.Now.ToString("o");
                    }

                    Dictionary<string, object> stateMap = ReadExistingDeployStateMapLocked();
                    int phaseToSave = 1;
                    string statePath = @"C:\setup\deploy-state.json";
                    if (stateMap.ContainsKey("phase") && stateMap["phase"] != null)
                    {
                        int.TryParse(Convert.ToString(stateMap["phase"], System.Globalization.CultureInfo.InvariantCulture), out phaseToSave);
                    }
                    else if (stateMap.ContainsKey("Phase") && stateMap["Phase"] != null)
                    {
                        int.TryParse(Convert.ToString(stateMap["Phase"], System.Globalization.CultureInfo.InvariantCulture), out phaseToSave);
                    }
                    else if (ViewModel != null && ViewModel.CurrentPhase > 0)
                    {
                        phaseToSave = ViewModel.CurrentPhase;
                    }
                    if (phaseToSave <= 0) phaseToSave = 1;

                    stateMap["phase"] = phaseToSave;
                    stateMap["Phase"] = phaseToSave;
                    if (!stateMap.ContainsKey("initial_start_time") || stateMap["initial_start_time"] == null)
                    {
                        stateMap["initial_start_time"] = _initialStartTimeIso;
                    }
                    stateMap["timestamp"] = DateTime.Now.ToString("o");
                    stateMap["computer_name"] = Environment.MachineName;
                    stateMap["restart_pending"] = _restartPending && _restartDueAtUtc > DateTime.UtcNow;
                    stateMap["restart_due_at"] = _restartDueAtUtc > DateTime.MinValue ? _restartDueAtUtc.ToLocalTime().ToString("o") : "";
                    stateMap["delayed_restart_minutes"] = _delayedRestartMinutesTotal;

                    string json = JsonHelper.Serialize(stateMap);
                    File.WriteAllText(statePath, json, Encoding.UTF8);
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// IMP-52: Pri resume-u u Fazu 5 ili 6 (npr. posle UEFI BIOS restarta) hidrira ViewModel.Apps iz deploy-state.json,
        /// step-journal.json, license-status.json i direktnom verifikacijom fajlova na disku (Get-WisAppPresence paritet),
        /// kako inicijalni WAIT statusi ne bi pregazili već instalirane programe na ServiceHub-u.
        /// </summary>
        private void HydrateAppsStateOnResume()
        {
            if (ViewModel == null) return;

            var hydrated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Pokušaj čitanja već snimljenih statusa iz C:\setup\deploy-state.json
            try
            {
                string statePath = @"C:\setup\deploy-state.json";
                if (File.Exists(statePath))
                {
                    string json = File.ReadAllText(statePath, Encoding.UTF8);
                    JsonNode root = JsonHelper.Parse(json);
                    if (root != null && root.IsObject && root.ContainsKey("apps"))
                    {
                        JsonNode appsNode = root["apps"];
                        if (appsNode != null && appsNode.IsObject)
                        {
                            foreach (string key in appsNode.Keys)
                            {
                                JsonNode valNode = appsNode[key];
                                string st = valNode != null && valNode.IsObject
                                    ? valNode["status"].AsString("WAIT")
                                    : (valNode != null ? valNode.AsString("WAIT") : "WAIT");
                                st = (st ?? "WAIT").Trim().ToUpperInvariant();
                                if (!string.IsNullOrEmpty(key) && st != "WAIT")
                                {
                                    hydrated[key] = st;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Dopuna iz C:\setup\step-journal.json (P4.* koraci)
            try
            {
                string journalPath = @"C:\setup\step-journal.json";
                if (File.Exists(journalPath))
                {
                    string jTxt = File.ReadAllText(journalPath, Encoding.UTF8);
                    JsonNode jRoot = JsonHelper.Parse(jTxt);
                    if (jRoot != null && jRoot.IsObject && jRoot.ContainsKey("steps") && jRoot["steps"].IsObject)
                    {
                        JsonNode stepsNode = jRoot["steps"];
                        var stepToAppMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "P4.Runtimes", "DotNet" },
                            { "P4.Chrome", "Chrome" },
                            { "P4.WinRAR", "WinRAR" },
                            { "P4.Acrobat", "Acrobat" },
                            { "P4.Office", "Office" },
                            { "P4.BatteryBar", "BatteryBar" },
                            { "P4.Shortcuts", "Shortcuts" },
                            { "P4.Activation", "License" }
                        };
                        foreach (KeyValuePair<string, string> kv in stepToAppMap)
                        {
                            if (hydrated.ContainsKey(kv.Value) && hydrated[kv.Value] != "WAIT") continue;
                            if (!stepsNode.ContainsKey(kv.Key)) continue;
                            JsonNode sNode = stepsNode[kv.Key];
                            if (sNode != null && sNode.IsObject)
                            {
                                string st = sNode["state"].AsString("").Trim().ToUpperInvariant();
                                if (st == "OK" || st == "SKIPPED_DONE")
                                {
                                    hydrated[kv.Value] = "OK";
                                }
                                else if (st == "WARN" || st == "ERROR" || st == "SKIPPED" || st == "SKIPPED_USER")
                                {
                                    hydrated[kv.Value] = st;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Fizička verifikacija prisustva aplikacija na disku (Get-WisAppPresence paritet)
            try
            {
                List<string> missingKeyApps = GetMissingKeyApplications();
                bool hasChrome = true;
                bool hasAcrobat = true;
                bool hasOffice = true;
                bool hasWinRar = true;
                for (int i = 0; i < missingKeyApps.Count; i++)
                {
                    string m = missingKeyApps[i] ?? "";
                    if (m.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0) hasChrome = false;
                    else if (m.IndexOf("Acrobat", StringComparison.OrdinalIgnoreCase) >= 0) hasAcrobat = false;
                    else if (m.IndexOf("Office", StringComparison.OrdinalIgnoreCase) >= 0) hasOffice = false;
                    else if (m.IndexOf("WinRAR", StringComparison.OrdinalIgnoreCase) >= 0) hasWinRar = false;
                }

                if (hasChrome && (!hydrated.ContainsKey("Chrome") || hydrated["Chrome"] == "WAIT")) hydrated["Chrome"] = "OK";
                if (hasAcrobat && (!hydrated.ContainsKey("Acrobat") || hydrated["Acrobat"] == "WAIT")) hydrated["Acrobat"] = "OK";
                if (hasOffice && (!hydrated.ContainsKey("Office") || hydrated["Office"] == "WAIT")) hydrated["Office"] = "OK";
                if (hasWinRar && (!hydrated.ContainsKey("WinRAR") || hydrated["WinRAR"] == "WAIT")) hydrated["WinRAR"] = "OK";

                // DotNet 8 Runtime
                if (!hydrated.ContainsKey("DotNet") || hydrated["DotNet"] == "WAIT")
                {
                    if (File.Exists(@"C:\Program Files\dotnet\dotnet.exe") ||
                        Directory.Exists(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"))
                    {
                        hydrated["DotNet"] = "OK";
                    }
                }

                // BatteryBar
                if (!hydrated.ContainsKey("BatteryBar") || hydrated["BatteryBar"] == "WAIT")
                {
                    string[] bbPaths = new string[]
                    {
                        @"C:\Tools\BatteryBar\BatteryBar.exe",
                        @"C:\Program Files\BatteryBar\BatteryBar.exe",
                        @"C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
                    };
                    if (ExistsAnyFileOrAppPath(bbPaths, null))
                    {
                        hydrated["BatteryBar"] = "OK";
                    }
                    else
                    {
                        hydrated["BatteryBar"] = "SKIPPED";
                    }
                }

                // Shortcuts
                if (!hydrated.ContainsKey("Shortcuts") || hydrated["Shortcuts"] == "WAIT")
                {
                    if (File.Exists(@"C:\Users\Public\Desktop\Google Chrome.lnk") ||
                        File.Exists(@"C:\Users\Public\Desktop\Word.lnk") ||
                        File.Exists(@"C:\Users\Public\Desktop\Adobe Acrobat.lnk"))
                    {
                        hydrated["Shortcuts"] = "OK";
                    }
                }

                // License (iz C:\setup\license-status.json)
                if (!hydrated.ContainsKey("License") || hydrated["License"] == "WAIT")
                {
                    string licPath = @"C:\setup\license-status.json";
                    if (File.Exists(licPath))
                    {
                        string licJson = File.ReadAllText(licPath, Encoding.UTF8);
                        JsonNode licRoot = JsonHelper.Parse(licJson);
                        if (licRoot != null && licRoot.IsObject)
                        {
                            bool winAct = licRoot["windows_activated"].AsBool(false);
                            bool offInst = licRoot["office_installed"].AsBool(hasOffice);
                            bool offAct = licRoot["office_activated"].AsBool(false);
                            hydrated["License"] = (winAct && (!offInst || offAct)) ? "OK" : "WARN";
                        }
                    }
                }
            }
            catch { }

            foreach (KeyValuePair<string, string> kv in hydrated)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    ViewModel.UpdateAppStatus(kv.Key, kv.Value);
                }
            }
            ViewModel.UpdateAppsSummary();
        }

        private bool GetSavedPostRebootFinalizeFlag()
        {
            string statePath = @"C:\setup\deploy-state.json";
            if (!File.Exists(statePath)) return false;
            try
            {
                string json = File.ReadAllText(statePath, Encoding.UTF8);
                var root = JsonHelper.Parse(json);
                if (root != null && !root.IsNull && root.ContainsKey("post_reboot_finalize"))
                {
                    return root["post_reboot_finalize"].AsBool(false);
                }
            }
            catch { }
            return false;
        }

        private bool WaitForPhaseProcessExitWithActiveTime(Process proc, int timeoutMinutes)
        {
            if (proc == null) return true;
            long maxActiveMs = Math.Max(60000L, (long)timeoutMinutes * 60L * 1000L);
            long activeElapsedMs = 0L;
            DateTime lastTickUtc = DateTime.UtcNow;

            while (true)
            {
                if (proc.WaitForExit(1000))
                {
                    return true;
                }

                DateTime nowUtc = DateTime.UtcNow;
                long deltaMs = (long)(nowUtc - lastTickUtc).TotalMilliseconds;
                lastTickUtc = nowUtc;

                if (deltaMs < 0L) deltaMs = 1000L;
                // IMP-67: Ako je sistem bio u sleep/hibernaciji (skok > 15s), ne uračunavaj sleep pauzu u timeout faze
                if (deltaMs > 15000L)
                {
                    deltaMs = 1000L;
                }

                bool isPaused = (ViewModel != null && ViewModel.IsPaused);
                if (!isPaused)
                {
                    activeElapsedMs += deltaMs;
                }

                if (activeElapsedMs >= maxActiveMs)
                {
                    return false;
                }
            }
        }

        private void WaitForBackgroundDriverDownloadIfActive(int maxWaitSeconds)
        {
            string statusPath = @"C:\setup\driver-stage-status.json";
            DateTime startUtc = DateTime.UtcNow;
            DateTime lastHeartbeatLogUtc = DateTime.MinValue;
            string lastNormState = "";
            int lastWorkerPid = 0;
            while ((DateTime.UtcNow - startUtc).TotalSeconds < maxWaitSeconds)
            {
                if (!File.Exists(statusPath)) break;
                string normState = "";
                string currentItem = "";
                string updatedAtIso = "";
                DateTime fileWriteUtc = DateTime.MinValue;
                int workerPid = 0;
                int hubStaged = 0;
                int wuDownloaded = 0;
                int totalCount = 0;
                try
                {
                    fileWriteUtc = File.GetLastWriteTimeUtc(statusPath);
                    string json = File.ReadAllText(statusPath, Encoding.UTF8);
                    var root = JsonHelper.Parse(json);
                    if (root != null && !root.IsNull)
                    {
                        normState = (root.ContainsKey("state") ? root["state"].AsString("") : "").Trim().ToUpperInvariant();
                        currentItem = root.ContainsKey("current_item") ? root["current_item"].AsString("") : "";
                        updatedAtIso = root.ContainsKey("updated_at_utc") ? root["updated_at_utc"].AsString("") : "";
                        workerPid = root.ContainsKey("worker_pid") ? root["worker_pid"].AsInt(0) : 0;
                        hubStaged = root.ContainsKey("hub_staged_count") ? root["hub_staged_count"].AsInt(0) : 0;
                        wuDownloaded = root.ContainsKey("wu_downloaded_count") ? root["wu_downloaded_count"].AsInt(0) : 0;
                        totalCount = root.ContainsKey("total_count") ? root["total_count"].AsInt(0) : 0;
                    }
                }
                catch { }

                lastNormState = normState;
                lastWorkerPid = workerPid;

                if (normState != "SEARCHING" && normState != "DOWNLOADING")
                {
                    break;
                }

                if (!IsDriverStageStatusAlive(normState, workerPid, updatedAtIso, fileWriteUtc))
                {
                    string deadWorkerMsg = "V351-02: Pozadinski radnik za preuzimanje drajvera više nije aktivan — resetujem driver-stage-status.json i nastavljam.";
                    if (_telemetryService != null)
                    {
                        _telemetryService.PushLog("[WARN] [TaskRunner] " + deadWorkerMsg);
                    }
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("WARN", "TaskRunner", deadWorkerMsg);
                        }
                    }));
                    ForceResetStuckDriverStageStatus("Pozadinski radnik za preuzimanje drajvera vise nije aktivan");
                    lastNormState = "FAILED";
                    break;
                }

                PollDriverStageStatusFile();
                int doneCount = hubStaged + wuDownloaded;
                int elapsedSec = Math.Max(0, (int)(DateTime.UtcNow - startUtc).TotalSeconds);
                string elapsedLabel = string.Format("{0}m {1:D2}s", elapsedSec / 60, elapsedSec % 60);
                string itemLabel = !string.IsNullOrWhiteSpace(currentItem) ? currentItem.Trim() : "obrada zahteva...";

                string waitMsg = totalCount > 0
                    ? string.Format(LocalizationService.Get("WinWaitDriverDownloadProgressFmt", "Čekam dovršetak preuzimanja drajvera ({0}/{1})... {2}"), doneCount, totalCount, itemLabel)
                    : string.Format("{0} ({1}: {2})", LocalizationService.Get("WinWaitDriverDownloadLive", "Čekam dovršetak preuzimanja drajvera u pozadini..."), elapsedLabel, itemLabel);

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.SubPhase = LocalizationService.Get("WinWaitDriverDownloadSub", "Preuzimanje drajvera u toku...");
                    ViewModel.LiveStatus = waitMsg;
                }));

                if (lastHeartbeatLogUtc == DateTime.MinValue || (DateTime.UtcNow - lastHeartbeatLogUtc).TotalSeconds >= 30.0)
                {
                    lastHeartbeatLogUtc = DateTime.UtcNow;
                    string hbLog = totalCount > 0
                        ? string.Format("IMP-96: Pozadinsko preuzimanje drajvera [{0}] ({1}/{2}, proteklo {3}) — CurrentItem: {4}", normState, doneCount, totalCount, elapsedLabel, itemLabel)
                        : string.Format("IMP-96: Pozadinsko preuzimanje drajvera [{0}] (proteklo {1}) — CurrentItem: {2}", normState, elapsedLabel, itemLabel);
                    if (_telemetryService != null)
                    {
                        _telemetryService.PushLog("[INFO] [TaskRunner] " + hbLog);
                    }
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.AddLog("INFO", "TaskRunner", hbLog);
                        }
                    }));
                }

                Thread.Sleep(2000);
            }

            if (lastNormState == "SEARCHING" || lastNormState == "DOWNLOADING")
            {
                TryKillPowerShellWorker(lastWorkerPid);
                ForceResetStuckDriverStageStatus("Timeout (15 min) pozadinskog preuzimanja drajvera");
                string dlTimeoutMsg = "V351-02: Isteklo čekanje na pozadinsko preuzimanje drajvera; nastavljam sa lokalno preuzetim drajverima.";
                if (_telemetryService != null)
                {
                    _telemetryService.PushLog("[WARN] [TaskRunner] " + dlTimeoutMsg);
                }
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("WARN", "TaskRunner", dlTimeoutMsg);
                    }
                }));
            }
        }

        private void ResetStuckStageStatusIfActive(string stage, string reason)
        {
            if (!string.Equals(stage, "InstallStaged", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(stage, "PostRebootFinalize", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                string statusPath = @"C:\setup\driver-stage-status.json";
                if (!File.Exists(statusPath)) return;
                string json = File.ReadAllText(statusPath, Encoding.UTF8);
                var root = JsonHelper.Parse(json);
                if (root == null || root.IsNull) return;
                string st = (root.ContainsKey("state") ? root["state"].AsString("") : "").Trim().ToUpperInvariant();
                if (st == "SEARCHING" || st == "DOWNLOADING" || st == "INSTALLING")
                {
                    ForceResetStuckDriverStageStatus(reason);
                }
            }
            catch { }
        }

        private void RunPhase2DriversStage(string stage, int timeoutMinutes)
        {
            bool allowPhaseUpdate = string.Equals(stage, "FastNas", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(stage, "Full", StringComparison.OrdinalIgnoreCase);
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = FindPhaseScriptPath(baseDir, "Phase2-Drivers.ps1");
                if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -Stage {1}", scriptPath, stage),
                    WorkingDirectory = Directory.Exists(@"C:\setup") ? @"C:\setup" : (Path.GetDirectoryName(scriptPath) ?? @"C:\"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return;
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string line = e.Data.Trim();
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (line.StartsWith("{") && line.EndsWith("}")) ProcessIncomingJson(line, allowPhaseUpdate);
                            else ParseAndAddRawLogLine(line);
                        }));
                        if (_telemetryService != null) _telemetryService.PushLog(line);
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string errLine = e.Data.Trim();
                        if (_telemetryService != null) _telemetryService.PushLog(errLine);
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!WaitForPhaseProcessExitWithActiveTime(proc, timeoutMinutes))
                    {
                        try
                        {
                            ProcessStartInfo tkPsi = new ProcessStartInfo("taskkill.exe", string.Format("/PID {0} /T /F", proc.Id))
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            using (Process tk = Process.Start(tkPsi))
                            {
                                if (tk != null) tk.WaitForExit(5000);
                            }
                        }
                        catch { }
                        try { proc.Kill(); } catch { }
                        string stageTimeoutMsg = string.Format("V351-02: Timeout ({0} min) za driver stage '{1}' — prekidam proces i čistim status.", timeoutMinutes, stage);
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog("[WARN] [TaskRunner] " + stageTimeoutMsg);
                        }
                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("WARN", "TaskRunner", stageTimeoutMsg);
                            }
                        }));
                        ResetStuckStageStatusIfActive(stage, string.Format("Timeout ({0} min) za Stage={1}", timeoutMinutes, stage));
                    }
                    else
                    {
                        try { proc.WaitForExit(2000); } catch { }
                        int exitCode = -1;
                        try { exitCode = proc.ExitCode; } catch { }
                        ResetStuckStageStatusIfActive(stage, string.Format("Stage={0} zavrsio bez finalnog statusa (ExitCode={1})", stage, exitCode));
                    }
                }
            }
            catch { }
        }

        private void RunPhase2DriversStageAsync(string stage)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptPath = FindPhaseScriptPath(baseDir, "Phase2-Drivers.ps1");
                    if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath)) return;

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -Stage {1}", scriptPath, stage),
                        WorkingDirectory = Directory.Exists(@"C:\setup") ? @"C:\setup" : (Path.GetDirectoryName(scriptPath) ?? @"C:\"),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch { }
            });
        }

        private void SaveDeployStatePhase(int phase, bool postRebootFinalize = false)
        {
            try
            {
                lock (_restartScheduleLock)
                {
                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                    if (string.IsNullOrEmpty(_initialStartTimeIso))
                    {
                        _initialStartTimeIso = DateTime.Now.ToString("o");
                    }

                    Dictionary<string, object> stateMap = ReadExistingDeployStateMapLocked();
                    Dictionary<string, string> existingApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string existingHubUrl = null;
                    string statePath = @"C:\setup\deploy-state.json";
                    if (File.Exists(statePath))
                    {
                        try
                        {
                            string existingJson = File.ReadAllText(statePath, Encoding.UTF8);
                            JsonNode existingRoot = JsonHelper.Parse(existingJson);
                            if (existingRoot != null && existingRoot.IsObject)
                            {
                                if (existingRoot.ContainsKey("hub_url"))
                                {
                                    existingHubUrl = existingRoot["hub_url"].AsString(null);
                                }
                                if (existingRoot.ContainsKey("apps") && existingRoot["apps"].IsObject)
                                {
                                    JsonNode appsNode = existingRoot["apps"];
                                    foreach (string k in appsNode.Keys)
                                    {
                                        string v = appsNode[k] != null ? appsNode[k].AsString("WAIT") : "WAIT";
                                        if (!string.IsNullOrEmpty(k) && !string.Equals(v, "WAIT", StringComparison.OrdinalIgnoreCase))
                                        {
                                            existingApps[k] = v;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (ViewModel != null)
                    {
                        Dictionary<string, string> currentApps = ViewModel.GetAppsStatusSnapshot();
                        foreach (KeyValuePair<string, string> kv in currentApps)
                        {
                            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value) &&
                                !string.Equals(kv.Value, "WAIT", StringComparison.OrdinalIgnoreCase))
                            {
                                existingApps[kv.Key] = kv.Value;
                            }
                        }
                    }

                    string prevRecordedComp = stateMap.ContainsKey("computer_name") && stateMap["computer_name"] != null
                        ? Convert.ToString(stateMap["computer_name"]).Trim()
                        : "";
                    string curComp = Environment.MachineName ?? "";
                    if (!string.IsNullOrEmpty(prevRecordedComp) &&
                        !string.Equals(prevRecordedComp, curComp, StringComparison.OrdinalIgnoreCase) &&
                        !prevRecordedComp.StartsWith("TK-", StringComparison.OrdinalIgnoreCase) &&
                        (!stateMap.ContainsKey("previous_hostname") || stateMap["previous_hostname"] == null || string.IsNullOrWhiteSpace(Convert.ToString(stateMap["previous_hostname"]))))
                    {
                        stateMap["previous_hostname"] = prevRecordedComp;
                    }
                    if (!string.IsNullOrEmpty(curComp) &&
                        !curComp.StartsWith("TK-", StringComparison.OrdinalIgnoreCase) &&
                        (!stateMap.ContainsKey("initial_hostname") || stateMap["initial_hostname"] == null || string.IsNullOrWhiteSpace(Convert.ToString(stateMap["initial_hostname"]))))
                    {
                        stateMap["initial_hostname"] = curComp;
                    }

                    stateMap["phase"] = phase;
                    stateMap["Phase"] = phase;
                    stateMap["post_reboot_finalize"] = postRebootFinalize;
                    if (!stateMap.ContainsKey("initial_start_time") || stateMap["initial_start_time"] == null)
                    {
                        stateMap["initial_start_time"] = _initialStartTimeIso;
                    }
                    stateMap["timestamp"] = DateTime.Now.ToString("o");
                    stateMap["computer_name"] = curComp;
                    stateMap["restart_pending"] = _restartPending && _restartDueAtUtc > DateTime.UtcNow;
                    stateMap["restart_due_at"] = _restartDueAtUtc > DateTime.MinValue ? _restartDueAtUtc.ToLocalTime().ToString("o") : "";
                    stateMap["delayed_restart_minutes"] = _delayedRestartMinutesTotal;
                    if (!string.IsNullOrEmpty(existingHubUrl))
                    {
                        stateMap["hub_url"] = existingHubUrl;
                    }
                    if (existingApps.Count > 0)
                    {
                        stateMap["apps"] = existingApps;
                    }
                    string json = JsonHelper.Serialize(stateMap);
                    File.WriteAllText(statePath, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("WARN", "TaskRunner", "Upozorenje pri upisu deploy-state.json: " + ex.Message);
                }));
            }
        }

        private string FindScreenCaptureScriptPath()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates = new string[]
                {
                    @"C:\setup\Skripte\Modules\WIS.ScreenCapture.ps1",
                    Path.Combine(exeDir, @"..\Modules\WIS.ScreenCapture.ps1"),
                    Path.Combine(exeDir, @"Modules\WIS.ScreenCapture.ps1")
                };

                for (int i = 0; i < candidates.Length; i++)
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(candidates[i]);
                        if (File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void StartScreenCaptureWorkerIfNeeded()
        {
            if (!_isMasterMode || _criticalStressFailure)
            {
                return;
            }

            if (File.Exists(@"C:\setup\.stage_completed"))
            {
                return;
            }

            if (ViewModel != null && (ViewModel.IsServiceFinished || ViewModel.CurrentPhase >= 6))
            {
                return;
            }

            lock (_screenCaptureLock)
            {
                try
                {
                    if (_screenCaptureProcess != null)
                    {
                        try
                        {
                            if (!_screenCaptureProcess.HasExited)
                            {
                                return;
                            }
                        }
                        catch { }

                        try { _screenCaptureProcess.Dispose(); } catch { }
                        _screenCaptureProcess = null;
                    }

                    string scriptPath = FindScreenCaptureScriptPath();
                    if (string.IsNullOrEmpty(scriptPath))
                    {
                        return;
                    }

                    string stopFlagPath = @"C:\setup\.screen_capture.stop";
                    if (File.Exists(stopFlagPath))
                    {
                        try { File.Delete(stopFlagPath); } catch { }
                    }

                    string hubUrl = GetServisHubBaseUrl();
                    if (string.IsNullOrEmpty(hubUrl))
                    {
                        hubUrl = "http://192.168.170.254:8080";
                    }

                    string deviceId = Environment.MachineName;
                    int parentPid = Process.GetCurrentProcess().Id;

                    string args = string.Format(
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\" -HubUrl \"{1}\" -DeviceId \"{2}\" -ParentPid {3} -RunAsJob",
                        scriptPath, hubUrl, deviceId, parentPid);

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    _screenCaptureProcess = Process.Start(psi);
                }
                catch { }
            }
        }

        private void StopScreenCaptureWorker()
        {
            lock (_screenCaptureLock)
            {
                try
                {
                    if (Directory.Exists(@"C:\setup"))
                    {
                        try
                        {
                            File.WriteAllText(@"C:\setup\.screen_capture.stop", DateTime.Now.ToString("o"), Encoding.UTF8);
                        }
                        catch { }
                    }

                    if (_screenCaptureProcess != null)
                    {
                        try
                        {
                            if (!_screenCaptureProcess.HasExited)
                            {
                                _screenCaptureProcess.WaitForExit(1500);
                            }
                        }
                        catch { }

                        try
                        {
                            if (!_screenCaptureProcess.HasExited)
                            {
                                _screenCaptureProcess.Kill();
                            }
                        }
                        catch { }

                        try { _screenCaptureProcess.Dispose(); } catch { }
                        _screenCaptureProcess = null;
                    }
                }
                catch { }
            }
        }

        #endregion

        #region IPC (Standard Input / Output)

        private void StartIpcListener()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Console.InputEncoding = Encoding.UTF8;
                    Console.OutputEncoding = Encoding.UTF8;

                    string line;
                    while ((line = Console.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string json = line.Trim();
                        Dispatcher.BeginInvoke(new Action(() => ProcessIncomingJson(json)));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_isMasterMode)
                        {
                            ViewModel.AddLog("ERROR", "IPC", string.Format("Prekid IPC komunikacije: {0}", ex.Message));
                            ViewModel.IpcConnected = false;
                            ViewModel.IpcStatusText = LocalizationService.Get("WinIpcDisconnected", "IPC Diskonektovan");
                        }
                    }));
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(HandleStdinClosed));
                }
            });
        }

        private void HandleStdinClosed()
        {
            if (!_isMasterMode)
            {
                ViewModel.IpcConnected = false;
                ViewModel.IpcStatusText = LocalizationService.Get("WinIpcEof", "IPC Završen (EOF)");
                ViewModel.AddLog("WARN", "IPC", "Standardni ulaz je zatvoren (roditeljski proces je prekinut).");
            }
        }

        public void SendIpcMessage(object payload)
        {
            try
            {
                string json = JsonHelper.Serialize(payload);
                lock (_stdoutLock)
                {
                    if (_currentPhaseProcess != null && !_currentPhaseProcess.HasExited)
                    {
                        try
                        {
                            _currentPhaseProcess.StandardInput.WriteLine(json);
                            _currentPhaseProcess.StandardInput.Flush();
                        }
                        catch { }
                    }

                    Console.Out.WriteLine(json);
                    Console.Out.Flush();
                }
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "IPC", string.Format("Greška pri slanju komande na izlaz: {0}", ex.Message));
            }
        }

        #endregion

        #region JSON Parser & ViewModel Mapping

        private void ProcessIncomingJson(string json, bool allowPhaseUpdate = true)
        {
            try
            {
                JsonNode root = JsonHelper.Parse(json);
                if (root == null || root.IsNull) return;

                // 1. Provera diskretnih događaja (event ili type)
                string eventType = root["event"].AsString(root["type"].AsString(""));

                if (string.IsNullOrEmpty(eventType))
                {
                    string depSt = root["deployment_status"].AsString(root["status"].AsString(""));
                    if (string.Equals(depSt, "NEEDS_ATTENTION", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(depSt, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase))
                    {
                        eventType = depSt;
                    }
                }

                if (!string.IsNullOrEmpty(eventType))
                {
                    HandleSpecificEvent(eventType, root);
                }

                // 2. Ažuriranje Faza i Progresa
                if (root.ContainsKey("initial_start_time"))
                {
                    string incomingStart = root["initial_start_time"].AsString(null);
                    DateTime parsedStart;
                    if (!string.IsNullOrEmpty(incomingStart) && DateTime.TryParse(incomingStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsedStart))
                    {
                        _initialStartTimeIso = incomingStart;
                        TimeSpan elapsed = DateTime.Now - parsedStart.ToLocalTime();
                        if (elapsed.TotalSeconds > 0 && elapsed.TotalDays < 30)
                        {
                            ViewModel.SetInitialElapsedDuration(elapsed);
                        }
                    }
                }
                if (allowPhaseUpdate)
                {
                    if (root.ContainsKey("phase"))
                    {
                        int p = root["phase"].AsInt(1);
                        if (!(ViewModel != null && ViewModel.CurrentPhase >= 4 && p < ViewModel.CurrentPhase))
                        {
                            ViewModel.CurrentPhase = p;
                        }
                    }
                    if (root.ContainsKey("phase_name"))
                    {
                        if (!(root.ContainsKey("phase") && ViewModel != null && ViewModel.CurrentPhase >= 4 && root["phase"].AsInt(1) < ViewModel.CurrentPhase))
                        {
                            ViewModel.PhaseName = root["phase_name"].AsString(ViewModel.PhaseName);
                        }
                    }
                }
                if (root.ContainsKey("sub_phase"))
                {
                    ViewModel.SubPhase = root["sub_phase"].AsString(ViewModel.SubPhase);
                }
                if (root.ContainsKey("live_status"))
                {
                    ViewModel.LiveStatus = root["live_status"].AsString(ViewModel.LiveStatus);
                }
                else if (root.ContainsKey("status_message"))
                {
                    ViewModel.LiveStatus = root["status_message"].AsString(ViewModel.LiveStatus);
                }

                if (allowPhaseUpdate)
                {
                    if (root.ContainsKey("progress_percent"))
                    {
                        ViewModel.Progress = root["progress_percent"].AsInt(ViewModel.Progress);
                    }
                    else if (root.ContainsKey("worker_progress"))
                    {
                        ViewModel.Progress = root["worker_progress"].AsInt(ViewModel.Progress);
                    }
                    else if (root.ContainsKey("progress"))
                    {
                        ViewModel.Progress = root["progress"].AsInt(ViewModel.Progress);
                    }
                }

                // 3. Provera statusa USB fleš diska (usb_free, usbFree, UsbFree)
                if (root.ContainsKey("usb_free"))
                {
                    ViewModel.UsbFree = root["usb_free"].AsBool(ViewModel.UsbFree);
                }
                else if (root.ContainsKey("usbFree"))
                {
                    ViewModel.UsbFree = root["usbFree"].AsBool(ViewModel.UsbFree);
                }
                else if (root.ContainsKey("UsbFree"))
                {
                    ViewModel.UsbFree = root["UsbFree"].AsBool(ViewModel.UsbFree);
                }

                // 4. Hardver (hw)
                bool modelFound = false;
                var hwNode = root["hw"];
                if (hwNode.IsObject)
                {
                    string mVal = hwNode.ContainsKey("model") ? hwNode["model"].AsString() : (hwNode.ContainsKey("Model") ? hwNode["Model"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(mVal))
                    {
                        if (HardwareIdentityService.TryUpdate("ComputerModel", mVal, HardwareUpdateSource.Ipc, ViewModel))
                        {
                            modelFound = true;
                        }
                    }

                    string sVal = hwNode.ContainsKey("serial") ? hwNode["serial"].AsString(null) : (hwNode.ContainsKey("SerialNumber") ? hwNode["SerialNumber"].AsString(null) : null);
                    if (!string.IsNullOrWhiteSpace(sVal))
                    {
                        HardwareIdentityService.TryUpdate("SerialNumber", sVal, HardwareUpdateSource.Ipc, ViewModel);
                    }

                    if (hwNode.ContainsKey("cpu")) HardwareIdentityService.TryUpdate("CpuInfo", hwNode["cpu"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                    if (hwNode.ContainsKey("CpuStr")) HardwareIdentityService.TryUpdate("CpuInfo", hwNode["CpuStr"].AsString(), HardwareUpdateSource.Ipc, ViewModel);

                    if (hwNode.ContainsKey("ram")) HardwareIdentityService.TryUpdate("RamInfo", hwNode["ram"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                    if (hwNode.ContainsKey("RamStr")) HardwareIdentityService.TryUpdate("RamInfo", hwNode["RamStr"].AsString(), HardwareUpdateSource.Ipc, ViewModel);

                    if (hwNode.ContainsKey("disk")) HardwareIdentityService.TryUpdate("DiskInfo", hwNode["disk"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                    if (hwNode.ContainsKey("DiskStr")) HardwareIdentityService.TryUpdate("DiskInfo", hwNode["DiskStr"].AsString(), HardwareUpdateSource.Ipc, ViewModel);

                    if (hwNode.ContainsKey("battery")) HardwareIdentityService.TryUpdate("BatteryStatus", hwNode["battery"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                    if (hwNode.ContainsKey("BatteryStr")) HardwareIdentityService.TryUpdate("BatteryStatus", hwNode["BatteryStr"].AsString(), HardwareUpdateSource.Ipc, ViewModel);

                    // GPU & Displej
                    string gVal = hwNode.ContainsKey("gpu") ? hwNode["gpu"].AsString() : (hwNode.ContainsKey("gpu_name") ? hwNode["gpu_name"].AsString() : (hwNode.ContainsKey("GpuName") ? hwNode["GpuName"].AsString() : null));
                    if (!string.IsNullOrWhiteSpace(gVal)) HardwareIdentityService.TryUpdate("GpuName", gVal, HardwareUpdateSource.Ipc, ViewModel);

                    string gVram = hwNode.ContainsKey("gpu_vram") ? hwNode["gpu_vram"].AsString() : (hwNode.ContainsKey("GpuVram") ? hwNode["GpuVram"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(gVram)) HardwareIdentityService.TryUpdate("GpuVram", gVram, HardwareUpdateSource.Ipc, ViewModel);

                    string gDrv = hwNode.ContainsKey("gpu_driver") ? hwNode["gpu_driver"].AsString() : (hwNode.ContainsKey("GpuDriverVersion") ? hwNode["GpuDriverVersion"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(gDrv)) HardwareIdentityService.TryUpdate("GpuDriverVersion", gDrv, HardwareUpdateSource.Ipc, ViewModel);

                    string dRes = hwNode.ContainsKey("display_resolution") ? hwNode["display_resolution"].AsString() : (hwNode.ContainsKey("DisplayResolution") ? hwNode["DisplayResolution"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(dRes)) HardwareIdentityService.TryUpdate("DisplayResolution", dRes, HardwareUpdateSource.Ipc, ViewModel);

                    string dHz = hwNode.ContainsKey("display_refresh_rate") ? hwNode["display_refresh_rate"].AsString() : (hwNode.ContainsKey("DisplayRefreshRate") ? hwNode["DisplayRefreshRate"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(dHz)) HardwareIdentityService.TryUpdate("DisplayRefreshRate", dHz, HardwareUpdateSource.Ipc, ViewModel);

                    string dDpi = hwNode.ContainsKey("display_dpi_scale") ? hwNode["display_dpi_scale"].AsString() : (hwNode.ContainsKey("DisplayDpiScale") ? hwNode["DisplayDpiScale"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(dDpi)) HardwareIdentityService.TryUpdate("DisplayDpiScale", dDpi, HardwareUpdateSource.Ipc, ViewModel);

                    // Matična ploča, BIOS & Autopilot Hardverski otisak
                    string mb = hwNode.ContainsKey("motherboard") ? hwNode["motherboard"].AsString() : (hwNode.ContainsKey("MotherboardModel") ? hwNode["MotherboardModel"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(mb)) HardwareIdentityService.TryUpdate("MotherboardModel", mb, HardwareUpdateSource.Ipc, ViewModel);

                    string bv = hwNode.ContainsKey("bios_version") ? hwNode["bios_version"].AsString() : (hwNode.ContainsKey("BiosVersion") ? hwNode["BiosVersion"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(bv)) HardwareIdentityService.TryUpdate("BiosVersion", bv, HardwareUpdateSource.Ipc, ViewModel);

                    string bd = hwNode.ContainsKey("bios_date") ? hwNode["bios_date"].AsString() : (hwNode.ContainsKey("BiosReleaseDate") ? hwNode["BiosReleaseDate"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(bd)) HardwareIdentityService.TryUpdate("BiosReleaseDate", bd, HardwareUpdateSource.Ipc, ViewModel);

                    string sb = hwNode.ContainsKey("secure_boot") ? hwNode["secure_boot"].AsString() : (hwNode.ContainsKey("SecureBootStatus") ? hwNode["SecureBootStatus"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(sb)) HardwareIdentityService.TryUpdate("SecureBootStatus", sb, HardwareUpdateSource.Ipc, ViewModel);

                    string mfg = hwNode.ContainsKey("manufacturer") ? hwNode["manufacturer"].AsString() : (hwNode.ContainsKey("Manufacturer") ? hwNode["Manufacturer"].AsString() : null);
                    if (!string.IsNullOrWhiteSpace(mfg)) HardwareIdentityService.TryUpdate("Autopilot.HwManufacturer", mfg, HardwareUpdateSource.Ipc, ViewModel);

                    if (hwNode.ContainsKey("uuid")) HardwareIdentityService.TryUpdate("Autopilot.HwSmbiosUuid", hwNode["uuid"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                    else if (hwNode.ContainsKey("SmbiosUuid")) HardwareIdentityService.TryUpdate("Autopilot.HwSmbiosUuid", hwNode["SmbiosUuid"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                }

                if (!modelFound && root.ContainsKey("model_info") && !string.IsNullOrWhiteSpace(root["model_info"].AsString()))
                {
                    HardwareIdentityService.TryUpdate("ComputerModel", root["model_info"].AsString(), HardwareUpdateSource.Ipc, ViewModel);
                }

                // 5. Dijagnostika i zdravlje (diag ili health ili hardware_health)
                var diagNode = root["diag"];
                if (!diagNode.IsObject) diagNode = root["health"];
                if (!diagNode.IsObject) diagNode = root["hardware_health"];

                if (diagNode.IsObject)
                {
                    if (diagNode.ContainsKey("wear")) ViewModel.DiskWear = diagNode["wear"].AsInt(ViewModel.DiskWear);
                    else if (diagNode.ContainsKey("DiskHealth")) ViewModel.DiskWear = diagNode["DiskHealth"].AsInt(ViewModel.DiskWear);
                    else if (diagNode.ContainsKey("DiskHealthPercent")) ViewModel.DiskWear = diagNode["DiskHealthPercent"].AsInt(ViewModel.DiskWear);

                    if (diagNode.ContainsKey("tbw")) ViewModel.DiskTbw = string.Format("{0} TB", diagNode["tbw"].AsString());
                    else if (diagNode.ContainsKey("TbwTB")) ViewModel.DiskTbw = string.Format("{0} TB", diagNode["TbwTB"].AsString());

                    long parsedHours = -1;
                    if (diagNode.ContainsKey("power_on_hours")) parsedHours = diagNode["power_on_hours"].AsLong(-1);
                    else if (diagNode.ContainsKey("PowerOnHours")) parsedHours = diagNode["PowerOnHours"].AsLong(-1);

                    if (parsedHours >= 0)
                    {
                        ViewModel.DiskPowerOnHours = parsedHours;
                        if (ViewModel.HardwareInfo != null)
                        {
                            ViewModel.HardwareInfo.StorageHours = string.Format("{0:N0} h", parsedHours);
                        }
                    }

                    if (diagNode.ContainsKey("smart_status")) ViewModel.SmartStatus = diagNode["smart_status"].AsString(ViewModel.SmartStatus);
                    else if (diagNode.ContainsKey("OverallStatus")) ViewModel.SmartStatus = diagNode["OverallStatus"].AsString(ViewModel.SmartStatus);

                    if (diagNode.ContainsKey("reallocated_sectors")) ViewModel.ReallocatedSectors = diagNode["reallocated_sectors"].AsInt(ViewModel.ReallocatedSectors);
                    else if (diagNode.ContainsKey("ReallocatedSectors")) ViewModel.ReallocatedSectors = diagNode["ReallocatedSectors"].AsInt(ViewModel.ReallocatedSectors);

                    if (diagNode.ContainsKey("critical_warning")) ViewModel.CriticalWarnings = diagNode["critical_warning"].AsInt(ViewModel.CriticalWarnings);
                    else if (diagNode.ContainsKey("CriticalWarning")) ViewModel.CriticalWarnings = diagNode["CriticalWarning"].AsInt(ViewModel.CriticalWarnings);

                    if (diagNode.ContainsKey("battery_health")) ViewModel.BatteryHealth = diagNode["battery_health"].AsInt(ViewModel.BatteryHealth);
                    else if (diagNode.ContainsKey("BatteryHealth")) ViewModel.BatteryHealth = diagNode["BatteryHealth"].AsInt(ViewModel.BatteryHealth);
                    else if (diagNode.ContainsKey("BatteryHealthPercent")) ViewModel.BatteryHealth = diagNode["BatteryHealthPercent"].AsInt(ViewModel.BatteryHealth);

                    if (diagNode.ContainsKey("battery_cycles")) ViewModel.BatteryCycles = diagNode["battery_cycles"].AsString(ViewModel.BatteryCycles);
                    if (diagNode.ContainsKey("battery_status")) ViewModel.BatteryStatus = diagNode["battery_status"].AsString(ViewModel.BatteryStatus);
                    if (diagNode.ContainsKey("is_laptop")) ViewModel.IsLaptop = diagNode["is_laptop"].AsBool(ViewModel.IsLaptop);
                    else if (diagNode.ContainsKey("IsLaptop")) ViewModel.IsLaptop = diagNode["IsLaptop"].AsBool(ViewModel.IsLaptop);

                    if (diagNode.ContainsKey("cpu_usage")) ViewModel.CpuUsage = diagNode["cpu_usage"].AsString(ViewModel.CpuUsage);
                    if (diagNode.ContainsKey("ram_usage")) ViewModel.RamUsage = diagNode["ram_usage"].AsString(ViewModel.RamUsage);

                    // TPM Modul iz dijagnostike
                    if (diagNode.ContainsKey("tpm_present")) ViewModel.Autopilot.TpmPresent = diagNode["tpm_present"].AsBool(ViewModel.Autopilot.TpmPresent);
                    else if (diagNode.ContainsKey("TpmPresent")) ViewModel.Autopilot.TpmPresent = diagNode["TpmPresent"].AsBool(ViewModel.Autopilot.TpmPresent);

                    if (diagNode.ContainsKey("tpm_ready")) ViewModel.Autopilot.TpmReady = diagNode["tpm_ready"].AsBool(ViewModel.Autopilot.TpmReady);
                    else if (diagNode.ContainsKey("TpmReady")) ViewModel.Autopilot.TpmReady = diagNode["TpmReady"].AsBool(ViewModel.Autopilot.TpmReady);

                    ViewModel.TpmStatusSummary = ViewModel.Autopilot.TpmStatusText;

                    // PnP Problematični uređaji
                    var pnpList = diagNode["pnp_problem_devices"];
                    if (!pnpList.IsArray) pnpList = diagNode["PnpProblemDevices"];
                    if (pnpList.IsArray)
                    {
                        var devList = new List<PnpProblemDevice>();
                        foreach (var item in pnpList.Elements)
                        {
                            var dev = new PnpProblemDevice();
                            if (item.IsObject)
                            {
                                dev.Name = item["name"].AsString(item["Name"].AsString(item["description"].AsString(LocalizationService.Get("MdlUnknownDevice", "Nepoznat uređaj"))));
                                dev.DeviceId = item["device_id"].AsString(item["DeviceId"].AsString(""));
                                dev.ErrorCode = item["error_code"].AsInt(item["ErrorCode"].AsInt(item["ConfigManagerErrorCode"].AsInt(0)));
                                dev.ErrorStatus = item["error_status"].AsString(item["ErrorStatus"].AsString(string.Format(LocalizationService.Get("WinErrorCodeFmt", "Kôd greške {0}"), dev.ErrorCode)));
                            }
                            else
                            {
                                dev.Name = item.AsString();
                            }
                            devList.Add(dev);
                        }
                        ViewModel.SetPnpProblemDevices(devList);
                    }
                }

                // 6. Mreža (network ili net)
                var netNode = root["network"];
                if (!netNode.IsObject) netNode = root["net"];
                if (netNode.IsObject)
                {
                    if (netNode.ContainsKey("wifi_ssid")) ViewModel.WifiSsid = netNode["wifi_ssid"].AsString(ViewModel.WifiSsid);
                    else if (netNode.ContainsKey("ssid")) ViewModel.WifiSsid = netNode["ssid"].AsString(ViewModel.WifiSsid);

                    if (netNode.ContainsKey("ip")) ViewModel.IpAddress = netNode["ip"].AsString(ViewModel.IpAddress);
                    else if (netNode.ContainsKey("ip_address")) ViewModel.IpAddress = netNode["ip_address"].AsString(ViewModel.IpAddress);

                    if (netNode.ContainsKey("nas_latency")) ViewModel.NasLatency = netNode["nas_latency"].AsString(ViewModel.NasLatency);
                    else if (netNode.ContainsKey("nas_latency_ms")) ViewModel.NasLatency = string.Format("{0} ms", netNode["nas_latency_ms"].AsString());

                    if (netNode.ContainsKey("nas_status")) ViewModel.NasStatus = netNode["nas_status"].AsString(ViewModel.NasStatus);
                    if (netNode.ContainsKey("connection_type"))
                    {
                        ViewModel.ConnectionType = netNode["connection_type"].AsString(ViewModel.ConnectionType);
                        string ctLower = (ViewModel.ConnectionType ?? "").ToLowerInvariant();
                        if (ctLower.Contains("wi-fi") || ctLower.Contains("wifi") || ctLower.Contains("wireless"))
                        {
                            ViewModel.ConnectionStatusText = LocalizationService.Get("WinNetWifiActive", "✓ Wi-Fi konekcija aktivna");
                            ViewModel.ConnectionStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                        }
                        else if (ctLower.Contains("ethernet") || ctLower.Contains("lan"))
                        {
                            ViewModel.ConnectionStatusText = LocalizationService.Get("WinNetLanActive", "✓ Ethernet (LAN) aktivan");
                            ViewModel.ConnectionStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                        }
                    }

                    string currIp = (ViewModel.IpAddress ?? "").Trim();
                    bool hasValidIp = !string.IsNullOrEmpty(currIp) &&
                                      !string.Equals(currIp, "N/A", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(currIp, "Nepoznato", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(currIp, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
                                      !currIp.StartsWith("127.", StringComparison.OrdinalIgnoreCase) &&
                                      !currIp.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase);
                    if (hasValidIp || netNode["internet_active"].AsBool(false))
                    {
                        TryRetryAutopilotOnNetworkReady();
                    }
                }

                // 7. Aplikacije (apps)
                var appsNode = root["apps"];
                if (appsNode.IsArray)
                {
                    foreach (var appItemNode in appsNode.Elements)
                    {
                        string id = appItemNode["id"].AsString(appItemNode["name"].AsString(appItemNode["app"].AsString("")));
                        string st = appItemNode["status"].AsString("WAIT");
                        string det = appItemNode["detail"].AsString(null);
                        ViewModel.UpdateAppStatus(id, st, det);
                    }
                }
                else if (appsNode.IsObject)
                {
                    foreach (var key in appsNode.Keys)
                    {
                        var valNode = appsNode[key];
                        if (valNode.IsObject)
                        {
                            string st = valNode["status"].AsString("WAIT");
                            string det = valNode["detail"].AsString(null);
                            ViewModel.UpdateAppStatus(key, st, det);
                        }
                        else
                        {
                            string st = valNode.AsString("WAIT");
                            ViewModel.UpdateAppStatus(key, st);
                        }
                    }
                }

                // 8. Log linija ili bafer
                var logNode = root["log"];
                if (logNode.IsObject)
                {
                    string t = logNode["time"].AsString(DateTime.Now.ToString("HH:mm:ss"));
                    string lvl = logNode["level"].AsString("INFO");
                    string tag = logNode["tag"].AsString("[WIS]");
                    string msg = logNode["message"].AsString("");
                    if (!string.IsNullOrWhiteSpace(msg) && msg != _lastProcessedLog)
                    {
                        _lastProcessedLog = msg;
                        ViewModel.AddLog(lvl, tag, msg);
                    }
                }
                else if (root.ContainsKey("last_log") && !string.IsNullOrWhiteSpace(root["last_log"].AsString()))
                {
                    var lastLogNode = root["last_log"];
                    if (lastLogNode.IsObject)
                    {
                        string msg = lastLogNode["message"].AsString("");
                        if (!string.IsNullOrWhiteSpace(msg) && msg != _lastProcessedLog)
                        {
                            _lastProcessedLog = msg;
                            string lvl = lastLogNode["level"].AsString("INFO");
                            string tag = lastLogNode["tag"].AsString("[WIS]");
                            ViewModel.AddLog(lvl, tag, msg);
                        }
                    }
                    else
                    {
                        string rawLog = lastLogNode.AsString("");
                        if (!string.IsNullOrWhiteSpace(rawLog) && rawLog != _lastProcessedLog)
                        {
                            _lastProcessedLog = rawLog;
                            ParseAndAddRawLogLine(rawLog);
                        }
                    }
                }
                else if (root.ContainsKey("lines") && root["lines"].IsArray)
                {
                    foreach (var lNode in root["lines"].Elements)
                    {
                        string raw = lNode.AsString("");
                        ParseAndAddRawLogLine(raw);
                    }
                }
                else if (root.ContainsKey("message"))
                {
                    string msg = root["message"].AsString("");
                    string lvl = root["level"].AsString("INFO");
                    string tag = root["component"].AsString("[WIS]");
                    ViewModel.AddLog(lvl, tag, msg);
                }

                // 9. Autopilot podaci preko IPC
                var apNode = root["autopilot"];
                if (apNode.IsObject)
                {
                    UpdateAutopilotFromData(apNode);
                }

                // 10. Status pozadinskog Windows Update-a preko IPC (WIS-TASK-175 / V344-01)
                if (root.ContainsKey("bg_wu") && root["bg_wu"].IsObject)
                {
                    var bgNode = root["bg_wu"];
                    string bgSt = bgNode.ContainsKey("status") ? bgNode["status"].AsString("RUNNING") : "RUNNING";
                    string bgTxtSr = bgNode.ContainsKey("phase_text") ? bgNode["phase_text"].AsString("") : "";
                    string bgTxtZh = bgNode.ContainsKey("phase_text_zh") ? bgNode["phase_text_zh"].AsString("") : "";
                    if (bgNode.ContainsKey("pending_driver_count"))
                    {
                        _bgWuPendingDriverCount = Math.Max(0, bgNode["pending_driver_count"].AsInt(0));
                    }
                    if (bgNode.ContainsKey("installing_driver"))
                    {
                        _bgWuInstallingDriver = bgNode["installing_driver"].AsBool(false);
                    }
                    string bgNorm = (bgSt ?? "RUNNING").Trim().ToUpperInvariant();
                    if (bgNorm == "COMPLETED" || bgNorm == "SKIPPED")
                    {
                        _bgWuPendingDriverCount = 0;
                        _bgWuInstallingDriver = false;
                    }
                    ViewModel.UpdateBackgroundWuStatus(bgSt, bgTxtSr, bgTxtZh);
                    NotifyHwTestDriverStateChanged(bgNorm == "COMPLETED" || bgNorm == "SKIPPED" || (_bgWuPendingDriverCount == 0 && !_bgWuInstallingDriver));
                }
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("DEBUG", "JSON", string.Format("Greška parsiranja: {0}", ex.Message));
            }
        }

        private void HandleStepEvent(JsonNode node)
        {
            if (node == null) return;

            int phaseNum = node["phase"].AsInt(ViewModel != null ? ViewModel.CurrentPhase : 1);
            string stepId = node["step_id"].AsString(node["stepId"].AsString(node["id"].AsString("")));
            string title = node["title"].AsString(node["name"].AsString(stepId));
            string state = node["state"].AsString(node["status"].AsString("WAIT"));
            string reason = node["reason"].AsString("");

            if (string.IsNullOrEmpty(stepId)) return;

            Dispatcher.BeginInvoke((Action)delegate
            {
                if (ViewModel == null || ViewModel.Phases == null) return;

                PhaseItem targetPhase = null;
                for (int i = 0; i < ViewModel.Phases.Count; i++)
                {
                    if (ViewModel.Phases[i].Number == phaseNum)
                    {
                        targetPhase = ViewModel.Phases[i];
                        break;
                    }
                }

                if (targetPhase != null)
                {
                    StepItem stepItem = null;
                    for (int i = 0; i < targetPhase.Steps.Count; i++)
                    {
                        if (string.Equals(targetPhase.Steps[i].Id, stepId, StringComparison.OrdinalIgnoreCase))
                        {
                            stepItem = targetPhase.Steps[i];
                            break;
                        }
                    }

                    if (stepItem == null)
                    {
                        stepItem = new StepItem
                        {
                            Id = stepId,
                            Title = !string.IsNullOrEmpty(title) ? title : stepId
                        };
                        targetPhase.Steps.Add(stepItem);
                    }

                    if (!string.IsNullOrEmpty(title))
                    {
                        stepItem.Title = title;
                    }
                    stepItem.Status = state;
                    stepItem.Reason = reason;

                    if (targetPhase.IsActive)
                    {
                        targetPhase.IsExpanded = true;
                    }

                    if (targetPhase.Steps.Count > 0)
                    {
                        bool allSkippedDone = true;
                        for (int i = 0; i < targetPhase.Steps.Count; i++)
                        {
                            if (targetPhase.Steps[i].Status != "SKIPPED_DONE")
                            {
                                allSkippedDone = false;
                                break;
                            }
                        }
                        if (allSkippedDone)
                        {
                            targetPhase.Status = "Skipped";
                        }
                    }
                }

                if (stepId.StartsWith("P2.", StringComparison.OrdinalIgnoreCase) && string.Equals(state, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    if (_deviceMonitor != null)
                    {
                        _deviceMonitor.TriggerDebouncedRefresh(HwCategory.All);
                    }
                }
                if (stepId.StartsWith("P1.", StringComparison.OrdinalIgnoreCase) && string.Equals(state, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    TryRetryAutopilotOnNetworkReady();
                }

                if (ViewModel != null)
                {
                    ViewModel.RefreshProgressBars();
                }
            });
        }

        private void HandleSpecificEvent(string eventType, JsonNode node)
        {
            switch (eventType.ToUpperInvariant())
            {
                case "STEP":
                    HandleStepEvent(node);
                    break;
                case "NEEDS_ATTENTION":
                case "THERMAL_CRITICAL":
                    EnsureStressTestResultFile();

                    float maxT = _lastStressMaxTemp;
                    if (node != null)
                    {
                        if (node.ContainsKey("max_cpu_temp_c"))
                        {
                            float parsedT;
                            if (float.TryParse(node["max_cpu_temp_c"].AsString("0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedT))
                            {
                                if (parsedT > 0f) maxT = parsedT;
                            }
                        }
                        else if (node.ContainsKey("max_temp"))
                        {
                            float parsedT;
                            if (float.TryParse(node["max_temp"].AsString("0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedT))
                            {
                                if (parsedT > 0f) maxT = parsedT;
                            }
                        }
                    }

                    string stNode = "";
                    if (node != null)
                    {
                        if (node.ContainsKey("stress_status")) stNode = node["stress_status"].AsString("");
                        else if (node.ContainsKey("status")) stNode = node["status"].AsString("");
                    }
                    string reasonNode = "";
                    if (node != null && node.ContainsKey("reason"))
                    {
                        reasonNode = node["reason"].AsString("");
                    }

                    bool isSensorUnavailable = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && maxT < 95.0f && !string.Equals(stNode, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) && (string.Equals(stNode, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(reasonNode, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                               maxT <= 0.5f);

                    if (isSensorUnavailable)
                    {
                        _criticalStressFailure = false;
                        _lastStressEmergencyAbort = false;
                        _lastStressStatus = "UNKNOWN";
                        _lastStressReason = "SENSOR_UNAVAILABLE";

                        if (ViewModel != null)
                        {
                            ViewModel.StressResultStatus = "UNKNOWN";
                            ViewModel.CriticalStressFailure = false;
                            ViewModel.EmergencyAbortTriggered = false;
                            ViewModel.AddLog("WARN", "Diagnostics", "Termalni senzori nisu dostupni (status UNKNOWN). Nastavljam normalan tok instalacije bez blokade...");
                        }
                    }
                    else
                    {
                        bool isActuallyCritical = HasCriticalThermalEvidence() || (maxT >= 95.0f) ||
                                                  (_lastStressEmergencyAbort && maxT > 0.5f) ||
                                                  (string.Equals(_lastStressStatus, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) && maxT >= 95.0f) ||
                                                  (string.Equals(stNode, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) && maxT >= 95.0f);

                        if (isActuallyCritical)
                        {
                            RecordThermalOverheatNote();
                        }
                    }
                    break;

                case "COMPLETED":
                case "COMPLETE":
                case "FINISHED":
                    if (HasUnfinishedInteractiveHwTests())
                    {
                        if (ViewModel != null)
                        {
                            ViewModel.IsCompleted = false;
                            ViewModel.IsServiceFinished = false;
                            ViewModel.CurrentPhase = 5;
                            ViewModel.AddLog("WARN", "Hardver", "HARD GATE: Blokirana IPC poruka COMPLETED dok se ne završe obavezni interaktivni testovi.");
                        }
                        ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                        break;
                    }
                    HidePendingInteractiveTestsWarning();
                    RefreshPeripheralsAfterDrivers();
                    EnsureStressTestResultFile();

                    bool isSensorUnavailCompleted = !HasCriticalThermalEvidence() && !HasIncompleteStressResult() && (string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(_lastStressReason, "SENSOR_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                                   _lastStressMaxTemp <= 0.5f);

                    if (isSensorUnavailCompleted)
                    {
                        _criticalStressFailure = false;
                        _lastStressEmergencyAbort = false;
                        _lastStressStatus = "UNKNOWN";
                    }

                    bool isActuallyCritCompleted = HasCriticalThermalEvidence();

                    if (isActuallyCritCompleted)
                    {
                        RecordThermalOverheatNote();
                    }
                    ViewModel.DeploymentStatus = "READY";
                    ViewModel.IsCompleted = true;
                    ViewModel.IsServiceFinished = true;
                    ViewModel.CriticalStressFailure = false;
                    if (isActuallyCritCompleted)
                    {
                        float noteTemp = _lastStressMaxTemp > 0.5f ? _lastStressMaxTemp : 95.0f;
                        string noteTxt = !string.IsNullOrEmpty(_thermalOverheatNote)
                            ? _thermalOverheatNote
                            : string.Format(LocalizationService.Get("WinThermalOverheatFallback", "NAPOMENA: Procesor se pregrevao tokom stres testa ({0:F1}°C >= 95°C) — potreban servis hlađenja / zamena termalne paste."), noteTemp);
                        ViewModel.StressResultStatus = "THERMAL_CRITICAL";
                        ViewModel.CompletionTitle = LocalizationService.Get("WinCompletionTitleOverheat", "⚠️ INSTALACIJA ZAVRŠENA (NAPOMENA: PREGREVANJE CPU)");
                        ViewModel.CompletionSummary = "⚠️ " + noteTxt;
                        ViewModel.LiveStatus = LocalizationService.Get("WinCompletionLiveThermalNote", "⚠️ Instalacija završena uz termalnu napomenu (sačuvano u izveštaju).");
                        ViewModel.ThermalVerdictTitle = string.Format(LocalizationService.Get("WinThermalOverheatTitle", "⚠️ NAPOMENA: PREGREVANJE CPU ({0:F1}°C >= 95°C)"), noteTemp);
                        ViewModel.ThermalVerdictDescription = noteTxt;
                        ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(230, 126, 34);
                        ViewModel.ThermalRecommendation = LocalizationService.Get("WinThermalServiceNoteShort", "NAPOMENA ZA SERVIS: Očistiti hladnjak/ventilator i zameniti termalnu pastu.");
                    }
                    else if (isSensorUnavailCompleted)
                    {
                        ViewModel.StressResultStatus = "UNKNOWN";
                        ViewModel.CompletionSummary = LocalizationService.Get("WinCompletionSensorUnknown", "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.");
                        ViewModel.LiveStatus = LocalizationService.Get("WinCompletionSensorUnknown", "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.");
                        ViewModel.ThermalVerdictTitle = LocalizationService.Get("WinThermalSensorUnknownTitle", "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)");
                        ViewModel.ThermalVerdictDescription = LocalizationService.Get("WinThermalSensorUnknownDesc", "Hlađenje nije automatski potvrđeno; obavezna ručna provera. Potrebna je ručna provera hladnjaka i ventilatora.");
                        ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18);
                        ViewModel.ThermalRecommendation = LocalizationService.Get("WinThermalSensorUnknownRec", "Preporuka: Proverite rad kulera i ventilaciju u BIOS/UEFI ili fizički pre isporuke računara.");
                    }
                    else
                    {
                        if (node.ContainsKey("title")) ViewModel.CompletionTitle = node["title"].AsString(ViewModel.CompletionTitle);
                        if (node.ContainsKey("summary")) ViewModel.CompletionSummary = node["summary"].AsString(ViewModel.CompletionSummary);
                    }
                    ViewModel.ShutdownTimerActive = false;
                    ViewModel.AddLog("SUCCESS", "Sistem", "SERVIS ZAVRŠEN! Računar uspešno pripremljen.");

                    ThreadPool.QueueUserWorkItem(delegate(object _)
                    {
                        try
                        {
                            Console.Beep(523, 120);
                            Console.Beep(659, 120);
                            Console.Beep(784, 150);
                            Console.Beep(1046, 250);
                        }
                        catch
                        {
                            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                        }
                    });
                    EnsureLabelPrintButtons();
                    EnsureHwTestButtons();
                    break;

                case "USB_FREE":
                case "USBFREE":
                    ViewModel.UsbFree = node["value"].AsBool(true);
                    ViewModel.AddLog("SUCCESS", "USB", "USB Fleš disk je bezbedno odvojen.");
                    break;

                case "AUTOPILOT_UPDATE":
                case "AUTOPILOT":
                    UpdateAutopilotFromData(node);
                    break;

                case "APP_UPDATE":
                    string appId = node["id"].AsString(node["app"].AsString(node["name"].AsString(node["app_id"].AsString(""))));
                    string appStatus = node["status"].AsString("OK");
                    string appDetail = node["detail"].AsString(null);
                    ViewModel.UpdateAppStatus(appId, appStatus, appDetail);
                    break;
            }
        }

        private void ParseAndAddRawLogLine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            string time = DateTime.Now.ToString("HH:mm:ss");
            string level = "INFO";
            string tag = "[WIS]";
            string message = raw;

            try
            {
                if (raw.Length > 20 && raw[4] == '-' && raw[7] == '-')
                {
                    time = raw.Substring(11, 8);
                    string rem = raw.Substring(20).Trim();
                    if (rem.StartsWith("["))
                    {
                        int end = rem.IndexOf(']');
                        if (end > 1)
                        {
                            level = rem.Substring(1, end - 1);
                            rem = rem.Substring(end + 1).Trim();
                        }
                    }
                    if (rem.StartsWith("["))
                    {
                        int end = rem.IndexOf(']');
                        if (end > 1)
                        {
                            tag = rem.Substring(0, end + 1);
                            rem = rem.Substring(end + 1).Trim();
                        }
                    }
                    message = rem;
                }
            }
            catch { }

            ViewModel.AddLog(level, tag, message);
        }

        #endregion

        #region Korisničke Akcije (Buttons)

        private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.Source == MainTabControl)
            {
                if (_hwTestTabItem != null && object.ReferenceEquals(MainTabControl.SelectedItem, _hwTestTabItem))
                {
                    RefreshEmbeddedHwTestIdentity();
                }

                if (MainTabControl.SelectedIndex != 0)
                {
                    if (_autoStartTimer != null && _autoStartTimer.IsEnabled)
                    {
                        _autoStartTimer.Stop();
                        string pauseMsg = LocalizationService.Get("WinAutoStartPaused", "Automatski start pauziran. Omogućen ručni pregled.");
                        ViewModel.AutoStartMessage = pauseMsg;
                        ViewModel.LiveStatus = pauseMsg;
                        ViewModel.IsPaused = true;
                        ViewModel.AddLog("INFO", "Korisnik", "Promenjen tab - automatski start pauziran za ručni pregled.");
                    }
                }
            }
        }

        private void ExecuteStartService(object param)
        {
            if (_autoStartTimer != null && _autoStartTimer.IsEnabled)
            {
                _autoStartTimer.Stop();
            }
            ViewModel.IsServiceReadyVisible = false;
            StartPhaseExecution(2);
        }

        private void ExecutePause(object param)
        {
            // Ako je prethodna faza pala, dugme služi za PONOVNI POKUŠAJ
            if (_failedPhase > 0)
            {
                int p = _failedPhase;
                SetFailedPhase(0);
                if (p == 3)
                {
                    _phase3AutoRetryCount = 0;
                }
                ViewModel.AddLog("INFO", "Korisnik", string.Format("Korisnik zahtevao ponovni pokušaj Faze {0}. Pokrećem...", p));
                StartPhaseExecution(p);
                return;
            }

            // Ako je automatski start još u odbrojavanju
            if (_isMasterMode && !_taskRunnerStarted)
            {
                if (_autoStartTimer != null && _autoStartTimer.IsEnabled)
                {
                    _autoStartTimer.Stop();
                    ViewModel.IsPaused = true;
                    string pauseMsg = LocalizationService.Get("WinAutoStartPaused", "Automatski start pauziran. Omogućen ručni pregled.");
                    ViewModel.AutoStartMessage = pauseMsg;
                    ViewModel.LiveStatus = pauseMsg;
                    ViewModel.AddLog("WARN", "Korisnik", "Automatski start pauziran od strane tehničara.");
                    return;
                }
                else if (ViewModel.IsPaused)
                {
                    ViewModel.IsPaused = false;
                    ViewModel.IsServiceReadyVisible = false;
                    StartPhaseExecution(2);
                    return;
                }
            }

            ViewModel.IsPaused = !ViewModel.IsPaused;
            ViewModel.AddLog("WARN", "Korisnik", ViewModel.IsPaused ? "Instalacija PAUZIRANA od strane tehničara." : "Instalacija NASTAVLJENA.");

            try
            {
                string pauseMarker = @"C:\setup\.pause";
                if (ViewModel.IsPaused)
                {
                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                    File.WriteAllText(pauseMarker, DateTime.Now.ToString("o"));
                }
                else
                {
                    if (File.Exists(pauseMarker)) File.Delete(pauseMarker);
                }
            }
            catch { }

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", ViewModel.IsPaused ? "pause" : "resume" },
                { "is_paused", ViewModel.IsPaused },
                { "phase", ViewModel.CurrentPhase },
                { "timestamp", DateTime.Now.ToString("o") }
            });
        }

        private void ExecuteSkip(object param)
        {
            // Ako je prethodna faza pala, preskačemo na sledeću fazu
            if (_failedPhase > 0)
            {
                int nextP = _failedPhase + 1;
                if (nextP == 6 && HasUnfinishedInteractiveHwTests())
                {
                    ViewModel.AddLog("WARN", "Hardver", "Preskakanje na Fazu 6 je blokirano dok postoje obavezni interaktivni testovi — koristite vanredno preskakanje uz unos razloga.");
                    ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                    return;
                }
                SetFailedPhase(0);
                ViewModel.AddLog("WARN", "Korisnik", string.Format("Korisnik zahtevao PRESKAKANJE pale faze. Prelazim na Fazu {0}...", nextP));
                if (nextP <= 6)
                {
                    StartPhaseExecution(nextP);
                }
                else
                {
                    HandleFinalCompletion();
                }
                return;
            }

            ViewModel.AddLog("WARN", "Korisnik", string.Format("Zahtev za PRESKAKANJE trenutne operacije u Fazi {0}.", ViewModel.CurrentPhase));

            if (ViewModel.CurrentPhase == 5)
            {
                if (HasUnfinishedInteractiveHwTests())
                {
                    RequestEmergencySkipPhase5WithConfirmation();
                    return;
                }
                if (ForceSkipEmbeddedPhase5())
                {
                    return;
                }
            }

            try
            {
                string skipMarker = @"C:\setup\.skip";
                if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                File.WriteAllText(skipMarker, DateTime.Now.ToString("o"));
            }
            catch { }

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "skip" },
                { "phase", ViewModel.CurrentPhase },
                { "sub_phase", ViewModel.SubPhase },
                { "timestamp", DateTime.Now.ToString("o") }
            });
        }

        private void ExecuteDelayRestart(object param)
        {
            // 1. Otkaži eventualno već pokrenut sistemski shutdown/restart tajmer
            try
            {
                ProcessStartInfo abortPsi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/a",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process abortProc = Process.Start(abortPsi);
                if (abortProc != null)
                {
                    try { abortProc.WaitForExit(1000); } catch { }
                    try { abortProc.Dispose(); } catch { }
                }
            }
            catch { }

            // 2. Ako je aktivan završni UI tajmer gašenja, produži ga za 600 sekundi (10 minuta)
            if (ViewModel.ShutdownTimerActive)
            {
                ViewModel.ShutdownCountdown += 600;
            }

            // 3. Izračunaj novo vreme dozvoljenog restarta i upiši u deploy-state.json
            DateTime dueLocal;
            string dueIso;
            int totalDelayedMin;
            lock (_restartScheduleLock)
            {
                DateTime currentScheduled = GetScheduledRestartDueUtc();
                DateTime baseUtc = (currentScheduled > DateTime.UtcNow) ? currentScheduled : DateTime.UtcNow;
                _restartDueAtUtc = baseUtc.AddMinutes(10);
                _restartPending = true;
                _delayedRestartMinutesTotal += 10;
                dueLocal = _restartDueAtUtc.ToLocalTime();
                dueIso = dueLocal.ToString("o");
                totalDelayedMin = _delayedRestartMinutesTotal;
            }

            string persistError;
            bool persisted = TryPersistRestartScheduleState(out persistError);

            string dueTimeFormatted = dueLocal.ToString("HH:mm:ss");
            ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinRebootDelayedLive", "Međurestart odložen za 10 minuta (novi termin: {0})."), dueTimeFormatted);
            ViewModel.AddLog(
                persisted ? "INFO" : "WARN",
                "Korisnik",
                string.Format("Restart odložen za +10 min (ukupno +{0} min). Novi dozvoljeni termin restarta: {1}.", totalDelayedMin, dueTimeFormatted));

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "delay_restart" },
                { "minutes", 10 },
                { "delayed_restart_minutes", totalDelayedMin },
                { "restart_pending", true },
                { "restart_due_at", dueIso },
                { "timestamp", DateTime.Now.ToString("o") }
            });

            if (persisted)
            {
                MessageBox.Show(
                    string.Format(LocalizationService.Get("WinRebootDelayedDialogBody", "Restart računara je stvarno odložen za 10 minuta.\nNovi termin restarta: {0} (ukupno odloženo: {1} min)."), dueTimeFormatted, totalDelayedMin),
                    LocalizationService.Get("WinRebootDelayedDialogTitle", "WIS Servis — Odlaganje restarta"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    string.Format(LocalizationService.Get("WinRebootDelayPersistErrBody", "Restart je odložen u memoriji do {0}, ali upis u deploy-state.json nije uspeo: {1}"), dueTimeFormatted, persistError ?? LocalizationService.Get("WinUnknownError", "Nepoznata greška")),
                    LocalizationService.Get("WinServiceWarningTitle", "WIS Servis — Upozorenje"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ExecuteRerunPhase4(object param)
        {
            RerunPhase4();
        }

        public void RerunPhase4()
        {
            Process activePhaseProc = _currentPhaseProcess;
            if (activePhaseProc != null)
            {
                bool running = false;
                try { running = !activePhaseProc.HasExited; } catch { }
                if (running)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("WARN", "TaskRunner", "Druga faza je već aktivna. Sačekajte završetak pre ponovnog pokretanja Faze 4.");
                    });
                    return;
                }
            }

            lock (_appRetryLock)
            {
                if (_isSingleAppRetryRunning)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("WARN", "TaskRunner", "Pojedinačni Retry programa je već u toku. Sačekajte završetak pre ponovnog pokretanja cele Faze 4.");
                    });
                    return;
                }
            }

            try
            {
                string setupDir = @"C:\setup";
                if (!Directory.Exists(setupDir))
                {
                    Directory.CreateDirectory(setupDir);
                }
                string flagPath = Path.Combine(setupDir, "force-rerun-phase4.flag");
                File.WriteAllText(flagPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("WARN", "TaskRunner", "Nije moguće kreirati force-rerun-phase4.flag: " + ex.Message);
            }

            Action startRerun = delegate
            {
                SetFailedPhase(0);
                if (ViewModel != null && ViewModel.Apps != null)
                {
                    foreach (var app in ViewModel.Apps)
                    {
                        app.Status = "WAIT";
                        app.Detail = LocalizationService.Get("WinAppQueued", "U redu čekanja");
                    }
                    ViewModel.UpdateAppsSummary();
                }
                if (ViewModel != null)
                {
                    ViewModel.AddLog("INFO", "TaskRunner", "[TaskRunner] Pokrećem ponovnu instalaciju programa...");
                }
                StartPhaseExecution(4);
            };

            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(startRerun);
            }
            else
            {
                startRerun();
            }
        }

        private void ExecuteRetryApp(object param)
        {
            AppItem targetApp = param as AppItem;
            if (targetApp == null && param is string && ViewModel != null && ViewModel.Apps != null)
            {
                string key = (string)param;
                foreach (var item in ViewModel.Apps)
                {
                    if (item != null && (string.Equals(item.Id, key, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetApp = item;
                        break;
                    }
                }
            }
            if (targetApp != null)
            {
                RetrySingleApp(targetApp);
            }
        }

        private static string ResolveSafeRetryAppId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return null;
            string trimmed = rawId.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "dotnet":
                case "runtimes":
                    return "DotNet";
                case "chrome":
                case "google chrome":
                    return "Chrome";
                case "winrar":
                    return "WinRAR";
                case "acrobat":
                case "adobe acrobat":
                case "adobe acrobat pro":
                    return "Acrobat";
                case "office":
                case "microsoft office":
                case "ms office":
                    return "Office";
                case "batterybar":
                    return "BatteryBar";
                case "license":
                case "activation":
                    return "License";
                case "shortcuts":
                    return "Shortcuts";
                default:
                    return null;
            }
        }

        private static bool ScriptSupportsParameter(string scriptPath, string parameterName)
        {
            try
            {
                if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(parameterName) || !File.Exists(scriptPath))
                {
                    return false;
                }
                string content = File.ReadAllText(scriptPath, Encoding.UTF8);
                return content.IndexOf(parameterName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldIgnoreRetryJsonLine(string jsonLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonLine)) return false;
                JsonNode root = JsonHelper.Parse(jsonLine);
                if (root == null || root.IsNull) return false;

                string ev = root["event"].AsString(root["type"].AsString("")).Trim().ToUpperInvariant();
                if (ev == "APP_UPDATE" || ev == "LOG" || ev == "STEP")
                {
                    return false;
                }

                if (root.ContainsKey("phase"))
                {
                    int msgPhase = root["phase"].AsInt(0);
                    int curPhase = ViewModel != null ? ViewModel.CurrentPhase : 0;
                    if (msgPhase > 0 && curPhase > 0 && msgPhase < curPhase)
                    {
                        if (ev == "PROGRESS" || string.IsNullOrEmpty(ev))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private bool NeedsPhase4StepRefresh(string appId)
        {
            if (ViewModel == null || ViewModel.Apps == null || string.IsNullOrEmpty(appId))
            {
                return false;
            }
            for (int i = 0; i < ViewModel.Apps.Count; i++)
            {
                AppItem item = ViewModel.Apps[i];
                if (item != null && string.Equals(item.Id, appId, StringComparison.OrdinalIgnoreCase))
                {
                    string st = (item.Status ?? "").Trim().ToUpperInvariant();
                    return st != "OK" && st != "COMPLETED" && st != "SKIPPED";
                }
            }
            return false;
        }

        private bool HasOtherRunningApps(AppItem excludedApp)
        {
            if (ViewModel == null || ViewModel.Apps == null) return false;
            for (int i = 0; i < ViewModel.Apps.Count; i++)
            {
                AppItem item = ViewModel.Apps[i];
                if (item != null && !object.ReferenceEquals(item, excludedApp) && item.IsRunning)
                {
                    return true;
                }
            }
            return false;
        }

        private bool RunPhase4DependentStepSync(string stepAppId, string scriptPath, bool preserveCurrentPhase)
        {
            try
            {
                if (string.IsNullOrEmpty(stepAppId) || string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
                {
                    return false;
                }

                string args = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -OnlyApp \"{1}\"", scriptPath, stepAppId);
                if (preserveCurrentPhase && ScriptSupportsParameter(scriptPath, "PreserveCurrentPhase"))
                {
                    args += " -PreserveCurrentPhase";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    WorkingDirectory = Directory.Exists(@"C:\setup") ? @"C:\setup" : (Path.GetDirectoryName(scriptPath) ?? @"C:\"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (ViewModel != null)
                    {
                        ViewModel.AddLog("INFO", "AppsRetry", string.Format("Osvežavam zavisni korak Faze 4: {0}...", stepAppId));
                    }
                });

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return false;

                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string line = e.Data.Trim();
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (line.StartsWith("{") && line.EndsWith("}"))
                            {
                                if (ShouldIgnoreRetryJsonLine(line)) return;
                                ProcessIncomingJson(line);
                            }
                            else
                            {
                                ParseAndAddRawLogLine(line);
                            }
                        });
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog(line);
                        }
                    };

                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        string errLine = e.Data.Trim();
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            ViewModel.AddLog("WARN", "AppsRetry", errLine);
                        });
                        if (_telemetryService != null)
                        {
                            _telemetryService.PushLog(errLine);
                        }
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    bool exited = proc.WaitForExit(180000);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        return false;
                    }
                    try { proc.WaitForExit(); } catch { }
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public void RetrySingleApp(AppItem targetApp)
        {
            if (targetApp == null || string.IsNullOrWhiteSpace(targetApp.Id) || targetApp.IsRunning) return;

            string safeAppId = ResolveSafeRetryAppId(targetApp.Id);
            if (string.IsNullOrEmpty(safeAppId))
            {
                ViewModel.AddLog("ERROR", "AppsRetry", "Nepoznat identifikator aplikacije za Retry: " + targetApp.Id);
                return;
            }

            Process activePhaseProc = _currentPhaseProcess;
            bool phase4Active = activePhaseProc != null && !activePhaseProc.HasExited && ViewModel != null && ViewModel.CurrentPhase == 4;
            if (phase4Active)
            {
                ViewModel.AddLog("WARN", "AppsRetry", "Faza 4 (instalacija programa) je već aktivna. Sačekajte završetak pre pojedinačnog Retry-a za " + targetApp.Name + ".");
                return;
            }

            lock (_appRetryLock)
            {
                if (_isSingleAppRetryRunning)
                {
                    ViewModel.AddLog("WARN", "AppsRetry", "Drugi pojedinačni Retry je već u toku. Sačekajte završetak.");
                    return;
                }
                _isSingleAppRetryRunning = true;
            }

            bool preserveCurrentPhase = (ViewModel != null && ViewModel.CurrentPhase > 4);

            Dispatcher.BeginInvoke((Action)delegate
            {
                targetApp.Status = "RUNNING";
                targetApp.Detail = "Ponovna instalacija u toku...";
                ViewModel.UpdateAppsSummary();
                ViewModel.AddLog("INFO", "AppsRetry", string.Format("Pokrećem pojedinačni Retry za program: {0} ({1})", targetApp.Name, safeAppId));
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool exited = false;
                int exitCode = -1;
                string scriptPath = null;
                bool shouldContinuePhase5 = false;
                try
                {
                    scriptPath = FindPhaseScriptPath(AppDomain.CurrentDomain.BaseDirectory, "Phase4-Apps.ps1");
                    if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            targetApp.Status = "ERROR";
                            targetApp.Detail = LocalizationService.Get("WinAppPhase4ScriptMissing", "Phase4-Apps.ps1 nije pronađena");
                            ViewModel.UpdateAppsSummary();
                            ViewModel.AddLog("ERROR", "AppsRetry", "Phase4-Apps.ps1 nije pronađena na disku za Retry.");
                        });
                        return;
                    }

                    string retryArgs = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -OnlyApp \"{1}\"", scriptPath, safeAppId);
                    if (preserveCurrentPhase && ScriptSupportsParameter(scriptPath, "PreserveCurrentPhase"))
                    {
                        retryArgs += " -PreserveCurrentPhase";
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = retryArgs,
                        WorkingDirectory = Directory.Exists(@"C:\setup") ? @"C:\setup" : (Path.GetDirectoryName(scriptPath) ?? @"C:\"),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    using (var proc = Process.Start(psi))
                    {
                        if (proc == null)
                        {
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                targetApp.Status = "ERROR";
                                targetApp.Detail = LocalizationService.Get("WinAppPsStartFailed", "Nije moguće pokrenuti powershell.exe");
                                ViewModel.UpdateAppsSummary();
                            });
                            return;
                        }

                        proc.OutputDataReceived += (s, e) =>
                        {
                            if (string.IsNullOrEmpty(e.Data)) return;
                            string line = e.Data.Trim();
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (line.StartsWith("{") && line.EndsWith("}"))
                                {
                                    if (ShouldIgnoreRetryJsonLine(line))
                                    {
                                        return;
                                    }
                                    ProcessIncomingJson(line);
                                }
                                else
                                {
                                    ParseAndAddRawLogLine(line);
                                }
                            });
                            if (_telemetryService != null)
                            {
                                _telemetryService.PushLog(line);
                            }
                        };

                        proc.ErrorDataReceived += (s, e) =>
                        {
                            if (string.IsNullOrEmpty(e.Data)) return;
                            string errLine = e.Data.Trim();
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                ViewModel.AddLog("WARN", "AppsRetry", errLine);
                            });
                            if (_telemetryService != null)
                            {
                                _telemetryService.PushLog(errLine);
                            }
                        };

                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();

                        exited = proc.WaitForExit(900000);
                        if (!exited)
                        {
                            try
                            {
                                var tkPsi = new ProcessStartInfo("taskkill.exe", string.Format("/PID {0} /T /F", proc.Id))
                                {
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                };
                                using (Process tk = Process.Start(tkPsi))
                                {
                                    if (tk != null) tk.WaitForExit(5000);
                                }
                            }
                            catch { }
                            try { proc.Kill(); } catch { }
                        }
                        else
                        {
                            try { proc.WaitForExit(); } catch { }
                            exitCode = proc.ExitCode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        targetApp.Status = "ERROR";
                        targetApp.Detail = LocalizationService.Get("WinAppLaunchErrorPrefix", "Greška pri pokretanju: ") + ex.Message;
                        ViewModel.UpdateAppsSummary();
                        ViewModel.AddLog("ERROR", "AppsRetry", string.Format("Izuzetak pri Retry-u za {0}: {1}", safeAppId, ex.Message));
                    });
                }
                finally
                {
                    bool otherRunning = false;
                    Action updateRetryResultUi = delegate
                    {
                        if (targetApp.Status == "RUNNING")
                        {
                            if (exited && exitCode == 0)
                            {
                                targetApp.Status = "OK";
                                targetApp.Detail = LocalizationService.Get("WinAppRetryVerified", "Instalacija uspešno verifikovana");
                                ViewModel.AddLog("SUCCESS", "AppsRetry", string.Format("Program {0} uspešno instaliran nakon Retry-a.", targetApp.Name));
                            }
                            else
                            {
                                targetApp.Status = "ERROR";
                                targetApp.Detail = exited
                                    ? string.Format(LocalizationService.Get("WinAppRetryFailedExit", "Ponovna instalacija nije uspela (ExitCode {0})"), exitCode)
                                    : LocalizationService.Get("WinAppRetryTimeout", "Ponovna instalacija prekoračila timeout od 15 min");
                                ViewModel.AddLog("ERROR", "AppsRetry", string.Format("Retry za {0} nije uspeo: {1}", targetApp.Name, targetApp.Detail));
                            }
                        }
                        ViewModel.UpdateAppsSummary();
                        otherRunning = HasOtherRunningApps(targetApp);
                    };

                    if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        try
                        {
                            Dispatcher.Invoke(updateRetryResultUi);
                        }
                        catch { }
                    }

                    if (exited && exitCode == 0 && _failedPhase == 4 && !otherRunning)
                    {
                        if (string.Equals(safeAppId, "Acrobat", StringComparison.OrdinalIgnoreCase))
                        {
                            _pendingPostRetryShortcutsRefresh = true;
                        }
                        else if (string.Equals(safeAppId, "Office", StringComparison.OrdinalIgnoreCase))
                        {
                            _pendingPostRetryShortcutsRefresh = true;
                            _pendingPostRetryLicenseRefresh = true;
                        }

                        List<string> missing = GetMissingKeyApplications();
                        if (missing.Count == 0)
                        {
                            if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath))
                            {
                                if (_pendingPostRetryShortcutsRefresh ||
                                    string.Equals(safeAppId, "Office", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(safeAppId, "Acrobat", StringComparison.OrdinalIgnoreCase))
                                {
                                    RunPhase4DependentStepSync("Shortcuts", scriptPath, preserveCurrentPhase);
                                }
                                if (_pendingPostRetryLicenseRefresh ||
                                    string.Equals(safeAppId, "Office", StringComparison.OrdinalIgnoreCase))
                                {
                                    RunPhase4DependentStepSync("License", scriptPath, preserveCurrentPhase);
                                }
                            }

                            if (PerformPhase4RealityCheck())
                            {
                                SetFailedPhase(0);
                                if (_telemetryService != null)
                                {
                                    _telemetryService.PushLog("[SUCCESS] [RealityCheck] Post-Execution Reality Check (Faza 4) USPEŠAN nakon pojedinačnog Retry-a.");
                                }
                                SaveDeployStatePhase(5);
                                shouldContinuePhase5 = true;
                            }
                        }
                        else
                        {
                            string remainingStr = string.Join(", ", missing.ToArray());
                            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                            {
                                Dispatcher.BeginInvoke((Action)delegate
                                {
                                    if (ViewModel != null)
                                    {
                                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("WinAppRetryRemaining", "Program {0} uspešno instaliran. Još nedostaju za Retry: {1}."), targetApp.Name, remainingStr);
                                        ViewModel.AddLog("INFO", "RealityCheck", "Preostale ključne aplikacije koje čekaju Retry: " + remainingStr);
                                    }
                                });
                            }
                        }
                    }

                    lock (_appRetryLock)
                    {
                        _isSingleAppRetryRunning = false;
                    }

                    if (shouldContinuePhase5 && Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.AddLog("SUCCESS", "TaskRunner", "Sve ključne aplikacije Faze 4 su sada prisutne na disku. Automatski nastavljam na Fazu 5...");
                            }
                            StartPhaseExecution(5);
                        });
                    }
                }
            });
        }

        #endregion

        #region Autopilot Provera & Skeniranje

        private void ExecuteAutopilotRescan(object param)
        {
            StartAutopilotScan();
        }

        public void StartAutopilotScan()
        {
            Action startAction = delegate
            {
                if (ViewModel.Autopilot.IsScanning) return;

                ViewModel.Autopilot.IsScanning = true;
                ViewModel.Autopilot.StatusTitle = LocalizationService.Get("WinApScanningTitle", "PROVERA AUTOPILOTA U TOKU...");
                ViewModel.Autopilot.StatusDescription = LocalizationService.Get("WinApScanningDesc", "Sistem vrši duboku forenziku lokalnog registra i proveru Microsoft ZTD oblaka...");
                ViewModel.Autopilot.RecommendedAction = LocalizationService.Get("WinApScanningRec", "Molimo sačekajte završetak bezbednosne provere...");
                ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(0, 122, 204);
                ViewModel.AddLog("INFO", "Autopilot", "Pokretanje bezbednosne provere Microsoft Autopilot statusa...");

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ExecuteAutopilotScanWork();
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            ViewModel.Autopilot.IsScanning = false;
                            ViewModel.Autopilot.StatusLevel = "ERROR";
                            ViewModel.Autopilot.StatusTitle = LocalizationService.Get("ApMatrixTitle_Error", "GREŠKA PRI PROVERI");
                            ViewModel.Autopilot.StatusDescription = LocalizationService.Get("WinApWorkerException", "Izuzetak na pozadinskoj niti: ") + ex.Message;
                            ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                            ViewModel.AddLog("ERROR", "Autopilot", "Izuzetak u Autopilot radniku: " + ex.Message);
                        }));
                    }
                });
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(startAction);
            }
            else
            {
                startAction();
            }
        }

        private string FindAutopilotTool(out bool isNativeAuditor)
        {
            isNativeAuditor = false;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string auditorName = "WIS-AutopilotAuditor.exe";
            string scriptName = "Provera_autopilota.ps1";

            // 1. PRIORITET: WIS-AutopilotAuditor.exe lokalne putanje
            string[] auditorCandidates = new string[]
            {
                Path.Combine(@"C:\setup", auditorName),
                Path.Combine(@"C:\setup\Tools", auditorName),
                Path.Combine(@"C:\setup\Tools\AutopilotAuditor", auditorName),
                Path.Combine(@"C:\setup\Skripte", auditorName),
                Path.Combine(baseDir, auditorName),
                Path.Combine(baseDir, "Tools", auditorName),
                Path.Combine(baseDir, @"Tools\AutopilotAuditor", auditorName),
                Path.Combine(baseDir, "..", auditorName),
                Path.Combine(baseDir, @"..\Tools\AutopilotAuditor", auditorName),
                Path.Combine(baseDir, @"..\..\Tools\AutopilotAuditor", auditorName)
            };

            for (int i = 0; i < auditorCandidates.Length; i++)
            {
                try
                {
                    if (File.Exists(auditorCandidates[i]))
                    {
                        isNativeAuditor = true;
                        return Path.GetFullPath(auditorCandidates[i]);
                    }
                }
                catch { }
            }

            // 2. Provera lokalnih i eksternih diskova za WIS-AutopilotAuditor.exe
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int d = 0; d < drives.Length; d++)
                {
                    try
                    {
                        if (!drives[d].IsReady) continue;
                        string root = drives[d].RootDirectory.FullName;
                        string[] driveCandidates = new string[]
                        {
                            Path.Combine(root, auditorName),
                            Path.Combine(root, @"Tools\" + auditorName),
                            Path.Combine(root, @"Tools\AutopilotAuditor\" + auditorName),
                            Path.Combine(root, @"sources\$OEM$\$1\setup\" + auditorName),
                            Path.Combine(root, @"sources\$OEM$\$1\setup\Skripte\" + auditorName),
                            Path.Combine(root, @"sources\$OEM$\$1\setup\Tools\AutopilotAuditor\" + auditorName)
                        };
                        for (int j = 0; j < driveCandidates.Length; j++)
                        {
                            if (File.Exists(driveCandidates[j]))
                            {
                                string foundPath = Path.GetFullPath(driveCandidates[j]);
                                try
                                {
                                    if (Directory.Exists(@"C:\setup"))
                                    {
                                        string localTarget = Path.Combine(@"C:\setup", auditorName);
                                        if (!File.Exists(localTarget)) File.Copy(foundPath, localTarget, true);
                                        if (File.Exists(localTarget))
                                        {
                                            isNativeAuditor = true;
                                            return localTarget;
                                        }
                                    }
                                }
                                catch { }
                                isNativeAuditor = true;
                                return foundPath;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Provera NAS deljenja za WIS-AutopilotAuditor.exe
            try
            {
                string[] nasShares = new string[] { @"\\192.168.170.254\Servis", @"\\192.168.170.254\WIS", @"\\truenas.servis\WIS" };
                for (int n = 0; n < nasShares.Length; n++)
                {
                    try
                    {
                        string share = nasShares[n];
                        try
                        {
                            ProcessStartInfo netPsi = new ProcessStartInfo
                            {
                                FileName = "net.exe",
                                Arguments = string.Format("use \"{0}\" /persistent:no", share),
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            using (Process pNet = Process.Start(netPsi))
                            {
                                if (pNet != null && pNet.WaitForExit(3000)) { }
                            }
                        }
                        catch { }

                        string[] nasCandidates = new string[]
                        {
                            Path.Combine(share, auditorName),
                            Path.Combine(share, @"Tools\" + auditorName),
                            Path.Combine(share, @"Tools\AutopilotAuditor\" + auditorName),
                            Path.Combine(share, @"sources\$OEM$\$1\setup\" + auditorName),
                            Path.Combine(share, @"sources\$OEM$\$1\setup\Skripte\" + auditorName)
                        };

                        for (int k = 0; k < nasCandidates.Length; k++)
                        {
                            if (File.Exists(nasCandidates[k]))
                            {
                                string nasPath = Path.GetFullPath(nasCandidates[k]);
                                try
                                {
                                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                                    string localTarget = Path.Combine(@"C:\setup", auditorName);
                                    File.Copy(nasPath, localTarget, true);
                                    if (File.Exists(localTarget))
                                    {
                                        isNativeAuditor = true;
                                        return localTarget;
                                    }
                                }
                                catch { }
                                isNativeAuditor = true;
                                return nasPath;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 4. FALLBACK: Provera_autopilota.ps1 lokalne putanje
            string[] scriptCandidates = new string[]
            {
                Path.Combine(@"C:\setup", scriptName),
                Path.Combine(@"C:\setup\Skripte", scriptName),
                Path.Combine(baseDir, scriptName),
                Path.Combine(baseDir, "..", scriptName),
                Path.Combine(baseDir, @"..\..", scriptName),
                Path.Combine(baseDir, @"..\..\..", scriptName),
                Path.Combine(baseDir, "Skripte", scriptName),
                Path.Combine(baseDir, @"..\Skripte", scriptName),
                Path.Combine(baseDir, @"..\..\Skripte", scriptName)
            };

            for (int i = 0; i < scriptCandidates.Length; i++)
            {
                try
                {
                    if (File.Exists(scriptCandidates[i]))
                    {
                        isNativeAuditor = false;
                        return Path.GetFullPath(scriptCandidates[i]);
                    }
                }
                catch { }
            }

            // 5. Provera lokalnih i eksternih diskova za Provera_autopilota.ps1
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int d = 0; d < drives.Length; d++)
                {
                    try
                    {
                        if (!drives[d].IsReady) continue;
                        string root = drives[d].RootDirectory.FullName;
                        string[] driveCandidates = new string[]
                        {
                            Path.Combine(root, scriptName),
                            Path.Combine(root, @"Skripte\" + scriptName),
                            Path.Combine(root, @"sources\$OEM$\$1\setup\" + scriptName),
                            Path.Combine(root, @"sources\$OEM$\$1\setup\Skripte\" + scriptName)
                        };
                        for (int j = 0; j < driveCandidates.Length; j++)
                        {
                            if (File.Exists(driveCandidates[j]))
                            {
                                string foundPath = Path.GetFullPath(driveCandidates[j]);
                                try
                                {
                                    if (Directory.Exists(@"C:\setup"))
                                    {
                                        string localTarget = Path.Combine(@"C:\setup", scriptName);
                                        if (!File.Exists(localTarget)) File.Copy(foundPath, localTarget, true);
                                        if (Directory.Exists(@"C:\setup\Skripte"))
                                        {
                                            string subTarget = Path.Combine(@"C:\setup\Skripte", scriptName);
                                            if (!File.Exists(subTarget)) File.Copy(foundPath, subTarget, true);
                                        }
                                        if (File.Exists(localTarget))
                                        {
                                            isNativeAuditor = false;
                                            return localTarget;
                                        }
                                    }
                                }
                                catch { }
                                isNativeAuditor = false;
                                return foundPath;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 6. Provera NAS deljenja za Provera_autopilota.ps1
            try
            {
                string[] nasShares = new string[] { @"\\192.168.170.254\Servis", @"\\192.168.170.254\WIS", @"\\truenas.servis\WIS" };
                for (int n = 0; n < nasShares.Length; n++)
                {
                    try
                    {
                        string share = nasShares[n];
                        try
                        {
                            ProcessStartInfo netPsi = new ProcessStartInfo
                            {
                                FileName = "net.exe",
                                Arguments = string.Format("use \"{0}\" /persistent:no", share),
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            using (Process pNet = Process.Start(netPsi))
                            {
                                if (pNet != null && pNet.WaitForExit(3000)) { }
                            }
                        }
                        catch { }

                        string[] nasCandidates = new string[]
                        {
                            Path.Combine(share, scriptName),
                            Path.Combine(share, @"Skripte\" + scriptName),
                            Path.Combine(share, @"sources\$OEM$\$1\setup\" + scriptName),
                            Path.Combine(share, @"sources\$OEM$\$1\setup\Skripte\" + scriptName)
                        };

                        for (int k = 0; k < nasCandidates.Length; k++)
                        {
                            if (File.Exists(nasCandidates[k]))
                            {
                                string nasPath = Path.GetFullPath(nasCandidates[k]);
                                try
                                {
                                    if (!Directory.Exists(@"C:\setup")) Directory.CreateDirectory(@"C:\setup");
                                    string localTarget = Path.Combine(@"C:\setup", scriptName);
                                    File.Copy(nasPath, localTarget, true);
                                    if (Directory.Exists(@"C:\setup\Skripte"))
                                    {
                                        string subTarget = Path.Combine(@"C:\setup\Skripte", scriptName);
                                        File.Copy(nasPath, subTarget, true);
                                    }
                                    if (File.Exists(localTarget))
                                    {
                                        isNativeAuditor = false;
                                        return localTarget;
                                    }
                                }
                                catch { }
                                isNativeAuditor = false;
                                return nasPath;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private bool IsAutopilotMatrixConfirmed(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            string upper = code.Trim().ToUpperInvariant();
            return upper == "LOCKED_TENANT" ||
                   upper == "HIDDEN_LOCK" ||
                   upper == "RELEASED_LOCAL_TRACES" ||
                   upper == "CONFIRMED_CLEAN" ||
                   upper == "CLEAN";
        }

        private JsonNode TryReadCachedAutopilotReport()
        {
            string[] candidates = new string[]
            {
                @"C:\Windows\Logs\WIS\autopilot_report.json",
                @"C:\setup\autopilot_report.json"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    if (!File.Exists(candidates[i])) continue;
                    string raw = File.ReadAllText(candidates[i], Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    JsonNode parsed = JsonHelper.Parse(raw);
                    if (parsed != null && !parsed.IsNull && parsed.IsObject)
                    {
                        return parsed;
                    }
                }
                catch { }
            }
            return null;
        }

        private void TryRetryAutopilotOnNetworkReady()
        {
            Action checkAndRetry = delegate
            {
                if (_autopilotRetriedAfterNetwork || ViewModel == null || ViewModel.Autopilot == null) return;
                if (ViewModel.Autopilot.IsScanning) return;
                if (IsAutopilotMatrixConfirmed(ViewModel.Autopilot.MatrixCode)) return;

                _autopilotRetriedAfterNetwork = true;
                ViewModel.AddLog("INFO", "Autopilot", "Mreža je postala aktivna — ponavljam proveru Autopilot ZTD oblaka...");
                StartAutopilotScan();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(checkAndRetry);
            }
            else
            {
                checkAndRetry();
            }
        }

        private void ExecuteAutopilotScanWork()
        {
            JsonNode cachedNode = null;
            bool cachedConfirmed = false;
            try
            {
                cachedNode = TryReadCachedAutopilotReport();
                if (cachedNode != null)
                {
                    string cachedMatrix = cachedNode["matrix_code"].AsString(cachedNode["status"].AsString(cachedNode["MatrixCode"].AsString("")));
                    cachedConfirmed = IsAutopilotMatrixConfirmed(cachedMatrix);
                    if (cachedConfirmed)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            UpdateAutopilotFromData(cachedNode);
                            ViewModel.AddLog("INFO", "Autopilot", "Učitan sačuvani Autopilot izveštaj (" + cachedMatrix + ").");
                        }));
                    }
                }

                bool isNativeAuditor;
                string toolPath = FindAutopilotTool(out isNativeAuditor);
                if (string.IsNullOrEmpty(toolPath))
                {
                    if (cachedConfirmed) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ViewModel.Autopilot.IsScanning = false;
                        ViewModel.Autopilot.StatusLevel = "WARN";
                        ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_ScriptNotFound", "ALAT NIJE PRONAĐEN");
                        ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_ScriptNotFound", "WIS-AutopilotAuditor.exe niti Provera_autopilota.ps1 nisu pronađeni na disku ili NAS deljenju.");
                        ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_ScriptNotFound", "Uverite se da se WIS-AutopilotAuditor.exe ili Provera_autopilota.ps1 nalaze u C:\\setup, na USB-u ili NAS deljenju.");
                        ViewModel.AddLog("WARN", "Autopilot", LocalizationService.Instance.GetString("ApLogScriptNotFound", "Autopilot auditor alat nije pronađen lokalno niti na NAS-u."));
                    }));
                    return;
                }

                ProcessStartInfo psi;
                if (isNativeAuditor)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ViewModel.AddLog("INFO", "Autopilot", "Pokrenut nativni forenzički auditor (WIS-AutopilotAuditor.exe)...");
                    }));
                    psi = new ProcessStartInfo
                    {
                        FileName = toolPath,
                        Arguments = "--json --silent",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ViewModel.AddLog("INFO", "Autopilot", "Pokrenuta skripta za forenziku Autopilota (Provera_autopilota.ps1)...");
                    }));
                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\" -JsonOutput", toolPath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                }

                StringBuilder stdoutBuilder = new StringBuilder();
                object syncLock = new object();
                using (Process proc = Process.Start(psi))
                {
                    proc.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (syncLock)
                        {
                            stdoutBuilder.AppendLine(e.Data);
                        }
                    };
                    proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(60000))
                    {
                        try { proc.Kill(); } catch { }
                    }
                    else
                    {
                        proc.WaitForExit();
                    }
                }

                string fullOutput;
                lock (syncLock)
                {
                    fullOutput = stdoutBuilder.ToString();
                }

                string jsonResult = null;
                if (!string.IsNullOrEmpty(fullOutput))
                {
                    int firstBrace = fullOutput.IndexOf('{');
                    int lastBrace = fullOutput.LastIndexOf('}');
                    if (firstBrace >= 0 && lastBrace > firstBrace)
                    {
                        jsonResult = fullOutput.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
                    }
                }

                if (string.IsNullOrEmpty(jsonResult))
                {
                    if (cachedConfirmed) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ViewModel.Autopilot.IsScanning = false;
                        ViewModel.Autopilot.StatusLevel = "WARN";
                        ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_ScanError", "GREŠKA PRI SKENIRANJU");
                        ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_ScanError", "Skripta za Autopilot proveru nije vratila validan izveštaj.");
                        ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_ScanError", "Kliknite na 'Osveži Proveru' ili proverite mrežnu vezu.");
                        ViewModel.AddLog("WARN", "Autopilot", LocalizationService.Instance.GetString("ApLogInvalidJson", "Skripta nije vratila validan JSON izlaz."));
                    }));
                    return;
                }

                JsonNode root = JsonHelper.Parse(jsonResult);
                if (root == null || root.IsNull)
                {
                    if (cachedConfirmed) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ViewModel.Autopilot.IsScanning = false;
                        ViewModel.Autopilot.StatusLevel = "WARN";
                        ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_ParseError", "GREŠKA U PARSIRANJU");
                        ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_ParseError", "Neispravan JSON format rezultata autopilota.");
                        ViewModel.AddLog("ERROR", "Autopilot", LocalizationService.Instance.GetString("ApLogInvalidJson", "Neispravan JSON format skripte."));
                    }));
                    return;
                }

                string newMatrix = root["matrix_code"].AsString(root["status"].AsString(root["MatrixCode"].AsString("")));
                if (cachedConfirmed && !IsAutopilotMatrixConfirmed(newMatrix))
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        UpdateAutopilotFromData(cachedNode);
                    }));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    UpdateAutopilotFromData(root);
                }));
            }
            catch (Exception ex)
            {
                if (cachedConfirmed) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ViewModel.Autopilot.IsScanning = false;
                    ViewModel.Autopilot.StatusLevel = "ERROR";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_Error", "IZUZETAK PRI PROVERI");
                    ViewModel.Autopilot.StatusDescription = ex.Message;
                    ViewModel.AddLog("ERROR", "Autopilot", string.Format(LocalizationService.Instance.GetString("ApLogWorkerException", "Izuzetak u Autopilot radniku: {0}"), ex.Message));
                }));
            }
        }

        private void UpdateAutopilotFromData(JsonNode root)
        {
            if (root == null || root.IsNull) return;

            string matrixCode = root["matrix_code"].AsString(root["status"].AsString(root["MatrixCode"].AsString("UNKNOWN"))).ToUpperInvariant().Trim();
            bool isLocked = root["is_locked"].AsBool(root["IsLocked"].AsBool(matrixCode == "LOCKED_TENANT" || matrixCode == "HIDDEN_LOCK"));

            ViewModel.Autopilot.MatrixCode = matrixCode;
            ViewModel.Autopilot.IsLocked = isLocked;
            ViewModel.Autopilot.IsScanning = false;
            ViewModel.Autopilot.LastScanTime = root["timestamp"].AsString(root["Timestamp"].AsString(DateTime.Now.ToString("HH:mm:ss")));

            if (root.ContainsKey("tenant_name")) ViewModel.Autopilot.TenantDomain = root["tenant_name"].AsString(ViewModel.Autopilot.TenantDomain);
            else if (root.ContainsKey("TenantName")) ViewModel.Autopilot.TenantDomain = root["TenantName"].AsString(ViewModel.Autopilot.TenantDomain);
            else if (root.ContainsKey("tenant_domain")) ViewModel.Autopilot.TenantDomain = root["tenant_domain"].AsString(ViewModel.Autopilot.TenantDomain);

            if (root.ContainsKey("tenant_id")) ViewModel.Autopilot.TenantId = root["tenant_id"].AsString(ViewModel.Autopilot.TenantId);
            else if (root.ContainsKey("TenantId")) ViewModel.Autopilot.TenantId = root["TenantId"].AsString(ViewModel.Autopilot.TenantId);

            if (root.ContainsKey("device_serial") && !string.IsNullOrEmpty(root["device_serial"].AsString()))
                HardwareIdentityService.TryUpdate("SerialNumber", root["device_serial"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);
            if (root.ContainsKey("device_model") && !string.IsNullOrEmpty(root["device_model"].AsString()))
                HardwareIdentityService.TryUpdate("ComputerModel", root["device_model"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);

            var hwNode = root["hardware"];
            if (hwNode.IsObject)
            {
                if (hwNode.ContainsKey("manufacturer")) HardwareIdentityService.TryUpdate("Autopilot.HwManufacturer", hwNode["manufacturer"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);
                if (hwNode.ContainsKey("model")) HardwareIdentityService.TryUpdate("ComputerModel", hwNode["model"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);
                if (hwNode.ContainsKey("serial_number")) HardwareIdentityService.TryUpdate("SerialNumber", hwNode["serial_number"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);
                if (hwNode.ContainsKey("smbios_uuid")) HardwareIdentityService.TryUpdate("Autopilot.HwSmbiosUuid", hwNode["smbios_uuid"].AsString(), HardwareUpdateSource.Autopilot, ViewModel);

                bool tpmPresent = hwNode["tpm_present"].AsBool(false);
                bool tpmReady = hwNode["tpm_ready"].AsBool(false);
                ViewModel.Autopilot.TpmPresent = tpmPresent;
                ViewModel.Autopilot.TpmReady = tpmReady;
                if (tpmPresent && tpmReady)
                {
                    ViewModel.Autopilot.TpmStatusText = LocalizationService.Instance.GetString("ApTpmReadyActive", "Spreman (TPM 2.0 aktivan)");
                    ViewModel.Autopilot.TpmStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                }
                else if (tpmPresent)
                {
                    ViewModel.Autopilot.TpmStatusText = LocalizationService.Instance.GetString("ApTpmPresentNotReady", "Prisutan (Nije spreman)");
                    ViewModel.Autopilot.TpmStatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                }
                else
                {
                    ViewModel.Autopilot.TpmStatusText = LocalizationService.Instance.GetString("ApTpmMissing", "Nije detektovan / Nema TPM");
                    ViewModel.Autopilot.TpmStatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                }
            }

            var lfNode = root["local_forensics"];
            if (lfNode.IsObject)
            {
                bool hasTraces = lfNode["has_traces"].AsBool(false);
                int devKeys = lfNode["device_management_keys"].AsInt(0);
                int enrollKeys = lfNode["enrollment_keys"].AsInt(0);
                int workAccs = lfNode["enterprise_work_accounts"].AsInt(0);

                if (!lfNode.ContainsKey("has_traces"))
                {
                    hasTraces = (devKeys > 0 || enrollKeys > 0 || workAccs > 0);
                }

                ViewModel.Autopilot.HasLocalTraces = hasTraces;
                if (hasTraces)
                {
                    ViewModel.Autopilot.LocalTracesText = (devKeys > 0 || enrollKeys > 0)
                        ? string.Format(LocalizationService.Instance.GetString("ApTracesKeysFound", "PRONAĐENI TRAGOVI ({0} ključeva)"), devKeys + enrollKeys)
                        : LocalizationService.Instance.GetString("ApTracesRegistryFound", "PRONAĐENI TRAGOVI U REGISTRU");
                    ViewModel.Autopilot.LocalTracesBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                }
                else
                {
                    ViewModel.Autopilot.LocalTracesText = LocalizationService.Instance.GetString("ApTracesClean", "Čisto (Nema registarskih tragova)");
                    ViewModel.Autopilot.LocalTracesBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                }

                if (lfNode.ContainsKey("tenant_domain") && !string.IsNullOrEmpty(lfNode["tenant_domain"].AsString()))
                {
                    ViewModel.Autopilot.TenantDomain = lfNode["tenant_domain"].AsString();
                }
                if (lfNode.ContainsKey("tenant_id") && !string.IsNullOrEmpty(lfNode["tenant_id"].AsString()))
                {
                    ViewModel.Autopilot.TenantId = lfNode["tenant_id"].AsString();
                }
                if (lfNode.ContainsKey("assigned_device_name"))
                {
                    ViewModel.Autopilot.AssignedDeviceName = lfNode["assigned_device_name"].AsString(ViewModel.Autopilot.AssignedDeviceName);
                }
                if (lfNode.ContainsKey("azure_ad_status"))
                {
                    ViewModel.Autopilot.AzureAdStatus = lfNode["azure_ad_status"].AsString(ViewModel.Autopilot.AzureAdStatus);
                }
                if (lfNode.ContainsKey("mdm_status"))
                {
                    ViewModel.Autopilot.MdmStatus = lfNode["mdm_status"].AsString(ViewModel.Autopilot.MdmStatus);
                }
                if (lfNode.ContainsKey("org_certificates_count"))
                {
                    ViewModel.Autopilot.OrgCertificatesCount = lfNode["org_certificates_count"].AsInt(ViewModel.Autopilot.OrgCertificatesCount);
                }
            }

            var czNode = root["cloud_ztd"];
            if (czNode.IsObject)
            {
                bool onlineChecked = czNode["online_checked"].AsBool(false);
                bool isRegistered = czNode["is_registered"].AsBool(false);
                string rawMsg = czNode["raw_status_message"].AsString("");
                string errCode = czNode["error_code"].AsString("");

                ViewModel.Autopilot.CloudChecked = onlineChecked;
                ViewModel.Autopilot.CloudCheckedText = onlineChecked
                    ? LocalizationService.Instance.GetString("ApCloudOnlineChecked", "Završena online provera")
                    : LocalizationService.Instance.GetString("ApCloudOffline", "Offline / Nije provereno");
                ViewModel.Autopilot.IsRegistered = isRegistered;
                ViewModel.Autopilot.RawStatusMessage = !string.IsNullOrEmpty(rawMsg) ? rawMsg : "N/A";
                ViewModel.Autopilot.ZtdErrorCode = !string.IsNullOrEmpty(errCode) ? errCode : (onlineChecked ? "OK" : "N/A");

                if (onlineChecked)
                {
                    if (isRegistered)
                    {
                        ViewModel.Autopilot.CloudStatusText = LocalizationService.Instance.GetString("ApCloudRegistered", "REGISTROVAN U MICROSOFT OBLAKU");
                        ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                    }
                    else
                    {
                        ViewModel.Autopilot.CloudStatusText = LocalizationService.Instance.GetString("ApCloudFree", "Slobodan (Nije registrovan u oblaku)");
                        ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                    }
                }
                else
                {
                    ViewModel.Autopilot.CloudStatusText = LocalizationService.Instance.GetString("ApCloudNotVerified", "ZTD Oblak nije proveren (Offline)");
                    ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                }

                if (czNode.ContainsKey("profile_tenant_domain") && !string.IsNullOrEmpty(czNode["profile_tenant_domain"].AsString()))
                {
                    ViewModel.Autopilot.ProfileTenantDomain = czNode["profile_tenant_domain"].AsString();
                    if (string.IsNullOrEmpty(ViewModel.Autopilot.TenantDomain) || ViewModel.Autopilot.TenantDomain == "Nema (Nije dodeljen)")
                    {
                        ViewModel.Autopilot.TenantDomain = ViewModel.Autopilot.ProfileTenantDomain;
                    }
                }
                if (czNode.ContainsKey("profile_tenant_id") && !string.IsNullOrEmpty(czNode["profile_tenant_id"].AsString()))
                {
                    ViewModel.Autopilot.ProfileTenantId = czNode["profile_tenant_id"].AsString();
                    if (string.IsNullOrEmpty(ViewModel.Autopilot.TenantId) || ViewModel.Autopilot.TenantId == "N/A")
                    {
                        ViewModel.Autopilot.TenantId = ViewModel.Autopilot.ProfileTenantId;
                    }
                }
            }
            else
            {
                var ztdNode = root["ztd_inspection"];
                if (ztdNode.IsObject)
                {
                    string ztdStatus = ztdNode["status"].AsString("OFFLINE").ToUpperInvariant();
                    bool checkedZtd = ztdNode["checked"].AsBool(false);
                    int httpStatus = ztdNode["http_status"].AsInt(0);

                    ViewModel.Autopilot.CloudChecked = checkedZtd;
                    ViewModel.Autopilot.CloudCheckedText = checkedZtd
                        ? LocalizationService.Instance.GetString("ApCloudOnlineChecked", "Završena online provera")
                        : LocalizationService.Instance.GetString("ApCloudOffline", "Offline / Nije provereno");
                    ViewModel.Autopilot.ZtdErrorCode = httpStatus > 0 ? string.Format("HTTP {0}", httpStatus) : (checkedZtd ? "Online OK" : "Offline");

                    if (ztdStatus == "ONLINE_MATCH")
                    {
                        ViewModel.Autopilot.IsRegistered = true;
                        ViewModel.Autopilot.CloudStatusText = LocalizationService.Instance.GetString("ApCloudRegistered", "REGISTROVAN U MICROSOFT OBLAKU");
                        ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                    }
                    else if (ztdStatus == "NOT_REGISTERED")
                    {
                        ViewModel.Autopilot.IsRegistered = false;
                        ViewModel.Autopilot.CloudStatusText = LocalizationService.Instance.GetString("ApCloudFree", "Slobodan (Nije registrovan)");
                        ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                    }
                    else
                    {
                        ViewModel.Autopilot.CloudStatusText = string.Format("ZTD: {0}", ztdStatus);
                        ViewModel.Autopilot.CloudStatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    }
                }
            }

            switch (matrixCode)
            {
                case "LOCKED_TENANT":
                    ViewModel.Autopilot.StatusLevel = "CRITICAL";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_LockedTenant", "🔴 ZAKLJUČAN U ORGANIZACIJI (TENANT LOCK)");
                    ViewModel.Autopilot.StatusDescription = string.Format(LocalizationService.Instance.GetString("ApMatrixDesc_LockedTenant", "Uređaj je registrovan u Microsoft oblaku ({0}). OOBE zahteva nalog korporacije!"), ViewModel.Autopilot.TenantDomain);
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_LockedTenant", "OBAVEZNO KONTAKTIRATI KORPORACIJU ILI PRODAVCA ZA DEREGISTRACIJU IZ INTUNE-A.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                    ViewModel.AddLog("CRITICAL", "Autopilot", string.Format(LocalizationService.Instance.GetString("ApLogLockedWarn", "UPOZORENJE: Računar je Autopilot zaključan: {0}"), ViewModel.Autopilot.TenantDomain));
                    break;

                case "RELEASED_LOCAL_TRACES":
                    ViewModel.Autopilot.StatusLevel = "WARNING";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_ReleasedLocalTraces", "🟡 OSLOBOĐEN U OBLAKU, LOKALNI TRAGOVI");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_ReleasedLocalTraces", "Uređaj je oslobođen iz Autopilot baze, ali na disku postoje ostaci prethodne organizacije.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_ReleasedLocalTraces", "Preporučuje se čista instalacija (Clean Install / OOBE reset) radi brisanja lokalnih sertifikata.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    ViewModel.AddLog("WARN", "Autopilot", LocalizationService.Instance.GetString("ApLogTracesWarn", "Autopilot je oslobođen u oblaku, ali postoje lokalni tragovi."));
                    break;

                case "HIDDEN_LOCK":
                    ViewModel.Autopilot.StatusLevel = "CRITICAL";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_HiddenLock", "🔴 SKRIVENA OPASNOST - ZAKLJUČAN U OBLAKU (DISK OBRISAN)");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_HiddenLock", "Lokalni sistem nema tragova, ali je hardverski hash ovog računara i dalje vezan u Microsoft bazi!");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_HiddenLock", "Računar ne sme ići u dalju prodaju dok ga vlasnik tenanta ne oslobodi iz Microsoft Intune portala.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                    try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                    ViewModel.AddLog("CRITICAL", "Autopilot", LocalizationService.Instance.GetString("ApLogHiddenLockCrit", "SKRIVENA OPASNOST: Hardver je vezan u ZTD oblaku iako je disk čist."));
                    break;

                case "CONFIRMED_CLEAN":
                case "CLEAN":
                    ViewModel.Autopilot.StatusLevel = "SUCCESS";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_Clean", "🟢 100% ČISTO - UREĐAJ JE POTPUNO SLOBODAN (POTVRĐENO U OBLAKU)");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_Clean", "Microsoft ZTD oblak potvrdio je da hardver nije registrovan (Event 807). Nema lokalnih tragova.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_Clean", "Uređaj je u potpunosti čist i bezbedan za rad bilo kog korisnika.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                    ViewModel.AddLog("SUCCESS", "Autopilot", LocalizationService.Instance.GetString("ApLogCleanSuccess", "Verifikacija završena: Microsoft ZTD oblak potvrdio slobodan status."));
                    break;

                case "UNVERIFIED_CLEAN":
                    ViewModel.Autopilot.StatusLevel = "INFO";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_UnverifiedClean", "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OBLAK NIJE POTVRDIO)");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_UnverifiedClean", "Lokalni sistem nema tragova, ali tokom brze instalacije Windows OOBE modul nije dobio eksplicitan odgovor iz Microsoft oblaka.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_UnverifiedClean", "Ako je uređaj iz korporativnog izvora, preporučuje se mrežna OOBE provera pre prodaje.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(0, 150, 214);
                    ViewModel.AddLog("INFO", "Autopilot", LocalizationService.Instance.GetString("ApLogUnverifiedCleanInfo", "Lokalni registar čist; status u ZTD oblaku nije eksplicitno potvrđen."));
                    break;

                case "UNVERIFIED_LOCAL_TRACES":
                    ViewModel.Autopilot.StatusLevel = "WARNING";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_UnverifiedLocalTraces", "🟡 UPOZORENJE - LOKALNI TRAGOVI (STATUS U OBLAKU NIJE POTVRĐEN)");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_UnverifiedLocalTraces", "Pronađeni su registarski tragovi organizacije, ali Microsoft ZTD oblak nije vratio eksplicitnu potvrdu.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_UnverifiedLocalTraces", "Povežite stabilnu internet vezu i kliknite 'Osveži Proveru'.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    ViewModel.AddLog("WARN", "Autopilot", LocalizationService.Instance.GetString("ApLogUnverifiedLocalTracesWarn", "Pronađeni lokalni tragovi; status u ZTD oblaku čeka potvrdu."));
                    break;

                case "OFFLINE_LOCAL_TRACES":
                    ViewModel.Autopilot.StatusLevel = "WARNING";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_OfflineLocalTraces", "🟡 OFFLINE - DETEKTOVANI LOKALNI TRAGOVI");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_OfflineLocalTraces", "Nema internet konekcije za proveru oblaka, a pronađeni su registarski tragovi organizacije.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_OfflineLocalTraces", "Povežite internet i kliknite 'Osveži Proveru' za online proveru.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    ViewModel.AddLog("WARN", "Autopilot", LocalizationService.Instance.GetString("ApLogOfflineTracesWarn", "Offline provera: pronađeni lokalni registarski tragovi."));
                    break;

                case "OFFLINE_CLEAN_LOCAL":
                default:
                    ViewModel.Autopilot.StatusLevel = "INFO";
                    ViewModel.Autopilot.StatusTitle = LocalizationService.Instance.GetString("ApMatrixTitle_OfflineCleanLocal", "🔵 USLOVNO ČISTO - NEMA LOKALNIH TRAGOVA (OFFLINE)");
                    ViewModel.Autopilot.StatusDescription = LocalizationService.Instance.GetString("ApMatrixDesc_OfflineCleanLocal", "Lokalni sistem nema tragova, ali je računar bio OFFLINE. Status u oblaku ostaje nepoznat.");
                    ViewModel.Autopilot.RecommendedAction = LocalizationService.Instance.GetString("ApMatrixRec_OfflineCleanLocal", "Za stopostotnu sigurnost, povežite računar na mrežu i ponovite test.");
                    ViewModel.Autopilot.StatusBrush = BaseViewModel.CreateFrozenBrush(0, 122, 204);
                    ViewModel.AddLog("INFO", "Autopilot", LocalizationService.Instance.GetString("ApLogOfflineCleanInfo", "Offline provera: lokalni registar je čist, preporučuje se mrežna verifikacija."));
                    break;
            }

            if (root.ContainsKey("status_title") && !string.IsNullOrWhiteSpace(root["status_title"].AsString()))
            {
                ViewModel.Autopilot.StatusTitle = root["status_title"].AsString();
            }
            if (root.ContainsKey("status_description") && !string.IsNullOrWhiteSpace(root["status_description"].AsString()))
            {
                ViewModel.Autopilot.StatusDescription = root["status_description"].AsString();
            }
            if (root.ContainsKey("status_level") && !string.IsNullOrWhiteSpace(root["status_level"].AsString()))
            {
                ViewModel.Autopilot.StatusLevel = root["status_level"].AsString();
            }
            if (root.ContainsKey("recommended_action") && !string.IsNullOrWhiteSpace(root["recommended_action"].AsString()))
            {
                ViewModel.Autopilot.RecommendedAction = root["recommended_action"].AsString();
            }
            if (root.ContainsKey("summary") && !string.IsNullOrWhiteSpace(root["summary"].AsString()))
            {
                ViewModel.Autopilot.StatusDescription = root["summary"].AsString();
            }

            if (ViewModel.Autopilot.IsAlertActive)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
        }

        #endregion

        #region Asinhrona Detekcija Hardvera (v2.7.1)

        public void DetectHardware()
        {
            RefreshCategories(HwCategory.All);
        }

        private void ExecuteRefreshHardware(object param)
        {
            ViewModel.AddLog("INFO", "Hardver", "Pokrenuto ponovno očitavanje hardverskih komponenti...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DetectHardware();
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("INFO", "Hardver", "Hardverske komponente uspešno osvežene.");
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("ERROR", "Hardver", "Greška pri osvežavanju hardvera: " + ex.Message);
                    });
                }
            });
        }

        #endregion

        #region Real-Time Dijagnostika i Mreža (v2.12.0 - DIAG-01 / NET-01 / NET-02 / NET-03)

        private void StartMetricsTimer()
        {
            if (_diagMetricsTimer != null) return;

            _diagMetricsTimer = new System.Timers.Timer(1000.0);
            _diagMetricsTimer.AutoReset = true;
            _diagMetricsTimer.Elapsed += OnDiagMetricsTick;
            _diagMetricsTimer.Start();

            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateDiagMetrics();
            });
        }

        private void OnDiagMetricsTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateDiagMetrics();
        }

        private void UpdateDiagMetrics()
        {
            if (Interlocked.CompareExchange(ref _diagMetricsGuard, 1, 0) != 0) return;

            try
            {
                // 1. CPU Zauzeće (DIAG-01: GetSystemTimes delta %)
                string cpuUsageStr = null;
                int? finalCpuPct = null;
                try
                {
                    FILETIME idleFt, kernelFt, userFt;
                    if (GetSystemTimes(out idleFt, out kernelFt, out userFt))
                    {
                        ulong idle = idleFt.ToUInt64();
                        ulong kernel = kernelFt.ToUInt64();
                        ulong user = userFt.ToUInt64();

                        if (_hasPrevCpuTimes)
                        {
                            ulong kernelDelta = kernel >= _prevKernelTime ? kernel - _prevKernelTime : 0;
                            ulong userDelta = user >= _prevUserTime ? user - _prevUserTime : 0;
                            ulong idleDelta = idle >= _prevIdleTime ? idle - _prevIdleTime : 0;

                            // Kernel vreme u Windows API-ju već obuhvata Idle vreme
                            ulong totalSys = kernelDelta + userDelta;
                            if (totalSys > 0)
                            {
                                double load = (1.0 - ((double)idleDelta / (double)totalSys)) * 100.0;
                                if (load < 0.0) load = 0.0;
                                if (load > 100.0) load = 100.0;
                                int cpuPct = (int)Math.Round(load);
                                finalCpuPct = cpuPct;
                                cpuUsageStr = string.Format("{0}%", cpuPct);
                            }
                        }

                        _prevIdleTime = idle;
                        _prevKernelTime = kernel;
                        _prevUserTime = user;
                        _hasPrevCpuTimes = true;
                    }
                }
                catch { }

                // 2. RAM Zauzeće (DIAG-01: GlobalMemoryStatusEx)
                string ramUsageStr = null;
                int? finalRamPct = null;
                double finalRamUsedGb = 0.0;
                double finalRamTotalGb = 0.0;
                double finalRamAvailGb = 0.0;
                double finalRamCommitUsedGb = 0.0;
                double finalRamCommitTotalGb = 0.0;
                try
                {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        ulong totalPhys = memStatus.ullTotalPhys;
                        ulong availPhys = memStatus.ullAvailPhys;
                        ulong usedBytes = totalPhys > availPhys ? totalPhys - availPhys : 0;
                        double usedGb = usedBytes / (1024.0 * 1024.0 * 1024.0);
                        double totalGb = totalPhys / (1024.0 * 1024.0 * 1024.0);
                        double availGb = availPhys / (1024.0 * 1024.0 * 1024.0);
                        ulong totalPage = memStatus.ullTotalPageFile;
                        ulong availPage = memStatus.ullAvailPageFile;
                        ulong commitBytes = totalPage > availPage ? totalPage - availPage : 0;
                        uint memLoad = memStatus.dwMemoryLoad;
                        finalRamPct = (int)memLoad;
                        finalRamUsedGb = usedGb;
                        finalRamTotalGb = totalGb;
                        finalRamAvailGb = availGb;
                        finalRamCommitUsedGb = commitBytes / (1024.0 * 1024.0 * 1024.0);
                        finalRamCommitTotalGb = totalPage / (1024.0 * 1024.0 * 1024.0);
                        ramUsageStr = string.Format("{0:0.0} GB / {1:0.0} GB ({2}%)", usedGb, totalGb, memLoad);
                    }
                }
                catch { }

                // 3. Mreža: Protok RX/TX i Preuzeti Paketi (NET-03)
                string downloadedMbStr = null;
                string netRateStr = null;
                double? netTotalMbPerSec = null;
                try
                {
                    long currentTotalBytes = 0;
                    long currentTotalSentBytes = 0;
                    NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (NetworkInterface nic in nics)
                    {
                        if (nic.OperationalStatus == OperationalStatus.Up &&
                            (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                        {
                            try
                            {
                                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                                if (stats != null)
                                {
                                    currentTotalBytes += stats.BytesReceived;
                                    currentTotalSentBytes += stats.BytesSent;
                                }
                            }
                            catch { }
                        }
                    }

                    if (_prevNetBytes >= 0)
                    {
                        double rxMb = 0.0;
                        double txMb = 0.0;
                        if (currentTotalBytes >= _prevNetBytes)
                        {
                            long deltaBytes = currentTotalBytes - _prevNetBytes;
                            _accumulatedNetBytes += deltaBytes;
                            rxMb = deltaBytes / (1024.0 * 1024.0);
                            double totalMbReceived = _accumulatedNetBytes / (1024.0 * 1024.0);
                            downloadedMbStr = string.Format("{0:0.0} MB/s ({1:N0} MB ukupno)", rxMb, totalMbReceived);
                        }
                        if (_prevNetSentBytes >= 0 && currentTotalSentBytes >= _prevNetSentBytes)
                        {
                            long deltaSent = currentTotalSentBytes - _prevNetSentBytes;
                            txMb = deltaSent / (1024.0 * 1024.0);
                        }
                        netTotalMbPerSec = rxMb + txMb;
                        netRateStr = string.Format("RX {0:0.0} MB/s • TX {1:0.0} MB/s", rxMb, txMb);
                    }
                    _prevNetBytes = currentTotalBytes;
                    _prevNetSentBytes = currentTotalSentBytes;
                }
                catch { }

                // 3b. Disk / SSD I/O (PhysicalDisk _Total)
                string diskIoStr = null;
                double? diskTotalMbSec = null;
                double? diskActivePct = null;
                try
                {
                    if (!_diskCountersInitAttempted)
                    {
                        _diskCountersInitAttempted = true;
                        try
                        {
                            _diskReadCounter = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                            _diskWriteCounter = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                            _diskTimeCounter = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                            _diskReadCounter.NextValue();
                            _diskWriteCounter.NextValue();
                            _diskTimeCounter.NextValue();
                        }
                        catch
                        {
                            if (_diskReadCounter != null) { try { _diskReadCounter.Dispose(); } catch { } _diskReadCounter = null; }
                            if (_diskWriteCounter != null) { try { _diskWriteCounter.Dispose(); } catch { } _diskWriteCounter = null; }
                            if (_diskTimeCounter != null) { try { _diskTimeCounter.Dispose(); } catch { } _diskTimeCounter = null; }
                        }
                    }

                    if (_diskReadCounter != null && _diskWriteCounter != null)
                    {
                        double rMb = _diskReadCounter.NextValue() / (1024.0 * 1024.0);
                        double wMb = _diskWriteCounter.NextValue() / (1024.0 * 1024.0);
                        double dPct = _diskTimeCounter != null ? _diskTimeCounter.NextValue() : 0.0;
                        if (dPct < 0.0) dPct = 0.0;
                        if (dPct > 100.0) dPct = 100.0;
                        diskTotalMbSec = rMb + wMb;
                        diskActivePct = dPct;
                        diskIoStr = string.Format("R {0:0.0} MB/s • W {1:0.0} MB/s ({2:0}%)", rMb, wMb, dPct);
                    }
                }
                catch { }

                // 3c. Sistemska statistika: broj procesa i Uptime (svake ~4 sekunde)
                string sysStatsStr = null;
                _diagSysCounter++;
                if (_diagSysCounter >= 4)
                {
                    _diagSysCounter = 0;
                    try
                    {
                        int procCount = System.Diagnostics.Process.GetProcesses().Length;
                        ulong upMs = GetTickCount64();
                        TimeSpan ts = TimeSpan.FromMilliseconds((double)upMs);
                        int totalHours = (ts.Days * 24) + ts.Hours;
                        sysStatsStr = string.Format("Procesi: {0} | Uptime: {1:D2}:{2:D2}:{3:D2}", procCount, totalHours, ts.Minutes, ts.Seconds);
                    }
                    catch { }
                }

                // 4. Mreža: IP Adresa Dodeljena od Rutera (NET-01 - provera na svake 2 sekunde)
                string detectedIp = null;
                string detectedConnType = null;
                string detectedConnStatus = null;
                Brush detectedConnBrush = null;
                _diagNetCounter++;
                if (_diagNetCounter >= 2)
                {
                    _diagNetCounter = 0;
                    try
                    {
                        NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                        foreach (NetworkInterface nic in nics)
                        {
                            if (nic.OperationalStatus != OperationalStatus.Up) continue;
                            if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                                nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;

                            IPInterfaceProperties props = nic.GetIPProperties();
                            if (props == null || props.GatewayAddresses == null || props.GatewayAddresses.Count == 0) continue;

                            bool hasValidGateway = false;
                            foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                            {
                                if (gw.Address != null && gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    string gwStr = gw.Address.ToString();
                                    if (!IPAddress.IsLoopback(gw.Address) && !gwStr.StartsWith("0.0.0.0"))
                                    {
                                        hasValidGateway = true;
                                        break;
                                    }
                                }
                            }
                            if (!hasValidGateway) continue;

                            foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                            {
                                if (uni.Address != null && uni.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    string ipStr = uni.Address.ToString();
                                    if (!IPAddress.IsLoopback(uni.Address) && !ipStr.StartsWith("169.254.") && !ipStr.StartsWith("0.0.0.0"))
                                    {
                                        detectedIp = ipStr;
                                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                                        {
                                            detectedConnType = LocalizationService.Get("WinNetWifiType", "Wi-Fi (Bežična mreža)");
                                            detectedConnStatus = LocalizationService.Get("WinNetWifiActive", "✓ Wi-Fi konekcija aktivna");
                                            detectedConnBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                                        }
                                        else
                                        {
                                            detectedConnType = LocalizationService.Get("WinNetLanType", "Ethernet (LAN kabl)");
                                            detectedConnStatus = LocalizationService.Get("WinNetLanActive", "✓ Ethernet (LAN) aktivan");
                                            detectedConnBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                                        }
                                        break;
                                    }
                                }
                            }
                            if (!string.IsNullOrEmpty(detectedIp)) break;
                        }

                        if (string.IsNullOrEmpty(detectedIp))
                        {
                            detectedConnStatus = LocalizationService.Get("WinNetNoConnection", "⚠ Nema mrežne veze");
                            detectedConnBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                        }
                    }
                    catch { }
                }

                // 4b. Napajanje, Baterija i trenutna potrošnja u W / A (na svake 2 sekunde)
                bool powerUpdated = false;
                bool liveHasBattery = false;
                bool liveIsOnAc = true;
                bool liveIsCharging = false;
                bool liveIsDischarging = false;
                int liveChargePct = 0;
                double liveWatts = 0.0;
                double liveAmps = 0.0;
                double liveVoltageV = 0.0;
                int liveRemainingSec = -1;

                _diagPowerCounter++;
                if (_diagPowerCounter >= 2)
                {
                    _diagPowerCounter = 0;
                    powerUpdated = ReadLivePowerTelemetry(
                        out liveHasBattery,
                        out liveIsOnAc,
                        out liveIsCharging,
                        out liveIsDischarging,
                        out liveChargePct,
                        out liveWatts,
                        out liveAmps,
                        out liveVoltageV,
                        out liveRemainingSec);
                }

                // Ažuriranje UI-ja preko Dispatcher-a
                string finalCpu = cpuUsageStr;
                string finalRam = ramUsageStr;
                int? dispatchCpuPct = finalCpuPct;
                int? dispatchRamPct = finalRamPct;
                double dispatchRamUsedGb = finalRamUsedGb;
                double dispatchRamTotalGb = finalRamTotalGb;
                double dispatchRamAvailGb = finalRamAvailGb;
                double dispatchRamCommitUsedGb = finalRamCommitUsedGb;
                double dispatchRamCommitTotalGb = finalRamCommitTotalGb;
                bool dispatchPowerUpdated = powerUpdated;
                bool dispatchHasBatt = liveHasBattery;
                bool dispatchOnAc = liveIsOnAc;
                bool dispatchCharging = liveIsCharging;
                bool dispatchDischarging = liveIsDischarging;
                int dispatchChargePct = liveChargePct;
                double dispatchWatts = liveWatts;
                double dispatchAmps = liveAmps;
                double dispatchVoltageV = liveVoltageV;
                int dispatchRemainingSec = liveRemainingSec;

                string finalDl = downloadedMbStr;
                string finalNetRate = netRateStr;
                double? dispatchNetTotalMb = netTotalMbPerSec;
                string finalDiskIo = diskIoStr;
                double? dispatchDiskTotalMb = diskTotalMbSec;
                double? dispatchDiskActivePct = diskActivePct;
                string finalSysStats = sysStatsStr;
                string finalIp = detectedIp;
                string finalConn = detectedConnType;
                string finalConnStatus = detectedConnStatus;
                Brush finalConnBrush = detectedConnBrush;

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (!string.IsNullOrEmpty(finalCpu)) ViewModel.CpuUsage = finalCpu;
                    if (!string.IsNullOrEmpty(finalRam)) ViewModel.RamUsage = finalRam;

                    if (dispatchCpuPct.HasValue || dispatchRamPct.HasValue)
                    {
                        int curCpu = dispatchCpuPct.HasValue ? dispatchCpuPct.Value : (int)Math.Round(ViewModel.CpuUsagePercent);
                        int curRam = dispatchRamPct.HasValue ? dispatchRamPct.Value : (int)Math.Round(ViewModel.RamUsagePercent);
                        ViewModel.UpdateCpuRamMonitor(curCpu, dispatchRamUsedGb, dispatchRamTotalGb, curRam);

                        if (dispatchCpuPct.HasValue)
                        {
                            Brush cpuLoadBrush = curCpu >= 85
                                ? MainViewModel.CreateFrozenBrush(239, 68, 68)
                                : (curCpu >= 60 ? MainViewModel.CreateFrozenBrush(245, 158, 11) : MainViewModel.CreateFrozenBrush(56, 189, 248));
                            ViewModel.UpdateHwMonitorSensor("load_cpu", curCpu, "{0:0} %", cpuLoadBrush);
                            ViewModel.LegendCpuLoadText = string.Format("CPU Load ({0}%)", curCpu);
                        }
                        if (dispatchRamPct.HasValue)
                        {
                            Brush ramBrush = curRam >= 85
                                ? MainViewModel.CreateFrozenBrush(239, 68, 68)
                                : (curRam >= 65 ? MainViewModel.CreateFrozenBrush(245, 158, 11) : MainViewModel.CreateFrozenBrush(96, 165, 250));
                            ViewModel.UpdateHwMonitorSensor("load_ram", curRam, "{0:0} %", ramBrush);
                            ViewModel.HwRamAvailableText = string.Format(LocalizationService.Get("WinRamAvailableFmt", "Slobodno: {0:0.0} GB / {1:0.0} GB"), dispatchRamAvailGb, dispatchRamTotalGb);
                            ViewModel.HwRamCommitText = string.Format(LocalizationService.Get("WinRamCommitFmt", "Commit: {0:0.0} / {1:0.0} GB"), dispatchRamCommitUsedGb, dispatchRamCommitTotalGb);
                        }
                    }

                    if (!string.IsNullOrEmpty(finalDiskIo))
                    {
                        ViewModel.HwDiskIoText = finalDiskIo;
                    }
                    if (dispatchDiskActivePct.HasValue)
                    {
                        ViewModel.HwDiskActivePercent = dispatchDiskActivePct.Value;
                    }
                    if (dispatchDiskTotalMb.HasValue)
                    {
                        ViewModel.UpdateHwMonitorSensor("io_disk", dispatchDiskTotalMb.Value, "{0:0.0} MB/s", MainViewModel.CreateFrozenBrush(16, 185, 129));
                    }

                    if (!string.IsNullOrEmpty(finalNetRate))
                    {
                        ViewModel.HwNetRateText = finalNetRate;
                    }
                    if (dispatchNetTotalMb.HasValue)
                    {
                        ViewModel.UpdateHwMonitorSensor("io_net", dispatchNetTotalMb.Value, "{0:0.00} MB/s", MainViewModel.CreateFrozenBrush(56, 189, 248));
                    }

                    if (!string.IsNullOrEmpty(finalSysStats))
                    {
                        ViewModel.HwSystemStatsText = finalSysStats;
                    }

                    if (dispatchPowerUpdated)
                    {
                        ViewModel.UpdatePowerMonitor(
                            dispatchHasBatt,
                            dispatchOnAc,
                            dispatchCharging,
                            dispatchDischarging,
                            dispatchChargePct,
                            dispatchWatts,
                            dispatchAmps,
                            dispatchVoltageV,
                            dispatchRemainingSec);

                        if (dispatchVoltageV > 0.1 || dispatchAmps > 0.01)
                        {
                            ViewModel.HwPowerVoltageText = string.Format("{0:0.00} V  |  {1:0.00} A", dispatchVoltageV, dispatchAmps);
                        }
                        else
                        {
                            ViewModel.HwPowerVoltageText = dispatchOnAc
                                ? LocalizationService.Get("WinPwrAcVoltageNa", "AC Adapter (Napon N/A)")
                                : LocalizationService.Get("WinPwrBattVoltageNa", "Baterija (Napon N/A)");
                        }
                        ViewModel.HwPowerSubText = dispatchHasBatt
                            ? string.Format(LocalizationService.Get("WinPwrBattSubFmt", "Baterija: {0}% ({1})"), dispatchChargePct, dispatchOnAc ? (dispatchCharging ? LocalizationService.Get("WinPwrCharging", "Punjenje") : LocalizationService.Get("WinPwrAcGrid", "AC mreža")) : LocalizationService.Get("WinPwrDischarging", "Pražnjenje"))
                            : LocalizationService.Get("WinPwrDesktopAc", "Desktop / AC napajanje");

                        if (dispatchWatts > 0.05)
                        {
                            ViewModel.UpdateHwMonitorSensor("pwr_watts", dispatchWatts, "{0:0.0} W", MainViewModel.CreateFrozenBrush(245, 158, 11));
                        }
                        else
                        {
                            ViewModel.SetHwMonitorSensorUnavailable("pwr_watts", "N/A");
                        }
                        if (dispatchAmps > 0.01)
                        {
                            ViewModel.UpdateHwMonitorSensor("pwr_amps", dispatchAmps, "{0:0.00} A", MainViewModel.CreateFrozenBrush(245, 158, 11));
                        }
                        else
                        {
                            ViewModel.SetHwMonitorSensorUnavailable("pwr_amps", "N/A");
                        }
                        if (dispatchVoltageV > 0.1)
                        {
                            ViewModel.UpdateHwMonitorSensor("pwr_volts", dispatchVoltageV, "{0:0.00} V", MainViewModel.CreateFrozenBrush(226, 232, 240));
                        }
                        else
                        {
                            ViewModel.SetHwMonitorSensorUnavailable("pwr_volts", "N/A");
                        }

                        ViewModel.RebuildOverviewAlerts();
                    }

                    ViewModel.RefreshHwMonitorKpiSummaries();

                    if (!string.IsNullOrEmpty(finalDl)) ViewModel.DownloadedMb = finalDl;
                    if (!string.IsNullOrEmpty(finalIp)) ViewModel.IpAddress = finalIp;
                    if (!string.IsNullOrEmpty(finalConn)) ViewModel.ConnectionType = finalConn;
                    if (!string.IsNullOrEmpty(finalConnStatus))
                    {
                        ViewModel.ConnectionStatusText = finalConnStatus;
                        ViewModel.ConnectionStatusBrush = finalConnBrush;
                    }
                });

                // 5. Mreža: TrueNAS Latencija & Status (NET-02 - asinhrono na svake 3 sekunde)
                _nasPingCounter++;
                if (_nasPingCounter >= 3 && !_isNasPingRunning)
                {
                    _nasPingCounter = 0;
                    _isNasPingRunning = true;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            using (Ping ping = new Ping())
                            {
                                PingReply reply = ping.Send("192.168.170.254", 1000);
                                if (reply != null && reply.Status == IPStatus.Success)
                                {
                                    Dispatcher.BeginInvoke((Action)delegate
                                    {
                                        ViewModel.NasStatus = LocalizationService.Get("WinNasConnected", "Povezan (192.168.170.254)");
                                        ViewModel.NasLatency = string.Format("{0} ms", reply.RoundtripTime);
                                    });
                                }
                                else
                                {
                                    Dispatcher.BeginInvoke((Action)delegate
                                    {
                                        ViewModel.NasStatus = LocalizationService.Get("WinNasOffline", "Lokalno (Offline)");
                                        ViewModel.NasLatency = "N/A";
                                    });
                                }
                            }
                        }
                        catch
                        {
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                ViewModel.NasStatus = LocalizationService.Get("WinNasOffline", "Lokalno (Offline)");
                                ViewModel.NasLatency = "N/A";
                            });
                        }
                        finally
                        {
                            _isNasPingRunning = false;
                        }
                    });
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _diagMetricsGuard, 0);
            }
        }

        private static bool ReadLivePowerTelemetry(
            out bool hasBattery,
            out bool isOnAc,
            out bool isCharging,
            out bool isDischarging,
            out int chargePercent,
            out double watts,
            out double amps,
            out double voltageV,
            out int lifeRemainingSec)
        {
            hasBattery = false;
            isOnAc = true;
            isCharging = false;
            isDischarging = false;
            chargePercent = -1;
            watts = 0.0;
            amps = 0.0;
            voltageV = 0.0;
            lifeRemainingSec = -1;

            try
            {
                SYSTEM_POWER_STATUS sps;
                if (GetSystemPowerStatus(out sps))
                {
                    hasBattery = (sps.BatteryFlag & 128) == 0 && sps.BatteryFlag != 255;
                    if (sps.ACLineStatus == 0) isOnAc = false;
                    else if (sps.ACLineStatus == 1) isOnAc = true;

                    isCharging = (sps.BatteryFlag & 8) != 0;
                    isDischarging = !isOnAc && hasBattery;
                    if (sps.BatteryLifePercent <= 100)
                    {
                        chargePercent = sps.BatteryLifePercent;
                        hasBattery = true;
                    }
                    lifeRemainingSec = sps.BatteryLifeTime;
                }

                if (hasBattery)
                {
                    double totalChargeMw = 0.0;
                    double totalDischargeMw = 0.0;
                    double sumVoltageMv = 0.0;
                    int voltageCount = 0;
                    bool wmiSawOnline = false;
                    bool wmiOnlineVal = false;
                    bool wmiSawCharging = false;
                    bool wmiSawDischarging = false;

                    try
                    {
                        var scope = new ManagementScope(@"\\.\root\wmi");
                        scope.Connect();
                        using (var searcher = new ManagementObjectSearcher(scope,
                            new ObjectQuery("SELECT PowerOnline, Charging, Discharging, ChargeRate, DischargeRate, Voltage FROM BatteryStatus")))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                if (obj["PowerOnline"] != null)
                                {
                                    wmiSawOnline = true;
                                    if (Convert.ToBoolean(obj["PowerOnline"])) wmiOnlineVal = true;
                                }
                                if (obj["Charging"] != null && Convert.ToBoolean(obj["Charging"])) wmiSawCharging = true;
                                if (obj["Discharging"] != null && Convert.ToBoolean(obj["Discharging"])) wmiSawDischarging = true;

                                uint cRate = 0;
                                if (obj["ChargeRate"] != null && uint.TryParse(obj["ChargeRate"].ToString(), out cRate) && cRate > 0 && cRate < 500000)
                                {
                                    totalChargeMw += cRate;
                                }
                                uint dRate = 0;
                                if (obj["DischargeRate"] != null && uint.TryParse(obj["DischargeRate"].ToString(), out dRate) && dRate > 0 && dRate < 500000)
                                {
                                    totalDischargeMw += dRate;
                                }
                                uint voltMv = 0;
                                if (obj["Voltage"] != null && uint.TryParse(obj["Voltage"].ToString(), out voltMv) && voltMv >= 5000 && voltMv <= 30000)
                                {
                                    sumVoltageMv += voltMv;
                                    voltageCount++;
                                }
                            }
                        }
                    }
                    catch { }

                    if (wmiSawOnline) isOnAc = wmiOnlineVal;
                    if (wmiSawCharging) isCharging = true;
                    if (wmiSawDischarging) isDischarging = true;

                    if (totalChargeMw > 0.0)
                    {
                        watts = totalChargeMw / 1000.0;
                        isCharging = true;
                        isDischarging = false;
                    }
                    else if (totalDischargeMw > 0.0)
                    {
                        watts = totalDischargeMw / 1000.0;
                        isDischarging = true;
                        if (!isOnAc) isCharging = false;
                    }

                    if (voltageCount > 0)
                    {
                        double avgMv = sumVoltageMv / (double)voltageCount;
                        voltageV = avgMv / 1000.0;
                        if (watts > 0.0)
                        {
                            amps = (watts * 1000.0) / avgMv;
                        }
                    }

                    if (chargePercent < 0)
                    {
                        try
                        {
                            using (var win32Bat = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                            {
                                foreach (ManagementObject obj in win32Bat.Get())
                                {
                                    if (obj["EstimatedChargeRemaining"] != null)
                                    {
                                        int pct = 0;
                                        if (int.TryParse(obj["EstimatedChargeRemaining"].ToString(), out pct) && pct >= 0 && pct <= 100)
                                        {
                                            chargePercent = pct;
                                        }
                                    }
                                    if (obj["BatteryStatus"] != null)
                                    {
                                        uint bs = 0;
                                        if (uint.TryParse(obj["BatteryStatus"].ToString(), out bs))
                                        {
                                            if (bs == 1) { isOnAc = false; isDischarging = true; isCharging = false; }
                                            else if (bs == 2) { isOnAc = true; isCharging = true; isDischarging = false; }
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (chargePercent < 0) chargePercent = 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Asinhrona Detekcija Drajvera i Uređaja (v2.7.3 - TASK-113)

        private void ExecuteRefreshDrivers(object param)
        {
            if (ViewModel.Drivers.IsScanning) return;
            ViewModel.AddLog("INFO", "Drajveri", "Pokrenuto osvežavanje stanja drajvera i PnP uređaja...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DetectDrivers();
                }
                catch { }
            });
        }

        private void ExecuteOpenDeviceManager(object param)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "devmgmt.msc",
                    UseShellExecute = true
                });
                ViewModel.AddLog("INFO", "Drajveri", "Otvoren Windows Device Manager (devmgmt.msc).");
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "Drajveri", "Greška pri otvaranju Device Managera: " + ex.Message);
            }
        }

        private void ExecuteCopyHwid(object param)
        {
            string hwid = param as string;
            if (string.IsNullOrEmpty(hwid)) return;
            try
            {
                Thread t = new Thread((ThreadStart)delegate
                {
                    try
                    {
                        Clipboard.SetText(hwid);
                    }
                    catch { }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ViewModel.AddLog("INFO", "Drajveri", "Hardware ID kopiran u Clipboard: " + hwid);
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "Drajveri", "Greška pri kopiranju HWID-ja: " + ex.Message);
            }
        }

        #region Faza 2B (GWADT-TASK-115) - Dinamička i Grupna Instalacija OEM Drajvera

        private string GetServisHubBaseUrl()
        {
            if (_telemetryService != null && !string.IsNullOrEmpty(_telemetryService.HubUrl))
            {
                return _telemetryService.HubUrl.TrimEnd('/');
            }

            string[] candidates = new string[]
            {
                "http://192.168.170.254:8080",
                "http://192.168.170.250:8080",
                "http://127.0.0.1:8080",
                "http://192.168.88.50:8080"
            };

            foreach (var cand in candidates)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(cand + "/api/status");
                    req.Timeout = 600;
                    req.Method = "GET";
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode == 200)
                        {
                            return cand;
                        }
                    }
                }
                catch { }
            }

            return "http://192.168.170.254:8080";
        }

        private void ExecuteInstallDriver(object param)
        {
            DriverItemViewModel item = param as DriverItemViewModel;
            if (item == null || item.IsInstalling || item.IsResolved) return;

            item.IsInstalling = true;
            item.CanInstall = false;
            item.InstallStatusText = LocalizationService.Get("WinDrvConnectingHub", "Povezivanje sa ServisHub...");
            item.InstallProgressPercent = 5;
            item.InstallSpeedText = "";

            Task.Factory.StartNew(delegate
            {
                InstallSingleDriverInternal(item, true);
            });
        }

        private static string SanitizeHardwareIdentityValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string trimmed = value.Trim();
            if (IsPlaceholderIdentity(trimmed)) return "";
            string lower = trimmed.ToLowerInvariant();
            if (lower.Length == 0 || lower == "n/a" || lower == "none" ||
                lower.StartsWith("učitavanje") || lower.StartsWith("detekcija") ||
                lower.StartsWith("nepoznat") || lower.StartsWith("greška"))
            {
                return "";
            }
            return trimmed;
        }

        private static string PostDriverResolveJson(string resolveUrl, byte[] reqBytes)
        {
            var httpReq = (HttpWebRequest)WebRequest.Create(resolveUrl);
            httpReq.Method = "POST";
            httpReq.ContentType = "application/json; charset=utf-8";
            httpReq.Timeout = 120000;
            httpReq.ReadWriteTimeout = 120000;
            httpReq.ContentLength = reqBytes.Length;

            using (var reqStream = httpReq.GetRequestStream())
            {
                reqStream.Write(reqBytes, 0, reqBytes.Length);
            }

            using (var httpResp = (HttpWebResponse)httpReq.GetResponse())
            using (var reader = new StreamReader(httpResp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private bool InstallSingleDriverInternal(DriverItemViewModel item, bool triggerRescan)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    item.IsInstalling = true;
                    item.CanInstall = false;
                    item.InstallStatusText = LocalizationService.Get("WinDrvConnectingHub", "Povezivanje sa ServisHub...");
                    item.InstallProgressPercent = 10;
                });

                string hubUrl = GetServisHubBaseUrl();
                string resolveUrl = hubUrl + "/api/drivers/resolve";

                string resolvedModel = SanitizeHardwareIdentityValue(ViewModel != null ? ViewModel.ComputerModel : "");
                if (string.IsNullOrEmpty(resolvedModel) && ViewModel != null && ViewModel.Autopilot != null)
                {
                    resolvedModel = SanitizeHardwareIdentityValue(ViewModel.Autopilot.HwModel);
                }
                if (string.IsNullOrEmpty(resolvedModel) && ViewModel != null && ViewModel.HardwareInfo != null)
                {
                    resolvedModel = SanitizeHardwareIdentityValue(ViewModel.HardwareInfo.MotherboardModel);
                }

                string resolvedSerial = SanitizeHardwareIdentityValue(ViewModel != null ? ViewModel.SerialNumber : "");
                if (string.IsNullOrEmpty(resolvedSerial) && ViewModel != null && ViewModel.Autopilot != null)
                {
                    resolvedSerial = SanitizeHardwareIdentityValue(ViewModel.Autopilot.HwSerialNumber);
                }
                if (string.IsNullOrEmpty(resolvedSerial) && ViewModel != null && ViewModel.HardwareInfo != null)
                {
                    resolvedSerial = SanitizeHardwareIdentityValue(ViewModel.HardwareInfo.SerialNumber);
                }

                // 1. Priprema JSON zahteva za POST /api/drivers/resolve
                var reqDict = new Dictionary<string, object>();
                var hwidList = new List<string>();
                if (!string.IsNullOrEmpty(item.HardwareId))
                {
                    hwidList.Add(item.HardwareId);
                }
                if (!string.IsNullOrEmpty(item.DeviceId) && !hwidList.Contains(item.DeviceId))
                {
                    hwidList.Add(item.DeviceId);
                }
                reqDict["hwids"] = hwidList;
                reqDict["device_name"] = item.Name ?? "UnknownDevice";
                reqDict["manufacturer"] = item.Manufacturer ?? "";
                reqDict["model"] = resolvedModel;
                reqDict["serial_number"] = resolvedSerial;
                reqDict["async_download"] = true;

                string jsonReq = JsonHelper.Serialize(reqDict);
                byte[] reqBytes = Encoding.UTF8.GetBytes(jsonReq);

                string respJson = PostDriverResolveJson(resolveUrl, reqBytes);
                JsonNode rootNode = JsonHelper.Parse(respJson);
                JsonNode resultsNode = rootNode["results"];
                if (resultsNode.IsNull || resultsNode.Count == 0)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallStatusText = LocalizationService.Get("WinDrvNotFoundOem", "Nije pronađen na OEM katalogu");
                        item.IsInstalling = false;
                        item.CanInstall = true;
                        ViewModel.AddLog("WARN", "Drajveri", string.Format("Drajver za '{0}' nije pronađen na ServisHub-u.", item.Name));
                    });
                    return false;
                }

                JsonNode firstRes = resultsNode[0];
                string status = firstRes["status"].AsString("");

                // Ako ServisHub preuzima veliki OEM paket sa Microsoft CDN-a u pozadini, čekaj završetak bez 30s timeouta
                Stopwatch pollSw = Stopwatch.StartNew();
                bool asyncLogEmitted = false;
                while (status.Equals("downloading", StringComparison.OrdinalIgnoreCase) && pollSw.Elapsed.TotalSeconds < 900)
                {
                    int retryAfterSec = firstRes["retry_after_sec"].AsInt(5);
                    if (retryAfterSec < 2) retryAfterSec = 2;
                    if (retryAfterSec > 15) retryAfterSec = 15;
                    int elapsedSec = (int)pollSw.Elapsed.TotalSeconds;

                    bool logNow = !asyncLogEmitted;
                    asyncLogEmitted = true;
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallStatusText = elapsedSec > 0
                            ? string.Format(LocalizationService.Get("WinDrvCachingElapsed", "ServiceHub preuzima OEM paket na keš ({0}s)..."), elapsedSec)
                            : LocalizationService.Get("WinDrvCaching", "ServiceHub preuzima OEM paket na keš...");
                        item.InstallProgressPercent = 12;
                        if (logNow)
                        {
                            ViewModel.AddLog("INFO", "Drajveri", string.Format("ServiceHub preuzima paket za '{0}' u pozadini — čekam keširanje...", item.Name));
                        }
                    });

                    Thread.Sleep(retryAfterSec * 1000);
                    respJson = PostDriverResolveJson(resolveUrl, reqBytes);
                    rootNode = JsonHelper.Parse(respJson);
                    resultsNode = rootNode["results"];
                    if (resultsNode.IsNull || resultsNode.Count == 0)
                    {
                        status = "not_found";
                        break;
                    }
                    firstRes = resultsNode[0];
                    status = firstRes["status"].AsString("");
                }

                if (status.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    string dlUrl = firstRes["download_url"].AsString("");
                    if (string.IsNullOrEmpty(dlUrl))
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.InstallStatusText = LocalizationService.Get("WinDrvInvalidUrl", "Neispravan download URL");
                            item.IsInstalling = false;
                            item.CanInstall = true;
                        });
                        return false;
                    }

                    if (dlUrl.StartsWith("/"))
                    {
                        dlUrl = hubUrl + dlUrl;
                    }

                    string drvTitle = firstRes["title"].AsString(item.Name);
                    string drvId = firstRes["driver_id"].AsString("");
                    if (string.IsNullOrEmpty(drvId)) drvId = Guid.NewGuid().ToString("N");

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallStatusText = LocalizationService.Get("WinDrvDownloadingOem", "Preuzimanje zvaničnog OEM drajvera...");
                        item.InstallProgressPercent = 15;
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Preuzimanje drajvera za '{0}' ({1})...", item.Name, drvTitle));
                    });

                    // Privremeni folder C:\setup\Drivers\Pending\
                    string pendingDir = @"C:\setup\Drivers\Pending";
                    if (!Directory.Exists(pendingDir))
                    {
                        Directory.CreateDirectory(pendingDir);
                    }

                    string cabPath = Path.Combine(pendingDir, string.Format("driver_{0}.cab", drvId));
                    string extractDir = Path.Combine(pendingDir, string.Format("ext_{0}", drvId));

                    // 2. Preuzimanje .cab arhive prateći procenat i brzinu u MB/s
                    using (WebClient wc = new WebClient())
                    {
                        DateTime lastTick = DateTime.Now;
                        long lastBytes = 0;

                        wc.DownloadProgressChanged += (s, e) =>
                        {
                            double elapsedSec = (DateTime.Now - lastTick).TotalSeconds;
                            string speedStr = "";
                            if (elapsedSec >= 0.4)
                            {
                                double bytesDiff = e.BytesReceived - lastBytes;
                                double speedMb = (bytesDiff / (1024.0 * 1024.0)) / elapsedSec;
                                speedStr = string.Format("{0:0.0} MB/s", speedMb);
                                lastTick = DateTime.Now;
                                lastBytes = e.BytesReceived;
                            }

                            int scaledPercent = 15 + (int)(e.ProgressPercentage * 0.65);

                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                item.InstallProgressPercent = scaledPercent;
                                if (!string.IsNullOrEmpty(speedStr))
                                {
                                    item.InstallSpeedText = speedStr;
                                }
                            });
                        };

                        var waitHandle = new ManualResetEvent(false);
                        Exception dlEx = null;

                        wc.DownloadFileCompleted += (s, e) =>
                        {
                            if (e.Error != null) dlEx = e.Error;
                            waitHandle.Set();
                        };

                        wc.DownloadFileAsync(new Uri(dlUrl), cabPath);
                        waitHandle.WaitOne();

                        if (dlEx != null)
                        {
                            throw dlEx;
                        }
                    }

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallProgressPercent = 85;
                        item.InstallSpeedText = "";
                        item.InstallStatusText = LocalizationService.Get("WinDrvInstallingPnputil", "Instalacija drajvera (pnputil)...");
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Raspakivanje i instalacija drajvera za '{0}'...", item.Name));
                    });

                    // 3. Raspakivanje pomoću expand.exe -R
                    if (!Directory.Exists(extractDir))
                    {
                        Directory.CreateDirectory(extractDir);
                    }

                    ProcessStartInfo psiExpand = new ProcessStartInfo
                    {
                        FileName = "expand.exe",
                        Arguments = string.Format("-R \"{0}\" -F:* \"{1}\"", cabPath, extractDir),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process procExpand = Process.Start(psiExpand))
                    {
                        if (procExpand != null)
                        {
                            procExpand.WaitForExit(45000);
                        }
                    }

                    // 4. Instalacija drajvera pomoću pnputil.exe
                    ProcessStartInfo psiPnp = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = string.Format("/add-driver \"{0}\\*.inf\" /install /subdirs", extractDir),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    int pnpExitCode = -1;
                    using (Process procPnp = Process.Start(psiPnp))
                    {
                        if (procPnp != null)
                        {
                            procPnp.WaitForExit(90000);
                            pnpExitCode = procPnp.ExitCode;
                        }
                    }

                    // 5. Provera rezultata: 0 = Uspeh, 3010 = Uspeh (potreban restart)
                    if (pnpExitCode == 0 || pnpExitCode == 3010)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.InstallProgressPercent = 100;
                            item.InstallStatusText = LocalizationService.Get("WinDrvInstallOk", "✓ Uspešno instalirano!");
                            item.IsResolved = true;
                            item.IsInstalling = false;
                            item.CanInstall = false;
                            ViewModel.AddLog("INFO", "Drajveri", string.Format("✓ Drajver za '{0}' je uspešno instaliran (pnputil izlazni kod: {1}).", item.Name, pnpExitCode));
                        });

                        Thread.Sleep(1000);

                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            ViewModel.Drivers.MissingDevices.Remove(item);
                            ViewModel.Drivers.MissingDevicesCount = ViewModel.Drivers.MissingDevices.Count;

                            if (ViewModel.Drivers.MissingDevicesCount == 0)
                            {
                                ViewModel.Drivers.HasMissingDevices = false;
                                ViewModel.Drivers.MissingSummaryText = LocalizationService.Get("WinDrvAllInstalledSummary", "✅ Svi drajveri su uspešno instalirani! (0 problematičnih uređaja)");
                                ViewModel.Drivers.MissingSummaryBrush = BaseViewModel.CreateFrozenBrush(63, 185, 80);
                            }
                            else
                            {
                                ViewModel.Drivers.MissingSummaryText = string.Format(LocalizationService.Get("WinDrvMissingSummary", "⚠ Pronađeno je {0} uređaja bez odgovarajućeg drajvera ili sa greškom!"), ViewModel.Drivers.MissingDevicesCount);
                            }

                            if (triggerRescan)
                            {
                                DetectDrivers();
                            }
                        });

                        return true;
                    }
                    else
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.InstallStatusText = string.Format(LocalizationService.Get("WinDrvInstallErrCode", "Greška instalacije (kod {0})"), pnpExitCode);
                            item.IsInstalling = false;
                            item.CanInstall = true;
                            ViewModel.AddLog("ERROR", "Drajveri", string.Format("Neuspešna instalacija drajvera za '{0}'. pnputil exit code: {1}", item.Name, pnpExitCode));
                        });
                        return false;
                    }
                }
                else
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallStatusText = LocalizationService.Get("WinDrvNotFoundOem", "Nije pronađen na OEM katalogu");
                        item.IsInstalling = false;
                        item.CanInstall = true;
                        ViewModel.AddLog("WARN", "Drajveri", string.Format("Drajver za '{0}' nije pronađen u OEM katalogu.", item.Name));
                    });
                    return false;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    item.InstallStatusText = LocalizationService.Get("WinDrvConnectHubErr", "Greška pri povezivanju sa ServisHub");
                    item.IsInstalling = false;
                    item.CanInstall = true;
                    ViewModel.AddLog("ERROR", "Drajveri", string.Format("Izuzetak pri instalaciji drajvera za '{0}': {1}", item.Name, ex.Message));
                });
                return false;
            }
        }

        private void ExecuteResolveAllDrivers(object param)
        {
            if (ViewModel.Drivers.IsBatchResolving) return;

            var missingCopy = new List<DriverItemViewModel>(ViewModel.Drivers.MissingDevices);
            if (missingCopy.Count == 0)
            {
                ViewModel.AddLog("INFO", "Drajveri", "Nema nedostajućih drajvera za instalaciju.");
                return;
            }

            ViewModel.Drivers.IsBatchResolving = true;
            ViewModel.Drivers.BatchProgressPercent = 0;
            ViewModel.Drivers.BatchStatusText = string.Format(LocalizationService.Get("WinDrvBatchStart", "Započinjem rešavanje {0} nedostajućih drajvera..."), missingCopy.Count);
            ViewModel.AddLog("INFO", "Drajveri", string.Format("Pokrenuto grupno rešavanje {0} nedostajućih drajvera...", missingCopy.Count));
            NotifyHwTestDriverStateChanged(false);

            Task.Factory.StartNew(delegate
            {
                int total = missingCopy.Count;
                int successfulCount = 0;

                for (int i = 0; i < total; i++)
                {
                    DriverItemViewModel item = missingCopy[i];
                    if (item.IsResolved)
                    {
                        successfulCount++;
                        continue;
                    }

                    int currentIdx = i + 1;
                    int percent = (int)(((double)i / total) * 100.0);

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.Drivers.BatchProgressPercent = percent;
                        ViewModel.Drivers.BatchStatusText = string.Format(LocalizationService.Get("WinDrvBatchProgress", "Instalacija {0}/{1}: {2}..."), currentIdx, total, item.Name);
                    });

                    bool success = InstallSingleDriverInternal(item, false);
                    if (success)
                    {
                        successfulCount++;
                    }

                    Thread.Sleep(500);
                }

                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.BatchProgressPercent = 100;
                    ViewModel.Drivers.BatchStatusText = string.Format(LocalizationService.Get("WinDrvBatchDone", "Grupna instalacija drajvera završena ({0}/{1} uspešno)."), successfulCount, total);
                    ViewModel.AddLog("INFO", "Drajveri", string.Format("Grupna instalacija drajvera završena: {0} od {1} uspešno instalirano.", successfulCount, total));
                });

                Thread.Sleep(1500);

                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsBatchResolving = false;
                    DetectDrivers();
                    NotifyHwTestDriverStateChanged(true);
                });
            });
        }

        private void ExecuteWindowsUpdateFallback(object param)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:windowsupdate",
                    UseShellExecute = true
                });
                ViewModel.AddLog("INFO", "Drajveri", "Otvoren Windows Update za traženje opcionih drajvera.");
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("ERROR", "Drajveri", "Greška pri otvaranju Windows Update: " + ex.Message);
            }
        }

        #region Faza 2 (GWADT-TASK-117) - Provera i nadogradnja zvaničnih OEM drajvera

        private bool IsKeyHardwareDevice(string pnpClass, string name)
        {
            if (string.IsNullOrEmpty(pnpClass)) return false;
            string cls = pnpClass.ToUpperInvariant().Trim();
            string n = (name ?? "").ToUpperInvariant().Trim();

            // 1. Grafičke kartice (Display)
            if (cls == "DISPLAY")
            {
                return true;
            }

            // 2. Mrežni adapteri (Net) - isključiti virtualne uređaje
            if (cls == "NET")
            {
                if (n.Contains("WAN MINIPORT") ||
                    n.Contains("MICROSOFT KERNEL") ||
                    n.Contains("BLUETOOTH DEVICE") ||
                    n.Contains("VIRTUAL") ||
                    n.Contains("LOOPBACK") ||
                    n.Contains("TAP") ||
                    n.Contains("VPN") ||
                    n.Contains("VMWARE") ||
                    n.Contains("HYPER-V"))
                {
                    return false;
                }
                return true;
            }

            // 3. Zvučne kartice (Media, Sound, AudioEndpoint)
            if (cls == "MEDIA" || cls == "SOUND" || cls == "AUDIOENDPOINT")
            {
                return true;
            }

            // 4. Bluetooth
            if (cls == "BLUETOOTH")
            {
                return true;
            }

            return false;
        }

        private bool CheckSingleDriverUpdateInternal(DriverItemViewModel item)
        {
            if (item == null) return false;

            Dispatcher.BeginInvoke((Action)delegate
            {
                item.IsCheckingUpdate = true;
            });

            try
            {
                string hubUrl = GetServisHubBaseUrl();
                string checkUrl = hubUrl + "/api/drivers/check_update";

                var reqDict = new Dictionary<string, object>();
                var hwidList = new List<string>();
                if (!string.IsNullOrEmpty(item.HardwareId) && item.HardwareId != "N/A")
                {
                    hwidList.Add(item.HardwareId);
                }
                if (!string.IsNullOrEmpty(item.DeviceId) && !hwidList.Contains(item.DeviceId))
                {
                    hwidList.Add(item.DeviceId);
                }
                reqDict["hwids"] = hwidList;
                reqDict["current_version"] = item.DriverVersion ?? "";
                reqDict["current_date"] = item.DriverDate ?? "";
                reqDict["device_name"] = item.Name ?? "UnknownDevice";

                string jsonReq = JsonHelper.Serialize(reqDict);
                byte[] reqBytes = Encoding.UTF8.GetBytes(jsonReq);

                var httpReq = (HttpWebRequest)WebRequest.Create(checkUrl);
                httpReq.Method = "POST";
                httpReq.ContentType = "application/json; charset=utf-8";
                httpReq.Timeout = 40000;
                httpReq.ContentLength = reqBytes.Length;

                using (var reqStream = httpReq.GetRequestStream())
                {
                    reqStream.Write(reqBytes, 0, reqBytes.Length);
                }

                string respJson = "";
                using (var httpResp = (HttpWebResponse)httpReq.GetResponse())
                using (var reader = new StreamReader(httpResp.GetResponseStream(), Encoding.UTF8))
                {
                    respJson = reader.ReadToEnd();
                }

                JsonNode rootNode = JsonHelper.Parse(respJson);
                bool updateAvail = rootNode["update_available"].AsBool(false);
                string latestVer = rootNode["latest_version"].AsString("");
                string latestDate = rootNode["latest_date"].AsString("");
                string drvId = rootNode["driver_id"].AsString("");

                Dispatcher.BeginInvoke((Action)delegate
                {
                    item.IsCheckingUpdate = false;
                    if (updateAvail)
                    {
                        item.HasUpdateAvailable = true;
                        item.IsUpToDate = false;
                        item.LatestVersion = latestVer;
                        item.LatestDate = latestDate;
                        item.TargetDriverId = drvId;
                        item.UpdateBadgeText = string.Format(LocalizationService.Get("WinDrvUpdateAvailableFmt", "Dostupna nova verzija: {0}"), latestVer);
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Pronađeno OEM ažuriranje za '{0}': v{1} ({2}).", item.Name, latestVer, latestDate));
                    }
                    else
                    {
                        item.HasUpdateAvailable = false;
                        item.IsUpToDate = true;
                        item.UpdateBadgeText = LocalizationService.Get("WinDrvLatestBadge", "✓ Najnoviji drajver");
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Drajver za '{0}' je ažuran (v{1}).", item.Name, item.DriverVersion));
                    }
                });

                return updateAvail;
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    item.IsCheckingUpdate = false;
                    ViewModel.AddLog("WARN", "Drajveri", string.Format("Provera drajvera za '{0}' nije uspela: {1}", item.Name, ex.Message));
                });
                return false;
            }
        }

        private void ExecuteCheckUpdate(object param)
        {
            DriverItemViewModel item = param as DriverItemViewModel;
            if (item == null || item.IsCheckingUpdate) return;

            Task.Factory.StartNew(delegate
            {
                CheckSingleDriverUpdateInternal(item);
            });
        }

        private void ExecuteUpgradeDriver(object param)
        {
            DriverItemViewModel item = param as DriverItemViewModel;
            if (item == null || item.IsCheckingUpdate) return;

            string targetId = item.TargetDriverId;
            if (string.IsNullOrEmpty(targetId))
            {
                ViewModel.AddLog("WARN", "Drajveri", string.Format("Nije specificiran TargetDriverId za nadogradnju '{0}'.", item.Name));
                return;
            }

            Dispatcher.BeginInvoke((Action)delegate
            {
                item.IsCheckingUpdate = true;
                item.UpdateBadgeText = LocalizationService.Get("WinDrvUpgradingBadge", "Nadogradnja u toku...");
                ViewModel.AddLog("INFO", "Drajveri", string.Format("Započinjem preuzimanje i nadogradnju zvaničnog OEM drajvera za '{0}' (Target ID: {1})...", item.Name, targetId));
            });

            Task.Factory.StartNew(delegate
            {
                try
                {
                    string hubUrl = GetServisHubBaseUrl();
                    string dlUrl = hubUrl + "/api/drivers/download?id=" + targetId;

                    string pendingDir = @"C:\setup\Drivers\Pending";
                    if (!Directory.Exists(pendingDir))
                    {
                        Directory.CreateDirectory(pendingDir);
                    }

                    string cabPath = Path.Combine(pendingDir, string.Format("upgrade_{0}.cab", targetId));
                    string extractDir = Path.Combine(pendingDir, string.Format("ext_upg_{0}", targetId));

                    // 1. Preuzimanje .cab arhive
                    using (WebClient wc = new WebClient())
                    {
                        var waitHandle = new ManualResetEvent(false);
                        Exception dlEx = null;

                        wc.DownloadFileCompleted += delegate(object s, AsyncCompletedEventArgs e)
                        {
                            if (e.Error != null) dlEx = e.Error;
                            waitHandle.Set();
                        };

                        wc.DownloadFileAsync(new Uri(dlUrl), cabPath);
                        waitHandle.WaitOne();

                        if (dlEx != null)
                        {
                            throw dlEx;
                        }
                    }

                    // 2. Raspakivanje pomoću expand.exe -R
                    if (!Directory.Exists(extractDir))
                    {
                        Directory.CreateDirectory(extractDir);
                    }

                    ProcessStartInfo psiExpand = new ProcessStartInfo
                    {
                        FileName = "expand.exe",
                        Arguments = string.Format("-R \"{0}\" -F:* \"{1}\"", cabPath, extractDir),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process procExpand = Process.Start(psiExpand))
                    {
                        if (procExpand != null)
                        {
                            procExpand.WaitForExit(45000);
                        }
                    }

                    // 3. Instalacija preko pnputil.exe
                    ProcessStartInfo psiPnp = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = string.Format("/add-driver \"{0}\\*.inf\" /install /subdirs", extractDir),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    int pnpExitCode = -1;
                    using (Process procPnp = Process.Start(psiPnp))
                    {
                        if (procPnp != null)
                        {
                            procPnp.WaitForExit(90000);
                            pnpExitCode = procPnp.ExitCode;
                        }
                    }

                    if (pnpExitCode == 0 || pnpExitCode == 3010)
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.IsCheckingUpdate = false;
                            item.HasUpdateAvailable = false;
                            item.IsUpToDate = true;
                            item.UpdateBadgeText = LocalizationService.Get("WinDrvLatestBadge", "✓ Najnoviji drajver");
                            if (!string.IsNullOrEmpty(item.LatestVersion))
                            {
                                item.DriverVersion = item.LatestVersion;
                            }
                            if (!string.IsNullOrEmpty(item.LatestDate))
                            {
                                item.DriverDate = item.LatestDate;
                            }
                            ViewModel.AddLog("INFO", "Drajveri", string.Format("✓ Drajver za '{0}' je uspešno nadograđen (pnputil kod: {1}).", item.Name, pnpExitCode));

                            // Osvežavanje stabla drajvera
                            DetectDrivers();
                        });
                    }
                    else
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.IsCheckingUpdate = false;
                            ViewModel.AddLog("ERROR", "Drajveri", string.Format("Neuspešna nadogradnja drajvera za '{0}'. pnputil kod: {1}", item.Name, pnpExitCode));
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.IsCheckingUpdate = false;
                        ViewModel.AddLog("ERROR", "Drajveri", string.Format("Greška tokom nadogradnje drajvera za '{0}': {1}", item.Name, ex.Message));
                    });
                }
            });
        }

        private void ExecuteCheckKeyDrivers(object param)
        {
            if (ViewModel.Drivers.IsCheckingKeyDrivers) return;

            var keyDevices = new List<DriverItemViewModel>();
            if (ViewModel.Drivers.Categories != null)
            {
                foreach (var cat in ViewModel.Drivers.Categories)
                {
                    if (cat.AllDevices != null)
                    {
                        foreach (var dev in cat.AllDevices)
                        {
                            if (dev.IsKeyDevice && !keyDevices.Contains(dev))
                            {
                                keyDevices.Add(dev);
                            }
                        }
                    }
                }
            }

            if (keyDevices.Count == 0)
            {
                ViewModel.Drivers.KeyDriversCheckStatus = LocalizationService.Get("WinDrvKeyNoneFound", "Nema pronađenih ključnih uređaja");
                ViewModel.AddLog("INFO", "Drajveri", "Nema detektovanih ključnih hardverskih uređaja (GPU, Mreža, Zvuk, Bluetooth) za proveru.");
                return;
            }

            ViewModel.Drivers.IsCheckingKeyDrivers = true;
            ViewModel.Drivers.KeyDriversCheckStatus = string.Format(LocalizationService.Get("WinDrvKeyCheckStart", "Započinjem proveru 0/{0} ključnih drajvera..."), keyDevices.Count);
            ViewModel.AddLog("INFO", "Drajveri", string.Format("Pokrenuta provera ažuriranja za {0} ključnih hardverskih uređaja...", keyDevices.Count));

            Task.Factory.StartNew(delegate
            {
                int total = keyDevices.Count;
                int updatesFound = 0;

                for (int i = 0; i < total; i++)
                {
                    DriverItemViewModel dev = keyDevices[i];
                    int currentIdx = i + 1;

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.Drivers.KeyDriversCheckStatus = string.Format(LocalizationService.Get("WinDrvKeyCheckProgress", "Provera {0}/{1}: {2}..."), currentIdx, total, dev.Name);
                    });

                    bool hasUpdate = CheckSingleDriverUpdateInternal(dev);
                    if (hasUpdate)
                    {
                        updatesFound++;
                    }

                    Thread.Sleep(300);
                }

                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsCheckingKeyDrivers = false;
                    if (updatesFound > 0)
                    {
                        ViewModel.Drivers.KeyDriversCheckStatus = string.Format(LocalizationService.Get("WinDrvKeyUpdatesFound", "Pronađeno {0} ažuriranja ključnih drajvera!"), updatesFound);
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Završena provera ključnih drajvera: pronađeno {0} novijih verzija drajvera.", updatesFound));
                    }
                    else
                    {
                        ViewModel.Drivers.KeyDriversCheckStatus = LocalizationService.Get("WinDrvKeyAllUpToDate", "Svi ključni drajveri su ažurni!");
                        ViewModel.AddLog("INFO", "Drajveri", "Završena provera: svi ključni hardverski drajveri su ažurni.");
                    }
                });
            });
        }

        #endregion

        #endregion

        private static string GetErrorCodeDescription(uint code)
        {
            switch (code)
            {
                case 1: return LocalizationService.Get("WinErrCode1", "Kod 1: Uređaj nije konfigurisan");
                case 3: return LocalizationService.Get("WinErrCode3", "Kod 3: Drajver za ovaj uređaj je oštećen");
                case 10: return LocalizationService.Get("WinErrCode10", "Kod 10: Uređaj ne može da se pokrene");
                case 12: return LocalizationService.Get("WinErrCode12", "Kod 12: Uređaj ne može da pronađe slobodne resurse");
                case 14: return LocalizationService.Get("WinErrCode14", "Kod 14: Potreban je restart računara da bi uređaj radio");
                case 18: return LocalizationService.Get("WinErrCode18", "Kod 18: Reinstalirajte drajvere za ovaj uređaj");
                case 22: return LocalizationService.Get("WinErrCode22", "Kod 22: Uređaj je onemogućen (Disabled)");
                case 24: return LocalizationService.Get("WinErrCode24", "Kod 24: Uređaj nije prisutan ili ne radi ispravno");
                case 28: return LocalizationService.Get("WinErrCode28", "Kod 28: Drajver nije instaliran (Missing Driver)");
                case 29: return LocalizationService.Get("WinErrCode29", "Kod 29: Uređaj je onemogućen u BIOS-u");
                case 31: return LocalizationService.Get("WinErrCode31", "Kod 31: Windows ne može da učita drajvere za ovaj uređaj");
                case 32: return LocalizationService.Get("WinErrCode32", "Kod 32: Drajver (servis) za ovaj uređaj je onemogućen");
                case 37: return LocalizationService.Get("WinErrCode37", "Kod 37: Windows ne može da inicijalizuje drajver uređaja");
                case 39: return LocalizationService.Get("WinErrCode39", "Kod 39: Windows ne može da učita drajver (oštećen ili nedostaje)");
                case 43: return LocalizationService.Get("WinErrCode43", "Kod 43: Windows je zaustavio uređaj zbog prijavljenog problema");
                default: return string.Format(LocalizationService.Get("WinErrCodeDefault", "Kod {0}: Problem u konfiguraciji uređaja"), code);
            }
        }

        private static string FormatWmiDate(string rawDate)
        {
            if (string.IsNullOrWhiteSpace(rawDate)) return "N/A";
            try
            {
                string s = rawDate.Trim();
                if (s.Length >= 8 && char.IsDigit(s[0]) && char.IsDigit(s[1]) && char.IsDigit(s[2]) && char.IsDigit(s[3]))
                {
                    string y = s.Substring(0, 4);
                    string m = s.Substring(4, 2);
                    string d = s.Substring(6, 2);
                    return string.Format("{0}.{1}.{2}.", d, m, y);
                }
            }
            catch { }
            return rawDate;
        }

        public void DetectDrivers()
        {
            try
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsScanning = true;
                });

                var missingList = new List<DriverItemViewModel>();
                var allDevicesList = new List<DriverItemViewModel>();
                var seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Detekcija problematičnih i nedostajućih uređaja (ConfigManagerErrorCode != 0)
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, PNPClass, Manufacturer, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : LocalizationService.Get("MdlUnknownDevice", "Nepoznat uređaj");
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                            string pnpClass = obj["PNPClass"] != null ? obj["PNPClass"].ToString().Trim() : "Other";
                            string manufacturer = obj["Manufacturer"] != null ? obj["Manufacturer"].ToString().Trim() : "Nepoznato";

                            uint errCode = 0;
                            if (obj["ConfigManagerErrorCode"] != null)
                            {
                                uint.TryParse(obj["ConfigManagerErrorCode"].ToString(), out errCode);
                            }

                            string hwid = "";
                            string[] hwids = obj["HardwareID"] as string[];
                            if (hwids != null && hwids.Length > 0 && !string.IsNullOrEmpty(hwids[0]))
                            {
                                hwid = hwids[0].Trim();
                            }
                            else
                            {
                                hwid = devId;
                            }

                            string errDesc = GetErrorCodeDescription(errCode);
                            SolidColorBrush brush = errCode == 28
                                ? BaseViewModel.CreateFrozenBrush(248, 81, 73)
                                : BaseViewModel.CreateFrozenBrush(210, 153, 34);

                            var item = new DriverItemViewModel
                            {
                                Name = name,
                                Manufacturer = manufacturer,
                                PnpClass = pnpClass,
                                DeviceId = devId,
                                HardwareId = hwid,
                                ErrorCode = errCode,
                                ErrorDescription = errDesc,
                                HasError = true,
                                StatusBrush = brush,
                                CanInstall = true,
                                IsInstalling = false,
                                IsResolved = false,
                                InstallProgressPercent = 0,
                                InstallStatusText = "",
                                InstallSpeedText = "",
                                InstallDriverCommand = ViewModel.Drivers.InstallDriverCommand,
                                WindowsUpdateFallbackCommand = new RelayCommand(ExecuteWindowsUpdateFallback)
                            };

                            missingList.Add(item);
                            if (!string.IsNullOrEmpty(devId))
                            {
                                seenDeviceIds.Add(devId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("WARN", "Drajveri", "Greška pri upitu problematičnih uređaja: " + ex.Message);
                    });
                }

                // 2. Detekcija potpisanih i instaliranih drajvera (Win32_PnPSignedDriver)
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT DeviceName, FriendlyName, DriverVersion, DriverDate, Manufacturer, DeviceClass, HardWareID, DeviceID FROM Win32_PnPSignedDriver"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["DeviceName"] != null ? obj["DeviceName"].ToString().Trim() : "";
                            if (string.IsNullOrEmpty(name) && obj["FriendlyName"] != null)
                            {
                                name = obj["FriendlyName"].ToString().Trim();
                            }
                            if (string.IsNullOrEmpty(name)) continue;

                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                            string devClass = obj["DeviceClass"] != null ? obj["DeviceClass"].ToString().Trim() : "Other";
                            string manufacturer = obj["Manufacturer"] != null ? obj["Manufacturer"].ToString().Trim() : "Nepoznato";
                            string version = obj["DriverVersion"] != null ? obj["DriverVersion"].ToString().Trim() : "N/A";
                            string dateRaw = obj["DriverDate"] != null ? obj["DriverDate"].ToString().Trim() : "";
                            string dateStr = FormatWmiDate(dateRaw);
                            string hwid = obj["HardWareID"] != null ? obj["HardWareID"].ToString().Trim() : (string.IsNullOrEmpty(devId) ? "N/A" : devId);

                            var item = new DriverItemViewModel
                            {
                                Name = name,
                                Manufacturer = manufacturer,
                                DriverVersion = version,
                                DriverDate = dateStr,
                                DeviceId = devId,
                                HardwareId = hwid,
                                PnpClass = devClass,
                                ErrorCode = 0,
                                ErrorDescription = LocalizationService.Get("WinDrvInstalledOkDesc", "Ispravno instaliran"),
                                HasError = false,
                                StatusBrush = BaseViewModel.CreateFrozenBrush(63, 185, 80)
                            };

                            if (IsKeyHardwareDevice(devClass, name))
                            {
                                item.IsKeyDevice = true;
                                DriverItemViewModel capturedItem = item;
                                item.CheckUpdateCommand = new RelayCommand(delegate(object p) { ExecuteCheckUpdate(p ?? capturedItem); });
                                item.UpgradeDriverCommand = new RelayCommand(delegate(object p) { ExecuteUpgradeDriver(p ?? capturedItem); });
                            }

                            allDevicesList.Add(item);
                            if (!string.IsNullOrEmpty(devId))
                            {
                                seenDeviceIds.Add(devId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("WARN", "Drajveri", "Greška pri čitanju Win32_PnPSignedDriver: " + ex.Message);
                    });
                }

                // 3. Dopuna sa Win32_PnPEntity za sve ispravne uređaje koji nisu u Win32_PnPSignedDriver
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT Name, DeviceID, PNPClass, Manufacturer, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 0"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string devId = obj["DeviceID"] != null ? obj["DeviceID"].ToString().Trim() : "";
                            if (!string.IsNullOrEmpty(devId) && seenDeviceIds.Contains(devId))
                            {
                                continue;
                            }

                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                            if (string.IsNullOrEmpty(name)) continue;

                            string pnpClass = obj["PNPClass"] != null ? obj["PNPClass"].ToString().Trim() : "Other";
                            string manufacturer = obj["Manufacturer"] != null ? obj["Manufacturer"].ToString().Trim() : "Nepoznato";

                            string hwid = "";
                            string[] hwids = obj["HardwareID"] as string[];
                            if (hwids != null && hwids.Length > 0 && !string.IsNullOrEmpty(hwids[0]))
                            {
                                hwid = hwids[0].Trim();
                            }
                            else
                            {
                                hwid = devId;
                            }

                            var item = new DriverItemViewModel
                            {
                                Name = name,
                                Manufacturer = manufacturer,
                                DriverVersion = "Standardni sistemski",
                                DriverDate = "N/A",
                                DeviceId = devId,
                                HardwareId = hwid,
                                PnpClass = pnpClass,
                                ErrorCode = 0,
                                ErrorDescription = LocalizationService.Get("WinDrvInstalledOkDesc", "Ispravno instaliran"),
                                HasError = false,
                                StatusBrush = BaseViewModel.CreateFrozenBrush(63, 185, 80)
                            };

                            if (IsKeyHardwareDevice(pnpClass, name))
                            {
                                item.IsKeyDevice = true;
                                DriverItemViewModel capturedItem = item;
                                item.CheckUpdateCommand = new RelayCommand(delegate(object p) { ExecuteCheckUpdate(p ?? capturedItem); });
                                item.UpgradeDriverCommand = new RelayCommand(delegate(object p) { ExecuteUpgradeDriver(p ?? capturedItem); });
                            }

                            allDevicesList.Add(item);
                            if (!string.IsNullOrEmpty(devId))
                            {
                                seenDeviceIds.Add(devId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("WARN", "Drajveri", "Greška pri dopunskom upitu Win32_PnPEntity: " + ex.Message);
                    });
                }

                // 4. Raspodela po kategorijama
                var catDisplay = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatDisplay", "Grafički adapteri"), CategoryCode = "Display", CategoryIcon = "🖥️", IsExpanded = true };
                var catNet = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatNet", "Mrežni adapteri"), CategoryCode = "Net", CategoryIcon = "🌐", IsExpanded = true };
                var catAudio = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatAudio", "Zvučne i audio komponente"), CategoryCode = "MEDIA", CategoryIcon = "🔊", IsExpanded = true };
                var catDisk = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatDisk", "Diskovi i skladište"), CategoryCode = "DiskDrive", CategoryIcon = "💾", IsExpanded = true };
                var catCamera = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatCamera", "Kamere i biometrija"), CategoryCode = "Camera", CategoryIcon = "📷", IsExpanded = true };
                var catInput = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatInput", "Tastature i pokazivači"), CategoryCode = "Keyboard", CategoryIcon = "⌨️", IsExpanded = true };
                var catUsb = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatUsb", "USB i kontroleri"), CategoryCode = "USB", CategoryIcon = "🔌", IsExpanded = true };
                var catSystem = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatSystem", "Sistemski uređaji & Čipset"), CategoryCode = "System", CategoryIcon = "⚙️", IsExpanded = false };
                var catOther = new DriverCategoryViewModel { CategoryName = LocalizationService.Get("WinCatOther", "Ostali uređaji"), CategoryCode = "Other", CategoryIcon = "📦", IsExpanded = false };

                foreach (var dev in allDevicesList)
                {
                    string cls = (dev.PnpClass ?? "").ToUpperInvariant().Trim();

                    if (cls == "DISPLAY")
                    {
                        catDisplay.AllDevices.Add(dev);
                    }
                    else if (cls == "NET")
                    {
                        catNet.AllDevices.Add(dev);
                    }
                    else if (cls == "MEDIA" || cls == "AUDIOENDPOINT" || cls == "SOUND")
                    {
                        catAudio.AllDevices.Add(dev);
                    }
                    else if (cls == "DISKDRIVE" || cls == "SCSIADAPTER" || cls == "HDC" || cls == "IDE" || cls == "VOLUMESNAPSHOT" || cls == "STORAGE")
                    {
                        catDisk.AllDevices.Add(dev);
                    }
                    else if (cls == "CAMERA" || cls == "IMAGE" || cls == "BIOMETRIC" || cls == "SENSOR")
                    {
                        catCamera.AllDevices.Add(dev);
                    }
                    else if (cls == "KEYBOARD" || cls == "MOUSE" || cls == "HIDCLASS")
                    {
                        catInput.AllDevices.Add(dev);
                    }
                    else if (cls == "USB" || cls == "USBDEVICE")
                    {
                        catUsb.AllDevices.Add(dev);
                    }
                    else if (cls == "SYSTEM" || cls == "PROCESSOR" || cls == "FIRMWARE" || cls == "COMPUTER")
                    {
                        catSystem.AllDevices.Add(dev);
                    }
                    else
                    {
                        catOther.AllDevices.Add(dev);
                    }
                }

                var categoriesToDisplay = new List<DriverCategoryViewModel>
                {
                    catDisplay, catNet, catAudio, catDisk, catCamera, catInput, catUsb, catSystem, catOther
                };

                // Sortiranje uređaja po nazivu unutar svake kategorije
                foreach (var c in categoriesToDisplay)
                {
                    c.AllDevices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                }

                // 5. Ažuriranje ViewModel-a na UI niti
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.MissingDevices.Clear();
                    foreach (var m in missingList)
                    {
                        ViewModel.Drivers.MissingDevices.Add(m);
                    }
                    ViewModel.Drivers.MissingDevicesCount = missingList.Count;

                    if (missingList.Count > 0)
                    {
                        ViewModel.Drivers.HasMissingDevices = true;
                        ViewModel.Drivers.MissingSummaryText = string.Format(LocalizationService.Get("WinDrvMissingSummary", "⚠ Pronađeno je {0} uređaja bez odgovarajućeg drajvera ili sa greškom!"), missingList.Count);
                        ViewModel.Drivers.MissingSummaryBrush = BaseViewModel.CreateFrozenBrush(248, 81, 73);
                    }
                    else
                    {
                        ViewModel.Drivers.HasMissingDevices = false;
                        ViewModel.Drivers.MissingSummaryText = LocalizationService.Get("WinDrvAllInstalledSummary", "✅ Svi drajveri su uspešno instalirani! (0 problematičnih uređaja)");
                        ViewModel.Drivers.MissingSummaryBrush = BaseViewModel.CreateFrozenBrush(63, 185, 80);
                    }

                    ViewModel.Drivers.Categories.Clear();
                    int totalCount = 0;
                    foreach (var cat in categoriesToDisplay)
                    {
                        if (cat.AllDevices.Count > 0)
                        {
                            cat.Devices.Clear();
                            foreach (var dev in cat.AllDevices)
                            {
                                cat.Devices.Add(dev);
                            }
                            cat.IsVisible = true;
                            cat.NotifyDeviceCountChanged();
                            totalCount += cat.AllDevices.Count;
                            ViewModel.Drivers.Categories.Add(cat);
                        }
                    }

                    ViewModel.Drivers.TotalDevicesCount = totalCount;
                    ViewModel.Drivers.IsScanning = false;

                    if (!string.IsNullOrEmpty(ViewModel.Drivers.SearchQuery))
                    {
                        ViewModel.Drivers.ApplySearchFilter(ViewModel.Drivers.SearchQuery);
                    }

                    ViewModel.AddLog("INFO", "Drajveri", string.Format("Detektovano {0} instaliranih uređaja, {1} problematičnih.", totalCount, missingList.Count));
                    NotifyHwTestDriverStateChanged(missingList.Count == 0);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsScanning = false;
                    ViewModel.AddLog("ERROR", "Drajveri", "Kritična greška tokom detekcije drajvera: " + ex.Message);
                    NotifyHwTestDriverStateChanged(false);
                });
            }
        }

        #endregion

        #region Termalna Telemetrija, 5-Minutni Grafikon, Taktovi Jezgara & Stres Test (v2.15.0 - WIS-TASK-131)

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            ShutdownEmbeddedHwTest();
            StopScreenCaptureWorker();

            if (_stressCts != null)
            {
                try
                {
                    _stressCts.Cancel();
                }
                catch { }
            }
            if (_thermalTimer != null)
            {
                try
                {
                    _thermalTimer.Stop();
                    _thermalTimer.Dispose();
                    _thermalTimer = null;
                }
                catch { }
            }
            if (_thermalEngine != null)
            {
                _thermalEngine.Dispose();
                _thermalEngine = null;
            }
            if (_diskReadCounter != null)
            {
                try { _diskReadCounter.Dispose(); } catch { }
                _diskReadCounter = null;
            }
            if (_diskWriteCounter != null)
            {
                try { _diskWriteCounter.Dispose(); } catch { }
                _diskWriteCounter = null;
            }
            if (_diskTimeCounter != null)
            {
                try { _diskTimeCounter.Dispose(); } catch { }
                _diskTimeCounter = null;
            }
        }

        private void ResetCoreClockExtremes()
        {
            if (_thermalEngine != null)
            {
                try
                {
                    _thermalEngine.ResetCoreClockExtremes();
                }
                catch { }
            }
            if (ViewModel != null)
            {
                ViewModel.ResetAllHwMonitorExtremes();
                ViewModel.RefreshHwMonitorKpiSummaries();
            }
            UpdateThermalTelemetry();
        }

        private PointCollection BuildGraphPoints(float[] history)
        {
            PointCollection pts = new PointCollection(GraphHistoryLength);
            double width = 900.0;
            double height = 200.0;
            double divisor = (double)(GraphHistoryLength - 1);

            for (int i = 0; i < GraphHistoryLength; i++)
            {
                double x = (i / divisor) * width;
                double val = (double)history[i];
                if (val < 0.0) val = 0.0;
                if (val > 100.0) val = 100.0;
                double y = height - (val / 100.0) * height;
                pts.Add(new Point(x, y));
            }

            if (pts.CanFreeze)
            {
                pts.Freeze();
            }
            return pts;
        }

        private void PushThermalGraphSample(float cpu, float gpu, float mb, float ssd, float throttling, float cpuLoad)
        {
            PointCollection cpuPts;
            PointCollection gpuPts;
            PointCollection mbPts;
            PointCollection ssdPts;
            PointCollection throtPts;
            PointCollection cpuLoadPts;

            lock (_graphLock)
            {
                if (!_graphBuffersInitialized)
                {
                    for (int i = 0; i < GraphHistoryLength; i++)
                    {
                        _cpuHistory[i] = cpu > 0f ? cpu : 0f;
                        _gpuHistory[i] = gpu > 0f ? gpu : 0f;
                        _mbHistory[i] = mb > 0f ? mb : 0f;
                        _ssdHistory[i] = ssd > 0f ? ssd : 0f;
                        _throttleHistory[i] = throttling > 0f ? throttling : 0f;
                        _cpuLoadHistory[i] = cpuLoad > 0f ? cpuLoad : 0f;
                    }
                    _graphBuffersInitialized = true;
                }
                else
                {
                    int last = GraphHistoryLength - 1;
                    Array.Copy(_cpuHistory, 1, _cpuHistory, 0, last);
                    Array.Copy(_gpuHistory, 1, _gpuHistory, 0, last);
                    Array.Copy(_mbHistory, 1, _mbHistory, 0, last);
                    Array.Copy(_ssdHistory, 1, _ssdHistory, 0, last);
                    Array.Copy(_throttleHistory, 1, _throttleHistory, 0, last);
                    Array.Copy(_cpuLoadHistory, 1, _cpuLoadHistory, 0, last);

                    _cpuHistory[last] = cpu > 0f ? cpu : 0f;
                    _gpuHistory[last] = gpu > 0f ? gpu : 0f;
                    _mbHistory[last] = mb > 0f ? mb : 0f;
                    _ssdHistory[last] = ssd > 0f ? ssd : 0f;
                    _throttleHistory[last] = throttling > 0f ? throttling : 0f;
                    _cpuLoadHistory[last] = cpuLoad > 0f ? cpuLoad : 0f;
                }

                cpuPts = BuildGraphPoints(_cpuHistory);
                gpuPts = BuildGraphPoints(_gpuHistory);
                mbPts = BuildGraphPoints(_mbHistory);
                ssdPts = BuildGraphPoints(_ssdHistory);
                throtPts = BuildGraphPoints(_throttleHistory);
                cpuLoadPts = BuildGraphPoints(_cpuLoadHistory);
            }

            ViewModel.CpuGraphPoints = cpuPts;
            ViewModel.GpuGraphPoints = gpuPts;
            ViewModel.MbGraphPoints = mbPts;
            ViewModel.SsdGraphPoints = ssdPts;
            ViewModel.ThrottleGraphPoints = throtPts;
            ViewModel.CpuLoadGraphPoints = cpuLoadPts;
        }

        private void UpdateCoreClocksTable()
        {
            if (_thermalEngine == null || ViewModel == null) return;

            List<CoreClockSensorInfo> snapshot = _thermalEngine.GetCoreClocksSnapshot();
            if (snapshot == null) return;

            double sumMhz = 0.0;
            int validCount = 0;

            if (ViewModel.CoreClocks.Count != snapshot.Count)
            {
                ViewModel.CoreClocks.Clear();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    CoreClockSensorInfo info = snapshot[i];
                    if (info.CurrentMHz > 0.0f) { sumMhz += info.CurrentMHz; validCount++; }
                    CoreClockItem item = new CoreClockItem();
                    item.CoreIndex = info.CoreIndex;
                    item.CoreName = info.CoreName;
                    item.CurrentClockText = string.Format("{0:F0} MHz", info.CurrentMHz);
                    item.MinClockText = string.Format("{0:F0} MHz", info.MinMHz);
                    item.MaxClockText = string.Format("{0:F0} MHz", info.MaxMHz);
                    item.RatioText = string.Format("x {0:F1}", info.CurrentMHz / 100.0f);
                    item.ClockRatioPercent = (info.MaxMHz > 0.0f) ? ((double)info.CurrentMHz / (double)info.MaxMHz) * 100.0 : 0.0;
                    ViewModel.CoreClocks.Add(item);
                }
            }
            else
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    CoreClockSensorInfo info = snapshot[i];
                    if (info.CurrentMHz > 0.0f) { sumMhz += info.CurrentMHz; validCount++; }
                    CoreClockItem item = ViewModel.CoreClocks[i];
                    item.CoreIndex = info.CoreIndex;
                    item.CoreName = info.CoreName;
                    item.CurrentClockText = string.Format("{0:F0} MHz", info.CurrentMHz);
                    item.MinClockText = string.Format("{0:F0} MHz", info.MinMHz);
                    item.MaxClockText = string.Format("{0:F0} MHz", info.MaxMHz);
                    item.RatioText = string.Format("x {0:F1}", info.CurrentMHz / 100.0f);
                    item.ClockRatioPercent = (info.MaxMHz > 0.0f) ? ((double)info.CurrentMHz / (double)info.MaxMHz) * 100.0 : 0.0;
                }
            }

            if (validCount > 0)
            {
                ViewModel.HwCpuAvgClockText = string.Format("Ø {0:F2} GHz", (sumMhz / validCount) / 1000.0);
            }
        }

        private void RunThermalUpdateBackground()
        {
            if (_thermalEngine == null) return;
            if (Interlocked.CompareExchange(ref _thermalUpdateInFlight, 1, 0) != 0) return;

            try
            {
                ThermalSensorEngine eng = _thermalEngine;
                if (eng == null) return;
                eng.Update();

                if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ApplyThermalTelemetryToUi();
                    });
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _thermalUpdateInFlight, 0);
            }
        }

        private void UpdateThermalTelemetry()
        {
            ThreadPool.QueueUserWorkItem(delegate { RunThermalUpdateBackground(); });
        }

        private void ApplyThermalTelemetryToUi()
        {
            if (ViewModel == null || _thermalEngine == null)
            {
                return;
            }

            try
            {
                // 1. CPU Temperatura
                float cpu = _thermalEngine.CpuTemperature;
                ViewModel.CpuTemp = cpu;
                if (cpu > 0f)
                {
                    ViewModel.CpuTempText = string.Format("{0:F1} °C", cpu);
                    ViewModel.CpuTempShortText = string.Format("{0:F0}°C", cpu);
                    ViewModel.CpuTempBrush = MainViewModel.GetThermalBrush(cpu, 65.0f, 80.0f);
                    ViewModel.LegendCpuText = string.Format("CPU Temp ({0:F0}°C)", cpu);
                    ViewModel.UpdateHwMonitorSensor("temp_cpu", cpu, "{0:F1} °C", ViewModel.CpuTempBrush);
                }
                else
                {
                    ViewModel.CpuTempText = LocalizationService.Get("WinCpuTempWmiNa", "N/A (WMI/ACPI nedostupan)");
                    ViewModel.CpuTempShortText = "N/A";
                    ViewModel.CpuTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendCpuText = "CPU Temp (N/A)";
                    ViewModel.SetHwMonitorSensorUnavailable("temp_cpu", "N/A");
                }

                // 2. GPU Temperatura
                float gpu = _thermalEngine.GpuTemperature;
                ViewModel.GpuTemp = gpu;
                if (gpu > 0f)
                {
                    ViewModel.GpuTempText = string.Format("{0:F1} °C", gpu);
                    ViewModel.GpuTempShortText = string.Format("{0:F0}°C", gpu);
                    ViewModel.GpuTempBrush = MainViewModel.GetThermalBrush(gpu, 75.0f, 85.0f);
                    ViewModel.LegendGpuText = string.Format("GPU Temp ({0:F0}°C)", gpu);
                    ViewModel.UpdateHwMonitorSensor("temp_gpu", gpu, "{0:F1} °C", ViewModel.GpuTempBrush);
                }
                else
                {
                    ViewModel.GpuTempText = LocalizationService.Get("WinGpuTempIgpuNa", "N/A (Integrisana / Idle)");
                    ViewModel.GpuTempShortText = "N/A";
                    ViewModel.GpuTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendGpuText = "GPU Temp (N/A)";
                    ViewModel.SetHwMonitorSensorUnavailable("temp_gpu", "N/A");
                }

                // 3. Matična ploča (MB) Temperatura
                float mb = _thermalEngine.MotherboardTemperature;
                ViewModel.MbTemp = mb;
                if (mb > 0f)
                {
                    ViewModel.MbTempText = string.Format("{0:F1} °C", mb);
                    ViewModel.MbTempShortText = string.Format("{0:F0}°C", mb);
                    ViewModel.MbTempBrush = MainViewModel.GetThermalBrush(mb, 60.0f, 75.0f);
                    ViewModel.LegendMbText = string.Format("MB Temp ({0:F0}°C)", mb);
                    ViewModel.UpdateHwMonitorSensor("temp_mb", mb, "{0:F1} °C", ViewModel.MbTempBrush);
                }
                else
                {
                    ViewModel.MbTempText = "N/A";
                    ViewModel.MbTempShortText = "N/A";
                    ViewModel.MbTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendMbText = "MB Temp (N/A)";
                    ViewModel.SetHwMonitorSensorUnavailable("temp_mb", "N/A");
                }

                // 4. SSD Temperatura
                float ssd = _thermalEngine.SsdTemperature;
                ViewModel.SsdTemp = ssd;
                if (ssd > 0f)
                {
                    ViewModel.SsdTempText = string.Format("{0:F1} °C", ssd);
                    ViewModel.SsdTempShortText = string.Format("{0:F0}°C", ssd);
                    ViewModel.SsdTempBrush = MainViewModel.GetThermalBrush(ssd, 55.0f, 70.0f);
                    ViewModel.LegendSsdText = string.Format("SSD Temp ({0:F0}°C)", ssd);
                    ViewModel.UpdateHwMonitorSensor("temp_ssd", ssd, "{0:F1} °C", ViewModel.SsdTempBrush);
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageTemp = string.Format("{0:F1} °C", ssd);
                    }
                }
                else
                {
                    ViewModel.SsdTempText = "N/A (SMART Passthrough)";
                    ViewModel.SsdTempShortText = "N/A";
                    ViewModel.SsdTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendSsdText = "SSD Temp (N/A)";
                    ViewModel.SetHwMonitorSensorUnavailable("temp_ssd", "N/A");
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageTemp = LocalizationService.Get("WinStorageTempNa", "N/A (Senzor nedostupan)");
                    }
                }

                // 5. CPU Thermal Throttling (%)
                float throt = _thermalEngine.ThrottlingPercentage;
                ViewModel.ThrottlingPercent = throt;
                if (_thermalEngine.IsThrottlingMetricAvailable)
                {
                    ViewModel.ThrottlingText = string.Format("{0:F0} %", throt);
                    ViewModel.ThrottlingBrush = throt > 5.0f
                        ? MainViewModel.CreateFrozenBrush(231, 76, 60)
                        : MainViewModel.CreateFrozenBrush(78, 201, 176);
                    ViewModel.LegendThrottleText = string.Format("Throttling ({0:F0}%)", throt);
                    ViewModel.UpdateHwMonitorSensor("load_throttle", throt, "{0:F0} %", ViewModel.ThrottlingBrush);
                }
                else
                {
                    ViewModel.ThrottlingText = LocalizationService.Get("MdlNotAvailable", "Nije dostupno");
                    ViewModel.ThrottlingBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendThrottleText = LocalizationService.Get("WinLegendThrottleNa", "Throttling (Nije dostupno)");
                    ViewModel.SetHwMonitorSensorUnavailable("load_throttle", "N/A");
                }

                // 6. Ventilator (Fan)
                int fan = _thermalEngine.FanRpm;
                ViewModel.FanRpm = fan;
                if (fan > 0)
                {
                    ViewModel.FanSpeedText = string.Format("{0} RPM", fan);
                    ViewModel.FanSpeedBrush = MainViewModel.CreateFrozenBrush(78, 201, 176);
                    ViewModel.UpdateHwMonitorSensor("fan_rpm", fan, "{0:F0} RPM", ViewModel.FanSpeedBrush);
                }
                else
                {
                    ViewModel.FanSpeedText = _thermalEngine.HasFanData ? LocalizationService.Get("WinFan0RpmQuiet", "0 RPM (Ugašen/Tihi rad)") : LocalizationService.Get("WinFanPassiveBios", "Pasivno / BIOS kontrola");
                    ViewModel.FanSpeedBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.SetHwMonitorSensorUnavailable("fan_rpm", _thermalEngine.HasFanData ? "0 RPM" : "BIOS/N/A");
                }

                // 7. Sensor Status
                if (_thermalEngine.IsSensorAvailable)
                {
                    ViewModel.ThermalSensorStatus = string.Format(LocalizationService.Get("WinThermalSensorNative", "● Nativni senzor ({0}, 1 Hz)"), _thermalEngine.SensorSource);
                    ViewModel.ThermalSensorStatusBrush = MainViewModel.CreateFrozenBrush(78, 201, 176);
                }
                else
                {
                    ViewModel.ThermalSensorStatus = LocalizationService.Get("WinThermalSensorNa", "● Termalni senzori nisu dostupni (N/A)");
                    ViewModel.ThermalSensorStatusBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                }

                // 8. Ažuriranje 5-minutnog (300s) višekanalnog grafikona i tabele taktova jezgara
                PushThermalGraphSample(cpu, gpu, mb, ssd, throt, (float)ViewModel.CpuUsagePercent);
                UpdateCoreClocksTable();
                ViewModel.RefreshHwMonitorKpiSummaries();
                ViewModel.RebuildOverviewAlerts();
            }
            catch { }
        }

        private void StartThermalStressTest()
        {
            if (ViewModel.IsStressTestRunning)
            {
                return;
            }

            ViewModel.IsStressTestRunning = true;
            ViewModel.EmergencyAbortTriggered = false;
            ViewModel.StressTestProgress = 0;
            ViewModel.MaxTempReached = 0.0f;
            ViewModel.StressTestTimeText = "0s";
            ViewModel.StressTestStatusText = LocalizationService.Get("WinStressRunningStatus", "Kontinuirani multi-core stres test u toku...");
            ViewModel.ThermalVerdictTitle = LocalizationService.Get("WinStressRunningTitle", "Stres test u toku...");
            ViewModel.ThermalVerdictDescription = LocalizationService.Get("WinStressRunningDesc", "Merenje temperature i taktova pod 100% opterećenjem svih jezgara...");
            ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(241, 196, 15);
            ViewModel.ThermalRecommendation = LocalizationService.Get("WinStressRunningRec", "Kliknite na 'Stop Stres' kada završite proveru hlađenja.");

            CommandManager.InvalidateRequerySuggested();

            _stressCts = new CancellationTokenSource();
            CancellationToken token = _stressCts.Token;

            // Ručni start iz Taba 4 pokreće kontinuirani stres test (durationSeconds = 0) dok korisnik ne klikne Stop Stres ili do 95°C/97°C
            Task.Factory.StartNew(delegate {
                RunStressTestWorker(token, 0);
            }, token);
        }

        private void CancelThermalStressTest()
        {
            if (_stressCts != null)
            {
                try
                {
                    _stressCts.Cancel();
                }
                catch { }
            }

            ViewModel.StressTestStatusText = LocalizationService.Get("WinStressStoppedByTech", "Stres test zaustavljen od strane servisera.");
            CommandManager.InvalidateRequerySuggested();
        }

        private void RunStressTestWorker(CancellationToken token, int durationSeconds)
        {
            if (_deviceMonitor != null) _deviceMonitor.PausePolling(true);
            try
            {
                RunStressTestWorkerInternal(token, durationSeconds);
            }
            finally
            {
                if (_deviceMonitor != null) _deviceMonitor.PausePolling(false);
            }
        }

        public static int ComputeCpuStressThreadCount(int logicalProcessors)
        {
            if (logicalProcessors <= 1) return 1;
            if (logicalProcessors >= 8) return logicalProcessors - 2;
            return logicalProcessors - 1;
        }

        private void RunStressTestWorkerInternal(CancellationToken token, int durationSeconds)
        {
            int logicalCount = Math.Max(1, Environment.ProcessorCount);
            int coreCount = ComputeCpuStressThreadCount(Environment.ProcessorCount);
            Thread[] workerThreads = new Thread[coreCount];

            for (int i = 0; i < coreCount; i++)
            {
                workerThreads[i] = new Thread(new ThreadStart(delegate
                {
                    double val = 1.0001;
                    int counter = 0;
                    while (!token.IsCancellationRequested)
                    {
                        for (int j = 0; j < 5000; j++)
                        {
                            val = Math.Sin(val) * Math.Cos(val) + Math.Sqrt(val + 1.0);
                        }
                        counter++;
                        if (counter % 20 == 0)
                        {
                            if (logicalCount <= 1)
                            {
                                Thread.Sleep(1);
                            }
                            else
                            {
                                Thread.Yield(); // Omogućava OS-u trenutnu obradu UI događaja
                            }
                        }
                    }
                }));
                workerThreads[i].IsBackground = true;
                workerThreads[i].Priority = ThreadPriority.Lowest; // KLJUČNO! UI nit i DWM uvek imaju prioritet!
                workerThreads[i].Start();
            }

            float maxTemp = 0.0f;
            float maxThrot = 0.0f;
            bool emergencyAbort = false;
            int consecutiveOverheat = 0;
            Stopwatch sw = Stopwatch.StartNew();
            long targetMs = durationSeconds > 0 ? (long)durationSeconds * 1000L : long.MaxValue;

            while (sw.ElapsedMilliseconds < targetMs && !token.IsCancellationRequested && !emergencyAbort)
            {
                Thread.Sleep(500);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                float curCpu = (_thermalEngine != null && _thermalEngine.IsSensorAvailable)
                    ? _thermalEngine.CpuTemperature
                    : 0f;
                if (curCpu > maxTemp)
                {
                    maxTemp = curCpu;
                }

                float curThrot = (_thermalEngine != null && _thermalEngine.IsThrottlingMetricAvailable)
                    ? _thermalEngine.ThrottlingPercentage
                    : 0f;
                if (curThrot > maxThrot)
                {
                    maxThrot = curThrot;
                }

                // EMERGENCY ABORT: >= 97°C momentalno ili 2 uzastopna uzorka >= 95°C (zaštita hardvera)
                if (curCpu >= 95.0f) { consecutiveOverheat++; } else { consecutiveOverheat = 0; }
                if (curCpu >= 97.0f || consecutiveOverheat >= 2)
                {
                    emergencyAbort = true;
                    try
                    {
                        if (_stressCts != null)
                        {
                            _stressCts.Cancel();
                        }
                    }
                    catch { }

                    try
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                    }
                    catch { }

                    break;
                }

                long elapsedMs = sw.ElapsedMilliseconds;
                double elapsedSeconds = elapsedMs / 1000.0;
                int progress = durationSeconds > 0
                    ? Math.Min(100, (int)((elapsedSeconds / (double)durationSeconds) * 100.0))
                    : (int)(elapsedSeconds % 100);

                float snapCpu = curCpu;
                float snapMaxTemp = maxTemp;
                double snapElapsed = elapsedSeconds;
                int snapProgress = progress;
                int snapDuration = durationSeconds;

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (ViewModel == null) return;

                    ViewModel.MaxTempReached = snapMaxTemp;
                    ViewModel.StressTestProgress = snapProgress;
                    ViewModel.StressTestTimeText = snapDuration > 0
                        ? string.Format("{0:F0} / {1}s", Math.Min((double)snapDuration, snapElapsed), snapDuration)
                        : string.Format("{0:F0}s", snapElapsed);
                    if (snapCpu > 0.5f)
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressActiveTemp", "Stres aktivno ({0:F0}s) | CPU: {1:F1}°C | Max: {2:F1}°C"), snapElapsed, snapCpu, snapMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressActiveNoSensor", "Stres aktivno ({0:F0}s) | CPU senzor: N/A"), snapElapsed);
                    }
                }));
            }

            sw.Stop();

            // Sačuvaj stanje otkazivanja PRE nego što cleanup kod pozove _stressCts.Cancel() za gašenje radničkih niti!
            bool wasCancelledBeforeCleanup = token.IsCancellationRequested;
            int actualDurationSec = (int)Math.Round(sw.Elapsed.TotalSeconds);
            bool isManualCancel = wasCancelledBeforeCleanup &&
                                  !emergencyAbort &&
                                  (durationSeconds <= 0 || sw.Elapsed.TotalSeconds < (durationSeconds - 0.5));

            _lastStressActualDurationSec = actualDurationSec;
            _lastStressMaxTemp = maxTemp;
            _lastStressMaxThrottling = maxThrot;
            _lastStressEmergencyAbort = emergencyAbort;
            _lastStressManualCancel = isManualCancel;
            _lastStressWasExecuted = true;

            try
            {
                if (_stressCts != null && !_stressCts.IsCancellationRequested)
                {
                    _stressCts.Cancel();
                }
            }
            catch { }

            Thread.Sleep(100);

            // Snimi izmereni rezultat u C:\setup\stress-test-result.json po A2 ugovoru
            WriteUiStressTestResultFile(actualDurationSec, maxTemp, maxThrot, emergencyAbort, isManualCancel);

            string verdictTitle;
            string verdictDesc;
            Brush verdictBrush;
            string verdictRec;

            if (emergencyAbort || maxTemp >= 95.0f)
            {
                verdictTitle = LocalizationService.Get("WinStressCritTitle", "🔴 HITNO POTREBNO ČIŠĆENJE I ZAMENA PASTE");
                verdictDesc = emergencyAbort ?
                    string.Format(LocalizationService.Get("WinStressCritAbortDesc", "Kritično pregrevanje! Test je automatski prekinut na {0:F1}°C (prag 95°C / 97°C) radi zaštite hardvera."), maxTemp) :
                    string.Format(LocalizationService.Get("WinStressCritTempDesc", "Temperatura je dostigla kritičnih {0:F1}°C! Hladnjak je zapušen ili je pasta sasušena."), maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(231, 76, 60); // #E74C3C
                verdictRec = LocalizationService.Get("WinStressCritRec", "Preporuka: Obavezno rastavljanje računara, čišćenje ventilatora i rebara hladnjaka, i nanošenje nove visokokvalitetne termalne paste.");
            }
            else if (maxTemp <= 0.5f || (_thermalEngine != null && !_thermalEngine.IsSensorAvailable))
            {
                verdictTitle = LocalizationService.Get("WinThermalSensorUnknownTitle", "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)");
                verdictDesc = LocalizationService.Get("WinStressNoSensorDesc", "CPU je opterećen 100%, ali senzor temperature nije dostupan (0.0°C). Hlađenje nije moguće automatski potvrditi — obavezna ručna provera ventilatora i izduva!");
                verdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18); // #F39C12 Žuto/Narandžasto upozorenje
                verdictRec = LocalizationService.Get("WinStressNoSensorRec", "Preporuka: Očitavanja senzora nisu dostupna preko WMI/ACPI/OEM. Proverite temperaturu u BIOS/UEFI-ju i fizički proverite rad kulera pre predaje računara.");
            }
            else if (maxTemp >= 80.0f && maxTemp < 95.0f)
            {
                verdictTitle = LocalizationService.Get("WinStressWarnTitle", "🟡 PREPORUČUJE SE PREVENTIVNI SERVIS");
                verdictDesc = string.Format(LocalizationService.Get("WinStressWarnDesc", "Maksimalna temperatura je dostigla {0:F1}°C pod punim opterećenjem."), maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(241, 196, 15); // #F1C40F
                verdictRec = LocalizationService.Get("WinStressWarnRec", "Preporuka: Termalna pasta počinje da gubi elastičnost ili ima naslaga prašine. Preporučuje se servis u narednom periodu.");
            }
            else
            {
                verdictTitle = LocalizationService.Get("WinStressOkTitle", "🟢 ODLIČNO HLAĐENJE");
                verdictDesc = string.Format(LocalizationService.Get("WinStressOkDesc", "Maksimalna temperatura od samo {0:F1}°C pod punim opterećenjem!"), maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(78, 201, 176); // #4EC9B0
                verdictRec = LocalizationService.Get("WinStressOkRec", "Preporuka: Hlađenje i termalna pasta su u vrhunskom fabričkom stanju. Nije potrebno servisiranje.");
            }

            float finalMaxTemp = maxTemp;
            bool finalAbort = emergencyAbort;
            int finalDuration = durationSeconds;
            int finalActualDuration = actualDurationSec;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                if (ViewModel == null) return;

                ViewModel.IsStressTestRunning = false;
                ViewModel.EmergencyAbortTriggered = finalAbort;
                ViewModel.MaxTempReached = finalMaxTemp;

                if (finalAbort)
                {
                    ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressAbortStatus", "PREKID: Zaštitni prag dostignut ({0:F1}°C)!"), finalMaxTemp);
                }
                else if (isManualCancel)
                {
                    if (finalMaxTemp > 0.5f)
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressStoppedTemp", "Stres test zaustavljen nakon {0}s (Max CPU: {1:F1}°C)"), finalActualDuration, finalMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressStoppedNoSensor", "Stres test zaustavljen nakon {0}s (Senzor: N/A)"), finalActualDuration);
                    }
                }
                else
                {
                    ViewModel.StressTestProgress = 100;
                    int shownDuration = finalDuration > 0 ? finalDuration : finalActualDuration;
                    if (finalDuration > 0)
                    {
                        ViewModel.StressTestTimeText = string.Format("{0} / {0}s", finalDuration);
                    }
                    if (finalMaxTemp > 0.5f)
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressDoneTemp", "Test uspešno završen ({0}s | Max CPU: {1:F1}°C)"), shownDuration, finalMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format(LocalizationService.Get("WinStressDoneNoSensor", "Test završen ({0}s | Senzor temperature nije dostupan)"), shownDuration);
                    }
                }

                ViewModel.ThermalVerdictTitle = verdictTitle;
                ViewModel.ThermalVerdictDescription = verdictDesc;
                ViewModel.ThermalVerdictBrush = verdictBrush;
                ViewModel.ThermalRecommendation = verdictRec;

                CommandManager.InvalidateRequerySuggested();
            }));
        }

        #endregion
    }
}
