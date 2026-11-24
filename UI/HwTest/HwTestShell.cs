using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;
using WIS.HwTest.Tests;

namespace WIS.HwTest
{
    public class HwTestShell : Window
    {
        private readonly string _outJsonPath;
        private string _modelName;
        private string _serialNumber;
        private readonly ModelProfileClient _profileClient;
        private readonly HardwareProbeResult _hwInfo;

        private readonly List<TestItemResult> _testItems;
        private readonly Dictionary<string, Border> _cardMap;
        private readonly Dictionary<string, TextBlock> _titleTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _descTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _metricTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> _detailsBtnMap = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> _runBtnMap = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> _biosBtnMap = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _statusIndicatorMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _statusIconMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _spinnerRingMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RotateTransform> _spinnerRotateMap = new Dictionary<string, RotateTransform>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _statusBadgeMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _statusBadgeTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _elapsedTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProgressBar> _runningBarMap = new Dictionary<string, ProgressBar>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _runningStartUtcMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Border _summaryBadgeBorder;
        private readonly TextBlock _summaryBadge;
        private readonly StackPanel _cardsPanel;
        private readonly StackPanel _autoCardsPanel;
        private readonly StackPanel _manualCardsPanel;
        private readonly ProgressBar _overallProgressBar;
        private readonly Stopwatch _runStopwatch;
        private DispatcherTimer _runningUiTimer;
        private bool _showNotApplicable = false;
        private TextBlock _factoryBannerText;
        private Button _btnToggleHidden;

        private readonly AutoDiagnosticsEngine _autoEngine = new AutoDiagnosticsEngine();
        private volatile bool _autoStressRunning = false;
        private readonly CancelFlag _autoStressCancel = new CancelFlag();
        private Button _btnRunAutoSuite;
        private TextBlock _autoSectionStatus;
        private TextBlock _manualSectionCountText;

        // Embedded i Phase 5 orkestracija (WIS v3.0.0)
        private Grid _rootGrid;
        private TextBlock _headerTitleBlock;
        private TextBlock _subTitleBlock;
        private TextBlock _autoSectionTitleBlock;
        private TextBlock _manualSectionTitleBlock;
        private TextBlock _manualSectionSubBlock;
        private Button _btnFinish;
        private Button _btnRunAll;
        private Button _btnResetAllTests;
        private bool _isEmbedded = false;
        private Window _hostWindow = null;
        private Action<HwTestRunResult> _onReportSavedCallback = null;
        private bool _passiveChecksStarted = false;
        private volatile bool _cancelPassiveAutoChecks = false;
        private Thread _passiveAutoThread = null;
        private volatile bool _phase5WorkflowActive = false;
        private volatile bool _waitingForManualPeripherals = false;
        private volatile bool _inSequentialPeripheralRun = false;
        private Window _activePeripheralDialog = null;
        public event Action<List<string>> PendingInteractiveTestsRequired;

        // V344-01: Driver Readiness Gate za interaktivne testove
        private Func<DriverGateState> _driverGateProvider;
        private Action _openDriversTabHandler;
        private bool _driverGateEmergencyUnlocked = false;
        private string _driverGateEmergencyOverrideReason = "";
        private bool _driverGateWasLocked = false;
        private bool _hardwareProbeRefreshedAfterDrivers = false;
        private int _probeRefreshInFlight = 0;
        private long _lastProbeRefreshUtcTicks = 0;
        private volatile bool _reportSavedInSession = false;
        private DriverGateState _lastDriverGateState = null;
        private DispatcherTimer _driverGatePollTimer = null;
        private Border _driverGateBanner = null;
        private TextBlock _driverGateBannerTitle = null;
        private TextBlock _driverGateBannerDetail = null;
        private Button _btnDriverGateOpenDrivers = null;
        private Button _btnDriverGateRecheck = null;
        private Button _btnDriverGateEmergencyUnlock = null;

        public void SetInteractiveTestsGateProvider(Func<DriverGateState> provider)
        {
            _driverGateProvider = provider;
            RefreshDriverGateUi();
        }

        public void SetOpenDriversTabHandler(Action handler)
        {
            _openDriversTabHandler = handler;
            RefreshDriverGateUi();
        }
        public bool IsWaitingForManualTests
        {
            get { return _phase5WorkflowActive && _waitingForManualPeripherals && !_phase5CompletedNotified; }
        }

        public List<string> GetPendingInteractiveTestNames()
        {
            List<string> pending = new List<string>();
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (!t.IsAutomated && (t.Status == TestStatus.Pending || t.Status == TestStatus.Running))
                {
                    pending.Add(t.Title);
                }
            }
            return pending;
        }

        public bool HasUnfinishedInteractiveTests()
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (!t.IsAutomated && (t.Status == TestStatus.Pending || t.Status == TestStatus.Running))
                {
                    return true;
                }
            }
            return false;
        }

        private void RaisePendingInteractiveTestsRequired()
        {
            List<string> pendingNames = GetPendingInteractiveTestNames();
            if (pendingNames.Count == 0) return;

            Action<List<string>> handler = PendingInteractiveTestsRequired;
            if (handler != null)
            {
                try { handler(pendingNames); } catch { }
            }
        }
        private Action<string, string, string, string> _phase5StepCallback = null;
        private Action<HwTestRunResult, int, float, float, bool, bool> _phase5FinishedCallback = null;
        private readonly object _phase5Lock = new object();
        private bool _phase5CompletedNotified = false;
        private int _phase5CpuDurationSec = 0;
        private float _phase5PeakCpuTemp = 0f;
        private float _phase5MaxCpuThrottle = 0f;
        private bool _phase5ThermalCritical = false;
        private string _phase5ThermalNote = "";
        private DateTime? _autoTestsFinishedUtc = null;
        private DateTime? _firstOperatorInteractionUtc = null;

        // HWT-04 / HWT-06: UEFI BIOS restart i Faza 5 checkpoint
        private Action<string> _uefiBiosRebootHandler = null;
        private volatile bool _uefiBiosRebootRequested = false;
        private volatile bool _uefiBiosPromptOpen = false;
        private readonly HashSet<string> _restoredTestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _checkpointRestored = false;
        private int _checkpointRestoredCount = 0;
        private int _checkpointTotalApplicable = 0;

        private void MarkOperatorInteraction()
        {
            if (!_firstOperatorInteractionUtc.HasValue)
            {
                _firstOperatorInteractionUtc = DateTime.UtcNow;
            }
        }

        public HwTestShell(string outJsonPath, string modelName, string serialNumber)
            : this(outJsonPath, modelName, serialNumber, "", "", null)
        {
        }

        public HwTestShell(string outJsonPath, string modelName, string serialNumber, string manufacturer, string sku)
            : this(outJsonPath, modelName, serialNumber, manufacturer, sku, null)
        {
        }

        public HwTestShell(string outJsonPath, string modelName, string serialNumber, string manufacturer, string sku, ModelProfileClient existingClient)
        {
            _outJsonPath = outJsonPath ?? @"C:\setup\hwtest-result.json";
            TryStartupRecoverTouchpadIfNeeded();
            try { NativeMethods.SuppressLocationPromptPopups(); } catch { }
            _profileClient = existingClient ?? new ModelProfileClient();
            _hwInfo = new HardwareProbeResult();

            if (!string.IsNullOrEmpty(manufacturer))
                _hwInfo.Manufacturer = manufacturer;
            if (!string.IsNullOrEmpty(sku))
                _hwInfo.SystemSkuNumber = sku;

            bool emptyModelArg = IsPlaceholderIdentity(modelName);
            _modelName = (!emptyModelArg) ? modelName : "Nepoznat model";

            bool emptySnArg = IsPlaceholderIdentity(serialNumber);
            _serialNumber = (!emptySnArg) ? serialNumber : "Nepoznat serijski broj";

            _hwInfo.ModelName = _modelName;
            _hwInfo.SerialNumber = _serialNumber;

            _testItems = new List<TestItemResult>();
            _cardMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
            _runStopwatch = Stopwatch.StartNew();

            Title = HwTestLocalization.Pick("WIS — Hardverska Verifikacija Uređaja", "WIS — 设备硬件检测");
            Width = 1100;
            Height = 780;
            MinWidth = 900;
            MinHeight = 650;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            SourceInitialized += (s, e) => NativeMethods.EnableDarkTitleBar(this);

            // Inicijalizacija testova (prikazuju se samo prisutni / fabrički očekivani uređaji)
            InitTestList();

            // Restauracija checkpointa Faze 5 posle UEFI BIOS restarta (ako postoji validan checkpoint)
            TryRestoreCheckpoint();
            StartInitialHardwareProbeAsync();

            // Izgradnja UI-ja (kompaktan 2-kolonski list raspored sa živim indikatorima rada)
            Grid root = new Grid();
            _rootGrid = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Kompaktno objedinjeno zaglavlje + fabrička traka + globalni progress bar
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 9, 16, 9)
            };
            StackPanel headerStack = new StackPanel();

            Grid hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel spH = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = new TextBlock
            {
                Text = HwTestLocalization.Pick("HARDVERSKA DIJAGNOSTIKA I TESTIRANJE", "硬件诊断与测试"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            _headerTitleBlock = title;
            spH.Children.Add(title);

            TextBlock sub = new TextBlock
            {
                Text = BuildSubTitleText(),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            _subTitleBlock = sub;
            spH.Children.Add(sub);
            hg.Children.Add(spH);

            StackPanel hdrRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _btnToggleHidden = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 230)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 77, 92)),
                BorderThickness = new Thickness(1),
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnToggleHidden.Click += (s, e) => {
                _showNotApplicable = !_showNotApplicable;
                RefreshCardVisibility();
            };
            hdrRight.Children.Add(_btnToggleHidden);

            _summaryBadge = new TextBlock
            {
                Text = HwTestLocalization.Pick("⏳ Čeka pokretanje", "⏳ 等待启动"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            _summaryBadgeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 72)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _summaryBadge
            };
            hdrRight.Children.Add(_summaryBadgeBorder);

            Grid.SetColumn(hdrRight, 1);
            hg.Children.Add(hdrRight);
            headerStack.Children.Add(hg);

            Border factoryLine = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 56)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(0, 5, 0, 0)
            };
            _factoryBannerText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(158, 170, 184)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            factoryLine.Child = _factoryBannerText;
            headerStack.Children.Add(factoryLine);

            _overallProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 4,
                Margin = new Thickness(0, 6, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                BorderThickness = new Thickness(0)
            };
            headerStack.Children.Add(_overallProgressBar);

            header.Child = headerStack;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // 2. Centralna 2-kolonska lista testova (Automatski levo | Periferni desno)
            ScrollViewer sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 10, 12, 10)
            };

            _cardsPanel = new StackPanel();
            Grid colsGrid = new Grid();
            colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            colsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border autoColBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };
            StackPanel autoColStack = new StackPanel();
            autoColStack.Children.Add(CreateAutoSectionHeader());
            _autoCardsPanel = new StackPanel();
            autoColStack.Children.Add(_autoCardsPanel);
            autoColBorder.Child = autoColStack;
            Grid.SetColumn(autoColBorder, 0);
            colsGrid.Children.Add(autoColBorder);

            Border manualColBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };
            StackPanel manualColStack = new StackPanel();
            manualColStack.Children.Add(CreateManualSectionHeader());
            _driverGateBanner = CreateDriverGateBanner();
            manualColStack.Children.Add(_driverGateBanner);
            _manualCardsPanel = new StackPanel();
            manualColStack.Children.Add(_manualCardsPanel);
            manualColBorder.Child = manualColStack;
            Grid.SetColumn(manualColBorder, 2);
            colsGrid.Children.Add(manualColBorder);

            foreach (TestItemResult t in _testItems)
            {
                Border card = CreateTestCard(t);
                _cardMap[t.Id] = card;
                if (t.IsAutomated)
                    _autoCardsPanel.Children.Add(card);
                else
                    _manualCardsPanel.Children.Add(card);
            }

            _cardsPanel.Children.Add(colsGrid);
            sv.Content = _cardsPanel;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);

            // 3. Kompaktni Footer Bar
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 8, 14, 8)
            };
            Grid fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel leftFooterStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button btnRunAll = new Button
            {
                Content = HwTestLocalization.Pick("▶ Pokreni sve testove redom", "▶ 依次运行全部测试"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnRunAll.Click += (s, e) => RunSequentialTests();
            _btnRunAll = btnRunAll;
            leftFooterStack.Children.Add(btnRunAll);

            Button btnResetAll = new Button
            {
                Content = HwTestLocalization.Pick("↻ Ponovi sve testove", "↻ 重新运行所有测试"),
                ToolTip = HwTestLocalization.Pick("Obriši sačuvane rezultate i ponovi sve hardverske testove ispočetka", "清除已保存的结果并从头重新运行所有硬件测试"),
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 72)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 235)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 92, 108)),
                BorderThickness = new Thickness(1),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(10, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnResetAll.Click += (s, e) => ResetAllTestsWithConfirmation();
            _btnResetAllTests = btnResetAll;
            leftFooterStack.Children.Add(btnResetAll);

            Grid.SetColumn(leftFooterStack, 0);
            fg.Children.Add(leftFooterStack);

            Button btnFinish = new Button
            {
                Content = HwTestLocalization.Pick("✓ Završi i sačuvaj izveštaj", "✓ 完成并保存报告"),
                Background = new SolidColorBrush(Color.FromRgb(35, 134, 54)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 6, 16, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnFinish = btnFinish;
            btnFinish.Click += (s, e) => FinishAndSave();
            Grid.SetColumn(btnFinish, 2);
            fg.Children.Add(btnFinish);

            footer.Child = fg;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            _runningUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _runningUiTimer.Tick += (s, e) => UpdateRunningElapsedTimers();

            _driverGatePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _driverGatePollTimer.Tick += (s, e) => RefreshDriverGateUi();

            Content = root;
            if (_checkpointRestored && _autoSectionStatus != null)
            {
                _autoSectionStatus.Text = HwTestLocalization.PickFormat(
                    "♻️ Nastavak posle UEFI BIOS restarta — restaurirano {0}/{1} već završenih testova.",
                    "♻️ UEFI BIOS 重启后继续 — 已恢复 {0}/{1} 项已完成的测试。",
                    _checkpointRestoredCount, _checkpointTotalApplicable);
                _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
            }
            RefreshCardVisibility();
            UpdateSummaryBadge();
            RefreshDriverGateUi();

            HwTestLocalization.LanguageChanged += OnHwTestLanguageChanged;
            Loaded += (s, e) => StartPassiveAutoChecks();
            Closing += (s, e) =>
            {
                HwTestLocalization.LanguageChanged -= OnHwTestLanguageChanged;
                _phase5WorkflowActive = false;
                if (_runningUiTimer != null) _runningUiTimer.Stop();
                if (_driverGatePollTimer != null) _driverGatePollTimer.Stop();
                _autoStressCancel.Cancel();
                if (!_uefiBiosRebootRequested && HasAnyCompletedTestsForCheckpoint())
                {
                    try { SaveCheckpoint(""); } catch { }
                }
                _autoEngine.Dispose();
            };
        }

        private string BuildSubTitleText()
        {
            bool laptop = (_hwInfo != null && _hwInfo.IsLaptop);
            return HwTestLocalization.PickFormat(
                "Model: {0} | Serijski broj: {1} | Šasija: {2}",
                "型号：{0} | 序列号：{1} | 机型：{2}",
                _modelName, _serialNumber,
                laptop ? HwTestLocalization.Pick("Laptop", "笔记本电脑") : HwTestLocalization.Pick("Desktop", "台式机"));
        }

        private void OnHwTestLanguageChanged()
        {
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    RefreshLocalization();
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(RefreshLocalization));
                }
            }
            catch { }
        }

        public void RefreshLocalization()
        {
            Title = HwTestLocalization.Pick("WIS — Hardverska Verifikacija Uređaja", "WIS — 设备硬件检测");
            if (_headerTitleBlock != null)
                _headerTitleBlock.Text = HwTestLocalization.Pick("HARDVERSKA DIJAGNOSTIKA I TESTIRANJE", "硬件诊断与测试");
            if (_subTitleBlock != null)
                _subTitleBlock.Text = BuildSubTitleText();
            if (_autoSectionTitleBlock != null)
                _autoSectionTitleBlock.Text = HwTestLocalization.Pick("⚡ AUTOMATSKI TESTOVI", "⚡ 自动测试");
            if (_manualSectionTitleBlock != null)
                _manualSectionTitleBlock.Text = HwTestLocalization.Pick("🔧 PERIFERNI TESTOVI SERVISERA", "🔧 技术员外设测试");
            if (_manualSectionSubBlock != null)
                _manualSectionSubBlock.Text = HwTestLocalization.Pick("Interaktivni testovi fizičkih periferija i ulazno/izlaznih uređaja", "物理外设及输入/输出设备的交互式测试");
            if (_btnRunAll != null)
                _btnRunAll.Content = HwTestLocalization.Pick("▶ Pokreni sve testove redom", "▶ 依次运行全部测试");
            if (_btnResetAllTests != null)
            {
                _btnResetAllTests.Content = HwTestLocalization.Pick("↻ Ponovi sve testove", "↻ 重新运行所有测试");
                _btnResetAllTests.ToolTip = HwTestLocalization.Pick("Obriši sačuvane rezultate i ponovi sve hardverske testove ispočetka", "清除已保存的结果并从头重新运行所有硬件测试");
            }
            if (_btnFinish != null)
                _btnFinish.Content = HwTestLocalization.Pick("✓ Završi i sačuvaj izveštaj", "✓ 完成并保存报告");
            if (_btnDriverGateOpenDrivers != null)
            {
                _btnDriverGateOpenDrivers.Content = HwTestLocalization.Pick("🔧 Otvori tab Drajveri", "🔧 打开驱动选项卡");
                _btnDriverGateOpenDrivers.ToolTip = HwTestLocalization.Pick("Prebaci se na tab Drajveri radi pregleda i instalacije nedostajućih drajvera", "切换到驱动选项卡以查看并安装缺失的驱动程序");
            }
            if (_btnDriverGateRecheck != null)
            {
                _btnDriverGateRecheck.Content = HwTestLocalization.Pick("🔄 Proveri ponovo", "🔄 重新检查");
                _btnDriverGateRecheck.ToolTip = HwTestLocalization.Pick("Ponovo proveri stanje drajvera i Device Manager-a", "重新检查驱动程序与设备管理器状态");
            }
            if (_btnDriverGateEmergencyUnlock != null)
            {
                _btnDriverGateEmergencyUnlock.Content = HwTestLocalization.Pick("⚠️ Vanredno otključaj testove", "⚠️ 紧急解锁测试");
                _btnDriverGateEmergencyUnlock.ToolTip = HwTestLocalization.Pick("Vanredno otključaj interaktivne testove uz obavezan serviserski razlog (samo nakon završene instalacije drajvera)", "在驱动安装完成后填写技术员原因以紧急解锁交互式测试");
            }
            if (_btnRunAutoSuite != null)
            {
                _btnRunAutoSuite.Content = _autoStressRunning
                    ? HwTestLocalization.Pick("⏳ Stres u toku...", "⏳ 压力测试进行中...")
                    : HwTestLocalization.Pick("⚡ Stres paket", "⚡ 压力测试包");
                _btnRunAutoSuite.ToolTip = HwTestLocalization.Pick("Pokreni sekvencijalni automatski stres paket (Disk, RAM, CPU, GPU)", "依次运行自动压力测试包（磁盘、内存、CPU、GPU）");
            }
            if (_autoSectionStatus != null && !string.IsNullOrEmpty(_autoSectionStatus.Text))
            {
                _autoSectionStatus.Text = HwTestLocalization.Retranslate(_autoSectionStatus.Text);
                _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
            }
            foreach (KeyValuePair<string, Button> kv in _detailsBtnMap)
            {
                if (kv.Value != null)
                {
                    kv.Value.Content = HwTestLocalization.Pick("Detalji", "详情");
                    kv.Value.ToolTip = HwTestLocalization.Pick("Otvori detaljan izveštaj testa", "打开该测试的详细报告");
                }
            }
            foreach (KeyValuePair<string, Button> kv in _runBtnMap)
            {
                if (kv.Value != null)
                    kv.Value.ToolTip = HwTestLocalization.Pick("Pokreni ovaj test", "运行此测试");
            }
            foreach (KeyValuePair<string, Button> kv in _biosBtnMap)
            {
                if (kv.Value != null)
                    kv.Value.ToolTip = HwTestLocalization.Pick("Restartuj računar u UEFI BIOS podešavanja da biste uključili uređaj (WIS nastavlja gde je stao)", "重启电脑进入 UEFI BIOS 设置以启用该设备（WIS 将从中断处继续）");
            }
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                t.Title = HwTestLocalization.Retranslate(t.Title);
                t.Description = HwTestLocalization.Retranslate(t.Description);
                t.MetricSummary = HwTestLocalization.Retranslate(t.MetricSummary);
                t.Reason = HwTestLocalization.Retranslate(t.Reason);
                TextBlock tbTitle;
                if (_titleTextMap.TryGetValue(t.Id, out tbTitle) && tbTitle != null)
                    tbTitle.Text = t.Title;
                Border card;
                if (_cardMap.TryGetValue(t.Id, out card) && card != null)
                    UpdateCardVisual(card, t);
            }
            RefreshCardVisibility();
            UpdateSummaryBadge();
            RefreshDriverGateUi();
        }

        private Window GetDialogOwner()
        {
            if (_hostWindow != null) return _hostWindow;
            return _isEmbedded ? null : this;
        }

        public UIElement CreateEmbeddedView(Window hostWindow, Action<HwTestRunResult> onReportSaved)
        {
            _isEmbedded = true;
            _hostWindow = hostWindow;
            _onReportSavedCallback = onReportSaved;

            if (_rootGrid != null && object.ReferenceEquals(Content, _rootGrid))
            {
                Content = null;
            }

            RefreshDriverGateUi();
            StartPassiveAutoChecks();
            return _rootGrid;
        }

        public void ShutdownEmbedded()
        {
            HwTestLocalization.LanguageChanged -= OnHwTestLanguageChanged;
            _phase5WorkflowActive = false;
            if (_runningUiTimer != null)
            {
                try { _runningUiTimer.Stop(); } catch { }
            }
            if (_driverGatePollTimer != null)
            {
                try { _driverGatePollTimer.Stop(); } catch { }
            }
            _autoStressCancel.Cancel();
            if (!_uefiBiosRebootRequested && HasAnyCompletedTestsForCheckpoint())
            {
                try { SaveCheckpoint(""); } catch { }
            }
            try { _autoEngine.Dispose(); } catch { }
        }

        public void CancelPhase5Workflow()
        {
            _phase5WorkflowActive = false;
            _autoStressCancel.Cancel();
        }

        private static bool IsPlaceholderIdentity(string val)
        {
            if (string.IsNullOrEmpty(val)) return true;
            string t = val.Trim();
            if (t.Length == 0) return true;
            if (t.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Standardni PC", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Nepoznat model", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Nepoznat", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Učitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Ucitavanje", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Detecting", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("加载中", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("读取中", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("正在加载", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("未知", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Default string", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void UpdateIdentity(string modelName, string serialNumber)
        {
            bool changed = false;
            bool serialBecameValid = false;
            if (!IsPlaceholderIdentity(modelName))
            {
                _modelName = modelName.Trim();
                if (_hwInfo != null) _hwInfo.ModelName = _modelName;
                changed = true;
            }
            if (!IsPlaceholderIdentity(serialNumber))
            {
                bool wasPlaceholder = IsPlaceholderIdentity(_serialNumber);
                _serialNumber = serialNumber.Trim();
                if (_hwInfo != null) _hwInfo.SerialNumber = _serialNumber;
                changed = true;
                if (wasPlaceholder) serialBecameValid = true;
            }
            if (serialBecameValid && !_checkpointRestored)
            {
                try { TryRestorePersistedTestState(); } catch { }
            }
            if (changed && _subTitleBlock != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _subTitleBlock.Text = BuildSubTitleText();
                    RefreshCardVisibility();
                    UpdateSummaryBadge();
                }));
            }
        }

        private void InitTestList()
        {
            // === AUTOMATSKI TESTOVI HARDVERA ===
            TestItemResult autoDisk = new TestItemResult("auto_disk", HwTestLocalization.Pick("Disk / SSD / NVMe", "磁盘 / SSD / NVMe"), HwTestLocalization.Pick("SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark", "SMART 健康度、剩余寿命（磨损 %）、温度及快速读写基准测试"));
            autoDisk.IsAutomated = true;
            autoDisk.MetricSummary = HwTestLocalization.Pick("⏳ Očitavam SMART podatke diska...", "⏳ 正在读取磁盘 SMART 数据...");
            _testItems.Add(autoDisk);

            TestItemResult autoCpu = new TestItemResult("auto_cpu", HwTestLocalization.Pick("Procesor (CPU)", "处理器 (CPU)"), HwTestLocalization.Pick("Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature", "多核压力负载、频率、温度降频及温度检测"));
            autoCpu.IsAutomated = true;
            autoCpu.MetricSummary = HwTestLocalization.Pick("⏳ Očitavam informacije procesora...", "⏳ 正在读取处理器信息...");
            _testItems.Add(autoCpu);

            TestItemResult autoRam = new TestItemResult("auto_ram", HwTestLocalization.Pick("Radna memorija (RAM)", "内存 (RAM)"), HwTestLocalization.Pick("Detekcija modula/brzine i write/read verifikacija integriteta memorije", "内存条/频率检测及读写完整性校验"));
            autoRam.IsAutomated = true;
            autoRam.MetricSummary = HwTestLocalization.Pick("⏳ Očitavam memorijske slotove...", "⏳ 正在读取内存插槽...");
            _testItems.Add(autoRam);

            TestItemResult autoGpu = new TestItemResult("auto_gpu", HwTestLocalization.Pick("Grafika (GPU)", "显卡 (GPU)"), HwTestLocalization.Pick("Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)", "检查驱动、显存及 3D 硬件渲染 (WPF Direct3D)"));
            autoGpu.IsAutomated = true;
            autoGpu.MetricSummary = HwTestLocalization.Pick("⏳ Očitavam grafički adapter...", "⏳ 正在读取显卡适配器...");
            _testItems.Add(autoGpu);

            TestItemResult autoBat = new TestItemResult("auto_battery", HwTestLocalization.Pick("Baterija i napajanje", "电池与电源"), HwTestLocalization.Pick("Provera zdravlja baterije, kapaciteta (Design vs Full Charge) i AC punjača", "检查电池健康度、容量（设计容量与满充容量）及电源适配器"));
            autoBat.IsAutomated = true;
            autoBat.MetricSummary = HwTestLocalization.Pick("⏳ Očitavam status baterije...", "⏳ 正在读取电池状态...");
            _testItems.Add(autoBat);

            TestItemResult autoNet = new TestItemResult("auto_network", HwTestLocalization.Pick("Mreža (Wi-Fi / LAN & Hub)", "网络 (Wi-Fi / LAN 与 Hub)"), HwTestLocalization.Pick("Provera mrežnih adaptera, link brzine, gateway-a i veze sa ServisHub serverom", "检查网络适配器、链路速度、网关及与 ServisHub 服务器的连接"));
            autoNet.IsAutomated = true;
            autoNet.MetricSummary = HwTestLocalization.Pick("⏳ Proveravam mrežne adaptere i ServisHub...", "⏳ 正在检查网络适配器和 ServisHub...");
            _testItems.Add(autoNet);

            TestItemResult autoSecurity = new TestItemResult("auto_security", HwTestLocalization.Pick("BIOS, TPM 2.0 i PnP Drajveri", "BIOS、TPM 2.0 与 PnP 驱动"), HwTestLocalization.Pick("Provera BIOS lozinke, Computrace statusa, TPM 2.0 čipa, OA3 licence i žutih uzvičnika u Device Manageru", "检查 BIOS 密码、Computrace 状态、TPM 2.0 芯片、OA3 许可证及设备管理器中的黄色感叹号"));
            autoSecurity.IsAutomated = true;
            autoSecurity.MetricSummary = HwTestLocalization.Pick("⏳ Proveravam BIOS, TPM 2.0 i PnP drajvere...", "⏳ 正在检查 BIOS、TPM 2.0 与 PnP 驱动...");
            _testItems.Add(autoSecurity);

            // === PERIFERNI TESTOVI SERVISERA ===
            // Na Desktop racunaru rucni testovi periferija (ekran, tastatura, zvuk, USB...) nisu obavezni
            // i automatski dobijaju status NotApplicable (serviser ih i dalje moze rucno prikazati i pokrenuti).
            bool isLaptop = _hwInfo.IsLaptop;

            // 1. Ekran (integrisan na laptopu; na Desktopu je eksterni monitor)
            TestItemResult disp = new TestItemResult("display", HwTestLocalization.Pick("Ekran (mrtvi pikseli)", "屏幕检测（坏点/漏光）"), HwTestLocalization.Pick("Provera matrice ekrana (6 boja + gradijent), WMI osvetljenja i HDMI izlaza", "检查屏幕面板（6 种颜色 + 渐变）、WMI 亮度及 HDMI 输出"));
            if (!isLaptop)
            {
                disp.Status = TestStatus.NotApplicable;
                disp.Reason = HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterni monitor)", "不适用 — 台式机（外接显示器）");
            }
            _testItems.Add(disp);

            // 2. Tastatura (integrisana na laptopu; na Desktopu je eksterna tastatura)
            TestItemResult kbd = new TestItemResult("keyboard", HwTestLocalization.Pick("Tastatura", "键盘测试"), HwTestLocalization.Pick("Ispitivanje odziva svih fizičkih tastera i provera zaglavljenih kontakata", "检测所有物理按键响应并检查卡键/接触不良"));
            if (!isLaptop)
            {
                kbd.Status = TestStatus.NotApplicable;
                kbd.Reason = HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterna tastatura)", "不适用 — 台式机（外接键盘）");
            }
            _testItems.Add(kbd);

            // 3. Touchpad (razdvojeno od TrackPointa po HWT-05; bez samostalnog BIOS flaga bez HW/BOM potvrde)
            bool tpExpected = isLaptop && (_hwInfo.HasTouchpad || _hwInfo.FactoryTouchpad == true);
            bool tpNeedsBiosAction = tpExpected && (_hwInfo.TouchpadDisabledInBios || (!_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true));
            string tpDesc = HwTestLocalization.Pick("Test praćenja pokazivača u 4 ugla, levog/desnog klika i skrola sa dva prsta", "测试光标在 4 个角的追踪、左/右键点击及双指滚动");
            if (tpExpected && _hwInfo.TouchpadDisabledInBios && !_hwInfo.HasTouchpad)
                tpDesc = HwTestLocalization.PickFormat("⚠️ UPOZORENJE: Touchpad je ONEMOGUĆEN u BIOS-u ({0})!", "⚠️ 警告：触摸板已在 BIOS 中禁用（{0}）！", _hwInfo.TouchpadBiosStatus);
            else if (tpExpected && !_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true)
                tpDesc = HwTestLocalization.Pick("⚠️ FABRIČKI PRISUTAN TOUCHPAD — Nije detektovan u Windowsu (proverite UEFI BIOS ili drajver)!", "⚠️ 出厂配备触摸板 — Windows 中未检测到（请检查 UEFI BIOS 或驱动）！");

            TestItemResult tp = new TestItemResult("touchpad", HwTestLocalization.Pick("Touchpad", "触摸板"), tpDesc);
            if (!tpExpected)
            {
                tp.Status = TestStatus.NotApplicable;
                tp.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterne periferije)", "不适用 — 台式机（外接外设）")
                    : HwTestLocalization.Pick("Uređaj fabrički nema touchpad", "设备出厂不带触摸板");
                if (isLaptop && _hwInfo.TouchpadDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "TouchpadDisabledInBios=true ({0}), ali HW/BOM potvrda ne postoji — test ostaje N/A.",
                        _hwInfo.TouchpadBiosStatus));
                }
            }
            else if (tpNeedsBiosAction)
            {
                tp.HasWarning = true;
                tp.Details["bios_action_recommended"] = true;
                if (_hwInfo.TouchpadDisabledInBios)
                {
                    tp.Reason = HwTestLocalization.PickFormat("Touchpad onemogućen u BIOS-u: {0}", "触摸板已在 BIOS 中禁用：{0}", _hwInfo.TouchpadBiosStatus);
                    tp.Details["bios_disabled"] = true;
                }
                else
                {
                    tp.Reason = HwTestLocalization.Pick("Touchpad potvrđen u fabričkom BOM-u, ali nije aktivan u Windowsu", "出厂 BOM 中确认有触摸板，但 Windows 中未激活");
                    tp.Details["factory_missing_in_os"] = true;
                }
            }
            _testItems.Add(tp);

            // 4. TrackPoint (zasebna stavka po HWT-05)
            bool trackPointExpected = isLaptop && (_hwInfo.HasTrackPoint || _hwInfo.FactoryTrackPoint == true);
            bool trackPointNeedsBiosAction = trackPointExpected && (_hwInfo.TrackPointDisabledInBios || (!_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true));
            string trackPointDesc = HwTestLocalization.Pick("Provera pomeranja kursora crvenim TrackPoint džojstikom (4 mete) i gornjih tastera (levi, srednji/Scroll, desni)", "使用红色 TrackPoint 指点杆移动光标（4 个目标）并检测上方按键（左键、中键/滚动、右键）");
            if (trackPointExpected && _hwInfo.TrackPointDisabledInBios && !_hwInfo.HasTrackPoint)
                trackPointDesc = HwTestLocalization.PickFormat("⚠️ UPOZORENJE: TrackPoint je ONEMOGUĆEN u BIOS-u ({0})!", "⚠️ 警告：TrackPoint 指点杆已在 BIOS 中禁用（{0}）！", _hwInfo.TrackPointBiosStatus);
            else if (trackPointExpected && !_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true)
                trackPointDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTAN TRACKPOINT ({0}) — Nije aktivan u Windowsu!", "⚠️ 出厂配备 TrackPoint 指点杆（{0}）— Windows 中未激活！", !string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence) ? _hwInfo.FactoryTrackPointEvidence : "BOM");

            TestItemResult trackPoint = new TestItemResult("trackpoint", "TrackPoint", trackPointDesc);
            trackPoint.Title = HwTestLocalization.Pick("TrackPoint", "TrackPoint 指点杆");
            if (!trackPointExpected)
            {
                trackPoint.Status = TestStatus.NotApplicable;
                trackPoint.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterne periferije)", "不适用 — 台式机（外接外设）")
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence)
                        ? HwTestLocalization.PickFormat("Uređaj nema TrackPoint ({0})", "设备没有 TrackPoint 指点杆（{0}）", _hwInfo.FactoryTrackPointEvidence)
                        : HwTestLocalization.Pick("Uređaj nema TrackPoint džojstik", "设备没有 TrackPoint 指点杆"));
                if (isLaptop && _hwInfo.TrackPointDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "TrackPointDisabledInBios=true ({0}), ali HW/BOM potvrda ne postoji — test ostaje N/A.",
                        _hwInfo.TrackPointBiosStatus));
                }
            }
            else if (trackPointNeedsBiosAction)
            {
                trackPoint.HasWarning = true;
                trackPoint.Details["bios_action_recommended"] = true;
                if (_hwInfo.TrackPointDisabledInBios)
                {
                    trackPoint.Reason = HwTestLocalization.PickFormat("TrackPoint onemogućen u BIOS-u: {0}", "TrackPoint 指点杆已在 BIOS 中禁用：{0}", _hwInfo.TrackPointBiosStatus);
                    trackPoint.Details["bios_disabled"] = true;
                }
                else
                {
                    trackPoint.Reason = HwTestLocalization.Pick("TrackPoint potvrđen u fabričkom BOM-u, ali nije aktivan u Windowsu", "出厂 BOM 中确认有 TrackPoint 指点杆，但 Windows 中未激活");
                    trackPoint.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence))
                        trackPoint.Details["factory_evidence"] = _hwInfo.FactoryTrackPointEvidence;
                }
            }
            _testItems.Add(trackPoint);

            // 5. Touchscreen (+ Stylus olovka)
            bool tsExpected = isLaptop && (_hwInfo.HasTouchscreen || _hwInfo.TouchscreenPnpPresent || _hwInfo.FactoryTouchscreen == true);
            bool tsNeedsBiosAction = tsExpected && (_hwInfo.TouchscreenDisabledInBios || (!_hwInfo.HasTouchscreen && _hwInfo.FactoryTouchscreen == true));
            string tsTitle = _hwInfo.HasStylusPen ? HwTestLocalization.Pick("Touchscreen + Olovka (Stylus)", "触摸屏 + 手写笔 (Stylus)") : HwTestLocalization.Pick("Ekran na dodir (Touchscreen)", "触摸屏 (Touchscreen)");
            string tsDesc = _hwInfo.HasStylusPen
                ? HwTestLocalization.Pick("Prolazak kroz lavirint prstom, multitouch detekcija (+ provera Stylus olovke i pritiska)", "用手指穿过迷宫、多点触控检测（+ 手写笔及压感检查）")
                : HwTestLocalization.Pick("Prolazak kroz lavirint prstom i multitouch detekcija", "用手指穿过迷宫及多点触控检测");
            if (tsExpected && _hwInfo.TouchscreenDisabledInBios && !_hwInfo.HasTouchscreen)
                tsDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTAN — Touchscreen je ONEMOGUĆEN u BIOS-u ({0})!", "⚠️ 出厂配备 — 触摸屏已在 BIOS 中禁用（{0}）！", _hwInfo.TouchscreenBiosStatus);
            else if (tsExpected && !_hwInfo.HasTouchscreen && (_hwInfo.FactoryTouchscreen == true || _hwInfo.TouchscreenPnpPresent))
                tsDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTAN ({0}), ali nije aktivan u Windowsu!", "⚠️ 出厂配备（{0}），但 Windows 中未激活！", !string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence) ? _hwInfo.FactoryTouchscreenEvidence : "PnP");

            TestItemResult ts = new TestItemResult("touchscreen", tsTitle, tsDesc);
            if (!tsExpected)
            {
                ts.Status = TestStatus.NotApplicable;
                ts.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterni monitor)", "不适用 — 台式机（外接显示器）")
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence)
                        ? HwTestLocalization.PickFormat("Ekran nije osetljiv na dodir ({0})", "屏幕不支持触控（{0}）", _hwInfo.FactoryTouchscreenEvidence)
                        : HwTestLocalization.Pick("Displej fabrički ne podržava unos dodirom", "屏幕出厂不支持触控输入"));
                if (isLaptop && _hwInfo.TouchscreenDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "TouchscreenDisabledInBios=true ({0}), ali HW/PnP/BOM potvrda ne postoji — test ostaje N/A.",
                        _hwInfo.TouchscreenBiosStatus));
                }
            }
            else if (!_hwInfo.HasTouchscreen)
            {
                ts.Details["factory_missing_in_os"] = true;
                if (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence))
                    ts.Details["factory_evidence"] = _hwInfo.FactoryTouchscreenEvidence;
                if (tsNeedsBiosAction)
                {
                    ts.HasWarning = true;
                    ts.Details["bios_action_recommended"] = true;
                    if (_hwInfo.TouchscreenDisabledInBios)
                    {
                        ts.Details["bios_disabled"] = true;
                        ts.Reason = HwTestLocalization.PickFormat("Touchscreen onemogućen u BIOS-u: {0}", "触摸屏已在 BIOS 中禁用：{0}", _hwInfo.TouchscreenBiosStatus);
                    }
                    else
                    {
                        ts.Reason = HwTestLocalization.Pick("Touchscreen potvrđen u OEM BOM-u, ali nije aktivan u Windowsu", "OEM BOM 中确认有触摸屏，但 Windows 中未激活");
                    }
                }
            }
            else if (tsNeedsBiosAction)
            {
                ts.HasWarning = true;
                ts.Details["bios_action_recommended"] = true;
                ts.Details["bios_disabled"] = true;
                ts.Reason = HwTestLocalization.PickFormat("Touchscreen ima BIOS upozorenje: {0}", "触摸屏有 BIOS 警告：{0}", _hwInfo.TouchscreenBiosStatus);
            }
            _testItems.Add(ts);

            // 6. Zvuk (Zvučnici)
            bool audioExpected = isLaptop && (_hwInfo.HasSpeakers || _hwInfo.FactorySpeakers == true);
            TestItemResult audio = new TestItemResult("audio", HwTestLocalization.Pick("Zvuk (Levi / Desni kanal)", "音频（左 / 右声道）"), HwTestLocalization.Pick("Test stereo zvučnika izgovorom nasumičnih cifara na levom i desnom kanalu", "通过左右声道随机播报数字测试立体声扬声器"));
            if (!audioExpected)
            {
                audio.Status = TestStatus.NotApplicable;
                audio.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar (eksterni zvučnici)", "不适用 — 台式机（外接扬声器）")
                    : HwTestLocalization.Pick("Uređaj nema detektovane zvučnike", "未检测到设备扬声器");
            }
            _testItems.Add(audio);

            // 7. Mikrofon (bez veštačkog fallbacka preko HasWebcam i bez samostalnog BIOS flaga)
            bool micExpected = isLaptop && (_hwInfo.HasMicrophone || _hwInfo.FactoryMicrophone == true);
            bool micNeedsBiosAction = micExpected && (_hwInfo.MicrophoneDisabledInBios || (!_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true));
            string micDesc = HwTestLocalization.Pick("VU metar nivoa glasa i automatsko preslušavanje snimka", "VU 电平表显示音量并自动回放录音");
            if (micExpected && _hwInfo.MicrophoneDisabledInBios && !_hwInfo.HasMicrophone)
                micDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTAN — Mikrofon je ONEMOGUĆEN u BIOS-u ({0})!", "⚠️ 出厂配备 — 麦克风已在 BIOS 中禁用（{0}）！", _hwInfo.MicrophoneBiosStatus);
            else if (micExpected && !_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true)
                micDesc = HwTestLocalization.Pick("⚠️ FABRIČKI OČEKIVAN MIKROFON — Nije detektovan aktivan capture uređaj u Windowsu!", "⚠️ 出厂应配备麦克风 — Windows 中未检测到可用的录音设备！");

            TestItemResult mic = new TestItemResult("mic", HwTestLocalization.Pick("Mikrofon", "麦克风"), micDesc);
            if (!micExpected)
            {
                mic.Status = TestStatus.NotApplicable;
                mic.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar", "不适用 — 台式机")
                    : HwTestLocalization.Pick("Uređaj nema ugrađeni mikrofon", "设备没有内置麦克风");
                if (isLaptop && _hwInfo.MicrophoneDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "MicrophoneDisabledInBios=true ({0}), ali HW/BOM potvrda ne postoji — test ostaje N/A.",
                        _hwInfo.MicrophoneBiosStatus));
                }
            }
            else if (micNeedsBiosAction)
            {
                mic.HasWarning = true;
                mic.Details["bios_action_recommended"] = true;
                if (_hwInfo.MicrophoneDisabledInBios)
                {
                    mic.Details["bios_disabled"] = true;
                    mic.Reason = HwTestLocalization.PickFormat("Mikrofon onemogućen u BIOS-u: {0}", "麦克风已在 BIOS 中禁用：{0}", _hwInfo.MicrophoneBiosStatus);
                }
                if (!_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true)
                {
                    mic.Details["factory_missing_in_os"] = true;
                    if (string.IsNullOrEmpty(mic.Reason))
                        mic.Reason = HwTestLocalization.Pick("Mikrofon potvrđen u OEM BOM-u, ali nije detektovan u Windowsu", "OEM BOM 中确认有麦克风，但 Windows 中未检测到");
                }
            }
            _testItems.Add(mic);

            // 8. Web kamera (RGB QR kod — razdvojeno od IR kamere po HWT-06)
            bool camExpected = isLaptop && (_hwInfo.HasWebcam || _hwInfo.WebcamPnpPresent || _hwInfo.FactoryWebcam == true);
            bool camNeedsBiosAction = camExpected && (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasWebcam && _hwInfo.FactoryWebcam == true));
            string camDesc = HwTestLocalization.Pick("Očitavanje otštampanog QR koda RGB web kamerom (WIS-CAMTEST-v1)", "使用 RGB 摄像头扫描打印的二维码 (WIS-CAMTEST-v1)");
            if (camExpected && _hwInfo.CameraDisabledInBios && !_hwInfo.HasWebcam)
                camDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTNA — Web kamera je ONEMOGUĆENA u BIOS-u ({0})!", "⚠️ 出厂配备 — 摄像头已在 BIOS 中禁用（{0}）！", _hwInfo.CameraBiosStatus);
            else if (camExpected && !_hwInfo.HasWebcam && (_hwInfo.FactoryWebcam == true || _hwInfo.WebcamPnpPresent))
                camDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTNA KAMERA ({0}), ali nije aktivna u Windowsu!", "⚠️ 出厂配备摄像头（{0}），但 Windows 中未激活！", !string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence) ? _hwInfo.FactoryWebcamEvidence : "PnP");

            TestItemResult cam = new TestItemResult("camera", HwTestLocalization.Pick("Web kamera (QR kod)", "网络摄像头（二维码）"), camDesc);
            if (!camExpected)
            {
                cam.Status = TestStatus.NotApplicable;
                cam.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar", "不适用 — 台式机")
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence)
                        ? HwTestLocalization.PickFormat("Uređaj fabrički nema web kameru ({0})", "设备出厂不带摄像头（{0}）", _hwInfo.FactoryWebcamEvidence)
                        : HwTestLocalization.Pick("Web kamera nije pronađena ni u OS-u ni u fabričkoj konfiguraciji", "操作系统和出厂配置中均未找到摄像头"));
                if (isLaptop && _hwInfo.CameraDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "CameraDisabledInBios=true ({0}), ali RGB kamera nije potvrđena u OS/BOM — test web kamere ostaje N/A.",
                        _hwInfo.CameraBiosStatus));
                }
            }
            else if (camNeedsBiosAction)
            {
                cam.HasWarning = true;
                cam.Details["bios_action_recommended"] = true;
                if (_hwInfo.CameraDisabledInBios)
                {
                    cam.Details["bios_disabled"] = true;
                    cam.Reason = HwTestLocalization.PickFormat("Web kamera onemogućena u BIOS-u: {0}", "摄像头已在 BIOS 中禁用：{0}", _hwInfo.CameraBiosStatus);
                }
                if (!_hwInfo.HasWebcam && _hwInfo.FactoryWebcam == true)
                {
                    cam.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence))
                        cam.Details["factory_evidence"] = _hwInfo.FactoryWebcamEvidence;
                    if (string.IsNullOrEmpty(cam.Reason))
                        cam.Reason = HwTestLocalization.Pick("Web kamera potvrđena u OEM BOM-u, ali nije aktivna u Windowsu", "OEM BOM 中确认有摄像头，但 Windows 中未激活");
                }
            }
            _testItems.Add(cam);

            // 9. IR kamera (Windows Hello — zasebna stavka po HWT-06)
            bool irCamExpected = isLaptop && (_hwInfo.HasIrCamera || _hwInfo.FactoryIrCamera == true);
            bool irCamNeedsBiosAction = irCamExpected && (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true));
            string irCamDesc = HwTestLocalization.Pick("Provera IR senzora kamere za Windows Hello (prepoznavanje lica)", "检查 Windows Hello 红外摄像头传感器（人脸识别）");
            if (irCamExpected && _hwInfo.CameraDisabledInBios && !_hwInfo.HasIrCamera)
                irCamDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTNA — IR kamera je ONEMOGUĆENA u BIOS-u ({0})!", "⚠️ 出厂配备 — 红外摄像头已在 BIOS 中禁用（{0}）！", _hwInfo.CameraBiosStatus);
            else if (irCamExpected && !_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true)
                irCamDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTNA IR KAMERA ({0}), ali nije aktivna u Windowsu!", "⚠️ 出厂配备红外摄像头（{0}），但 Windows 中未激活！", !string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence) ? _hwInfo.FactoryIrCameraEvidence : "BOM");

            TestItemResult irCam = new TestItemResult("ir_camera", "IR kamera (Windows Hello)", irCamDesc);
            irCam.Title = HwTestLocalization.Pick("IR kamera (Windows Hello)", "红外摄像头 (Windows Hello)");
            if (!irCamExpected)
            {
                irCam.Status = TestStatus.NotApplicable;
                irCam.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar", "不适用 — 台式机")
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence)
                        ? HwTestLocalization.PickFormat("Uređaj fabrički nema IR kameru ({0})", "设备出厂不带红外摄像头（{0}）", _hwInfo.FactoryIrCameraEvidence)
                        : HwTestLocalization.Pick("IR kamera nije pronađena ni u OS-u ni u fabričkoj konfiguraciji", "操作系统和出厂配置中均未找到红外摄像头"));
            }
            else if (irCamNeedsBiosAction)
            {
                irCam.HasWarning = true;
                irCam.Details["bios_action_recommended"] = true;
                if (_hwInfo.CameraDisabledInBios)
                {
                    irCam.Details["bios_disabled"] = true;
                    irCam.Reason = HwTestLocalization.PickFormat("IR kamera onemogućena u BIOS-u: {0}", "红外摄像头已在 BIOS 中禁用：{0}", _hwInfo.CameraBiosStatus);
                }
                if (!_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true)
                {
                    irCam.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence))
                        irCam.Details["factory_evidence"] = _hwInfo.FactoryIrCameraEvidence;
                    if (string.IsNullOrEmpty(irCam.Reason))
                        irCam.Reason = HwTestLocalization.Pick("IR kamera potvrđena u OEM BOM-u, ali nije aktivna u Windowsu", "OEM BOM 中确认有红外摄像头，但 Windows 中未激活");
                }
            }
            _testItems.Add(irCam);

            // 10. USB portovi (integrisani na laptopu; na Desktopu nisu obavezni u Fazi 5)
            TestItemResult usb = new TestItemResult("usb", HwTestLocalization.Pick("USB portovi", "USB 端口"), HwTestLocalization.Pick("Verifikacija svih spoljnih USB portova ubacivanjem fleš diska", "插入 U 盘逐一验证所有外部 USB 端口"));
            if (!isLaptop)
            {
                usb.Status = TestStatus.NotApplicable;
                usb.Reason = HwTestLocalization.Pick("Nije primenljivo — Desktop računar", "不适用 — 台式机");
            }
            _testItems.Add(usb);

            // 11. Čitač otiska prsta (OS + OEM BOM po HWT-04; bez !FactoryBomLoaded i samostalnog FingerprintDisabledInBios)
            bool fpExpected = isLaptop && (_hwInfo.HasFingerprint || _hwInfo.FactoryFingerprint == true);
            bool fpNeedsBiosAction = fpExpected && (_hwInfo.FingerprintDisabledInBios || (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true));
            string fpDesc = HwTestLocalization.Pick("Provera odziva biometrijskog senzora otiska prsta preko Windows Biometric API-ja", "通过 Windows Biometric API 检查指纹传感器响应");
            if (fpExpected && _hwInfo.FingerprintDisabledInBios)
                fpDesc = HwTestLocalization.PickFormat("⚠️ UPOZORENJE: Uređaj je ONEMOGUĆEN u podešavanjima BIOS-a ({0})!", "⚠️ 警告：设备已在 BIOS 设置中禁用（{0}）！", _hwInfo.FingerprintBiosStatus);
            else if (fpExpected && !_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
                fpDesc = HwTestLocalization.PickFormat("⚠️ FABRIČKI PRISUTAN U OEM BOM-U ({0}), ali nije detektovan u Windowsu!", "⚠️ OEM BOM 中出厂配备（{0}），但 Windows 中未检测到！", _hwInfo.FactoryFingerprintEvidence);

            TestItemResult fp = new TestItemResult("fingerprint", HwTestLocalization.Pick("Čitač otiska prsta", "指纹识别器"), fpDesc);
            if (!fpExpected)
            {
                fp.Status = TestStatus.NotApplicable;
                fp.Reason = !isLaptop
                    ? HwTestLocalization.Pick("Nije primenljivo — Desktop računar", "不适用 — 台式机")
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryFingerprintEvidence)
                        ? HwTestLocalization.PickFormat("Čitač otiska prsta nije fabrički ugrađen ({0})", "指纹识别器非出厂配置（{0}）", _hwInfo.FactoryFingerprintEvidence)
                        : HwTestLocalization.Pick("Čitač otiska prsta nije pronađen ni u OS-u ni u fabričkoj specifikaciji", "操作系统和出厂规格中均未找到指纹识别器"));
                if (isLaptop && _hwInfo.FingerprintDisabledInBios)
                {
                    HwTestLogger.Info("BIOS_AUDIT", string.Format(
                        "FingerprintDisabledInBios=true ({0}), ali HW/BOM potvrda ne postoji — test otiska prsta ostaje N/A.",
                        _hwInfo.FingerprintBiosStatus));
                }
            }
            else if (fpNeedsBiosAction)
            {
                fp.HasWarning = true;
                fp.Details["bios_action_recommended"] = true;
                if (_hwInfo.FingerprintDisabledInBios)
                {
                    fp.Reason = HwTestLocalization.PickFormat("Čitač otiska prsta je onemogućen u BIOS-u: {0}", "指纹识别器已在 BIOS 中禁用：{0}", _hwInfo.FingerprintBiosStatus);
                    fp.Details["bios_disabled"] = true;
                    fp.Details["bios_setting"] = _hwInfo.FingerprintBiosStatus;
                }
                if (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
                {
                    fp.Details["factory_missing_in_os"] = true;
                    fp.Details["factory_evidence"] = _hwInfo.FactoryFingerprintEvidence;
                    if (string.IsNullOrEmpty(fp.Reason))
                        fp.Reason = HwTestLocalization.Pick("Čitač otiska prsta potvrđen u OEM BOM-u, ali nije detektovan u Windowsu", "OEM BOM 中确认有指纹识别器，但 Windows 中未检测到");
                }
            }
            _testItems.Add(fp);

            // Logovanje finalne odluke vidljivosti testova
            List<string> shown = new List<string>();
            List<string> hidden = new List<string>();
            foreach (TestItemResult item in _testItems)
            {
                if (item.Status == TestStatus.NotApplicable)
                    hidden.Add(item.Id + " (" + item.Reason + ")");
                else
                    shown.Add(item.Id);
            }
            HwTestLogger.Info("TEST_VISIBILITY", string.Format("Prikazani=[{0}] | Sakriveni=[{1}]",
                string.Join(", ", shown.ToArray()),
                string.Join("; ", hidden.ToArray())));
        }

        private void RefreshCardVisibility()
        {
            int hiddenCount = 0;
            int shownCount = 0;
            List<string> hiddenTitles = new List<string>();

            foreach (TestItemResult t in _testItems)
            {
                bool isNa = (t.Status == TestStatus.NotApplicable);
                if (isNa)
                {
                    hiddenCount++;
                    hiddenTitles.Add(t.Title);
                }
                else
                {
                    shownCount++;
                }

                Border card;
                if (_cardMap.TryGetValue(t.Id, out card))
                {
                    card.Visibility = (!isNa || _showNotApplicable) ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (_factoryBannerText != null)
            {
                string srcLabel = !string.IsNullOrEmpty(_hwInfo.FactorySummary) ? HwTestLocalization.Retranslate(_hwInfo.FactorySummary) : HwTestLocalization.Pick("Lokalna detekcija", "本地检测");
                if (hiddenCount > 0)
                {
                    _factoryBannerText.Text = HwTestLocalization.PickFormat(
                        "🏭 Fabrička provera ({0}): Prikazano {1} testova | Sakriveno (nije prisutno na uređaju): {2}",
                        "🏭 出厂配置检查（{0}）：显示 {1} 项测试 | 已隐藏（设备上不存在）：{2}",
                        srcLabel, shownCount, string.Join(", ", hiddenTitles.ToArray()));
                }
                else
                {
                    _factoryBannerText.Text = HwTestLocalization.PickFormat(
                        "🏭 Fabrička provera ({0}): Svi testovi ({1}) su primenjivi za ovaj uređaj",
                        "🏭 出厂配置检查（{0}）：全部 {1} 项测试均适用于此设备",
                        srcLabel, shownCount);
                }
            }

            if (_btnToggleHidden != null)
            {
                if (hiddenCount == 0)
                {
                    _btnToggleHidden.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _btnToggleHidden.Visibility = Visibility.Visible;
                    _btnToggleHidden.Content = _showNotApplicable
                        ? HwTestLocalization.PickFormat("🙈 Sakrij odsutne ({0})", "🙈 隐藏不存在的项（{0}）", hiddenCount)
                        : HwTestLocalization.PickFormat("👁 Prikaži sakrivene ({0})", "👁 显示已隐藏项（{0}）", hiddenCount);
                }
            }
        }

        private Border CreateAutoSectionHeader()
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(27, 34, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _autoSectionTitleBlock = new TextBlock
            {
                Text = HwTestLocalization.Pick("⚡ AUTOMATSKI TESTOVI", "⚡ 自动测试"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            sp.Children.Add(_autoSectionTitleBlock);
            _autoSectionStatus = new TextBlock
            {
                Text = HwTestLocalization.Pick("Pasivno (Baterija, Mreža, BIOS/TPM/PnP, SMART) + stres (Disk, RAM, CPU, GPU)", "被动检测（电池、网络、BIOS/TPM/PnP、SMART）+ 压力测试（磁盘、内存、CPU、GPU）"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(165, 180, 198)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
            sp.Children.Add(_autoSectionStatus);
            g.Children.Add(sp);

            _btnRunAutoSuite = new Button
            {
                Content = HwTestLocalization.Pick("⚡ Stres paket", "⚡ 压力测试包"),
                ToolTip = HwTestLocalization.Pick("Pokreni sekvencijalni automatski stres paket (Disk, RAM, CPU, GPU)", "依次运行自动压力测试包（磁盘、内存、CPU、GPU）"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnRunAutoSuite.Click += (s, e) => ToggleAutoStressSuite();
            Grid.SetColumn(_btnRunAutoSuite, 1);
            g.Children.Add(_btnRunAutoSuite);

            b.Child = g;
            return b;
        }

        private Border CreateManualSectionHeader()
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(27, 30, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 72, 85)),
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _manualSectionTitleBlock = new TextBlock
            {
                Text = HwTestLocalization.Pick("🔧 PERIFERNI TESTOVI SERVISERA", "🔧 技术员外设测试"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 235))
            };
            sp.Children.Add(_manualSectionTitleBlock);
            _manualSectionSubBlock = new TextBlock
            {
                Text = HwTestLocalization.Pick("Interaktivni testovi fizičkih periferija i ulazno/izlaznih uređaja", "物理外设及输入/输出设备的交互式测试"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            sp.Children.Add(_manualSectionSubBlock);
            g.Children.Add(sp);

            _manualSectionCountText = new TextBlock
            {
                Text = HwTestLocalization.Pick("0/0 završeno", "0/0 已完成"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(158, 170, 184)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(_manualSectionCountText, 1);
            g.Children.Add(_manualSectionCountText);

            b.Child = g;
            return b;
        }

        private Border CreateDriverGateBanner()
        {
            Border banner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(54, 30, 24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(230, 126, 34)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };

            StackPanel sp = new StackPanel();

            _driverGateBannerTitle = new TextBlock
            {
                Text = HwTestLocalization.Pick("🔒 Interaktivni testovi su zaključani dok se svi drajveri ne instaliraju", "🔒 所有驱动程序安装完成前，交互式测试已锁定"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102)),
                TextWrapping = TextWrapping.Wrap
            };
            sp.Children.Add(_driverGateBannerTitle);

            _driverGateBannerDetail = new TextBlock
            {
                Text = HwTestLocalization.Pick("Sačekajte završetak Faze 2 i instalaciju svih drajvera pre pokretanja interaktivnih testova periferija.", "请等待阶段 2 及所有驱动程序安装完成后再启动外设交互式测试。"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(235, 215, 205)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 6)
            };
            sp.Children.Add(_driverGateBannerDetail);

            WrapPanel btnPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal
            };

            _btnDriverGateOpenDrivers = new Button
            {
                Content = HwTestLocalization.Pick("🔧 Otvori tab Drajveri", "🔧 打开驱动选项卡"),
                ToolTip = HwTestLocalization.Pick("Prebaci se na tab Drajveri radi pregleda i instalacije nedostajućih drajvera", "切换到驱动选项卡以查看并安装缺失的驱动程序"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _btnDriverGateOpenDrivers.Click += (s, e) =>
            {
                Action h = _openDriversTabHandler;
                if (h != null)
                {
                    try { h(); } catch { }
                }
            };
            btnPanel.Children.Add(_btnDriverGateOpenDrivers);

            _btnDriverGateRecheck = new Button
            {
                Content = HwTestLocalization.Pick("🔄 Proveri ponovo", "🔄 重新检查"),
                ToolTip = HwTestLocalization.Pick("Ponovo proveri stanje drajvera i Device Manager-a", "重新检查驱动程序与设备管理器状态"),
                Background = new SolidColorBrush(Color.FromRgb(55, 62, 76)),
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnDriverGateRecheck.Click += (s, e) => RefreshDriverGateUi();
            btnPanel.Children.Add(_btnDriverGateRecheck);

            _btnDriverGateEmergencyUnlock = new Button
            {
                Content = HwTestLocalization.Pick("⚠️ Vanredno otključaj testove", "⚠️ 紧急解锁测试"),
                ToolTip = HwTestLocalization.Pick("Vanredno otključaj interaktivne testove uz obavezan serviserski razlog (samo nakon završene instalacije drajvera)", "在驱动安装完成后填写技术员原因以紧急解锁交互式测试"),
                Background = new SolidColorBrush(Color.FromRgb(180, 90, 20)),
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _btnDriverGateEmergencyUnlock.Click += (s, e) => PromptEmergencyDriverGateUnlock();
            btnPanel.Children.Add(_btnDriverGateEmergencyUnlock);

            sp.Children.Add(btnPanel);
            banner.Child = sp;
            return banner;
        }

        public DriverGateState GetCurrentDriverGateState()
        {
            return EvaluateEffectiveDriverGateState();
        }

        public bool IsInteractiveDriverGateUnlocked()
        {
            DriverGateState st = EvaluateEffectiveDriverGateState();
            return st != null && st.IsUnlocked;
        }

        private DriverGateState EvaluateEffectiveDriverGateState()
        {
            DriverGateState raw = null;
            Func<DriverGateState> provider = _driverGateProvider;
            if (provider != null)
            {
                try { raw = provider(); }
                catch { raw = null; }
            }
            if (raw == null)
            {
                raw = DriverReadinessGate.EvaluateLocal();
            }
            if (raw == null)
            {
                raw = new DriverGateState { IsUnlocked = true };
            }

            // Odluka 1A: Vanredno otkljucavanje vazi iskljucivo kada instalacija drajvera NIJE aktivna,
            // nema Code 14 restarta i nema BackgroundWu drajvera (tj. raw.CanEmergencyUnlock == true).
            if (!raw.IsUnlocked &&
                _driverGateEmergencyUnlocked &&
                !string.IsNullOrEmpty(_driverGateEmergencyOverrideReason) &&
                raw.CanEmergencyUnlock &&
                !raw.IsDriverInstallActive &&
                !raw.HasCode14PendingReboot &&
                !raw.HasBackgroundWuDrivers)
            {
                DriverGateState unlockedOverride = new DriverGateState
                {
                    IsUnlocked = true,
                    CanEmergencyUnlock = false,
                    ReasonTitle = raw.ReasonTitle,
                    ReasonDetail = raw.ReasonDetail,
                    ProblemDevices = raw.ProblemDevices != null ? new List<string>(raw.ProblemDevices) : new List<string>(),
                    IsDriverInstallActive = false,
                    HasCode14PendingReboot = false,
                    HasBackgroundWuDrivers = false
                };
                _lastDriverGateState = unlockedOverride;
                return unlockedOverride;
            }

            _lastDriverGateState = raw;
            return raw;
        }

        private bool HasPendingAutomatedTests()
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.IsAutomated && (t.Status == TestStatus.Pending || t.Status == TestStatus.Running))
                {
                    return true;
                }
            }
            return false;
        }

        public void RefreshDriverGateUi()
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshDriverGateUi));
                return;
            }

            DriverGateState state = EvaluateEffectiveDriverGateState();
            bool unlocked = state != null && state.IsUnlocked;

            if (!unlocked)
            {
                _driverGateWasLocked = true;
            }
            else if (_driverGateWasLocked)
            {
                _driverGateWasLocked = false;
                RefreshHardwareProbeAfterDrivers();
            }

            if (_driverGateBanner != null)
            {
                _driverGateBanner.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
            }

            if (!unlocked && state != null)
            {
                if (_driverGateBannerTitle != null)
                {
                    string title = !string.IsNullOrEmpty(state.ReasonTitle)
                        ? state.ReasonTitle
                        : HwTestLocalization.Pick("Interaktivni testovi su zaključani dok se svi drajveri ne instaliraju", "所有驱动程序安装完成前，交互式测试已锁定");
                    _driverGateBannerTitle.Text = "🔒 " + title;
                }
                if (_driverGateBannerDetail != null)
                {
                    _driverGateBannerDetail.Text = !string.IsNullOrEmpty(state.ReasonDetail)
                        ? state.ReasonDetail
                        : HwTestLocalization.Pick("Instalirajte sve nedostajuće drajvere pre pokretanja interaktivnih testova periferija.", "请先安装所有缺失的驱动程序，然后再启动外设交互式测试。");
                }
                if (_btnDriverGateOpenDrivers != null)
                {
                    _btnDriverGateOpenDrivers.Visibility = (_openDriversTabHandler != null) ? Visibility.Visible : Visibility.Collapsed;
                }
                if (_btnDriverGateEmergencyUnlock != null)
                {
                    bool canEmergency = state.CanEmergencyUnlock &&
                                        !state.IsDriverInstallActive &&
                                        !state.HasCode14PendingReboot &&
                                        !state.HasBackgroundWuDrivers;
                    _btnDriverGateEmergencyUnlock.Visibility = canEmergency ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (!t.IsAutomated)
                {
                    Button btnRun;
                    if (_runBtnMap.TryGetValue(t.Id, out btnRun) && btnRun != null)
                    {
                        btnRun.IsEnabled = unlocked;
                        btnRun.Opacity = unlocked ? 1.0 : 0.45;
                    }
                }
            }

            if (_btnRunAll != null)
            {
                bool wouldStartInteractive = !HasPendingAutomatedTests() || (_phase5WorkflowActive && _waitingForManualPeripherals);
                bool canRunAll = !_autoStressRunning && (unlocked || !wouldStartInteractive);
                _btnRunAll.IsEnabled = canRunAll;
                _btnRunAll.Opacity = canRunAll ? 1.0 : 0.50;
            }

            if (_driverGatePollTimer != null)
            {
                if (!unlocked)
                {
                    if (!_driverGatePollTimer.IsEnabled) _driverGatePollTimer.Start();
                }
                else
                {
                    if (_driverGatePollTimer.IsEnabled) _driverGatePollTimer.Stop();
                }
            }

            if (unlocked && _phase5WorkflowActive && _waitingForManualPeripherals && !_phase5CompletedNotified && !_autoStressRunning)
            {
                List<string> pendingNames = GetPendingInteractiveTestNames();
                if (pendingNames.Count > 0)
                {
                    EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), "RUNNING", "");
                    if (_autoSectionStatus != null)
                    {
                        _autoSectionStatus.Text = HwTestLocalization.PickFormat("⚠️ Drajveri su spremni — preostalo {0} obaveznih interaktivnih testova ({1}).", "⚠️ 驱动程序已就绪 — 剩余 {0} 项必选交互式测试（{1}）。",
                            pendingNames.Count,
                            string.Join(", ", pendingNames.ToArray()));
                    }
                    RaisePendingInteractiveTestsRequired();
                }
                else
                {
                    CheckAndHandlePhase5InteractiveCompletion();
                }
            }
        }

        private bool EnsureInteractiveTestsDriverGateUnlocked(bool showMessageBox)
        {
            RefreshDriverGateUi();
            DriverGateState state = EvaluateEffectiveDriverGateState();
            if (state != null && state.IsUnlocked)
            {
                if (!_hardwareProbeRefreshedAfterDrivers)
                {
                    RefreshHardwareProbeAfterDrivers();
                }
                return true;
            }

            string title = (state != null && !string.IsNullOrEmpty(state.ReasonTitle))
                ? state.ReasonTitle
                : HwTestLocalization.Pick("Interaktivni testovi su zaključani dok svi drajveri nisu instalirani", "所有驱动程序安装完成前，交互式测试已锁定");
            string detail = (state != null && !string.IsNullOrEmpty(state.ReasonDetail))
                ? state.ReasonDetail
                : HwTestLocalization.Pick("Sačekajte završetak Faze 2 i instalaciju svih drajvera pre testiranja periferija.", "请等待阶段 2 和所有驱动程序安装完成后再测试外设。");

            HwTestLogger.Warn("DRIVER_GATE_BLOCKED", title + " | " + detail);

            if (showMessageBox)
            {
                MessageBox.Show(
                    title + "\n\n" + detail,
                    HwTestLocalization.Pick("Drajveri nisu spremni", "驱动程序未就绪"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }

        private void PromptEmergencyDriverGateUnlock()
        {
            DriverGateState state = EvaluateEffectiveDriverGateState();
            if (state == null || state.IsUnlocked)
            {
                RefreshDriverGateUi();
                return;
            }

            if (!state.CanEmergencyUnlock || state.IsDriverInstallActive || state.HasCode14PendingReboot || state.HasBackgroundWuDrivers)
            {
                MessageBox.Show(
                    HwTestLocalization.Pick(
                        "Vanredno otključavanje nije dozvoljeno dok je instalacija drajvera u toku, dok pozadinski Windows Update preuzima drajvere ili dok sistem čeka restart (Code 14).",
                        "驱动安装进行中、后台 Windows Update 正在安装驱动或系统等待重启 (Code 14) 时，不允许紧急解锁。"),
                    HwTestLocalization.Pick("Vanredno otključavanje odbijeno", "拒绝紧急解锁"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                HwTestLocalization.PickFormat(
                    "U Device Manager-u su i dalje prisutni uređaji sa greškom posle završene instalacije drajvera:\n\n{0}\n\n" +
                    "Da li potvrđujete da je instalacija drajvera završena i želite vanredno da otključate interaktivne testove uz unos serviserskog razloga (biće trajno zabeleženo u izveštaju)?",
                    "驱动安装完成后，设备管理器中仍存在异常设备：\n\n{0}\n\n" +
                    "是否确认驱动安装已结束，并填写技术员原因以紧急解锁交互式测试（将永久记录在报告中）？",
                    state.ReasonDetail),
                HwTestLocalization.Pick("Vanredno otključavanje interaktivnih testova", "紧急解锁交互式测试"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            Window promptWin = new Window
            {
                Title = HwTestLocalization.Pick("Serviserski razlog za vanredno otključavanje testova", "紧急解锁测试的技术员原因"),
                Width = 480,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Owner = GetDialogOwner()
            };

            StackPanel rootSp = new StackPanel { Margin = new Thickness(16) };
            TextBlock lbl = new TextBlock
            {
                Text = HwTestLocalization.Pick("Unesite tačan serviserski razlog za vanredno otključavanje (najmanje 5 znakova, npr. neispravan senzor ili uređaj bez podrške):", "请输入紧急解锁的详细技术员原因（至少 5 个字符，例如传感器损坏或无可用驱动）："),
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            rootSp.Children.Add(lbl);

            TextBox tbReason = new TextBox
            {
                Height = 56,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                FontSize = 12,
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Color.FromRgb(22, 24, 28)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 92, 108))
            };
            rootSp.Children.Add(tbReason);

            StackPanel btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            string enteredReason = "";
            Button btnOk = new Button
            {
                Content = HwTestLocalization.Pick("Otključaj testove", "解锁测试"),
                Background = new SolidColorBrush(Color.FromRgb(180, 90, 20)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnOk.Click += (s, e) =>
            {
                string r = (tbReason.Text ?? "").Trim();
                if (r.Length < 5)
                {
                    MessageBox.Show(
                        HwTestLocalization.Pick("Unesite validan serviserski razlog (najmanje 5 znakova).", "请输入有效的技术员原因（至少 5 个字符）。"),
                        HwTestLocalization.Pick("Prekratak razlog", "原因过短"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                enteredReason = r;
                promptWin.DialogResult = true;
                promptWin.Close();
            };

            Button btnCancel = new Button
            {
                Content = HwTestLocalization.Pick("Otkaži", "取消"),
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 72)),
                Foreground = Brushes.White,
                Padding = new Thickness(14, 5, 14, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnCancel.Click += (s, e) =>
            {
                promptWin.DialogResult = false;
                promptWin.Close();
            };

            btnRow.Children.Add(btnOk);
            btnRow.Children.Add(btnCancel);
            rootSp.Children.Add(btnRow);
            promptWin.Content = rootSp;

            bool? ok = promptWin.ShowDialog();
            if (ok == true && enteredReason.Length >= 5)
            {
                _driverGateEmergencyOverrideReason = enteredReason;
                _driverGateEmergencyUnlocked = true;
                HwTestLogger.Warn("DRIVER_GATE_OVERRIDE", "Vanredno otključani interaktivni testovi uz razlog: " + _driverGateEmergencyOverrideReason);
                try { SaveCheckpoint(""); } catch { }
                RefreshDriverGateUi();
            }
        }

        private void StartInitialHardwareProbeAsync()
        {
            if (Interlocked.CompareExchange(ref _probeRefreshInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
            {
                RunProbeRefreshWorker(false);
            }));
        }

        public void RefreshHardwareProbeAfterDrivers(bool force = false)
        {
            if (_phase5CompletedNotified) return;
            if (!force && _hardwareProbeRefreshedAfterDrivers) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            long prevTicks = Interlocked.Read(ref _lastProbeRefreshUtcTicks);
            if (!force && prevTicks > 0 && (nowTicks - prevTicks) < TimeSpan.FromMilliseconds(1500).Ticks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _probeRefreshInFlight, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _lastProbeRefreshUtcTicks, nowTicks);
            ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
            {
                RunProbeRefreshWorker(true);
            }));
        }

        private void RunProbeRefreshWorker(bool isPostDriverRefresh)
        {
            bool dispatchedToUi = false;
            try
            {
                HardwareProbeResult fresh = HardwareProbe.DetectHardware();
                if (fresh == null) return;

                if (string.IsNullOrEmpty(fresh.Manufacturer) && !string.IsNullOrEmpty(_hwInfo.Manufacturer))
                    fresh.Manufacturer = _hwInfo.Manufacturer;
                if (string.IsNullOrEmpty(fresh.SystemSkuNumber) && !string.IsNullOrEmpty(_hwInfo.SystemSkuNumber))
                    fresh.SystemSkuNumber = _hwInfo.SystemSkuNumber;
                if (IsPlaceholderIdentity(fresh.ModelName) && !IsPlaceholderIdentity(_modelName))
                    fresh.ModelName = _modelName;
                if (IsPlaceholderIdentity(fresh.SerialNumber) && !IsPlaceholderIdentity(_serialNumber))
                    fresh.SerialNumber = _serialNumber;

                FactorySpecProbe.ResolveAndMerge(fresh, _profileClient);

                if (Dispatcher != null)
                {
                    dispatchedToUi = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            ApplyHardwareProbeSnapshotOnUi(fresh, isPostDriverRefresh);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _probeRefreshInFlight, 0);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("PROBE_REFRESH", "Greška pri pozadinskom osvežavanju hardverske probe: " + ex.Message);
            }
            finally
            {
                if (!dispatchedToUi)
                {
                    Interlocked.Exchange(ref _probeRefreshInFlight, 0);
                }
            }
        }

        private void ApplyHardwareProbeSnapshotOnUi(HardwareProbeResult fresh, bool isPostDriverRefresh)
        {
            if (fresh == null) return;
            try
            {
                CopyHardwareProbeResult(fresh, _hwInfo);
                if (IsPlaceholderIdentity(_modelName) && !IsPlaceholderIdentity(_hwInfo.ModelName))
                    _modelName = _hwInfo.ModelName;
                if (IsPlaceholderIdentity(_serialNumber) && !IsPlaceholderIdentity(_hwInfo.SerialNumber))
                    _serialNumber = _hwInfo.SerialNumber;

                if (isPostDriverRefresh)
                {
                    _hardwareProbeRefreshedAfterDrivers = true;
                }

                HwTestLogger.Info("PROBE", string.Format(
                    "Mfg={0} | Model={1} | SN={2} | SKU={3} | MTM={4} | Diag={5}\" | Chassis={6} | KbdType={7} | OsOem102={8} | RecLayout={9} | Touch={10} | Stylus={11} ({12}) | Pad={13} | TrackPoint={14} | RotSensor={15} | Cam={16} ({17}) | IR={18} | AudioOut={19} | Mic={20} | FP={21} ({22} | Id={23} | Err={24} | BIOS='{25}' | WbioSvc='{26}')",
                    _hwInfo.Manufacturer, _modelName, _serialNumber, _hwInfo.SystemSkuNumber, _hwInfo.ProductMtm,
                    _hwInfo.ScreenInches, _hwInfo.IsLaptop ? "Laptop" : "Desktop", _hwInfo.KeyboardType,
                    _hwInfo.OsHasOem102.HasValue ? _hwInfo.OsHasOem102.Value.ToString() : "null",
                    _hwInfo.RecommendedLayout, _hwInfo.HasTouchscreen, _hwInfo.HasStylusPen, _hwInfo.StylusPenName,
                    _hwInfo.HasTouchpad, _hwInfo.HasTrackPoint, _hwInfo.HasRotationSensor,
                    _hwInfo.HasWebcam, _hwInfo.WebcamName, _hwInfo.HasIrCamera,
                    _hwInfo.HasSpeakers, _hwInfo.HasMicrophone,
                    _hwInfo.HasFingerprint, _hwInfo.FingerprintName,
                    _hwInfo.FingerprintDeviceId, _hwInfo.FingerprintPnpError,
                    _hwInfo.FingerprintBiosStatus, _hwInfo.WbioServiceState));

                if (isPostDriverRefresh)
                {
                    for (int i = 0; i < _testItems.Count; i++)
                    {
                        TestItemResult t = _testItems[i];
                        if (t != null && t.IsPassivePreliminary)
                        {
                            t.Status = TestStatus.Pending;
                            t.HasWarning = false;
                            t.IsPassivePreliminary = false;
                            t.Reason = string.Empty;
                            t.MetricSummary = string.Empty;
                            Border card;
                            if (_cardMap.TryGetValue(t.Id, out card) && card != null)
                            {
                                UpdateCardVisual(card, t);
                            }
                        }
                    }
                }

                // Sacuvaj postojece testove kako bismo osvezili SAMO nezapocete (Pending / NotApplicable) stavke
                List<TestItemResult> snapshot = new List<TestItemResult>(_testItems);
                _testItems.Clear();
                InitTestList();
                List<TestItemResult> freshItems = new List<TestItemResult>(_testItems);
                _testItems.Clear();

                Dictionary<string, TestItemResult> freshMap = new Dictionary<string, TestItemResult>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < freshItems.Count; i++)
                {
                    freshMap[freshItems[i].Id] = freshItems[i];
                }

                int updatedPendingOrNa = 0;
                int preservedCompleted = 0;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    TestItemResult existing = snapshot[i];
                    _testItems.Add(existing);

                    bool isCompletedOrRestored =
                        !existing.IsPassivePreliminary &&
                        (existing.Status == TestStatus.Pass ||
                         existing.Status == TestStatus.Fail ||
                         existing.Status == TestStatus.Running ||
                         (existing.Status == TestStatus.Skip && !string.IsNullOrEmpty(existing.Reason)) ||
                         (_restoredTestIds.Contains(existing.Id) && existing.Status != TestStatus.Pending && existing.Status != TestStatus.NotApplicable));

                    if (isCompletedOrRestored)
                    {
                        preservedCompleted++;
                        continue;
                    }

                    TestItemResult freshItem;
                    if (freshMap.TryGetValue(existing.Id, out freshItem) && freshItem != null)
                    {
                        existing.Title = freshItem.Title;
                        existing.Description = freshItem.Description;
                        if (!existing.IsAutomated)
                        {
                            existing.Status = freshItem.Status;
                            existing.HasWarning = freshItem.HasWarning;
                            existing.Reason = freshItem.Reason;
                            existing.Details = freshItem.Details;
                        }
                        TextBlock tbTitle;
                        if (_titleTextMap.TryGetValue(existing.Id, out tbTitle) && tbTitle != null)
                        {
                            tbTitle.Text = existing.Title;
                        }
                        Border card;
                        if (_cardMap.TryGetValue(existing.Id, out card) && card != null)
                        {
                            UpdateCardVisual(card, existing);
                        }
                        updatedPendingOrNa++;
                    }
                }

                if (_subTitleBlock != null)
                {
                    _subTitleBlock.Text = BuildSubTitleText();
                }

                RefreshCardVisibility();
                UpdateSummaryBadge();
                if (isPostDriverRefresh)
                {
                    HwTestLogger.Info("PROBE_REFRESH", string.Format(
                        "Osvežena hardverska proba posle instalacije drajvera (ažurirano Pending/NA={0}, sačuvano završenih={1}).",
                        updatedPendingOrNa, preservedCompleted));
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("PROBE_REFRESH", "Greška pri primeni hardverske probe na UI: " + ex.Message);
            }
        }

        private static void CopyHardwareProbeResult(HardwareProbeResult source, HardwareProbeResult target)
        {
            if (source == null || target == null) return;
            target.HasTouchscreen = source.HasTouchscreen;
            target.MaxTouches = source.MaxTouches;
            target.TouchscreenPnpPresent = source.TouchscreenPnpPresent;
            target.TouchscreenBiosPresent = source.TouchscreenBiosPresent;
            target.TouchscreenDisabledInBios = source.TouchscreenDisabledInBios;
            target.TouchscreenBiosStatus = source.TouchscreenBiosStatus;
            target.HasTouchpad = source.HasTouchpad;
            target.TouchpadBiosPresent = source.TouchpadBiosPresent;
            target.TouchpadDisabledInBios = source.TouchpadDisabledInBios;
            target.TouchpadBiosStatus = source.TouchpadBiosStatus;
            target.TrackPointBiosPresent = source.TrackPointBiosPresent;
            target.TrackPointDisabledInBios = source.TrackPointDisabledInBios;
            target.TrackPointBiosStatus = source.TrackPointBiosStatus;
            target.FactoryTrackPoint = source.FactoryTrackPoint;
            target.FactoryTrackPointEvidence = source.FactoryTrackPointEvidence;
            target.HasWebcam = source.HasWebcam;
            target.WebcamPnpPresent = source.WebcamPnpPresent;
            target.WebcamName = source.WebcamName;
            target.CameraBiosPresent = source.CameraBiosPresent;
            target.CameraDisabledInBios = source.CameraDisabledInBios;
            target.CameraBiosStatus = source.CameraBiosStatus;
            target.HasIrCamera = source.HasIrCamera;
            target.IrCameraName = source.IrCameraName;
            target.HasSpeakers = source.HasSpeakers;
            target.HasMicrophone = source.HasMicrophone;
            target.MicrophoneBiosPresent = source.MicrophoneBiosPresent;
            target.MicrophoneDisabledInBios = source.MicrophoneDisabledInBios;
            target.MicrophoneBiosStatus = source.MicrophoneBiosStatus;
            target.HasFingerprint = source.HasFingerprint;
            target.FingerprintName = source.FingerprintName;
            target.FingerprintDeviceId = source.FingerprintDeviceId;
            target.FingerprintPnpError = source.FingerprintPnpError;
            target.FingerprintBiosPresent = source.FingerprintBiosPresent;
            target.FingerprintDisabledInBios = source.FingerprintDisabledInBios;
            target.FingerprintBiosStatus = source.FingerprintBiosStatus;
            target.WbioServiceState = source.WbioServiceState;
            target.FingerprintUnitId = source.FingerprintUnitId;
            target.PnpAuditNotes = source.PnpAuditNotes;
            target.BiosAuditNotes = source.BiosAuditNotes;
            target.IsLaptop = source.IsLaptop;
            target.RecommendedLayout = source.RecommendedLayout;
            target.FactoryBomLoaded = source.FactoryBomLoaded;
            target.FactorySource = source.FactorySource;
            target.FactorySummary = source.FactorySummary;
            target.FactoryTouchscreen = source.FactoryTouchscreen;
            target.FactoryTouchscreenEvidence = source.FactoryTouchscreenEvidence;
            target.FactoryFingerprint = source.FactoryFingerprint;
            target.FactoryFingerprintEvidence = source.FactoryFingerprintEvidence;
            target.FactoryWebcam = source.FactoryWebcam;
            target.FactoryWebcamEvidence = source.FactoryWebcamEvidence;
            target.FactoryIrCamera = source.FactoryIrCamera;
            target.FactoryIrCameraEvidence = source.FactoryIrCameraEvidence;
            target.FactoryTouchpad = source.FactoryTouchpad;
            target.FactorySpeakers = source.FactorySpeakers;
            target.FactoryMicrophone = source.FactoryMicrophone;
            if (!string.IsNullOrEmpty(source.Manufacturer)) target.Manufacturer = source.Manufacturer;
            if (!string.IsNullOrEmpty(source.ModelName)) target.ModelName = source.ModelName;
            if (!string.IsNullOrEmpty(source.SerialNumber)) target.SerialNumber = source.SerialNumber;
            if (!string.IsNullOrEmpty(source.SystemSkuNumber)) target.SystemSkuNumber = source.SystemSkuNumber;
            if (!string.IsNullOrEmpty(source.ProductMtm)) target.ProductMtm = source.ProductMtm;
            target.ScreenInches = source.ScreenInches;
            target.OsHasOem102 = source.OsHasOem102;
            target.KeyboardType = source.KeyboardType;
            target.HasNavColumn = source.HasNavColumn;
            target.IsThinkPad = source.IsThinkPad;
            target.FactoryKeyboardIso = source.FactoryKeyboardIso;
            target.FactoryKeyboardNumpad = source.FactoryKeyboardNumpad;
            target.FactoryKeyboardDesc = source.FactoryKeyboardDesc;
            target.FactoryKeyboardBacklight = source.FactoryKeyboardBacklight;
            target.FactoryKeyboardBacklightEvidence = source.FactoryKeyboardBacklightEvidence;
            target.HasKeyboardBacklight = source.HasKeyboardBacklight;
            target.KeyboardBacklightBiosDisabled = source.KeyboardBacklightBiosDisabled;
            target.KeyboardBacklightEvidence = source.KeyboardBacklightEvidence;
            target.EstimatedUsbPortCount = source.EstimatedUsbPortCount;
            target.HasStylusPen = source.HasStylusPen;
            target.StylusPenName = source.StylusPenName;
            target.HasTrackPoint = source.HasTrackPoint;
            target.HasRotationSensor = source.HasRotationSensor;
        }

        private Border CreateTestCard(TestItemResult t)
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 56)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 5),
                Tag = t
            };

            StackPanel rootStack = new StackPanel();

            Grid cg = new Grid();
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Indikator statusa / animirani spinner dok test radi
            Grid indHost = new Grid
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            Border statusCircle = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 72))
            };
            TextBlock statusIcon = new TextBlock
            {
                Text = "○",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusCircle.Child = statusIcon;
            _statusIndicatorMap[t.Id] = statusCircle;
            _statusIconMap[t.Id] = statusIcon;
            indHost.Children.Add(statusCircle);

            RotateTransform spinRot = new RotateTransform(0);
            Border spinnerRing = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(2, 2, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = spinRot,
                Visibility = Visibility.Collapsed
            };
            _spinnerRingMap[t.Id] = spinnerRing;
            _spinnerRotateMap[t.Id] = spinRot;
            indHost.Children.Add(spinnerRing);

            Grid.SetColumn(indHost, 0);
            cg.Children.Add(indHost);

            // Col 1: Naziv testa + živo trajanje i 1 kompaktna linija rezultata/opisa
            StackPanel spInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            Grid titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock tbTitle = new TextBlock
            {
                Text = t.Title,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _titleTextMap[t.Id] = tbTitle;
            Grid.SetColumn(tbTitle, 0);
            titleRow.Children.Add(tbTitle);

            TextBlock tbElapsed = new TextBlock
            {
                Text = "",
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _elapsedTextMap[t.Id] = tbElapsed;
            Grid.SetColumn(tbElapsed, 1);
            titleRow.Children.Add(tbElapsed);
            spInfo.Children.Add(titleRow);

            TextBlock tbMetric = new TextBlock
            {
                Text = t.MetricSummary ?? "",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = t.MetricSummary ?? "",
                Margin = new Thickness(0, 1, 0, 0)
            };
            spInfo.Children.Add(tbMetric);
            _metricTextMap[t.Id] = tbMetric;

            TextBlock tbDesc = new TextBlock
            {
                Text = t.Description ?? "",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 165)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = t.Description ?? "",
                Margin = new Thickness(0, 1, 0, 0)
            };
            spInfo.Children.Add(tbDesc);
            _descTextMap[t.Id] = tbDesc;

            Grid.SetColumn(spInfo, 1);
            cg.Children.Add(spInfo);

            // Col 2: Kompaktan status bedž
            Border statusBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                MinWidth = 60,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            TextBlock tbStatus = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            statusBorder.Child = tbStatus;
            _statusBadgeMap[t.Id] = statusBorder;
            _statusBadgeTextMap[t.Id] = tbStatus;
            Grid.SetColumn(statusBorder, 2);
            cg.Children.Add(statusBorder);

            // Col 3: Kompaktna dugmad desno
            StackPanel spBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (t.IsAutomated)
            {
                Button btnDetails = new Button
                {
                    Content = HwTestLocalization.Pick("Detalji", "详情"),
                    ToolTip = HwTestLocalization.Pick("Otvori detaljan izveštaj testa", "打开该测试的详细报告"),
                    Background = new SolidColorBrush(Color.FromRgb(50, 56, 70)),
                    Foreground = Brushes.White,
                    FontSize = 10.5,
                    Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(0, 0, 5, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                btnDetails.Click += (s, e) => OpenAutoDetails(t);
                _detailsBtnMap[t.Id] = btnDetails;
                spBtns.Children.Add(btnDetails);
            }

            Button btnRun = new Button
            {
                Content = "▶",
                ToolTip = HwTestLocalization.Pick("Pokreni ovaj test", "运行此测试"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnRun.Click += (s, e) => RunSingleTest(t);
            _runBtnMap[t.Id] = btnRun;
            spBtns.Children.Add(btnRun);

            if (!t.IsAutomated)
            {
                Button btnBios = new Button
                {
                    Content = "🔧 UEFI BIOS",
                    ToolTip = HwTestLocalization.Pick("Restartuj računar u UEFI BIOS podešavanja da biste uključili uređaj (WIS nastavlja gde je stao)", "重启电脑进入 UEFI BIOS 设置以启用该设备（WIS 将从中断处继续）"),
                    Background = new SolidColorBrush(Color.FromRgb(180, 110, 20)),
                    Foreground = Brushes.White,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(5, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Visibility = Visibility.Collapsed
                };
                btnBios.Click += (s, e) => RequestUefiBiosReboot(t.Id);
                _biosBtnMap[t.Id] = btnBios;
                spBtns.Children.Add(btnBios);
            }

            Grid.SetColumn(spBtns, 3);
            cg.Children.Add(spBtns);

            rootStack.Children.Add(cg);

            // Indikator rada duž dna reda (vidljiv samo kada test radi)
            ProgressBar runBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                Margin = new Thickness(0, 5, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            _runningBarMap[t.Id] = runBar;
            rootStack.Children.Add(runBar);

            card.Child = rootStack;
            UpdateCardVisual(card, t);
            return card;
        }

        private static bool IsBoolDetailTrue(Dictionary<string, object> details, string key)
        {
            if (details == null || string.IsNullOrEmpty(key) || !details.ContainsKey(key) || details[key] == null)
                return false;
            object v = details[key];
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private void SyncCardRowTexts(TestItemResult t)
        {
            if (t == null) return;
            bool hasWarn = t.HasWarning || (t.Details != null &&
                (t.Details.ContainsKey("bios_disabled") || t.Details.ContainsKey("factory_missing_in_os") || IsBoolDetailTrue(t.Details, "bios_action_recommended")));

            TextBlock tbMetric;
            bool hasMetric = !string.IsNullOrEmpty(t.MetricSummary);
            if (_metricTextMap.TryGetValue(t.Id, out tbMetric))
            {
                tbMetric.Text = t.MetricSummary ?? "";
                tbMetric.ToolTip = t.MetricSummary ?? "";
                tbMetric.Foreground = (t.Status == TestStatus.Running)
                    ? new SolidColorBrush(Color.FromRgb(56, 189, 248))
                    : new SolidColorBrush(Color.FromRgb(78, 201, 176));
                tbMetric.Visibility = hasMetric ? Visibility.Visible : Visibility.Collapsed;
            }

            TextBlock tbDesc;
            if (_descTextMap.TryGetValue(t.Id, out tbDesc))
            {
                string fullDesc;
                if (t.IsAutomated && !string.IsNullOrEmpty(t.Reason) && t.Status != TestStatus.Pending)
                {
                    fullDesc = t.Reason;
                }
                else
                {
                    fullDesc = (t.Status == TestStatus.NotApplicable && !string.IsNullOrEmpty(t.Reason))
                        ? (t.Description + " — " + t.Reason)
                        : (!string.IsNullOrEmpty(t.Reason) && (t.Status == TestStatus.Fail || t.Status == TestStatus.Skip || hasWarn))
                            ? t.Reason
                            : t.Description;
                }

                tbDesc.Text = fullDesc ?? "";
                tbDesc.ToolTip = fullDesc ?? "";

                if (t.Status == TestStatus.Fail)
                    tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 140));
                else if (hasWarn)
                    tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102));
                else
                    tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(155, 162, 174));

                bool showDesc = !hasMetric || t.Status == TestStatus.Fail || hasWarn || t.Status == TestStatus.NotApplicable;
                tbDesc.Visibility = (showDesc && !string.IsNullOrEmpty(fullDesc)) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string FormatShortDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int mins = seconds / 60;
            int secs = seconds % 60;
            return string.Format("{0:00}:{1:00}", mins, secs);
        }

        private void UpdateRunningElapsedTimers()
        {
            if (_runningStartUtcMap.Count == 0)
            {
                if (_runningUiTimer != null) _runningUiTimer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, DateTime> kv in _runningStartUtcMap)
            {
                TextBlock tbElapsed;
                if (_elapsedTextMap.TryGetValue(kv.Key, out tbElapsed))
                {
                    int sec = (int)Math.Max(0, (now - kv.Value).TotalSeconds);
                    tbElapsed.Text = FormatShortDuration(sec);
                    tbElapsed.Visibility = Visibility.Visible;
                }
            }
        }

        private void UpdateCardVisual(Border card, TestItemResult t)
        {
            if (card == null || t == null) return;

            bool needsBiosAction = IsBoolDetailTrue(t.Details, "bios_action_recommended");
            bool hasWarn = t.HasWarning || (t.Details != null &&
                (t.Details.ContainsKey("bios_disabled") || t.Details.ContainsKey("factory_missing_in_os") || needsBiosAction));

            Button btnBios;
            if (_biosBtnMap.TryGetValue(t.Id, out btnBios) && btnBios != null)
            {
                bool showBiosBtn = needsBiosAction && t.Status != TestStatus.Pass && t.Status != TestStatus.NotApplicable;
                btnBios.Visibility = showBiosBtn ? Visibility.Visible : Visibility.Collapsed;
            }

            if (t.Status == TestStatus.Running)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(27, 40, 54));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                card.BorderThickness = new Thickness(3, 1, 1, 1);
                card.Opacity = 1.0;
            }
            else if (t.Status == TestStatus.Fail)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(38, 28, 30));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                card.BorderThickness = new Thickness(3, 1, 1, 1);
                card.Opacity = 1.0;
            }
            else if (hasWarn)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(38, 34, 26));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                card.BorderThickness = new Thickness(3, 1, 1, 1);
                card.Opacity = 1.0;
            }
            else if (t.Status == TestStatus.Pass)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(30, 34, 32));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 110, 72));
                card.BorderThickness = new Thickness(2, 1, 1, 1);
                card.Opacity = 1.0;
            }
            else if (t.Status == TestStatus.NotApplicable)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(28, 28, 31));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(46, 48, 55));
                card.BorderThickness = new Thickness(1);
                card.Opacity = 0.68;
            }
            else
            {
                card.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 51, 56));
                card.BorderThickness = new Thickness(1);
                card.Opacity = 1.0;
            }

            SyncCardRowTexts(t);

            // Indikator rada (spinner + progress bar + live tajmer)
            Border statusCircle;
            TextBlock statusIcon;
            Border spinnerRing;
            RotateTransform spinRot;
            ProgressBar runBar;
            TextBlock tbElapsed;

            _statusIndicatorMap.TryGetValue(t.Id, out statusCircle);
            _statusIconMap.TryGetValue(t.Id, out statusIcon);
            _spinnerRingMap.TryGetValue(t.Id, out spinnerRing);
            _spinnerRotateMap.TryGetValue(t.Id, out spinRot);
            _runningBarMap.TryGetValue(t.Id, out runBar);
            _elapsedTextMap.TryGetValue(t.Id, out tbElapsed);

            if (t.Status == TestStatus.Running)
            {
                if (statusCircle != null) statusCircle.Visibility = Visibility.Collapsed;
                if (spinnerRing != null) spinnerRing.Visibility = Visibility.Visible;
                if (spinRot != null)
                {
                    DoubleAnimation anim = new DoubleAnimation(0.0, 360.0, new Duration(TimeSpan.FromMilliseconds(900)));
                    anim.RepeatBehavior = RepeatBehavior.Forever;
                    spinRot.BeginAnimation(RotateTransform.AngleProperty, anim);
                }
                if (runBar != null) runBar.Visibility = Visibility.Visible;

                if (!_runningStartUtcMap.ContainsKey(t.Id))
                {
                    _runningStartUtcMap[t.Id] = DateTime.UtcNow;
                }
                if (_runningUiTimer != null && !_runningUiTimer.IsEnabled)
                {
                    _runningUiTimer.Start();
                }
                if (tbElapsed != null)
                {
                    int sec = (int)Math.Max(0, (DateTime.UtcNow - _runningStartUtcMap[t.Id]).TotalSeconds);
                    tbElapsed.Text = FormatShortDuration(sec);
                    tbElapsed.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                    tbElapsed.Visibility = Visibility.Visible;
                }
            }
            else
            {
                _runningStartUtcMap.Remove(t.Id);
                if (_runningStartUtcMap.Count == 0 && _runningUiTimer != null && _runningUiTimer.IsEnabled)
                {
                    _runningUiTimer.Stop();
                }

                if (spinRot != null) spinRot.BeginAnimation(RotateTransform.AngleProperty, null);
                if (spinnerRing != null) spinnerRing.Visibility = Visibility.Collapsed;
                if (runBar != null) runBar.Visibility = Visibility.Collapsed;
                if (statusCircle != null) statusCircle.Visibility = Visibility.Visible;

                if (tbElapsed != null)
                {
                    if (t.DurationSeconds > 0)
                    {
                        tbElapsed.Text = FormatShortDuration(t.DurationSeconds);
                        tbElapsed.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
                        tbElapsed.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        tbElapsed.Visibility = Visibility.Collapsed;
                    }
                }
            }

            if (statusCircle != null && statusIcon != null)
            {
                switch (t.Status)
                {
                    case TestStatus.Pending:
                        statusCircle.Background = hasWarn
                            ? new SolidColorBrush(Color.FromRgb(210, 153, 34))
                            : new SolidColorBrush(Color.FromRgb(55, 60, 72));
                        statusIcon.Text = hasWarn ? "!" : "○";
                        break;
                    case TestStatus.Pass:
                        statusCircle.Background = t.HasWarning
                            ? new SolidColorBrush(Color.FromRgb(217, 130, 15))
                            : new SolidColorBrush(Color.FromRgb(35, 134, 54));
                        statusIcon.Text = t.HasWarning ? "!" : "✓";
                        break;
                    case TestStatus.Fail:
                        statusCircle.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                        statusIcon.Text = "✗";
                        break;
                    case TestStatus.Skip:
                        statusCircle.Background = new SolidColorBrush(Color.FromRgb(108, 117, 125));
                        statusIcon.Text = "»";
                        break;
                    case TestStatus.NotApplicable:
                        statusCircle.Background = new SolidColorBrush(Color.FromRgb(60, 65, 75));
                        statusIcon.Text = "—";
                        break;
                }
            }

            Border statusBorder;
            TextBlock tbStatus;
            _statusBadgeMap.TryGetValue(t.Id, out statusBorder);
            _statusBadgeTextMap.TryGetValue(t.Id, out tbStatus);

            if (statusBorder != null && tbStatus != null)
            {
                switch (t.Status)
                {
                    case TestStatus.Pending:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(55, 60, 72));
                        tbStatus.Text = HwTestLocalization.Pick("ČEKA", "等待");
                        break;
                    case TestStatus.Running:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                        tbStatus.Text = HwTestLocalization.Pick("U TOKU", "进行中");
                        break;
                    case TestStatus.Pass:
                        if (t.HasWarning)
                        {
                            statusBorder.Background = new SolidColorBrush(Color.FromRgb(217, 130, 15));
                            tbStatus.Text = HwTestLocalization.Pick("UPOZ.", "警告");
                        }
                        else
                        {
                            statusBorder.Background = new SolidColorBrush(Color.FromRgb(35, 134, 54));
                            tbStatus.Text = "PASS";
                        }
                        break;
                    case TestStatus.Fail:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                        tbStatus.Text = "FAIL";
                        break;
                    case TestStatus.Skip:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(108, 117, 125));
                        tbStatus.Text = "SKIP";
                        break;
                    case TestStatus.NotApplicable:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(60, 65, 75));
                        tbStatus.Text = "N/A";
                        break;
                }
            }
        }

        private void RunSingleTest(TestItemResult t)
        {
            if (t.IsAutomated)
            {
                if (_autoStressRunning)
                {
                    MessageBox.Show(HwTestLocalization.Pick("Automatski stres paket je već u toku. Sačekajte da se završi.", "自动压力测试包正在运行，请等待其完成。"),
                        HwTestLocalization.Pick("Test u toku", "测试进行中"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (t.Id == "auto_gpu")
                {
                    RunGpuStressTest(t);
                    return;
                }
                RunSingleAutoTestAsync(t);
                return;
            }

            if (!EnsureInteractiveTestsDriverGateUnlocked(true))
            {
                return;
            }

            bool prevHadBiosAction = IsBoolDetailTrue(t.Details, "bios_action_recommended");
            bool prevWarn = t.HasWarning;
            string prevReason = t.Reason;
            Dictionary<string, object> prevDetails = t.Details != null
                ? new Dictionary<string, object>(t.Details, StringComparer.OrdinalIgnoreCase)
                : null;

            Stopwatch sw = Stopwatch.StartNew();
            t.Status = TestStatus.Running;
            HwTestLogger.Info("TEST_START", string.Format("Pokrenut test '{0}' ({1})", t.Id, t.Title));
            Border card;
            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);

            Window dlg = null;
            switch (t.Id)
            {
                case "display": dlg = new DisplayTestWindow(); break;
                case "keyboard": dlg = new KeyboardTestWindow(_modelName, _hwInfo.RecommendedLayout, _profileClient, _hwInfo, _serialNumber); break;
                case "touchpad":
                    TouchpadTestWindow tpDlg = new TouchpadTestWindow(_hwInfo);
                    tpDlg.OnRequestUefiBiosReboot = RequestUefiBiosReboot;
                    tpDlg.ShowBiosRebootHint = (_hwInfo.TouchpadDisabledInBios || (!_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true));
                    tpDlg.RestoreStateFilePath = GetTouchpadRestoreFilePath();
                    dlg = tpDlg;
                    break;
                case "trackpoint":
                    bool trkHint = (_hwInfo.TrackPointDisabledInBios || (!_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true));
                    TouchpadTestWindow trkDlg = new TouchpadTestWindow(_hwInfo, PointingTestMode.TrackPointOnly, RequestUefiBiosReboot, trkHint);
                    trkDlg.RestoreStateFilePath = GetTouchpadRestoreFilePath();
                    dlg = trkDlg;
                    break;
                case "touchscreen": dlg = new TouchscreenMazeWindow(_hwInfo, RequestUefiBiosReboot); break;
                case "audio": dlg = new AudioTestWindow(); break;
                case "mic":
                    bool micHint = (_hwInfo.MicrophoneDisabledInBios || (!_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true));
                    dlg = new MicTestWindow(RequestUefiBiosReboot, micHint);
                    break;
                case "camera":
                    bool camHint = (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasWebcam && _hwInfo.FactoryWebcam == true));
                    dlg = new CameraTestWindow(CameraTestMode.RgbOnly, _hwInfo.WebcamName, RequestUefiBiosReboot, camHint);
                    break;
                case "ir_camera":
                    bool irCamHint = (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true));
                    dlg = new CameraTestWindow(CameraTestMode.IrOnly, _hwInfo.IrCameraName, RequestUefiBiosReboot, irCamHint);
                    break;
                case "usb":
                    ModelProfileData prof = _profileClient != null ? _profileClient.GetProfile(_modelName) : null;
                    int? pCount = (prof != null && prof.UsbPortCount.HasValue && prof.UsbPortCount.Value > 0)
                        ? prof.UsbPortCount
                        : (int?)_hwInfo.EstimatedUsbPortCount;
                    dlg = new UsbPortTestWindow(_modelName, pCount, _profileClient, _serialNumber);
                    break;
                case "fingerprint":
                    FingerprintTestWindow fpDlg = new FingerprintTestWindow(_hwInfo, RequestUefiBiosReboot);
                    fpDlg.ShowBiosRebootHint = (_hwInfo.FingerprintDisabledInBios || (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true));
                    dlg = fpDlg;
                    break;
            }

            if (dlg != null)
            {
                _activePeripheralDialog = dlg;
                try { dlg.ShowDialog(); }
                finally
                {
                    if (ReferenceEquals(_activePeripheralDialog, dlg)) _activePeripheralDialog = null;
                    MarkOperatorInteraction();
                }

                if (dlg is DisplayTestWindow) ApplyResult(t, ((DisplayTestWindow)dlg).Result);
                else if (dlg is KeyboardTestWindow) ApplyResult(t, ((KeyboardTestWindow)dlg).Result);
                else if (dlg is TouchpadTestWindow) ApplyResult(t, ((TouchpadTestWindow)dlg).Result);
                else if (dlg is TouchscreenMazeWindow) ApplyResult(t, ((TouchscreenMazeWindow)dlg).Result);
                else if (dlg is AudioTestWindow) ApplyResult(t, ((AudioTestWindow)dlg).Result);
                else if (dlg is MicTestWindow) ApplyResult(t, ((MicTestWindow)dlg).Result);
                else if (dlg is CameraTestWindow) ApplyResult(t, ((CameraTestWindow)dlg).Result);
                else if (dlg is UsbPortTestWindow) ApplyResult(t, ((UsbPortTestWindow)dlg).Result);
                else if (dlg is FingerprintTestWindow) ApplyResult(t, ((FingerprintTestWindow)dlg).Result);
            }

            // HWT-02 Odluka A4: zatvaranje bez PASS/FAIL vraća test u Pending (nikada Skip ili Running)
            if (t.Status == TestStatus.Running || t.Status == TestStatus.Pending || t.Status == TestStatus.Skip)
            {
                t.Status = TestStatus.Pending;
                if (prevHadBiosAction)
                {
                    t.HasWarning = prevWarn;
                    if (string.IsNullOrEmpty(t.Reason)) t.Reason = prevReason;
                    if (t.Details == null) t.Details = new Dictionary<string, object>();
                    t.Details["bios_action_recommended"] = true;
                    if (prevDetails != null)
                    {
                        if (prevDetails.ContainsKey("bios_disabled")) t.Details["bios_disabled"] = prevDetails["bios_disabled"];
                        if (prevDetails.ContainsKey("factory_missing_in_os")) t.Details["factory_missing_in_os"] = prevDetails["factory_missing_in_os"];
                        if (prevDetails.ContainsKey("bios_setting")) t.Details["bios_setting"] = prevDetails["bios_setting"];
                        if (prevDetails.ContainsKey("factory_evidence")) t.Details["factory_evidence"] = prevDetails["factory_evidence"];
                    }
                }
            }

            sw.Stop();
            t.DurationSeconds = (int)sw.Elapsed.TotalSeconds;
            if (!string.IsNullOrEmpty(_driverGateEmergencyOverrideReason) &&
                (t.Status == TestStatus.Pass || t.Status == TestStatus.Fail))
            {
                if (t.Details == null) t.Details = new Dictionary<string, object>();
                t.Details["driver_gate_emergency_override_reason"] = _driverGateEmergencyOverrideReason;
            }
            HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Trajanje={2}s | Reason='{3}' | Details={4}",
                t.Id, t.Status, t.DurationSeconds, t.Reason ?? "", SimpleJson.Serialize(t.Details ?? new Dictionary<string, object>())));

            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
            UpdateSummaryBadge();
            if (!_uefiBiosRebootRequested && (t.Status == TestStatus.Pass || t.Status == TestStatus.Fail))
            {
                try { SaveCheckpoint(""); } catch { }
            }

            if (!t.IsAutomated && !_inSequentialPeripheralRun)
            {
                CheckAndHandlePhase5InteractiveCompletion();
                if (t.Status == TestStatus.Pass || t.Status == TestStatus.Fail)
                {
                    ReconcilePhase5StateAndReportAfterTest();
                }
            }
        }

        private void RunGpuStressTest(TestItemResult t)
        {
            Stopwatch sw = Stopwatch.StartNew();
            t.Status = TestStatus.Running;
            HwTestLogger.Info("TEST_START", string.Format("Pokrenut automatski test '{0}' ({1})", t.Id, t.Title));
            Border card;
            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);

            // HWT-03: Pre-flight blokada pokretanja GPU 3D testa ako nedostaje grafički drajver
            TestItemResult blockedResult;
            if (!AutoDiagnosticsEngine.TryValidateGpuDriverForStress(out blockedResult))
            {
                string blockReason = (blockedResult != null && !string.IsNullOrEmpty(blockedResult.Reason))
                    ? blockedResult.Reason
                    : HwTestLocalization.Pick("GPU 3D test ne može da se pokrene: nedostaje grafički drajver.", "无法启动 GPU 3D 测试：缺少显卡驱动。");
                HwTestLogger.Warn("GPU_BLOCKED", blockReason);
                if (blockedResult != null)
                {
                    ApplyResult(t, blockedResult);
                }
                else
                {
                    t.Status = TestStatus.Fail;
                    t.HasWarning = true;
                    t.Reason = blockReason;
                }
                t.DurationSeconds = 0;
                sw.Stop();
                HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                    t.Id, t.Status, t.HasWarning, t.DurationSeconds, t.Reason ?? "", SimpleJson.Serialize(t.Details ?? new Dictionary<string, object>())));

                if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
                UpdateSummaryBadge();
                if (!_uefiBiosRebootRequested)
                {
                    try { SaveCheckpoint(""); } catch { }
                    ReconcilePhase5StateAndReportAfterTest();
                }
                return;
            }

            var gt = new GpuStressWindow(_autoEngine);
            Window gpuOwner = GetDialogOwner();
            if (gpuOwner != null) gt.Owner = gpuOwner;
            gt.ShowDialog();
            ApplyResult(t, gt.Result);

            sw.Stop();
            if (t.DurationSeconds <= 0) t.DurationSeconds = (int)sw.Elapsed.TotalSeconds;
            HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                t.Id, t.Status, t.HasWarning, t.DurationSeconds, t.Reason ?? "", SimpleJson.Serialize(t.Details ?? new Dictionary<string, object>())));

            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
            UpdateSummaryBadge();
            if (!_uefiBiosRebootRequested)
            {
                try { SaveCheckpoint(""); } catch { }
                ReconcilePhase5StateAndReportAfterTest();
            }
        }

        private void ApplyResult(TestItemResult target, TestItemResult source)
        {
            if (source == null) return;
            target.Status = source.Status;
            target.HasWarning = source.HasWarning;
            target.IsPassivePreliminary = source.IsPassivePreliminary;
            if (!string.IsNullOrEmpty(source.MetricSummary))
                target.MetricSummary = source.MetricSummary;
            target.Attempts = Math.Max(1, source.Attempts);
            if (source.DurationSeconds > 0)
                target.DurationSeconds = source.DurationSeconds;
            target.Reason = source.Reason;
            target.Details = source.Details;
        }

        private TestItemResult FindItemById(string id)
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                if (string.Equals(_testItems[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return _testItems[i];
            }
            return null;
        }

        private void OpenAutoDetails(TestItemResult t)
        {
            var dlg = new AutoTestDetailsWindow(t);
            Window dlgOwner = GetDialogOwner();
            if (dlgOwner != null) dlg.Owner = dlgOwner;
            dlg.ShowDialog();
            if (dlg.RerunRequested)
            {
                RunSingleTest(t);
            }
        }

        private void StartPassiveAutoChecks()
        {
            if (_passiveChecksStarted || _phase5WorkflowActive || _cancelPassiveAutoChecks) return;
            _passiveChecksStarted = true;
            Thread th = new Thread(() =>
            {
                TestItemResult[] passiveResults = new TestItemResult[7];
                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[0] = _autoEngine.RunBatteryCheck(); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                    Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[0], true)));

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[1] = _autoEngine.RunNetworkCheck(_profileClient); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                    Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[1], true)));

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[2] = _autoEngine.RunDiskPassiveCheck(); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                    Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[2], false)));

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[3] = _autoEngine.RunRamPassiveSummary(); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                    Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[3], false)));

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[4] = _autoEngine.RunCpuPassiveSummary(); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                    Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[4], false)));

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[5] = _autoEngine.RunGpuPassiveSummary(); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ApplyAutoPassiveResult(passiveResults[5], false);
                        RefreshCardVisibility();
                        UpdateSummaryBadge();
                    }));
                }

                if (_phase5WorkflowActive || _cancelPassiveAutoChecks) return;
                try { passiveResults[6] = _autoEngine.RunSecurityAndFirmwareCheck(_hwInfo); } catch { }
                if (!_phase5WorkflowActive && !_cancelPassiveAutoChecks)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ApplyAutoPassiveResult(passiveResults[6], true);
                        RefreshCardVisibility();
                        UpdateSummaryBadge();
                    }));
                }
            });
            th.IsBackground = true;
            _passiveAutoThread = th;
            th.Start();
        }

        private void ApplyAutoPassiveResult(TestItemResult source, bool isCompletePassiveTest)
        {
            if (source == null || _phase5WorkflowActive || _cancelPassiveAutoChecks) return;
            TestItemResult target = FindItemById(source.Id);
            if (target == null) return;

            // Ne prepisuj ako je serviser već ručno pokrenuo/završio test
            if (target.Status != TestStatus.Pending) return;

            DriverGateState gate = _lastDriverGateState;
            bool gateUnlocked = gate != null && gate.IsUnlocked;
            if (string.Equals(source.Id, "auto_gpu", StringComparison.OrdinalIgnoreCase) &&
                source.Status == TestStatus.Fail &&
                !gateUnlocked)
            {
                target.Status = TestStatus.Pending;
                target.HasWarning = false;
                target.IsPassivePreliminary = true;
                target.MetricSummary = HwTestLocalization.Pick(
                    "Čeka instalaciju drajvera i Fazu 5",
                    "等待驱动程序安装及阶段 5");
                target.Reason = HwTestLocalization.Pick(
                    "Preliminarni status pre instalacije grafičkih drajvera (biće ponovo provereno u Fazi 5).",
                    "显卡驱动安装前的初步状态（将在阶段 5 重新检测）。");
                if (source.Details != null) target.Details = source.Details;
            }
            else if (isCompletePassiveTest || source.Status == TestStatus.Fail || source.Status == TestStatus.NotApplicable)
            {
                ApplyResult(target, source);
                target.IsPassivePreliminary = true;
                HwTestLogger.Info("AUTO_PASSIVE", string.Format("{0} -> {1} (Warn={2}) | {3}",
                    target.Id, target.Status, target.HasWarning, target.MetricSummary ?? ""));
            }
            else
            {
                target.HasWarning = source.HasWarning;
                target.IsPassivePreliminary = true;
                target.MetricSummary = source.MetricSummary;
                target.Reason = source.Reason;
                if (source.Details != null) target.Details = source.Details;
            }

            Border card;
            if (_cardMap.TryGetValue(target.Id, out card)) UpdateCardVisual(card, target);
            RefreshCardVisibility();
            UpdateSummaryBadge();
        }

        private void ToggleAutoStressSuite()
        {
            if (_autoStressRunning)
            {
                return;
            }

            _autoStressRunning = true;
            _autoStressCancel.Reset();
            SetAutoControlsBusy(true);

            Thread th = new Thread(() =>
            {
                try
                {
                    string[] seq = new string[] { "auto_disk", "auto_ram", "auto_cpu" };
                    for (int i = 0; i < seq.Length; i++)
                    {
                        if (_autoStressCancel.IsCancelled) break;
                        string id = seq[i];
                        TestItemResult item = FindItemById(id);
                        if (item == null || item.Status == TestStatus.Skip || item.Status == TestStatus.NotApplicable)
                            continue;

                        int stepNum = i + 1;
                        Dispatcher.Invoke(new Action(() =>
                        {
                            item.Status = TestStatus.Running;
                            if (_autoSectionStatus != null)
                                _autoSectionStatus.Text = HwTestLocalization.PickFormat("🔄 Korak {0}/4: {1} u toku...", "🔄 步骤 {0}/4：{1} 进行中...", stepNum, item.Title);
                            Border c;
                            if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                            UpdateSummaryBadge();
                        }));

                        HwTestLogger.Info("TEST_START", string.Format("Pokrenut automatski test '{0}' ({1})", item.Id, item.Title));

                        TestItemResult result = ExecuteAutoWorkerById(id, (msg) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                item.MetricSummary = msg;
                                SyncCardRowTexts(item);
                            }));
                        });

                        Dispatcher.Invoke(new Action(() =>
                        {
                            ApplyResult(item, result);
                            HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                                item.Id, item.Status, item.HasWarning, item.DurationSeconds, item.Reason ?? "", SimpleJson.Serialize(item.Details ?? new Dictionary<string, object>())));
                            Border c;
                            if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                            UpdateSummaryBadge();
                            if (!_uefiBiosRebootRequested)
                            {
                                try { SaveCheckpoint(""); } catch { }
                            }
                        }));
                    }

                    // Korak 4/4: GPU 3D test na UI niti
                    if (!_autoStressCancel.IsCancelled)
                    {
                        TestItemResult gpuItem = FindItemById("auto_gpu");
                        if (gpuItem != null && gpuItem.Status != TestStatus.Skip && gpuItem.Status != TestStatus.NotApplicable)
                        {
                            Dispatcher.Invoke(new Action(() =>
                            {
                                if (_autoSectionStatus != null)
                                    _autoSectionStatus.Text = HwTestLocalization.Pick("🔄 Korak 4/4: GPU 3D hardverski stres test...", "🔄 步骤 4/4：GPU 3D 硬件压力测试...");
                                RunGpuStressTest(gpuItem);
                            }));
                        }
                    }

                    // Osveži Bateriju i Mrežu ako su u Pending
                    TestItemResult batItem = FindItemById("auto_battery");
                    if (!_autoStressCancel.IsCancelled && batItem != null && batItem.Status == TestStatus.Pending)
                    {
                        TestItemResult br = _autoEngine.RunBatteryCheck();
                        Dispatcher.Invoke(new Action(() => { ApplyResult(batItem, br); Border c; if (_cardMap.TryGetValue(batItem.Id, out c)) UpdateCardVisual(c, batItem); RefreshCardVisibility(); }));
                    }
                    TestItemResult netItem = FindItemById("auto_network");
                    if (!_autoStressCancel.IsCancelled && netItem != null && netItem.Status == TestStatus.Pending)
                    {
                        TestItemResult nr = _autoEngine.RunNetworkCheck(_profileClient);
                        Dispatcher.Invoke(new Action(() => { ApplyResult(netItem, nr); Border c; if (_cardMap.TryGetValue(netItem.Id, out c)) UpdateCardVisual(c, netItem); }));
                    }
                    TestItemResult secItem = FindItemById("auto_security");
                    if (!_autoStressCancel.IsCancelled && secItem != null && secItem.Status == TestStatus.Pending)
                    {
                        TestItemResult sr = _autoEngine.RunSecurityAndFirmwareCheck(_hwInfo);
                        Dispatcher.Invoke(new Action(() => { ApplyResult(secItem, sr); Border c; if (_cardMap.TryGetValue(secItem.Id, out c)) UpdateCardVisual(c, secItem); RefreshCardVisibility(); UpdateSummaryBadge(); }));
                    }
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _autoStressRunning = false;
                        SetAutoControlsBusy(false);
                        UpdateSummaryBadge();
                        if (!_uefiBiosRebootRequested && HasAnyCompletedTestsForCheckpoint())
                        {
                            try { SaveCheckpoint(""); } catch { }
                        }
                        if (!_uefiBiosRebootRequested)
                        {
                            ReconcilePhase5StateAndReportAfterTest();
                        }
                        if (_autoSectionStatus != null)
                        {
                            _autoSectionStatus.Text = _autoStressCancel.IsCancelled
                                ? HwTestLocalization.Pick("⏹ Automatski stres paket je zaustavljen.", "⏹ 自动压力测试包已停止。")
                                : HwTestLocalization.Pick("✓ Automatski stres paket je završen. Pregledajte rezultate i nastavite periferne testove.", "✓ 自动压力测试包已完成。请查看结果并继续外设测试。");
                        }
                    }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void RunSingleAutoTestAsync(TestItemResult item)
        {
            _autoStressRunning = true;
            _autoStressCancel.Reset();
            SetAutoControlsBusy(true);
            item.Status = TestStatus.Running;
            Border card;
            if (_cardMap.TryGetValue(item.Id, out card)) UpdateCardVisual(card, item);
            UpdateSummaryBadge();

            Thread th = new Thread(() =>
            {
                TestItemResult result = null;
                try
                {
                    result = ExecuteAutoWorkerById(item.Id, (msg) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            item.MetricSummary = msg;
                            SyncCardRowTexts(item);
                        }));
                    });
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (result != null)
                        {
                            ApplyResult(item, result);
                            HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                                item.Id, item.Status, item.HasWarning, item.DurationSeconds, item.Reason ?? "", SimpleJson.Serialize(item.Details ?? new Dictionary<string, object>())));
                        }
                        Border c;
                        if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                        _autoStressRunning = false;
                        SetAutoControlsBusy(false);
                        RefreshCardVisibility();
                        UpdateSummaryBadge();
                        if (!_uefiBiosRebootRequested)
                        {
                            try { SaveCheckpoint(""); } catch { }
                            ReconcilePhase5StateAndReportAfterTest();
                        }
                    }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void ReconcilePhase5StateAndReportAfterTest()
        {
            if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;

            TestItemResult cpuItem = FindItemById("auto_cpu");
            if (cpuItem != null && cpuItem.Status == TestStatus.Pass && !cpuItem.IsPassivePreliminary)
            {
                float pk = ExtractFloatDetail(cpuItem.Details, "peak_temp_c");
                bool critReason = !string.IsNullOrEmpty(cpuItem.Reason) &&
                    cpuItem.Reason.IndexOf("KRITIČNO PREGREVANJE", StringComparison.OrdinalIgnoreCase) >= 0;
                if (pk < 95.0f && !critReason)
                {
                    _phase5PeakCpuTemp = pk;
                    _phase5ThermalCritical = false;
                    _phase5ThermalNote = string.Empty;
                }
            }

            if (_phase5StepCallback != null)
            {
                string[] passiveIds = new string[] { "auto_battery", "auto_network", "auto_security" };
                bool autoDiagWarn = false;
                List<string> autoDiagIssues = new List<string>();
                for (int i = 0; i < passiveIds.Length; i++)
                {
                    TestItemResult pi = FindItemById(passiveIds[i]);
                    if (pi != null && (pi.HasWarning || pi.Status == TestStatus.Fail))
                    {
                        autoDiagWarn = true;
                        autoDiagIssues.Add(FormatAutoDiagIssueSummary(passiveIds[i], pi));
                    }
                }
                EmitPhase5Step("P5.AutoDiag", HwTestLocalization.Pick("Automatska provera sistema", "系统自动检测"),
                    autoDiagWarn ? "WARN" : "OK", string.Join(" | ", autoDiagIssues.ToArray()));

                string[] stressIds = new string[] { "auto_disk", "auto_ram", "auto_cpu", "auto_gpu" };
                bool stressWarn = false;
                for (int i = 0; i < stressIds.Length; i++)
                {
                    TestItemResult si = FindItemById(stressIds[i]);
                    if (si != null && (si.HasWarning || si.Status == TestStatus.Fail))
                        stressWarn = true;
                }
                if (_phase5ThermalCritical)
                {
                    EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"), "WARN",
                        HwTestLocalization.PickFormat("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", "注意：CPU 过热 ({0:F1}°C)", _phase5PeakCpuTemp));
                }
                else
                {
                    EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"),
                        stressWarn ? "WARN" : "OK", "");
                }

                int manualDone = 0;
                int manualPending = 0;
                int manualWarnOrFail = 0;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    TestItemResult ti = _testItems[i];
                    if (ti.IsAutomated || ti.Status == TestStatus.NotApplicable) continue;
                    if (ti.Status == TestStatus.Pending || ti.Status == TestStatus.Running)
                        manualPending++;
                    else
                    {
                        manualDone++;
                        if (ti.Status == TestStatus.Fail || ti.HasWarning || ti.Status == TestStatus.Skip)
                            manualWarnOrFail++;
                    }
                }
                if (manualPending == 0)
                {
                    if (_hwInfo != null && _hwInfo.IsLaptop && manualDone > 0)
                    {
                        EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"),
                            manualWarnOrFail > 0 ? "WARN" : "OK", "");
                    }
                    else
                    {
                        string skipReason = (_hwInfo != null && !_hwInfo.IsLaptop)
                            ? HwTestLocalization.Pick("Desktop računar — ručni testovi periferija nisu potrebni", "台式机 — 无需手动外设测试")
                            : HwTestLocalization.Pick("Nema primenljivih periferijskih testova", "没有适用的外设测试");
                        EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"),
                            "SKIPPED_DONE", skipReason);
                    }
                }
            }

            int pendingOrPreliminary = 0;
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.NotApplicable) continue;
                if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running || t.IsPassivePreliminary)
                {
                    pendingOrPreliminary++;
                    break;
                }
            }

            bool reportAlreadyExists = _reportSavedInSession ||
                (!string.IsNullOrEmpty(_outJsonPath) && File.Exists(_outJsonPath)) ||
                File.Exists(@"C:\setup\hwtest-result.json");

            if (pendingOrPreliminary == 0 || reportAlreadyExists)
            {
                SaveCurrentReport(false, false, "", false);
            }
        }

        public void SyncFinalReportBeforeCompletion()
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(SyncFinalReportBeforeCompletion));
                return;
            }
            if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;

            ReconcilePhase5StateAndReportAfterTest();
            if (!_reportSavedInSession && HasAnyRealCompletedTestsForCheckpoint())
            {
                SaveCurrentReport(false, false, "", false);
            }
        }

        private TestItemResult ExecuteAutoWorkerById(string id, Action<string> onProgress)
        {
            switch (id)
            {
                case "auto_disk": return _autoEngine.RunDiskBenchmark(onProgress, _autoStressCancel);
                case "auto_ram": return _autoEngine.RunRamCheck(onProgress, _autoStressCancel);
                case "auto_cpu": return _autoEngine.RunCpuStress(onProgress, _autoStressCancel);
                case "auto_battery": return _autoEngine.RunBatteryCheck();
                case "auto_network": return _autoEngine.RunNetworkCheck(_profileClient);
                case "auto_security": return _autoEngine.RunSecurityAndFirmwareCheck(_hwInfo);
                default: return null;
            }
        }

        private void SetAutoControlsBusy(bool busy)
        {
            if (_btnRunAutoSuite != null)
            {
                _btnRunAutoSuite.IsEnabled = !busy;
                _btnRunAutoSuite.Content = busy ? HwTestLocalization.Pick("⏳ Stres u toku...", "⏳ 压力测试进行中...") : HwTestLocalization.Pick("⚡ Stres paket", "⚡ 压力测试包");
                _btnRunAutoSuite.Background = busy
                    ? new SolidColorBrush(Color.FromRgb(65, 82, 108))
                    : new SolidColorBrush(Color.FromRgb(0, 122, 204));
            }
            foreach (KeyValuePair<string, Button> kv in _runBtnMap)
            {
                if (kv.Key.StartsWith("auto_", StringComparison.OrdinalIgnoreCase))
                    kv.Value.IsEnabled = !busy;
            }
            UpdateSummaryBadge();
            RefreshDriverGateUi();
        }

        private void RunSequentialTests()
        {
            if (_autoStressRunning)
            {
                MessageBox.Show(
                    HwTestLocalization.Pick("Automatski stres testovi su već u toku. Sačekajte da se završe pre sekvencijalnog pokretanja.", "自动压力测试正在运行，请等待其完成后再依次运行。"),
                    HwTestLocalization.Pick("Stres test u toku", "压力测试进行中"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_phase5WorkflowActive && _waitingForManualPeripherals)
            {
                RunSequentialPeripheralTests();
                return;
            }

            if (!HasPendingAutomatedTests())
            {
                if (!EnsureInteractiveTestsDriverGateUnlocked(true))
                {
                    return;
                }
            }

            for (int i = 0; i < _testItems.Count; i++)
            {
                if (_uefiBiosRebootRequested) break;
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.Pending)
                {
                    if (t.IsAutomated)
                    {
                        if (t.Id == "auto_gpu")
                        {
                            RunGpuStressTest(t);
                        }
                        else
                        {
                            RunSingleAutoTestBlockingForSequential(t);
                        }
                    }
                    else
                    {
                        if (!EnsureInteractiveTestsDriverGateUnlocked(true))
                        {
                            break;
                        }
                        RunSingleTest(t);
                    }
                }
            }
        }

        private void RunSingleAutoTestBlockingForSequential(TestItemResult item)
        {
            _autoStressRunning = true;
            _autoStressCancel.Reset();
            SetAutoControlsBusy(true);
            item.Status = TestStatus.Running;
            Border card;
            if (_cardMap.TryGetValue(item.Id, out card)) UpdateCardVisual(card, item);
            UpdateSummaryBadge();

            DispatcherFrame frame = new DispatcherFrame();
            TestItemResult result = null;
            Thread th = new Thread(() =>
            {
                try
                {
                    result = ExecuteAutoWorkerById(item.Id, (msg) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            item.MetricSummary = msg;
                            SyncCardRowTexts(item);
                        }));
                    });
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            th.IsBackground = true;
            th.Start();
            Dispatcher.PushFrame(frame);

            if (result != null)
            {
                ApplyResult(item, result);
                HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                    item.Id, item.Status, item.HasWarning, item.DurationSeconds, item.Reason ?? "", SimpleJson.Serialize(item.Details ?? new Dictionary<string, object>())));
            }
            if (_cardMap.TryGetValue(item.Id, out card)) UpdateCardVisual(card, item);
            _autoStressRunning = false;
            SetAutoControlsBusy(false);
            RefreshCardVisibility();
            UpdateSummaryBadge();
            if (!_uefiBiosRebootRequested)
            {
                try { SaveCheckpoint(""); } catch { }
            }
        }

        private void UpdateSummaryBadge()
        {
            int pass = 0, warn = 0, fail = 0, skip = 0, na = 0, pending = 0, running = 0;
            int manualTotal = 0, manualDone = 0;
            foreach (TestItemResult t in _testItems)
            {
                if (!t.IsAutomated && t.Status != TestStatus.NotApplicable)
                {
                    manualTotal++;
                    if (t.Status != TestStatus.Pending && t.Status != TestStatus.Running)
                    {
                        manualDone++;
                    }
                }

                if (t.Status == TestStatus.Pass)
                {
                    pass++;
                    if (t.HasWarning) warn++;
                }
                else if (t.Status == TestStatus.Fail) fail++;
                else if (t.Status == TestStatus.Skip) skip++;
                else if (t.Status == TestStatus.NotApplicable) na++;
                else if (t.Status == TestStatus.Running) running++;
                else pending++;
            }

            int applicable = pass + fail + skip + pending + running;
            int completed = pass + fail + skip;
            double pct = applicable > 0 ? Math.Round((completed * 100.0) / applicable, 1) : 100.0;

            if (_overallProgressBar != null)
            {
                _overallProgressBar.Value = pct;
                if (fail > 0)
                    _overallProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                else if ((pending + running) == 0)
                    _overallProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(46, 204, 113));
                else
                    _overallProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            }

            if (_manualSectionCountText != null)
            {
                _manualSectionCountText.Text = HwTestLocalization.PickFormat("{0}/{1} završeno", "{0}/{1} 已完成", manualDone, manualTotal);
            }

            Brush badgeBg;
            if (fail > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                _summaryBadge.Text = HwTestLocalization.PickFormat("✗ {0} FAIL | {1} PASS | {2} SKIP", "✗ {0} FAIL | {1} PASS | {2} SKIP", fail, pass, skip);
            }
            else if (running > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                _summaryBadge.Text = HwTestLocalization.PickFormat("▶ {0} U TOKU | {1}/{2} završeno", "▶ {0} 进行中 | {1}/{2} 已完成", running, completed, applicable);
            }
            else if (pending > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(217, 150, 15));
                _summaryBadge.Text = (na > 0)
                    ? HwTestLocalization.PickFormat("⏳ {0}/{1} ({2} odsutno)", "⏳ {0}/{1}（{2} 项不存在）", completed, applicable, na)
                    : HwTestLocalization.PickFormat("⏳ {0}/{1} završeno", "⏳ {0}/{1} 已完成", completed, applicable);
            }
            else
            {
                badgeBg = warn > 0
                    ? new SolidColorBrush(Color.FromRgb(217, 130, 15))
                    : new SolidColorBrush(Color.FromRgb(35, 134, 54));
                _summaryBadge.Text = warn > 0
                    ? HwTestLocalization.PickFormat("! ZAVRŠENO ({0} PASS, {1} UPOZ., {2} SKIP)", "! 已完成（{0} PASS，{1} 警告，{2} SKIP）", pass, warn, skip)
                    : HwTestLocalization.PickFormat("✓ ZAVRŠENO ({0} PASS, {1} SKIP)", "✓ 已完成（{0} PASS，{1} SKIP）", pass, skip);
            }

            if (_summaryBadgeBorder != null)
            {
                _summaryBadgeBorder.Background = badgeBg;
            }

            if (_btnFinish != null)
            {
                int remaining = pending + running;
                bool canFinish = !_autoStressRunning && (pending == 0) && (running == 0);
                _btnFinish.IsEnabled = canFinish;
                if (canFinish)
                {
                    _btnFinish.Opacity = 1.0;
                    _btnFinish.ToolTip = HwTestLocalization.Pick("Završi testiranje i sačuvaj konačan hardverski izveštaj", "完成测试并保存最终硬件报告");
                }
                else if (_autoStressRunning)
                {
                    _btnFinish.Opacity = 0.55;
                    _btnFinish.ToolTip = HwTestLocalization.Pick("Završetak je zaključan dok traje automatski stres paket.", "自动压力测试包运行期间无法完成。");
                }
                else
                {
                    _btnFinish.Opacity = 0.55;
                    _btnFinish.ToolTip = HwTestLocalization.PickFormat("Završetak je zaključan dok svi primenljivi testovi ne dobiju PASS ili FAIL (preostalo: {0}).", "在所有适用测试获得 PASS 或 FAIL 之前无法完成（剩余：{0}）。",
                        remaining);
                }
            }
        }

        private static float ExtractFloatDetail(Dictionary<string, object> details, string key)
        {
            if (details == null || string.IsNullOrEmpty(key) || !details.ContainsKey(key)) return 0f;
            object val = details[key];
            if (val == null) return 0f;
            float parsed;
            if (float.TryParse(Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return 0f;
        }

        private void EmitPhase5Step(string stepId, string title, string state, string reason)
        {
            Action<string, string, string, string> cb = _phase5StepCallback;
            if (cb != null)
            {
                try { cb(stepId, title, state, reason); } catch { }
            }
        }

        private void NotifyPhase5Finished(HwTestRunResult runResult, bool cancelled)
        {
            Action<HwTestRunResult, int, float, float, bool, bool> cb = null;
            int durSec;
            float peakTemp;
            float maxThrot;
            bool thermalCrit;

            lock (_phase5Lock)
            {
                if (_phase5CompletedNotified) return;
                _phase5CompletedNotified = true;
                _phase5WorkflowActive = false;
                _waitingForManualPeripherals = false;
                cb = _phase5FinishedCallback;
                durSec = _phase5CpuDurationSec;
                peakTemp = _phase5PeakCpuTemp;
                maxThrot = _phase5MaxCpuThrottle;
                thermalCrit = _phase5ThermalCritical;
            }

            if (cb != null)
            {
                try { cb(runResult, durSec, peakTemp, maxThrot, thermalCrit, cancelled); } catch { }
            }
        }

        private static bool IsFinalTestStatus(TestStatus status)
        {
            return status == TestStatus.Pass ||
                   status == TestStatus.Fail ||
                   status == TestStatus.Skip ||
                   status == TestStatus.NotApplicable;
        }

        public void RunSequentialPeripheralTests()
        {
            if (_autoStressRunning) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(RunSequentialPeripheralTests));
                return;
            }

            if (!EnsureInteractiveTestsDriverGateUnlocked(true))
            {
                return;
            }

            _inSequentialPeripheralRun = true;
            try
            {
                for (int i = 0; i < _testItems.Count; i++)
                {
                    if (_uefiBiosRebootRequested || _uefiBiosPromptOpen || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                    TestItemResult t = _testItems[i];
                    if (!t.IsAutomated && t.Status == TestStatus.Pending)
                    {
                        if (!EnsureInteractiveTestsDriverGateUnlocked(true))
                        {
                            break;
                        }
                        RunSingleTest(t);
                        if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) break;
                        if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                        {
                            // Korisnik je prekinuo ili zatvorio pojedinacni test pre PASS/FAIL ishoda
                            break;
                        }
                    }
                }
            }
            finally
            {
                _inSequentialPeripheralRun = false;
            }

            CheckAndHandlePhase5InteractiveCompletion();
        }

        private void CheckAndHandlePhase5InteractiveCompletion()
        {
            if (!_isEmbedded || !_phase5WorkflowActive || !_waitingForManualPeripherals || _autoStressRunning) return;
            if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;

            bool alreadyNotified;
            lock (_phase5Lock) { alreadyNotified = _phase5CompletedNotified; }
            if (alreadyNotified) return;

            int remainingInteractive = 0;
            int remainingAny = 0;
            int periphWarnOrFail = 0;
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                {
                    remainingAny++;
                    if (!t.IsAutomated) remainingInteractive++;
                }
                if (!t.IsAutomated && (t.Status == TestStatus.Fail || t.HasWarning || t.Status == TestStatus.Skip))
                {
                    periphWarnOrFail++;
                }
            }

            if (remainingInteractive == 0 && remainingAny == 0)
            {
                EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), periphWarnOrFail > 0 ? "WARN" : "OK", "");
                SaveCurrentReport(false, false, "", false);
            }
            else if (remainingInteractive > 0)
            {
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = HwTestLocalization.PickFormat("⏳ Preostalo {0} obaveznih interaktivnih testova. Dovršite preostale testove da biste otključali završetak i čuvanje izveštaja.", "⏳ 剩余 {0} 项必选交互式测试。请完成剩余测试以解锁完成并保存报告。",
                        remainingInteractive);
                }
                RaisePendingInteractiveTestsRequired();
            }
        }

        public void ForceCompletePhase5SkipPending()
        {
            ForceCompletePhase5SkipPending(HwTestLocalization.Pick("Preskočeno u okviru Faze 5", "在阶段 5 中跳过"));
        }

        public void ForceCompletePhase5SkipPending(string customReason)
        {
            if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
            _autoStressCancel.Cancel();
            _waitingForManualPeripherals = false;
            bool shouldFinish = false;
            lock (_phase5Lock)
            {
                shouldFinish = !_phase5CompletedNotified;
            }
            if (!shouldFinish) return;

            string effectiveReason = (customReason != null && customReason.Trim().Length > 0)
                ? customReason.Trim()
                : HwTestLocalization.Pick("Preskočeno u okviru Faze 5", "在阶段 5 中跳过");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
                Window activeDlg = _activePeripheralDialog;
                if (activeDlg != null)
                {
                    try { activeDlg.Close(); } catch { }
                }
                SaveCurrentReport(false, true, effectiveReason, true);
            }));
        }

        private static string FormatAutoDiagIssueSummary(string pid, TestItemResult item)
        {
            string label = pid == "auto_battery"
                ? HwTestLocalization.Pick("Baterija", "电池")
                : (pid == "auto_network" ? HwTestLocalization.Pick("Mreža", "网络") : (pid == "auto_security" ? HwTestLocalization.Pick("Sigurnost", "安全") : (item != null ? item.Title : pid)));
            string rawReason = (item != null ? (item.Reason ?? "") : "").Trim();
            if (rawReason.StartsWith("UPOZORENJE:", StringComparison.OrdinalIgnoreCase))
                rawReason = rawReason.Substring("UPOZORENJE:".Length).Trim();
            else if (rawReason.StartsWith("KRITIČNO:", StringComparison.OrdinalIgnoreCase))
                rawReason = rawReason.Substring("KRITIČNO:".Length).Trim();
            if (string.IsNullOrEmpty(rawReason) && item != null)
                rawReason = (item.MetricSummary ?? "").Trim();
            if (string.IsNullOrEmpty(rawReason))
                rawReason = (item != null && item.Status == TestStatus.Fail) ? "FAIL" : HwTestLocalization.Pick("Upozorenje", "警告");
            return string.Format("{0}: {1}", label, rawReason);
        }

        public bool IsAllApplicableTestsCompleted()
        {
            int applicable = 0;
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.NotApplicable) continue;
                applicable++;
                if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                {
                    return false;
                }
            }
            return applicable > 0;
        }

        public void StartPhase5FullWorkflow(
            Action<string, string, string, string> onStepProgress,
            Action<HwTestRunResult, int, float, float, bool, bool> onPhase5Finished)
        {
            if (!_checkpointRestored)
            {
                try { TryRestorePersistedTestState(); } catch { }
            }

            // V344-01: Osvezi hardversku probu nakon instalacije drajvera u Fazi 2 (nikada ne gazi vec zavrsene ili restaurirane testove)
            DriverGateState preGate = EvaluateEffectiveDriverGateState();
            if (preGate != null && preGate.IsUnlocked && !_hardwareProbeRefreshedAfterDrivers)
            {
                try { RefreshHardwareProbeAfterDrivers(); } catch { }
            }

            TestItemResult existingCpu = FindItemById("auto_cpu");
            bool preserveRestoredThermalMetrics = existingCpu != null && IsFinalTestStatus(existingCpu.Status) &&
                                                  (_checkpointRestored || _restoredTestIds.Contains("auto_cpu") || _phase5CpuDurationSec > 0);

            _cancelPassiveAutoChecks = true;

            lock (_phase5Lock)
            {
                _phase5WorkflowActive = true;
                _phase5CompletedNotified = false;
                _phase5StepCallback = onStepProgress;
                _phase5FinishedCallback = onPhase5Finished;
                if (!preserveRestoredThermalMetrics)
                {
                    _phase5CpuDurationSec = 0;
                    _phase5PeakCpuTemp = 0f;
                    _phase5MaxCpuThrottle = 0f;
                    _phase5ThermalCritical = false;
                }
            }

            _autoStressCancel.Reset();
            if (!preserveRestoredThermalMetrics)
            {
                _phase5ThermalCritical = false;
                _phase5ThermalNote = "";
            }
            _autoTestsFinishedUtc = null;
            _firstOperatorInteractionUtc = null;

            // V344-03: Ako su svi primenljivi automatski i interaktivni testovi vec zavrseni pre restarta (0 Pending / Running),
            // odmah popuni P5.AutoDiag, P5.Stress, P5.Peripherals iz restauriranih rezultata i zavrsi Fazu 5 bez ponovnog testiranja.
            int pendingOrRunningCount = 0;
            int preliminaryCount = 0;
            int autoFailCount = 0;
            int totalApplicableCount = 0;
            int manualApplicableDoneCount = 0;
            int manualWarnOrFailEarly = 0;
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.NotApplicable) continue;
                totalApplicableCount++;
                if (t.IsPassivePreliminary)
                {
                    preliminaryCount++;
                }
                if (t.IsAutomated && t.Status == TestStatus.Fail)
                {
                    autoFailCount++;
                }
                if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                {
                    pendingOrRunningCount++;
                }
                else if (!t.IsAutomated)
                {
                    manualApplicableDoneCount++;
                    if (t.Status == TestStatus.Fail || t.HasWarning || t.Status == TestStatus.Skip)
                        manualWarnOrFailEarly++;
                }
            }

            if (pendingOrRunningCount == 0 && preliminaryCount == 0 && autoFailCount == 0 && totalApplicableCount > 0)
            {
                string[] passiveIdsEarly = new string[] { "auto_battery", "auto_network", "auto_security" };
                bool autoDiagWarnEarly = false;
                List<string> autoDiagIssuesEarly = new List<string>();
                for (int i = 0; i < passiveIdsEarly.Length; i++)
                {
                    TestItemResult pi = FindItemById(passiveIdsEarly[i]);
                    if (pi != null && (pi.HasWarning || pi.Status == TestStatus.Fail))
                    {
                        autoDiagWarnEarly = true;
                        autoDiagIssuesEarly.Add(FormatAutoDiagIssueSummary(passiveIdsEarly[i], pi));
                    }
                }
                EmitPhase5Step("P5.AutoDiag", HwTestLocalization.Pick("Automatska provera sistema", "系统自动检测"),
                    autoDiagWarnEarly ? "WARN" : "OK", string.Join(" | ", autoDiagIssuesEarly.ToArray()));

                string[] stressIdsEarly = new string[] { "auto_disk", "auto_ram", "auto_cpu", "auto_gpu" };
                bool stressWarnEarly = false;
                for (int i = 0; i < stressIdsEarly.Length; i++)
                {
                    TestItemResult si = FindItemById(stressIdsEarly[i]);
                    if (si != null && (si.HasWarning || si.Status == TestStatus.Fail))
                        stressWarnEarly = true;
                }
                if (_phase5ThermalCritical)
                {
                    EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"), "WARN",
                        HwTestLocalization.PickFormat("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", "注意：CPU 过热 ({0:F1}°C)", _phase5PeakCpuTemp));
                }
                else
                {
                    EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"),
                        stressWarnEarly ? "WARN" : "OK", "");
                }

                if (_hwInfo != null && _hwInfo.IsLaptop && manualApplicableDoneCount > 0)
                {
                    EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"),
                        manualWarnOrFailEarly > 0 ? "WARN" : "OK", "");
                }
                else
                {
                    string skipPeriphReasonEarly = (_hwInfo != null && !_hwInfo.IsLaptop)
                        ? HwTestLocalization.Pick("Desktop računar — ručni testovi periferija nisu potrebni", "台式机 — 无需手动外设测试")
                        : HwTestLocalization.Pick("Nema primenljivih periferijskih testova", "没有适用的外设测试");
                    EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"),
                        "SKIPPED_DONE", skipPeriphReasonEarly);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_autoSectionStatus != null)
                    {
                        _autoSectionStatus.Text = HwTestLocalization.PickFormat(
                            "♻️ Svi hardverski testovi ({0}/{0}) su već završeni pre restarta — preskačem ponovno testiranje.",
                            "♻️ 所有硬件测试 ({0}/{0}) 在重启前已完成 — 跳过重复测试。",
                            totalApplicableCount);
                        _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
                    }
                    UpdateSummaryBadge();
                    SaveCurrentReport(false, false, "", false);
                }));
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = _checkpointRestored
                        ? HwTestLocalization.PickFormat("♻️ Nastavak posle UEFI BIOS restarta — restaurirano {0}/{1} već završenih testova. Nastavljam Fazu 5...", "♻️ UEFI BIOS 重启后继续 — 已恢复 {0}/{1} 项已完成的测试。正在继续阶段 5...", _checkpointRestoredCount, _checkpointTotalApplicable)
                        : HwTestLocalization.Pick("🔄 Faza 5 u toku: pokrećem automatsku dijagnostiku i stres test...", "🔄 阶段 5 进行中：正在启动自动诊断和压力测试...");
                }
            }));

            if (_autoStressRunning)
            {
                _autoStressCancel.Cancel();
                Thread.Sleep(500);
                _autoStressCancel.Reset();
            }

            _autoStressRunning = true;
            Dispatcher.BeginInvoke(new Action(() => SetAutoControlsBusy(true)));

            Thread th = new Thread(() =>
            {
                bool autoDiagWarn = false;
                bool stressWarn = false;
                try
                {
                    // Otkloni race condition sa inicijalnim StartPassiveAutoChecks() thread-om iz Faze 2
                    Thread passiveTh = _passiveAutoThread;
                    if (passiveTh != null && passiveTh.IsAlive)
                    {
                        try { passiveTh.Join(2500); } catch { }
                    }

                    // 1. Automatska provera (baterija, mreža, sigurnost)
                    EmitPhase5Step("P5.AutoDiag", HwTestLocalization.Pick("Automatska provera sistema", "系统自动检测"), "RUNNING", "");
                    string[] passiveIds = new string[] { "auto_battery", "auto_network", "auto_security" };
                    List<string> autoDiagIssues = new List<string>();
                    for (int i = 0; i < passiveIds.Length; i++)
                    {
                        if (_uefiBiosRebootRequested || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                        string pid = passiveIds[i];
                        TestItemResult item = FindItemById(pid);
                        if (item == null) continue;

                        bool restoredFromCheckpoint = item.Status == TestStatus.Pass &&
                                                      !item.HasWarning &&
                                                      !item.IsPassivePreliminary;
                        if (!restoredFromCheckpoint)
                        {
                            Dispatcher.Invoke(new Action(() =>
                            {
                                item.Status = TestStatus.Running;
                                if (_autoSectionStatus != null)
                                    _autoSectionStatus.Text = HwTestLocalization.PickFormat("🔄 Faza 5 (Dijagnostika {0}/{1}): {2}...", "🔄 阶段 5（诊断 {0}/{1}）：{2}...", i + 1, passiveIds.Length, item.Title);
                                Border c;
                                if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                                UpdateSummaryBadge();
                            }));

                            TestItemResult r = ExecuteAutoWorkerById(pid, null);
                            if (r != null)
                            {
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    ApplyResult(item, r);
                                    Border c;
                                    if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                                    RefreshCardVisibility();
                                    UpdateSummaryBadge();
                                    if (!_uefiBiosRebootRequested)
                                    {
                                        try { SaveCheckpoint(""); } catch { }
                                    }
                                }));
                            }
                        }

                        if (item.HasWarning || item.Status == TestStatus.Fail)
                        {
                            autoDiagWarn = true;
                            autoDiagIssues.Add(FormatAutoDiagIssueSummary(pid, item));
                        }
                    }
                    if (!_uefiBiosRebootRequested)
                    {
                        string autoDiagReason = string.Join(" | ", autoDiagIssues.ToArray());
                        EmitPhase5Step("P5.AutoDiag", HwTestLocalization.Pick("Automatska provera sistema", "系统自动检测"),
                            _autoStressCancel.IsCancelled ? "SKIPPED_USER" : (autoDiagWarn ? "WARN" : "OK"),
                            autoDiagReason);
                    }

                    // 2. Automatski stres paket (Disk, RAM, CPU 30s, GPU 3D)
                    if (!_uefiBiosRebootRequested && !_autoStressCancel.IsCancelled && _phase5WorkflowActive)
                    {
                        bool allStressAlreadyDone = true;
                        string[] allStressIds = new string[] { "auto_disk", "auto_ram", "auto_cpu", "auto_gpu" };
                        for (int i = 0; i < allStressIds.Length; i++)
                        {
                            TestItemResult si = FindItemById(allStressIds[i]);
                            if (si == null) continue;
                            bool canSkipStress = (si.Status == TestStatus.Pass && !si.HasWarning && !si.IsPassivePreliminary) ||
                                                 si.Status == TestStatus.Skip ||
                                                 si.Status == TestStatus.NotApplicable;
                            if (!canSkipStress)
                            {
                                allStressAlreadyDone = false;
                                break;
                            }
                        }

                        if (allStressAlreadyDone)
                        {
                            for (int i = 0; i < allStressIds.Length; i++)
                            {
                                TestItemResult si = FindItemById(allStressIds[i]);
                                if (si != null && (si.HasWarning || si.Status == TestStatus.Fail))
                                    stressWarn = true;
                            }
                            if (_phase5ThermalCritical)
                            {
                                EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"), "WARN",
                                    HwTestLocalization.PickFormat("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", "注意：CPU 过热 ({0:F1}°C)", _phase5PeakCpuTemp));
                            }
                            else
                            {
                                EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"),
                                    stressWarn ? "WARN" : "OK", "");
                            }
                        }
                        else
                        {
                            EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"), "RUNNING", "");
                            string[] seq = new string[] { "auto_disk", "auto_ram", "auto_cpu" };
                            for (int i = 0; i < seq.Length; i++)
                            {
                                if (_uefiBiosRebootRequested || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                                string id = seq[i];
                                TestItemResult item = FindItemById(id);
                                bool canSkipAutoStressItem = item == null ||
                                    (item.Status == TestStatus.Pass && !item.HasWarning && !item.IsPassivePreliminary) ||
                                    item.Status == TestStatus.Skip ||
                                    item.Status == TestStatus.NotApplicable;
                                if (canSkipAutoStressItem)
                                {
                                    if (item != null && (item.HasWarning || item.Status == TestStatus.Fail))
                                        stressWarn = true;
                                    continue;
                                }

                                int stepNum = i + 1;
                                Dispatcher.Invoke(new Action(() =>
                                {
                                    item.Status = TestStatus.Running;
                                    if (_autoSectionStatus != null)
                                        _autoSectionStatus.Text = HwTestLocalization.PickFormat("🔄 Faza 5 ({0}/4): {1} u toku...", "🔄 阶段 5 ({0}/4)：{1} 进行中...", stepNum, item.Title);
                                    Border c;
                                    if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                                    UpdateSummaryBadge();
                                }));

                                HwTestLogger.Info("TEST_START", string.Format("Pokrenut automatski test '{0}' ({1})", item.Id, item.Title));

                                TestItemResult result = ExecuteAutoWorkerById(id, (msg) =>
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        item.MetricSummary = msg;
                                        SyncCardRowTexts(item);
                                    }));
                                });

                                if (result != null)
                                {
                                    if (result.HasWarning || result.Status == TestStatus.Fail) stressWarn = true;
                                    if (id == "auto_cpu")
                                    {
                                        _phase5CpuDurationSec = result.DurationSeconds;
                                        _phase5PeakCpuTemp = ExtractFloatDetail(result.Details, "peak_temp_c");
                                        _phase5MaxCpuThrottle = ExtractFloatDetail(result.Details, "max_throttle_pct");
                                        bool reasonCrit = !string.IsNullOrEmpty(result.Reason) &&
                                            result.Reason.IndexOf("KRITIČNO PREGREVANJE", StringComparison.OrdinalIgnoreCase) >= 0;
                                        if ((_phase5PeakCpuTemp >= 95.0f) || (result.Status == TestStatus.Fail && reasonCrit))
                                        {
                                            _phase5ThermalCritical = true;
                                        }
                                    }
                                }

                                Dispatcher.Invoke(new Action(() =>
                                {
                                    ApplyResult(item, result);
                                    HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Warn={2} | Trajanje={3}s | Reason='{4}' | Details={5}",
                                        item.Id, item.Status, item.HasWarning, item.DurationSeconds, item.Reason ?? "", SimpleJson.Serialize(item.Details ?? new Dictionary<string, object>())));
                                    Border c;
                                    if (_cardMap.TryGetValue(item.Id, out c)) UpdateCardVisual(c, item);
                                    UpdateSummaryBadge();
                                    if (!_uefiBiosRebootRequested)
                                    {
                                        try { SaveCheckpoint(""); } catch { }
                                    }
                                }));

                                if (_phase5ThermalCritical) break;
                            }

                            if (!_uefiBiosRebootRequested && _phase5ThermalCritical)
                            {
                                _phase5ThermalNote = HwTestLocalization.PickFormat("NAPOMENA: Procesor je dostigao kritičnu temperaturu ({0:F1}°C >= 95°C) tokom stres testa — potrebno čišćenje hladnjaka i zamena termalne paste.", "注意：处理器在压力测试期间达到临界温度 ({0:F1}°C >= 95°C) — 需要清理散热器并更换导热硅脂。",
                                    _phase5PeakCpuTemp);
                                HwTestLogger.Warn("THERMAL_NOTE", _phase5ThermalNote);
                                EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"), "WARN",
                                    HwTestLocalization.PickFormat("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", "注意：CPU 过热 ({0:F1}°C)", _phase5PeakCpuTemp));

                                TestItemResult gpuSkipItem = FindItemById("auto_gpu");
                                if (gpuSkipItem != null && gpuSkipItem.Status != TestStatus.Skip && gpuSkipItem.Status != TestStatus.NotApplicable)
                                {
                                    Dispatcher.Invoke(new Action(() =>
                                    {
                                        gpuSkipItem.Status = TestStatus.Skip;
                                        gpuSkipItem.HasWarning = true;
                                        gpuSkipItem.Reason = HwTestLocalization.PickFormat("Preskočeno radi zaštite hardvera jer je CPU dostigao {0:F1}°C (>= 95°C)", "为保护硬件已跳过，因为 CPU 温度达到 {0:F1}°C (>= 95°C)", _phase5PeakCpuTemp);
                                        gpuSkipItem.MetricSummary = HwTestLocalization.Pick("Preskočeno (CPU >= 95°C)", "已跳过 (CPU >= 95°C)");
                                        Border gc;
                                        if (_cardMap.TryGetValue(gpuSkipItem.Id, out gc)) UpdateCardVisual(gc, gpuSkipItem);
                                        UpdateSummaryBadge();
                                        if (!_uefiBiosRebootRequested)
                                        {
                                            try { SaveCheckpoint(""); } catch { }
                                        }
                                    }));
                                }

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (_autoSectionStatus != null)
                                        _autoSectionStatus.Text = HwTestLocalization.PickFormat("⚠️ NAPOMENA: CPU se pregrevao ({0:F1}°C >= 95°C) — opterećenje zaustavljeno radi zaštite, nastavljam ostale testove...", "⚠️ 注意：CPU 温度过高 ({0:F1}°C >= 95°C) — 为保护硬件已停止负载，继续其他测试...", _phase5PeakCpuTemp);
                                }));
                            }

                            if (!_uefiBiosRebootRequested && !_autoStressCancel.IsCancelled && _phase5WorkflowActive && !_phase5ThermalCritical)
                            {
                                TestItemResult gpuItem = FindItemById("auto_gpu");
                                if (gpuItem != null)
                                {
                                    bool gpuDoneFromCheckpoint = gpuItem.Status == TestStatus.Pass &&
                                                                 !gpuItem.HasWarning &&
                                                                 !gpuItem.IsPassivePreliminary;
                                    if (gpuDoneFromCheckpoint)
                                    {
                                        if (gpuItem.HasWarning || gpuItem.Status == TestStatus.Fail) stressWarn = true;
                                    }
                                    else if (gpuItem.Status != TestStatus.Skip && gpuItem.Status != TestStatus.NotApplicable)
                                    {
                                        Dispatcher.Invoke(new Action(() =>
                                        {
                                            if (_autoSectionStatus != null)
                                                _autoSectionStatus.Text = HwTestLocalization.Pick("🔄 Faza 5 (4/4): GPU 3D hardverski stres test...", "🔄 阶段 5 (4/4)：GPU 3D 硬件压力测试...");
                                            RunGpuStressTest(gpuItem);
                                            if (gpuItem.HasWarning || gpuItem.Status == TestStatus.Fail) stressWarn = true;
                                        }));
                                    }
                                }
                            }

                            if (!_uefiBiosRebootRequested && !_phase5ThermalCritical)
                            {
                                EmitPhase5Step("P5.Stress", HwTestLocalization.Pick("Stres test (Disk, RAM, CPU 30s, GPU 3D)", "压力测试（磁盘、内存、CPU 30s、GPU 3D）"),
                                    _autoStressCancel.IsCancelled ? "SKIPPED_USER" : (stressWarn ? "WARN" : "OK"), "");
                            }
                        }
                    }
                }
                finally
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        _autoStressRunning = false;
                        SetAutoControlsBusy(false);
                        UpdateSummaryBadge();
                    }));
                }

                if (_uefiBiosRebootRequested || _uefiBiosPromptOpen)
                {
                    return;
                }

                if (_autoStressCancel.IsCancelled || !_phase5WorkflowActive)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
                        SaveCurrentReport(false, true, HwTestLocalization.Pick("Preskočeno od strane servisera", "技术员已跳过"), true);
                    }));
                    return;
                }

                // V344-01: Pre prelaska na P5.Peripherals proveri gate i osvezi probu ako su drajveri spremni
                DriverGateState gateBeforePeripherals = EvaluateEffectiveDriverGateState();
                if (gateBeforePeripherals != null && gateBeforePeripherals.IsUnlocked && !_hardwareProbeRefreshedAfterDrivers)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        RefreshHardwareProbeAfterDrivers();
                    }));
                }

                // 3. Interaktivni periferijski testovi
                bool hasPendingManual = false;
                int manualCompletedCount = 0;
                int manualWarnOrFail = 0;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    TestItemResult t = _testItems[i];
                    if (!t.IsAutomated)
                    {
                        if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                        {
                            hasPendingManual = true;
                        }
                        else if (t.Status != TestStatus.NotApplicable)
                        {
                            manualCompletedCount++;
                            if (t.Status == TestStatus.Fail || t.HasWarning || t.Status == TestStatus.Skip)
                                manualWarnOrFail++;
                        }
                    }
                }

                if (!hasPendingManual)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
                        bool alreadyNotified;
                        lock (_phase5Lock) { alreadyNotified = _phase5CompletedNotified; }
                        if (alreadyNotified) return;

                        if (_hwInfo != null && _hwInfo.IsLaptop && manualCompletedCount > 0)
                        {
                            EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), manualWarnOrFail > 0 ? "WARN" : "OK", "");
                            if (_autoSectionStatus != null)
                            {
                                _autoSectionStatus.Text = HwTestLocalization.Pick("✓ Svi interaktivni testovi su završeni — automatski završavam Fazu 5 i snimam izveštaj.", "✓ 所有交互式测试已完成 — 正在自动结束阶段 5 并保存报告。");
                            }
                        }
                        else
                        {
                            string skipPeriphReason = (_hwInfo != null && !_hwInfo.IsLaptop)
                                ? HwTestLocalization.Pick("Desktop računar — ručni testovi periferija nisu potrebni", "台式机 — 无需手动外设测试")
                                : HwTestLocalization.Pick("Nema primenljivih periferijskih testova", "没有适用的外设测试");
                            EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), "SKIPPED_DONE", skipPeriphReason);
                            if (_autoSectionStatus != null)
                            {
                                _autoSectionStatus.Text = HwTestLocalization.PickFormat(
                                    "✓ {0} — automatski završavam Fazu 5 i snimam izveštaj.",
                                    "✓ {0} — 正在自动结束阶段 5 并保存报告。",
                                    skipPeriphReason);
                            }
                        }
                        UpdateSummaryBadge();
                        SaveCurrentReport(false, false, "", false);
                    }));
                    return;
                }

                _waitingForManualPeripherals = true;
                _autoTestsFinishedUtc = DateTime.UtcNow;

                // V344-01: Ako drajveri jos nisu spremni, NE oglasavaj interaktivne testove spremnim
                if (gateBeforePeripherals != null && !gateBeforePeripherals.IsUnlocked)
                {
                    string gateBlockReason = !string.IsNullOrEmpty(gateBeforePeripherals.ReasonTitle)
                        ? gateBeforePeripherals.ReasonTitle
                        : HwTestLocalization.Pick("Čeka se završetak instalacije drajvera", "正在等待驱动程序安装完成");
                    EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), "WARN", gateBlockReason);
                    Dispatcher.Invoke(new Action(() =>
                    {
                        RefreshDriverGateUi();
                        if (_autoSectionStatus != null)
                        {
                            _autoSectionStatus.Text = HwTestLocalization.PickFormat(
                                "🔒 Interaktivni testovi su zaključani dok svi drajveri ne budu instalirani: {0}",
                                "🔒 所有驱动程序安装完成前，交互式测试已锁定：{0}",
                                gateBlockReason);
                        }
                    }));
                    return;
                }

                EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), "RUNNING", "");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Console.Beep(880, 140);
                        Thread.Sleep(60);
                        Console.Beep(1175, 180);
                    }
                    catch { }
                });
                Dispatcher.Invoke(new Action(() =>
                {
                    if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;

                    bool alreadyNotified;
                    lock (_phase5Lock) { alreadyNotified = _phase5CompletedNotified; }
                    if (alreadyNotified) return;

                    RefreshDriverGateUi();
                    List<string> pendingNames = GetPendingInteractiveTestNames();
                    if (pendingNames.Count == 0)
                    {
                        EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"), manualWarnOrFail > 0 ? "WARN" : "OK", "");
                        SaveCurrentReport(false, false, "", false);
                        return;
                    }

                    if (_autoSectionStatus != null)
                    {
                        _autoSectionStatus.Text = HwTestLocalization.PickFormat("⚠️ Automatski proces je završen — preostalo {0} obaveznih interaktivnih testova ({1}).", "⚠️ 自动流程已完成 — 剩余 {0} 项必选交互式测试（{1}）。",
                            pendingNames.Count,
                            string.Join(", ", pendingNames.ToArray()));
                    }
                    RaisePendingInteractiveTestsRequired();
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        private void FinishAndSave()
        {
            MarkOperatorInteraction();
            if (_autoStressRunning)
            {
                MessageBox.Show(
                    HwTestLocalization.Pick(
                        "Automatski stres testovi su još uvek u toku.\n\nSačekajte da se stres test završi do kraja pre čuvanja izveštaja.",
                        "自动压力测试仍在进行中。\n\n请等待压力测试全部完成后再保存报告。"),
                    HwTestLocalization.Pick("Stres test u toku", "压力测试进行中"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            List<string> unfinished = new List<string>();
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                {
                    unfinished.Add("• " + t.Title);
                }
            }

            if (unfinished.Count > 0)
            {
                MessageBox.Show(
                    HwTestLocalization.PickFormat(
                        "Nije moguće sačuvati izveštaj dok svi primenljivi testovi nisu završeni (PASS ili FAIL).\n\nPreostali testovi ({0}):\n{1}",
                        "在所有适用测试完成（PASS 或 FAIL）之前无法保存报告。\n\n剩余测试 ({0})：\n{1}",
                        unfinished.Count,
                        string.Join("\n", unfinished.ToArray())),
                    HwTestLocalization.Pick("Neizvršeni testovi", "未完成的测试"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SaveCurrentReport(false, false, "", false);
        }

        private void SaveCurrentReport(bool promptIfPending, bool autoMarkPendingAsSkip, string defaultSkipReason, bool isCancelled)
        {
            if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
            int pass = 0, warn = 0, fail = 0, skip = 0, na = 0, pending = 0;
            foreach (TestItemResult t in _testItems)
            {
                if (t.Status == TestStatus.Pass)
                {
                    pass++;
                    if (t.HasWarning) warn++;
                }
                else if (t.Status == TestStatus.Fail) fail++;
                else if (t.Status == TestStatus.Skip) skip++;
                else if (t.Status == TestStatus.NotApplicable) na++;
                else pending++;
            }

            // Sistemski izuzetak (HWT-02 Odluka A3): isključivo za ForceCompletePhase5SkipPending / sistemski prekid
            if (pending > 0 && autoMarkPendingAsSkip)
            {
                string skipReason = string.IsNullOrEmpty(defaultSkipReason) ? HwTestLocalization.Pick("Preskočeno u okviru Faze 5", "在阶段 5 中跳过") : defaultSkipReason;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    TestItemResult t = _testItems[i];
                    if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                    {
                        t.Status = TestStatus.Skip;
                        if (string.IsNullOrEmpty(t.Reason))
                        {
                            t.Reason = skipReason;
                        }
                        else if (t.Reason.IndexOf(skipReason, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            t.Reason = skipReason + " (" + t.Reason + ")";
                        }
                        Border card;
                        if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
                    }
                }

                pass = 0; warn = 0; fail = 0; skip = 0; na = 0; pending = 0;
                foreach (TestItemResult t in _testItems)
                {
                    if (t.Status == TestStatus.Pass)
                    {
                        pass++;
                        if (t.HasWarning) warn++;
                    }
                    else if (t.Status == TestStatus.Fail) fail++;
                    else if (t.Status == TestStatus.Skip) skip++;
                    else if (t.Status == TestStatus.NotApplicable) na++;
                    else pending++;
                }
                UpdateSummaryBadge();
            }

            string overall = "PASS";
            if (fail > 0) overall = "FAIL";
            else if (skip > 0 || pending > 0) overall = "PASS_WITH_SKIP";

            double operatorWaitSec = (_autoTestsFinishedUtc.HasValue && _firstOperatorInteractionUtc.HasValue && _firstOperatorInteractionUtc.Value >= _autoTestsFinishedUtc.Value)
                ? Math.Round((_firstOperatorInteractionUtc.Value - _autoTestsFinishedUtc.Value).TotalSeconds, 1)
                : 0.0;
            double activeHwTestSec = _firstOperatorInteractionUtc.HasValue
                ? Math.Round(Math.Max(0.0, (DateTime.UtcNow - _firstOperatorInteractionUtc.Value).TotalSeconds), 1)
                : 0.0;

            HwTestRunResult runResult = new HwTestRunResult
            {
                Started = DateTime.Now.AddSeconds(-_runStopwatch.Elapsed.TotalSeconds).ToString("o"),
                Finished = DateTime.Now.ToString("o"),
                Model = _modelName,
                Serial = _serialNumber,
                Summary = new Dictionary<string, object>
                {
                    { "pass", pass },
                    { "warnings", warn },
                    { "fail", fail },
                    { "skip", skip },
                    { "na", na },
                    { "overall", overall },
                    { "operator_wait_sec", operatorWaitSec },
                    { "active_hwtest_sec", activeHwTestSec }
                }
            };
            if (!string.IsNullOrEmpty(_driverGateEmergencyOverrideReason))
            {
                runResult.Summary["driver_gate_emergency_override_reason"] = _driverGateEmergencyOverrideReason;
            }

            foreach (TestItemResult t in _testItems)
            {
                Dictionary<string, object> itemDict = new Dictionary<string, object>
                {
                    { "id", t.Id },
                    { "title", t.Title },
                    { "automated", t.IsAutomated },
                    { "status", t.Status.ToString().ToUpperInvariant() },
                    { "warning", t.HasWarning },
                    { "metric_summary", t.MetricSummary ?? "" },
                    { "attempts", t.Attempts },
                    { "duration_s", t.DurationSeconds },
                    { "reason", t.Reason ?? "" },
                    { "details", t.Details ?? new Dictionary<string, object>() }
                };
                runResult.Tests.Add(itemDict);
            }

            TestItemResult cpuTest = FindItemById("auto_cpu");
            int cpuDurReport = _phase5CpuDurationSec > 0 ? _phase5CpuDurationSec : (cpuTest != null ? cpuTest.DurationSeconds : 0);
            float peakTempReport = _phase5PeakCpuTemp > 0f ? _phase5PeakCpuTemp : (cpuTest != null ? ExtractFloatDetail(cpuTest.Details, "peak_temp_c") : 0f);
            float maxThrotReport = _phase5MaxCpuThrottle > 0f ? _phase5MaxCpuThrottle : (cpuTest != null ? ExtractFloatDetail(cpuTest.Details, "max_throttle_pct") : 0f);
            Dictionary<string, object> phase5MetricsReport = new Dictionary<string, object>
            {
                { "cpu_duration_sec", cpuDurReport },
                { "peak_cpu_temp", (double)peakTempReport },
                { "max_cpu_throttle", (double)maxThrotReport },
                { "thermal_critical", _phase5ThermalCritical },
                { "thermal_note", _phase5ThermalNote ?? "" }
            };

            // Upis u JSON fajl (lokalno + trajna kopija u C:\Windows\Logs\WIS\hwtest-result.json + kopija pored exe-a u hwtest-logs/)
            var reportPayload = new Dictionary<string, object>
            {
                { "schema", runResult.Schema },
                { "started", runResult.Started },
                { "finished", runResult.Finished },
                { "model", runResult.Model },
                { "serial", runResult.Serial },
                { "app_version", runResult.AppVersion },
                { "summary", runResult.Summary },
                { "phase5_metrics", phase5MetricsReport },
                { "tests", runResult.Tests }
            };
            if (!string.IsNullOrEmpty(_driverGateEmergencyOverrideReason))
            {
                reportPayload["driver_gate_emergency_override_reason"] = _driverGateEmergencyOverrideReason;
            }
            if (!string.IsNullOrEmpty(_phase5ThermalNote))
            {
                reportPayload["thermal_note"] = _phase5ThermalNote;
                reportPayload["notes"] = new List<object> { _phase5ThermalNote };
            }
            string json = SimpleJson.Serialize(reportPayload);

            HwTestLogger.Info("REPORT_JSON", json);

            bool savedPrimary = false;
            try
            {
                string dir = Path.GetDirectoryName(_outJsonPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_outJsonPath, json, System.Text.Encoding.UTF8);
                savedPrimary = true;
                _reportSavedInSession = true;
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("SAVE", "Upis u primary path pao (" + _outJsonPath + "): " + ex.Message);
                try
                {
                    string fallbackOut = Path.Combine(Path.GetTempPath(), "wis-hwtest-result.json");
                    File.WriteAllText(fallbackOut, json, System.Text.Encoding.UTF8);
                    savedPrimary = true;
                }
                catch { }
            }

            try
            {
                const string persistentLogResultPath = @"C:\Windows\Logs\WIS\hwtest-result.json";
                string persistentDir = Path.GetDirectoryName(persistentLogResultPath);
                if (!string.IsNullOrEmpty(persistentDir) && !Directory.Exists(persistentDir))
                    Directory.CreateDirectory(persistentDir);
                File.WriteAllText(persistentLogResultPath, json, System.Text.Encoding.UTF8);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(HwTestLogger.NasLogPath))
                {
                    string resCopy = HwTestLogger.NasLogPath + ".result.json";
                    File.WriteAllText(resCopy, json, System.Text.Encoding.UTF8);
                }
            }
            catch { }

            if (savedPrimary)
            {
                DeleteCheckpointFile();
            }
            else
            {
                MessageBox.Show(HwTestLocalization.Pick("Nije moguće upisati izveštaj ni u primarnu ni u TEMP putanju.", "无法将报告写入主路径或 TEMP 路径。"), HwTestLocalization.Pick("Upozorenje", "警告"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_isEmbedded)
            {
                if (_phase5WorkflowActive)
                {
                    EmitPhase5Step("P5.Peripherals", HwTestLocalization.Pick("Interaktivni testovi periferija", "外设交互式测试"),
                        isCancelled ? "SKIPPED_USER" : ((fail > 0 || warn > 0 || skip > 0) ? "WARN" : "OK"), "");
                }
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = HwTestLocalization.PickFormat("✓ Hardverski izveštaj sačuvan ({0}: {1} PASS, {2} SKIP, {3} FAIL).", "✓ 硬件报告已保存（{0}：{1} PASS，{2} SKIP，{3} FAIL）。", overall, pass, skip, fail);
                }
                Action<HwTestRunResult> cb = _onReportSavedCallback;
                if (cb != null)
                {
                    try { cb(runResult); } catch { }
                }
                NotifyPhase5Finished(runResult, isCancelled);
                return;
            }

            Environment.ExitCode = (fail > 0) ? 1 : 0;
            Close();
        }

        private string GetTouchpadRestoreFilePath()
        {
            if (_isEmbedded || string.IsNullOrEmpty(_outJsonPath))
            {
                return @"C:\setup\hwtest-touchpad-restore.json";
            }
            try
            {
                string dir = Path.GetDirectoryName(_outJsonPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    return Path.Combine(dir, "hwtest-touchpad-restore.json");
                }
            }
            catch { }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                return Path.Combine(baseDir, "hwtest-touchpad-restore.json");
            }
            return Path.Combine(Path.GetTempPath(), "hwtest-touchpad-restore.json");
        }

        private void TryStartupRecoverTouchpadIfNeeded()
        {
            try
            {
                string primaryPath = GetTouchpadRestoreFilePath();
                TouchpadTestWindow.TryRecoverTouchpadFromRestoreFile(primaryPath);

                const string canonicalSetupPath = @"C:\setup\hwtest-touchpad-restore.json";
                if (!string.Equals(primaryPath, canonicalSetupPath, StringComparison.OrdinalIgnoreCase))
                {
                    TouchpadTestWindow.TryRecoverTouchpadFromRestoreFile(canonicalSetupPath);
                }
            }
            catch { }
        }

        private string GetCheckpointFilePath()
        {
            if (_isEmbedded || string.IsNullOrEmpty(_outJsonPath))
            {
                return @"C:\setup\hwtest-checkpoint.json";
            }
            try
            {
                string dir = Path.GetDirectoryName(_outJsonPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    return Path.Combine(dir, "hwtest-checkpoint.json");
                }
            }
            catch { }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                return Path.Combine(baseDir, "hwtest-checkpoint.json");
            }
            return Path.Combine(Path.GetTempPath(), "hwtest-checkpoint.json");
        }

        private void DeleteCheckpointFile()
        {
            try
            {
                string cpPath = GetCheckpointFilePath();
                if (!string.IsNullOrEmpty(cpPath) && File.Exists(cpPath))
                {
                    File.Delete(cpPath);
                    HwTestLogger.Info("CHECKPOINT", "Obrisan privremeni checkpoint fajl nakon čuvanja konačnog izveštaja: " + cpPath);
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("CHECKPOINT", "Nije uspelo brisanje checkpoint fajla: " + ex.Message);
            }
        }

        public bool HasAnyRealCompletedTestsForCheckpoint()
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (!t.IsPassivePreliminary && (t.Status == TestStatus.Pass || t.Status == TestStatus.Fail))
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasAnyCompletedTestsForCheckpoint()
        {
            return HasAnyRealCompletedTestsForCheckpoint();
        }

        public void SaveCheckpoint(string rebootReasonTestId)
        {
            string reasonId = string.IsNullOrEmpty(rebootReasonTestId) ? "" : rebootReasonTestId.Trim();
            List<object> completedTests = new List<object>();

            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.IsPassivePreliminary)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(reasonId) && string.Equals(t.Id, reasonId, StringComparison.OrdinalIgnoreCase))
                {
                    if (t.Status == TestStatus.Running)
                        t.Status = TestStatus.Pending;
                    continue;
                }

                if (t.Status == TestStatus.Pass ||
                    t.Status == TestStatus.Fail ||
                    t.Status == TestStatus.NotApplicable ||
                    t.Status == TestStatus.Skip)
                {
                    Dictionary<string, object> itemDict = new Dictionary<string, object>
                    {
                        { "id", t.Id },
                        { "status", t.Status.ToString().ToUpperInvariant() },
                        { "warning", t.HasWarning },
                        { "metric_summary", t.MetricSummary ?? "" },
                        { "attempts", t.Attempts },
                        { "duration_s", t.DurationSeconds },
                        { "reason", t.Reason ?? "" },
                        { "details", t.Details ?? new Dictionary<string, object>() }
                    };
                    completedTests.Add(itemDict);
                }
            }

            TestItemResult cpuItem = FindItemById("auto_cpu");
            int cpuDurSec = _phase5CpuDurationSec > 0 ? _phase5CpuDurationSec : (cpuItem != null ? cpuItem.DurationSeconds : 0);
            float peakCpuTemp = _phase5PeakCpuTemp > 0f ? _phase5PeakCpuTemp : (cpuItem != null ? ExtractFloatDetail(cpuItem.Details, "peak_temp_c") : 0f);
            float maxCpuThrot = _phase5MaxCpuThrottle > 0f ? _phase5MaxCpuThrottle : (cpuItem != null ? ExtractFloatDetail(cpuItem.Details, "max_throttle_pct") : 0f);

            Dictionary<string, object> phase5Metrics = new Dictionary<string, object>
            {
                { "cpu_duration_sec", cpuDurSec },
                { "peak_cpu_temp", (double)peakCpuTemp },
                { "max_cpu_throttle", (double)maxCpuThrot },
                { "thermal_critical", _phase5ThermalCritical },
                { "thermal_note", _phase5ThermalNote ?? "" }
            };

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "schema", "wis.hwtest.checkpoint.v1" },
                { "saved_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "serial", _serialNumber ?? "" },
                { "model", _modelName ?? "" },
                { "reboot_reason_test_id", reasonId },
                { "driver_gate_emergency_override_reason", _driverGateEmergencyOverrideReason ?? "" },
                { "phase5_metrics", phase5Metrics },
                { "completed_tests", completedTests }
            };

            string json = SimpleJson.Serialize(payload);
            string cpPath = GetCheckpointFilePath();
            try
            {
                string dir = Path.GetDirectoryName(cpPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmpPath = cpPath + ".tmp";
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                if (File.Exists(cpPath))
                {
                    try
                    {
                        File.Replace(tmpPath, cpPath, null);
                    }
                    catch
                    {
                        File.Delete(cpPath);
                        File.Move(tmpPath, cpPath);
                    }
                }
                else
                {
                    File.Move(tmpPath, cpPath);
                }
                HwTestLogger.Info("CHECKPOINT_SAVE", string.Format(
                    "Sačuvan checkpoint na '{0}' (sačuvano={1}, razlog='{2}').",
                    cpPath, completedTests.Count, reasonId));
            }
            catch (Exception ex)
            {
                HwTestLogger.Error("CHECKPOINT_SAVE", "Greška pri upisu checkpointa: " + ex.Message);
            }
        }

        public static bool TryParsePersistedUtcTimestamp(string rawTimestamp, out DateTime savedUtc)
        {
            savedUtc = DateTime.MinValue;
            if (string.IsNullOrEmpty(rawTimestamp)) return false;
            if (!DateTime.TryParse(rawTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out savedUtc))
            {
                return false;
            }
            savedUtc = savedUtc.ToUniversalTime();
            return true;
        }

        private static bool HasRecordedServiceSkipReason(string reason, IDictionary<string, object> details)
        {
            if (details != null)
            {
                string manualReason = ReadCpString(details, "manual_skip_reason");
                if (!string.IsNullOrEmpty(manualReason) && manualReason.Trim().Length >= 5) return true;
                string emergencyReason = ReadCpString(details, "emergency_skip_reason");
                if (!string.IsNullOrEmpty(emergencyReason) && emergencyReason.Trim().Length >= 5) return true;
            }

            if (string.IsNullOrEmpty(reason)) return false;
            string r = reason.Trim();
            if (r.Length < 5) return false;

            // Genericke poruke prekida bez konkretnog servisnog razloga se NE priznaju kao zavrseni testovi
            if (r.Equals("Preskočeno u okviru Faze 5", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("在阶段 5 中跳过", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("Preskočeno od strane servisera", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("技术员已跳过", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("CPU stres test je prekinut od strane korisnika", StringComparison.OrdinalIgnoreCase) ||
                r.Equals("CPU 压力测试已被用户中断", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private void TryRestoreCheckpoint()
        {
            TryRestorePersistedTestState();
        }

        public bool TryRestorePersistedTestState()
        {
            const double MaxTtlHours = 48.0;
            List<string> candidatePaths = new List<string>();
            const string logsResultPath = @"C:\Windows\Logs\WIS\hwtest-result.json";
            const string setupResultPath = @"C:\setup\hwtest-result.json";
            const string setupCheckpointPath = @"C:\setup\hwtest-checkpoint.json";

            candidatePaths.Add(logsResultPath);
            if (!string.IsNullOrEmpty(_outJsonPath) &&
                !string.Equals(_outJsonPath, logsResultPath, StringComparison.OrdinalIgnoreCase))
            {
                candidatePaths.Add(_outJsonPath);
            }
            if (!candidatePaths.Contains(setupResultPath))
            {
                candidatePaths.Add(setupResultPath);
            }

            string primaryCpPath = GetCheckpointFilePath();
            if (!string.IsNullOrEmpty(primaryCpPath))
            {
                candidatePaths.Add(primaryCpPath);
            }
            if (!string.Equals(primaryCpPath, setupCheckpointPath, StringComparison.OrdinalIgnoreCase))
            {
                candidatePaths.Add(setupCheckpointPath);
            }

            string activeRebootReasonId = "";
            string lastLoadedSourcePath = "";
            int totalMerged = 0;

            for (int pIdx = 0; pIdx < candidatePaths.Count; pIdx++)
            {
                string path = candidatePaths[pIdx];
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrEmpty(json)) continue;

                    JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                    IDictionary<string, object> root = ser.DeserializeObject(json) as IDictionary<string, object>;
                    if (root == null) continue;

                    string schema = ReadCpString(root, "schema");
                    bool isCheckpoint = string.Equals(schema, "wis.hwtest.checkpoint.v1", StringComparison.Ordinal);
                    bool isReport = string.Equals(schema, "wis.hwtest.v1", StringComparison.Ordinal);
                    if (!isCheckpoint && !isReport)
                    {
                        HwTestLogger.Warn("CHECKPOINT_RESTORE", "Ignorišem fajl nepoznate šeme (" + path + "): " + schema);
                        continue;
                    }

                    string savedUtcStr = isCheckpoint ? ReadCpString(root, "saved_utc") : ReadCpString(root, "finished");
                    if (string.IsNullOrEmpty(savedUtcStr) && isReport)
                    {
                        savedUtcStr = ReadCpString(root, "started");
                    }

                    DateTime savedUtc;
                    if (!TryParsePersistedUtcTimestamp(savedUtcStr, out savedUtc))
                    {
                        HwTestLogger.Warn("CHECKPOINT_RESTORE", "Neispravan saved_utc/finished u '" + path + "' — ignorišem.");
                        continue;
                    }

                    double ageHours = (DateTime.UtcNow - savedUtc).TotalHours;
                    if (ageHours >= MaxTtlHours || ageHours < -24.0)
                    {
                        HwTestLogger.Warn("CHECKPOINT_RESTORE", string.Format(
                            "Persisted stanje '{0}' je zastarelo ({1:F1}h >= {2:F0}h) — ignorišem.",
                            path, ageHours, MaxTtlHours));
                        continue;
                    }

                    string srcSerial = ReadCpString(root, "serial");
                    if (!IsPlaceholderIdentity(srcSerial) && !IsPlaceholderIdentity(_serialNumber) &&
                        !string.Equals(srcSerial.Trim(), _serialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        HwTestLogger.Warn("CHECKPOINT_RESTORE", string.Format(
                            "Serial neusklađen (izvor='{0}', uređaj='{1}', fajl='{2}') — ignorišem.",
                            srcSerial, _serialNumber, path));
                        continue;
                    }

                    string rebootReasonId = isCheckpoint ? ReadCpString(root, "reboot_reason_test_id") : "";
                    if (!string.IsNullOrEmpty(rebootReasonId))
                    {
                        activeRebootReasonId = rebootReasonId;
                    }

                    string emergencyOverride = ReadCpString(root, "driver_gate_emergency_override_reason");
                    if (string.IsNullOrEmpty(emergencyOverride))
                    {
                        object sumObj;
                        if (root.TryGetValue("summary", out sumObj) && sumObj is IDictionary<string, object>)
                        {
                            emergencyOverride = ReadCpString((IDictionary<string, object>)sumObj, "driver_gate_emergency_override_reason");
                        }
                    }
                    if (!string.IsNullOrEmpty(emergencyOverride) && emergencyOverride.Trim().Length >= 5)
                    {
                        _driverGateEmergencyOverrideReason = emergencyOverride.Trim();
                        _driverGateEmergencyUnlocked = true;
                    }

                    object metricsObj;
                    if (root.TryGetValue("phase5_metrics", out metricsObj))
                    {
                        IDictionary<string, object> mDict = metricsObj as IDictionary<string, object>;
                        if (mDict != null)
                        {
                            int dur = ReadCpInt(mDict, "cpu_duration_sec");
                            float pk = ReadCpFloat(mDict, "peak_cpu_temp");
                            float thr = ReadCpFloat(mDict, "max_cpu_throttle");
                            bool crit = ReadCpBool(mDict, "thermal_critical");
                            string tNote = ReadCpString(mDict, "thermal_note");
                            if (dur > 0) _phase5CpuDurationSec = dur;
                            if (pk > 0f) _phase5PeakCpuTemp = pk;
                            if (thr > 0f) _phase5MaxCpuThrottle = thr;
                            if (crit) _phase5ThermalCritical = true;
                            if (!string.IsNullOrEmpty(tNote)) _phase5ThermalNote = tNote;
                        }
                    }
                    else if (isReport)
                    {
                        string rootThermalNote = ReadCpString(root, "thermal_note");
                        if (!string.IsNullOrEmpty(rootThermalNote)) _phase5ThermalNote = rootThermalNote;
                    }

                    string testsKey = isCheckpoint ? "completed_tests" : "tests";
                    object testsObj;
                    if (root.TryGetValue(testsKey, out testsObj) && testsObj != null)
                    {
                        System.Collections.IEnumerable testsEnum = testsObj as System.Collections.IEnumerable;
                        if (testsEnum != null)
                        {
                            foreach (object entry in testsEnum)
                            {
                                IDictionary<string, object> td = entry as IDictionary<string, object>;
                                if (td == null) continue;

                                string id = ReadCpString(td, "id");
                                if (string.IsNullOrEmpty(id)) continue;

                                if (!string.IsNullOrEmpty(rebootReasonId) &&
                                    string.Equals(id, rebootReasonId, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                // V344-03: auto_security se preskace pri restauraciji ISKLJUCIVO kod stvarnog UEFI BIOS restarta
                                if (!string.IsNullOrEmpty(rebootReasonId) &&
                                    string.Equals(id, "auto_security", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                TestItemResult item = FindItemById(id);
                                if (item == null) continue;

                                string statusStr = ReadCpString(td, "status");
                                TestStatus parsedStatus;
                                if (!TryParseCheckpointStatus(statusStr, out parsedStatus)) continue;

                                if (parsedStatus == TestStatus.NotApplicable)
                                {
                                    continue;
                                }

                                if (ReadCpBool(td, "is_passive_preliminary"))
                                {
                                    continue;
                                }

                                string reasonStr = ReadCpString(td, "reason");
                                IDictionary<string, object> rawDetailsDict = null;
                                object dObj;
                                if (td.TryGetValue("details", out dObj))
                                {
                                    rawDetailsDict = dObj as IDictionary<string, object>;
                                }

                                if (string.Equals(id, "auto_gpu", StringComparison.OrdinalIgnoreCase) &&
                                    parsedStatus == TestStatus.Fail)
                                {
                                    bool isBasicDisplayDetail = rawDetailsDict != null &&
                                        (ReadCpBool(rawDetailsDict, "is_basic_display") || ReadCpBool(rawDetailsDict, "has_any_basic_display"));
                                    bool isBasicDisplayReason = !string.IsNullOrEmpty(reasonStr) &&
                                        reasonStr.IndexOf("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) >= 0;
                                    if (isBasicDisplayDetail || isBasicDisplayReason)
                                    {
                                        HwTestLogger.Info("CHECKPOINT_RESTORE", "Ignorišem pre-drajverski Basic Display auto_gpu=FAIL iz persisted stanja.");
                                        continue;
                                    }
                                }

                                // V344-03: SKIP bez zabelezenog servisnog razloga ostaje Pending
                                if (parsedStatus == TestStatus.Skip && !HasRecordedServiceSkipReason(reasonStr, rawDetailsDict))
                                {
                                    continue;
                                }

                                if (item.Status == TestStatus.NotApplicable && parsedStatus != TestStatus.Skip)
                                {
                                    continue;
                                }

                                item.Status = parsedStatus;
                                item.HasWarning = ReadCpBool(td, "warning");
                                string mSum = ReadCpString(td, "metric_summary");
                                if (!string.IsNullOrEmpty(mSum)) item.MetricSummary = mSum;
                                item.Attempts = Math.Max(1, ReadCpInt(td, "attempts"));
                                item.DurationSeconds = Math.Max(0, ReadCpInt(td, "duration_s"));
                                item.Reason = reasonStr;
                                if (rawDetailsDict != null)
                                {
                                    item.Details = NormalizeCpDetails(rawDetailsDict);
                                }

                                if (string.Equals(item.Id, "auto_cpu", StringComparison.OrdinalIgnoreCase) &&
                                    (parsedStatus == TestStatus.Pass || parsedStatus == TestStatus.Fail))
                                {
                                    if (_phase5CpuDurationSec <= 0 && item.DurationSeconds > 0)
                                        _phase5CpuDurationSec = item.DurationSeconds;
                                    float pkDetail = ExtractFloatDetail(item.Details, "peak_temp_c");
                                    if (_phase5PeakCpuTemp <= 0f && pkDetail > 0f)
                                        _phase5PeakCpuTemp = pkDetail;
                                    float thDetail = ExtractFloatDetail(item.Details, "max_throttle_pct");
                                    if (_phase5MaxCpuThrottle <= 0f && thDetail > 0f)
                                        _phase5MaxCpuThrottle = thDetail;
                                    if (_phase5PeakCpuTemp >= 95.0f)
                                        _phase5ThermalCritical = true;
                                }

                                Border card;
                                if (_cardMap != null && _cardMap.TryGetValue(item.Id, out card) && card != null)
                                {
                                    UpdateCardVisual(card, item);
                                }

                                _restoredTestIds.Add(item.Id);
                                totalMerged++;
                            }
                        }
                    }

                    lastLoadedSourcePath = path;
                }
                catch (Exception ex)
                {
                    HwTestLogger.Warn("CHECKPOINT_RESTORE", "Greška pri čitanju '" + path + "': " + ex.Message);
                }
            }

            if (!string.IsNullOrEmpty(activeRebootReasonId))
            {
                TestItemResult rebootTarget = FindItemById(activeRebootReasonId);
                if (rebootTarget != null && rebootTarget.Status != TestStatus.NotApplicable)
                {
                    rebootTarget.Status = TestStatus.Pending;
                    _restoredTestIds.Remove(activeRebootReasonId);
                }
                TestItemResult secTarget = FindItemById("auto_security");
                if (secTarget != null && secTarget.Status != TestStatus.NotApplicable)
                {
                    secTarget.Status = TestStatus.Pending;
                    _restoredTestIds.Remove("auto_security");
                }
            }

            int restoredCount = _restoredTestIds.Count;
            int applicableTotal = 0;
            for (int i = 0; i < _testItems.Count; i++)
            {
                if (_testItems[i].Status != TestStatus.NotApplicable)
                    applicableTotal++;
            }

            _checkpointRestoredCount = restoredCount;
            _checkpointTotalApplicable = applicableTotal;
            _checkpointRestored = (restoredCount > 0 || !string.IsNullOrEmpty(activeRebootReasonId));

            if (_checkpointRestored)
            {
                HwTestLogger.Info("CHECKPOINT_RESTORE", string.Format(
                    "Restaurirano {0}/{1} već završenih testova iz '{2}' (reboot_reason='{3}', spojeno={4}).",
                    restoredCount, applicableTotal, lastLoadedSourcePath, activeRebootReasonId, totalMerged));
            }

            return _checkpointRestored;
        }

        public void ResetAllTestsWithConfirmation()
        {
            MarkOperatorInteraction();
            if (_autoStressRunning)
            {
                MessageBox.Show(
                    HwTestLocalization.Pick(
                        "Automatski stres testovi su trenutno u toku. Sačekajte da se završe pre resetovanja svih testova.",
                        "自动压力测试正在运行，请等待其完成后再重置所有测试。"),
                    HwTestLocalization.Pick("Test u toku", "测试进行中"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                HwTestLocalization.Pick(
                    "Da li ste sigurni da želite da obrišete sve sačuvane rezultate (checkpoint i izveštaj) i ponovite sve hardverske testove ispočetka?",
                    "确定要清除所有已保存的测试结果（检查点和报告）并从头重新运行所有硬件测试吗？"),
                HwTestLocalization.Pick("Ponovi sve testove", "重新运行所有测试"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            ResetAllPersistedTests();
        }

        public void ResetAllPersistedTests()
        {
            try { DeleteCheckpointFile(); } catch { }
            string[] filesToClear = new string[]
            {
                @"C:\setup\hwtest-checkpoint.json",
                _outJsonPath,
                @"C:\setup\hwtest-result.json",
                @"C:\Windows\Logs\WIS\hwtest-result.json"
            };
            for (int i = 0; i < filesToClear.Length; i++)
            {
                string fp = filesToClear[i];
                if (string.IsNullOrEmpty(fp)) continue;
                try
                {
                    if (File.Exists(fp)) File.Delete(fp);
                }
                catch { }
            }

            _restoredTestIds.Clear();
            _checkpointRestored = false;
            _checkpointRestoredCount = 0;
            _phase5CpuDurationSec = 0;
            _phase5PeakCpuTemp = 0f;
            _phase5MaxCpuThrottle = 0f;
            _phase5ThermalCritical = false;
            _phase5ThermalNote = "";

            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
                if (t.Status != TestStatus.NotApplicable)
                {
                    t.Status = TestStatus.Pending;
                    t.DurationSeconds = 0;
                    t.Attempts = 0;
                    t.HasWarning = false;
                    t.Reason = "";
                    t.MetricSummary = "";
                    t.Details = new Dictionary<string, object>();
                }
                Border card;
                if (_cardMap.TryGetValue(t.Id, out card) && card != null)
                {
                    UpdateCardVisual(card, t);
                }
            }

            RefreshHardwareProbeAfterDrivers();
            RefreshCardVisibility();
            UpdateSummaryBadge();
            RefreshDriverGateUi();

            if (_autoSectionStatus != null)
            {
                _autoSectionStatus.Text = HwTestLocalization.Pick(
                    "↻ Svi sačuvani rezultati su obrisani — testovi su vraćeni na početak.",
                    "↻ 所有已保存的测试结果已清除 — 测试已重置为初始状态。");
                _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
            }

            HwTestLogger.Info("RESET_ALL", "Serviser je ručno obrisao sačuvano stanje i vratio sve hardverske testove na Pending.");

            if (!_phase5WorkflowActive)
            {
                _passiveChecksStarted = false;
                _cancelPassiveAutoChecks = false;
                StartPassiveAutoChecks();
            }
        }

        private static bool TryParseCheckpointStatus(string statusStr, out TestStatus status)
        {
            status = TestStatus.Pending;
            if (string.IsNullOrEmpty(statusStr)) return false;
            string s = statusStr.Trim().ToUpperInvariant();
            if (s == "PASS") { status = TestStatus.Pass; return true; }
            if (s == "FAIL") { status = TestStatus.Fail; return true; }
            if (s == "SKIP") { status = TestStatus.Skip; return true; }
            if (s == "NOTAPPLICABLE" || s == "N/A") { status = TestStatus.NotApplicable; return true; }
            return false;
        }

        private static Dictionary<string, object> NormalizeCpDetails(IDictionary<string, object> raw)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (raw == null) return result;
            foreach (KeyValuePair<string, object> kv in raw)
            {
                result[kv.Key] = NormalizeCpValue(kv.Value);
            }
            return result;
        }

        private static object NormalizeCpValue(object v)
        {
            if (v == null) return null;
            if (v is decimal) return Convert.ToDouble((decimal)v, CultureInfo.InvariantCulture);
            IDictionary<string, object> dict = v as IDictionary<string, object>;
            if (dict != null) return NormalizeCpDetails(dict);
            if (!(v is string))
            {
                System.Collections.IEnumerable arr = v as System.Collections.IEnumerable;
                if (arr != null)
                {
                    List<object> list = new List<object>();
                    foreach (object elem in arr) list.Add(NormalizeCpValue(elem));
                    return list;
                }
            }
            return v;
        }

        private static string ReadCpString(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return "";
            return Convert.ToString(dict[key], CultureInfo.InvariantCulture) ?? "";
        }

        private static int ReadCpInt(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0;
            int res;
            if (int.TryParse(Convert.ToString(dict[key], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out res))
                return res;
            return 0;
        }

        private static float ReadCpFloat(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0f;
            float res;
            if (float.TryParse(Convert.ToString(dict[key], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out res))
                return res;
            return 0f;
        }

        private static bool ReadCpBool(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return false;
            object v = dict[key];
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        public void SetUefiBiosRebootHandler(Action<string> handler)
        {
            _uefiBiosRebootHandler = handler;
        }

        public void RequestUefiBiosReboot(string testId)
        {
            string safeTestId = string.IsNullOrEmpty(testId) ? "peripherals" : testId.Trim();
            TestItemResult target = FindItemById(safeTestId);
            string testTitle = target != null ? target.Title : safeTestId;

            MessageBoxResult confirm = MessageBoxResult.No;
            _uefiBiosPromptOpen = true;
            try
            {
                confirm = MessageBox.Show(
                    HwTestLocalization.PickFormat(
                        "Računar će biti restartovan direktno u UEFI BIOS podešavanja kako biste uključili uređaj za test '{0}'.\n\n" +
                        "• Svi dosadašnji završeni rezultati i progres Faze 5 biće sačuvani u checkpoint fajlu.\n" +
                        "• Po izlasku iz UEFI BIOS-a, Windows i WIS će se automatski podići i nastaviti tačno gde ste stali.\n" +
                        "• Tipični BIOS tasteri: HP: F10, Lenovo: F1, Dell: F2.\n\n" +
                        "Da li želite da odmah restartujete računar u UEFI BIOS?",
                        "电脑将直接重启进入 UEFI BIOS 设置，以便为测试“{0}”启用设备。\n\n" +
                        "• 迄今完成的所有测试结果和阶段 5 进度都将保存在检查点文件中。\n" +
                        "• 退出 UEFI BIOS 后，Windows 和 WIS 将自动启动并从中断处继续。\n" +
                        "• 常见 BIOS 热键：HP：F10，Lenovo：F1，Dell：F2。\n\n" +
                        "是否立即重启电脑进入 UEFI BIOS？",
                        testTitle),
                    HwTestLocalization.Pick("Restart u UEFI BIOS", "重启进入 UEFI BIOS"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    _uefiBiosRebootRequested = true;
                }
            }
            finally
            {
                _uefiBiosPromptOpen = false;
            }

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            _phase5WorkflowActive = false;
            _waitingForManualPeripherals = false;
            _autoStressCancel.Cancel();

            if (target != null && target.Status == TestStatus.Running)
            {
                target.Status = TestStatus.Pending;
            }

            SaveCheckpoint(safeTestId);

            HwTestLogger.Warn("UEFI_REBOOT", string.Format(
                "Potvrđen restart u UEFI BIOS zbog testa '{0}' (checkpoint='{1}').",
                safeTestId, GetCheckpointFilePath()));

            Action<string> handler = _uefiBiosRebootHandler;
            if (handler != null)
            {
                handler(safeTestId);
                return;
            }

            // Standalone režim (bez WIS.UI hosta): izvrši shutdown.exe /r /fw /t 0 sa fallbackom
            bool fwOk = false;
            int exitCode = -1;
            string errMsg = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /fw /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        if (p.WaitForExit(3000))
                        {
                            exitCode = p.ExitCode;
                            fwOk = (exitCode == 0);
                        }
                        else
                        {
                            fwOk = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errMsg = ex.Message;
            }

            if (!fwOk)
            {
                HwTestLogger.Warn("UEFI_REBOOT", string.Format(
                    "Standalone shutdown.exe /r /fw /t 0 nije uspeo (ExitCode={0}, Err='{1}') — pokrećem fallback /r /t 3 /f.",
                    exitCode, errMsg));
                MessageBox.Show(
                    HwTestLocalization.Pick(
                        "Direktan ulazak u UEFI firmware (/r /fw) nije podržan na ovom računaru.\n\n" +
                        "Računar će se sada restartovati — pritisnite BIOS taster odmah pri paljenju:\n" +
                        "HP: F10 | Lenovo: F1 | Dell: F2",
                        "此电脑不支持直接进入 UEFI 固件 (/r /fw)。\n\n" +
                        "电脑即将重启 — 请在开机时立即按下 BIOS 热键：\n" +
                        "HP：F10 | Lenovo：F1 | Dell：F2"),
                    HwTestLocalization.Pick("Ručni ulazak u BIOS", "手动进入 BIOS"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                try
                {
                    ProcessStartInfo fallbackPsi = new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 3 /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(fallbackPsi);
                }
                catch { }
            }

            try { Close(); } catch { }
        }
    }
}
