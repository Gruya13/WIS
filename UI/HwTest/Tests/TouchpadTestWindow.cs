using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class TouchpadTestWindow : Window
    {
        private readonly Canvas _testCanvas;
        private readonly TextBlock _instructions;
        private readonly List<Ellipse> _targets;
        private readonly HashSet<int> _visitedTargets;

        private readonly HardwareProbeResult _hwInfo;
        private readonly bool _hasTrackPoint;

        private bool _leftClickDone;
        private bool _rightClickDone;
        private bool _scrollUpDone;
        private bool _scrollDownDone;
        private bool _middleClickDone;
        private bool _isClosingOrDone;

        private readonly TextBlock _statusMove;
        private readonly TextBlock _statusLeft;
        private readonly TextBlock _statusRight;
        private readonly TextBlock _statusScroll;
        private readonly TextBlock _statusMiddle;
        private Border _tpVisualBox;
        private Border _tpBtnLeft;
        private Border _tpBtnMiddle;
        private Border _tpBtnRight;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _mouseHookProc;
        private DispatcherTimer _middleKeyPollTimer;

        public TestItemResult Result { get; private set; }

        public TouchpadTestWindow() : this(null)
        {
        }

        public TouchpadTestWindow(HardwareProbeResult hwInfo)
        {
            _hwInfo = hwInfo;
            _hasTrackPoint = (_hwInfo != null && (_hwInfo.HasTrackPoint || _hwInfo.IsThinkPad));

            Title = _hasTrackPoint ? "WIS — Test Touchpada i TrackPointa" : "WIS — Test Touchpada";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = new TestItemResult("touchpad", _hasTrackPoint ? "Touchpad / TrackPoint" : "Touchpad",
                _hasTrackPoint
                    ? "Ispitivanje touchpada (kretanje, levi/desni klik, skrol) i TrackPoint srednjeg tastera"
                    : "Ispitivanje praćenja pokazivača, levog/desnog klika i skrola sa dva prsta");

            _targets = new List<Ellipse>();
            _visitedTargets = new HashSet<int>();

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

            // Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            StackPanel spH = new StackPanel();
            _instructions = new TextBlock
            {
                Text = "👉 Pomerite pokazivač touchpada kroz 4 kruga u uglovima ekrana",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(_instructions);

            // Task indicators
            StackPanel spTasks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _statusMove = CreateBadge("1. Kretanje (4 ugla): 0/4");
            _statusLeft = CreateBadge("2. Levi klik: ⏳");
            _statusRight = CreateBadge("3. Desni klik: ⏳");
            _statusScroll = CreateBadge("4. Skrol (2 prsta): ⏳");
            spTasks.Children.Add(_statusMove);
            spTasks.Children.Add(_statusLeft);
            spTasks.Children.Add(_statusRight);
            spTasks.Children.Add(_statusScroll);
            if (_hasTrackPoint)
            {
                _statusMiddle = CreateBadge("5. Srednji TrackPoint klik: ⏳");
                spTasks.Children.Add(_statusMiddle);
            }
            spH.Children.Add(spTasks);
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
                Content = "✗ Prijavi kvar touchpada (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(185, 38, 48)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 12, 0)
            };
            btnTouchpadFail.Click += (s, e) => FinishFailGeneral();
            spFooter.Children.Add(btnTouchpadFail);

            Button btnSkip = new Button
            {
                Content = "Preskoči test touchpada",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            spFooter.Children.Add(btnSkip);
            footer.Child = spFooter;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            Loaded += (s, e) =>
            {
                SetupTargets();
                if (_hasTrackPoint)
                {
                    StartMiddleButtonMonitoring();
                }
            };
            Closed += (s, e) => StopMiddleButtonMonitoring();
            PreviewMouseDown += OnCanvasMouseDown;
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

            for (int i = 0; i < pts.Length; i++)
            {
                Ellipse el = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = new SolidColorBrush(Color.FromArgb(100, 78, 201, 176)),
                    Stroke = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                    StrokeThickness = 3,
                    Tag = i
                };
                Canvas.SetLeft(el, pts[i].X);
                Canvas.SetTop(el, pts[i].Y);
                _testCanvas.Children.Add(el);
                _targets.Add(el);
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
                Text = "TrackPoint tasteri (kliknite srednji taster)",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(spTitle);

            StackPanel spBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _tpBtnLeft = CreateTpButtonVisual("LEVI", 122);
            _tpBtnMiddle = CreateTpButtonVisual("SREDNJI", 122);
            _tpBtnRight = CreateTpButtonVisual("DESNI", 122);
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
                Content = "✓ Srednji taster radi (Scroll mod)",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnBoxPass.Click += (s, e) => MarkMiddleClickDone("ručna potvrda (Synaptics Scroll mod)");
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

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(_testCanvas);
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_visitedTargets.Contains(i)) continue;

                double l = Canvas.GetLeft(_targets[i]);
                double t = Canvas.GetTop(_targets[i]);
                Rect r = new Rect(l, t, _targets[i].Width, _targets[i].Height);

                if (r.Contains(p))
                {
                    _visitedTargets.Add(i);
                    _targets[i].Fill = new SolidColorBrush(Color.FromArgb(200, 40, 167, 69));
                    _targets[i].Stroke = Brushes.White;
                }
            }

            _statusMove.Text = string.Format("1. Kretanje (4 ugla): {0}/4", _visitedTargets.Count);
            if (_visitedTargets.Count >= 4)
            {
                _statusMove.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _statusMove.Text = "1. Kretanje (4 ugla): ✓ Prošlo";
                UpdateNextInstruction();
            }
        }

        private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
        {
            _leftClickDone = true;
            _statusLeft.Text = "2. Levi klik: ✓ Prošlo";
            _statusLeft.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            HighlightTpButton(_tpBtnLeft, "LEVI ✓");
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
        {
            _rightClickDone = true;
            _statusRight.Text = "3. Desni klik: ✓ Prošlo";
            _statusRight.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            HighlightTpButton(_tpBtnRight, "DESNI ✓");
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && !_middleClickDone)
            {
                MarkMiddleClickDone("hardverski srednji taster");
            }
        }

        private void MarkMiddleClickDone(string source)
        {
            if (!_hasTrackPoint || _middleClickDone || _isClosingOrDone) return;
            _middleClickDone = true;
            if (_statusMiddle != null)
            {
                _statusMiddle.Text = "5. Srednji TrackPoint klik: ✓ Prošlo";
                _statusMiddle.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
            }
            HighlightTpButton(_tpBtnMiddle, "SREDNJI ✓");
            HwTestLogger.Info("TOUCHPAD", "TrackPoint srednji klik potvrden: " + source);
            UpdateNextInstruction();
            CheckAllPassed();
        }

        private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
        {
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
            else if (_hasTrackPoint && !_middleClickDone)
            {
                _instructions.Text = "🔴 Pritisnite SREDNJI TrackPoint taster (ili kliknite plavo dugme ako je u Scroll modu)";
            }
        }

        private void StartMiddleButtonMonitoring()
        {
            InstallMouseHook();
            if (_middleKeyPollTimer == null)
            {
                _middleKeyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
                _middleKeyPollTimer.Tick += (s, e) =>
                {
                    if (_isClosingOrDone || _middleClickDone) return;
                    try
                    {
                        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON) & 0x8000) != 0)
                        {
                            MarkMiddleClickDone("VK_MBUTTON hardversko stanje");
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
            UninstallMouseHook();
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
            bool middleOk = (!_hasTrackPoint || _middleClickDone);
            if (_visitedTargets.Count >= 4 && _leftClickDone && _rightClickDone && _scrollUpDone && _scrollDownDone && middleOk)
            {
                FinishPass();
            }
        }

        private void PopulateTouchpadResultDetails()
        {
            Result.Details["movement_corners"] = _visitedTargets.Count;
            Result.Details["left_click"] = _leftClickDone;
            Result.Details["right_click"] = _rightClickDone;
            Result.Details["scroll_2finger"] = (_scrollUpDone && _scrollDownDone);
            if (_hasTrackPoint)
            {
                Result.Details["has_trackpoint"] = true;
                Result.Details["trackpoint_middle_click"] = _middleClickDone;
            }
        }

        private List<string> CollectFailedComponents(bool forceIncludeMiddle)
        {
            List<string> failed = new List<string>();
            if (_visitedTargets.Count < 4) failed.Add("Kretanje kursora (4 ugla)");
            if (!_leftClickDone) failed.Add("Levi klik");
            if (!_rightClickDone) failed.Add("Desni klik");
            if (!(_scrollUpDone && _scrollDownDone)) failed.Add("Skrol (2 prsta)");
            if (_hasTrackPoint && (forceIncludeMiddle || !_middleClickDone))
            {
                failed.Add("Srednji TrackPoint taster");
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
            bool allPadOk = (_visitedTargets.Count >= 4 && _leftClickDone && _rightClickDone && _scrollUpDone && _scrollDownDone);
            string reason = allPadOk
                ? "Kvar: Srednji TrackPoint taster ne radi (Touchpad kretanje 4/4, levi/desni klik i skrol su ispravni)"
                : "Kvar touchpada / TrackPointa — ne radi: " + string.Join(", ", failed.ToArray());
            FinishWithFailReason(failed, reason);
        }

        private void FinishFailGeneral()
        {
            List<string> failed = CollectFailedComponents(false);
            if (failed.Count == 0)
            {
                failed.Add("Touchpad / TrackPoint (prijavio serviser)");
            }
            string reason = "Kvar touchpada" + (_hasTrackPoint ? " / TrackPointa" : "") + " — ne radi: " + string.Join(", ", failed.ToArray());
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

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test touchpada";
            SafeCloseDialog(false);
        }
    }
}
