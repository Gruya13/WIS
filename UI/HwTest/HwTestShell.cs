using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private readonly TextBlock _summaryBadge;
        private readonly StackPanel _cardsPanel;
        private readonly Stopwatch _runStopwatch;
        private bool _showNotApplicable = false;
        private TextBlock _factoryBannerText;
        private Button _btnToggleHidden;

        private readonly AutoDiagnosticsEngine _autoEngine = new AutoDiagnosticsEngine();
        private volatile bool _autoStressRunning = false;
        private readonly CancelFlag _autoStressCancel = new CancelFlag();
        private Button _btnRunAutoSuite;
        private TextBlock _autoSectionStatus;

        // Embedded i Phase 5 orkestracija (WIS v3.0.0)
        private Grid _rootGrid;
        private TextBlock _subTitleBlock;
        private Button _btnFinish;
        private bool _isEmbedded = false;
        private Window _hostWindow = null;
        private Action<HwTestRunResult> _onReportSavedCallback = null;
        private bool _passiveChecksStarted = false;
        private volatile bool _phase5WorkflowActive = false;
        private Action<string, string, string, string> _phase5StepCallback = null;
        private Action<HwTestRunResult, int, float, float, bool, bool> _phase5FinishedCallback = null;
        private readonly object _phase5Lock = new object();
        private bool _phase5CompletedNotified = false;
        private int _phase5CpuDurationSec = 0;
        private float _phase5PeakCpuTemp = 0f;
        private float _phase5MaxCpuThrottle = 0f;
        private bool _phase5ThermalCritical = false;

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
            _profileClient = existingClient ?? new ModelProfileClient();
            _hwInfo = HardwareProbe.DetectHardware();

            if (!string.IsNullOrEmpty(manufacturer) && string.IsNullOrEmpty(_hwInfo.Manufacturer))
                _hwInfo.Manufacturer = manufacturer;
            if (!string.IsNullOrEmpty(sku) && string.IsNullOrEmpty(_hwInfo.SystemSkuNumber))
                _hwInfo.SystemSkuNumber = sku;

            bool emptyModelArg = string.IsNullOrEmpty(modelName) || modelName.Equals("Nepoznat model", StringComparison.OrdinalIgnoreCase);
            _modelName = (!emptyModelArg)
                ? modelName
                : (!string.IsNullOrEmpty(_hwInfo.ModelName) ? _hwInfo.ModelName : "Nepoznat model");

            bool emptySnArg = string.IsNullOrEmpty(serialNumber) || serialNumber.StartsWith("Nepoznat", StringComparison.OrdinalIgnoreCase);
            _serialNumber = (!emptySnArg)
                ? serialNumber
                : (!string.IsNullOrEmpty(_hwInfo.SerialNumber) ? _hwInfo.SerialNumber : "Nepoznat serijski broj");

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
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            SourceInitialized += (s, e) => NativeMethods.EnableDarkTitleBar(this);

            // Inicijalizacija testova (prikazuju se samo prisutni / fabrički očekivani uređaji)
            InitTestList();

            // Izgradnja UI-ja
            Grid root = new Grid();
            _rootGrid = root;
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(85) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(75) });

            // 1. Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            Grid hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel spH = new StackPanel();
            TextBlock title = new TextBlock
            {
                Text = "🧪 WIS HARDVERSKA DIJAGNOSTIKA I TESTIRANJE",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(title);

            TextBlock sub = new TextBlock
            {
                Text = string.Format("Model: {0} | Serijski broj: {1} | Šasija: {2}",
                    _modelName, _serialNumber, _hwInfo.IsLaptop ? "Laptop" : "Desktop"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            _subTitleBlock = sub;
            spH.Children.Add(sub);
            hg.Children.Add(spH);

            _summaryBadge = new TextBlock
            {
                Text = "⏳ Čeka pokretanje",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 72)),
                Padding = new Thickness(14, 8, 14, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_summaryBadge, 1);
            hg.Children.Add(_summaryBadge);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // 1b. Info traka o fabričkoj detekciji i sakrivenim N/A testovima
            Border factoryBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 31, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 50, 60)),
                BorderThickness = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(25, 8, 25, 8)
            };
            Grid fbg = new Grid();
            fbg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fbg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _factoryBannerText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 195)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            fbg.Children.Add(_factoryBannerText);

            _btnToggleHidden = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb(50, 56, 68)),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 230)),
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(15, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _btnToggleHidden.Click += (s, e) => {
                _showNotApplicable = !_showNotApplicable;
                RefreshCardVisibility();
            };
            Grid.SetColumn(_btnToggleHidden, 1);
            fbg.Children.Add(_btnToggleHidden);
            factoryBar.Child = fbg;
            Grid.SetRow(factoryBar, 1);
            root.Children.Add(factoryBar);

            // 2. Central Scrollable Cards
            ScrollViewer sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(25, 15, 25, 15)
            };
            _cardsPanel = new StackPanel();
            bool addedAutoHeader = false;
            bool addedManualHeader = false;
            foreach (TestItemResult t in _testItems)
            {
                if (t.IsAutomated && !addedAutoHeader)
                {
                    _cardsPanel.Children.Add(CreateAutoSectionHeader());
                    addedAutoHeader = true;
                }
                else if (!t.IsAutomated && !addedManualHeader)
                {
                    _cardsPanel.Children.Add(CreateManualSectionHeader());
                    addedManualHeader = true;
                }

                Border card = CreateTestCard(t);
                _cardsPanel.Children.Add(card);
                _cardMap[t.Id] = card;
            }
            sv.Content = _cardsPanel;
            Grid.SetRow(sv, 2);
            root.Children.Add(sv);

            // 3. Footer Bar
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
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
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 10, 20, 10)
            };
            btnRunAll.Click += (s, e) => RunSequentialTests();
            Grid.SetColumn(btnRunAll, 0);
            fg.Children.Add(btnRunAll);

            Button btnFinish = new Button
            {
                Content = "✓ Završi i sačuvaj izveštaj",
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(25, 10, 25, 10)
            };
            _btnFinish = btnFinish;
            btnFinish.Click += (s, e) => FinishAndSave();
            Grid.SetColumn(btnFinish, 2);
            fg.Children.Add(btnFinish);

            footer.Child = fg;
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            RefreshCardVisibility();
            UpdateSummaryBadge();

            Loaded += (s, e) => StartPassiveAutoChecks();
            Closing += (s, e) =>
            {
                _phase5WorkflowActive = false;
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
            _autoStressCancel.Cancel();
            try { _autoEngine.Dispose(); } catch { }
        }

        public void CancelPhase5Workflow()
        {
            _phase5WorkflowActive = false;
            _autoStressCancel.Cancel();
        }

        public void UpdateIdentity(string modelName, string serialNumber)
        {
            bool changed = false;
            if (!string.IsNullOrEmpty(modelName) && !modelName.Equals("Nepoznat model", StringComparison.OrdinalIgnoreCase))
            {
                _modelName = modelName;
                if (_hwInfo != null) _hwInfo.ModelName = modelName;
                changed = true;
            }
            if (!string.IsNullOrEmpty(serialNumber) && !serialNumber.StartsWith("Nepoznat", StringComparison.OrdinalIgnoreCase))
            {
                _serialNumber = serialNumber;
                if (_hwInfo != null) _hwInfo.SerialNumber = serialNumber;
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
            // 1. Ekran (uvek prisutan)
            _testItems.Add(new TestItemResult("display", "Ekran (mrtvi pikseli)", "Provera matrice ekrana (6 boja + gradijent), WMI osvetljenja i HDMI izlaza"));

            // 2. Tastatura (uvek prisutna)
            _testItems.Add(new TestItemResult("keyboard", "Tastatura", "Ispitivanje odziva svih fizičkih tastera i provera zaglavljenih kontakata"));

            // 3. Touchpad / TrackPoint
            bool tpExpected = _hwInfo.HasTouchpad || _hwInfo.IsLaptop || _hwInfo.FactoryTouchpad == true || _hwInfo.TouchpadDisabledInBios;
            bool hasTrackPoint = _hwInfo.HasTrackPoint || _hwInfo.IsThinkPad;
            string tpTitle = hasTrackPoint ? "Touchpad / TrackPoint" : "Touchpad";
            string tpDesc = hasTrackPoint
                ? "Provera kursora, 4 ugla, L/D klika i TrackPoint srednjeg tastera"
                : "Test praćenja pokazivača u 4 ugla, levog/desnog klika i skrola sa dva prsta";
            if (_hwInfo.TouchpadDisabledInBios && !_hwInfo.HasTouchpad)
                tpDesc = "⚠️ UPOZORENJE: Touchpad je ONEMOGUĆEN u BIOS-u (" + _hwInfo.TouchpadBiosStatus + ")!";
            TestItemResult tp = new TestItemResult("touchpad", tpTitle, tpDesc);
            if (!tpExpected)
            {
                tp.Status = TestStatus.NotApplicable;
                tp.Reason = "Uređaj fabrički nema touchpad (Desktop konfiguracija)";
            }
            else if (_hwInfo.TouchpadDisabledInBios && !_hwInfo.HasTouchpad)
            {
                tp.Reason = "Touchpad onemogućen u BIOS-u: " + _hwInfo.TouchpadBiosStatus;
                tp.Details["bios_disabled"] = true;
            }
            _testItems.Add(tp);

            // 4. Touchscreen (+ Stylus olovka)
            bool tsExpected = _hwInfo.HasTouchscreen || _hwInfo.TouchscreenPnpPresent || _hwInfo.TouchscreenDisabledInBios || _hwInfo.FactoryTouchscreen == true;
            string tsTitle = _hwInfo.HasStylusPen ? "Touchscreen + Olovka (Stylus)" : "Ekran na dodir (Touchscreen)";
            string tsDesc = _hwInfo.HasStylusPen
                ? "Prolazak kroz lavirint prstom, multitouch detekcija (+ provera Stylus olovke i pritiska)"
                : "Prolazak kroz lavirint prstom i multitouch detekcija";
            if (_hwInfo.TouchscreenDisabledInBios && !_hwInfo.HasTouchscreen)
                tsDesc = "⚠️ FABRIČKI PRISUTAN — Touchscreen je ONEMOGUĆEN u BIOS-u (" + _hwInfo.TouchscreenBiosStatus + ")!";
            else if (!_hwInfo.HasTouchscreen && (_hwInfo.FactoryTouchscreen == true || _hwInfo.TouchscreenPnpPresent))
                tsDesc = "⚠️ FABRIČKI PRISUTAN (" + (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence) ? _hwInfo.FactoryTouchscreenEvidence : "PnP") + "), ali nije aktivan u Windowsu!";

            TestItemResult ts = new TestItemResult("touchscreen", tsTitle, tsDesc);
            if (!tsExpected)
            {
                ts.Status = TestStatus.NotApplicable;
                ts.Reason = !string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence)
                    ? ("Ekran nije osetljiv na dodir (" + _hwInfo.FactoryTouchscreenEvidence + ")")
                    : "Displej fabrički ne podržava unos dodirom";
            }
            else if (!_hwInfo.HasTouchscreen)
            {
                ts.Details["factory_missing_in_os"] = true;
                if (_hwInfo.TouchscreenDisabledInBios) ts.Details["bios_disabled"] = true;
                if (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence))
                    ts.Details["factory_evidence"] = _hwInfo.FactoryTouchscreenEvidence;
            }
            _testItems.Add(ts);

            // 5. Zvuk (Zvučnici)
            bool audioExpected = _hwInfo.HasSpeakers || _hwInfo.IsLaptop || _hwInfo.FactorySpeakers == true;
            TestItemResult audio = new TestItemResult("audio", "Zvuk (Levi / Desni kanal)", "Test stereo zvučnika izgovorom nasumičnih cifara na levom i desnom kanalu");
            if (!audioExpected)
            {
                audio.Status = TestStatus.NotApplicable;
                audio.Reason = "Uređaj nema detektovane zvučnike (Desktop bez audio izlaza)";
            }
            _testItems.Add(audio);

            // 6. Mikrofon
            bool micExpected = _hwInfo.HasMicrophone || _hwInfo.MicrophoneDisabledInBios || _hwInfo.FactoryMicrophone == true ||
                               (_hwInfo.IsLaptop && _hwInfo.FactoryMicrophone != false && (_hwInfo.HasWebcam || _hwInfo.FactoryWebcam == true));
            string micDesc = "VU metar nivoa glasa i automatsko preslušavanje snimka";
            if (_hwInfo.MicrophoneDisabledInBios && !_hwInfo.HasMicrophone)
                micDesc = "⚠️ FABRIČKI PRISUTAN — Mikrofon je ONEMOGUĆEN u BIOS-u (" + _hwInfo.MicrophoneBiosStatus + ")!";
            else if (!_hwInfo.HasMicrophone && micExpected)
                micDesc = "⚠️ FABRIČKI OČEKIVAN MIKROFON — Nije detektovan aktivan capture uređaj u Windowsu!";

            TestItemResult mic = new TestItemResult("mic", "Mikrofon", micDesc);
            if (!micExpected)
            {
                mic.Status = TestStatus.NotApplicable;
                mic.Reason = "Uređaj nema ugrađeni mikrofon";
            }
            else if (!_hwInfo.HasMicrophone)
            {
                mic.Details["factory_missing_in_os"] = true;
                if (_hwInfo.MicrophoneDisabledInBios) mic.Details["bios_disabled"] = true;
            }
            _testItems.Add(mic);

            // 7. Kamera (Web / IR)
            bool camExpected = _hwInfo.HasWebcam || _hwInfo.HasIrCamera || _hwInfo.WebcamPnpPresent ||
                               _hwInfo.CameraDisabledInBios || _hwInfo.FactoryWebcam == true || _hwInfo.FactoryIrCamera == true;
            string camDesc = "Očitavanje otštampanog QR koda kamerom i provera IR senzora";
            if (_hwInfo.CameraDisabledInBios && !_hwInfo.HasWebcam)
                camDesc = "⚠️ FABRIČKI PRISUTNA — Kamera je ONEMOGUĆENA u BIOS-u (" + _hwInfo.CameraBiosStatus + ")!";
            else if (!_hwInfo.HasWebcam && camExpected)
                camDesc = "⚠️ FABRIČKI PRISUTNA KAMERA (" + (!string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence) ? _hwInfo.FactoryWebcamEvidence : "PnP") + "), ali nije aktivna u Windowsu!";

            TestItemResult cam = new TestItemResult("camera", "Kamera (QR kod i IR)", camDesc);
            if (!camExpected)
            {
                cam.Status = TestStatus.NotApplicable;
                cam.Reason = !string.IsNullOrEmpty(_hwInfo.FactoryWebcamEvidence)
                    ? ("Uređaj fabrički nema kameru (" + _hwInfo.FactoryWebcamEvidence + ")")
                    : "Video kamera nije pronađena ni u OS-u ni u fabričkoj konfiguraciji";
            }
            else if (!_hwInfo.HasWebcam && !_hwInfo.HasIrCamera)
            {
                cam.Details["factory_missing_in_os"] = true;
                if (_hwInfo.CameraDisabledInBios) cam.Details["bios_disabled"] = true;
            }
            _testItems.Add(cam);

            // 8. USB portovi (uvek prisutni)
            _testItems.Add(new TestItemResult("usb", "USB portovi", "Verifikacija svih spoljnih USB portova ubacivanjem fleš diska"));

            // 9. Čitač otiska prsta (OS + PnP + BIOS + OEM BOM provera; ako BOM nije dostupan na laptopu ne krijemo ga naslepo)
            bool fpExpected = _hwInfo.HasFingerprint ||
                              _hwInfo.FingerprintDisabledInBios ||
                              _hwInfo.FactoryFingerprint == true ||
                              (_hwInfo.IsLaptop && !_hwInfo.FactoryBomLoaded && _hwInfo.FactoryFingerprint != false);
            string fpDesc = "Provera odziva biometrijskog senzora otiska prsta preko Windows Biometric API-ja";
            if (_hwInfo.FingerprintDisabledInBios)
                fpDesc = "⚠️ UPOZORENJE: Uređaj je ONEMOGUĆEN u podešavanjima BIOS-a (" + _hwInfo.FingerprintBiosStatus + ")!";
            else if (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
                fpDesc = "⚠️ FABRIČKI PRISUTAN U OEM BOM-U (" + _hwInfo.FactoryFingerprintEvidence + "), ali nije detektovan u Windowsu!";
            else if (!_hwInfo.HasFingerprint && _hwInfo.IsLaptop && !_hwInfo.FactoryBomLoaded)
                fpDesc = "⚠️ Senzor nije automatski detektovan (fabrički BOM nedostupan) — ako laptop ima čitač otiska, testirajte ili preskočite ako ga nema.";

            TestItemResult fp = new TestItemResult("fingerprint", "Čitač otiska prsta", fpDesc);
            if (!fpExpected)
            {
                fp.Status = TestStatus.NotApplicable;
                fp.Reason = !string.IsNullOrEmpty(_hwInfo.FactoryFingerprintEvidence)
                    ? ("Čitač otiska prsta nije fabrički ugrađen (" + _hwInfo.FactoryFingerprintEvidence + ")")
                    : "Čitač otiska prsta nije pronađen ni u OS-u ni u fabričkoj specifikaciji";
            }
            else if (_hwInfo.FingerprintDisabledInBios)
            {
                fp.Reason = "Čitač otiska prsta je onemogućen u BIOS-u: " + _hwInfo.FingerprintBiosStatus;
                fp.Details["bios_disabled"] = true;
                fp.Details["bios_setting"] = _hwInfo.FingerprintBiosStatus;
            }
            else if (!_hwInfo.HasFingerprint && _hwInfo.FactoryFingerprint == true)
            {
                fp.Details["factory_missing_in_os"] = true;
                fp.Details["factory_evidence"] = _hwInfo.FactoryFingerprintEvidence;
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
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 2, 0, 10)
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "⚡ AUTOMATSKI TESTOVI HARDVERA",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            });
            _autoSectionStatus = new TextBlock
            {
                Text = "Pasivne provere (Baterija, Mreža, BIOS/TPM/PnP, SMART) pokreću se automatski pri otvaranju; stres testovi (Disk, RAM, CPU, GPU) idu sekvencijalno jednim klikom.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 210)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            sp.Children.Add(_autoSectionStatus);
            g.Children.Add(sp);

            _btnRunAutoSuite = new Button
            {
                Content = "⚡ Pokreni automatski stres paket",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 8, 16, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0)
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
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 9, 16, 9),
                Margin = new Thickness(0, 10, 0, 10)
            };
            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "🔧 PERIFERNI TESTOVI SERVISERA",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 235))
            });
            sp.Children.Add(new TextBlock
            {
                Text = "Interaktivni testovi fizičkih periferija i ulazno/izlaznih uređaja",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
                Margin = new Thickness(0, 2, 0, 0)
            });
            b.Child = sp;
            return b;
        }

        private Border CreateTestCard(TestItemResult t)
        {
            bool hasWarning = t.HasWarning || (t.Details != null &&
                (t.Details.ContainsKey("bios_disabled") || t.Details.ContainsKey("factory_missing_in_os")));

            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                BorderBrush = hasWarning
                    ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
                    : new SolidColorBrush(Color.FromRgb(50, 54, 64)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 14, 20, 14),
                Margin = new Thickness(0, 0, 0, 10),
                Tag = t
            };

            Grid cg = new Grid();
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Info leva strana
            StackPanel spInfo = new StackPanel();
            TextBlock tbTitle = new TextBlock
            {
                Text = t.Title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            spInfo.Children.Add(tbTitle);

            string descDisplay = (t.Status == TestStatus.NotApplicable && !string.IsNullOrEmpty(t.Reason))
                ? (t.Description + " — " + t.Reason)
                : t.Description;

            TextBlock tbDesc = new TextBlock
            {
                Text = descDisplay,
                FontSize = 12,
                Foreground = hasWarning
                    ? new SolidColorBrush(Color.FromRgb(255, 209, 102))
                    : new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            spInfo.Children.Add(tbDesc);
            _descTextMap[t.Id] = tbDesc;

            TextBlock tbMetric = new TextBlock
            {
                Text = t.MetricSummary ?? "",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = string.IsNullOrEmpty(t.MetricSummary) ? Visibility.Collapsed : Visibility.Visible
            };
            spInfo.Children.Add(tbMetric);
            _metricTextMap[t.Id] = tbMetric;

            cg.Children.Add(spInfo);

            // Status Badge u sredini
            Border statusBorder = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 15, 0)
            };
            TextBlock tbStatus = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            statusBorder.Child = tbStatus;
            Grid.SetColumn(statusBorder, 1);
            cg.Children.Add(statusBorder);

            // Dugmad desno
            StackPanel spBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (t.IsAutomated)
            {
                Button btnDetails = new Button
                {
                    Content = "🔍 Detalji",
                    Background = new SolidColorBrush(Color.FromRgb(50, 56, 70)),
                    Foreground = Brushes.White,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                btnDetails.Click += (s, e) => OpenAutoDetails(t);
                spBtns.Children.Add(btnDetails);
            }

            Button btnRun = new Button
            {
                Content = "Pokreni",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnRun.Click += (s, e) => RunSingleTest(t);
            _runBtnMap[t.Id] = btnRun;
            spBtns.Children.Add(btnRun);

            Button btnSkip = new Button
            {
                Content = "Preskoči",
                Background = new SolidColorBrush(Color.FromRgb(70, 75, 85)),
                Foreground = Brushes.White,
                Padding = new Thickness(10, 6, 10, 6)
            };
            btnSkip.Click += (s, e) => PromptSkipReason(t);
            spBtns.Children.Add(btnSkip);

            Grid.SetColumn(spBtns, 2);
            cg.Children.Add(spBtns);

            card.Child = cg;
            UpdateCardVisual(card, t);
            return card;
        }

        private void UpdateCardVisual(Border card, TestItemResult t)
        {
            Grid cg = card.Child as Grid;
            if (cg == null) return;

            bool hasWarn = t.HasWarning || (t.Details != null &&
                (t.Details.ContainsKey("bios_disabled") || t.Details.ContainsKey("factory_missing_in_os")));

            if (t.Status == TestStatus.Fail)
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
            else if (hasWarn)
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            else if (t.Status == TestStatus.Pass)
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 110, 72));
            else
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(50, 54, 64));

            TextBlock tbMetric;
            if (_metricTextMap.TryGetValue(t.Id, out tbMetric))
            {
                tbMetric.Text = t.MetricSummary ?? "";
                tbMetric.Visibility = string.IsNullOrEmpty(t.MetricSummary) ? Visibility.Collapsed : Visibility.Visible;
            }

            TextBlock tbDesc;
            if (_descTextMap.TryGetValue(t.Id, out tbDesc))
            {
                if (t.IsAutomated && !string.IsNullOrEmpty(t.Reason) && t.Status != TestStatus.Pending)
                {
                    tbDesc.Text = t.Reason;
                    if (t.Status == TestStatus.Fail)
                        tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 140));
                    else if (hasWarn)
                        tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102));
                    else
                        tbDesc.Foreground = new SolidColorBrush(Color.FromRgb(175, 185, 195));
                }
                else
                {
                    tbDesc.Text = (t.Status == TestStatus.NotApplicable && !string.IsNullOrEmpty(t.Reason))
                        ? (t.Description + " — " + t.Reason)
                        : t.Description;
                    tbDesc.Foreground = hasWarn
                        ? new SolidColorBrush(Color.FromRgb(255, 209, 102))
                        : new SolidColorBrush(Color.FromRgb(160, 160, 170));
                }
            }

            Border statusBorder = cg.Children[1] as Border;
            TextBlock tbStatus = statusBorder != null ? statusBorder.Child as TextBlock : null;

            if (tbStatus != null)
            {
                switch (t.Status)
                {
                    case TestStatus.Pending:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(55, 60, 72));
                        tbStatus.Text = "⏳ Čeka";
                        break;
                    case TestStatus.Running:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(217, 150, 15));
                        tbStatus.Text = "🔄 U toku";
                        break;
                    case TestStatus.Pass:
                        if (t.HasWarning)
                        {
                            statusBorder.Background = new SolidColorBrush(Color.FromRgb(217, 130, 15));
                            tbStatus.Text = "⚠ PASS (UPOZ.)";
                        }
                        else
                        {
                            statusBorder.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                            tbStatus.Text = "✓ PASS";
                        }
                        break;
                    case TestStatus.Fail:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                        tbStatus.Text = "❌ FAIL";
                        break;
                    case TestStatus.Skip:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(108, 117, 125));
                        tbStatus.Text = "⏭️ SKIP";
                        break;
                    case TestStatus.NotApplicable:
                        statusBorder.Background = new SolidColorBrush(Color.FromRgb(60, 65, 75));
                        tbStatus.Text = "⚪ N/A";
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
                    MessageBox.Show("Automatski stres paket je već u toku. Sačekajte završetak ili kliknite Zaustavi.",
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

            Stopwatch sw = Stopwatch.StartNew();
            t.Status = TestStatus.Running;
            HwTestLogger.Info("TEST_START", string.Format("Pokrenut test '{0}' ({1})", t.Id, t.Title));
            Border card;
            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);

            switch (t.Id)
            {
                case "display":
                    var dt = new DisplayTestWindow();
                    dt.ShowDialog();
                    ApplyResult(t, dt.Result);
                    break;
                case "keyboard":
                    var kt = new KeyboardTestWindow(_modelName, _hwInfo.RecommendedLayout, _profileClient, _hwInfo, _serialNumber);
                    kt.ShowDialog();
                    ApplyResult(t, kt.Result);
                    break;
                case "touchpad":
                    var tpt = new TouchpadTestWindow(_hwInfo);
                    tpt.ShowDialog();
                    ApplyResult(t, tpt.Result);
                    break;
                case "touchscreen":
                    var tst = new TouchscreenMazeWindow(_hwInfo);
                    tst.ShowDialog();
                    ApplyResult(t, tst.Result);
                    break;
                case "audio":
                    var at = new AudioTestWindow();
                    at.ShowDialog();
                    ApplyResult(t, at.Result);
                    break;
                case "mic":
                    var mt = new MicTestWindow();
                    mt.ShowDialog();
                    ApplyResult(t, mt.Result);
                    break;
                case "camera":
                    var ct = new CameraTestWindow(_hwInfo.HasIrCamera, _hwInfo.WebcamName, _hwInfo.IrCameraName);
                    ct.ShowDialog();
                    ApplyResult(t, ct.Result);
                    break;
                case "usb":
                    ModelProfileData prof = _profileClient != null ? _profileClient.GetProfile(_modelName) : null;
                    int? pCount = (prof != null && prof.UsbPortCount.HasValue && prof.UsbPortCount.Value > 0)
                        ? prof.UsbPortCount
                        : (int?)_hwInfo.EstimatedUsbPortCount;
                    var ut = new UsbPortTestWindow(_modelName, pCount, _profileClient);
                    ut.ShowDialog();
                    ApplyResult(t, ut.Result);
                    break;
                case "fingerprint":
                    var fpt = new FingerprintTestWindow(_hwInfo);
                    fpt.ShowDialog();
                    ApplyResult(t, fpt.Result);
                    break;
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
            HwTestLogger.Info("TEST_START", string.Format("Pokrenut test '{0}' ({1})", t.Id, t.Title));
            Border card;
            if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);

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
                _autoStressCancel.Cancel();
                if (_autoSectionStatus != null)
                    _autoSectionStatus.Text = "⏳ Zaustavljam automatski stres paket nakon trenutnog koraka...";
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

                        TestItemResult result = ExecuteAutoWorkerById(id, (msg) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                item.MetricSummary = msg;
                                TextBlock tm;
                                if (_metricTextMap.TryGetValue(item.Id, out tm))
                                {
                                    tm.Text = msg;
                                    tm.Visibility = Visibility.Visible;
                                }
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
                            TextBlock tm;
                            if (_metricTextMap.TryGetValue(item.Id, out tm))
                            {
                                tm.Text = msg;
                                tm.Visibility = Visibility.Visible;
                            }
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
                _btnRunAutoSuite.Content = busy ? "⏹ Zaustavi stres" : "⚡ Pokreni automatski stres paket";
                _btnRunAutoSuite.Background = busy
                    ? new SolidColorBrush(Color.FromRgb(185, 65, 50))
                    : new SolidColorBrush(Color.FromRgb(0, 122, 204));
            }
            foreach (KeyValuePair<string, Button> kv in _runBtnMap)
            {
                if (kv.Key.StartsWith("auto_", StringComparison.OrdinalIgnoreCase))
                    kv.Value.IsEnabled = !busy;
            }
        }

        private void PromptSkipReason(TestItemResult t)
        {
            Window dlg = new Window
            {
                Title = "Preskakanje testa: " + t.Title,
                Width = 460,
                Height = 220,
                WindowStartupLocation = GetDialogOwner() != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = GetDialogOwner(),
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                WindowStyle = WindowStyle.ToolWindow
            };
            dlg.SourceInitialized += (s, e) => NativeMethods.EnableDarkTitleBar(dlg);

            StackPanel sp = new StackPanel { Margin = new Thickness(20) };
            TextBlock lbl = new TextBlock
            {
                Text = "Unesite obavezno obrazloženje za preskakanje:",
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10)
            };
            sp.Children.Add(lbl);

            TextBox tb = new TextBox
            {
                Height = 35,
                FontSize = 14,
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                Margin = new Thickness(0, 0, 0, 20)
            };
            sp.Children.Add(tb);

            Button btnOk = new Button
            {
                Content = "Potvrdi preskakanje",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 8, 15, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btnOk.Click += (s, e) => {
                string r = tb.Text.Trim();
                if (string.IsNullOrEmpty(r))
                {
                    MessageBox.Show("Obrazloženje je obavezno!", "Greška", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                t.Status = TestStatus.Skip;
                t.Reason = r;
                dlg.DialogResult = true;
                dlg.Close();
            };
            sp.Children.Add(btnOk);

            dlg.Content = sp;
            if (dlg.ShowDialog() == true)
            {
                Border card;
                if (_cardMap.TryGetValue(t.Id, out card)) UpdateCardVisual(card, t);
                UpdateSummaryBadge();
            }
        }

        private void RunSequentialTests()
        {
            if (_autoStressRunning)
            {
                MessageBox.Show(
                    "Automatski stres testovi su već u toku. Sačekajte da se završe ili ih zaustavite pre sekvencijalnog pokretanja.",
                    "Stres test u toku",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            for (int i = 0; i < _testItems.Count; i++)
            {
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
                            TextBlock tm;
                            if (_metricTextMap.TryGetValue(item.Id, out tm))
                            {
                                tm.Text = msg;
                                tm.Visibility = Visibility.Visible;
                            }
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

            if (fail > 0)
            {
                _summaryBadge.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                _summaryBadge.Text = string.Format("❌ {0} FAIL | {1} PASS | {2} SKIP", fail, pass, skip);
            }
            else if (pending > 0)
            {
                _summaryBadge.Background = new SolidColorBrush(Color.FromRgb(217, 150, 15));
                _summaryBadge.Text = (na > 0)
                    ? string.Format("⏳ U toku ({0} preostalo | {1} odsutno)", pending, na)
                    : string.Format("⏳ U toku ({0} preostalo)", pending);
            }
            else
            {
                _summaryBadge.Background = warn > 0
                    ? new SolidColorBrush(Color.FromRgb(217, 130, 15))
                    : new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _summaryBadge.Text = warn > 0
                    ? string.Format("⚠ ZAVRŠENO ({0} PASS, {1} UPOZ., {2} SKIP)", pass, warn, skip)
                    : string.Format("✓ SVI TESTOVI ZAVRŠENI ({0} PASS, {1} SKIP)", pass, skip);
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

        private void RunSequentialPeripheralTests()
        {
            for (int i = 0; i < _testItems.Count; i++)
            {
                if (_autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                TestItemResult t = _testItems[i];
                if (!t.IsAutomated && t.Status == TestStatus.Pending)
                {
                    RunSingleTest(t);
                }
            }
        }

        public void ForceCompletePhase5SkipPending()
        {
            _autoStressCancel.Cancel();
            bool shouldFinish = false;
            lock (_phase5Lock)
            {
                shouldFinish = !_phase5CompletedNotified;
            }
            if (!shouldFinish) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                SaveCurrentReport(false, true, "Preskočeno u okviru Faze 5", true);
            }));
        }

        public void StartPhase5FullWorkflow(
            Action<string, string, string, string> onStepProgress,
            Action<HwTestRunResult, int, float, float, bool, bool> onPhase5Finished)
        {
            lock (_phase5Lock)
            {
                _phase5WorkflowActive = true;
                _phase5CompletedNotified = false;
                _phase5StepCallback = onStepProgress;
                _phase5FinishedCallback = onPhase5Finished;
                _phase5CpuDurationSec = 0;
                _phase5PeakCpuTemp = 0f;
                _phase5MaxCpuThrottle = 0f;
                _phase5ThermalCritical = false;
            }

            _autoStressCancel.Reset();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartPassiveAutoChecks();
                if (_autoSectionStatus != null)
                {
                    _autoSectionStatus.Text = "🔄 Faza 5 u toku: pokrećem automatsku dijagnostiku i stres test...";
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
                        if (_autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                        string pid = passiveIds[i];
                        TestItemResult item = FindItemById(pid);
                        if (item != null && (item.Status == TestStatus.Pending || item.Status == TestStatus.Running))
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
                    EmitPhase5Step("P5.AutoDiag", "Automatska provera sistema",
                        _autoStressCancel.IsCancelled ? "SKIPPED_USER" : (autoDiagWarn ? "WARN" : "OK"), "");

                    // 2. Automatski stres paket (Disk, RAM, CPU 30s, GPU 3D)
                    if (!_autoStressCancel.IsCancelled && _phase5WorkflowActive)
                    {
                        EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)", "RUNNING", "");
                        string[] seq = new string[] { "auto_disk", "auto_ram", "auto_cpu" };
                        for (int i = 0; i < seq.Length; i++)
                        {
                            if (_autoStressCancel.IsCancelled || !_phase5WorkflowActive) break;
                            string id = seq[i];
                            TestItemResult item = FindItemById(id);
                            if (item == null || item.Status == TestStatus.Skip || item.Status == TestStatus.NotApplicable)
                                continue;

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

                            TestItemResult result = ExecuteAutoWorkerById(id, (msg) =>
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    item.MetricSummary = msg;
                                    TextBlock tm;
                                    if (_metricTextMap.TryGetValue(item.Id, out tm))
                                    {
                                        tm.Text = msg;
                                        tm.Visibility = Visibility.Visible;
                                    }
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

                        if (_phase5ThermalCritical)
                        {
                            EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)", "ERROR",
                                string.Format("CPU {0:F1}°C", _phase5PeakCpuTemp));
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _autoStressRunning = false;
                                SetAutoControlsBusy(false);
                                if (_autoSectionStatus != null)
                                    _autoSectionStatus.Text = string.Format("❌ KRITIČNO PREGREVANJE CPU ({0:F1}°C) — Faza 5 zaustavljena!", _phase5PeakCpuTemp);
                                SaveCurrentReport(false, true, "Prekinuto zbog kritičnog pregrevanja CPU-a", false);
                            }));
                            return;
                        }

                        if (!_autoStressCancel.IsCancelled && _phase5WorkflowActive)
                        {
                            TestItemResult gpuItem = FindItemById("auto_gpu");
                            if (gpuItem != null && gpuItem.Status != TestStatus.Skip && gpuItem.Status != TestStatus.NotApplicable)
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

                        EmitPhase5Step("P5.Stress", "Stres test (Disk, RAM, CPU 30s, GPU 3D)",
                            _autoStressCancel.IsCancelled ? "SKIPPED_USER" : (stressWarn ? "WARN" : "OK"), "");
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

                if (_autoStressCancel.IsCancelled || !_phase5WorkflowActive)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SaveCurrentReport(false, true, "Preskočeno od strane servisera", true);
                    }));
                    return;
                }

                // 3. Interaktivni periferijski testovi
                EmitPhase5Step("P5.Peripherals", "Interaktivni testovi periferija", "RUNNING", "");
                Dispatcher.Invoke(new Action(() =>
                {
                    if (_autoSectionStatus != null)
                    {
                        _autoSectionStatus.Text = "✓ Automatski stres završen — pokrećem interaktivne testove periferija...";
                    }
                    RunSequentialPeripheralTests();

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
                                "⏳ Preostalo {0} testova. Kliknite ih pojedinačno ili kliknite '✓ Završi i sačuvaj izveštaj' za nastavak u Fazu 6.",
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
            if (_autoStressRunning)
            {
                MessageBoxResult stopRes = MessageBox.Show(
                    "Automatski stres testovi su još uvek u toku.\n\nDa li želite da prekinete stres test i odmah sačuvate izveštaj?",
                    "Stres test u toku",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (stopRes != MessageBoxResult.Yes)
                {
                    return;
                }
                _autoStressCancel.Cancel();
            }

            SaveCurrentReport(true, true, "Preskočeno pri ranom završetku izveštaja", false);
        }

        private void SaveCurrentReport(bool promptIfPending, bool autoMarkPendingAsSkip, string defaultSkipReason, bool isCancelled)
        {
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

            if (pending > 0 && promptIfPending)
            {
                MessageBoxResult confirmRes = MessageBox.Show(
                    string.Format("Ostalo je još {0} neizvršenih testova.\n\nDa li želite da ih označite kao PRESKOČENE (SKIP) i završite izveštaj?", pending),
                    "Neizvršeni testovi",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmRes != MessageBoxResult.Yes)
                {
                    return;
                }
                autoMarkPendingAsSkip = true;
            }

            if (pending > 0 && autoMarkPendingAsSkip)
            {
                string skipReason = string.IsNullOrEmpty(defaultSkipReason) ? "Preskočeno pri ranom završetku izveštaja" : defaultSkipReason;
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
                    { "overall", overall }
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
            string json = SimpleJson.Serialize(new Dictionary<string, object>
            {
                { "schema", runResult.Schema },
                { "started", runResult.Started },
                { "finished", runResult.Finished },
                { "model", runResult.Model },
                { "serial", runResult.Serial },
                { "app_version", runResult.AppVersion },
                { "summary", runResult.Summary },
                { "tests", runResult.Tests }
            });

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

            if (!savedPrimary)
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
    }
}
