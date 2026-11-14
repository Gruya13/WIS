using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WIS.HwTest.Core;

namespace WIS.HwTest.Tests
{
    public class DisplayTestWindow : Window
    {
        private readonly List<Color> _colors;
        private readonly string[] _colorNames;
        private int _currentIndex;
        private int _cyclesCompleted;
        private bool _showingGradientStep;
        private bool _showingDecision;
        private bool _isClosingOrDone = false;
        private readonly Grid _rootGrid;
        private readonly Grid _gradientGridOverlay;
        private readonly Border _decisionOverlay;
        private readonly TextBlock _brightnessBadge;
        private readonly TextBlock _externalMonitorBadge;
        private DispatcherTimer _extMonitorTimer;
        private bool _brightnessTestStarted;
        private bool _brightnessRampOk;
        private int _initialBrightnessPct = -1;
        private string _brightnessRampDetail = "Provera u toku...";
        private int _activeMonitorCount = 1;

        public TestItemResult Result { get; private set; }

        public DisplayTestWindow()
        {
            Title = "WIS — Test Ekrana (Mrtvi Pikseli, Gradijent i HDMI)";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.None;

            Result = new TestItemResult("display", "Ekran (mrtvi pikseli)", "Provera defekata matrice, gradijenta, osvetljenja i HDMI izlaza");

            _colors = new List<Color>
            {
                Color.FromRgb(0, 0, 0),       // Crna (stuck/bright pixels)
                Color.FromRgb(255, 255, 255), // Bela (dead pixels)
                Color.FromRgb(255, 0, 0),     // Crvena
                Color.FromRgb(0, 255, 0),     // Zelena
                Color.FromRgb(0, 0, 255),     // Plava
                Color.FromRgb(128, 128, 128)  // Siva 50% (uniformnost)
            };
            _colorNames = new string[] { "Crna", "Bela", "Crvena", "Zelena", "Plava", "Siva 50%" };
            _currentIndex = 0;
            _cyclesCompleted = 0;
            _showingGradientStep = false;
            _showingDecision = false;

            _rootGrid = new Grid();
            Content = _rootGrid;

            _gradientGridOverlay = BuildGradientAndContrastOverlay();
            _gradientGridOverlay.Visibility = Visibility.Collapsed;
            _rootGrid.Children.Add(_gradientGridOverlay);

            // Završni meni koji se prikazuje tek nakon prolaska celog ciklusa boja + gradijenta
            _decisionOverlay = new Border
            {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(30, 33, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 76, 90)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(32, 26, 32, 26),
                MaxWidth = 680
            };

            StackPanel spMenu = new StackPanel { Orientation = Orientation.Vertical };
            spMenu.Children.Add(new TextBlock
            {
                Text = "Ciklus ekrana i gradijenta je završen",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });
            spMenu.Children.Add(new TextBlock
            {
                Text = "Provereno: 6 osnovnih boja + 7. korak (gradijent i kontrast mreža).\nIzaberite rezultat provere ili ponovite ciklus:",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 190, 200)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });

            Border statusBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 24, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 62, 76)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 18)
            };
            StackPanel spLiveInfo = new StackPanel { Orientation = Orientation.Vertical };
            _brightnessBadge = new TextBlock
            {
                Text = "💡 Automatski WMI test osvetljenja: provera u toku...",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 225)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _externalMonitorBadge = new TextBlock
            {
                Text = "🔌 Eksterni monitor (HDMI/DP/USB-C): skeniram...",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 210)),
                TextWrapping = TextWrapping.Wrap
            };
            spLiveInfo.Children.Add(_brightnessBadge);
            spLiveInfo.Children.Add(_externalMonitorBadge);
            statusBox.Child = spLiveInfo;
            spMenu.Children.Add(statusBox);

            StackPanel spButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Button btnPass = new Button
            {
                Content = "✓ PASS",
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(26, 12, 26, 12),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            btnPass.Click += (s, e) => {
                e.Handled = true;
                FinishPass();
            };
            spButtons.Children.Add(btnPass);

            Button btnFail = new Button
            {
                Content = "✗ FAIL",
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(26, 12, 26, 12),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            btnFail.Click += (s, e) => {
                e.Handled = true;
                FinishFail();
            };
            spButtons.Children.Add(btnFail);

            Button btnRepeat = new Button
            {
                Content = "↻ Ponovi",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(24, 12, 24, 12),
                Cursor = Cursors.Hand
            };
            btnRepeat.Click += (s, e) => {
                e.Handled = true;
                RestartCycle();
            };
            spButtons.Children.Add(btnRepeat);

            spMenu.Children.Add(spButtons);
            _decisionOverlay.Child = spMenu;
            _rootGrid.Children.Add(_decisionOverlay);

            _rootGrid.MouseLeftButtonDown += OnScreenClick;
            KeyDown += OnKeyDown;
            Closed += (s, e) => StopExtMonitorTimer();

            HwTestLogger.Info("DISPLAY", "Test ekrana pokrenut (6 boja + gradijent/kontrast, WMI brightness i HDMI detekcija)");
            StartBrightnessRampCheckAsync();
            UpdateColor();
        }

        private Grid BuildGradientAndContrastOverlay()
        {
            Grid g = new Grid
            {
                Background = Brushes.Black,
                IsHitTestVisible = false
            };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

            // Gornji deo: 16 nivoa sive boje (5% - 98%) za proveru uniformnosti i bandinga
            Grid grayBands = new Grid();
            for (int i = 0; i < 16; i++)
            {
                grayBands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                byte v = (byte)Math.Min(255, 10 + i * 16);
                Border cell = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(v, v, v)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1)
                };
                int pct = (int)Math.Round(v / 255.0 * 100.0);
                cell.Child = new TextBlock
                {
                    Text = string.Format("{0}%", pct),
                    FontSize = 11,
                    Foreground = (v > 140) ? Brushes.Black : Brushes.White,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8),
                    Opacity = 0.75
                };
                Grid.SetColumn(cell, i);
                grayBands.Children.Add(cell);
            }
            Grid.SetRow(grayBands, 0);
            g.Children.Add(grayBands);

            // Donji deo: RGB trake gradijenta i kontrastna mreža
            Grid rgbGrid = new Grid();
            rgbGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rgbGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rgbGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < 16; c++)
            {
                rgbGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                byte v = (byte)Math.Min(255, 12 + c * 16);
                Color[] rowColors = new Color[]
                {
                    Color.FromRgb(v, 0, 0),
                    Color.FromRgb(0, v, 0),
                    Color.FromRgb(0, 0, v)
                };
                for (int r = 0; r < 3; r++)
                {
                    Border strip = new Border
                    {
                        Background = new SolidColorBrush(rowColors[r]),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                        BorderThickness = new Thickness(0.5)
                    };
                    Grid.SetRow(strip, r);
                    Grid.SetColumn(strip, c);
                    rgbGrid.Children.Add(strip);
                }
            }
            Grid.SetRow(rgbGrid, 1);
            g.Children.Add(rgbGrid);

            Border badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 18, 20, 26)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 0, 0)
            };
            badge.Child = new TextBlock
            {
                Text = "Korak 7/7: Gradijent sive skale, RGB opseg i kontrast mreža — kliknite / Enter za završetak",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetRowSpan(badge, 2);
            g.Children.Add(badge);

            return g;
        }

        private void OnScreenClick(object sender, MouseButtonEventArgs e)
        {
            if (_showingDecision) return;
            AdvanceColor();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                FinishSkip();
                return;
            }

            if (_showingDecision) return;

            if (e.Key == Key.Space || e.Key == Key.Right || e.Key == Key.Enter)
            {
                AdvanceColor();
            }
            else if (e.Key == Key.Left)
            {
                if (_showingGradientStep)
                {
                    _showingGradientStep = false;
                    _currentIndex = _colors.Count - 1;
                    UpdateColor();
                }
                else if (_currentIndex > 0)
                {
                    _currentIndex--;
                    UpdateColor();
                }
            }
        }

        private void AdvanceColor()
        {
            if (_showingGradientStep)
            {
                _showingGradientStep = false;
                ShowDecisionMenu();
                return;
            }

            _currentIndex++;
            if (_currentIndex >= _colors.Count)
            {
                _showingGradientStep = true;
                ShowGradientStep();
                return;
            }
            UpdateColor();
        }

        private void UpdateColor()
        {
            _showingDecision = false;
            _showingGradientStep = false;
            _decisionOverlay.Visibility = Visibility.Collapsed;
            _gradientGridOverlay.Visibility = Visibility.Collapsed;
            StopExtMonitorTimer();
            Cursor = Cursors.None;

            Color c = _colors[_currentIndex];
            SolidColorBrush brush = new SolidColorBrush(c);
            Background = brush;
            _rootGrid.Background = brush;

            string name = (_currentIndex < _colorNames.Length) ? _colorNames[_currentIndex] : "Nepoznato";
            HwTestLogger.Info("DISPLAY", string.Format("Prikazana boja {0}/{1}: {2}", _currentIndex + 1, _colors.Count, name));
        }

        private void ShowGradientStep()
        {
            _showingDecision = false;
            _decisionOverlay.Visibility = Visibility.Collapsed;
            Cursor = Cursors.None;
            Background = Brushes.Black;
            _rootGrid.Background = Brushes.Black;
            _gradientGridOverlay.Visibility = Visibility.Visible;
            HwTestLogger.Info("DISPLAY", "Prikazan korak 7/7: Gradijent sive skale i kontrast mreza");
        }

        private void ShowDecisionMenu()
        {
            _showingDecision = true;
            _showingGradientStep = false;
            _cyclesCompleted++;
            Cursor = Cursors.Arrow;

            SolidColorBrush darkBg = new SolidColorBrush(Color.FromRgb(18, 20, 24));
            Background = darkBg;
            _rootGrid.Background = darkBg;
            _gradientGridOverlay.Visibility = Visibility.Collapsed;
            _decisionOverlay.Visibility = Visibility.Visible;

            StartExtMonitorTimer();
            HwTestLogger.Info("DISPLAY", string.Format("Zavrsen ciklus boja + gradijenta #{0} — prikazan izbor PASS / FAIL / Ponovi", _cyclesCompleted));
        }

        private void StartBrightnessRampCheckAsync()
        {
            if (_brightnessTestStarted) return;
            _brightnessTestStarted = true;

            Thread th = new Thread(() =>
            {
                int origPct = -1;
                bool wmiSupported = false;
                try
                {
                    ManagementScope scope = new ManagementScope(@"\\.\root\wmi");
                    scope.Connect();

                    using (ManagementObjectSearcher sCur = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness")))
                    {
                        foreach (ManagementObject mo in sCur.Get())
                        {
                            origPct = Convert.ToInt32(mo["CurrentBrightness"]);
                            break;
                        }
                    }

                    if (origPct >= 0)
                    {
                        _initialBrightnessPct = origPct;
                        using (ManagementObjectSearcher sMeth = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods")))
                        {
                            foreach (ManagementObject mo in sMeth.Get())
                            {
                                wmiSupported = true;
                                try
                                {
                                    if (!_isClosingOrDone)
                                    {
                                        mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)30 });
                                    }
                                    if (!_isClosingOrDone) Thread.Sleep(450);
                                    if (!_isClosingOrDone)
                                    {
                                        mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)100 });
                                    }
                                    if (!_isClosingOrDone) Thread.Sleep(450);
                                }
                                finally
                                {
                                    try
                                    {
                                        int restoreTarget = _initialBrightnessPct >= 0 ? _initialBrightnessPct : origPct;
                                        mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Max(0, Math.Min(100, restoreTarget)) });
                                    }
                                    catch { }
                                }
                                break;
                            }
                        }
                    }

                    if (wmiSupported)
                    {
                        _brightnessRampOk = true;
                        _brightnessRampDetail = string.Format("30% -> 100% -> {0}% OK", origPct);
                    }
                    else
                    {
                        _brightnessRampOk = false;
                        _brightnessRampDetail = "WMI kontrola nije podržana (eksterni/DDC ekran)";
                    }
                }
                catch (Exception ex)
                {
                    _brightnessRampOk = false;
                    _brightnessRampDetail = "WMI nedostupan: " + ex.Message;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_brightnessRampOk)
                    {
                        _brightnessBadge.Text = string.Format("💡 Automatski WMI test osvetljenja: {0} ✓", _brightnessRampDetail);
                        _brightnessBadge.Foreground = new SolidColorBrush(Color.FromRgb(78, 235, 125));
                    }
                    else
                    {
                        _brightnessBadge.Text = string.Format("💡 Kontrola osvetljenja (WMI): {0}", _brightnessRampDetail);
                        _brightnessBadge.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    }
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        private void StartExtMonitorTimer()
        {
            PollExternalMonitors();
            if (_extMonitorTimer != null) return;
            _extMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _extMonitorTimer.Tick += (s, e) => PollExternalMonitors();
            _extMonitorTimer.Start();
        }

        private void StopExtMonitorTimer()
        {
            if (_extMonitorTimer != null)
            {
                try { _extMonitorTimer.Stop(); } catch { }
                _extMonitorTimer = null;
            }
        }

        private void PollExternalMonitors()
        {
            try
            {
                int active = 0;
                ManagementScope scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Active FROM WmiMonitorBasicDisplayParams")))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        object val = mo["Active"];
                        if (val != null && Convert.ToBoolean(val))
                        {
                            active++;
                        }
                    }
                }
                if (active <= 0) active = 1;
                _activeMonitorCount = active;

                if (active >= 2)
                {
                    _externalMonitorBadge.Text = string.Format("🔌 Eksterni monitor (HDMI/DP/USB-C): DETEKTOVAN ({0} aktivna ekrana) ✓", active);
                    _externalMonitorBadge.Foreground = new SolidColorBrush(Color.FromRgb(78, 235, 125));
                }
                else
                {
                    _externalMonitorBadge.Text = string.Format("🔌 Eksterni monitor (HDMI/DP/USB-C): Priključite HDMI/DP kabl za detekciju (aktivno: {0})", active);
                    _externalMonitorBadge.Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 210));
                }
            }
            catch
            {
                _externalMonitorBadge.Text = "🔌 Eksterni monitor: WMI proveru nije moguće očitati na ovom uređaju";
            }
        }

        private void RestartCycle()
        {
            _currentIndex = 0;
            _showingGradientStep = false;
            HwTestLogger.Info("DISPLAY", "Ponovno pokretanje ciklusa boja i gradijenta ekrana");
            UpdateColor();
        }

        private void RestoreInitialBrightnessIfNeeded()
        {
            if (_initialBrightnessPct < 0) return;
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using (ManagementObjectSearcher sMeth = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods")))
                {
                    foreach (ManagementObject mo in sMeth.Get())
                    {
                        mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Max(0, Math.Min(100, _initialBrightnessPct)) });
                        break;
                    }
                }
            }
            catch { }
        }

        private void SafeCloseDialog(bool result)
        {
            StopExtMonitorTimer();
            RestoreInitialBrightnessIfNeeded();
            try { DialogResult = result; } catch { }
            try { Close(); } catch { }
        }

        private void FinishPass()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            int totalCycles = Math.Max(1, _cyclesCompleted);
            Result.Status = TestStatus.Pass;
            Result.Attempts = totalCycles;
            Result.Details["defects_found"] = 0;
            Result.Details["cycles_completed"] = totalCycles;
            Result.Details["gradient_grid_checked"] = true;
            Result.Details["brightness_ramp_ok"] = _brightnessRampOk;
            Result.Details["brightness_ramp_detail"] = _brightnessRampDetail;
            Result.Details["external_monitor_detected"] = (_activeMonitorCount >= 2);
            Result.Details["active_monitors_count"] = _activeMonitorCount;
            SafeCloseDialog(true);
        }

        private void FinishFail()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            int totalCycles = Math.Max(1, _cyclesCompleted);
            Result.Status = TestStatus.Fail;
            Result.Attempts = totalCycles;
            Result.Reason = "Prijavljen defekt ekrana nakon pregleda ciklusa boja i gradijenta.";
            Result.Details["cycles_completed"] = totalCycles;
            Result.Details["gradient_grid_checked"] = true;
            Result.Details["brightness_ramp_ok"] = _brightnessRampOk;
            Result.Details["brightness_ramp_detail"] = _brightnessRampDetail;
            Result.Details["external_monitor_detected"] = (_activeMonitorCount >= 2);
            Result.Details["active_monitors_count"] = _activeMonitorCount;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test ekrana";
            SafeCloseDialog(false);
        }
    }
}
