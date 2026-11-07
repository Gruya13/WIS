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
        private DispatcherTimer _thermalTimer;
        private CancellationTokenSource _stressCts;

        private const int GraphHistoryLength = 300;
        private readonly float[] _cpuHistory = new float[GraphHistoryLength];
        private readonly float[] _gpuHistory = new float[GraphHistoryLength];
        private readonly float[] _mbHistory = new float[GraphHistoryLength];
        private readonly float[] _ssdHistory = new float[GraphHistoryLength];
        private readonly float[] _throttleHistory = new float[GraphHistoryLength];
        private bool _graphBuffersInitialized = false;
        private readonly object _graphLock = new object();
        private string _initialStartTimeIso = null;

        private System.Timers.Timer _diagMetricsTimer;
        private ulong _prevIdleTime = 0;
        private ulong _prevKernelTime = 0;
        private ulong _prevUserTime = 0;
        private bool _hasPrevCpuTimes = false;
        private long _prevNetBytes = -1;
        private long _accumulatedNetBytes = 0;
        private int _diagNetCounter = 2;
        private int _nasPingCounter = 0;
        private bool _isNasPingRunning = false;

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

        private bool _isMasterMode = false;
        private DispatcherTimer _autoStartTimer;
        private int _autoStartCountdown = 15;
        private bool _taskRunnerStarted = false;
        private Process _currentPhaseProcess = null;
        private int _failedPhase = 0;
        private string _lastProcessedLog = "";

        private int _lastStressActualDurationSec = 0;
        private float _lastStressMaxTemp = 0.0f;
        private float _lastStressMaxThrottling = 0.0f;
        private bool _lastStressEmergencyAbort = false;
        private bool _lastStressManualCancel = false;
        private bool _lastStressWasExecuted = false;
        private bool _criticalStressFailure = false;
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

            DataContext = ViewModel;

            // Inicijalizacija termalnog senzorskog motora (v2.15.0 - WIS-TASK-131)
            try
            {
                _thermalEngine = new ThermalSensorEngine();
            }
            catch { }

            _thermalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _thermalTimer.Tick += (s, e) => UpdateThermalTelemetry();
            _thermalTimer.Start();
            UpdateThermalTelemetry();

            // Reaktivno praćenje promene jezika (v2.14.0 - I18N-01)
            LocalizationService.LanguageChanged += OnLanguageChanged;

            // Inicijalizacija i pokretanje ServisHub Telemetrije
            _telemetryService = new TelemetryService(ViewModel, Dispatcher);
            _telemetryService.Start();

            Closing += Window_Closing;
            Closed += (s, e) =>
            {
                LocalizationService.LanguageChanged -= OnLanguageChanged;

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
            };

            Loaded += (s, e) =>
            {
                StartMetricsTimer();
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

            // Povezivanje AutoScroll događaja
            ViewModel.AllLogs.CollectionChanged += (s, e) =>
            {
                if (ViewModel.AutoScroll && LogListView != null && ViewModel.AllLogs.Count > 0)
                {
                    LogListView.ScrollIntoView(ViewModel.AllLogs[ViewModel.AllLogs.Count - 1]);
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
                    ViewModel.IsServiceReadyVisible = false;
                    ViewModel.AddLog("INFO", "Sistem", string.Format("Detektovano stanje nakon međurestarta: automatski nastavljam Fazu {0}...", savedPhase));
                    ViewModel.CurrentPhase = savedPhase;
                    StartPhaseExecution(savedPhase);
                }
                else
                {
                    ViewModel.IsServiceReadyVisible = true;
                    string initialMsg = string.Format("Automatski start za {0}s... (Kliknite za trenutni start ili prebacite tab za pauzu)", _autoStartCountdown);
                    ViewModel.AutoStartMessage = initialMsg;
                    ViewModel.LiveStatus = initialMsg;

                    _autoStartTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    _autoStartTimer.Tick += (s, e) =>
                    {
                        _autoStartCountdown--;
                        if (_autoStartCountdown > 0)
                        {
                            string tickMsg = string.Format("Automatski start za {0}s... (Kliknite za trenutni start ili prebacite tab za pauzu)", _autoStartCountdown);
                            ViewModel.AutoStartMessage = tickMsg;
                            ViewModel.LiveStatus = tickMsg;
                        }
                        else
                        {
                            _autoStartTimer.Stop();
                            ViewModel.IsServiceReadyVisible = false;
                            StartPhaseExecution(2);
                        }
                    };
                    _autoStartTimer.Start();
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
        }

        #region DWM Immersive Dark Title Bar

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            EnableDarkTitleBar();
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

        #endregion

        private void OnDurationTick(object sender, EventArgs e)
        {
            ViewModel.TickDuration();

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
            _failedPhase = 0;
            ViewModel.IsPaused = false;
            ViewModel.IpcConnected = true;
            ViewModel.IpcStatusText = string.Format("Master Faza {0}", phaseNumber);

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
                        ViewModel.PhaseName = "Faza 1: Mreža i povezivanje";
                        ViewModel.SubPhase = "Povezivanje na servisnu mrežu...";
                        ViewModel.LiveStatus = "Konfigurisanje mrežnih interfejsa i rutiranja...";
                        ViewModel.Progress = 10;
                        break;
                    case 2:
                        ViewModel.PhaseName = "Faza 2: Drajveri i zakrpe";
                        ViewModel.SubPhase = "Instalacija PnP drajvera...";
                        ViewModel.LiveStatus = "Skeniranje i instalacija nedostajućih drajvera...";
                        ViewModel.Progress = 25;
                        break;
                    case 3:
                        ViewModel.PhaseName = "Faza 3: Optimizacija i debloat";
                        ViewModel.SubPhase = "Sistemske optimizacije...";
                        ViewModel.LiveStatus = "Primenjivanje debloat i brending skripti...";
                        ViewModel.Progress = 50;
                        break;
                    case 4:
                        ViewModel.PhaseName = "Faza 4: Instalacija programa";
                        ViewModel.SubPhase = "Instalacija programskih paketa...";
                        ViewModel.LiveStatus = "Instalacija i verifikacija servisnih programa...";
                        ViewModel.Progress = 70;
                        break;
                    case 5:
                        ViewModel.PhaseName = "Faza 5: Termalni i stres test (30s)";
                        ViewModel.SubPhase = "Automatski 30s stres test procesora...";
                        ViewModel.LiveStatus = "Merenje termalnog hlađenja pod 100% opterećenjem (30s)...";
                        ViewModel.Progress = 85;
                        break;
                    case 6:
                        ViewModel.PhaseName = "Faza 6: Završetak i rezime";
                        ViewModel.SubPhase = "Finalizacija i dijagnostika...";
                        ViewModel.LiveStatus = "Generisanje servisnog izveštaja i verifikacija...";
                        ViewModel.Progress = 95;
                        break;
                    default:
                        ViewModel.PhaseName = string.Format("Faza {0}", phaseNumber);
                        ViewModel.SubPhase = "Izvršavanje faze...";
                        ViewModel.LiveStatus = string.Format("Rad na Fazi {0}...", phaseNumber);
                        break;
                }

                ViewModel.AddLog("INFO", "TaskRunner", string.Format("Započeto izvršavanje Faze {0}: {1}", phaseNumber, ViewModel.PhaseName));
            }));

            // 2. Na početku Faze 1 emitujemo STARTED Lifecycle događaj
            if (phaseNumber == 1 && _telemetryService != null)
            {
                var startData = new Dictionary<string, object>
                {
                    { "phase", 1 },
                    { "phase_name", "Mreza" },
                    { "model", ViewModel.ComputerModel },
                    { "serial", ViewModel.SerialNumber }
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
                    bool isThermalCritical = HasCriticalThermalEvidence();
                    if (isThermalCritical)
                    {
                        HandleCriticalThermalAbort(2);
                        return;
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
                    ViewModel.LiveStatus = string.Format("Greška: {0} nedostaje na sistemu!", fileName);
                    _failedPhase = phaseNumber;
                }));
                return;
            }

            // 4. Pokretanje PowerShell radnika za traženu fazu
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", scriptPath),
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
                bool exited = proc.WaitForExit(timeoutMinutes * 60 * 1000);

                if (!exited)
                {
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
                            bool isThermalCritTimeout = HasCriticalThermalEvidence();
                            if (isThermalCritTimeout)
                            {
                                HandleCriticalThermalAbort(2);
                                return;
                            }
                        }
                        ExecutePhase(6);
                        return;
                    }
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Faza {0} je prekinuta zbog isteka timeout-a od {1} minuta!", phaseNumber, timeoutMinutes));
                        ViewModel.LiveStatus = string.Format("Faza {0} prekoračila timeout. Kliknite 'Preskoči' ili 'Nastavi'.", phaseNumber);
                        _failedPhase = phaseNumber;
                    }));
                    return;
                }

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

                    bool isThermalCritical = HasCriticalThermalEvidence();

                    if (isThermalCritical)
                    {
                        HandleCriticalThermalAbort(exitCode);
                        return;
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
                    }
                    else if (phaseNumber == 4)
                    {
                        bool pass = PerformPhase4RealityCheck();
                        if (!pass)
                        {
                            return; // Faza 4 označena kao FAILED, TaskRunner ne prelazi dalje
                        }
                    }

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("SUCCESS", "TaskRunner", string.Format("Faza {0} uspešno završena (ExitCode 0).", phaseNumber));
                    }));

                    // Upravljanje međurestartom (između Faze 3 i Faze 4)
                    if (phaseNumber == 3)
                    {
                        HandleInterimReboot();
                        return;
                    }

                    // Završetak celokupnog servisa (po završetku Faze 6)
                    if (phaseNumber >= 6)
                    {
                        HandleFinalCompletion();
                        return;
                    }

                    // Prelazak na sledeću fazu
                    ExecutePhase(phaseNumber + 1);
                }
                else
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Faza {0} završila sa greškom (ExitCode {1}).", phaseNumber, exitCode));
                        ViewModel.LiveStatus = string.Format("Greška u Fazi {0} (Kod {1}). Kliknite 'Preskoči' za sledeću ili 'Nastavi' za ponovni pokušaj.", phaseNumber, exitCode);
                        _failedPhase = phaseNumber;
                    }));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "TaskRunner", string.Format("Izuzetak pri pokretanju Faze {0}: {1}", phaseNumber, ex.Message));
                    _failedPhase = phaseNumber;
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
                ViewModel.StressTestStatusText = "Automatski 30s stres test procesora (Faza 5)...";
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
                else if (maxT >= 85.0f || maxThrot > 5.0f)
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
                _criticalStressFailure = (maxT > 0.5f) && string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase);

                var resMap = new Dictionary<string, object>
                {
                    { "status", status },
                    { "reason", reason },
                    { "emergency_abort", abort },
                    { "sensor_available", sensorAvailable },
                    { "sensor_source", sensorSource },
                    { "duration_sec", _lastStressActualDurationSec },
                    { "max_cpu_temp_c", Math.Round(maxT, 1) },
                    { "max_throttling_percent", Math.Round(maxThrot, 1) },
                    { "timestamp", DateTime.Now.ToString("o") }
                };
                File.WriteAllText(resultPath, JsonHelper.Serialize(resMap), Encoding.UTF8);

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ViewModel != null)
                    {
                        ViewModel.StressResultStatus = status;
                        ViewModel.StressResultReason = reason;
                        ViewModel.CriticalStressFailure = _criticalStressFailure;
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
                        _criticalStressFailure = string.Equals(status, "THERMAL_CRITICAL", StringComparison.OrdinalIgnoreCase) || abort;

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
                            File.WriteAllText(resultPath, JsonHelper.Serialize(updatedMap), Encoding.UTF8);
                        }

                        Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (ViewModel != null)
                            {
                                ViewModel.StressResultStatus = status;
                                ViewModel.StressResultReason = reason;
                                ViewModel.CriticalStressFailure = _criticalStressFailure;
                                if (maxT > ViewModel.MaxTempReached)
                                {
                                    ViewModel.MaxTempReached = maxT;
                                }
                                if (_lastStressEmergencyAbort)
                                {
                                    ViewModel.EmergencyAbortTriggered = true;
                                }
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

        private void HandleCriticalThermalAbort(int exitCode)
        {
            // Sigurnosna zaštita: Ako senzori nisu dostupni ili temperatura nije očitana (status UNKNOWN),
            // instalacija se NE SME zaustaviti niti zaključati crvenim ekranom!
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

            _criticalStressFailure = true;
            _lastStressEmergencyAbort = true;
            _lastStressStatus = "THERMAL_CRITICAL";

            // U2 (Paket C2): Zaustavi Screen Capture worker pri kritičnom termalnom prekidu
            StopScreenCaptureWorker();

            // Bezbednosni Anti-Leak korak: ukloni servisne Wi-Fi tajne čak i pri blokadi zbog pregrevanja
            PerformAntiLeakWifiCleanupOnly();

            if (_telemetryService != null)
            {
                var critData = new Dictionary<string, object>
                {
                    { "phase", 5 },
                    { "phase_name", "Stres" },
                    { "status", "NEEDS_ATTENTION" },
                    { "stress_status", "THERMAL_CRITICAL" },
                    { "reason", _lastStressReason },
                    { "max_cpu_temp_c", Math.Round(_lastStressMaxTemp, 1) },
                    { "exit_code", exitCode }
                };
                _telemetryService.SendLifecycleEvent("NEEDS_ATTENTION", critData);
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (ViewModel == null) return;

                ViewModel.CriticalStressFailure = true;
                ViewModel.DeploymentStatus = "NEEDS_ATTENTION";
                ViewModel.StressResultStatus = "THERMAL_CRITICAL";
                ViewModel.StressResultReason = _lastStressReason;
                ViewModel.IsCompleted = false;
                ViewModel.IsServiceFinished = false;
                ViewModel.ShutdownTimerActive = false;
                ViewModel.ShutdownCountdown = 0;
                ViewModel.EmergencyAbortTriggered = true;
                ViewModel.CurrentPhase = 5;
                _failedPhase = 5;

                float displayTemp = _lastStressMaxTemp > 0f ? _lastStressMaxTemp : 98.0f;
                ViewModel.MaxTempReached = displayTemp;
                ViewModel.PhaseName = "🔴 KRITIČNO PREGREVANJE PROCESORA (THERMAL_CRITICAL)";
                ViewModel.SubPhase = "ZAUSTAVLJENO — Obavezan servis hlađenja i zamena termalne paste!";
                ViewModel.LiveStatus = string.Format("🔴 ZABRANJENA ISPORUKA KLIJENTU: Procesor je dostigao kritičnih {0:F1}°C! Obavezno očistiti hladnjak i zameniti pastu.", displayTemp);
                ViewModel.StressTestStatusText = string.Format("🔴 KRITIČAN PREKID ({0:F1}°C) — OBAVEZAN SERVIS HLAĐENJA!", displayTemp);

                ViewModel.ThermalVerdictTitle = "🔴 KRITIČNO PREGREVANJE — RAČUNAR NE SME BITI PREDAT KLIJENTU!";
                ViewModel.ThermalVerdictDescription = string.Format("Automatski stres test je prekinut zbog kritičnog pregrevanja procesora ({0:F1}°C). Sistem je blokirao proglašavanje instalacije spremnom i zaustavio automatsko gašenje.", displayTemp);
                ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(231, 76, 60);
                ViewModel.ThermalRecommendation = "OBAVEZNA AKCIJA SERVISERA: Rastaviti sistem hlađenja, očistiti ventilator i rebra hladnjaka, naneti novu termalnu pastu i ponoviti stres test.";

                ViewModel.AddLog("ERROR", "ThermalGuard", string.Format("KRITIČNO PREGREVANJE ({0:F1}°C, Kod {1})! Status: NEEDS_ATTENTION / THERMAL_CRITICAL. Automatsko gašenje je deaktivirano, a servisna Wi-Fi lozinka obrisana.", displayTemp, exitCode));

                try
                {
                    if (MainTabControl != null)
                    {
                        MainTabControl.SelectedIndex = 3; // Prebaci fokus na Tab Dijagnostika
                    }
                }
                catch { }

                ThreadPool.QueueUserWorkItem(delegate(object _)
                {
                    try
                    {
                        Console.Beep(880, 300);
                        Console.Beep(880, 300);
                        Console.Beep(440, 500);
                    }
                    catch
                    {
                        try { System.Media.SystemSounds.Hand.Play(); } catch { }
                    }
                });
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
                case 5: return 5;  // Termalni i stres test (30s)
                case 6: return 45; // Završetak i čišćenje
                default: return 30;
            }
        }

        private void HandleInterimReboot()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.AddLog("WARN", "TaskRunner", "Faza 3 uspešno završena. Pripremam međurestart radi rasterećenja CBS/MSI podsistema...");
                ViewModel.LiveStatus = "Kontrolisani međurestart za 5 sekundi...";
            }));

            // 1. Zapis stanja phase: 4 u C:\setup\deploy-state.json
            SaveDeployStatePhase(4);

            // 2. Obezbeđivanje RunOnce unosa za WIS.UI.exe --master
            EnsureRunOnceEntry();

            // 3. Nalaz 14 (Paket C2): Realno poštovanje odloženog restarta (+10m Restart) i pred-restart odbrojavanje
            bool readyForReboot = false;
            while (!readyForReboot)
            {
                DateTime dueUtc = GetScheduledRestartDueUtc();
                while (dueUtc > DateTime.UtcNow)
                {
                    TimeSpan remaining = dueUtc - DateTime.UtcNow;
                    if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
                    int remMin = (int)Math.Floor(remaining.TotalMinutes);
                    int remSec = remaining.Seconds;
                    string dueLocalStr = dueUtc.ToLocalTime().ToString("HH:mm:ss");
                    string waitMsg = string.Format("Međurestart odložen od strane servisera — preostalo {0:D2}:{1:D2} (do {2})", remMin, remSec, dueLocalStr);

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.SubPhase = string.Format("Međurestart odložen (do {0})", dueLocalStr);
                        ViewModel.LiveStatus = waitMsg;
                    }));

                    Thread.Sleep(1000);
                    dueUtc = GetScheduledRestartDueUtc();
                }

                // Pred-restart odbrojavanje (5 sekundi) sa proverom odlaganja u svakoj sekundi
                bool delayedDuringCountdown = false;
                for (int sec = 5; sec >= 1; sec--)
                {
                    if (GetScheduledRestartDueUtc() > DateTime.UtcNow)
                    {
                        delayedDuringCountdown = true;
                        break;
                    }
                    int currentSec = sec;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.LiveStatus = string.Format("Kontrolisani međurestart za {0}s... (Kliknite '+10m Restart' za odlaganje)", currentSec);
                    }));
                    Thread.Sleep(1000);
                }

                if (!delayedDuringCountdown && GetScheduledRestartDueUtc() <= DateTime.UtcNow)
                {
                    readyForReboot = true;
                }
            }

            lock (_restartScheduleLock)
            {
                _restartPending = false;
                _restartDueAtUtc = DateTime.MinValue;
            }
            SaveDeployStatePhase(4);

            // U2 (Paket C2): Čisto zaustavi Screen Capture worker pre međurestarta
            StopScreenCaptureWorker();

            // 4. Pokretanje kontrolisanog restarta računara
            try
            {
                Process.Start("shutdown.exe", "/r /t 5 /f /c \"WIS: Kontrolisani međurestart radi stabilizacije CBS/MSI podsistema.\"");
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "TaskRunner", "Greška pri pozivu shutdown.exe: " + ex.Message);
                }));
            }

            Thread.Sleep(1500);
            Dispatcher.BeginInvoke((Action)(() => Close()));
        }

        private List<string> GetMissingKeyApplications()
        {
            List<string> missing = new List<string>();

            // 1. Google Chrome
            bool hasChrome = File.Exists(@"C:\Program Files\Google\Chrome\Application\chrome.exe") ||
                             File.Exists(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe");
            if (!hasChrome)
            {
                missing.Add("Google Chrome");
            }

            // 2. Adobe Acrobat
            bool hasAcrobat = File.Exists(@"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") ||
                              File.Exists(@"C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") ||
                              File.Exists(@"C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") ||
                              File.Exists(@"C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe");
            if (!hasAcrobat)
            {
                missing.Add("Adobe Acrobat");
            }

            // 3. Microsoft Office (WINWORD / EXCEL)
            bool hasOffice = File.Exists(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") ||
                             File.Exists(@"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") ||
                             File.Exists(@"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") ||
                             File.Exists(@"C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE") ||
                             File.Exists(@"C:\Program Files\Microsoft Office\Office16\WINWORD.EXE") ||
                             File.Exists(@"C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE") ||
                             File.Exists(@"C:\Program Files\Microsoft Office\Office15\WINWORD.EXE") ||
                             File.Exists(@"C:\Program Files (x86)\Microsoft Office\Office15\WINWORD.EXE");
            if (!hasOffice)
            {
                missing.Add("Microsoft Office (Word/Excel)");
            }

            // 4. WinRAR
            bool hasWinrar = File.Exists(@"C:\Program Files\WinRAR\WinRAR.exe") ||
                             File.Exists(@"C:\Program Files (x86)\WinRAR\WinRAR.exe");
            if (!hasWinrar)
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
                    ViewModel.LiveStatus = "Greška u Fazi 4: Nisu instalirani ključni programi (" + missingStr + "). Faza označena kao FAILED.";
                    _failedPhase = 4;

                    foreach (string item in missing)
                    {
                        if (item.Contains("Chrome")) ViewModel.UpdateAppStatus("Chrome", "FAIL", "Fajl nije pronađen");
                        if (item.Contains("Acrobat")) ViewModel.UpdateAppStatus("Acrobat", "FAIL", "Fajl nije pronađen");
                        if (item.Contains("Office")) ViewModel.UpdateAppStatus("Office", "FAIL", "Fajl nije pronađen");
                        if (item.Contains("WinRAR")) ViewModel.UpdateAppStatus("WinRAR", "FAIL", "Fajl nije pronađen");
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
                                    string devName = mo["Name"] != null ? mo["Name"].ToString() : "Nepoznat uređaj";
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
                        ViewModel.LiveStatus = string.Format("Upozorenje: Pronađeno {0} neispravnih uređaja u Upravljaču uređaja.", badDevices.Count);
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

        private void HandleFinalCompletion()
        {
            StopScreenCaptureWorker();
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

            // HandleCriticalThermalAbort se poziva ISKLJUČIVO ako je procesor stvarno dostigao >= 95°C
            // ili je _lastStressEmergencyAbort == true sa očitanom temperaturom!
            bool isActuallyThermalCritical = HasCriticalThermalEvidence();

            if (isActuallyThermalCritical)
            {
                HandleCriticalThermalAbort(2);
                return;
            }

            // Pre nego što TaskRunner označi 100% "Završeno", proveri fizičke artefakte aplikacija
            List<string> missingApps = GetMissingKeyApplications();
            if (missingApps.Count > 0)
            {
                string missingStr = string.Join(", ", missingApps.ToArray());
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "RealityCheck", "Post-Execution Reality Check: Servis NE MOŽE biti označen kao završen jer nedostaju ključne aplikacije: " + missingStr);
                    ViewModel.LiveStatus = "Greška: Nedostaju ključni programi (" + missingStr + "). TaskRunner ne može postaviti 100% završeno.";
                    ViewModel.CurrentPhase = 4;
                    _failedPhase = 4;
                }));
                return;
            }
            if (_telemetryService != null)
            {
                var compData = new Dictionary<string, object>
                {
                    { "phase", 6 },
                    { "phase_name", "Zavrsetak" },
                    { "status", "SUCCESS" },
                    { "stress_status", _lastStressStatus },
                    { "duration", ViewModel.DurationString },
                    { "apps_summary", ViewModel.AppsSummaryText }
                };
                _telemetryService.SendLifecycleEvent("COMPLETED", compData);
            }

            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.CurrentPhase = 6;
                ViewModel.Progress = 100;
                ViewModel.DeploymentStatus = "READY";
                ViewModel.IsCompleted = true;
                ViewModel.IsServiceFinished = true;
                ViewModel.CompletionTitle = "INSTALACIJA ZAVRŠENA";
                if (isSensorUnavailable || string.Equals(_lastStressStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.StressResultStatus = "UNKNOWN";
                    ViewModel.CompletionSummary = "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.";
                    ViewModel.LiveStatus = "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.";
                    ViewModel.ThermalVerdictTitle = "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)";
                    ViewModel.ThermalVerdictDescription = "Hlađenje nije automatski potvrđeno; obavezna ručna provera. Potrebna je ručna provera hladnjaka i ventilatora.";
                    ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18); // #F39C12
                    ViewModel.ThermalRecommendation = "Preporuka: Proverite rad kulera i ventilaciju u BIOS/UEFI ili fizički pre isporuke računara.";
                }
                else if (string.Equals(_lastStressStatus, "WARN", StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.CompletionSummary = string.Format("Svi programi, drajveri i optimizacije su kompletirani. UPOZORENJE: Maksimalna CPU temperatura na stres testu je dostigla {0:F1}°C (preporučen preventivni servis hlađenja).", _lastStressMaxTemp);
                }
                else
                {
                    ViewModel.CompletionSummary = "Svi programi, drajveri, stres test i sistemske optimizacije su kompletirani.";
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

                    int phaseToSave = 1;
                    string statePath = @"C:\setup\deploy-state.json";
                    if (File.Exists(statePath))
                    {
                        try
                        {
                            string existingJson = File.ReadAllText(statePath, Encoding.UTF8);
                            var root = JsonHelper.Parse(existingJson);
                            if (root != null && !root.IsNull)
                            {
                                if (root.ContainsKey("phase")) phaseToSave = root["phase"].AsInt(1);
                                else if (root.ContainsKey("Phase")) phaseToSave = root["Phase"].AsInt(1);
                            }
                        }
                        catch { }
                    }
                    else if (ViewModel != null && ViewModel.CurrentPhase > 0)
                    {
                        phaseToSave = ViewModel.CurrentPhase;
                    }

                    var stateMap = new Dictionary<string, object>
                    {
                        { "phase", phaseToSave },
                        { "Phase", phaseToSave },
                        { "initial_start_time", _initialStartTimeIso },
                        { "timestamp", DateTime.Now.ToString("o") },
                        { "computer_name", Environment.MachineName },
                        { "restart_pending", _restartPending && _restartDueAtUtc > DateTime.UtcNow },
                        { "restart_due_at", _restartDueAtUtc > DateTime.MinValue ? _restartDueAtUtc.ToLocalTime().ToString("o") : "" },
                        { "delayed_restart_minutes", _delayedRestartMinutesTotal }
                    };

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

        private void SaveDeployStatePhase(int phase)
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
                    var stateMap = new Dictionary<string, object>
                    {
                        { "phase", phase },
                        { "Phase", phase },
                        { "initial_start_time", _initialStartTimeIso },
                        { "timestamp", DateTime.Now.ToString("o") },
                        { "computer_name", Environment.MachineName },
                        { "restart_pending", _restartPending && _restartDueAtUtc > DateTime.UtcNow },
                        { "restart_due_at", _restartDueAtUtc > DateTime.MinValue ? _restartDueAtUtc.ToLocalTime().ToString("o") : "" },
                        { "delayed_restart_minutes", _delayedRestartMinutesTotal }
                    };
                    string json = JsonHelper.Serialize(stateMap);
                    File.WriteAllText(@"C:\setup\deploy-state.json", json, Encoding.UTF8);
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
                        hubUrl = "http://192.168.170.254:8000";
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

        private void EnsureRunOnceEntry()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!File.Exists(exePath)) exePath = @"C:\setup\Skripte\WIS.UI\WIS.UI.exe";

                // 1. HKLM RunOnce unos
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    {
                        if (key != null)
                        {
                            try { key.DeleteValue("GWADT_Resume", false); } catch { }
                            key.SetValue("WIS_Resume", string.Format("\"{0}\" --master", exePath));
                        }
                    }
                }
                catch { }

                // 2. HKCU RunOnce fallback
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    {
                        if (key != null)
                        {
                            try { key.DeleteValue("GWADT_Resume", false); } catch { }
                            key.SetValue("WIS_Resume", string.Format("\"{0}\" --master", exePath));
                        }
                    }
                }
                catch { }
            }
            catch { }
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
                            ViewModel.IpcStatusText = "IPC Diskonektovan";
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
                ViewModel.IpcStatusText = "IPC Završen (EOF)";
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

        private void ProcessIncomingJson(string json)
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
                if (root.ContainsKey("phase"))
                {
                    int p = root["phase"].AsInt(1);
                    ViewModel.CurrentPhase = p;
                }
                if (root.ContainsKey("phase_name"))
                {
                    ViewModel.PhaseName = root["phase_name"].AsString(ViewModel.PhaseName);
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
                    if (hwNode.ContainsKey("model") && !string.IsNullOrWhiteSpace(hwNode["model"].AsString()))
                    {
                        ViewModel.ComputerModel = hwNode["model"].AsString();
                        modelFound = true;
                    }
                    else if (hwNode.ContainsKey("Model") && !string.IsNullOrWhiteSpace(hwNode["Model"].AsString()))
                    {
                        ViewModel.ComputerModel = hwNode["Model"].AsString();
                        modelFound = true;
                    }

                    if (hwNode.ContainsKey("serial")) ViewModel.SerialNumber = hwNode["serial"].AsString(ViewModel.SerialNumber);
                    if (hwNode.ContainsKey("SerialNumber")) ViewModel.SerialNumber = hwNode["SerialNumber"].AsString(ViewModel.SerialNumber);

                    if (hwNode.ContainsKey("cpu")) ViewModel.CpuInfo = hwNode["cpu"].AsString(ViewModel.CpuInfo);
                    if (hwNode.ContainsKey("CpuStr")) ViewModel.CpuInfo = hwNode["CpuStr"].AsString(ViewModel.CpuInfo);

                    if (hwNode.ContainsKey("ram")) ViewModel.RamInfo = hwNode["ram"].AsString(ViewModel.RamInfo);
                    if (hwNode.ContainsKey("RamStr")) ViewModel.RamInfo = hwNode["RamStr"].AsString(ViewModel.RamInfo);

                    if (hwNode.ContainsKey("disk")) ViewModel.DiskInfo = hwNode["disk"].AsString(ViewModel.DiskInfo);
                    if (hwNode.ContainsKey("DiskStr")) ViewModel.DiskInfo = hwNode["DiskStr"].AsString(ViewModel.DiskInfo);

                    if (hwNode.ContainsKey("battery")) ViewModel.BatteryStatus = hwNode["battery"].AsString(ViewModel.BatteryStatus);
                    if (hwNode.ContainsKey("BatteryStr")) ViewModel.BatteryStatus = hwNode["BatteryStr"].AsString(ViewModel.BatteryStatus);

                    // GPU & Displej
                    if (hwNode.ContainsKey("gpu")) ViewModel.GpuName = hwNode["gpu"].AsString(ViewModel.GpuName);
                    else if (hwNode.ContainsKey("gpu_name")) ViewModel.GpuName = hwNode["gpu_name"].AsString(ViewModel.GpuName);
                    else if (hwNode.ContainsKey("GpuName")) ViewModel.GpuName = hwNode["GpuName"].AsString(ViewModel.GpuName);

                    if (hwNode.ContainsKey("gpu_vram")) ViewModel.GpuVram = hwNode["gpu_vram"].AsString(ViewModel.GpuVram);
                    else if (hwNode.ContainsKey("GpuVram")) ViewModel.GpuVram = hwNode["GpuVram"].AsString(ViewModel.GpuVram);

                    if (hwNode.ContainsKey("gpu_driver")) ViewModel.GpuDriverVersion = hwNode["gpu_driver"].AsString(ViewModel.GpuDriverVersion);
                    else if (hwNode.ContainsKey("GpuDriverVersion")) ViewModel.GpuDriverVersion = hwNode["GpuDriverVersion"].AsString(ViewModel.GpuDriverVersion);

                    if (hwNode.ContainsKey("display_resolution")) ViewModel.DisplayResolution = hwNode["display_resolution"].AsString(ViewModel.DisplayResolution);
                    else if (hwNode.ContainsKey("DisplayResolution")) ViewModel.DisplayResolution = hwNode["DisplayResolution"].AsString(ViewModel.DisplayResolution);

                    if (hwNode.ContainsKey("display_refresh_rate")) ViewModel.DisplayRefreshRate = hwNode["display_refresh_rate"].AsString(ViewModel.DisplayRefreshRate);
                    else if (hwNode.ContainsKey("DisplayRefreshRate")) ViewModel.DisplayRefreshRate = hwNode["DisplayRefreshRate"].AsString(ViewModel.DisplayRefreshRate);

                    if (hwNode.ContainsKey("display_dpi_scale")) ViewModel.DisplayDpiScale = hwNode["display_dpi_scale"].AsString(ViewModel.DisplayDpiScale);
                    else if (hwNode.ContainsKey("DisplayDpiScale")) ViewModel.DisplayDpiScale = hwNode["DisplayDpiScale"].AsString(ViewModel.DisplayDpiScale);

                    // Matična ploča, BIOS & Autopilot Hardverski otisak
                    if (hwNode.ContainsKey("motherboard")) ViewModel.MotherboardModel = hwNode["motherboard"].AsString(ViewModel.MotherboardModel);
                    else if (hwNode.ContainsKey("MotherboardModel")) ViewModel.MotherboardModel = hwNode["MotherboardModel"].AsString(ViewModel.MotherboardModel);

                    if (hwNode.ContainsKey("bios_version")) ViewModel.BiosVersion = hwNode["bios_version"].AsString(ViewModel.BiosVersion);
                    else if (hwNode.ContainsKey("BiosVersion")) ViewModel.BiosVersion = hwNode["BiosVersion"].AsString(ViewModel.BiosVersion);

                    if (hwNode.ContainsKey("bios_date")) ViewModel.BiosReleaseDate = hwNode["bios_date"].AsString(ViewModel.BiosReleaseDate);
                    else if (hwNode.ContainsKey("BiosReleaseDate")) ViewModel.BiosReleaseDate = hwNode["BiosReleaseDate"].AsString(ViewModel.BiosReleaseDate);

                    if (hwNode.ContainsKey("secure_boot")) ViewModel.SecureBootStatus = hwNode["secure_boot"].AsString(ViewModel.SecureBootStatus);
                    else if (hwNode.ContainsKey("SecureBootStatus")) ViewModel.SecureBootStatus = hwNode["SecureBootStatus"].AsString(ViewModel.SecureBootStatus);

                    if (hwNode.ContainsKey("manufacturer")) ViewModel.Autopilot.HwManufacturer = hwNode["manufacturer"].AsString(ViewModel.Autopilot.HwManufacturer);
                    else if (hwNode.ContainsKey("Manufacturer")) ViewModel.Autopilot.HwManufacturer = hwNode["Manufacturer"].AsString(ViewModel.Autopilot.HwManufacturer);

                    if (modelFound) ViewModel.Autopilot.HwModel = ViewModel.ComputerModel;
                    if (!string.IsNullOrEmpty(ViewModel.SerialNumber)) ViewModel.Autopilot.HwSerialNumber = ViewModel.SerialNumber;

                    if (hwNode.ContainsKey("uuid")) ViewModel.Autopilot.HwSmbiosUuid = hwNode["uuid"].AsString(ViewModel.Autopilot.HwSmbiosUuid);
                    else if (hwNode.ContainsKey("SmbiosUuid")) ViewModel.Autopilot.HwSmbiosUuid = hwNode["SmbiosUuid"].AsString(ViewModel.Autopilot.HwSmbiosUuid);
                }

                if (!modelFound && root.ContainsKey("model_info") && !string.IsNullOrWhiteSpace(root["model_info"].AsString()))
                {
                    ViewModel.ComputerModel = root["model_info"].AsString();
                    ViewModel.Autopilot.HwModel = ViewModel.ComputerModel;
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
                                dev.Name = item["name"].AsString(item["Name"].AsString(item["description"].AsString("Nepoznat uređaj")));
                                dev.DeviceId = item["device_id"].AsString(item["DeviceId"].AsString(""));
                                dev.ErrorCode = item["error_code"].AsInt(item["ErrorCode"].AsInt(item["ConfigManagerErrorCode"].AsInt(0)));
                                dev.ErrorStatus = item["error_status"].AsString(item["ErrorStatus"].AsString(string.Format("Kôd greške {0}", dev.ErrorCode)));
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
                            ViewModel.ConnectionStatusText = "✓ Wi-Fi konekcija aktivna";
                            ViewModel.ConnectionStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                        }
                        else if (ctLower.Contains("ethernet") || ctLower.Contains("lan"))
                        {
                            ViewModel.ConnectionStatusText = "✓ Ethernet (LAN) aktivan";
                            ViewModel.ConnectionStatusBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                        }
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
            }
            catch (Exception ex)
            {
                ViewModel.AddLog("DEBUG", "JSON", string.Format("Greška parsiranja: {0}", ex.Message));
            }
        }

        private void HandleSpecificEvent(string eventType, JsonNode node)
        {
            switch (eventType.ToUpperInvariant())
            {
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
                            HandleCriticalThermalAbort(2);
                        }
                    }
                    break;

                case "COMPLETED":
                case "COMPLETE":
                case "FINISHED":
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
                        HandleCriticalThermalAbort(2);
                        break;
                    }
                    ViewModel.DeploymentStatus = "READY";
                    ViewModel.IsCompleted = true;
                    ViewModel.IsServiceFinished = true;
                    if (isSensorUnavailCompleted)
                    {
                        ViewModel.StressResultStatus = "UNKNOWN";
                        ViewModel.CompletionSummary = "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.";
                        ViewModel.LiveStatus = "⚠️ Instalacija uspešna. Dijagnostika hlađenja: Hlađenje nije automatski potvrđeno; obavezna ručna provera.";
                        ViewModel.ThermalVerdictTitle = "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)";
                        ViewModel.ThermalVerdictDescription = "Hlađenje nije automatski potvrđeno; obavezna ručna provera. Potrebna je ručna provera hladnjaka i ventilatora.";
                        ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18);
                        ViewModel.ThermalRecommendation = "Preporuka: Proverite rad kulera i ventilaciju u BIOS/UEFI ili fizički pre isporuke računara.";
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
                if (MainTabControl.SelectedIndex != 0)
                {
                    if (_autoStartTimer != null && _autoStartTimer.IsEnabled)
                    {
                        _autoStartTimer.Stop();
                        string pauseMsg = "Automatski start pauziran. Omogućen ručni pregled.";
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
                _failedPhase = 0;
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
                    string pauseMsg = "Automatski start pauziran. Omogućen ručni pregled.";
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
                _failedPhase = 0;
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
            ViewModel.LiveStatus = string.Format("Međurestart odložen za 10 minuta (novi termin: {0}).", dueTimeFormatted);
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
                    string.Format("Restart računara je stvarno odložen za 10 minuta.\nNovi termin restarta: {0} (ukupno odloženo: {1} min).", dueTimeFormatted, totalDelayedMin),
                    "WIS Servis — Odlaganje restarta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    string.Format("Restart je odložen u memoriji do {0}, ali upis u deploy-state.json nije uspeo: {1}", dueTimeFormatted, persistError ?? "Nepoznata greška"),
                    "WIS Servis — Upozorenje",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ExecuteShowCompletionSummary(object param)
        {
            ViewModel.IsCompleted = true;
        }

        private void ExecuteCancelShutdown(object param)
        {
            ViewModel.ShutdownTimerActive = false;
            ViewModel.IsCompleted = false; // Skloni completion overlay da se mogu pregledati tabovi
            ViewModel.IsServiceFinished = true; // Trajno zadrži završnu traku u dnu ekrana
            ViewModel.AddLog("INFO", "Korisnik", "Izabran ostanak u programu radi pregleda. Dugme [🔴 OČISTI I UGASI RAČUNAR] je dostupno u donjoj traci.");

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "cancel_shutdown" },
                { "timestamp", DateTime.Now.ToString("o") }
            });
        }

        private void ExecuteShutdownNow(object param)
        {
            ViewModel.ShutdownTimerActive = false;
            ViewModel.AddLog("WARN", "Korisnik", "Zahtevano trenutno gašenje računara.");

            StopScreenCaptureWorker();

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "shutdown_now" },
                { "timestamp", DateTime.Now.ToString("o") }
            });

            try
            {
                Process.Start("shutdown.exe", "/s /t 1 /c \"WIS: Trenutno gasenje na zahtev tehnicara.\"");
            }
            catch { }

            Close();
        }

        private void ExecuteCleanAndShutdown(object param)
        {
            ViewModel.AddLog("INFO", "Sistem", "Čišćenje servisnih tragova i gašenje računara...");
            ViewModel.CompletionTitle = "ČIŠĆENJE I GAŠENJE...";
            ViewModel.CompletionSummary = "Čišćenje servisnih tragova i gašenje računara...";

            ThreadPool.QueueUserWorkItem(delegate(object _)
            {
                try
                {
                    // U2 (Paket C2): Prvo čisto zaustavi Screen Capture worker pre brisanja C:\setup
                    StopScreenCaptureWorker();

                    // a) Briše lozinku i Wi-Fi profile: ako postoji C:\setup\Wifi-Servis.ini briše ga
                    string wifiIni = @"C:\setup\Wifi-Servis.ini";
                    if (File.Exists(wifiIni))
                    {
                        try { File.Delete(wifiIni); } catch { }
                    }

                    // Briše i privremeni Registry ključ HKLM\SOFTWARE\WIS za InitialStartTime
                    try
                    {
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\WIS", false);
                    }
                    catch { }

                    // b) Agresivno briše prečice sa radne površine (Desktop):
                    // Pretražuje C:\Users\Public\Desktop i direktorijume C:\Users\*\Desktop i briše:
                    // *Launcher*.lnk, *WIS*.lnk, *WIS*.*, *GWADT*.lnk, *GWADT*.*, *Pokreni*.bat, *Pokreni*.lnk, setup.lnk
                    string[] patterns = new string[]
                    {
                        "*Launcher*.lnk",
                        "*WIS*.lnk",
                        "*WIS*.*",
                        "*GWADT*.lnk",
                        "*GWADT*.*",
                        "*Pokreni*.bat",
                        "*Pokreni*.lnk",
                        "setup.lnk"
                    };

                    List<string> desktopDirs = new List<string>();
                    string publicDesktop = @"C:\Users\Public\Desktop";
                    if (Directory.Exists(publicDesktop))
                    {
                        desktopDirs.Add(publicDesktop);
                    }

                    string usersDir = @"C:\Users";
                    if (Directory.Exists(usersDir))
                    {
                        try
                        {
                            string[] userDirs = Directory.GetDirectories(usersDir);
                            for (int i = 0; i < userDirs.Length; i++)
                            {
                                string userDesktop = Path.Combine(userDirs[i], "Desktop");
                                if (Directory.Exists(userDesktop) && !desktopDirs.Contains(userDesktop))
                                {
                                    desktopDirs.Add(userDesktop);
                                }
                            }
                        }
                        catch { }
                    }

                    for (int d = 0; d < desktopDirs.Count; d++)
                    {
                        string dir = desktopDirs[d];
                        for (int p = 0; p < patterns.Length; p++)
                        {
                            try
                            {
                                string[] files = Directory.GetFiles(dir, patterns[p]);
                                for (int f = 0; f < files.Length; f++)
                                {
                                    try { File.Delete(files[f]); } catch { }
                                }
                            }
                            catch { }
                        }
                    }

                    // c) Poziva osvežavanje Explorer shell-a (SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero))
                    try
                    {
                        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }

                    // d) Pokreće odloženo brisanje C:\setup i gašenje:
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c timeout /t 2 /nobreak >nul & rmdir /s /q C:\\setup >nul 2>&1 & shutdown.exe /s /t 3 /c \"WIS: Servis zavrsen i ociscen za pakovanje.\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                    }
                    catch { }
                }
                catch { }

                // e) Zatvara prozor Close() i gasi proces
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { Close(); } catch { }
                    try { Environment.Exit(0); } catch { }
                }));
            });
        }

        private void ExecuteExitWithoutClean(object param)
        {
            ViewModel.AddLog("INFO", "Korisnik", "Izlazak iz aplikacije bez čišćenja servisnih tragova.");
            StopScreenCaptureWorker();
            try { Close(); } catch { }
            try { Environment.Exit(0); } catch { }
        }

        private void ExecuteRerunPhase4(object param)
        {
            RerunPhase4();
        }

        public void RerunPhase4()
        {
            Dispatcher.BeginInvoke((Action)delegate
            {
                if (ViewModel.Apps != null)
                {
                    foreach (var app in ViewModel.Apps)
                    {
                        app.Status = "WAIT";
                        app.Detail = "U redu čekanja";
                    }
                }
                ViewModel.AddLog("INFO", "TaskRunner", "[TaskRunner] Pokrećem ponovnu instalaciju programa...");
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ExecutePhase(4);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.AddLog("ERROR", "TaskRunner", "Greška pri ponovnoj instalaciji programa: " + ex.Message);
                    });
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
                ViewModel.Autopilot.StatusTitle = "PROVERA AUTOPILOTA U TOKU...";
                ViewModel.Autopilot.StatusDescription = "Sistem vrši duboku forenziku lokalnog registra i proveru Microsoft ZTD oblaka...";
                ViewModel.Autopilot.RecommendedAction = "Molimo sačekajte završetak bezbednosne provere...";
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
                            ViewModel.Autopilot.StatusTitle = "GREŠKA PRI PROVERI";
                            ViewModel.Autopilot.StatusDescription = "Izuzetak na pozadinskoj niti: " + ex.Message;
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

        private void ExecuteAutopilotScanWork()
        {
            try
            {
                bool isNativeAuditor;
                string toolPath = FindAutopilotTool(out isNativeAuditor);
                if (string.IsNullOrEmpty(toolPath))
                {
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
                        Arguments = "--json",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
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
                        Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -JsonOutput", toolPath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
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

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    UpdateAutopilotFromData(root);
                }));
            }
            catch (Exception ex)
            {
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

            if (root.ContainsKey("device_serial") && !string.IsNullOrEmpty(root["device_serial"].AsString())) ViewModel.Autopilot.HwSerialNumber = root["device_serial"].AsString();
            if (root.ContainsKey("device_model") && !string.IsNullOrEmpty(root["device_model"].AsString())) ViewModel.Autopilot.HwModel = root["device_model"].AsString();

            var hwNode = root["hardware"];
            if (hwNode.IsObject)
            {
                if (hwNode.ContainsKey("manufacturer")) ViewModel.Autopilot.HwManufacturer = hwNode["manufacturer"].AsString(ViewModel.Autopilot.HwManufacturer);
                if (hwNode.ContainsKey("model")) ViewModel.Autopilot.HwModel = hwNode["model"].AsString(ViewModel.Autopilot.HwModel);
                if (hwNode.ContainsKey("serial_number")) ViewModel.Autopilot.HwSerialNumber = hwNode["serial_number"].AsString(ViewModel.Autopilot.HwSerialNumber);
                if (hwNode.ContainsKey("smbios_uuid")) ViewModel.Autopilot.HwSmbiosUuid = hwNode["smbios_uuid"].AsString(ViewModel.Autopilot.HwSmbiosUuid);

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
            // 1. PROCESOR (CPU)
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

                // Fallback na Registry (0ms čak i ako WMI zakaže)
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
                cpuName = System.Text.RegularExpressions.Regex.Replace(cpuName, @"\s+", " ").Trim();

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
                    ViewModel.HardwareInfo.CpuModel = finalCpuName;
                    ViewModel.HardwareInfo.CpuCoresThreads = finalCores;
                    ViewModel.HardwareInfo.CpuClock = finalClock;
                    ViewModel.HardwareInfo.CpuSocket = finalSocket;

                    if (string.IsNullOrEmpty(ViewModel.CpuInfo) || ViewModel.CpuInfo.StartsWith("Detekcija"))
                    {
                        ViewModel.CpuInfo = finalCpuName;
                    }
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.CpuModel = "Greška pri detekciji CPU";
                });
            }

            // 2. RADNA MEMORIJA (RAM)
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

                // Ako WMI nije dao tačne bajtove ili je dao 0, koristi siguran Kernel32 GlobalMemoryStatusEx
                if (totalBytes == 0 && kernelTotalBytes > 0)
                {
                    totalBytes = kernelTotalBytes;
                }

                // Detekcija ukupnih slotova preko Win32_PhysicalMemoryArray
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

                    if (string.IsNullOrEmpty(ViewModel.RamInfo) || ViewModel.RamInfo.StartsWith("Detekcija"))
                    {
                        ViewModel.RamInfo = string.Format("{0} GB {1} {2}", totalGb, ramType, speedStr);
                    }
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.RamSummary = "Greška pri detekciji RAM";
                    ViewModel.HardwareInfo.RamSlotsSummary = "N/A";
                });
            }

            // 3. SKLADIŠTE / DISK
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

                            if (string.IsNullOrEmpty(ViewModel.DiskInfo) || ViewModel.DiskInfo.StartsWith("Detekcija"))
                            {
                                ViewModel.DiskInfo = finalModel;
                            }
                        });
                        break;
                    }
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.StorageModel = "Greška pri detekciji diska";
                });
            }

            // 4. BATERIJA & NAPAJANJE (v2.12.0 - HW-02)
            bool isLaptopDevice = false;
            try
            {
                bool hasBattery = false;
                uint designCap = 0;
                uint fullCap = 0;
                uint cycleCount = 0;
                bool hasCycles = false;

                // Pokušaj primarno iz WMI root\wmi (precizni podaci na Windows 10 i 11)
                try
                {
                    ManagementScope wmiScope = new ManagementScope(@"\\.\root\wmi");
                    wmiScope.Connect();

                    // BatteryStaticData -> DesignedCapacity
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

                    // BatteryFullChargedCapacity -> FullChargedCapacity
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

                    // BatteryCycleCount -> CycleCount
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

                // Rezervni fallback na Win32_Battery ako root\wmi nije vratio kapacitete
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

                if (hasBattery) isLaptopDevice = true;

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
                        healthBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113); // #2ECC71
                    }
                    else if (wearLevel < 35.0)
                    {
                        healthBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15); // #F1C40F
                    }
                    else
                    {
                        healthBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);  // #E74C3C
                    }

                    string cyclesStr = hasCycles ? string.Format("{0} ciklusa", cycleCount) : (!string.IsNullOrEmpty(ViewModel.BatteryCycles) && ViewModel.BatteryCycles != "N/A" ? ViewModel.BatteryCycles : "Detektovano");

                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.HardwareInfo.BatteryWear = wearStr;
                        ViewModel.HardwareInfo.BatteryCapacity = capStr;
                        ViewModel.HardwareInfo.BatteryCycles = cyclesStr;
                        ViewModel.HardwareInfo.BatteryPowerStatus = powerStr;
                        ViewModel.HardwareInfo.BatteryHealthBrush = healthBrush;

                        ViewModel.BatteryHealth = (int)Math.Max(0, Math.Min(100, Math.Round(100.0 - wearLevel)));
                        ViewModel.BatteryCycles = cyclesStr;
                        ViewModel.BatteryStatus = powerStr;
                        ViewModel.IsLaptop = true;
                    });
                }
                else if (hasBattery)
                {
                    string powerStr = (batteryStatus == 2) ? "Punjenje (AC Adapter)" : (batteryStatus == 1 ? "Na bateriji (DC)" : "AC Mreža / Baterija");
                    string cyclesStr = hasCycles ? string.Format("{0} ciklusa", cycleCount) : "N/A";
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.HardwareInfo.BatteryWear = "Detektovana baterija";
                        ViewModel.HardwareInfo.BatteryCapacity = "N/A";
                        ViewModel.HardwareInfo.BatteryCycles = cyclesStr;
                        ViewModel.HardwareInfo.BatteryPowerStatus = powerStr;
                        ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);

                        ViewModel.IsLaptop = true;
                    });
                }
                else
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        ViewModel.HardwareInfo.BatteryWear = "Nema baterije (Desktop / AC Only)";
                        ViewModel.HardwareInfo.BatteryCapacity = "N/A (Fiksno AC napajanje)";
                        ViewModel.HardwareInfo.BatteryCycles = "N/A";
                        ViewModel.HardwareInfo.BatteryPowerStatus = "Desktop AC Napajanje";
                        ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);

                        ViewModel.BatteryHealth = 0;
                        ViewModel.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                        ViewModel.BatteryHealthText = "Fiksno napajanje (Desktop)";
                        ViewModel.BatteryCycles = "N/A";
                        ViewModel.BatteryStatus = "Desktop (Fiksno AC)";
                        ViewModel.IsLaptop = false;
                    });
                }
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.BatteryWear = "Nema baterije (Desktop / AC Only)";
                    ViewModel.HardwareInfo.BatteryCapacity = "N/A";
                    ViewModel.HardwareInfo.BatteryCycles = "N/A";
                    ViewModel.HardwareInfo.BatteryPowerStatus = "AC Napajanje";
                    ViewModel.HardwareInfo.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);

                    ViewModel.BatteryHealth = 0;
                    ViewModel.BatteryHealthBrush = BaseViewModel.CreateFrozenBrush(100, 100, 100);
                    ViewModel.BatteryHealthText = "Fiksno napajanje (Desktop)";
                    ViewModel.BatteryCycles = "N/A";
                    ViewModel.BatteryStatus = "Desktop (Fiksno AC)";
                    ViewModel.IsLaptop = false;
                });
            }

            // 5. GPU & DISPLEJ
            try
            {
                List<string> gpus = new List<string>();
                List<ulong> gpuRams = new List<ulong>();
                int screenW = 0;
                int screenH = 0;
                int refreshRate = 60;

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "";
                        ulong ram = 0;
                        if (obj["AdapterRAM"] != null)
                        {
                            ulong.TryParse(obj["AdapterRAM"].ToString(), out ram);
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (!gpus.Contains(name))
                            {
                                gpus.Add(name);
                                gpuRams.Add(ram);
                            }
                        }

                        int w = 0, h = 0, hz = 0;
                        if (obj["CurrentHorizontalResolution"] != null) int.TryParse(obj["CurrentHorizontalResolution"].ToString(), out w);
                        if (obj["CurrentVerticalResolution"] != null) int.TryParse(obj["CurrentVerticalResolution"].ToString(), out h);
                        if (obj["CurrentRefreshRate"] != null) int.TryParse(obj["CurrentRefreshRate"].ToString(), out hz);

                        if (w > 0 && h > 0)
                        {
                            screenW = w;
                            screenH = h;
                            if (hz > 0) refreshRate = hz;
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

                string gpu1 = gpus.Count > 0 ? gpus[0] : "Standardni VGA grafički adapter";
                ulong ram1 = gpuRams.Count > 0 ? gpuRams[0] : 0;
                string gpu1Vram = GetGpuVram(gpu1, ram1);

                string gpu2 = gpus.Count > 1 ? gpus[1] : "Nema (Single GPU)";
                ulong ram2 = gpuRams.Count > 1 ? gpuRams[1] : 0;
                bool hasDual = gpus.Count > 1;
                string gpu2Vram = hasDual ? GetGpuVram(gpu2, ram2) : "";

                string formatTag = "";
                if (screenW == 1920 && screenH == 1080) formatTag = "(FHD)";
                else if (screenW == 1920 && screenH == 1200) formatTag = "(FHD+)";
                else if (screenW == 2560 && screenH == 1440) formatTag = "(QHD / 2K)";
                else if (screenW == 2560 && screenH == 1600) formatTag = "(QHD+)";
                else if (screenW == 3840 && screenH == 2160) formatTag = "(4K UHD)";
                else if (screenW == 1366 && screenH == 768) formatTag = "(HD)";
                else if (screenW == 1600 && screenH == 900) formatTag = "(HD+)";

                // Detekcija Touchscreen preko GetSystemMetrics
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

                // Detekcija tipa panela ekrana (IPS vs OLED vs TN) - HW-01
                string panelType = DetectDisplayPanelType(screenW, screenH, isLaptopDevice || ViewModel.IsLaptop);

                // Napredna specifikacija ekrana: model panela, osvetljenje i gamut boja (v2.14.0 - HW-03)
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
                    ViewModel.HardwareInfo.GpuPrimary = gpu1;
                    ViewModel.HardwareInfo.GpuPrimaryVram = gpu1Vram;
                    ViewModel.HardwareInfo.GpuSecondary = gpu2;
                    ViewModel.HardwareInfo.GpuSecondaryVram = gpu2Vram;
                    ViewModel.HardwareInfo.HasDualGpu = hasDual;
                    ViewModel.HardwareInfo.DisplaySpec = displaySpecStr;
                    ViewModel.HardwareInfo.DisplayModel = finalDisplayModel;
                    ViewModel.HardwareInfo.DisplayNits = finalDisplayNits;
                    ViewModel.HardwareInfo.DisplayColorGamut = finalDisplayGamut;
                    ViewModel.HardwareInfo.HasTouchscreen = hasTouch;
                    ViewModel.HardwareInfo.TouchscreenStatus = touchStatusStr;
                    ViewModel.HardwareInfo.TouchscreenBrush = touchBrush;

                    if (string.IsNullOrEmpty(ViewModel.GpuName) || ViewModel.GpuName.StartsWith("Detekcija"))
                    {
                        ViewModel.GpuName = gpu1;
                    }
                    ViewModel.DisplayResolution = string.Format("{0}x{1}", screenW, screenH);
                    ViewModel.DisplayRefreshRate = string.Format("{0} Hz", refreshRate);
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.GpuPrimary = "Greška pri detekciji GPU";
                    ViewModel.HardwareInfo.GpuPrimaryVram = "N/A";
                    ViewModel.HardwareInfo.GpuSecondary = "";
                    ViewModel.HardwareInfo.GpuSecondaryVram = "";
                    ViewModel.HardwareInfo.DisplaySpec = "N/A";
                    ViewModel.HardwareInfo.DisplayModel = "N/A";
                    ViewModel.HardwareInfo.DisplayNits = "N/A";
                    ViewModel.HardwareInfo.DisplayColorGamut = "N/A";
                    ViewModel.HardwareInfo.HasTouchscreen = false;
                    ViewModel.HardwareInfo.TouchscreenStatus = "N/A";
                });
            }

            // 6. MATIČNA PLOČA & BIOS & SECUREBOOT & MODEL DETEKCIJA
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

                // Win32_ComputerSystem
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

                // Win32_ComputerSystemProduct
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

                // Win32_BaseBoard
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

                // Win32 Registry fallback (HARDWARE\DESCRIPTION\System\BIOS)
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

                // Logika normalizacije modela:
                string rawMfg = (csMfg ?? "").Trim();
                string rawModel = (csModel ?? "").Trim();
                string cspVer = (cspVersion ?? "").Trim();
                string cspName = (cspProductName ?? "").Trim();
                string bbProd = (baseBoardProd ?? "").Trim();
                string bbMfg = (baseBoardMfg ?? "").Trim();

                string finalModel = "";
                // Dell:
                if (rawMfg.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finalModel = !string.IsNullOrEmpty(rawModel) ? "Dell " + rawModel : "Dell Inc.";
                }
                // Lenovo:
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
                // HP:
                else if (rawMfg.IndexOf("HP", StringComparison.OrdinalIgnoreCase) >= 0 || rawMfg.IndexOf("Hewlett", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finalModel = rawModel.StartsWith("HP", StringComparison.OrdinalIgnoreCase) ? rawModel : "HP " + rawModel;
                }
                // Custom desktop build (Asus, Gigabyte, MSI, ASRock) gde je model "System Product Name" ili "To be filled by O.E.M.":
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
                    ViewModel.HardwareInfo.MotherboardModel = finalMbProd;
                    ViewModel.HardwareInfo.MotherboardManufacturer = finalMbMfg;
                    ViewModel.HardwareInfo.SerialNumber = finalSerial;
                    ViewModel.HardwareInfo.BiosVersionDate = biosFull;
                    ViewModel.HardwareInfo.SecureBootStatus = secureBootStr;

                    ViewModel.ComputerModel = finalModel;
                    ViewModel.Autopilot.HwModel = finalModel;

                    if (string.IsNullOrEmpty(ViewModel.SerialNumber) || ViewModel.SerialNumber == "N/A")
                    {
                        ViewModel.SerialNumber = finalSerial;
                    }
                    ViewModel.BiosVersion = biosVer;
                    ViewModel.BiosReleaseDate = biosDate;
                    ViewModel.SecureBootStatus = secureBootStr;
                });
            }
            catch
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.HardwareInfo.MotherboardModel = "Greška pri detekciji ploče";
                });
            }

            // 7. PERIFERIJE & BIOMETRIJA & AUDIO (v2.14.0 - HW-04..HW-07 / I18N-01)
            try
            {
                // a) Veb kamera
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

                // b) IR kamera (Windows Hello Face) - HW-04: Eliminacija lažno pozitivnih
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

                            // Isključi virtuelne i softverske uređaje (HW-04)
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

                // c) Svetleća tastatura (Pozadinsko osvetljenje) - HW-06
                string backlitStatus = LocalizationService.Instance.GetString("HwKbNo", "Standardna (Bez osvetljenja)");
                bool hasBacklit = false;
                bool backlitIsManual = false;
                SolidColorBrush backlitBrush = BaseViewModel.CreateFrozenBrush(136, 136, 136);

                try
                {
                    // 1. PnP pretraga
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

                    // 2. Lenovo OEM WMI upit
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

                    // 3. Dell OEM WMI upit
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

                    // 4. HP OEM BIOS WMI upit
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

                    // Trostepeni status za laptopove gde WMI telemetrija nije izložena
                    if (!hasBacklit && (isLaptopDevice || ViewModel.IsLaptop))
                    {
                        backlitIsManual = true;
                        backlitStatus = LocalizationService.Instance.GetString("HwKbCheckManual", "Standardna / Proveri ručno (Fn+Space)");
                        backlitBrush = BaseViewModel.CreateFrozenBrush(241, 196, 15);
                    }
                }
                catch { }

                // d) Čitač otiska prsta (Biometric) - HW-05: Eliminacija virtuelnog Hello Face
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

                            // Isključi virtuelni softverski uređaj Windows Hello Face (HW-05)
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

                // e) Audio podsistem i mikrofon (v2.14.0 - HW-07)
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

                // Thread-safe ažuriranje modela
                Dispatcher.BeginInvoke((Action)delegate
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
                                            // 64-bit HardwareInformation.qwMemorySize (zaobilazi 4GB WMI limit za dGPU)
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

                                            // Fallback na HardwareInformation.MemorySize (DWORD ili binary)
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

            // Provera da li je integrisana grafika
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

            if (finalBytes >= 1024UL * 1024UL * 1024UL) // >= 1 GB
            {
                double gb = Math.Round((double)finalBytes / (1024.0 * 1024.0 * 1024.0));
                if (isIntegrated)
                {
                    return string.Format("{0} GB (Deljena sistemska memorija)", (int)gb);
                }
                return string.Format("{0} GB VRAM", (int)gb);
            }
            else if (finalBytes >= 1024UL * 1024UL) // >= 1 MB i < 1 GB
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

        #region Detekcija Tipa Panela Displeja (v2.12.0 - HW-01)

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

                // 1. OLED / AMOLED provera
                if (rawText.Contains("OLED") || rawText.Contains("AMOLED"))
                {
                    return "OLED";
                }

                // 2. Eksplicitne IPS / AHVA / PLS / ADS / IGZO oznake
                if (rawText.Contains("IPS") || rawText.Contains("AHVA") || rawText.Contains("PLS") ||
                    rawText.Contains("ADS") || rawText.Contains("AH-IPS") || rawText.Contains("IGZO"))
                {
                    return "IPS";
                }

                // 3. Poznate serije laptop IPS panela
                bool hasKnownIpsPrefix = rawText.Contains("NV13") || rawText.Contains("NV14") || rawText.Contains("NV15") || rawText.Contains("NV16") || rawText.Contains("NV17") ||
                                        rawText.Contains("LP13") || rawText.Contains("LP14") || rawText.Contains("LP15") || rawText.Contains("LP16") || rawText.Contains("LP17") ||
                                        rawText.Contains("B140HAN") || rawText.Contains("B156HAN") || rawText.Contains("B173HAN") ||
                                        rawText.Contains("N140HCE") || rawText.Contains("N156HCE") || rawText.Contains("N173HCE");
                if (hasKnownIpsPrefix)
                {
                    return "IPS";
                }

                // 4. Laptop ekrani sa modernom rezolucijom (FHD i veće) su praktično svi IPS paneli
                if (isLaptop && screenW >= 1920 && screenH >= 1080)
                {
                    return "IPS";
                }

                // Niska rezolucija na laptopu (1366x768 ili 1600x900) bez IPS oznaka je obično TN
                if (isLaptop && (screenW <= 1366 || (screenW == 1600 && screenH == 900)))
                {
                    return "TN / Standard";
                }

                // Proizvođači laptop panela uz FHD+
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

        /// <summary>
        /// Heuristička i EDID kalkulacija specifikacija panela.
        /// Osvetljenje (nits) i gamut boja se aproksimiraju tabelarno i preko CIE xy Shoelace formule,
        /// s obzirom da standardni EDID blok 1.3/1.4 ne sadrži direktan fabrički nits deskriptor.
        /// </summary>
        private static void GetEdidDisplayDetails(out string model, out int nits, out int srgb, out int ntsc)
        {
            model = "";
            nits = 0;
            srgb = 0;
            ntsc = 0;

            byte[] edidBytes = null;

            // 1. Očitavanje sirovog EDID bloka iz Registra (HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY)
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

            // Fallback: WmiMonitorID
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
                // a) Ekstrakcija naziva modela iz deskriptor blokova (ofseti 54, 72, 90, 108)
                int[] descriptorOffsets = new int[] { 54, 72, 90, 108 };
                foreach (int off in descriptorOffsets)
                {
                    if (off + 18 <= edidBytes.Length)
                    {
                        // Tag 0xFC = Display Product Name
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

                // b) Proračun CIE xy hromatskih koordinata za % sRGB i % NTSC iz bajtova 0x19-0x22 (indeksi 25-34)
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

                    // Površina hromatskog trougla (Shoelace formula): 0.5 * |rx*(gy-by) + gx*(by-ry) + bx*(ry-gy)|
                    double area = 0.5 * Math.Abs(rX * (gY - bY) + gX * (bY - rY) + bX * (rY - gY));
                    if (area > 0.02 && area < 0.25)
                    {
                        ntsc = (int)Math.Round((area / 0.1582) * 100.0);
                        srgb = (int)Math.Round((area / 0.1118) * 100.0);
                    }
                }
                catch { }
            }

            // Heuristički fallback za Gamut i Nits ako hromatske tačke nisu očitane
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

        #endregion

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
            try
            {
                // 1. CPU Zauzeće (DIAG-01: GetSystemTimes delta %)
                string cpuUsageStr = null;
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
                        uint memLoad = memStatus.dwMemoryLoad;
                        ramUsageStr = string.Format("{0:0.0} GB / {1:0.0} GB ({2}%)", usedGb, totalGb, memLoad);
                    }
                }
                catch { }

                // 3. Mreža: Protok i Preuzeti Paketi (NET-03)
                string downloadedMbStr = null;
                try
                {
                    long currentTotalBytes = 0;
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
                                }
                            }
                            catch { }
                        }
                    }

                    if (_prevNetBytes >= 0)
                    {
                        if (currentTotalBytes >= _prevNetBytes)
                        {
                            long deltaBytes = currentTotalBytes - _prevNetBytes;
                            _accumulatedNetBytes += deltaBytes;
                            double speedMb = deltaBytes / (1024.0 * 1024.0);
                            double totalMbReceived = _accumulatedNetBytes / (1024.0 * 1024.0);
                            downloadedMbStr = string.Format("{0:0.0} MB/s ({1:N0} MB ukupno)", speedMb, totalMbReceived);
                        }
                    }
                    _prevNetBytes = currentTotalBytes;
                }
                catch { }

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
                                            detectedConnType = "Wi-Fi (Bežična mreža)";
                                            detectedConnStatus = "✓ Wi-Fi konekcija aktivna";
                                            detectedConnBrush = BaseViewModel.CreateFrozenBrush(46, 204, 113);
                                        }
                                        else
                                        {
                                            detectedConnType = "Ethernet (LAN kabl)";
                                            detectedConnStatus = "✓ Ethernet (LAN) aktivan";
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
                            detectedConnStatus = "⚠ Nema mrežne veze";
                            detectedConnBrush = BaseViewModel.CreateFrozenBrush(231, 76, 60);
                        }
                    }
                    catch { }
                }

                // Ažuriranje UI-ja preko Dispatcher-a
                string finalCpu = cpuUsageStr;
                string finalRam = ramUsageStr;
                string finalDl = downloadedMbStr;
                string finalIp = detectedIp;
                string finalConn = detectedConnType;
                string finalConnStatus = detectedConnStatus;
                Brush finalConnBrush = detectedConnBrush;

                Dispatcher.BeginInvoke((Action)delegate
                {
                    if (!string.IsNullOrEmpty(finalCpu)) ViewModel.CpuUsage = finalCpu;
                    if (!string.IsNullOrEmpty(finalRam)) ViewModel.RamUsage = finalRam;
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
                                        ViewModel.NasStatus = "Povezan (192.168.170.254)";
                                        ViewModel.NasLatency = string.Format("{0} ms", reply.RoundtripTime);
                                    });
                                }
                                else
                                {
                                    Dispatcher.BeginInvoke((Action)delegate
                                    {
                                        ViewModel.NasStatus = "Lokalno (Offline)";
                                        ViewModel.NasLatency = "N/A";
                                    });
                                }
                            }
                        }
                        catch
                        {
                            Dispatcher.BeginInvoke((Action)delegate
                            {
                                ViewModel.NasStatus = "Lokalno (Offline)";
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
                "http://192.168.170.254:8000",
                "http://192.168.170.250:8000",
                "http://127.0.0.1:8000",
                "http://192.168.88.50:8000"
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

            return "http://192.168.170.254:8000";
        }

        private void ExecuteInstallDriver(object param)
        {
            DriverItemViewModel item = param as DriverItemViewModel;
            if (item == null || item.IsInstalling || item.IsResolved) return;

            item.IsInstalling = true;
            item.CanInstall = false;
            item.InstallStatusText = "Povezivanje sa ServisHub...";
            item.InstallProgressPercent = 5;
            item.InstallSpeedText = "";

            Task.Factory.StartNew(delegate
            {
                InstallSingleDriverInternal(item, true);
            });
        }

        private bool InstallSingleDriverInternal(DriverItemViewModel item, bool triggerRescan)
        {
            try
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    item.IsInstalling = true;
                    item.CanInstall = false;
                    item.InstallStatusText = "Povezivanje sa ServisHub...";
                    item.InstallProgressPercent = 10;
                });

                string hubUrl = GetServisHubBaseUrl();
                string resolveUrl = hubUrl + "/api/drivers/resolve";

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
                reqDict["model"] = "";

                string jsonReq = JsonHelper.Serialize(reqDict);
                byte[] reqBytes = Encoding.UTF8.GetBytes(jsonReq);

                var httpReq = (HttpWebRequest)WebRequest.Create(resolveUrl);
                httpReq.Method = "POST";
                httpReq.ContentType = "application/json; charset=utf-8";
                httpReq.Timeout = 30000;
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
                JsonNode resultsNode = rootNode["results"];
                if (resultsNode.IsNull || resultsNode.Count == 0)
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        item.InstallStatusText = "Nije pronađen na OEM katalogu";
                        item.IsInstalling = false;
                        item.CanInstall = true;
                        ViewModel.AddLog("WARN", "Drajveri", string.Format("Drajver za '{0}' nije pronađen na ServisHub-u.", item.Name));
                    });
                    return false;
                }

                JsonNode firstRes = resultsNode[0];
                string status = firstRes["status"].AsString("");

                if (status.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    string dlUrl = firstRes["download_url"].AsString("");
                    if (string.IsNullOrEmpty(dlUrl))
                    {
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            item.InstallStatusText = "Neispravan download URL";
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
                        item.InstallStatusText = "Preuzimanje zvaničnog OEM drajvera...";
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
                        item.InstallStatusText = "Instalacija drajvera (pnputil)...";
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
                            item.InstallStatusText = "✓ Uspešno instalirano!";
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
                                ViewModel.Drivers.MissingSummaryText = "✅ Svi drajveri su uspešno instalirani! (0 problematičnih uređaja)";
                                ViewModel.Drivers.MissingSummaryBrush = BaseViewModel.CreateFrozenBrush(63, 185, 80);
                            }
                            else
                            {
                                ViewModel.Drivers.MissingSummaryText = string.Format("⚠ Pronađeno je {0} uređaja bez odgovarajućeg drajvera ili sa greškom!", ViewModel.Drivers.MissingDevicesCount);
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
                            item.InstallStatusText = string.Format("Greška instalacije (kod {0})", pnpExitCode);
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
                        item.InstallStatusText = "Nije pronađen na OEM katalogu";
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
                    item.InstallStatusText = "Greška pri povezivanju sa ServisHub";
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
            ViewModel.Drivers.BatchStatusText = string.Format("Započinjem rešavanje {0} nedostajućih drajvera...", missingCopy.Count);
            ViewModel.AddLog("INFO", "Drajveri", string.Format("Pokrenuto grupno rešavanje {0} nedostajućih drajvera...", missingCopy.Count));

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
                        ViewModel.Drivers.BatchStatusText = string.Format("Instalacija {0}/{1}: {2}...", currentIdx, total, item.Name);
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
                    ViewModel.Drivers.BatchStatusText = string.Format("Grupna instalacija drajvera završena ({0}/{1} uspešno).", successfulCount, total);
                    ViewModel.AddLog("INFO", "Drajveri", string.Format("Grupna instalacija drajvera završena: {0} od {1} uspešno instalirano.", successfulCount, total));
                });

                Thread.Sleep(1500);

                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsBatchResolving = false;
                    DetectDrivers();
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
                        item.UpdateBadgeText = string.Format("Dostupna nova verzija: {0}", latestVer);
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Pronađeno OEM ažuriranje za '{0}': v{1} ({2}).", item.Name, latestVer, latestDate));
                    }
                    else
                    {
                        item.HasUpdateAvailable = false;
                        item.IsUpToDate = true;
                        item.UpdateBadgeText = "✓ Najnoviji drajver";
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
                item.UpdateBadgeText = "Nadogradnja u toku...";
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
                            item.UpdateBadgeText = "✓ Najnoviji drajver";
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
                ViewModel.Drivers.KeyDriversCheckStatus = "Nema pronađenih ključnih uređaja";
                ViewModel.AddLog("INFO", "Drajveri", "Nema detektovanih ključnih hardverskih uređaja (GPU, Mreža, Zvuk, Bluetooth) za proveru.");
                return;
            }

            ViewModel.Drivers.IsCheckingKeyDrivers = true;
            ViewModel.Drivers.KeyDriversCheckStatus = string.Format("Započinjem proveru 0/{0} ključnih drajvera...", keyDevices.Count);
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
                        ViewModel.Drivers.KeyDriversCheckStatus = string.Format("Provera {0}/{1}: {2}...", currentIdx, total, dev.Name);
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
                        ViewModel.Drivers.KeyDriversCheckStatus = string.Format("Pronađeno {0} ažuriranja ključnih drajvera!", updatesFound);
                        ViewModel.AddLog("INFO", "Drajveri", string.Format("Završena provera ključnih drajvera: pronađeno {0} novijih verzija drajvera.", updatesFound));
                    }
                    else
                    {
                        ViewModel.Drivers.KeyDriversCheckStatus = "Svi ključni drajveri su ažurni!";
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
                case 1: return "Kod 1: Uređaj nije konfigurisan";
                case 3: return "Kod 3: Drajver za ovaj uređaj je oštećen";
                case 10: return "Kod 10: Uređaj ne može da se pokrene";
                case 12: return "Kod 12: Uređaj ne može da pronađe slobodne resurse";
                case 14: return "Kod 14: Potreban je restart računara da bi uređaj radio";
                case 18: return "Kod 18: Reinstalirajte drajvere za ovaj uređaj";
                case 22: return "Kod 22: Uređaj je onemogućen (Disabled)";
                case 24: return "Kod 24: Uređaj nije prisutan ili ne radi ispravno";
                case 28: return "Kod 28: Drajver nije instaliran (Missing Driver)";
                case 29: return "Kod 29: Uređaj je onemogućen u BIOS-u";
                case 31: return "Kod 31: Windows ne može da učita drajvere za ovaj uređaj";
                case 32: return "Kod 32: Drajver (servis) za ovaj uređaj je onemogućen";
                case 37: return "Kod 37: Windows ne može da inicijalizuje drajver uređaja";
                case 39: return "Kod 39: Windows ne može da učita drajver (oštećen ili nedostaje)";
                case 43: return "Kod 43: Windows je zaustavio uređaj zbog prijavljenog problema";
                default: return string.Format("Kod {0}: Problem u konfiguraciji uređaja", code);
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
                            string name = obj["Name"] != null ? obj["Name"].ToString().Trim() : "Nepoznat uređaj";
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
                                ErrorDescription = "Ispravno instaliran",
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
                                ErrorDescription = "Ispravno instaliran",
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
                var catDisplay = new DriverCategoryViewModel { CategoryName = "Grafički adapteri", CategoryCode = "Display", CategoryIcon = "🖥️", IsExpanded = true };
                var catNet = new DriverCategoryViewModel { CategoryName = "Mrežni adapteri", CategoryCode = "Net", CategoryIcon = "🌐", IsExpanded = true };
                var catAudio = new DriverCategoryViewModel { CategoryName = "Zvučne i audio komponente", CategoryCode = "MEDIA", CategoryIcon = "🔊", IsExpanded = true };
                var catDisk = new DriverCategoryViewModel { CategoryName = "Diskovi i skladište", CategoryCode = "DiskDrive", CategoryIcon = "💾", IsExpanded = true };
                var catCamera = new DriverCategoryViewModel { CategoryName = "Kamere i biometrija", CategoryCode = "Camera", CategoryIcon = "📷", IsExpanded = true };
                var catInput = new DriverCategoryViewModel { CategoryName = "Tastature i pokazivači", CategoryCode = "Keyboard", CategoryIcon = "⌨️", IsExpanded = true };
                var catUsb = new DriverCategoryViewModel { CategoryName = "USB i kontroleri", CategoryCode = "USB", CategoryIcon = "🔌", IsExpanded = true };
                var catSystem = new DriverCategoryViewModel { CategoryName = "Sistemski uređaji & Čipset", CategoryCode = "System", CategoryIcon = "⚙️", IsExpanded = false };
                var catOther = new DriverCategoryViewModel { CategoryName = "Ostali uređaji", CategoryCode = "Other", CategoryIcon = "📦", IsExpanded = false };

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
                        ViewModel.Drivers.MissingSummaryText = string.Format("⚠ Pronađeno je {0} uređaja bez odgovarajućeg drajvera ili sa greškom!", missingList.Count);
                        ViewModel.Drivers.MissingSummaryBrush = BaseViewModel.CreateFrozenBrush(248, 81, 73);
                    }
                    else
                    {
                        ViewModel.Drivers.HasMissingDevices = false;
                        ViewModel.Drivers.MissingSummaryText = "✅ Svi drajveri su uspešno instalirani! (0 problematičnih uređaja)";
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
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ViewModel.Drivers.IsScanning = false;
                    ViewModel.AddLog("ERROR", "Drajveri", "Kritična greška tokom detekcije drajvera: " + ex.Message);
                });
            }
        }

        #endregion

        #region Termalna Telemetrija, 5-Minutni Grafikon, Taktovi Jezgara & Stres Test (v2.15.0 - WIS-TASK-131)

        private void Window_Closing(object sender, CancelEventArgs e)
        {
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
                }
                catch { }
            }
            if (_thermalEngine != null)
            {
                _thermalEngine.Dispose();
                _thermalEngine = null;
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

        private void PushThermalGraphSample(float cpu, float gpu, float mb, float ssd, float throttling)
        {
            PointCollection cpuPts;
            PointCollection gpuPts;
            PointCollection mbPts;
            PointCollection ssdPts;
            PointCollection throtPts;

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

                    _cpuHistory[last] = cpu > 0f ? cpu : 0f;
                    _gpuHistory[last] = gpu > 0f ? gpu : 0f;
                    _mbHistory[last] = mb > 0f ? mb : 0f;
                    _ssdHistory[last] = ssd > 0f ? ssd : 0f;
                    _throttleHistory[last] = throttling > 0f ? throttling : 0f;
                }

                cpuPts = BuildGraphPoints(_cpuHistory);
                gpuPts = BuildGraphPoints(_gpuHistory);
                mbPts = BuildGraphPoints(_mbHistory);
                ssdPts = BuildGraphPoints(_ssdHistory);
                throtPts = BuildGraphPoints(_throttleHistory);
            }

            ViewModel.CpuGraphPoints = cpuPts;
            ViewModel.GpuGraphPoints = gpuPts;
            ViewModel.MbGraphPoints = mbPts;
            ViewModel.SsdGraphPoints = ssdPts;
            ViewModel.ThrottleGraphPoints = throtPts;
        }

        private void UpdateCoreClocksTable()
        {
            if (_thermalEngine == null || ViewModel == null) return;

            List<CoreClockSensorInfo> snapshot = _thermalEngine.GetCoreClocksSnapshot();
            if (snapshot == null) return;

            if (ViewModel.CoreClocks.Count != snapshot.Count)
            {
                ViewModel.CoreClocks.Clear();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    CoreClockSensorInfo info = snapshot[i];
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
        }

        private void UpdateThermalTelemetry()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateThermalTelemetry));
                return;
            }

            if (ViewModel == null || _thermalEngine == null)
            {
                return;
            }

            try
            {
                _thermalEngine.Update();

                // 1. CPU Temperatura
                float cpu = _thermalEngine.CpuTemperature;
                ViewModel.CpuTemp = cpu;
                if (cpu > 0f)
                {
                    ViewModel.CpuTempText = string.Format("{0:F1} °C", cpu);
                    ViewModel.CpuTempBrush = MainViewModel.GetThermalBrush(cpu, 65.0f, 80.0f);
                    ViewModel.LegendCpuText = string.Format("CPU Temp ({0:F0}°C)", cpu);
                }
                else
                {
                    ViewModel.CpuTempText = "N/A (WMI/ACPI nedostupan)";
                    ViewModel.CpuTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendCpuText = "CPU Temp (N/A)";
                }

                // 2. GPU Temperatura
                float gpu = _thermalEngine.GpuTemperature;
                ViewModel.GpuTemp = gpu;
                if (gpu > 0f)
                {
                    ViewModel.GpuTempText = string.Format("{0:F1} °C", gpu);
                    ViewModel.GpuTempBrush = MainViewModel.GetThermalBrush(gpu, 75.0f, 85.0f);
                    ViewModel.LegendGpuText = string.Format("GPU Temp ({0:F0}°C)", gpu);
                }
                else
                {
                    ViewModel.GpuTempText = "N/A (Integrisana / Idle)";
                    ViewModel.GpuTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendGpuText = "GPU Temp (N/A)";
                }

                // 3. Matična ploča (MB) Temperatura
                float mb = _thermalEngine.MotherboardTemperature;
                ViewModel.MbTemp = mb;
                if (mb > 0f)
                {
                    ViewModel.MbTempText = string.Format("{0:F1} °C", mb);
                    ViewModel.MbTempBrush = MainViewModel.GetThermalBrush(mb, 60.0f, 75.0f);
                    ViewModel.LegendMbText = string.Format("MB Temp ({0:F0}°C)", mb);
                }
                else
                {
                    ViewModel.MbTempText = "N/A";
                    ViewModel.MbTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendMbText = "MB Temp (N/A)";
                }

                // 4. SSD Temperatura
                float ssd = _thermalEngine.SsdTemperature;
                ViewModel.SsdTemp = ssd;
                if (ssd > 0f)
                {
                    ViewModel.SsdTempText = string.Format("{0:F1} °C", ssd);
                    ViewModel.SsdTempBrush = MainViewModel.GetThermalBrush(ssd, 55.0f, 70.0f);
                    ViewModel.LegendSsdText = string.Format("SSD Temp ({0:F0}°C)", ssd);
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageTemp = string.Format("{0:F1} °C", ssd);
                    }
                }
                else
                {
                    ViewModel.SsdTempText = "N/A (SMART Passthrough)";
                    ViewModel.SsdTempBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendSsdText = "SSD Temp (N/A)";
                    if (ViewModel.HardwareInfo != null)
                    {
                        ViewModel.HardwareInfo.StorageTemp = "N/A (Senzor nedostupan)";
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
                }
                else
                {
                    bool isZh = LocalizationService.Instance.IsChinese;
                    ViewModel.ThrottlingText = isZh ? "不可用" : "Nije dostupno";
                    ViewModel.ThrottlingBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                    ViewModel.LegendThrottleText = isZh ? "Throttling (不可用)" : "Throttling (Nije dostupno)";
                }

                // 6. Ventilator (Fan)
                int fan = _thermalEngine.FanRpm;
                ViewModel.FanRpm = fan;
                if (fan > 0)
                {
                    ViewModel.FanSpeedText = string.Format("{0} RPM", fan);
                    ViewModel.FanSpeedBrush = MainViewModel.CreateFrozenBrush(78, 201, 176);
                }
                else
                {
                    ViewModel.FanSpeedText = _thermalEngine.HasFanData ? "0 RPM (Ugašen/Tihi rad)" : "Pasivno / BIOS kontrola";
                    ViewModel.FanSpeedBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                }

                // 7. Sensor Status
                if (_thermalEngine.IsLhmActive && _thermalEngine.IsSensorAvailable)
                {
                    ViewModel.ThermalSensorStatus = "● LibreHardwareMonitor aktivan (1 Hz)";
                    ViewModel.ThermalSensorStatusBrush = MainViewModel.CreateFrozenBrush(78, 201, 176);
                }
                else if (_thermalEngine.IsSensorAvailable)
                {
                    ViewModel.ThermalSensorStatus = string.Format("● {0} senzor aktivan (1 Hz)", _thermalEngine.SensorSource);
                    ViewModel.ThermalSensorStatusBrush = MainViewModel.CreateFrozenBrush(241, 196, 15);
                }
                else
                {
                    ViewModel.ThermalSensorStatus = "● Termalni senzori nisu dostupni (N/A)";
                    ViewModel.ThermalSensorStatusBrush = MainViewModel.CreateFrozenBrush(140, 140, 140);
                }

                // 8. Ažuriranje 5-minutnog (300s) višekanalnog grafikona i tabele taktova jezgara
                PushThermalGraphSample(cpu, gpu, mb, ssd, throt);
                UpdateCoreClocksTable();
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
            ViewModel.StressTestStatusText = "Kontinuirani multi-core stres test u toku...";
            ViewModel.ThermalVerdictTitle = "Stres test u toku...";
            ViewModel.ThermalVerdictDescription = "Merenje temperature i taktova pod 100% opterećenjem svih jezgara...";
            ViewModel.ThermalVerdictBrush = MainViewModel.CreateFrozenBrush(241, 196, 15);
            ViewModel.ThermalRecommendation = "Kliknite na 'Stop Stres' kada završite proveru hlađenja.";

            CommandManager.InvalidateRequerySuggested();

            _stressCts = new CancellationTokenSource();
            CancellationToken token = _stressCts.Token;

            // Ručni start iz Taba 4 pokreće kontinuirani stres test (durationSeconds = 0) dok korisnik ne klikne Stop Stres ili do 98°C
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

            ViewModel.StressTestStatusText = "Stres test zaustavljen od strane servisera.";
            CommandManager.InvalidateRequerySuggested();
        }

        private void RunStressTestWorker(CancellationToken token, int durationSeconds)
        {
            int coreCount = Math.Max(1, Environment.ProcessorCount);
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
                            Thread.Yield(); // Omogućava OS-u trenutnu obradu UI događaja
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

                // EMERGENCY ABORT NA 98°C
                if (curCpu >= 98.0f)
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
                        ViewModel.StressTestStatusText = string.Format("Stres aktivno ({0:F0}s) | CPU: {1:F1}°C | Max: {2:F1}°C", snapElapsed, snapCpu, snapMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format("Stres aktivno ({0:F0}s) | CPU senzor: N/A", snapElapsed);
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
                verdictTitle = "🔴 HITNO POTREBNO ČIŠĆENJE I ZAMENA PASTE";
                verdictDesc = emergencyAbort ?
                    "Kritično pregrevanje! Test je automatski prekinut na 98°C radi zaštite hardvera." :
                    string.Format("Temperatura je dostigla kritičnih {0:F1}°C! Hladnjak je zapušen ili je pasta sasušena.", maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(231, 76, 60); // #E74C3C
                verdictRec = "Preporuka: Obavezno rastavljanje računara, čišćenje ventilatora i rebara hladnjaka, i nanošenje nove visokokvalitetne termalne paste.";
            }
            else if (maxTemp <= 0.5f || (_thermalEngine != null && !_thermalEngine.IsSensorAvailable))
            {
                verdictTitle = "⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)";
                verdictDesc = "CPU je opterećen 100%, ali senzor temperature nije dostupan (0.0°C). Hlađenje nije moguće automatski potvrditi — obavezna ručna provera ventilatora i izduva!";
                verdictBrush = MainViewModel.CreateFrozenBrush(243, 156, 18); // #F39C12 Žuto/Narandžasto upozorenje
                verdictRec = "Preporuka: Očitavanja senzora nisu dostupna preko LHM/WMI/ACPI. Proverite temperaturu u BIOS/UEFI-ju i fizički proverite rad kulera pre predaje računara.";
            }
            else if (maxTemp >= 80.0f && maxTemp < 95.0f)
            {
                verdictTitle = "🟡 PREPORUČUJE SE PREVENTIVNI SERVIS";
                verdictDesc = string.Format("Maksimalna temperatura je dostigla {0:F1}°C pod punim opterećenjem.", maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(241, 196, 15); // #F1C40F
                verdictRec = "Preporuka: Termalna pasta počinje da gubi elastičnost ili ima naslaga prašine. Preporučuje se servis u narednom periodu.";
            }
            else
            {
                verdictTitle = "🟢 ODLIČNO HLAĐENJE";
                verdictDesc = string.Format("Maksimalna temperatura od samo {0:F1}°C pod punim opterećenjem!", maxTemp);
                verdictBrush = MainViewModel.CreateFrozenBrush(78, 201, 176); // #4EC9B0
                verdictRec = "Preporuka: Hlađenje i termalna pasta su u vrhunskom fabričkom stanju. Nije potrebno servisiranje.";
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
                    ViewModel.StressTestStatusText = "PREKID: Zaštitni prag dostignut (98°C)!";
                }
                else if (isManualCancel)
                {
                    if (finalMaxTemp > 0.5f)
                    {
                        ViewModel.StressTestStatusText = string.Format("Stres test zaustavljen nakon {0}s (Max CPU: {1:F1}°C)", finalActualDuration, finalMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format("Stres test zaustavljen nakon {0}s (Senzor: N/A)", finalActualDuration);
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
                        ViewModel.StressTestStatusText = string.Format("Test uspešno završen ({0}s | Max CPU: {1:F1}°C)", shownDuration, finalMaxTemp);
                    }
                    else
                    {
                        ViewModel.StressTestStatusText = string.Format("Test završen ({0}s | Senzor temperature nije dostupan)", shownDuration);
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
