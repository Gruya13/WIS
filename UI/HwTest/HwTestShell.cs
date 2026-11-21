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
        private readonly Dictionary<string, TextBlock> _descTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _metricTextMap = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
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
        private TextBlock _subTitleBlock;
        private Button _btnFinish;
        private bool _isEmbedded = false;
        private Window _hostWindow = null;
        private Action<HwTestRunResult> _onReportSavedCallback = null;
        private bool _passiveChecksStarted = false;
        private volatile bool _phase5WorkflowActive = false;
        private volatile bool _waitingForManualPeripherals = false;
        private Window _activePeripheralDialog = null;
        public bool IsWaitingForManualTests
        {
            get { return _phase5WorkflowActive && _waitingForManualPeripherals && !_phase5CompletedNotified; }
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
            try { NativeMethods.SuppressLocationPromptPopups(); } catch { }
            _profileClient = existingClient ?? new ModelProfileClient();
            _hwInfo = HardwareProbe.DetectHardware();

            if (!string.IsNullOrEmpty(manufacturer) && string.IsNullOrEmpty(_hwInfo.Manufacturer))
                _hwInfo.Manufacturer = manufacturer;
            if (!string.IsNullOrEmpty(sku) && string.IsNullOrEmpty(_hwInfo.SystemSkuNumber))
                _hwInfo.SystemSkuNumber = sku;

            bool emptyModelArg = IsPlaceholderIdentity(modelName);
            _modelName = (!emptyModelArg)
                ? modelName
                : (!IsPlaceholderIdentity(_hwInfo.ModelName) ? _hwInfo.ModelName : "Nepoznat model");

            bool emptySnArg = IsPlaceholderIdentity(serialNumber);
            _serialNumber = (!emptySnArg)
                ? serialNumber
                : (!IsPlaceholderIdentity(_hwInfo.SerialNumber) ? _hwInfo.SerialNumber : "Nepoznat serijski broj");

            if (string.IsNullOrEmpty(_hwInfo.ModelName)) _hwInfo.ModelName = _modelName;
            if (string.IsNullOrEmpty(_hwInfo.SerialNumber)) _hwInfo.SerialNumber = _serialNumber;

            // Fabrička provera (OEM BOM / ServiceHub / lokalna heuristika) PRE formiranja liste testova
            FactorySpecProbe.ResolveAndMerge(_hwInfo, _profileClient);

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

            if (_hwInfo.PnpAuditNotes != null)
            {
                foreach (string note in _hwInfo.PnpAuditNotes)
                {
                    HwTestLogger.Info("PNP_AUDIT", note);
                }
            }
            if (_hwInfo.BiosAuditNotes != null)
            {
                foreach (string bNote in _hwInfo.BiosAuditNotes)
                {
                    HwTestLogger.Info("BIOS_AUDIT", bNote);
                }
            }

            _testItems = new List<TestItemResult>();
            _cardMap = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
            _runStopwatch = Stopwatch.StartNew();

            Title = "WIS — Hardverska Verifikacija Uređaja";
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
                Text = "HARDVERSKA DIJAGNOSTIKA I TESTIRANJE",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(title);

            TextBlock sub = new TextBlock
            {
                Text = string.Format("Model: {0} | Serijski broj: {1} | Šasija: {2}",
                    _modelName, _serialNumber, _hwInfo.IsLaptop ? "Laptop" : "Desktop"),
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
                Text = "⏳ Čeka pokretanje",
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

            Button btnRunAll = new Button
            {
                Content = "▶ Pokreni sve testove redom",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnRunAll.Click += (s, e) => RunSequentialTests();
            Grid.SetColumn(btnRunAll, 0);
            fg.Children.Add(btnRunAll);

            Button btnFinish = new Button
            {
                Content = "✓ Završi i sačuvaj izveštaj",
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

            Content = root;
            if (_checkpointRestored && _autoSectionStatus != null)
            {
                _autoSectionStatus.Text = string.Format(
                    "♻️ Nastavak posle UEFI BIOS restarta — restaurirano {0}/{1} već završenih testova.",
                    _checkpointRestoredCount, _checkpointTotalApplicable);
                _autoSectionStatus.ToolTip = _autoSectionStatus.Text;
            }
            RefreshCardVisibility();
            UpdateSummaryBadge();

            Loaded += (s, e) => StartPassiveAutoChecks();
            Closing += (s, e) =>
            {
                _phase5WorkflowActive = false;
                if (_runningUiTimer != null) _runningUiTimer.Stop();
                _autoStressCancel.Cancel();
                _autoEngine.Dispose();
            };
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

            StartPassiveAutoChecks();
            return _rootGrid;
        }

        public void ShutdownEmbedded()
        {
            _phase5WorkflowActive = false;
            if (_runningUiTimer != null)
            {
                try { _runningUiTimer.Stop(); } catch { }
            }
            _autoStressCancel.Cancel();
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
            if (t.StartsWith("To be filled", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("Default string", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void UpdateIdentity(string modelName, string serialNumber)
        {
            bool changed = false;
            if (!IsPlaceholderIdentity(modelName))
            {
                _modelName = modelName.Trim();
                if (_hwInfo != null) _hwInfo.ModelName = _modelName;
                changed = true;
            }
            if (!IsPlaceholderIdentity(serialNumber))
            {
                _serialNumber = serialNumber.Trim();
                if (_hwInfo != null) _hwInfo.SerialNumber = _serialNumber;
                changed = true;
            }
            if (changed && _subTitleBlock != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _subTitleBlock.Text = string.Format("Model: {0} | Serijski broj: {1} | Šasija: {2}",
                        _modelName, _serialNumber, (_hwInfo != null && _hwInfo.IsLaptop) ? "Laptop" : "Desktop");
                }));
            }
        }

        private void InitTestList()
        {
            // === AUTOMATSKI TESTOVI HARDVERA ===
            TestItemResult autoDisk = new TestItemResult("auto_disk", "Disk / SSD / NVMe", "SMART zdravlje, preostali vek (wear %), temperatura i brzi Read/Write benchmark");
            autoDisk.IsAutomated = true;
            autoDisk.MetricSummary = "⏳ Očitavam SMART podatke diska...";
            _testItems.Add(autoDisk);

            TestItemResult autoCpu = new TestItemResult("auto_cpu", "Procesor (CPU)", "Multi-core stres opterećenje, kontrola takta, thermal throttling-a i temperature");
            autoCpu.IsAutomated = true;
            autoCpu.MetricSummary = "⏳ Očitavam informacije procesora...";
            _testItems.Add(autoCpu);

            TestItemResult autoRam = new TestItemResult("auto_ram", "Radna memorija (RAM)", "Detekcija modula/brzine i write/read verifikacija integriteta memorije");
            autoRam.IsAutomated = true;
            autoRam.MetricSummary = "⏳ Očitavam memorijske slotove...";
            _testItems.Add(autoRam);

            TestItemResult autoGpu = new TestItemResult("auto_gpu", "Grafika (GPU)", "Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)");
            autoGpu.IsAutomated = true;
            autoGpu.MetricSummary = "⏳ Očitavam grafički adapter...";
            _testItems.Add(autoGpu);

            TestItemResult autoBat = new TestItemResult("auto_battery", "Baterija i napajanje", "Provera zdravlja baterije, kapaciteta (Design vs Full Charge) i AC punjača");
            autoBat.IsAutomated = true;
            autoBat.MetricSummary = "⏳ Očitavam status baterije...";
            _testItems.Add(autoBat);

            TestItemResult autoNet = new TestItemResult("auto_network", "Mreža (Wi-Fi / LAN & Hub)", "Provera mrežnih adaptera, link brzine, gateway-a i veze sa ServisHub serverom");
            autoNet.IsAutomated = true;
            autoNet.MetricSummary = "⏳ Proveravam mrežne adaptere i ServisHub...";
            _testItems.Add(autoNet);

            TestItemResult autoSecurity = new TestItemResult("auto_security", "BIOS, TPM 2.0 i PnP Drajveri", "Provera BIOS lozinke, Computrace statusa, TPM 2.0 čipa, OA3 licence i žutih uzvičnika u Device Manageru");
            autoSecurity.IsAutomated = true;
            autoSecurity.MetricSummary = "⏳ Proveravam BIOS, TPM 2.0 i PnP drajvere...";
            _testItems.Add(autoSecurity);

            // === PERIFERNI TESTOVI SERVISERA ===
            // Na Desktop racunaru rucni testovi periferija (ekran, tastatura, zvuk, USB...) nisu obavezni
            // i automatski dobijaju status NotApplicable (serviser ih i dalje moze rucno prikazati i pokrenuti).
            bool isLaptop = _hwInfo.IsLaptop;

            // 1. Ekran (integrisan na laptopu; na Desktopu je eksterni monitor)
            TestItemResult disp = new TestItemResult("display", "Ekran (mrtvi pikseli)", "Provera matrice ekrana (6 boja + gradijent), WMI osvetljenja i HDMI izlaza");
            if (!isLaptop)
            {
                disp.Status = TestStatus.NotApplicable;
                disp.Reason = "Nije primenljivo — Desktop računar (eksterni monitor)";
            }
            _testItems.Add(disp);

            // 2. Tastatura (integrisana na laptopu; na Desktopu je eksterna tastatura)
            TestItemResult kbd = new TestItemResult("keyboard", "Tastatura", "Ispitivanje odziva svih fizičkih tastera i provera zaglavljenih kontakata");
            if (!isLaptop)
            {
                kbd.Status = TestStatus.NotApplicable;
                kbd.Reason = "Nije primenljivo — Desktop računar (eksterna tastatura)";
            }
            _testItems.Add(kbd);

            // 3. Touchpad (razdvojeno od TrackPointa po HWT-05; bez samostalnog BIOS flaga bez HW/BOM potvrde)
            bool tpExpected = isLaptop && (_hwInfo.HasTouchpad || _hwInfo.FactoryTouchpad == true);
            bool tpNeedsBiosAction = tpExpected && (_hwInfo.TouchpadDisabledInBios || (!_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true));
            string tpDesc = "Test praćenja pokazivača u 4 ugla, levog/desnog klika i skrola sa dva prsta";
            if (tpExpected && _hwInfo.TouchpadDisabledInBios && !_hwInfo.HasTouchpad)
                tpDesc = "⚠️ UPOZORENJE: Touchpad je ONEMOGUĆEN u BIOS-u (" + _hwInfo.TouchpadBiosStatus + ")!";
            else if (tpExpected && !_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true)
                tpDesc = "⚠️ FABRIČKI PRISUTAN TOUCHPAD — Nije detektovan u Windowsu (proverite UEFI BIOS ili drajver)!";

            TestItemResult tp = new TestItemResult("touchpad", "Touchpad", tpDesc);
            if (!tpExpected)
            {
                tp.Status = TestStatus.NotApplicable;
                tp.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar (eksterne periferije)"
                    : "Uređaj fabrički nema touchpad";
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
                    tp.Reason = "Touchpad onemogućen u BIOS-u: " + _hwInfo.TouchpadBiosStatus;
                    tp.Details["bios_disabled"] = true;
                }
                else
                {
                    tp.Reason = "Touchpad potvrđen u fabričkom BOM-u, ali nije aktivan u Windowsu";
                    tp.Details["factory_missing_in_os"] = true;
                }
            }
            _testItems.Add(tp);

            // 4. TrackPoint (zasebna stavka po HWT-05)
            bool trackPointExpected = isLaptop && (_hwInfo.HasTrackPoint || _hwInfo.FactoryTrackPoint == true);
            bool trackPointNeedsBiosAction = trackPointExpected && (_hwInfo.TrackPointDisabledInBios || (!_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true));
            string trackPointDesc = "Provera pomeranja kursora crvenim TrackPoint džojstikom (4 mete) i gornjih tastera (levi, srednji/Scroll, desni)";
            if (trackPointExpected && _hwInfo.TrackPointDisabledInBios && !_hwInfo.HasTrackPoint)
                trackPointDesc = "⚠️ UPOZORENJE: TrackPoint je ONEMOGUĆEN u BIOS-u (" + _hwInfo.TrackPointBiosStatus + ")!";
            else if (trackPointExpected && !_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true)
                trackPointDesc = "⚠️ FABRIČKI PRISUTAN TRACKPOINT (" + (!string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence) ? _hwInfo.FactoryTrackPointEvidence : "BOM") + ") — Nije aktivan u Windowsu!";

            TestItemResult trackPoint = new TestItemResult("trackpoint", "TrackPoint", trackPointDesc);
            if (!trackPointExpected)
            {
                trackPoint.Status = TestStatus.NotApplicable;
                trackPoint.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar (eksterne periferije)"
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence)
                        ? ("Uređaj nema TrackPoint (" + _hwInfo.FactoryTrackPointEvidence + ")")
                        : "Uređaj nema TrackPoint džojstik");
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
                    trackPoint.Reason = "TrackPoint onemogućen u BIOS-u: " + _hwInfo.TrackPointBiosStatus;
                    trackPoint.Details["bios_disabled"] = true;
                }
                else
                {
                    trackPoint.Reason = "TrackPoint potvrđen u fabričkom BOM-u, ali nije aktivan u Windowsu";
                    trackPoint.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryTrackPointEvidence))
                        trackPoint.Details["factory_evidence"] = _hwInfo.FactoryTrackPointEvidence;
                }
            }
            _testItems.Add(trackPoint);

            // 5. Touchscreen (+ Stylus olovka)
            bool tsExpected = isLaptop && (_hwInfo.HasTouchscreen || _hwInfo.TouchscreenPnpPresent || _hwInfo.FactoryTouchscreen == true);
            bool tsNeedsBiosAction = tsExpected && (_hwInfo.TouchscreenDisabledInBios || (!_hwInfo.HasTouchscreen && _hwInfo.FactoryTouchscreen == true));
            string tsTitle = _hwInfo.HasStylusPen ? "Touchscreen + Olovka (Stylus)" : "Ekran na dodir (Touchscreen)";
            string tsDesc = _hwInfo.HasStylusPen
                ? "Prolazak kroz lavirint prstom, multitouch detekcija (+ provera Stylus olovke i pritiska)"
                : "Prolazak kroz lavirint prstom i multitouch detekcija";
            if (tsExpected && _hwInfo.TouchscreenDisabledInBios && !_hwInfo.HasTouchscreen)
                tsDesc = "⚠️ FABRIČKI PRISUTAN — Touchscreen je ONEMOGUĆEN u BIOS-u (" + _hwInfo.TouchscreenBiosStatus + ")!";
            else if (tsExpected && !_hwInfo.HasTouchscreen && (_hwInfo.FactoryTouchscreen == true || _hwInfo.TouchscreenPnpPresent))
                tsDesc = "⚠️ FABRIČKI PRISUTAN (" + (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence) ? _hwInfo.FactoryTouchscreenEvidence : "PnP") + "), ali nije aktivan u Windowsu!";

            TestItemResult ts = new TestItemResult("touchscreen", tsTitle, tsDesc);
            if (!tsExpected)
            {
                ts.Status = TestStatus.NotApplicable;
                ts.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar (eksterni monitor)"
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence)
                        ? ("Ekran nije osetljiv na dodir (" + _hwInfo.FactoryTouchscreenEvidence + ")")
                        : "Displej fabrički ne podržava unos dodirom");
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
                        ts.Reason = "Touchscreen onemogućen u BIOS-u: " + _hwInfo.TouchscreenBiosStatus;
                    }
                    else
                    {
                        ts.Reason = "Touchscreen potvrđen u OEM BOM-u, ali nije aktivan u Windowsu";
                    }
                }
            }
            else if (tsNeedsBiosAction)
            {
                ts.HasWarning = true;
                ts.Details["bios_action_recommended"] = true;
                ts.Details["bios_disabled"] = true;
                ts.Reason = "Touchscreen ima BIOS upozorenje: " + _hwInfo.TouchscreenBiosStatus;
            }
            _testItems.Add(ts);

            // 6. Zvuk (Zvučnici)
            bool audioExpected = isLaptop && (_hwInfo.HasSpeakers || _hwInfo.FactorySpeakers == true);
            TestItemResult audio = new TestItemResult("audio", "Zvuk (Levi / Desni kanal)", "Test stereo zvučnika izgovorom nasumičnih cifara na levom i desnom kanalu");
            if (!audioExpected)
            {
                audio.Status = TestStatus.NotApplicable;
                audio.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar (eksterni zvučnici)"
                    : "Uređaj nema detektovane zvučnike";
            }
            _testItems.Add(audio);

            // 7. Mikrofon (bez veštačkog fallbacka preko HasWebcam i bez samostalnog BIOS flaga)
            bool micExpected = isLaptop && (_hwInfo.HasMicrophone || _hwInfo.FactoryMicrophone == true);
            bool micNeedsBiosAction = micExpected && (_hwInfo.MicrophoneDisabledInBios || (!_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true));
            string micDesc = "VU metar nivoa glasa i automatsko preslušavanje snimka";
            if (micExpected && _hwInfo.MicrophoneDisabledInBios && !_hwInfo.HasMicrophone)
                micDesc = "⚠️ FABRIČKI PRISUTAN — Mikrofon je ONEMOGUĆEN u BIOS-u (" + _hwInfo.MicrophoneBiosStatus + ")!";
            else if (micExpected && !_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true)
                micDesc = "⚠️ FABRIČKI OČEKIVAN MIKROFON — Nije detektovan aktivan capture uređaj u Windowsu!";

            TestItemResult mic = new TestItemResult("mic", "Mikrofon", micDesc);
            if (!micExpected)
            {
                mic.Status = TestStatus.NotApplicable;
                mic.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar"
                    : "Uređaj nema ugrađeni mikrofon";
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
                    mic.Reason = "Mikrofon onemogućen u BIOS-u: " + _hwInfo.MicrophoneBiosStatus;
                }
                if (!_hwInfo.HasMicrophone && _hwInfo.FactoryMicrophone == true)
                {
                    mic.Details["factory_missing_in_os"] = true;
                    if (string.IsNullOrEmpty(mic.Reason))
                        mic.Reason = "Mikrofon potvrđen u OEM BOM-u, ali nije detektovan u Windowsu";
                }
            }
            _testItems.Add(mic);

            // 8. Web kamera (RGB QR kod — razdvojeno od IR kamere po HWT-06)
            bool camExpected = isLaptop && (_hwInfo.HasWebcam || _hwInfo.WebcamPnpPresent || _hwInfo.FactoryWebcam == true);
            bool camNeedsBiosAction = camExpected && (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasWebcam && _hwInfo.FactoryWebcam == true));
            string camDesc = "Očitavanje otštampanog QR koda RGB web kamerom (WIS-CAMTEST-v1)";
            if (camExpected && _hwInfo.CameraDisabledInBios && !_hwInfo.HasWebcam)
                camDesc = "⚠️ FABRIČKI PRISUTNA — Web kamera je ONEMOGUĆENA u BIOS-u (" + _hwInfo.CameraBiosStatus + ")!";
            else if (camExpected && !_hwInfo.HasWebcam && (_hwInfo.FactoryWebcam == true || _hwInfo.WebcamPnpPresent))
                camDesc = "⚠️ FABRIČKI PRISUTNA KAMERA (" + (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence) ? _hwInfo.FactoryWebcamEvidence : "PnP") + "), ali nije aktivna u Windowsu!";

            TestItemResult cam = new TestItemResult("camera", "Web kamera (QR kod)", camDesc);
            if (!camExpected)
            {
                cam.Status = TestStatus.NotApplicable;
                cam.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar"
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence)
                        ? ("Uređaj fabrički nema web kameru (" + _hwInfo.FactoryWebcamEvidence + ")")
                        : "Web kamera nije pronađena ni u OS-u ni u fabričkoj konfiguraciji");
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
                    cam.Reason = "Web kamera onemogućena u BIOS-u: " + _hwInfo.CameraBiosStatus;
                }
                if (!_hwInfo.HasWebcam && _hwInfo.FactoryWebcam == true)
                {
                    cam.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence))
                        cam.Details["factory_evidence"] = _hwInfo.FactoryWebcamEvidence;
                    if (string.IsNullOrEmpty(cam.Reason))
                        cam.Reason = "Web kamera potvrđena u OEM BOM-u, ali nije aktivna u Windowsu";
                }
            }
            _testItems.Add(cam);

            // 9. IR kamera (Windows Hello — zasebna stavka po HWT-06)
            bool irCamExpected = isLaptop && (_hwInfo.HasIrCamera || _hwInfo.FactoryIrCamera == true);
            bool irCamNeedsBiosAction = irCamExpected && (_hwInfo.CameraDisabledInBios || (!_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true));
            string irCamDesc = "Provera IR senzora kamere za Windows Hello (prepoznavanje lica)";
            if (irCamExpected && _hwInfo.CameraDisabledInBios && !_hwInfo.HasIrCamera)
                irCamDesc = "⚠️ FABRIČKI PRISUTNA — IR kamera je ONEMOGUĆENA u BIOS-u (" + _hwInfo.CameraBiosStatus + ")!";
            else if (irCamExpected && !_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true)
                irCamDesc = "⚠️ FABRIČKI PRISUTNA IR KAMERA (" + (!string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence) ? _hwInfo.FactoryIrCameraEvidence : "BOM") + "), ali nije aktivna u Windowsu!";

            TestItemResult irCam = new TestItemResult("ir_camera", "IR kamera (Windows Hello)", irCamDesc);
            if (!irCamExpected)
            {
                irCam.Status = TestStatus.NotApplicable;
                irCam.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar"
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence)
                        ? ("Uređaj fabrički nema IR kameru (" + _hwInfo.FactoryIrCameraEvidence + ")")
                        : "IR kamera nije pronađena ni u OS-u ni u fabričkoj konfiguraciji");
            }
            else if (irCamNeedsBiosAction)
            {
                irCam.HasWarning = true;
                irCam.Details["bios_action_recommended"] = true;
                if (_hwInfo.CameraDisabledInBios)
                {
                    irCam.Details["bios_disabled"] = true;
                    irCam.Reason = "IR kamera onemogućena u BIOS-u: " + _hwInfo.CameraBiosStatus;
                }
                if (!_hwInfo.HasIrCamera && _hwInfo.FactoryIrCamera == true)
                {
                    irCam.Details["factory_missing_in_os"] = true;
                    if (!string.IsNullOrEmpty(_hwInfo.FactoryIrCameraEvidence))
                        irCam.Details["factory_evidence"] = _hwInfo.FactoryIrCameraEvidence;
                    if (string.IsNullOrEmpty(irCam.Reason))
                        irCam.Reason = "IR kamera potvrđena u OEM BOM-u, ali nije aktivna u Windowsu";
                }
            }
            _testItems.Add(irCam);

            // 10. USB portovi (integrisani na laptopu; na Desktopu nisu obavezni u Fazi 5)
            TestItemResult usb = new TestItemResult("usb", "USB portovi", "Verifikacija svih spoljnih USB portova ubacivanjem fleš diska");
            if (!isLaptop)
            {
                usb.Status = TestStatus.NotApplicable;
                usb.Reason = "Nije primenljivo — Desktop računar";
            }
            _testItems.Add(usb);

            // 11. Čitač otiska prsta (OS + OEM BOM po HWT-04; bez !FactoryBomLoaded i samostalnog FingerprintDisabledInBios)
            bool fpExpected = isLaptop && (_hwInfo.HasFingerprint || _hwInfo.FactoryFingerprint == true);
            bool fpNeedsBiosAction = fpExpected && (_hwInfo.FingerprintDisabledInBios || (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true));
            string fpDesc = "Provera odziva biometrijskog senzora otiska prsta preko Windows Biometric API-ja";
            if (fpExpected && _hwInfo.FingerprintDisabledInBios)
                fpDesc = "⚠️ UPOZORENJE: Uređaj je ONEMOGUĆEN u podešavanjima BIOS-a (" + _hwInfo.FingerprintBiosStatus + ")!";
            else if (fpExpected && !_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
                fpDesc = "⚠️ FABRIČKI PRISUTAN U OEM BOM-U (" + _hwInfo.FactoryFingerprintEvidence + "), ali nije detektovan u Windowsu!";

            TestItemResult fp = new TestItemResult("fingerprint", "Čitač otiska prsta", fpDesc);
            if (!fpExpected)
            {
                fp.Status = TestStatus.NotApplicable;
                fp.Reason = !isLaptop
                    ? "Nije primenljivo — Desktop računar"
                    : (!string.IsNullOrEmpty(_hwInfo.FactoryFingerprintEvidence)
                        ? ("Čitač otiska prsta nije fabrički ugrađen (" + _hwInfo.FactoryFingerprintEvidence + ")")
                        : "Čitač otiska prsta nije pronađen ni u OS-u ni u fabričkoj specifikaciji");
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
                    fp.Reason = "Čitač otiska prsta je onemogućen u BIOS-u: " + _hwInfo.FingerprintBiosStatus;
                    fp.Details["bios_disabled"] = true;
                    fp.Details["bios_setting"] = _hwInfo.FingerprintBiosStatus;
                }
                if (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
                {
                    fp.Details["factory_missing_in_os"] = true;
                    fp.Details["factory_evidence"] = _hwInfo.FactoryFingerprintEvidence;
                    if (string.IsNullOrEmpty(fp.Reason))
                        fp.Reason = "Čitač otiska prsta potvrđen u OEM BOM-u, ali nije detektovan u Windowsu";
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
                string srcLabel = !string.IsNullOrEmpty(_hwInfo.FactorySummary) ? _hwInfo.FactorySummary : "Lokalna detekcija";
                if (hiddenCount > 0)
                {
                    _factoryBannerText.Text = string.Format(
                        "🏭 Fabrička provera ({0}): Prikazano {1} testova | Sakriveno (nije prisutno na uređaju): {2}",
                        srcLabel, shownCount, string.Join(", ", hiddenTitles.ToArray()));
                }
                else
                {
                    _factoryBannerText.Text = string.Format(
                        "🏭 Fabrička provera ({0}): Svi testovi ({1}) su primenjivi za ovaj uređaj",
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
                        ? string.Format("🙈 Sakrij odsutne ({0})", hiddenCount)
                        : string.Format("👁 Prikaži sakrivene ({0})", hiddenCount);
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
            sp.Children.Add(new TextBlock
            {
                Text = "⚡ AUTOMATSKI TESTOVI",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            });
            _autoSectionStatus = new TextBlock
            {
                Text = "Pasivno (Baterija, Mreža, BIOS/TPM/PnP, SMART) + stres (Disk, RAM, CPU, GPU)",
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
                Content = "⚡ Stres paket",
                ToolTip = "Pokreni sekvencijalni automatski stres paket (Disk, RAM, CPU, GPU)",
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
            sp.Children.Add(new TextBlock
            {
                Text = "🔧 PERIFERNI TESTOVI SERVISERA",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 235))
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Interaktivni testovi fizičkih periferija i ulazno/izlaznih uređaja",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            g.Children.Add(sp);

            _manualSectionCountText = new TextBlock
            {
                Text = "0/0 završeno",
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
                    Content = "Detalji",
                    ToolTip = "Otvori detaljan izveštaj testa",
                    Background = new SolidColorBrush(Color.FromRgb(50, 56, 70)),
                    Foreground = Brushes.White,
                    FontSize = 10.5,
                    Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(0, 0, 5, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                btnDetails.Click += (s, e) => OpenAutoDetails(t);
                spBtns.Children.Add(btnDetails);
            }

            Button btnRun = new Button
            {
                Content = "▶",
                ToolTip = "Pokreni ovaj test",
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
                    ToolTip = "Restartuj računar u UEFI BIOS podešavanja da biste uključili uređaj (WIS nastavlja gde je stao)",
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
                        tbStatus.Text = "ČEKA";
                        break;
                    case TestStatus.Running:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                        tbStatus.Text = "U TOKU";
                        break;
                    case TestStatus.Pass:
                        if (t.HasWarning)
                        {
                            statusBorder.Background = new SolidColorBrush(Color.FromRgb(217, 130, 15));
                            tbStatus.Text = "UPOZ.";
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
                    MessageBox.Show("Automatski stres paket je već u toku. Sačekajte da se završi.",
                        "Test u toku", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    dlg = tpDlg;
                    break;
                case "trackpoint":
                    bool trkHint = (_hwInfo.TrackPointDisabledInBios || (!_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true));
                    dlg = new TouchpadTestWindow(_hwInfo, PointingTestMode.TrackPointOnly, RequestUefiBiosReboot, trkHint);
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
                    dlg = new UsbPortTestWindow(_modelName, pCount, _profileClient);
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
            HwTestLogger.Info("TEST_END", string.Format("Završen '{0}' -> Status={1} | Trajanje={2}s | Reason='{3}' | Details={4}",
                t.Id, t.Status, t.DurationSeconds, t.Reason ?? "", SimpleJson.Serialize(t.Details ?? new Dictionary<string, object>())));

            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
            UpdateSummaryBadge();
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
                    : "GPU 3D test ne može da se pokrene: nedostaje grafički drajver.";
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
        }

        private void ApplyResult(TestItemResult target, TestItemResult source)
        {
            if (source == null) return;
            target.Status = source.Status;
            target.HasWarning = source.HasWarning;
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
            if (_passiveChecksStarted) return;
            _passiveChecksStarted = true;
            Thread th = new Thread(() =>
            {
                TestItemResult[] passiveResults = new TestItemResult[7];
                try { passiveResults[0] = _autoEngine.RunBatteryCheck(); } catch { }
                Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[0], true)));

                try { passiveResults[1] = _autoEngine.RunNetworkCheck(_profileClient); } catch { }
                Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[1], true)));

                try { passiveResults[2] = _autoEngine.RunDiskPassiveCheck(); } catch { }
                Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[2], false)));

                try { passiveResults[3] = _autoEngine.RunRamPassiveSummary(); } catch { }
                Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[3], false)));

                try { passiveResults[4] = _autoEngine.RunCpuPassiveSummary(); } catch { }
                Dispatcher.BeginInvoke(new Action(() => ApplyAutoPassiveResult(passiveResults[4], false)));

                try { passiveResults[5] = _autoEngine.RunGpuPassiveSummary(); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyAutoPassiveResult(passiveResults[5], false);
                    RefreshCardVisibility();
                    UpdateSummaryBadge();
                }));

                try { passiveResults[6] = _autoEngine.RunSecurityAndFirmwareCheck(_hwInfo); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyAutoPassiveResult(passiveResults[6], true);
                    RefreshCardVisibility();
                    UpdateSummaryBadge();
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        private void ApplyAutoPassiveResult(TestItemResult source, bool isCompletePassiveTest)
        {
            if (source == null) return;
            TestItemResult target = FindItemById(source.Id);
            if (target == null) return;

            // Ne prepisuj ako je serviser već ručno pokrenuo/završio test
            if (target.Status != TestStatus.Pending) return;

            if (isCompletePassiveTest || source.Status == TestStatus.Fail || source.Status == TestStatus.NotApplicable)
            {
                ApplyResult(target, source);
                HwTestLogger.Info("AUTO_PASSIVE", string.Format("{0} -> {1} (Warn={2}) | {3}",
                    target.Id, target.Status, target.HasWarning, target.MetricSummary ?? ""));
            }
            else
            {
                target.HasWarning = source.HasWarning;
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
                                _autoSectionStatus.Text = string.Format("🔄 Korak {0}/4: {1} u toku...", stepNum, item.Title);
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
                                    _autoSectionStatus.Text = "🔄 Korak 4/4: GPU 3D hardverski stres test...";
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
                        if (_autoSectionStatus != null)
                        {
                            _autoSectionStatus.Text = _autoStressCancel.IsCancelled
                                ? "⏹ Automatski stres paket je zaustavljen."
                                : "✓ Automatski stres paket je završen. Pregledajte rezultate i nastavite periferne testove.";
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
                    }));
                }
            });
            th.IsBackground = true;
            th.Start();
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
                _btnRunAutoSuite.Content = busy ? "⏳ Stres u toku..." : "⚡ Stres paket";
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
        }

        private void RunSequentialTests()
        {
            if (_autoStressRunning)
            {
                MessageBox.Show(
                    "Automatski stres testovi su već u toku. Sačekajte da se završe pre sekvencijalnog pokretanja.",
                    "Stres test u toku",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
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
                _manualSectionCountText.Text = string.Format("{0}/{1} završeno", manualDone, manualTotal);
            }

            Brush badgeBg;
            if (fail > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                _summaryBadge.Text = string.Format("✗ {0} FAIL | {1} PASS | {2} SKIP", fail, pass, skip);
            }
            else if (running > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                _summaryBadge.Text = string.Format("▶ {0} U TOKU | {1}/{2} završeno", running, completed, applicable);
            }
            else if (pending > 0)
            {
                badgeBg = new SolidColorBrush(Color.FromRgb(217, 150, 15));
                _summaryBadge.Text = (na > 0)
                    ? string.Format("⏳ {0}/{1} ({2} odsutno)", completed, applicable, na)
                    : string.Format("⏳ {0}/{1} završeno", completed, applicable);
            }
            else
            {
                badgeBg = warn > 0
                    ? new SolidColorBrush(Color.FromRgb(217, 130, 15))
                    : new SolidColorBrush(Color.FromRgb(35, 134, 54));
                _summaryBadge.Text = warn > 0
                    ? string.Format("! ZAVRŠENO ({0} PASS, {1} UPOZ., {2} SKIP)", pass, warn, skip)
                    : string.Format("✓ ZAVRŠENO ({0} PASS, {1} SKIP)", pass, skip);
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
                    _btnFinish.ToolTip = "Završi testiranje i sačuvaj konačan hardverski izveštaj";
                }
                else if (_autoStressRunning)
                {
                    _btnFinish.Opacity = 0.55;
                    _btnFinish.ToolTip = "Završetak je zaključan dok traje automatski stres paket.";
                }
                else
                {
                    _btnFinish.Opacity = 0.55;
                    _btnFinish.ToolTip = string.Format(
                        "Završetak je zaključan dok svi primenljivi testovi ne dobiju PASS ili FAIL (preostalo: {0}).",
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

        private void RunSequentialPeripheralTests()
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                if (_uefiBiosRebootRequested || _uefiBiosPromptOpen || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                TestItemResult t = _testItems[i];
                if (!t.IsAutomated && t.Status == TestStatus.Pending)
                {
                    RunSingleTest(t);
                    if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) break;
                }
            }
        }

        public void ForceCompletePhase5SkipPending()
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

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;
                Window activeDlg = _activePeripheralDialog;
                if (activeDlg != null)
                {
                    try { activeDlg.Close(); } catch { }
                }
                SaveCurrentReport(false, true, "Preskočeno u okviru Faze 5", true);
            }));
        }

        public void StartPhase5FullWorkflow(
            Action<string, string, string, string> onStepProgress,
            Action<HwTestRunResult, int, float, float, bool, bool> onPhase5Finished)
        {
            TestItemResult existingCpu = FindItemById("auto_cpu");
            bool preserveRestoredThermalMetrics = _checkpointRestored && existingCpu != null &&
                                                  _restoredTestIds.Contains("auto_cpu") && IsFinalTestStatus(existingCpu.Status);

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

            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartPassiveAutoChecks();
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = _checkpointRestored
                        ? string.Format("♻️ Nastavak posle UEFI BIOS restarta — restaurirano {0}/{1} već završenih testova. Nastavljam Fazu 5...", _checkpointRestoredCount, _checkpointTotalApplicable)
                        : "🔄 Faza 5 u toku: pokrećem automatsku dijagnostiku i stres test...";
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
                    // 1. Automatska provera (baterija, mreža, sigurnost)
                    EmitPhase5Step("P5.AutoDiag", "Automatska provera sistema", "RUNNING", "");
                    string[] passiveIds = new string[] { "auto_battery", "auto_network", "auto_security" };
                    for (int i = 0; i < passiveIds.Length; i++)
                    {
                        if (_uefiBiosRebootRequested || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                        string pid = passiveIds[i];
                        TestItemResult item = FindItemById(pid);
                        if (item != null && !IsFinalTestStatus(item.Status))
                        {
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
                                }));
                                if (r.HasWarning || r.Status == TestStatus.Fail) autoDiagWarn = true;
                            }
                        }
                        else if (item != null && (item.HasWarning || item.Status == TestStatus.Fail))
                        {
                            autoDiagWarn = true;
                        }
                    }
                    if (!_uefiBiosRebootRequested)
                    {
                        EmitPhase5Step("P5.AutoDiag", "Automatska provera sistema",
                            _autoStressCancel.IsCancelled ? "SKIPPED_USER" : (autoDiagWarn ? "WARN" : "OK"), "");
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
                            bool restoredOrNonFailDone = IsFinalTestStatus(si.Status) &&
                                (_restoredTestIds.Contains(si.Id) || si.Id != "auto_gpu" || si.Status != TestStatus.Fail);
                            if (!restoredOrNonFailDone)
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
                                EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)", "WARN",
                                    string.Format("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", _phase5PeakCpuTemp));
                            }
                            else
                            {
                                EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)",
                                    stressWarn ? "WARN" : "OK", "");
                            }
                        }
                        else
                        {
                            EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)", "RUNNING", "");
                            string[] seq = new string[] { "auto_disk", "auto_ram", "auto_cpu" };
                            for (int i = 0; i < seq.Length; i++)
                            {
                                if (_uefiBiosRebootRequested || _autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                                string id = seq[i];
                                TestItemResult item = FindItemById(id);
                                if (item == null || IsFinalTestStatus(item.Status))
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
                                        _autoSectionStatus.Text = string.Format("🔄 Faza 5 ({0}/4): {1} u toku...", stepNum, item.Title);
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
                                }));

                                if (_phase5ThermalCritical) break;
                            }

                            if (!_uefiBiosRebootRequested && _phase5ThermalCritical)
                            {
                                _phase5ThermalNote = string.Format(
                                    "NAPOMENA: Procesor je dostigao kritičnu temperaturu ({0:F1}°C >= 95°C) tokom stres testa — potrebno čišćenje hladnjaka i zamena termalne paste.",
                                    _phase5PeakCpuTemp);
                                HwTestLogger.Warn("THERMAL_NOTE", _phase5ThermalNote);
                                EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)", "WARN",
                                    string.Format("NAPOMENA: Pregrevanje CPU ({0:F1}°C)", _phase5PeakCpuTemp));

                                TestItemResult gpuSkipItem = FindItemById("auto_gpu");
                                if (gpuSkipItem != null && gpuSkipItem.Status != TestStatus.Skip && gpuSkipItem.Status != TestStatus.NotApplicable)
                                {
                                    Dispatcher.Invoke(new Action(() =>
                                    {
                                        gpuSkipItem.Status = TestStatus.Skip;
                                        gpuSkipItem.HasWarning = true;
                                        gpuSkipItem.Reason = string.Format("Preskočeno radi zaštite hardvera jer je CPU dostigao {0:F1}°C (>= 95°C)", _phase5PeakCpuTemp);
                                        gpuSkipItem.MetricSummary = "Preskočeno (CPU >= 95°C)";
                                        Border gc;
                                        if (_cardMap.TryGetValue(gpuSkipItem.Id, out gc)) UpdateCardVisual(gc, gpuSkipItem);
                                        UpdateSummaryBadge();
                                    }));
                                }

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (_autoSectionStatus != null)
                                        _autoSectionStatus.Text = string.Format("⚠️ NAPOMENA: CPU se pregrevao ({0:F1}°C >= 95°C) — opterećenje zaustavljeno radi zaštite, nastavljam ostale testove...", _phase5PeakCpuTemp);
                                }));
                            }

                            if (!_uefiBiosRebootRequested && !_autoStressCancel.IsCancelled && _phase5WorkflowActive && !_phase5ThermalCritical)
                            {
                                TestItemResult gpuItem = FindItemById("auto_gpu");
                                if (gpuItem != null)
                                {
                                    bool gpuDoneFromCheckpoint = IsFinalTestStatus(gpuItem.Status) &&
                                        (_restoredTestIds.Contains("auto_gpu") || gpuItem.Status != TestStatus.Fail);
                                    if (gpuDoneFromCheckpoint)
                                    {
                                        if (gpuItem.HasWarning || gpuItem.Status == TestStatus.Fail) stressWarn = true;
                                    }
                                    else if (gpuItem.Status != TestStatus.Skip && gpuItem.Status != TestStatus.NotApplicable)
                                    {
                                        Dispatcher.Invoke(new Action(() =>
                                        {
                                            if (_autoSectionStatus != null)
                                                _autoSectionStatus.Text = "🔄 Faza 5 (4/4): GPU 3D hardverski stres test...";
                                            RunGpuStressTest(gpuItem);
                                            if (gpuItem.HasWarning || gpuItem.Status == TestStatus.Fail) stressWarn = true;
                                        }));
                                    }
                                }
                            }

                            if (!_uefiBiosRebootRequested && !_phase5ThermalCritical)
                            {
                                EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)",
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
                        SaveCurrentReport(false, true, "Preskočeno od strane servisera", true);
                    }));
                    return;
                }

                // 3. Interaktivni periferijski testovi
                bool hasPendingManual = false;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    TestItemResult t = _testItems[i];
                    if (!t.IsAutomated && t.Status == TestStatus.Pending)
                    {
                        hasPendingManual = true;
                        break;
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

                        string skipPeriphReason = (_hwInfo != null && !_hwInfo.IsLaptop)
                            ? "Desktop računar — ručni testovi periferija nisu potrebni"
                            : "Nema primenljivih periferijskih testova";
                        EmitPhase5Step("P5.Peripherals", "Interaktivni testovi periferija", "SKIPPED_DONE", skipPeriphReason);
                        if (_autoSectionStatus != null)
                        {
                            _autoSectionStatus.Text = "✓ " + skipPeriphReason + " — automatski završavam Fazu 5 i snimam izveštaj.";
                        }
                        UpdateSummaryBadge();
                        SaveCurrentReport(false, false, "", false);
                    }));
                    return;
                }

                EmitPhase5Step("P5.Peripherals", "Interaktivni testovi periferija", "RUNNING", "");
                _waitingForManualPeripherals = true;
                _autoTestsFinishedUtc = DateTime.UtcNow;
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
                    if (_autoSectionStatus != null)
                    {
                        _autoSectionStatus.Text = "✓ Automatski stres završen — pokrećem interaktivne testove periferija...";
                    }
                    RunSequentialPeripheralTests();

                    if (_uefiBiosRebootRequested || _uefiBiosPromptOpen) return;

                    bool alreadyNotified;
                    lock (_phase5Lock) { alreadyNotified = _phase5CompletedNotified; }
                    if (alreadyNotified) return;

                    int remainingPending = 0;
                    int periphWarnOrFail = 0;
                    for (int i = 0; i < _testItems.Count; i++)
                    {
                        TestItemResult t = _testItems[i];
                        if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                            remainingPending++;
                        if (!t.IsAutomated && (t.Status == TestStatus.Fail || t.HasWarning || t.Status == TestStatus.Skip))
                            periphWarnOrFail++;
                    }

                    if (remainingPending == 0)
                    {
                        EmitPhase5Step("P5.Peripherals", "Interaktivni testovi periferija", periphWarnOrFail > 0 ? "WARN" : "OK", "");
                        SaveCurrentReport(false, false, "", false);
                    }
                    else
                    {
                        if (_autoSectionStatus != null)
                        {
                            _autoSectionStatus.Text = string.Format(
                                "⏳ Preostalo {0} testova. Dovršite preostale testove da biste otključali završetak i čuvanje izveštaja.",
                                remainingPending);
                        }
                    }
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
                    "Automatski stres testovi su još uvek u toku.\n\nSačekajte da se stres test završi do kraja pre čuvanja izveštaja.",
                    "Stres test u toku",
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
                    string.Format(
                        "Nije moguće sačuvati izveštaj dok svi primenljivi testovi nisu završeni (PASS ili FAIL).\n\nPreostali testovi ({0}):\n{1}",
                        unfinished.Count,
                        string.Join("\n", unfinished.ToArray())),
                    "Neizvršeni testovi",
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
                string skipReason = string.IsNullOrEmpty(defaultSkipReason) ? "Preskočeno u okviru Faze 5" : defaultSkipReason;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    TestItemResult t = _testItems[i];
                    if (t.Status == TestStatus.Pending || t.Status == TestStatus.Running)
                    {
                        t.Status = TestStatus.Skip;
                        if (string.IsNullOrEmpty(t.Reason))
                            t.Reason = skipReason;
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

            // Upis u JSON fajl (lokalno + kopija pored exe-a u hwtest-logs/)
            var reportPayload = new Dictionary<string, object>
            {
                { "schema", runResult.Schema },
                { "started", runResult.Started },
                { "finished", runResult.Finished },
                { "model", runResult.Model },
                { "serial", runResult.Serial },
                { "app_version", runResult.AppVersion },
                { "summary", runResult.Summary },
                { "tests", runResult.Tests }
            };
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
                MessageBox.Show("Nije moguće upisati izveštaj ni u primarnu ni u TEMP putanju.", "Upozorenje", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_isEmbedded)
            {
                if (_phase5WorkflowActive)
                {
                    EmitPhase5Step("P5.Peripherals", "Interaktivni testovi periferija",
                        isCancelled ? "SKIPPED_USER" : ((fail > 0 || warn > 0 || skip > 0) ? "WARN" : "OK"), "");
                }
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = string.Format("✓ Hardverski izveštaj sačuvan ({0}: {1} PASS, {2} SKIP, {3} FAIL).", overall, pass, skip, fail);
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

        public void SaveCheckpoint(string rebootReasonTestId)
        {
            string reasonId = string.IsNullOrEmpty(rebootReasonTestId) ? "" : rebootReasonTestId.Trim();
            List<object> completedTests = new List<object>();

            for (int i = 0; i < _testItems.Count; i++)
            {
                TestItemResult t = _testItems[i];
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

            Dictionary<string, object> phase5Metrics = new Dictionary<string, object>
            {
                { "cpu_duration_sec", _phase5CpuDurationSec },
                { "peak_cpu_temp", (double)_phase5PeakCpuTemp },
                { "max_cpu_throttle", (double)_phase5MaxCpuThrottle },
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

        private void TryRestoreCheckpoint()
        {
            string cpPath = GetCheckpointFilePath();
            if (string.IsNullOrEmpty(cpPath) || !File.Exists(cpPath))
                return;

            try
            {
                string json = File.ReadAllText(cpPath, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return;

                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                IDictionary<string, object> root = ser.DeserializeObject(json) as IDictionary<string, object>;
                if (root == null) return;

                string schema = ReadCpString(root, "schema");
                if (!string.Equals(schema, "wis.hwtest.checkpoint.v1", StringComparison.Ordinal))
                {
                    HwTestLogger.Warn("CHECKPOINT_RESTORE", "Ignorišem checkpoint nepoznate šeme: " + schema);
                    return;
                }

                string savedUtcStr = ReadCpString(root, "saved_utc");
                DateTime savedUtc;
                if (!DateTime.TryParse(savedUtcStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out savedUtc))
                {
                    HwTestLogger.Warn("CHECKPOINT_RESTORE", "Neispravan saved_utc u checkpointu — ignorišem.");
                    return;
                }

                double ageHours = (DateTime.UtcNow - savedUtc).TotalHours;
                if (ageHours >= 24.0 || ageHours < -1.0)
                {
                    HwTestLogger.Warn("CHECKPOINT_RESTORE", string.Format(
                        "Checkpoint je zastareo ({0:F1}h) — ignorišem.", ageHours));
                    return;
                }

                string cpSerial = ReadCpString(root, "serial");
                if (!IsPlaceholderIdentity(cpSerial) && !IsPlaceholderIdentity(_serialNumber) &&
                    !string.Equals(cpSerial.Trim(), _serialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    HwTestLogger.Warn("CHECKPOINT_RESTORE", string.Format(
                        "Serial neusklađen (checkpoint='{0}', uređaj='{1}') — ignorišem.",
                        cpSerial, _serialNumber));
                    return;
                }

                string rebootReasonId = ReadCpString(root, "reboot_reason_test_id");

                object metricsObj;
                if (root.TryGetValue("phase5_metrics", out metricsObj))
                {
                    IDictionary<string, object> mDict = metricsObj as IDictionary<string, object>;
                    if (mDict != null)
                    {
                        _phase5CpuDurationSec = ReadCpInt(mDict, "cpu_duration_sec");
                        _phase5PeakCpuTemp = ReadCpFloat(mDict, "peak_cpu_temp");
                        _phase5MaxCpuThrottle = ReadCpFloat(mDict, "max_cpu_throttle");
                        _phase5ThermalCritical = ReadCpBool(mDict, "thermal_critical");
                        _phase5ThermalNote = ReadCpString(mDict, "thermal_note");
                    }
                }

                int restoredCount = 0;
                object testsObj;
                if (root.TryGetValue("completed_tests", out testsObj) && testsObj != null)
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
                            if (string.Equals(id, "auto_security", StringComparison.OrdinalIgnoreCase))
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
                            item.Reason = ReadCpString(td, "reason");
                            object dObj;
                            if (td.TryGetValue("details", out dObj))
                            {
                                item.Details = NormalizeCpDetails(dObj as IDictionary<string, object>);
                            }
                            _restoredTestIds.Add(item.Id);
                            restoredCount++;
                        }
                    }
                }

                int applicableTotal = 0;
                for (int i = 0; i < _testItems.Count; i++)
                {
                    if (_testItems[i].Status != TestStatus.NotApplicable)
                        applicableTotal++;
                }

                _checkpointRestoredCount = restoredCount;
                _checkpointTotalApplicable = applicableTotal;
                _checkpointRestored = (restoredCount > 0 || !string.IsNullOrEmpty(rebootReasonId));

                HwTestLogger.Info("CHECKPOINT_RESTORE", string.Format(
                    "Restaurirano {0}/{1} već završenih testova iz checkpointa '{2}' (reboot_reason='{3}').",
                    restoredCount, applicableTotal, cpPath, rebootReasonId));
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("CHECKPOINT_RESTORE", "Greška pri čitanju checkpointa: " + ex.Message);
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
                    string.Format(
                        "Računar će biti restartovan direktno u UEFI BIOS podešavanja kako biste uključili uređaj za test '{0}'.\n\n" +
                        "• Svi dosadašnji završeni rezultati i progres Faze 5 biće sačuvani u checkpoint fajlu.\n" +
                        "• Po izlasku iz UEFI BIOS-a, Windows i WIS će se automatski podići i nastaviti tačno gde ste stali.\n" +
                        "• Tipični BIOS tasteri: HP: F10, Lenovo: F1, Dell: F2.\n\n" +
                        "Da li želite da odmah restartujete računar u UEFI BIOS?",
                        testTitle),
                    "Restart u UEFI BIOS",
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
                    "Direktan ulazak u UEFI firmware (/r /fw) nije podržan na ovom računaru.\n\n" +
                    "Računar će se sada restartovati — pritisnite BIOS taster odmah pri paljenju:\n" +
                    "HP: F10 | Lenovo: F1 | Dell: F2",
                    "Ručni ulazak u BIOS",
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
