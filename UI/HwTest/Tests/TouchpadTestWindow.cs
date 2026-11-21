using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        private string _lastMouseRawSource = "";
        private int _lastMouseRawTicks;

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

            Title = _hasTrackPoint ? "WIS — Test TrackPointa" : "WIS — Test Touchpada";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = _hasTrackPoint
                ? new TestItemResult("trackpoint", "TrackPoint",
                    "Provera pomeranja kursora crvenim TrackPoint džojstikom (4 mete) i gornjih tastera (levi, srednji/Scroll, desni)")
                : new TestItemResult("touchpad", "Touchpad",
                    "Ispitivanje praćenja pokazivača, levog/desnog klika i skrola sa dva prsta");

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
                    ? "🔴 Pomerite kursor crvenim TrackPoint džojstikom kroz 4 crvene mete (ne dirajte touchpad!)"
                    : "👉 Pomerite pokazivač touchpada kroz 4 kruga u uglovima ekrana",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(_instructions);

            // Indikatori napretka u zaglavlju (odvojeni po modu)
            WrapPanel spTasks = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            if (_hasTrackPoint)
            {
                _statusMove = CreateBadge("1. TrackPoint kretanje (4 mete): 0/4");
                _statusTpLeft = CreateBadge("2. TrackPoint levi: ⏳");
                _statusMiddle = CreateBadge("3. TrackPoint srednji / Scroll: ⏳");
                _statusTpRight = CreateBadge("4. TrackPoint desni: ⏳");
                spTasks.Children.Add(_statusMove);
                spTasks.Children.Add(_statusTpLeft);
                spTasks.Children.Add(_statusMiddle);
                spTasks.Children.Add(_statusTpRight);
            }
            else
            {
                _statusMove = CreateBadge("1. Kretanje (4 ugla): 0/4");
                _statusLeft = CreateBadge("2. Levi klik: ⏳");
                _statusRight = CreateBadge("3. Desni klik: ⏳");
                _statusScroll = CreateBadge("4. Skrol (2 prsta): ⏳");
                spTasks.Children.Add(_statusMove);
                spTasks.Children.Add(_statusLeft);
                spTasks.Children.Add(_statusRight);
                spTasks.Children.Add(_statusScroll);
            }
            spH.Children.Add(spTasks);

            _biosHintBanner = new TextBlock
            {
                Text = "⚠️ Uređaj je onemogućen u BIOS podešavanjima ili nije aktivan u Windowsu (HP: F10, Lenovo: F1, Dell: F2).",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            spH.Children.Add(_biosHintBanner);

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
            StackPanel spFooter = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _btnBiosReboot = new Button
            {
                Content = "🔧 Restartuj u UEFI BIOS (i nastavi WIS)",
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
                    Content = "✓ Srednji taster radi (Scroll mod)",
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
                    Content = "✗ Srednji TrackPoint taster NE RADI (FAIL)",
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
                    ? "✗ Prijavi kvar TrackPointa (FAIL)"
                    : "✗ Prijavi kvar touchpada (FAIL)",
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
                if (_hasTrackPoint)
                {
                    InstallHwndAndRawInputHook();
                    StartMiddleButtonMonitoring();
                }
            };
            Closed += (s, e) => StopMiddleButtonMonitoring();
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
                        _biosHintBanner.Text = string.Format(
                            "⚠️ TrackPoint je onemogućen u BIOS-u ({0}) — restartujte u UEFI BIOS (HP: F10, Lenovo: F1, Dell: F2).",
                            _hwInfo.TrackPointBiosStatus);
                    }
                    else if (!_hasTrackPoint && _hwInfo.TouchpadDisabledInBios && !string.IsNullOrEmpty(_hwInfo.TouchpadBiosStatus))
                    {
                        _biosHintBanner.Text = string.Format(
                            "⚠️ Touchpad je onemogućen u BIOS-u ({0}) — restartujte u UEFI BIOS (HP: F10, Lenovo: F1, Dell: F2).",
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
                    "Restartujte računar i uđite u UEFI BIOS podešavanja (HP: F10, Lenovo: F1, Dell: F2) kako biste uključili uređaj.",
                    "Restart u UEFI BIOS",
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
                Text = "TrackPoint tasteri (gornja 3 tastera iznad touchpada)",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(spTitle);

            sp.Children.Add(new TextBlock
            {
                Text = "Srednji taster: kliknite SREDNJI, ili ga DRŽITE + pomerite crveni TrackPoint gore/dole (Scroll mod)",
                Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 210)),
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 10)
            });

            StackPanel spBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _tpBtnLeft = CreateTpButtonVisual("TP LEVI", 122);
            _tpBtnMiddle = CreateTpButtonVisual("TP SREDNJI", 122);
            _tpBtnRight = CreateTpButtonVisual("TP DESNI", 122);
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
                Content = "✓ Srednji taster radi (Scroll / driver mod)",
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
                Content = "✗ Srednji TrackPoint taster NE RADI (FAIL)",
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
            _showingTouchpadWarning = true;
            _instructions.Text = "⚠️ Koristite crveni TrackPoint džojstik, ne touchpad!";
            _instructions.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        }

        private void ClearTrackPointTouchpadWarning()
        {
            if (!_showingTouchpadWarning) return;
            _showingTouchpadWarning = false;
            _instructions.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            UpdateNextInstruction();
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_isClosingOrDone) return;

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
                        ShowTrackPointTouchpadWarning();
                        continue;
                    }
                    _visitedTargets.Add(i);
                    _targets[i].Fill = new SolidColorBrush(Color.FromArgb(200, 40, 167, 69));
                    _targets[i].Stroke = Brushes.White;
                }
            }

            _statusMove.Text = _hasTrackPoint
                ? string.Format("1. TrackPoint kretanje (4 mete): {0}/4", _visitedTargets.Count)
                : string.Format("1. Kretanje (4 ugla): {0}/4", _visitedTargets.Count);
            if (_visitedTargets.Count >= 4)
            {
                _statusMove.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _statusMove.Text = _hasTrackPoint
                    ? "1. TrackPoint kretanje (4 mete): ✓ Prošlo"
                    : "1. Kretanje (4 ugla): ✓ Prošlo";
                UpdateNextInstruction();
                CheckAllPassed();
            }
        }

        private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
        {
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
                        ShowTrackPointTouchpadWarning();
                    }
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastMouseRawTicks)) <= 120u && _lastMouseRawSource == "TOUCHPAD")
                {
                    ShowTrackPointTouchpadWarning();
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
                        ShowTrackPointTouchpadWarning();
                    }
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastMouseRawTicks)) <= 120u && _lastMouseRawSource == "TOUCHPAD")
                {
                    ShowTrackPointTouchpadWarning();
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
                _statusLeft.Text = "2. Levi klik: ✓ Prošlo";
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
                _statusRight.Text = "3. Desni klik: ✓ Prošlo";
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
                _statusTpLeft.Text = "2. TrackPoint levi: ✓ Prošlo";
                _statusTpLeft.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnLeft, "TP LEVI ✓");
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
                _statusTpRight.Text = "4. TrackPoint desni: ✓ Prošlo";
                _statusTpRight.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnRight, "TP DESNI ✓");
            HwTestLogger.Info("TOUCHPAD", "TrackPoint desni klik potvrđen: " + source);
            ClearTrackPointTouchpadWarning();
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_hasTrackPoint) return;
            if (e.ChangedButton == MouseButton.Middle && !_middleClickDone)
            {
                MarkMiddleClickDone("hardverski srednji taster");
            }
            else if (e.MiddleButton == MouseButtonState.Pressed && !_middleClickDone)
            {
                MarkMiddleClickDone("hardverski srednji taster (MiddleButton)");
            }
        }

        private void MarkMiddleClickDone(string source)
        {
            if (!_hasTrackPoint || _middleClickDone || _isClosingOrDone) return;
            _middleClickDone = true;
            if (_statusMiddle != null)
            {
                _statusMiddle.Text = "3. TrackPoint srednji / Scroll: ✓ Prošlo";
                _statusMiddle.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnMiddle, "TP SREDNJI ✓");
            HwTestLogger.Info("TOUCHPAD", "TrackPoint srednji klik potvrđen: " + source);
            ClearTrackPointTouchpadWarning();
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isClosingOrDone) return;

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
                        ShowTrackPointTouchpadWarning();
                    }
                }
                return;
            }

            if (e.Delta > 0) _scrollUpDone = true;
            if (e.Delta < 0) _scrollDownDone = true;

            if (_scrollUpDone && _scrollDownDone)
            {
                _statusScroll.Text = "4. Skrol (2 prsta): ✓ Prošlo (Gore i Dole)";
                _statusScroll.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                UpdateNextInstruction();
                CheckAllPassed();
            }
            else
            {
                _statusScroll.Text = string.Format("4. Skrol (2 prsta): {0}{1}",
                    _scrollUpDone ? "▲ Gore OK " : "▲ Gore čeka ",
                    _scrollDownDone ? "▼ Dole OK" : "▼ Dole čeka");
            }
        }

        private void UpdateNextInstruction()
        {
            if (_showingTouchpadWarning) return;

            if (_hasTrackPoint)
            {
                if (_visitedTargets.Count < 4)
                {
                    _instructions.Text = "🔴 Pomerite kursor crvenim TrackPoint džojstikom kroz 4 crvene mete (ne dirajte touchpad!)";
                }
                else if (!_tpLeftDone)
                {
                    _instructions.Text = "🔴 Pritisnite LEVI gornji taster TrackPoint-a (iznad touchpada)";
                }
                else if (!_tpRightDone)
                {
                    _instructions.Text = "🔴 Pritisnite DESNI gornji taster TrackPoint-a (iznad touchpada)";
                }
                else if (!_middleClickDone)
                {
                    _instructions.Text = "🔴 SREDNJI TrackPoint taster: kliknite ga ili ga DRŽITE + pomerite crveni džojstik gore/dole (Scroll mod)";
                }
                return;
            }

            if (_visitedTargets.Count < 4)
            {
                _instructions.Text = "👉 Pomerite pokazivač touchpada kroz 4 kruga u uglovima ekrana";
            }
            else if (!_leftClickDone)
            {
                _instructions.Text = "👉 Pritisnite LEVI klik na touchpadu";
            }
            else if (!_rightClickDone)
            {
                _instructions.Text = "👉 Pritisnite DESNI klik na touchpadu (dva prsta tap ili desni ugao)";
            }
            else if (!_scrollUpDone || !_scrollDownDone)
            {
                _instructions.Text = "👉 Prevucite dva prsta preko touchpada GORE i DOLE (gest skrola)";
            }
        }

        private void StartMiddleButtonMonitoring()
        {
            SwitchElanMiddleButtonToClickMode();
            OpenElanMiddleButtonEvents();
            StartElanKernelWaitThread();
            InstallMouseHook();
            InstallHwndAndRawInputHook();
            _canSeparateTrackPointButtons = (_hasPrecisionTouchpadHid || _elanEventWatches.Count > 0 || _mouseDevicesSeen.Count >= 2);
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
                        else if (uPage >= 0xFF00)
                        {
                            _registeredRawDevs.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = uPage, usUsage = uId, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = src.Handle });
                        }
                    }

                    NativeMethods.RAWINPUTDEVICE[] devs = _registeredRawDevs.ToArray();
                    uint structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE));
                    if (NativeMethods.RegisterRawInputDevices(devs, (uint)devs.Length, structSize))
                    {
                        _rawInputRegistered = true;
                        HwTestLogger.Info("TOUCHPAD", string.Format("RawInput RIDEV_INPUTSINK registrovan ({0} uređaja/kolekcija, PrecisionTouchpadHID={1})", devs.Length, _hasPrecisionTouchpadHid));
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
                if (!_middleClickDone) MarkMiddleClickDone("WM_MBUTTON poruka prozora");
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
                        // Touchpad HID izveštaj (ELAN COL03 / SYNHIDMINI / opšti Precision HID 0x0D/0x05)
                        bool isVendorCol = (upper.IndexOf("COL02") >= 0 || upper.IndexOf("VENDOR") >= 0);
                        bool looksLikeTouchpadHid = (upper.IndexOf("COL03") >= 0 || upper.IndexOf("SYNHID") >= 0 || upper.IndexOf("ELAN") >= 0 || upper.IndexOf("SYNA") >= 0 || upper.IndexOf("ALPS") >= 0 || upper.IndexOf("TOUCH") >= 0);
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
                        if (firstForDev && !string.IsNullOrEmpty(devName))
                        {
                            HwTestLogger.Info("TOUCHPAD", "Detektovan RawInput Mouse uređaj: " + devName);
                            if (_mouseDevicesSeen.Count >= 2 && _hasTrackPoint)
                                _canSeparateTrackPointButtons = true;
                        }

                        if ((btnFlags & (NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN | NativeMethods.RI_MOUSE_MIDDLE_BUTTON_UP)) != 0 && !_middleClickDone)
                        {
                            MarkMiddleClickDone("RawInput RI_MOUSE_MIDDLE_BUTTON (" + devName + ")");
                        }

                        string upper = devName.ToUpperInvariant();
                        int now = Environment.TickCount;
                        bool recentTouchpadHid = (_hasPrecisionTouchpadHid && unchecked((uint)(now - _lastTouchpadHidTicks)) <= 200u);
                        bool explicitPs2TrackPoint = (_hasPrecisionTouchpadHid && (upper.IndexOf("ACPI#LEN") >= 0 || upper.IndexOf("ACPI\\LEN") >= 0));
                        bool explicitTouchpadMouse = (upper.IndexOf("COL03") >= 0 || upper.IndexOf("SYNHID") >= 0 || recentTouchpadHid);

                        if ((btnFlags & NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN) != 0 ||
                            (btnFlags & NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0 ||
                            lastX != 0 || lastY != 0)
                        {
                            if (explicitTouchpadMouse && !explicitPs2TrackPoint)
                                _lastMouseRawSource = "TOUCHPAD";
                            else if (explicitPs2TrackPoint || (_hasPrecisionTouchpadHid && !recentTouchpadHid))
                                _lastMouseRawSource = "TRACKPOINT";
                            else
                                _lastMouseRawSource = "";
                            _lastMouseRawTicks = now;
                        }

                        if ((btnFlags & NativeMethods.RI_MOUSE_WHEEL) != 0 && _hasTrackPoint && !_middleClickDone)
                        {
                            if (explicitPs2TrackPoint || (_hasPrecisionTouchpadHid && !recentTouchpadHid))
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
            if (nCode >= 0 && !_isClosingOrDone && !_middleClickDone)
            {
                int msg = wParam.ToInt32();
                if (msg == NativeMethods.WM_MBUTTONDOWN || msg == NativeMethods.WM_MBUTTONUP)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MarkMiddleClickDone("WH_MOUSE_LL hardverski srednji klik");
                    }));
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
        }

        private List<string> CollectFailedComponents(bool forceIncludeMiddle)
        {
            List<string> failed = new List<string>();
            if (_hasTrackPoint)
            {
                if (_visitedTargets.Count < 4) failed.Add("TrackPoint kretanje (4 mete)");
                if (!_tpLeftDone) failed.Add("Levi TrackPoint taster");
                if (forceIncludeMiddle || !_middleClickDone) failed.Add("Srednji TrackPoint taster");
                if (!_tpRightDone) failed.Add("Desni TrackPoint taster");
            }
            else
            {
                if (_visitedTargets.Count < 4) failed.Add("Kretanje kursora (4 ugla)");
                if (!_leftClickDone) failed.Add("Levi klik");
                if (!_rightClickDone) failed.Add("Desni klik");
                if (!(_scrollUpDone && _scrollDownDone)) failed.Add("Skrol (2 prsta)");
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
                ? "Kvar: Srednji TrackPoint taster ne radi (TrackPoint kretanje 4/4, levi i desni taster su ispravni)"
                : "Kvar TrackPointa — ne radi: " + string.Join(", ", failed.ToArray());
            FinishWithFailReason(failed, reason);
        }

        private void FinishFailGeneral()
        {
            List<string> failed = CollectFailedComponents(false);
            if (failed.Count == 0)
            {
                failed.Add(_hasTrackPoint ? "TrackPoint (prijavio serviser)" : "Touchpad (prijavio serviser)");
            }
            string reason = (_hasTrackPoint ? "Kvar TrackPointa — ne radi: " : "Kvar touchpada — ne radi: ") + string.Join(", ", failed.ToArray());
            FinishWithFailReason(failed, reason);
        }

        private void SafeCloseDialog(bool result)
        {
            StopMiddleButtonMonitoring();
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
