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
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public enum PointingTestMode
    {
        TouchpadOnly,
        TrackPointOnly
    }

    public class TouchpadTestWindow : Window
    {
        private readonly Canvas _testCanvas;
        private readonly TextBlock _instructions;
        private readonly List<Ellipse> _targets;
        private readonly HashSet<int> _visitedTargets;

        private readonly HardwareProbeResult _hwInfo;
        private readonly PointingTestMode _mode;
        private readonly bool _hasTrackPoint;
        private bool _showBiosRebootHint;
        private Button _btnBiosReboot;
        private TextBlock _biosHintBanner;

        private bool _leftClickDone;
        private bool _rightClickDone;
        private bool _scrollUpDone;
        private bool _scrollDownDone;
        private bool _tpLeftDone;
        private bool _tpRightDone;
        private bool _middleClickDone;
        private bool _isClosingOrDone;

        private readonly TextBlock _statusMove;
        private readonly TextBlock _statusLeft;
        private readonly TextBlock _statusRight;
        private readonly TextBlock _statusScroll;
        private readonly TextBlock _statusTpLeft;
        private readonly TextBlock _statusMiddle;
        private readonly TextBlock _statusTpRight;
        private Border _tpVisualBox;
        private Border _tpBtnLeft;
        private Border _tpBtnMiddle;
        private Border _tpBtnRight;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _mouseHookProc;
        private DispatcherTimer _middleKeyPollTimer;
        private HwndSource _hwndSource;
        private bool _rawInputRegistered;
        private readonly List<NativeMethods.RAWINPUTDEVICE> _registeredRawDevs = new List<NativeMethods.RAWINPUTDEVICE>();
        private bool _elanHkcuSwitched;
        private int _origElanHkcuButtonMiddle = -1;
        private bool _elanHklmSwitched;
        private int _origElanHklmButtonMiddle = -1;
        private readonly List<ElanEventWatch> _elanEventWatches = new List<ElanEventWatch>();
        private Thread _elanWaitThread;
        private volatile bool _stopElanWaitThread;

        private readonly Dictionary<IntPtr, string> _rawDeviceNames = new Dictionary<IntPtr, string>();
        private readonly HashSet<IntPtr> _mouseDevicesSeen = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _hidTouchpadDevices = new HashSet<IntPtr>();
        private readonly HashSet<string> _loggedRawSamples = new HashSet<string>();
        private bool _hasPrecisionTouchpadHid;
        private bool _hasSeenTouchpadHidPacket;
        private bool _canSeparateTrackPointButtons;
        private int _lastTouchpadHidTicks;
        private int _lastTouchscreenHidTicks;
        private string _lastMouseRawSource = "";
        private int _lastMouseRawTicks;

        private Border _footerBorder;
        private TextBlock _antiCheatSubHint;
        private string _restoreStateFilePath;

        // Sloj 1 Anti-Cheat & Anti-Brick stanje
        private readonly object _touchpadRestoreLock = new object();
        private bool _touchpadTemporarilyDisabled;
        private string _touchpadDisableMethod = "NONE";
        private bool _spiTouchpadSwitched;
        private bool _origSpiTouchpadEnabled = true;
        private bool _elanTouchpadEnableSwitched;
        private int _origElanTouchpadEnable = -1;
        private bool _touchpadRestored;
        private bool _recoveryHandlersHooked;
        private EventHandler _processExitHandler;
        private DispatcherUnhandledExceptionEventHandler _appDispatcherExceptionHandler;
        private DispatcherUnhandledExceptionEventHandler _localDispatcherExceptionHandler;

        // Sloj 2 RawInput Anti-Cheat klasifikacija & brojači
        private readonly Dictionary<IntPtr, string> _rawDeviceClassCache = new Dictionary<IntPtr, string>();
        private readonly HashSet<string> _precisionTouchpadBaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _precisionTouchpadParentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _touchscreenBaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _touchscreenParentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _blockedTouchpadEvents;
        private int _blockedExternalMouseEvents;
        private int _blockedTrackPointEvents;
        private int _blockedTouchscreenEvents;
        private int _lastAntiCheatLogTicks;

        public string RestoreStateFilePath
        {
            get { return _restoreStateFilePath; }
            set { _restoreStateFilePath = value; }
        }

        private sealed class ElanEventWatch
        {
            public string Name;
            public string Action; // "TP_MIDDLE", "TP_LEFT", "TP_RIGHT", "TP_SCROLL"
            public IntPtr Handle;
            public bool WaitLowBeforeTrigger;
        }

        public TestItemResult Result { get; private set; }
        public Action<string> OnRequestUefiBiosReboot { get; set; }

        public bool ShowBiosRebootHint
        {
            get { return _showBiosRebootHint; }
            set
            {
                _showBiosRebootHint = value;
                UpdateBiosRebootHintUi();
            }
        }

        public TouchpadTestWindow() : this(null, PointingTestMode.TouchpadOnly, null, false)
        {
        }

        public TouchpadTestWindow(HardwareProbeResult hwInfo)
            : this(hwInfo, PointingTestMode.TouchpadOnly, null, false)
        {
        }

        public TouchpadTestWindow(
            HardwareProbeResult hwInfo,
            PointingTestMode mode,
            Action<string> onRequestUefiBiosReboot = null,
            bool showBiosRebootHint = false)
        {
            _hwInfo = hwInfo;
            _mode = mode;
            _hasTrackPoint = (_mode == PointingTestMode.TrackPointOnly);
            OnRequestUefiBiosReboot = onRequestUefiBiosReboot;
            _showBiosRebootHint = showBiosRebootHint;

            Title = _hasTrackPoint
                ? HwTestLocalization.Pick("WIS — Test TrackPointa", "WIS — 指点杆 (TrackPoint) 测试")
                : HwTestLocalization.Pick("WIS — Test Touchpada", "WIS — 触控板 (Touchpad) 测试");
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = _hasTrackPoint
                ? new TestItemResult("trackpoint", "TrackPoint",
                    HwTestLocalization.Pick("Provera pomeranja kursora crvenim TrackPoint džojstikom (4 mete) i gornjih tastera (levi, srednji/Scroll, desni)",
                        "检测红色 TrackPoint 指点杆移动（4 个目标）及上方按键（左键、中键/滚轮、右键）"))
                : new TestItemResult("touchpad", "Touchpad",
                    HwTestLocalization.Pick("Ispitivanje praćenja pokazivača, levog/desnog klika i skrola sa dva prsta",
                        "检测触控板光标移动、左右键点击及双指滚动"));

            _targets = new List<Ellipse>();
            _visitedTargets = new HashSet<int>();

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

            // Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 10, 25, 10)
            };
            StackPanel spH = new StackPanel();
            _instructions = new TextBlock
            {
                Text = _hasTrackPoint
                    ? HwTestLocalization.Pick("🔴 Pomerite kursor crvenim TrackPoint džojstikom kroz 4 crvene mete (ne dirajte touchpad!)",
                        "🔴 请使用红色 TrackPoint 指点杆将光标移过 4 个红色目标（请勿触碰触控板！）")
                    : HwTestLocalization.Pick("👉 Pomerite pokazivač touchpada kroz 4 kruga u uglovima ekrana",
                        "👉 请使用触控板将光标移过屏幕四角上的 4 个圆圈"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(_instructions);

            // Indikatori napretka u zaglavlju (odvojeni po modu)
            WrapPanel spTasks = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            if (_hasTrackPoint)
            {
                _statusMove = CreateBadge(HwTestLocalization.Pick("1. TrackPoint kretanje (4 mete): 0/4", "1. 指点杆移动（4 个目标）: 0/4"));
                _statusTpLeft = CreateBadge(HwTestLocalization.Pick("2. TrackPoint levi: ⏳", "2. 指点杆左键: ⏳"));
                _statusMiddle = CreateBadge(HwTestLocalization.Pick("3. TrackPoint srednji / Scroll: ⏳", "3. 指点杆中键 / 滚动: ⏳"));
                _statusTpRight = CreateBadge(HwTestLocalization.Pick("4. TrackPoint desni: ⏳", "4. 指点杆右键: ⏳"));
                spTasks.Children.Add(_statusMove);
                spTasks.Children.Add(_statusTpLeft);
                spTasks.Children.Add(_statusMiddle);
                spTasks.Children.Add(_statusTpRight);
            }
            else
            {
                _statusMove = CreateBadge(HwTestLocalization.Pick("1. Kretanje (4 ugla): 0/4", "1. 光标移动（4 个角落）: 0/4"));
                _statusLeft = CreateBadge(HwTestLocalization.Pick("2. Levi klik: ⏳", "2. 左键点击: ⏳"));
                _statusRight = CreateBadge(HwTestLocalization.Pick("3. Desni klik: ⏳", "3. 右键点击: ⏳"));
                _statusScroll = CreateBadge(HwTestLocalization.Pick("4. Skrol (2 prsta): ⏳", "4. 双指滚动: ⏳"));
                spTasks.Children.Add(_statusMove);
                spTasks.Children.Add(_statusLeft);
                spTasks.Children.Add(_statusRight);
                spTasks.Children.Add(_statusScroll);
            }
            spH.Children.Add(spTasks);

            _biosHintBanner = new TextBlock
            {
                Text = HwTestLocalization.Pick("⚠️ Uređaj je onemogućen u BIOS podešavanjima ili nije aktivan u Windowsu (HP: F10, Lenovo: F1, Dell: F2).",
                    "⚠️ 设备已在 BIOS 设置中禁用或在 Windows 中未激活（HP: F10, Lenovo: F1, Dell: F2）。"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            spH.Children.Add(_biosHintBanner);

            _antiCheatSubHint = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };
            spH.Children.Add(_antiCheatSubHint);

            header.Child = spH;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Canvas
            _testCanvas = new Canvas { Background = Brushes.Transparent };
            _testCanvas.MouseMove += OnCanvasMouseMove;
            _testCanvas.MouseLeftButtonDown += OnCanvasLeftDown;
            _testCanvas.MouseRightButtonDown += OnCanvasRightDown;
            _testCanvas.MouseDown += OnCanvasMouseDown;
            _testCanvas.MouseWheel += OnCanvasWheel;
            Grid.SetRow(_testCanvas, 1);
            root.Children.Add(_testCanvas);

            // Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            _footerBorder = footer;
            StackPanel spFooter = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _btnBiosReboot = new Button
            {
                Content = HwTestLocalization.Pick("🔧 Restartuj u UEFI BIOS (i nastavi WIS)", "🔧 重启进入 UEFI BIOS（之后继续 WIS）"),
                Background = new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Visibility = Visibility.Collapsed
            };
            _btnBiosReboot.Click += (s, e) => RequestUefiBiosRebootFromWindow();
            spFooter.Children.Add(_btnBiosReboot);

            if (_hasTrackPoint)
            {
                Button btnManualMiddle = new Button
                {
                    Content = HwTestLocalization.Pick("✓ Srednji taster radi (Scroll mod)", "✓ 中键正常（滚动模式）"),
                    Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 0, 10, 0)
                };
                btnManualMiddle.Click += (s, e) => MarkMiddleClickDone("ručna potvrda (Synaptics Scroll mod)");
                spFooter.Children.Add(btnManualMiddle);

                Button btnMiddleFail = new Button
                {
                    Content = HwTestLocalization.Pick("✗ Srednji TrackPoint taster NE RADI (FAIL)", "✗ TrackPoint 中键故障 (FAIL)"),
                    Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(15, 8, 15, 8),
                    Margin = new Thickness(0, 0, 10, 0)
                };
                btnMiddleFail.Click += (s, e) => FinishMiddleButtonFail();
                spFooter.Children.Add(btnMiddleFail);
            }
            Button btnTouchpadFail = new Button
            {
                Content = _hasTrackPoint
                    ? HwTestLocalization.Pick("✗ Prijavi kvar TrackPointa (FAIL) [F9]", "✗ 报告 TrackPoint 故障 (FAIL) [F9]")
                    : HwTestLocalization.Pick("✗ Prijavi kvar touchpada (FAIL) [F9]", "✗ 报告触控板故障 (FAIL) [F9]"),
                ToolTip = HwTestLocalization.Pick("Prijavi kvar hardvera (prečica: F9 ili Ctrl+F)", "报告硬件故障（快捷键: F9 或 Ctrl+F）"),
                Background = new SolidColorBrush(Color.FromRgb(185, 38, 48)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnTouchpadFail.Click += (s, e) => FinishFailGeneral();
            spFooter.Children.Add(btnTouchpadFail);

            footer.Child = spFooter;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            UpdateBiosRebootHintUi();

            Loaded += (s, e) =>
            {
                SetupTargets();
                UpdateBiosRebootHintUi();
                try { Activate(); Focus(); } catch { }
                InstallHwndAndRawInputHook();
                if (_hasTrackPoint)
                {
                    StartMiddleButtonMonitoring();
                    ApplyTemporaryTouchpadDisableForTrackPoint();
                }
            };
            Closed += (s, e) => CleanupAllResourcesAndRestoreTouchpad();
            PreviewKeyDown += (s, e) =>
            {
                if (_isClosingOrDone) return;
                if (e.Key == Key.F9 ||
                    (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
                {
                    e.Handled = true;
                    FinishFailGeneral();
                }
            };
            PreviewMouseDown += OnCanvasMouseDown;
            PreviewMouseUp += OnCanvasMouseDown;
        }

        private bool ShouldShowBiosRebootHint()
        {
            if (_showBiosRebootHint) return true;
            if (_hwInfo != null)
            {
                if (_hasTrackPoint)
                {
                    if (_hwInfo.TrackPointDisabledInBios || (!_hwInfo.HasTrackPoint && _hwInfo.FactoryTrackPoint == true))
                        return true;
                }
                else
                {
                    if (_hwInfo.TouchpadDisabledInBios || (!_hwInfo.HasTouchpad && _hwInfo.FactoryTouchpad == true))
                        return true;
                }
            }
            return false;
        }

        private void UpdateBiosRebootHintUi()
        {
            bool show = ShouldShowBiosRebootHint();
            if (_btnBiosReboot != null)
            {
                _btnBiosReboot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_biosHintBanner != null)
            {
                if (show && _hwInfo != null)
                {
                    if (_hasTrackPoint && _hwInfo.TrackPointDisabledInBios && !string.IsNullOrEmpty(_hwInfo.TrackPointBiosStatus))
                    {
                        _biosHintBanner.Text = HwTestLocalization.PickFormat(
                            "⚠️ TrackPoint je onemogućen u BIOS-u ({0}) — restartujte u UEFI BIOS (HP: F10, Lenovo: F1, Dell: F2).",
                            "⚠️ TrackPoint 已在 BIOS 中禁用 ({0}) — 请重启进入 UEFI BIOS（HP: F10, Lenovo: F1, Dell: F2）。",
                            _hwInfo.TrackPointBiosStatus);
                    }
                    else if (!_hasTrackPoint && _hwInfo.TouchpadDisabledInBios && !string.IsNullOrEmpty(_hwInfo.TouchpadBiosStatus))
                    {
                        _biosHintBanner.Text = HwTestLocalization.PickFormat(
                            "⚠️ Touchpad je onemogućen u BIOS-u ({0}) — restartujte u UEFI BIOS (HP: F10, Lenovo: F1, Dell: F2).",
                            "⚠️ 触控板已在 BIOS 中禁用 ({0}) — 请重启进入 UEFI BIOS（HP: F10, Lenovo: F1, Dell: F2）。",
                            _hwInfo.TouchpadBiosStatus);
                    }
                }
                _biosHintBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RequestUefiBiosRebootFromWindow()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Pending;

            string testId = _hasTrackPoint ? "trackpoint" : "touchpad";
            if (OnRequestUefiBiosReboot == null)
            {
                MessageBox.Show(
                    HwTestLocalization.Pick("Restartujte računar i uđite u UEFI BIOS podešavanja (HP: F10, Lenovo: F1, Dell: F2) kako biste uključili uređaj.",
                        "请重启电脑并进入 UEFI BIOS 设置（HP: F10, Lenovo: F1, Dell: F2）以启用设备。"),
                    HwTestLocalization.Pick("Restart u UEFI BIOS", "重启进入 UEFI BIOS"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                SafeCloseDialog(false);
                return;
            }

            SafeCloseDialog(false);
            OnRequestUefiBiosReboot(testId);
        }

        private TextBlock CreateBadge(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 190)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 20, 0)
            };
        }

        private void SetupTargets()
        {
            double w = _testCanvas.ActualWidth;
            double h = _testCanvas.ActualHeight;
            if (w < 100 || h < 100) return;

            double radius = 50;
            Point[] pts = new Point[]
            {
                new Point(80, 80),
                new Point(w - 80 - radius*2, 80),
                new Point(80, h - 80 - radius*2),
                new Point(w - 80 - radius*2, h - 80 - radius*2)
            };
            string[] tpLabels = new string[] { "↖ TP 1", "↗ TP 2", "↙ TP 3", "↘ TP 4" };

            for (int i = 0; i < pts.Length; i++)
            {
                Ellipse el = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = _hasTrackPoint
                        ? new SolidColorBrush(Color.FromArgb(115, 220, 53, 69))
                        : new SolidColorBrush(Color.FromArgb(100, 78, 201, 176)),
                    Stroke = _hasTrackPoint
                        ? new SolidColorBrush(Color.FromRgb(235, 75, 88))
                        : new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                    StrokeThickness = 3,
                    Tag = i
                };
                Canvas.SetLeft(el, pts[i].X);
                Canvas.SetTop(el, pts[i].Y);
                _testCanvas.Children.Add(el);
                _targets.Add(el);

                if (_hasTrackPoint)
                {
                    TextBlock lbl = new TextBlock
                    {
                        Text = tpLabels[i],
                        Foreground = Brushes.White,
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Width = radius * 2,
                        TextAlignment = TextAlignment.Center,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(lbl, pts[i].X);
                    Canvas.SetTop(lbl, pts[i].Y + radius - 10);
                    _testCanvas.Children.Add(lbl);
                }
            }

            if (_hasTrackPoint)
            {
                _tpVisualBox = BuildTrackPointDiagram();
                Canvas.SetLeft(_tpVisualBox, Math.Max(20, (w - 440) / 2.0));
                Canvas.SetTop(_tpVisualBox, Math.Max(20, (h - 210) / 2.0));
                _testCanvas.Children.Add(_tpVisualBox);
            }
        }

        private Border BuildTrackPointDiagram()
        {
            Border box = new Border
            {
                Width = 440,
                Background = new SolidColorBrush(Color.FromRgb(32, 35, 44)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 82, 98)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14)
            };
            box.MouseDown += OnCanvasMouseDown;

            StackPanel sp = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            StackPanel spTitle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
            spTitle.Children.Add(new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(Color.FromRgb(215, 40, 50)),
                Stroke = Brushes.White,
                StrokeThickness = 1.2,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            spTitle.Children.Add(new TextBlock
            {
                Text = HwTestLocalization.Pick("TrackPoint tasteri (gornja 3 tastera iznad touchpada)", "TrackPoint 按键（触控板上方的 3 个物理按键）"),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(spTitle);

            sp.Children.Add(new TextBlock
            {
                Text = HwTestLocalization.Pick("Srednji taster: kliknite SREDNJI, ili ga DRŽITE + pomerite crveni TrackPoint gore/dole (Scroll mod)",
                    "中键测试：直接点击中键，或按住中键并上下推动红色指点杆（滚动模式）"),
                Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 210)),
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 10)
            });

            StackPanel spBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _tpBtnLeft = CreateTpButtonVisual(HwTestLocalization.Pick("TP LEVI", "TP 左键"), 122);
            _tpBtnMiddle = CreateTpButtonVisual(HwTestLocalization.Pick("TP SREDNJI", "TP 中键"), 122);
            _tpBtnRight = CreateTpButtonVisual(HwTestLocalization.Pick("TP DESNI", "TP 右键"), 122);
            _tpBtnLeft.Margin = new Thickness(0, 0, 8, 0);
            _tpBtnMiddle.Margin = new Thickness(0, 0, 8, 0);
            spBtns.Children.Add(_tpBtnLeft);
            spBtns.Children.Add(_tpBtnMiddle);
            spBtns.Children.Add(_tpBtnRight);
            sp.Children.Add(spBtns);

            WrapPanel wpActions = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Button btnBoxPass = new Button
            {
                Content = HwTestLocalization.Pick("✓ Srednji taster radi (Scroll / driver mod)", "✓ 中键正常（滚动 / 驱动模式）"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnBoxPass.Click += (s, e) => MarkMiddleClickDone("ručna potvrda (Scroll / driver mod)");
            wpActions.Children.Add(btnBoxPass);

            Button btnBoxFail = new Button
            {
                Content = HwTestLocalization.Pick("✗ Srednji TrackPoint taster NE RADI (FAIL)", "✗ TrackPoint 中键故障 (FAIL)"),
                Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 6, 10, 6)
            };
            btnBoxFail.Click += (s, e) => FinishMiddleButtonFail();
            wpActions.Children.Add(btnBoxFail);

            sp.Children.Add(wpActions);

            box.Child = sp;
            return box;
        }

        private static Border CreateTpButtonVisual(string label, double width)
        {
            return new Border
            {
                Width = width,
                Height = 42,
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 64)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(95, 102, 120)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private static void HighlightTpButton(Border b, string text)
        {
            if (b == null) return;
            b.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            b.BorderBrush = Brushes.White;
            TextBlock tb = b.Child as TextBlock;
            if (tb != null) tb.Text = text;
        }

        private bool _showingTouchpadWarning;

        private void ShowTrackPointTouchpadWarning()
        {
            ShowAntiCheatWarning("TOUCHPAD");
        }

        private void ShowAntiCheatWarning(string blockedSource)
        {
            _showingTouchpadWarning = true;
            _instructions.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));

            if (_hasTrackPoint)
            {
                _instructions.Text = HwTestLocalization.Pick("⛔ Detektovan Touchpad / USB miš — koristite isključivo crveni TrackPoint!", "⛔ 检测到触控板或外接鼠标 — 请仅使用红色 TrackPoint 指点杆！");
                if (_antiCheatSubHint != null)
                {
                    if (blockedSource == "TOUCHSCREEN")
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⛔ Touchscreen dodir nije dozvoljen — koristite isključivo crveni TrackPoint!", "⛔ 不允许使用触控屏 — 请仅使用红色 TrackPoint 指点杆！");
                    else if (blockedSource == "EXTERNAL_MOUSE")
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⛔ Spoljni USB/Bluetooth miš nije dozvoljen za test — za prijavu kvara koristite footer dugme ili F9.", "⛔ 测试不允许使用外接 USB/蓝牙鼠标 — 如需报告故障请使用底部按钮或按 F9。");
                    else
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⚠️ Koristite crveni TrackPoint džojstik, ne touchpad!", "⚠️ 请使用红色 TrackPoint 指点杆，不要使用触控板！");
                    _antiCheatSubHint.Visibility = Visibility.Visible;
                }
            }
            else
            {
                _instructions.Text = HwTestLocalization.Pick("⛔ Detektovan USB miš / TrackPoint — koristite isključivo Touchpad!", "⛔ 检测到外接鼠标或指点杆 — 请仅使用触控板！");
                if (_antiCheatSubHint != null)
                {
                    if (blockedSource == "TOUCHSCREEN")
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⛔ Touchscreen dodir nije dozvoljen — koristite isključivo Touchpad!", "⛔ 不允许使用触控屏 — 请仅使用触控板！");
                    else if (blockedSource == "TRACKPOINT")
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⛔ TrackPoint nije dozvoljen u testu touchpada — koristite isključivo Touchpad!", "⛔ 触控板测试中不允许使用指点杆 — 请仅使用触控板！");
                    else
                        _antiCheatSubHint.Text = HwTestLocalization.Pick("⛔ Spoljni USB/Bluetooth miš nije dozvoljen za test — za prijavu kvara koristite footer dugme ili F9.", "⛔ 测试不允许使用外接 USB/蓝牙鼠标 — 如需报告故障请使用底部按钮或按 F9。");
                    _antiCheatSubHint.Visibility = Visibility.Visible;
                }
            }
        }

        private void ClearTrackPointTouchpadWarning()
        {
            if (!_showingTouchpadWarning) return;
            _showingTouchpadWarning = false;
            _instructions.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            if (_antiCheatSubHint != null)
            {
                _antiCheatSubHint.Text = "";
                _antiCheatSubHint.Visibility = Visibility.Collapsed;
            }
            UpdateNextInstruction();
        }

        private void RecordAntiCheatBlock(string source)
        {
            if (source == "TOUCHPAD") _blockedTouchpadEvents++;
            else if (source == "EXTERNAL_MOUSE") _blockedExternalMouseEvents++;
            else if (source == "TRACKPOINT") _blockedTrackPointEvents++;
            else if (source == "TOUCHSCREEN") _blockedTouchscreenEvents++;

            ShowAntiCheatWarning(source);

            int now = Environment.TickCount;
            if (unchecked((uint)(now - _lastAntiCheatLogTicks)) >= 1500u)
            {
                _lastAntiCheatLogTicks = now;
                HwTestLogger.Warn("TOUCHPAD_ANTICHEAT", string.Format(
                    "Odbijen nedozvoljen ulaz [{0}] u modu {1} (TP={2}, EXT={3}, TRK={4}, TS={5})",
                    source, _hasTrackPoint ? "TrackPointOnly" : "TouchpadOnly",
                    _blockedTouchpadEvents, _blockedExternalMouseEvents, _blockedTrackPointEvents, _blockedTouchscreenEvents));
            }
        }

        private bool CheckAndHandleAntiCheatOnCanvas(MouseEventArgs e, out string activeSource)
        {
            activeSource = "";
            int now = Environment.TickCount;

            if (e != null && e.StylusDevice != null)
            {
                activeSource = "TOUCHSCREEN";
            }
            else if (unchecked((uint)(now - _lastTouchscreenHidTicks)) <= 200u)
            {
                activeSource = "TOUCHSCREEN";
            }
            else if (unchecked((uint)(now - _lastMouseRawTicks)) <= 160u && !string.IsNullOrEmpty(_lastMouseRawSource))
            {
                activeSource = _lastMouseRawSource;
            }

            if (_hasTrackPoint &&
                (activeSource == "" || activeSource == "SHARED_PS2") &&
                _hasPrecisionTouchpadHid &&
                _hasSeenTouchpadHidPacket &&
                unchecked((uint)(now - _lastTouchpadHidTicks)) <= 200u)
            {
                activeSource = "TOUCHPAD";
            }

            if (_hasTrackPoint)
            {
                if (activeSource == "TOUCHPAD" || activeSource == "EXTERNAL_MOUSE" || activeSource == "TOUCHSCREEN")
                {
                    RecordAntiCheatBlock(activeSource);
                    return false;
                }
            }
            else
            {
                if (activeSource == "EXTERNAL_MOUSE" || activeSource == "TRACKPOINT" || activeSource == "TOUCHSCREEN")
                {
                    RecordAntiCheatBlock(activeSource);
                    return false;
                }
            }

            ClearTrackPointTouchpadWarning();
            return true;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_isClosingOrDone) return;

            string activeSource;
            if (!CheckAndHandleAntiCheatOnCanvas(e, out activeSource))
            {
                return;
            }

            Point p = e.GetPosition(_testCanvas);
            bool allowTrackPointMove = true;
            if (_hasTrackPoint && _hasPrecisionTouchpadHid)
            {
                allowTrackPointMove = IsRecentRawTrackPointSource();
                if (allowTrackPointMove)
                {
                    ClearTrackPointTouchpadWarning();
                }
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                if (_visitedTargets.Contains(i)) continue;

                double l = Canvas.GetLeft(_targets[i]);
                double t = Canvas.GetTop(_targets[i]);
                Rect r = new Rect(l, t, _targets[i].Width, _targets[i].Height);

                if (r.Contains(p))
                {
                    if (_hasTrackPoint && _hasPrecisionTouchpadHid && !allowTrackPointMove)
                    {
                        RecordAntiCheatBlock("TOUCHPAD");
                        continue;
                    }
                    _visitedTargets.Add(i);
                    _targets[i].Fill = new SolidColorBrush(Color.FromArgb(200, 40, 167, 69));
                    _targets[i].Stroke = Brushes.White;
                }
            }

            _statusMove.Text = _hasTrackPoint
                ? HwTestLocalization.PickFormat("1. TrackPoint kretanje (4 mete): {0}/4", "1. 指点杆移动（4 个目标）: {0}/4", _visitedTargets.Count)
                : HwTestLocalization.PickFormat("1. Kretanje (4 ugla): {0}/4", "1. 光标移动（4 个角落）: {0}/4", _visitedTargets.Count);
            if (_visitedTargets.Count >= 4)
            {
                _statusMove.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _statusMove.Text = _hasTrackPoint
                    ? HwTestLocalization.Pick("1. TrackPoint kretanje (4 mete): ✓ Prošlo", "1. 指点杆移动（4 个目标）: ✓ 通过")
                    : HwTestLocalization.Pick("1. Kretanje (4 ugla): ✓ Prošlo", "1. 光标移动（4 个角落）: ✓ 通过");
                UpdateNextInstruction();
                CheckAllPassed();
            }
        }

        private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_isClosingOrDone) return;
            string activeSource;
            if (!CheckAndHandleAntiCheatOnCanvas(e, out activeSource))
            {
                return;
            }

            if (_hasTrackPoint)
            {
                if (_hasPrecisionTouchpadHid)
                {
                    if (IsRecentRawTrackPointSource())
                    {
                        ClearTrackPointTouchpadWarning();
                        MarkTrackPointLeftDone("RawInput (bez Touchpad kontakta)");
                    }
                    else
                    {
                        RecordAntiCheatBlock("TOUCHPAD");
                    }
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastMouseRawTicks)) <= 120u && _lastMouseRawSource == "TOUCHPAD")
                {
                    RecordAntiCheatBlock("TOUCHPAD");
                    return;
                }
                ClearTrackPointTouchpadWarning();
                MarkTrackPointLeftDone("zajednički PS/2 ulaz");
                return;
            }
            MarkTouchpadLeftDone();
        }

        private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
        {
            if (_isClosingOrDone) return;
            string activeSource;
            if (!CheckAndHandleAntiCheatOnCanvas(e, out activeSource))
            {
                return;
            }

            if (_hasTrackPoint)
            {
                if (_hasPrecisionTouchpadHid)
                {
                    if (IsRecentRawTrackPointSource())
                    {
                        ClearTrackPointTouchpadWarning();
                        MarkTrackPointRightDone("RawInput (bez Touchpad kontakta)");
                    }
                    else
                    {
                        RecordAntiCheatBlock("TOUCHPAD");
                    }
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastMouseRawTicks)) <= 120u && _lastMouseRawSource == "TOUCHPAD")
                {
                    RecordAntiCheatBlock("TOUCHPAD");
                    return;
                }
                ClearTrackPointTouchpadWarning();
                MarkTrackPointRightDone("zajednički PS/2 ulaz");
                return;
            }
            MarkTouchpadRightDone();
        }

        private bool IsRecentRawTrackPointSource()
        {
            int now = Environment.TickCount;
            if (unchecked((uint)(now - _lastMouseRawTicks)) <= 120u && _lastMouseRawSource == "TRACKPOINT")
            {
                return true;
            }
            // Ako postoji Precision Touchpad HID i nije bilo kontakta u poslednjih 200ms, ulaz nije došao sa touchpada
            if (_hasPrecisionTouchpadHid && (!_hasSeenTouchpadHidPacket || unchecked((uint)(now - _lastTouchpadHidTicks)) > 200u))
            {
                return true;
            }
            return false;
        }

        private void MarkTouchpadLeftDone()
        {
            if (_hasTrackPoint || _isClosingOrDone) return;
            if (!_leftClickDone)
            {
                _leftClickDone = true;
                _statusLeft.Text = HwTestLocalization.Pick("2. Levi klik: ✓ Prošlo", "2. 左键点击: ✓ 通过");
                _statusLeft.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void MarkTouchpadRightDone()
        {
            if (_hasTrackPoint || _isClosingOrDone) return;
            if (!_rightClickDone)
            {
                _rightClickDone = true;
                _statusRight.Text = HwTestLocalization.Pick("3. Desni klik: ✓ Prošlo", "3. 右键点击: ✓ 通过");
                _statusRight.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void MarkTrackPointLeftDone(string source)
        {
            if (!_hasTrackPoint || _tpLeftDone || _isClosingOrDone) return;
            _tpLeftDone = true;
            if (_statusTpLeft != null)
            {
                _statusTpLeft.Text = HwTestLocalization.Pick("2. TrackPoint levi: ✓ Prošlo", "2. 指点杆左键: ✓ 通过");
                _statusTpLeft.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnLeft, HwTestLocalization.Pick("TP LEVI ✓", "TP 左键 ✓"));
            HwTestLogger.Info("TOUCHPAD", "TrackPoint levi klik potvrđen: " + source);
            ClearTrackPointTouchpadWarning();
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void MarkTrackPointRightDone(string source)
        {
            if (!_hasTrackPoint || _tpRightDone || _isClosingOrDone) return;
            _tpRightDone = true;
            if (_statusTpRight != null)
            {
                _statusTpRight.Text = HwTestLocalization.Pick("4. TrackPoint desni: ✓ Prošlo", "4. 指点杆右键: ✓ 通过");
                _statusTpRight.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnRight, HwTestLocalization.Pick("TP DESNI ✓", "TP 右键 ✓"));
            HwTestLogger.Info("TOUCHPAD", "TrackPoint desni klik potvrđen: " + source);
            ClearTrackPointTouchpadWarning();
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private int _lastForbiddenMiddleClickTicks;

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_hasTrackPoint || _isClosingOrDone) return;
            if (_footerBorder != null && _footerBorder.IsMouseOver) return;

            if (e.ChangedButton == MouseButton.Middle || e.MiddleButton == MouseButtonState.Pressed)
            {
                string activeSource;
                if (!CheckAndHandleAntiCheatOnCanvas(e, out activeSource))
                {
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastForbiddenMiddleClickTicks)) <= 200u)
                {
                    return;
                }
                if (e.ChangedButton == MouseButton.Middle && !_middleClickDone)
                {
                    MarkMiddleClickDone("hardverski srednji taster");
                }
                else if (e.MiddleButton == MouseButtonState.Pressed && !_middleClickDone)
                {
                    MarkMiddleClickDone("hardverski srednji taster (MiddleButton)");
                }
            }
        }

        private void MarkMiddleClickDone(string source)
        {
            if (!_hasTrackPoint || _middleClickDone || _isClosingOrDone) return;
            _middleClickDone = true;
            if (_statusMiddle != null)
            {
                _statusMiddle.Text = HwTestLocalization.Pick("3. TrackPoint srednji / Scroll: ✓ Prošlo", "3. 指点杆中键 / 滚动: ✓ 通过");
                _statusMiddle.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnMiddle, HwTestLocalization.Pick("TP SREDNJI ✓", "TP 中键 ✓"));
            HwTestLogger.Info("TOUCHPAD", "TrackPoint srednji klik potvrđen: " + source);
            ClearTrackPointTouchpadWarning();
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isClosingOrDone) return;

            string activeSource;
            if (!CheckAndHandleAntiCheatOnCanvas(e, out activeSource))
            {
                return;
            }

            if (_hasTrackPoint)
            {
                if (!_middleClickDone)
                {
                    int now = Environment.TickCount;
                    // Ako nema nedavnog Precision Touchpad HID kontakta u zadnjih 220ms, skrol potiče od TrackPoint Scroll moda
                    if (!_hasPrecisionTouchpadHid || !_hasSeenTouchpadHidPacket || unchecked((uint)(now - _lastTouchpadHidTicks)) > 220u)
                    {
                        MarkMiddleClickDone("TrackPoint Scroll mod (Srednji taster + džojstik)");
                    }
                    else
                    {
                        RecordAntiCheatBlock("TOUCHPAD");
                    }
                }
                return;
            }

            if (e.Delta > 0) _scrollUpDone = true;
            if (e.Delta < 0) _scrollDownDone = true;

            if (_scrollUpDone && _scrollDownDone)
            {
                _statusScroll.Text = HwTestLocalization.Pick("4. Skrol (2 prsta): ✓ Prošlo (Gore i Dole)", "4. 双指滚动: ✓ 通过（上下均已验证）");
                _statusScroll.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                UpdateNextInstruction();
                CheckAllPassed();
            }
            else
            {
                _statusScroll.Text = HwTestLocalization.PickFormat("4. Skrol (2 prsta): {0}{1}", "4. 双指滚动: {0}{1}",
                    _scrollUpDone ? HwTestLocalization.Pick("▲ Gore OK ", "▲ 向上正常 ") : HwTestLocalization.Pick("▲ Gore čeka ", "▲ 等待向上 "),
                    _scrollDownDone ? HwTestLocalization.Pick("▼ Dole OK", "▼ 向下正常") : HwTestLocalization.Pick("▼ Dole čeka", "▼ 等待向下"));
            }
        }

        private void UpdateNextInstruction()
        {
            if (_showingTouchpadWarning) return;

            if (_hasTrackPoint)
            {
                if (_visitedTargets.Count < 4)
                {
                    _instructions.Text = HwTestLocalization.Pick("🔴 Pomerite kursor crvenim TrackPoint džojstikom kroz 4 crvene mete (ne dirajte touchpad!)",
                        "🔴 请使用红色 TrackPoint 指点杆将光标移过 4 个红色目标（请勿触碰触控板！）");
                }
                else if (!_tpLeftDone)
                {
                    _instructions.Text = HwTestLocalization.Pick("🔴 Pritisnite LEVI gornji taster TrackPoint-a (iznad touchpada)", "🔴 请按下 TrackPoint 左上按键（触控板上方）");
                }
                else if (!_tpRightDone)
                {
                    _instructions.Text = HwTestLocalization.Pick("🔴 Pritisnite DESNI gornji taster TrackPoint-a (iznad touchpada)", "🔴 请按下 TrackPoint 右上按键（触控板上方）");
                }
                else if (!_middleClickDone)
                {
                    _instructions.Text = HwTestLocalization.Pick("🔴 SREDNJI TrackPoint taster: kliknite ga ili ga DRŽITE + pomerite crveni džojstik gore/dole (Scroll mod)",
                        "🔴 TrackPoint 中键：请直接点击中键，或按住中键并上下推动红色指点杆（滚动模式）");
                }
                return;
            }

            if (_visitedTargets.Count < 4)
            {
                _instructions.Text = HwTestLocalization.Pick("👉 Pomerite pokazivač touchpada kroz 4 kruga u uglovima ekrana", "👉 请使用触控板将光标移过屏幕四角上的 4 个圆圈");
            }
            else if (!_leftClickDone)
            {
                _instructions.Text = HwTestLocalization.Pick("👉 Pritisnite LEVI klik na touchpadu", "👉 请按下触控板左键");
            }
            else if (!_rightClickDone)
            {
                _instructions.Text = HwTestLocalization.Pick("👉 Pritisnite DESNI klik na touchpadu (dva prsta tap ili desni ugao)", "👉 请按下触控板右键（双指轻点或右下角按压）");
            }
            else if (!_scrollUpDone || !_scrollDownDone)
            {
                _instructions.Text = HwTestLocalization.Pick("👉 Prevucite dva prsta preko touchpada GORE i DOLE (gest skrola)", "👉 请用双指在触控板上向上和向下滑动（双指滚动手势）");
            }
        }

        private void StartMiddleButtonMonitoring()
        {
            SwitchElanMiddleButtonToClickMode();
            OpenElanMiddleButtonEvents();
            StartElanKernelWaitThread();
            InstallMouseHook();
            InstallHwndAndRawInputHook();
            _canSeparateTrackPointButtons = (_hasPrecisionTouchpadHid || _elanEventWatches.Count > 0 || HasDistinctInternalPointingDevices());
            HwTestLogger.Info("TOUCHPAD", string.Format("Razdvajanje Touchpad/TrackPoint: {0} (TouchpadHID={1}, ElanEvents={2}, MouseDevs={3})",
                _canSeparateTrackPointButtons, _hasPrecisionTouchpadHid, _elanEventWatches.Count, _mouseDevicesSeen.Count));

            // Ispraznimo eventualno staro 0x0001 stanje VK_MBUTTON pre pocetka testa
            try { NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON); } catch { }

            if (_middleKeyPollTimer == null)
            {
                _middleKeyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                _middleKeyPollTimer.Tick += (s, e) =>
                {
                    if (_isClosingOrDone || _middleClickDone) return;
                    try
                    {
                        short vkState = NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON);
                        if ((vkState & 0x8001) != 0)
                        {
                            int now = Environment.TickCount;
                            if (unchecked((uint)(now - _lastForbiddenMiddleClickTicks)) <= 220u ||
                                (unchecked((uint)(now - _lastMouseRawTicks)) <= 120u &&
                                 (_lastMouseRawSource == "EXTERNAL_MOUSE" || _lastMouseRawSource == "TOUCHPAD" || _lastMouseRawSource == "TOUCHSCREEN")))
                            {
                                return;
                            }
                            MarkMiddleClickDone("VK_MBUTTON hardversko stanje");
                            return;
                        }
                    }
                    catch { }
                };
                _middleKeyPollTimer.Start();
            }
        }

        private void StopMiddleButtonMonitoring()
        {
            if (_middleKeyPollTimer != null)
            {
                try { _middleKeyPollTimer.Stop(); } catch { }
                _middleKeyPollTimer = null;
            }
            StopElanKernelWaitThread();
            UninstallHwndAndRawInputHook();
            UninstallMouseHook();
            CloseElanMiddleButtonEvents();
            RestoreElanMiddleButtonMode();
        }

        private void CleanupAllResourcesAndRestoreTouchpad()
        {
            RestoreTemporaryTouchpadDisableAndCleanupFile();
            UnhookAppCrashRecoveryHandlers();
            StopMiddleButtonMonitoring();
        }

        protected override void OnClosed(EventArgs e)
        {
            CleanupAllResourcesAndRestoreTouchpad();
            base.OnClosed(e);
        }

        private string ResolveRestoreFilePath()
        {
            if (!string.IsNullOrEmpty(_restoreStateFilePath)) return _restoreStateFilePath;
            try
            {
                if (Directory.Exists(@"C:\setup"))
                {
                    return @"C:\setup\hwtest-touchpad-restore.json";
                }
            }
            catch { }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                return System.IO.Path.Combine(baseDir, "hwtest-touchpad-restore.json");
            }
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hwtest-touchpad-restore.json");
        }

        private static int ReadElanTouchpadEnableValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Elantech\SmartPad", false))
                {
                    if (key == null) return -1;
                    object v = key.GetValue("TouchPad_Enable");
                    if (v == null) return -1;
                    int parsed;
                    if (int.TryParse(v.ToString(), out parsed)) return parsed;
                }
            }
            catch { }
            return -1;
        }

        private static bool WriteElanTouchpadEnableValue(int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Elantech\SmartPad", true))
                {
                    if (key == null) return false;
                    key.SetValue("TouchPad_Enable", value, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void PulseElanTouchpadEnableEvents(bool enable)
        {
            PulseNamedEvents(new string[]
            {
                enable ? @"Global\RegTouchPadEnable" : @"Global\RegTouchPadDisable",
                @"Global\ETD_TP_Enable_Update",
                @"Global\EPDMessage_HSA_TP_PST_UI_Set",
                @"Global\EPDCtrlRegNotifyEvent_Start",
                @"Global\EPDCombo_UpdateRegistry",
                @"Global\ETDCombo_UpdateRegistry",
                @"Global\MainPage_Realtime_UpdateUI"
            });
        }

        private bool HasDistinctInternalPointingDevices()
        {
            try
            {
                List<NativeMethods.RawInputDeviceMeta> metas = NativeMethods.EnumRawInputDevicesMeta();
                bool hasTouchpad = _hasPrecisionTouchpadHid;
                bool hasTrackPoint = false;
                for (int i = 0; i < metas.Count; i++)
                {
                    string cls = ClassifyMouseRawDevice(metas[i].Handle, metas[i].DeviceName);
                    if (cls == "TOUCHPAD") hasTouchpad = true;
                    else if (cls == "TRACKPOINT") hasTrackPoint = true;
                }
                return hasTouchpad && hasTrackPoint;
            }
            catch { return false; }
        }

        private bool HasIndependentElanController(int origElanTouchpadEnable)
        {
            if (origElanTouchpadEnable < 0) return false;

            bool hasPointStickReg = false;
            try
            {
                using (RegistryKey k1 = Registry.CurrentUser.OpenSubKey(@"Software\Elantech\PointStick", false))
                {
                    if (k1 != null) hasPointStickReg = true;
                }
                if (!hasPointStickReg)
                {
                    using (RegistryKey k2 = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Elantech\PointStick", false))
                    {
                        if (k2 != null) hasPointStickReg = true;
                    }
                }
            }
            catch { }

            if (!hasPointStickReg && _elanEventWatches.Count == 0 && (_hwInfo == null || !_hwInfo.HasTrackPoint))
            {
                return false;
            }

            try
            {
                List<NativeMethods.RawInputDeviceMeta> metas = NativeMethods.EnumRawInputDevicesMeta();
                int internalCount = 0;
                bool hasElanHidOrTouchpad = _hasPrecisionTouchpadHid;
                for (int i = 0; i < metas.Count; i++)
                {
                    NativeMethods.RawInputDeviceMeta m = metas[i];
                    string up = (m.DeviceName ?? "").ToUpperInvariant();
                    if (m.RawType == NativeMethods.RIM_TYPEHID && m.UsagePage == 0x0D && m.Usage == 0x05)
                    {
                        hasElanHidOrTouchpad = true;
                    }
                    if (up.IndexOf("ELAN") >= 0 || up.IndexOf("ETD") >= 0 || up.IndexOf("VEN_04F3") >= 0 || up.IndexOf("VID_04F3") >= 0)
                    {
                        hasElanHidOrTouchpad = true;
                    }
                    if (m.RawType == NativeMethods.RIM_TYPEMOUSE)
                    {
                        string cls = ClassifyMouseRawDevice(m.Handle, m.DeviceName);
                        if (cls != "EXTERNAL_MOUSE" && cls != "TOUCHSCREEN")
                        {
                            internalCount++;
                        }
                    }
                }
                return hasElanHidOrTouchpad && (internalCount >= 2 || _hasPrecisionTouchpadHid || _elanEventWatches.Count > 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool WriteRestoreFileAtomic(
            string restorePath,
            string disableMethod,
            bool? origSpiEnabled,
            bool spiModified,
            int origElanTouchpadEnable,
            bool elanModified)
        {
            if (string.IsNullOrEmpty(restorePath)) return false;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(restorePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["schema"] = "wis.hwtest.touchpad_restore.v1";
                payload["saved_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                payload["disable_method"] = disableMethod;
                if (origSpiEnabled.HasValue)
                    payload["original_spi_enabled"] = origSpiEnabled.Value;
                else
                    payload["original_spi_enabled"] = null;
                payload["spi_modified"] = spiModified;
                if (origElanTouchpadEnable >= 0)
                    payload["original_elan_touchpad_enable"] = origElanTouchpadEnable;
                else
                    payload["original_elan_touchpad_enable"] = null;
                payload["elan_hkcu_modified"] = elanModified;

                string json = SimpleJson.Serialize(payload);
                string tmpPath = restorePath + ".tmp";
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                if (File.Exists(restorePath))
                {
                    try
                    {
                        File.Replace(tmpPath, restorePath, null);
                    }
                    catch
                    {
                        File.Delete(restorePath);
                        File.Move(tmpPath, restorePath);
                    }
                }
                else
                {
                    File.Move(tmpPath, restorePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("TOUCHPAD", "Neuspešan upis hwtest-touchpad-restore.json: " + ex.Message);
                return false;
            }
        }

        private static void TryDeleteFileQuiet(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        public static bool TryRecoverTouchpadFromRestoreFile(string restorePath)
        {
            if (string.IsNullOrEmpty(restorePath) || !File.Exists(restorePath)) return false;
            try
            {
                string json = File.ReadAllText(restorePath, Encoding.UTF8);
                if (!string.IsNullOrEmpty(json))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    IDictionary<string, object> root = ser.DeserializeObject(json) as IDictionary<string, object>;
                    if (root != null)
                    {
                        string method = "";
                        object mObj;
                        if (root.TryGetValue("disable_method", out mObj) && mObj != null)
                        {
                            method = Convert.ToString(mObj, CultureInfo.InvariantCulture) ?? "";
                        }

                        bool spiModified = false;
                        object smObj;
                        if (root.TryGetValue("spi_modified", out smObj) && smObj != null)
                        {
                            try { spiModified = Convert.ToBoolean(smObj, CultureInfo.InvariantCulture); } catch { }
                        }

                        object origSpiObj;
                        if ((spiModified || method == "SPI_TOUCHPADPARAMETERS") &&
                            root.TryGetValue("original_spi_enabled", out origSpiObj) && origSpiObj != null)
                        {
                            bool origSpi = Convert.ToBoolean(origSpiObj, CultureInfo.InvariantCulture);
                            bool dummyPrev;
                            bool spiOk = NativeMethods.TrySetPrecisionTouchpadEnabled(origSpi, out dummyPrev);
                            HwTestLogger.Info("TOUCHPAD_RECOVERY", string.Format(
                                "Startup/Crash recovery: Precision Touchpad vraćen na {0} (ok={1}).", origSpi, spiOk));
                        }

                        bool elanModified = false;
                        object emObj;
                        if (root.TryGetValue("elan_hkcu_modified", out emObj) && emObj != null)
                        {
                            try { elanModified = Convert.ToBoolean(emObj, CultureInfo.InvariantCulture); } catch { }
                        }

                        object origElanObj;
                        if ((elanModified || method == "ELAN_REGISTRY") &&
                            root.TryGetValue("original_elan_touchpad_enable", out origElanObj) && origElanObj != null)
                        {
                            int origElan;
                            if (int.TryParse(Convert.ToString(origElanObj, CultureInfo.InvariantCulture), out origElan) && origElan >= 0)
                            {
                                WriteElanTouchpadEnableValue(origElan);
                                PulseElanTouchpadEnableEvents(origElan != 0);
                                HwTestLogger.Info("TOUCHPAD_RECOVERY", string.Format(
                                    "Startup/Crash recovery: ELAN TouchPad_Enable vraćen na {0}.", origElan));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("TOUCHPAD_RECOVERY", "Greška pri oporavku touchpada iz restore fajla: " + ex.Message);
            }
            finally
            {
                TryDeleteFileQuiet(restorePath);
                TryDeleteFileQuiet(restorePath + ".tmp");
            }
            return true;
        }

        private void HookAppCrashRecoveryHandlers()
        {
            if (_recoveryHandlersHooked) return;
            _recoveryHandlersHooked = true;

            _processExitHandler = (s, e) =>
            {
                try { RestoreTemporaryTouchpadDisableAndCleanupFile(); } catch { }
            };
            try { AppDomain.CurrentDomain.ProcessExit += _processExitHandler; } catch { }

            _appDispatcherExceptionHandler = (s, e) =>
            {
                try { RestoreTemporaryTouchpadDisableAndCleanupFile(); } catch { }
            };
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.DispatcherUnhandledException += _appDispatcherExceptionHandler;
                }
                else
                {
                    _localDispatcherExceptionHandler = _appDispatcherExceptionHandler;
                    Dispatcher.UnhandledException += _localDispatcherExceptionHandler;
                }
            }
            catch { }
        }

        private void UnhookAppCrashRecoveryHandlers()
        {
            if (!_recoveryHandlersHooked) return;
            _recoveryHandlersHooked = false;

            if (_processExitHandler != null)
            {
                try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; } catch { }
                _processExitHandler = null;
            }
            if (_appDispatcherExceptionHandler != null)
            {
                try
                {
                    if (Application.Current != null)
                    {
                        Application.Current.DispatcherUnhandledException -= _appDispatcherExceptionHandler;
                    }
                }
                catch { }
                _appDispatcherExceptionHandler = null;
            }
            if (_localDispatcherExceptionHandler != null)
            {
                try { Dispatcher.UnhandledException -= _localDispatcherExceptionHandler; } catch { }
                _localDispatcherExceptionHandler = null;
            }
        }

        private void ApplyTemporaryTouchpadDisableForTrackPoint()
        {
            if (!_hasTrackPoint) return;

            string restorePath = ResolveRestoreFilePath();
            bool origSpiEnabled = false;
            bool spiSupported = NativeMethods.TryGetPrecisionTouchpadEnabled(out origSpiEnabled);
            int origElanVal = ReadElanTouchpadEnableValue();

            if (spiSupported)
            {
                if (WriteRestoreFileAtomic(restorePath, "SPI_TOUCHPADPARAMETERS", origSpiEnabled, true, origElanVal, false))
                {
                    HookAppCrashRecoveryHandlers();
                    bool prevSpi;
                    if (NativeMethods.TrySetPrecisionTouchpadEnabled(false, out prevSpi))
                    {
                        lock (_touchpadRestoreLock)
                        {
                            _touchpadRestored = false;
                            _spiTouchpadSwitched = true;
                            _origSpiTouchpadEnabled = prevSpi;
                            _touchpadTemporarilyDisabled = true;
                            _touchpadDisableMethod = "SPI_TOUCHPADPARAMETERS";
                        }
                        HwTestLogger.Info("TOUCHPAD", string.Format(
                            "Sloj 1 Anti-Cheat: Precision Touchpad privremeno isključen (SPI_SETTOUCHPADPARAMETERS, original={0}, restore={1})",
                            prevSpi, restorePath));
                        return;
                    }
                    TryDeleteFileQuiet(restorePath);
                    TryDeleteFileQuiet(restorePath + ".tmp");
                }
            }

            if (origElanVal >= 0 && HasIndependentElanController(origElanVal))
            {
                if (WriteRestoreFileAtomic(restorePath, "ELAN_REGISTRY", null, false, origElanVal, true))
                {
                    HookAppCrashRecoveryHandlers();
                    bool switched = (origElanVal == 0) || WriteElanTouchpadEnableValue(0);
                    if (switched)
                    {
                        if (origElanVal != 0) PulseElanTouchpadEnableEvents(false);
                        lock (_touchpadRestoreLock)
                        {
                            _touchpadRestored = false;
                            _elanTouchpadEnableSwitched = true;
                            _origElanTouchpadEnable = origElanVal;
                            _touchpadTemporarilyDisabled = true;
                            _touchpadDisableMethod = "ELAN_REGISTRY";
                        }
                        HwTestLogger.Info("TOUCHPAD", string.Format(
                            "Sloj 1 Anti-Cheat (Win10 fallback): ELAN SmartPad TouchPad_Enable privremeno postavljen {0} -> 0 (restore={1})",
                            origElanVal, restorePath));
                        return;
                    }
                    TryDeleteFileQuiet(restorePath);
                    TryDeleteFileQuiet(restorePath + ".tmp");
                }
            }

            lock (_touchpadRestoreLock)
            {
                _touchpadTemporarilyDisabled = false;
                _touchpadDisableMethod = "RAWINPUT_ONLY";
            }
            HwTestLogger.Info("TOUCHPAD", "Sloj 1 privremeno gašenje touchpada nije primenljivo — aktivan Sloj 2 filter (touchpad_disable_method=RAWINPUT_ONLY).");
        }

        private void RestoreTemporaryTouchpadDisableAndCleanupFile()
        {
            string restorePath = ResolveRestoreFilePath();
            lock (_touchpadRestoreLock)
            {
                if (_touchpadRestored)
                {
                    TryDeleteFileQuiet(restorePath);
                    TryDeleteFileQuiet(restorePath + ".tmp");
                    return;
                }
                _touchpadRestored = true;

                if (_spiTouchpadSwitched)
                {
                    _spiTouchpadSwitched = false;
                    bool dummyPrev;
                    bool ok = NativeMethods.TrySetPrecisionTouchpadEnabled(_origSpiTouchpadEnabled, out dummyPrev);
                    HwTestLogger.Info("TOUCHPAD", string.Format(
                        "Restauriran Precision Touchpad preko SPI_SETTOUCHPADPARAMETERS na {0} (ok={1}).",
                        _origSpiTouchpadEnabled, ok));
                }

                if (_elanTouchpadEnableSwitched && _origElanTouchpadEnable >= 0)
                {
                    _elanTouchpadEnableSwitched = false;
                    bool ok = WriteElanTouchpadEnableValue(_origElanTouchpadEnable);
                    PulseElanTouchpadEnableEvents(_origElanTouchpadEnable != 0);
                    HwTestLogger.Info("TOUCHPAD", string.Format(
                        "Restauriran ELAN SmartPad TouchPad_Enable na {0} (ok={1}).",
                        _origElanTouchpadEnable, ok));
                }
            }
            TryDeleteFileQuiet(restorePath);
            TryDeleteFileQuiet(restorePath + ".tmp");
        }

        private static bool TrySwitchPointStickRegistryKey(RegistryKey root, string subKeyPath, out bool switched, out int origVal)
        {
            switched = false;
            origVal = -1;
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKeyPath, true))
                {
                    if (key == null) return false;
                    object valObj = key.GetValue("Button_Middle");
                    if (valObj == null) return false;
                    if (!int.TryParse(valObj.ToString(), out origVal)) return false;
                    if (origVal != 2)
                    {
                        key.SetValue("Button_Middle", 2, RegistryValueKind.DWord);
                        switched = true;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void TryRestorePointStickRegistryKey(RegistryKey root, string subKeyPath, int origVal)
        {
            if (origVal < 0) return;
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKeyPath, true))
                {
                    if (key != null)
                    {
                        key.SetValue("Button_Middle", origVal, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private void SwitchElanMiddleButtonToClickMode()
        {
            bool changedAny = false;
            if (TrySwitchPointStickRegistryKey(Registry.CurrentUser, @"Software\Elantech\PointStick", out _elanHkcuSwitched, out _origElanHkcuButtonMiddle))
            {
                changedAny = true;
                HwTestLogger.Info("TOUCHPAD", string.Format("HKCU\\Elantech\\PointStick\\Button_Middle privremeno prebacen sa {0} na 2 (Middle Click)", _origElanHkcuButtonMiddle));
            }
            if (TrySwitchPointStickRegistryKey(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Elantech\PointStick", out _elanHklmSwitched, out _origElanHklmButtonMiddle))
            {
                changedAny = true;
                HwTestLogger.Info("TOUCHPAD", string.Format("HKLM\\Elantech\\PointStick\\Button_Middle privremeno prebacen sa {0} na 2 (Middle Click)", _origElanHklmButtonMiddle));
            }

            if (changedAny)
            {
                PulseNamedEvents(new string[]
                {
                    @"Global\RegMiddleBtnMiddleClick",
                    @"Global\EPDMessage_HSA_TP_PST_UI_Set",
                    @"Global\EPDCtrlRegNotifyEvent_Start",
                    @"Global\EPDCombo_UpdateRegistry",
                    @"Global\ETDCombo_UpdateRegistry",
                    @"Global\ButtonPage_Realtime_UpdateUI",
                    @"Global\MainPage_Realtime_UpdateUI"
                });
            }
        }

        private void RestoreElanMiddleButtonMode()
        {
            bool restoredAny = false;
            int restoreTarget = 1;
            if (_elanHkcuSwitched)
            {
                _elanHkcuSwitched = false;
                TryRestorePointStickRegistryKey(Registry.CurrentUser, @"Software\Elantech\PointStick", _origElanHkcuButtonMiddle);
                restoreTarget = _origElanHkcuButtonMiddle;
                restoredAny = true;
                HwTestLogger.Info("TOUCHPAD", string.Format("HKCU\\Elantech\\PointStick\\Button_Middle vracen na originalnih {0}", _origElanHkcuButtonMiddle));
            }
            if (_elanHklmSwitched)
            {
                _elanHklmSwitched = false;
                TryRestorePointStickRegistryKey(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Elantech\PointStick", _origElanHklmButtonMiddle);
                restoreTarget = _origElanHklmButtonMiddle;
                restoredAny = true;
                HwTestLogger.Info("TOUCHPAD", string.Format("HKLM\\Elantech\\PointStick\\Button_Middle vracen na originalnih {0}", _origElanHklmButtonMiddle));
            }

            if (restoredAny)
            {
                PulseNamedEvents(new string[]
                {
                    restoreTarget == 1 ? @"Global\RegMiddleBtnScroll" : @"Global\RegMiddleBtnMiddleClick",
                    @"Global\EPDMessage_HSA_TP_PST_UI_Set",
                    @"Global\EPDCtrlRegNotifyEvent_Start",
                    @"Global\EPDCombo_UpdateRegistry",
                    @"Global\ETDCombo_UpdateRegistry",
                    @"Global\ButtonPage_Realtime_UpdateUI",
                    @"Global\MainPage_Realtime_UpdateUI"
                });
            }
        }

        private static void PulseNamedEvents(string[] eventNames)
        {
            for (int i = 0; i < eventNames.Length; i++)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = NativeMethods.OpenEvent(NativeMethods.EVENT_MODIFY_STATE, false, eventNames[i]);
                    if (h != IntPtr.Zero)
                    {
                        NativeMethods.SetEvent(h);
                    }
                }
                catch { }
                finally
                {
                    if (h != IntPtr.Zero)
                    {
                        try { NativeMethods.CloseHandle(h); } catch { }
                    }
                }
            }
        }

        private void OpenElanMiddleButtonEvents()
        {
            CloseElanMiddleButtonEvents();
            string[,] candidates = new string[,]
            {
                { @"Global\EPDPSTGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"Global\ETDPSTGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"Global\ETDGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"Global\EPDGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"Global\EPDPSTGesture_Scroll_Finish", "TP_SCROLL" },
                { @"Global\ETDPSTGesture_Scroll_Finish", "TP_SCROLL" },
                { @"Global\EPDPSTGesture_LeftButton_Down", "TP_LEFT" },
                { @"Global\ETDPSTGesture_LeftButton_Down", "TP_LEFT" },
                { @"Global\EPDPSTGesture_RightButton_Down", "TP_RIGHT" },
                { @"Global\ETDPSTGesture_RightButton_Down", "TP_RIGHT" },
                { @"EPDPSTGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"ETDPSTGesture_MiddleButton_Down", "TP_MIDDLE" },
                { @"ETDGesture_MiddleButton_Down", "TP_MIDDLE" }
            };
            for (int i = 0; i < candidates.GetLength(0); i++)
            {
                string evtName = candidates[i, 0];
                string action = candidates[i, 1];
                try
                {
                    IntPtr h = NativeMethods.OpenEvent(NativeMethods.SYNCHRONIZE, false, evtName);
                    if (h != IntPtr.Zero)
                    {
                        bool staleSignaled = (NativeMethods.WaitForSingleObject(h, 0) == NativeMethods.WAIT_OBJECT_0);
                        _elanEventWatches.Add(new ElanEventWatch
                        {
                            Name = evtName,
                            Action = action,
                            Handle = h,
                            WaitLowBeforeTrigger = staleSignaled
                        });
                    }
                }
                catch { }
            }
            if (_elanEventWatches.Count > 0)
            {
                HwTestLogger.Info("TOUCHPAD", string.Format("Registrovano {0} ELAN TrackPoint event(a) (kernel wait)", _elanEventWatches.Count));
            }
        }

        private void StartElanKernelWaitThread()
        {
            StopElanKernelWaitThread();
            if (_elanEventWatches.Count == 0) return;
            _stopElanWaitThread = false;
            _elanWaitThread = new Thread(() =>
            {
                try
                {
                    while (!_stopElanWaitThread && !_isClosingOrDone)
                    {
                        List<ElanEventWatch> active = new List<ElanEventWatch>();
                        for (int i = 0; i < _elanEventWatches.Count; i++)
                        {
                            ElanEventWatch w = _elanEventWatches[i];
                            if (w.Handle == IntPtr.Zero) continue;
                            if (w.Action == "TP_MIDDLE" && _middleClickDone) continue;
                            if (w.Action == "TP_SCROLL" && _middleClickDone) continue;
                            if (w.Action == "TP_LEFT" && _tpLeftDone) continue;
                            if (w.Action == "TP_RIGHT" && _tpRightDone) continue;
                            if (w.WaitLowBeforeTrigger)
                            {
                                if (NativeMethods.WaitForSingleObject(w.Handle, 0) != NativeMethods.WAIT_OBJECT_0)
                                    w.WaitLowBeforeTrigger = false;
                                continue;
                            }
                            active.Add(w);
                        }
                        if (active.Count == 0)
                        {
                            Thread.Sleep(50);
                            continue;
                        }
                        IntPtr[] handles = new IntPtr[active.Count];
                        for (int i = 0; i < active.Count; i++) handles[i] = active[i].Handle;
                        uint waitRes = NativeMethods.WaitForMultipleObjects((uint)handles.Length, handles, false, 150u);
                        if (waitRes < (uint)active.Count)
                        {
                            ElanEventWatch triggered = active[(int)waitRes];
                            triggered.WaitLowBeforeTrigger = true;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_isClosingOrDone) return;
                                if (triggered.Action == "TP_MIDDLE" || triggered.Action == "TP_SCROLL")
                                {
                                    MarkMiddleClickDone("ELAN kernel event (" + triggered.Name + ")");
                                }
                                else if (triggered.Action == "TP_LEFT")
                                {
                                    MarkTrackPointLeftDone("ELAN kernel event (" + triggered.Name + ")");
                                }
                                else if (triggered.Action == "TP_RIGHT")
                                {
                                    MarkTrackPointRightDone("ELAN kernel event (" + triggered.Name + ")");
                                }
                            }));
                            Thread.Sleep(15);
                        }
                    }
                }
                catch { }
            });
            _elanWaitThread.IsBackground = true;
            _elanWaitThread.Start();
        }

        private void StopElanKernelWaitThread()
        {
            _stopElanWaitThread = true;
            if (_elanWaitThread != null)
            {
                try { _elanWaitThread.Join(250); } catch { }
                _elanWaitThread = null;
            }
        }

        private void CloseElanMiddleButtonEvents()
        {
            for (int i = 0; i < _elanEventWatches.Count; i++)
            {
                IntPtr h = _elanEventWatches[i].Handle;
                if (h != IntPtr.Zero)
                {
                    try { NativeMethods.CloseHandle(h); } catch { }
                }
            }
            _elanEventWatches.Clear();
        }

        private string GetCachedRawDeviceName(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return "";
            string cached;
            if (_rawDeviceNames.TryGetValue(hDevice, out cached)) return cached;
            string name = NativeMethods.GetRawInputDeviceNameString(hDevice);
            _rawDeviceNames[hDevice] = name;
            return name;
        }

        private static string ExtractBaseHidId(string pnpId)
        {
            if (string.IsNullOrEmpty(pnpId)) return "";
            int colIdx = pnpId.IndexOf("&Col", StringComparison.OrdinalIgnoreCase);
            if (colIdx > 0) return pnpId.Substring(0, colIdx);
            return pnpId;
        }

        private void IndexKnownHidCollectionHierarchy(NativeMethods.RawInputDeviceMeta meta, HashSet<string> baseSet, HashSet<string> parentSet)
        {
            if (meta == null || string.IsNullOrEmpty(meta.DeviceName)) return;
            string leaf;
            List<string> ancestry;
            string desc, svc;
            if (NativeMethods.TryGetRawInputPnpHierarchy(meta.DeviceName, out leaf, out ancestry, out desc, out svc))
            {
                string b = ExtractBaseHidId(leaf);
                if (!string.IsNullOrEmpty(b)) baseSet.Add(b);
                for (int i = 1; i < ancestry.Count && i <= 2; i++)
                {
                    if (!string.IsNullOrEmpty(ancestry[i])) parentSet.Add(ancestry[i]);
                }
            }
            else
            {
                string conv = NativeMethods.ConvertRawDevicePathToPnpInstanceId(meta.DeviceName);
                string b = ExtractBaseHidId(conv);
                if (!string.IsNullOrEmpty(b)) baseSet.Add(b);
            }
        }

        private string ClassifyMouseRawDevice(IntPtr hDevice, string devName)
        {
            if (hDevice == IntPtr.Zero)
            {
                if (unchecked((uint)(Environment.TickCount - _lastTouchscreenHidTicks)) <= 200u)
                {
                    return "TOUCHSCREEN";
                }
                return "";
            }

            string cached;
            if (_rawDeviceClassCache.TryGetValue(hDevice, out cached))
            {
                return cached;
            }

            if (string.IsNullOrEmpty(devName))
            {
                devName = NativeMethods.GetRawInputDeviceNameString(hDevice);
            }

            string classification = "SHARED_PS2";
            uint rawType, vid, pid;
            ushort uPage, uId;
            if (NativeMethods.TryGetRawInputDeviceInfoDetails(hDevice, out rawType, out vid, out pid, out uPage, out uId))
            {
                if (rawType == NativeMethods.RIM_TYPEHID)
                {
                    if (uPage == 0x0D && uId == 0x04)
                    {
                        _rawDeviceClassCache[hDevice] = "TOUCHSCREEN";
                        return "TOUCHSCREEN";
                    }
                    if (uPage == 0x0D && uId == 0x05)
                    {
                        _rawDeviceClassCache[hDevice] = "TOUCHPAD";
                        return "TOUCHPAD";
                    }
                }
            }

            string leafPnpId;
            List<string> ancestryIds;
            string deviceDesc, serviceName;
            NativeMethods.TryGetRawInputPnpHierarchy(devName, out leafPnpId, out ancestryIds, out deviceDesc, out serviceName);

            StringBuilder sb = new StringBuilder();
            sb.Append((devName ?? "").ToUpperInvariant()).Append('|');
            sb.Append((leafPnpId ?? "").ToUpperInvariant()).Append('|');
            sb.Append((deviceDesc ?? "").ToUpperInvariant()).Append('|');
            sb.Append((serviceName ?? "").ToUpperInvariant()).Append('|');
            bool hasUsbAncestor = false;
            bool hasBluetoothAncestor = false;
            bool hasPs2Ancestor = false;
            bool matchesTouchpadParent = false;
            bool matchesTouchscreenParent = false;

            string leafBase = ExtractBaseHidId(leafPnpId);
            if (!string.IsNullOrEmpty(leafBase))
            {
                if (_touchscreenBaseIds.Contains(leafBase)) matchesTouchscreenParent = true;
                if (_precisionTouchpadBaseIds.Contains(leafBase)) matchesTouchpadParent = true;
            }

            for (int i = 0; i < ancestryIds.Count; i++)
            {
                string anc = ancestryIds[i];
                if (string.IsNullOrEmpty(anc)) continue;
                string ancUp = anc.ToUpperInvariant();
                sb.Append(ancUp).Append('|');

                if (i > 0)
                {
                    if (_touchscreenParentIds.Contains(anc)) matchesTouchscreenParent = true;
                    if (_precisionTouchpadParentIds.Contains(anc)) matchesTouchpadParent = true;
                }

                if (ancUp.StartsWith("BTHENUM\\", StringComparison.Ordinal) ||
                    ancUp.StartsWith("BTHLE\\", StringComparison.Ordinal) ||
                    ancUp.StartsWith("BTHLEDEVICE\\", StringComparison.Ordinal) ||
                    ancUp.IndexOf("00001124-0000-1000-8000-00805F9B34FB", StringComparison.Ordinal) >= 0)
                {
                    hasBluetoothAncestor = true;
                }
                else if ((ancUp.StartsWith("USB\\", StringComparison.Ordinal) || ancUp.StartsWith("USB4\\", StringComparison.Ordinal)) &&
                         ancUp.IndexOf("ROOT_HUB", StringComparison.Ordinal) < 0)
                {
                    hasUsbAncestor = true;
                }
                else if (ancUp.StartsWith("ACPI\\", StringComparison.Ordinal) || ancUp.IndexOf("PNP0F", StringComparison.Ordinal) >= 0)
                {
                    hasPs2Ancestor = true;
                }
            }

            string combined = sb.ToString();
            if (combined.IndexOf("BTHENUM", StringComparison.Ordinal) >= 0 ||
                combined.IndexOf("BTHLE", StringComparison.Ordinal) >= 0 ||
                combined.IndexOf("00001124-0000-1000-8000-00805F9B34FB", StringComparison.Ordinal) >= 0)
            {
                hasBluetoothAncestor = true;
            }
            if (combined.IndexOf("I8042PRT", StringComparison.Ordinal) >= 0 ||
                combined.IndexOf("ACPI#", StringComparison.Ordinal) >= 0 ||
                combined.IndexOf("ACPI\\", StringComparison.Ordinal) >= 0)
            {
                hasPs2Ancestor = true;
            }

            bool explicitTouchpadToken = (combined.IndexOf("COL03", StringComparison.Ordinal) >= 0 ||
                                          combined.IndexOf("SYNHID", StringComparison.Ordinal) >= 0 ||
                                          combined.IndexOf("TOUCHPAD", StringComparison.Ordinal) >= 0 ||
                                          combined.IndexOf("TRACKPAD", StringComparison.Ordinal) >= 0 ||
                                          combined.IndexOf("CLICKPAD", StringComparison.Ordinal) >= 0 ||
                                          combined.IndexOf("MSFT0001", StringComparison.Ordinal) >= 0);
            bool explicitTrackPointToken = (combined.IndexOf("TRACKPOINT", StringComparison.Ordinal) >= 0 ||
                                            combined.IndexOf("POINTSTICK", StringComparison.Ordinal) >= 0 ||
                                            combined.IndexOf("POINTING STICK", StringComparison.Ordinal) >= 0 ||
                                            combined.IndexOf("ACPI#LEN", StringComparison.Ordinal) >= 0 ||
                                            combined.IndexOf("ACPI\\LEN", StringComparison.Ordinal) >= 0 ||
                                            combined.IndexOf("LEN0071", StringComparison.Ordinal) >= 0);
            bool explicitTouchscreenToken = ((combined.IndexOf("TOUCHSCREEN", StringComparison.Ordinal) >= 0 ||
                                              combined.IndexOf("TOUCH SCREEN", StringComparison.Ordinal) >= 0 ||
                                              combined.IndexOf("DIGITIZER", StringComparison.Ordinal) >= 0) &&
                                             !explicitTouchpadToken);

            if (matchesTouchscreenParent || explicitTouchscreenToken)
            {
                classification = "TOUCHSCREEN";
            }
            else if ((hasBluetoothAncestor || hasUsbAncestor) && !explicitTouchpadToken && !matchesTouchpadParent)
            {
                classification = "EXTERNAL_MOUSE";
            }
            else if (matchesTouchpadParent || (explicitTouchpadToken && !explicitTrackPointToken))
            {
                classification = "TOUCHPAD";
            }
            else if (explicitTrackPointToken)
            {
                classification = "TRACKPOINT";
            }
            else if (hasPs2Ancestor)
            {
                classification = _hasPrecisionTouchpadHid ? "TRACKPOINT" : "SHARED_PS2";
            }
            else if (_hasPrecisionTouchpadHid && (combined.IndexOf("ELAN", StringComparison.Ordinal) >= 0 || combined.IndexOf("SYNA", StringComparison.Ordinal) >= 0 || combined.IndexOf("ALPS", StringComparison.Ordinal) >= 0))
            {
                classification = "TOUCHPAD";
            }
            else
            {
                classification = "SHARED_PS2";
            }

            _rawDeviceClassCache[hDevice] = classification;
            return classification;
        }

        private void InstallHwndAndRawInputHook()
        {
            try
            {
                HwndSource src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return;
                if (_hwndSource == null)
                {
                    _hwndSource = src;
                    _hwndSource.AddHook(WndProcMiddleButtonHook);
                }

                if (!_rawInputRegistered && src.Handle != IntPtr.Zero)
                {
                    _registeredRawDevs.Clear();
                    _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle }); // Mouse
                    _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x01, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle }); // Pointer

                    bool hasTouchscreenPair = false;
                    List<uint> hidPairs = NativeMethods.EnumRawHidUsagePairs();
                    for (int i = 0; i < hidPairs.Count; i++)
                    {
                        ushort uPage = (ushort)((hidPairs[i] >> 16) & 0xFFFF);
                        ushort uId = (ushort)(hidPairs[i] & 0xFFFF);
                        if (uPage == 0x0D && uId == 0x05)
                        {
                            _hasPrecisionTouchpadHid = true;
                            _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = uPage, usUsage = uId, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle });
                        }
                        else if (uPage == 0x0D && uId == 0x04)
                        {
                            hasTouchscreenPair = true;
                            _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = uPage, usUsage = uId, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle });
                        }
                        else if (uPage >= 0xFF00)
                        {
                            _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = uPage, usUsage = uId, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle });
                        }
                    }

                    // Popišemo bazne i parent PnP ID-jeve za Precision Touchpad (0x0D/0x05) i Touchscreen (0x0D/0x04)
                    List<NativeMethods.RawInputDeviceMeta> allMetas = NativeMethods.EnumRawInputDevicesMeta();
                    for (int i = 0; i < allMetas.Count; i++)
                    {
                        NativeMethods.RawInputDeviceMeta meta = allMetas[i];
                        if (meta.RawType == NativeMethods.RIM_TYPEHID)
                        {
                            if (meta.UsagePage == 0x0D && meta.Usage == 0x05)
                            {
                                _hasPrecisionTouchpadHid = true;
                                IndexKnownHidCollectionHierarchy(meta, _precisionTouchpadBaseIds, _precisionTouchpadParentIds);
                            }
                            else if (meta.UsagePage == 0x0D && meta.Usage == 0x04)
                            {
                                IndexKnownHidCollectionHierarchy(meta, _touchscreenBaseIds, _touchscreenParentIds);
                            }
                        }
                    }

                    NativeMethods.RAWINPUTDEVICE[] devs = _registeredRawDevs.ToArray();
                    uint structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE));
                    if (NativeMethods.RegisterRawInputDevices(devs, (uint)devs.Length, structSize))
                    {
                        _rawInputRegistered = true;
                        if (!hasTouchscreenPair)
                        {
                            NativeMethods.RAWINPUTDEVICE tsDev = new NativeMethods.RAWINPUTDEVICE
                            {
                                usUsagePage = 0x0D,
                                usUsage = 0x04,
                                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                                hwndTarget = src.Handle
                            };
                            if (NativeMethods.RegisterRawInputDevices(new NativeMethods.RAWINPUTDEVICE[] { tsDev }, 1u, structSize))
                            {
                                _registeredRawDevs.Add(tsDev);
                            }
                        }
                        HwTestLogger.Info("TOUCHPAD", string.Format("RawInput RIDEV_INPUTSINK registrovan ({0} uređaja/kolekcija, PrecisionTouchpadHID={1})", _registeredRawDevs.Count, _hasPrecisionTouchpadHid));
                    }
                }
            }
            catch { }
        }

        private void UninstallHwndAndRawInputHook()
        {
            if (_rawInputRegistered && _registeredRawDevs.Count > 0)
            {
                try
                {
                    NativeMethods.RAWINPUTDEVICE[] removeDevs = new NativeMethods.RAWINPUTDEVICE[_registeredRawDevs.Count];
                    for (int i = 0; i < _registeredRawDevs.Count; i++)
                    {
                        removeDevs[i] = new NativeMethods.RAWINPUTDEVICE
                        {
                            usUsagePage = _registeredRawDevs[i].usUsagePage,
                            usUsage = _registeredRawDevs[i].usUsage,
                            dwFlags = NativeMethods.RIDEV_REMOVE,
                            hwndTarget = IntPtr.Zero
                        };
                    }
                    uint structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE));
                    NativeMethods.RegisterRawInputDevices(removeDevs, (uint)removeDevs.Length, structSize);
                }
                catch { }
                _rawInputRegistered = false;
                _registeredRawDevs.Clear();
            }

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProcMiddleButtonHook); } catch { }
                _hwndSource = null;
            }
        }

        private IntPtr WndProcMiddleButtonHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_isClosingOrDone) return IntPtr.Zero;

            if (msg == NativeMethods.WM_MBUTTONDOWN || msg == NativeMethods.WM_MBUTTONUP)
            {
                if (_hasTrackPoint && !_middleClickDone)
                {
                    int now = Environment.TickCount;
                    if (unchecked((uint)(now - _lastForbiddenMiddleClickTicks)) > 220u &&
                        !(unchecked((uint)(now - _lastMouseRawTicks)) <= 120u &&
                          (_lastMouseRawSource == "EXTERNAL_MOUSE" || _lastMouseRawSource == "TOUCHPAD" || _lastMouseRawSource == "TOUCHSCREEN")))
                    {
                        MarkMiddleClickDone("WM_MBUTTON poruka prozora");
                    }
                }
                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_INPUT)
            {
                uint rawType;
                IntPtr rawDev;
                ushort btnFlags;
                short btnData;
                int lastX, lastY;
                byte[] hidSample;
                if (NativeMethods.TryReadRawInputPacket(lParam, out rawType, out rawDev, out btnFlags, out btnData, out lastX, out lastY, out hidSample))
                {
                    if (rawType == NativeMethods.RIM_TYPEHID)
                    {
                        string devName = GetCachedRawDeviceName(rawDev);
                        string upper = devName.ToUpperInvariant();
                        uint infoType, vid, pid;
                        ushort uPage, uId;
                        bool gotHidInfo = NativeMethods.TryGetRawInputDeviceInfoDetails(rawDev, out infoType, out vid, out pid, out uPage, out uId);

                        if (gotHidInfo && uPage == 0x0D && uId == 0x04)
                        {
                            _lastTouchscreenHidTicks = Environment.TickCount;
                            _lastMouseRawSource = "TOUCHSCREEN";
                            _lastMouseRawTicks = _lastTouchscreenHidTicks;
                            return IntPtr.Zero;
                        }

                        // Touchpad HID izveštaj (ELAN COL03 / SYNHIDMINI / opšti Precision HID 0x0D/0x05)
                        bool isVendorCol = (upper.IndexOf("COL02") >= 0 || upper.IndexOf("VENDOR") >= 0);
                        bool looksLikeTouchpadHid = (gotHidInfo && uPage == 0x0D && uId == 0x05) ||
                                                    (upper.IndexOf("COL03") >= 0 || upper.IndexOf("SYNHID") >= 0 || upper.IndexOf("ELAN") >= 0 || upper.IndexOf("SYNA") >= 0 || upper.IndexOf("ALPS") >= 0 || upper.IndexOf("TOUCH") >= 0);
                        if (looksLikeTouchpadHid)
                        {
                            if (!_hidTouchpadDevices.Contains(rawDev))
                            {
                                _hidTouchpadDevices.Add(rawDev);
                                if (!isVendorCol) _hasPrecisionTouchpadHid = true;
                            }
                            if (!isVendorCol)
                            {
                                _hasSeenTouchpadHidPacket = true;
                                _lastTouchpadHidTicks = Environment.TickCount;
                            }
                            else if (hidSample != null && hidSample.Length > 0 && _loggedRawSamples.Count < 8)
                            {
                                string hex = BitConverter.ToString(hidSample);
                                if (_loggedRawSamples.Add(hex))
                                {
                                    HwTestLogger.Info("TOUCHPAD", string.Format("RawVendorHID [{0}]: {1}", devName, hex));
                                }
                            }
                        }
                    }
                    else if (rawType == NativeMethods.RIM_TYPEMOUSE)
                    {
                        bool firstForDev = _mouseDevicesSeen.Add(rawDev);
                        string devName = GetCachedRawDeviceName(rawDev);
                        string devClass = ClassifyMouseRawDevice(rawDev, devName);
                        if (firstForDev && !string.IsNullOrEmpty(devName))
                        {
                            HwTestLogger.Info("TOUCHPAD", string.Format("Detektovan RawInput Mouse uređaj [{0}]: {1}", devClass, devName));
                            if (_hasTrackPoint && (_hasPrecisionTouchpadHid || _elanEventWatches.Count > 0 || HasDistinctInternalPointingDevices()))
                                _canSeparateTrackPointButtons = true;
                        }

                        int now = Environment.TickCount;
                        bool recentTouchpadHid = (_hasPrecisionTouchpadHid && unchecked((uint)(now - _lastTouchpadHidTicks)) <= 200u);
                        if (devClass == "SHARED_PS2" && recentTouchpadHid)
                        {
                            devClass = "TOUCHPAD";
                        }

                        if ((btnFlags & (NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN | NativeMethods.RI_MOUSE_MIDDLE_BUTTON_UP)) != 0)
                        {
                            if (devClass == "EXTERNAL_MOUSE" || devClass == "TOUCHPAD" || devClass == "TOUCHSCREEN")
                            {
                                _lastForbiddenMiddleClickTicks = now;
                            }
                            else if (_hasTrackPoint && !_middleClickDone)
                            {
                                MarkMiddleClickDone("RawInput RI_MOUSE_MIDDLE_BUTTON (" + devName + ")");
                            }
                        }

                        if ((btnFlags & NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN) != 0 ||
                            (btnFlags & NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0 ||
                            (btnFlags & NativeMethods.RI_MOUSE_WHEEL) != 0 ||
                            lastX != 0 || lastY != 0)
                        {
                            _lastMouseRawSource = devClass;
                            _lastMouseRawTicks = now;
                        }

                        if ((btnFlags & NativeMethods.RI_MOUSE_WHEEL) != 0 && _hasTrackPoint && !_middleClickDone)
                        {
                            if (devClass == "TRACKPOINT" || (devClass == "SHARED_PS2" && !recentTouchpadHid))
                            {
                                MarkMiddleClickDone("RawInput TrackPoint Scroll mod (Srednji + džojstik)");
                            }
                        }
                    }
                }
            }
            return IntPtr.Zero;
        }

        private void InstallMouseHook()
        {
            if (_mouseHookId != IntPtr.Zero) return;
            try
            {
                _mouseHookProc = MouseHookCallback;
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    _mouseHookId = NativeMethods.SetWindowsHookEx(
                        NativeMethods.WH_MOUSE_LL,
                        _mouseHookProc,
                        NativeMethods.GetModuleHandle(curModule.ModuleName),
                        0);
                }
            }
            catch { }
        }

        private void UninstallMouseHook()
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWindowsHookEx(_mouseHookId); } catch { }
                _mouseHookId = IntPtr.Zero;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_isClosingOrDone && !_middleClickDone && _hasTrackPoint)
            {
                int msg = wParam.ToInt32();
                if (msg == NativeMethods.WM_MBUTTONDOWN || msg == NativeMethods.WM_MBUTTONUP)
                {
                    int now = Environment.TickCount;
                    if (unchecked((uint)(now - _lastForbiddenMiddleClickTicks)) > 220u &&
                        !(unchecked((uint)(now - _lastMouseRawTicks)) <= 120u &&
                          (_lastMouseRawSource == "EXTERNAL_MOUSE" || _lastMouseRawSource == "TOUCHPAD" || _lastMouseRawSource == "TOUCHSCREEN")))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            int cur = Environment.TickCount;
                            if (unchecked((uint)(cur - _lastForbiddenMiddleClickTicks)) <= 220u) return;
                            MarkMiddleClickDone("WH_MOUSE_LL hardverski srednji klik");
                        }));
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private void CheckAllPassed()
        {
            if (_hasTrackPoint)
            {
                if (_visitedTargets.Count >= 4 && _tpLeftDone && _middleClickDone && _tpRightDone)
                {
                    FinishPass();
                }
            }
            else
            {
                if (_visitedTargets.Count >= 4 && _leftClickDone && _rightClickDone && _scrollUpDone && _scrollDownDone)
                {
                    FinishPass();
                }
            }
        }

        private void PopulateTouchpadResultDetails()
        {
            if (_hasTrackPoint)
            {
                Result.Details["mode"] = "trackpoint";
                Result.Details["trackpoint_movement_targets"] = _visitedTargets.Count;
                Result.Details["touchpad_separate_detection"] = _canSeparateTrackPointButtons;
                Result.Details["trackpoint_left_click"] = _tpLeftDone;
                Result.Details["trackpoint_middle_click"] = _middleClickDone;
                Result.Details["trackpoint_right_click"] = _tpRightDone;
            }
            else
            {
                Result.Details["mode"] = "touchpad";
                Result.Details["movement_corners"] = _visitedTargets.Count;
                Result.Details["left_click"] = _leftClickDone;
                Result.Details["right_click"] = _rightClickDone;
                Result.Details["scroll_2finger"] = (_scrollUpDone && _scrollDownDone);
            }
            Result.Details["touchpad_temporarily_disabled"] = _touchpadTemporarilyDisabled;
            Result.Details["touchpad_disable_method"] = _touchpadDisableMethod ?? "NONE";
            Result.Details["anticheat_blocked_touchpad_events"] = _blockedTouchpadEvents;
            Result.Details["anticheat_blocked_external_mouse_events"] = _blockedExternalMouseEvents;
            Result.Details["anticheat_blocked_trackpoint_events"] = _blockedTrackPointEvents;
            Result.Details["anticheat_blocked_touchscreen_events"] = _blockedTouchscreenEvents;
        }

        private List<string> CollectFailedComponents(bool forceIncludeMiddle)
        {
            List<string> failed = new List<string>();
            if (_hasTrackPoint)
            {
                if (_visitedTargets.Count < 4) failed.Add(HwTestLocalization.Pick("TrackPoint kretanje (4 mete)", "指点杆移动（4 个目标）"));
                if (!_tpLeftDone) failed.Add(HwTestLocalization.Pick("Levi TrackPoint taster", "TrackPoint 左键"));
                if (forceIncludeMiddle || !_middleClickDone) failed.Add(HwTestLocalization.Pick("Srednji TrackPoint taster", "TrackPoint 中键"));
                if (!_tpRightDone) failed.Add(HwTestLocalization.Pick("Desni TrackPoint taster", "TrackPoint 右键"));
            }
            else
            {
                if (_visitedTargets.Count < 4) failed.Add(HwTestLocalization.Pick("Kretanje kursora (4 ugla)", "光标移动（4 个角落）"));
                if (!_leftClickDone) failed.Add(HwTestLocalization.Pick("Levi klik", "左键点击"));
                if (!_rightClickDone) failed.Add(HwTestLocalization.Pick("Desni klik", "右键点击"));
                if (!(_scrollUpDone && _scrollDownDone)) failed.Add(HwTestLocalization.Pick("Skrol (2 prsta)", "双指滚动"));
            }
            return failed;
        }

        private void FinishWithFailReason(List<string> failedComponents, string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = reason;
            PopulateTouchpadResultDetails();
            Result.Details["failed_components"] = string.Join(", ", failedComponents.ToArray());
            SafeCloseDialog(false);
        }

        private void FinishMiddleButtonFail()
        {
            List<string> failed = CollectFailedComponents(true);
            bool allOtherTpOk = (_visitedTargets.Count >= 4 && _tpLeftDone && _tpRightDone);
            string reason = allOtherTpOk
                ? HwTestLocalization.Pick("Kvar: Srednji TrackPoint taster ne radi (TrackPoint kretanje 4/4, levi i desni taster su ispravni)", "故障：TrackPoint 中键无响应（指点杆移动 4/4、左键和右键正常）")
                : (HwTestLocalization.Pick("Kvar TrackPointa — ne radi: ", "TrackPoint 故障 — 异常项: ") + string.Join(", ", failed.ToArray()));
            FinishWithFailReason(failed, reason);
        }

        private void FinishFailGeneral()
        {
            List<string> failed = CollectFailedComponents(false);
            if (failed.Count == 0)
            {
                failed.Add(_hasTrackPoint
                    ? HwTestLocalization.Pick("TrackPoint (prijavio serviser)", "TrackPoint（技术员报告）")
                    : HwTestLocalization.Pick("Touchpad (prijavio serviser)", "触控板（技术员报告）"));
            }
            string reason = (_hasTrackPoint
                ? HwTestLocalization.Pick("Kvar TrackPointa — ne radi: ", "TrackPoint 故障 — 异常项: ")
                : HwTestLocalization.Pick("Kvar touchpada — ne radi: ", "触控板故障 — 异常项: ")) + string.Join(", ", failed.ToArray());
            FinishWithFailReason(failed, reason);
        }

        private void SafeCloseDialog(bool result)
        {
            CleanupAllResourcesAndRestoreTouchpad();
            try { DialogResult = result; } catch { }
            try { Close(); } catch { }
        }

        private void FinishPass()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            PopulateTouchpadResultDetails();
            SafeCloseDialog(true);
        }
    }
}
