using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class UsbPortTestWindow : Window
    {
        private readonly ModelProfileClient _profileClient;
        private readonly string _modelName;
        private readonly string _serialNumber;
        private int _targetPortCount = 3;
        private readonly HashSet<string> _detectedPortLocations;
        private readonly HashSet<string> _physicalPortLocations;
        private bool _userManuallyAdjustedTarget = false;

        private TextBlock _progressText;
        private ProgressBar _progressBar;
        private TextBlock _usbDeviceStatusText;
        private Button _btnConfirmSharedPort;
        private Button _btnAdjustTargetToDetected;
        private TextBlock _chargerStatusText;
        private StackPanel _portListPanel;
        private TextBox _txtPortCount;
        private Button _btnPass;
        private HwndSource _hwndSource;
        private DispatcherTimer _pollTimer;
        private DispatcherTimer _debounceTimer;
        private DateTime _lastDeviceChangeUtc = DateTime.MinValue;
        private int _scanInProgress = 0;
        private readonly HashSet<string> _loggedScanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedSkipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _baselineUsbPnpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _currentlyAttachedDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _attachedDeviceHadStrongLocation = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _deviceReplugCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastUnplugUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastAnyUnplugUtc = DateTime.MinValue;
        private bool _baselineCaptured = false;
        private int _totalReplugEvents = 0;
        private string _pendingSharedPortKey = null;
        private string _pendingSharedDeviceName = null;

        private bool _chargerStateInitialized = false;
        private bool _lastAcOnline = false;
        private bool _waitingForChargerReplug = false;
        private DateTime _chargerDisconnectedUtc = DateTime.MinValue;
        private int _chargerPlugCount = 0;
        private int _maxTypeCChargerCap = 0;
        private bool _cachedTypeCCapInitialized = false;
        private bool? _cachedHasTypeCController = null;
        private bool _isClosingOrDone = false;

        private Button _btnUsbEasterEgg;
        private bool _easterEggTriggered = false;
        private Grid _easterEggOverlay;
        private Image _easterEggImageControl;
        private BitmapImage _easterEggBitmap;
        private int _easterEggAnimGen = 0;

        private readonly object _sirenWaveLock = new object();
        private readonly ManualResetEventSlim _sirenDoneEvent = new ManualResetEventSlim(true);
        private IntPtr _activeSirenWaveOut = IntPtr.Zero;
        private volatile bool _stopSiren = false;
        private NativeMethods.IAudioEndpointVolume _sirenAudioVolume;
        private float _savedSirenVolume = 0.5f;
        private bool _savedSirenMute = false;
        private bool _sirenVolumeModified = false;

        public TestItemResult Result { get; private set; }

        public UsbPortTestWindow(string modelName, int? prefilledCount, ModelProfileClient profileClient)
            : this(modelName, prefilledCount, profileClient, "")
        {
        }

        public UsbPortTestWindow(string modelName, int? prefilledCount, ModelProfileClient profileClient, string serialNumber)
        {
            _modelName = modelName ?? "Generic";
            _serialNumber = serialNumber ?? "";
            _profileClient = profileClient;
            if (prefilledCount.HasValue && prefilledCount.Value > 0)
                _targetPortCount = prefilledCount.Value;

            Result = new TestItemResult("usb",
                HwTestLocalization.Pick("USB portovi", "USB 接口"),
                HwTestLocalization.Pick("Ispitivanje svih fizičkih USB portova uzastopnim ubacivanjem fleš diska", "通过依次插入 U 盘测试所有物理 USB 接口"));
            _detectedPortLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _physicalPortLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Title = HwTestLocalization.Pick("WIS — Test USB Portova", "WIS — USB 接口测试");
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

            // Header
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
                Text = HwTestLocalization.Pick("🔌 TEST USB PORTOVA", "🔌 USB 接口测试"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(title);

            TextBlock sub = new TextBlock
            {
                Text = HwTestLocalization.Pick("Ubacite bilo koji USB fleš disk redom u svaki USB port na računaru (sačekajte detekciju pre prelaska na sledeći)",
                    "请将任意 U 盘依次插入电脑的每个 USB 接口（等待识别后再换下一个接口）"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            spH.Children.Add(sub);
            hg.Children.Add(spH);

            // Target port setting
            StackPanel spTarget = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock lblTarget = new TextBlock
            {
                Text = HwTestLocalization.Pick("Ukupan broj USB portova na kućištu:", "机身 USB 接口总数:"),
                Foreground = Brushes.White,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            spTarget.Children.Add(lblTarget);

            Button btnMinusPort = new Button
            {
                Content = "−",
                Width = 32,
                Height = 32,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(65, 70, 82)),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnMinusPort.Click += (s, e) =>
            {
                if (_targetPortCount > 1)
                {
                    _userManuallyAdjustedTarget = true;
                    _targetPortCount--;
                    _txtPortCount.Text = _targetPortCount.ToString();
                }
            };
            spTarget.Children.Add(btnMinusPort);

            _txtPortCount = new TextBox
            {
                Text = _targetPortCount.ToString(),
                Width = 46,
                Height = 32,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            _txtPortCount.TextChanged += (s, e) => {
                int val;
                if (int.TryParse(_txtPortCount.Text.Trim(), out val) && val > 0)
                {
                    _userManuallyAdjustedTarget = true;
                    _targetPortCount = val;
                    UpdateProgress();
                }
            };
            spTarget.Children.Add(_txtPortCount);

            Button btnPlusPort = new Button
            {
                Content = "+",
                Width = 32,
                Height = 32,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(65, 70, 82)),
                Foreground = Brushes.White,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnPlusPort.Click += (s, e) =>
            {
                if (_targetPortCount < 20)
                {
                    _userManuallyAdjustedTarget = true;
                    _targetPortCount++;
                    _txtPortCount.Text = _targetPortCount.ToString();
                }
            };
            spTarget.Children.Add(btnPlusPort);
            Grid.SetColumn(spTarget, 1);
            hg.Children.Add(spTarget);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Center Panel
            Border centerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(40),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 650
            };

            StackPanel spC = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _progressText = new TextBlock
            {
                Text = HwTestLocalization.PickFormat("Detektovano: 0 / {0} porta", "已检测到: 0 / {0} 个接口", _targetPortCount),
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15)
            };
            spC.Children.Add(_progressText);

            _progressBar = new ProgressBar
            {
                Width = 500,
                Height = 24,
                Minimum = 0,
                Maximum = _targetPortCount,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            spC.Children.Add(_progressBar);

            _chargerStatusText = new TextBlock
            {
                Text = HwTestLocalization.Pick("⚡ Status punjača: proveravam USB-C / PD napajanje...", "⚡ 充电器状态: 正在检测 USB-C / PD 供电..."),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 215)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            spC.Children.Add(_chargerStatusText);

            _usbDeviceStatusText = new TextBlock
            {
                Text = HwTestLocalization.Pick("🔌 Ubacite USB fleš ili bilo koji USB uređaj u prvi port (isti uređaj testira sve portove).",
                    "🔌 请将 U 盘或任意 USB 设备插入第一个接口（可用同一设备测试所有接口）。"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(165, 215, 255)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            spC.Children.Add(_usbDeviceStatusText);

            _btnConfirmSharedPort = new Button
            {
                Content = HwTestLocalization.Pick("➕ Priznaj priključeni uređaj kao novi port", "➕ 将当前连接的设备确认为新接口"),
                Background = new SolidColorBrush(Color.FromRgb(230, 145, 25)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnConfirmSharedPort.Click += (s, e) => ConfirmPendingSharedPort();
            spC.Children.Add(_btnConfirmSharedPort);

            _btnAdjustTargetToDetected = new Button
            {
                Content = HwTestLocalization.Pick("🎯 Uređaj fizički ima manje portova? Podesi cilj na detektovane", "🎯 设备物理接口数量更少？将目标调整为已检测数量"),
                Background = new SolidColorBrush(Color.FromRgb(45, 95, 155)),
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnAdjustTargetToDetected.Click += (s, e) =>
            {
                if (_detectedPortLocations.Count > 0)
                {
                    _userManuallyAdjustedTarget = true;
                    _targetPortCount = _detectedPortLocations.Count;
                    _txtPortCount.Text = _targetPortCount.ToString();
                    UpdateProgress();
                }
            };
            spC.Children.Add(_btnAdjustTargetToDetected);

            _portListPanel = new StackPanel { Width = 500 };
            spC.Children.Add(_portListPanel);

            centerBorder.Child = spC;
            Grid.SetRow(centerBorder, 1);
            root.Children.Add(centerBorder);

            // Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            Grid fgFooter = new Grid();
            fgFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fgFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _btnUsbEasterEgg = new Button
            {
                Content = HwTestLocalization.Pick("Preskoči test", "跳过测试"),
                Background = new SolidColorBrush(Color.FromRgb(75, 80, 92)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(16, 8, 16, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            _btnUsbEasterEgg.Click += (s, e) => TriggerSkipEasterEgg();
            Grid.SetColumn(_btnUsbEasterEgg, 0);
            fgFooter.Children.Add(_btnUsbEasterEgg);

            StackPanel spF = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _btnPass = new Button
            {
                Content = HwTestLocalization.Pick("✓ Potvrdi (PASS)", "✓ 确认通过 (PASS)"),
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 15, 0),
                IsEnabled = false
            };
            _btnPass.Click += (s, e) => FinishPass();
            spF.Children.Add(_btnPass);

            Button btnUsbFail = new Button
            {
                Content = HwTestLocalization.Pick("✗ Neispravan USB port (FAIL)", "✗ USB 接口故障 (FAIL)"),
                Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 8, 16, 8)
            };
            btnUsbFail.Click += (s, e) => FinishFail();
            spF.Children.Add(btnUsbFail);

            Grid.SetColumn(spF, 1);
            fgFooter.Children.Add(spF);

            footer.Child = fgFooter;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            // Fullscreen Easter Egg overlay (skriven dok se ne klikne mamac dugme)
            _easterEggImageControl = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(_easterEggImageControl, BitmapScalingMode.HighQuality);

            _easterEggOverlay = new Grid
            {
                Background = Brushes.Black,
                Visibility = Visibility.Collapsed,
                Opacity = 0.0,
                IsHitTestVisible = false
            };
            Grid.SetRowSpan(_easterEggOverlay, 3);
            Panel.SetZIndex(_easterEggOverlay, 999);
            _easterEggOverlay.Children.Add(_easterEggImageControl);
            root.Children.Add(_easterEggOverlay);

            Content = root;

            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            WindowInteropHelper helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(HwndHook);
            }

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _debounceTimer.Tick += (s, args) =>
            {
                if (_debounceTimer != null) _debounceTimer.Stop();
                if (!_isClosingOrDone) TriggerUsbScan();
            };

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _pollTimer.Tick += (s, args) =>
            {
                if (_isClosingOrDone) return;
                if (_debounceTimer != null && _debounceTimer.IsEnabled) return;
                if (_lastDeviceChangeUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _lastDeviceChangeUtc).TotalMilliseconds < 300.0)
                {
                    return;
                }
                TriggerUsbScan();
            };
            _pollTimer.Start();

            TriggerUsbScan();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _isClosingOrDone = true;
            _easterEggAnimGen++;
            if (_easterEggOverlay != null)
            {
                try
                {
                    _easterEggOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                    _easterEggOverlay.Visibility = Visibility.Collapsed;
                }
                catch { }
            }

            _stopSiren = true;
            lock (_sirenWaveLock)
            {
                if (_activeSirenWaveOut != IntPtr.Zero)
                {
                    try { NativeMethods.waveOutReset(_activeSirenWaveOut); } catch { }
                }
            }
            try { _sirenDoneEvent.Wait(500); } catch { }
            RestoreSirenAudioVolume();

            if (_debounceTimer != null)
            {
                try { _debounceTimer.Stop(); } catch { }
                _debounceTimer = null;
            }
            if (_pollTimer != null)
            {
                try { _pollTimer.Stop(); } catch { }
                _pollTimer = null;
            }
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource = null;
            }
        }

        private void TriggerSkipEasterEgg()
        {
            if (_isClosingOrDone || _easterEggTriggered) return;
            _easterEggTriggered = true;

            if (_btnUsbEasterEgg != null)
            {
                _btnUsbEasterEgg.IsEnabled = false;
                _btnUsbEasterEgg.Visibility = Visibility.Collapsed;
            }

            _stopSiren = false;
            BoostAudioToMaxForSiren();
            ThreadPool.QueueUserWorkItem((state) =>
            {
                PlaySirenAsync();
            });

            BitmapImage bmp = LoadEasterEggBitmap();
            if (bmp != null && _easterEggOverlay != null && _easterEggImageControl != null)
            {
                if (_easterEggImageControl.Source == null)
                {
                    _easterEggImageControl.Source = bmp;
                }

                int myGen = ++_easterEggAnimGen;
                _easterEggOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _easterEggOverlay.Opacity = 1.0;
                _easterEggOverlay.Visibility = Visibility.Visible;

                DoubleAnimation anim = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromSeconds(2.0)),
                    FillBehavior = FillBehavior.Stop
                };
                anim.Completed += (s, e) =>
                {
                    if (myGen != _easterEggAnimGen || _isClosingOrDone) return;
                    _easterEggOverlay.Opacity = 0.0;
                    _easterEggOverlay.Visibility = Visibility.Collapsed;
                };
                _easterEggOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
            }

            HwTestLogger.Info("USB_EASTER_EGG", "Serviser je kliknuo mamac dugme 'Preskoči test' — prikazan fullscreen Easter Egg i sirena (2s, 100%).");
        }

        private BitmapImage LoadEasterEggBitmap()
        {
            if (_easterEggBitmap != null) return _easterEggBitmap;

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream resStream = asm.GetManifestResourceStream("Assets.usb_skip_easter_egg.jpg"))
                {
                    if (resStream == null)
                    {
                        HwTestLogger.Warn("USB_EASTER_EGG", "Ugrađeni resurs 'Assets.usb_skip_easter_egg.jpg' nije pronađen.");
                        return null;
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        resStream.CopyTo(ms);
                        ms.Position = 0;

                        BitmapImage bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();

                        _easterEggBitmap = bmp;
                        return _easterEggBitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("USB_EASTER_EGG", "Greška pri učitavanju Easter Egg slike: " + ex.Message);
                return null;
            }
        }

        private void BoostAudioToMaxForSiren()
        {
            try
            {
                NativeMethods.IMMDeviceEnumerator enumerator = (NativeMethods.IMMDeviceEnumerator)new NativeMethods.MMDeviceEnumeratorComObject();
                NativeMethods.IMMDevice dev;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) == 0 && dev != null)
                {
                    Guid iid = typeof(NativeMethods.IAudioEndpointVolume).GUID;
                    object obj;
                    if (dev.Activate(ref iid, 1, IntPtr.Zero, out obj) == 0 && obj != null)
                    {
                        _sirenAudioVolume = (NativeMethods.IAudioEndpointVolume)obj;
                        _sirenAudioVolume.GetMasterVolumeLevelScalar(out _savedSirenVolume);
                        _sirenAudioVolume.GetMute(out _savedSirenMute);

                        Guid empty = Guid.Empty;
                        _sirenAudioVolume.SetMute(false, ref empty);
                        _sirenAudioVolume.SetMasterVolumeLevelScalar(1.0f, ref empty);
                        _sirenVolumeModified = true;
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("USB_EASTER_EGG", "BoostAudioToMaxForSiren fallback: " + ex.Message);
            }
        }

        private void RestoreSirenAudioVolume()
        {
            try
            {
                if (_sirenVolumeModified && _sirenAudioVolume != null)
                {
                    _sirenVolumeModified = false;
                    Guid empty = Guid.Empty;
                    _sirenAudioVolume.SetMute(_savedSirenMute, ref empty);
                    _sirenAudioVolume.SetMasterVolumeLevelScalar(_savedSirenVolume, ref empty);
                    _sirenAudioVolume = null;
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("USB_EASTER_EGG", "RestoreSirenAudioVolume fallback: " + ex.Message);
            }
        }

        private static byte[] GenerateSirenPcm2Seconds()
        {
            const int sampleRate = 22050;
            const int totalSamples = sampleRate * 2; // 44,100 uzoraka = tačno 2.0 s
            byte[] pcm = new byte[totalSamples * 2]; // 16-bit PCM mono = 88,200 bajtova
            int fadeSamples = (int)(sampleRate * 0.015); // 15 ms fade-in / fade-out
            double phase = 0.0;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double freq = 1000.0 + 350.0 * Math.Sin(2.0 * Math.PI * 1.8 * t);
                phase += 2.0 * Math.PI * freq / sampleRate;
                if (phase > 2.0 * Math.PI * 1000.0)
                {
                    phase -= 2.0 * Math.PI * 1000.0;
                }

                double wave = 0.78 * Math.Sin(phase) + 0.22 * Math.Sin(3.0 * phase);

                double env = 1.0;
                if (i < fadeSamples)
                {
                    env = (double)i / fadeSamples;
                }
                else if (i >= totalSamples - fadeSamples)
                {
                    env = (double)(totalSamples - 1 - i) / fadeSamples;
                }

                double scaled = wave * env * (0.94 * short.MaxValue);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                if (scaled < short.MinValue) scaled = short.MinValue;
                short sample = (short)scaled;

                int idx = i * 2;
                pcm[idx] = (byte)(sample & 0xFF);
                pcm[idx + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return pcm;
        }

        private void PlaySirenAsync()
        {
            if (_stopSiren || _isClosingOrDone || NativeMethods.waveOutGetNumDevs() == 0)
            {
                try
                {
                    if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.BeginInvoke(new Action(RestoreSirenAudioVolume));
                    }
                }
                catch { }
                return;
            }

            byte[] pcm = GenerateSirenPcm2Seconds();
            NativeMethods.WAVEFORMATEX wfx = new NativeMethods.WAVEFORMATEX
            {
                wFormatTag = NativeMethods.WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = 22050,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = 44100,
                cbSize = 0
            };

            lock (_sirenWaveLock)
            {
                _sirenDoneEvent.Reset();
            }

            IntPtr hwo = IntPtr.Zero;
            IntPtr pData = IntPtr.Zero;
            NativeMethods.WAVEHDR wh = new NativeMethods.WAVEHDR();
            bool hdrPrepared = false;

            try
            {
                if (_stopSiren || _isClosingOrDone)
                {
                    return;
                }

                if (NativeMethods.waveOutOpen(out hwo, NativeMethods.WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, 0) == 0 && hwo != IntPtr.Zero)
                {
                    lock (_sirenWaveLock)
                    {
                        if (!_stopSiren && !_isClosingOrDone)
                        {
                            _activeSirenWaveOut = hwo;
                        }
                    }

                    if (!_stopSiren && !_isClosingOrDone)
                    {
                        pData = Marshal.AllocHGlobal(pcm.Length);
                        Marshal.Copy(pcm, 0, pData, pcm.Length);

                        wh.lpData = pData;
                        wh.dwBufferLength = (uint)pcm.Length;

                        if (NativeMethods.waveOutPrepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh)) == 0)
                        {
                            hdrPrepared = true;
                            if (NativeMethods.waveOutWrite(hwo, ref wh, (uint)Marshal.SizeOf(wh)) == 0)
                            {
                                const int sirenDurationMs = 2000;
                                int maxWaitMs = sirenDurationMs + 40;
                                int waited = 0;
                                while (waited < maxWaitMs && !_stopSiren && !_isClosingOrDone)
                                {
                                    Thread.Sleep(40);
                                    waited += 40;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("USB_EASTER_EGG", "PlaySirenAsync error: " + ex.Message);
            }
            finally
            {
                lock (_sirenWaveLock)
                {
                    if (hwo != IntPtr.Zero && _activeSirenWaveOut == hwo)
                    {
                        _activeSirenWaveOut = IntPtr.Zero;
                    }
                }
                if (hwo != IntPtr.Zero)
                {
                    try { NativeMethods.waveOutReset(hwo); } catch { }
                    if (hdrPrepared)
                    {
                        try { NativeMethods.waveOutUnprepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh)); } catch { }
                    }
                    try { NativeMethods.waveOutClose(hwo); } catch { }
                }
                if (pData != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(pData); } catch { }
                }

                _sirenDoneEvent.Set();

                try
                {
                    if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.BeginInvoke(new Action(RestoreSirenAudioVolume));
                    }
                }
                catch { }
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == NativeMethods.DBT_DEVICEARRIVAL ||
                    eventType == NativeMethods.DBT_DEVICEREMOVECOMPLETE ||
                    eventType == NativeMethods.DBT_DEVNODES_CHANGED)
                {
                    ScheduleDebouncedUsbScan();
                }
            }
            else if (msg == NativeMethods.WM_POWERBROADCAST)
            {
                CheckUsbCChargerPort();
                ScheduleDebouncedUsbScan();
            }
            return IntPtr.Zero;
        }

        private void ScheduleDebouncedUsbScan()
        {
            if (_isClosingOrDone) return;
            _lastDeviceChangeUtc = DateTime.UtcNow;
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }

        private const string AutoChargerPortKey = "AUTO-USBC-CHARGER";

        private class UsbCandidateScan
        {
            public string DeviceId;
            public string FriendlyName;
            public string RawPortKey;
            public string DisplayLocationInfo;
            public bool IsStrongLocation;
            public string Diag;
        }

        private void ConfirmPendingSharedPort()
        {
            if (string.IsNullOrEmpty(_pendingSharedPortKey)) return;
            string devLabel = string.IsNullOrEmpty(_pendingSharedDeviceName) ? "USB uređaj" : _pendingSharedDeviceName;
            string uniqueKey = string.Format("{0}#PORT_{1} ({2})", _pendingSharedPortKey, _detectedPortLocations.Count + 1, devLabel);
            _pendingSharedPortKey = null;
            _pendingSharedDeviceName = null;
            if (_btnConfirmSharedPort != null)
            {
                _btnConfirmSharedPort.Visibility = Visibility.Collapsed;
            }
            if (_detectedPortLocations.Add(uniqueKey))
            {
                AddPortEntry(uniqueKey);
                UpdateProgress();
                HwTestLogger.Info("USB", string.Format("Registrovan deljeni port #{0}: {1}", _detectedPortLocations.Count, uniqueKey));
            }
            UpdateSingleDeviceStatusUi();
        }

        private void UpdateSingleDeviceStatusUi()
        {
            if (_usbDeviceStatusText == null) return;

            if (!string.IsNullOrEmpty(_pendingSharedPortKey))
            {
                string devLabel = string.IsNullOrEmpty(_pendingSharedDeviceName)
                    ? HwTestLocalization.Pick("USB uređaj", "USB 设备")
                    : _pendingSharedDeviceName;
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                _usbDeviceStatusText.Text = HwTestLocalization.PickFormat(
                    "⚠️ '{0}' je ponovo uboden, ali kontroler prijavljuje istu putanju kao prethodni port. Ako je ovo drugi port, kliknite dugme ispod:",
                    "⚠️ “{0}”已重新插入，但控制器报告了与上一个接口相同的路径。如果这是另一个物理接口，请点击下方按钮：",
                    devLabel);
                if (_btnConfirmSharedPort != null) _btnConfirmSharedPort.Visibility = Visibility.Visible;
                return;
            }

            if (_btnConfirmSharedPort != null) _btnConfirmSharedPort.Visibility = Visibility.Collapsed;

            if (_detectedPortLocations.Count > _targetPortCount)
            {
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                _usbDeviceStatusText.Text = HwTestLocalization.PickFormat(
                    "✨ Otkriven dodatni fizički port ({0}/{1}) — profil modela će automatski naučiti {0} portova nakon PASS.",
                    "✨ 检测到额外物理接口 ({0}/{1}) — 点击 PASS 后机型配置将自动学习为 {0} 个接口。",
                    _detectedPortLocations.Count, _targetPortCount);
                return;
            }

            if (_currentlyAttachedDevices.Count > 0)
            {
                if (_detectedPortLocations.Count >= _targetPortCount)
                {
                    _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                    _usbDeviceStatusText.Text = HwTestLocalization.PickFormat("✅ Svi ciljani USB portovi ({0}/{1}) su uspešno detektovani!", "✅ 所有目标 USB 接口 ({0}/{1}) 已成功检测到！", _detectedPortLocations.Count, _targetPortCount);
                }
                else
                {
                    _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(165, 215, 255));
                    _usbDeviceStatusText.Text = HwTestLocalization.Pick("🔄 Uređaj je u portu — sada ga IZVUCITE i ubacite u SLEDEĆI USB port!", "🔄 设备已插入接口 — 现在请拔出并插入下一个 USB 接口！");
                }
            }
            else if (_totalReplugEvents > 0)
            {
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                _usbDeviceStatusText.Text = HwTestLocalization.Pick("✅ Uređaj izvučen — spremno! Ubacite ga u sledeći USB port...", "✅ 设备已拔出 — 就绪！请将其插入下一个 USB 接口...");
            }
            else
            {
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 215));
                _usbDeviceStatusText.Text = HwTestLocalization.Pick("🔌 Ubacite USB fleš ili bilo koji USB uređaj u prvi port (isti uređaj testira sve portove).",
                    "🔌 请将 U 盘或任意 USB 设备插入第一个接口（可用同一设备测试所有接口）。");
            }
        }

        private void LogUsbScanSkipOnce(string pnp, string source, string statusDiag)
        {
            string key = string.Format("{0}|{1}|{2}|{3}", _totalReplugEvents, source, pnp, statusDiag ?? "");
            bool shouldLog;
            lock (_loggedSkipKeys)
            {
                shouldLog = _loggedSkipKeys.Add(key);
            }
            if (shouldLog)
            {
                HwTestLogger.Info("USB_SCAN_SKIP", string.Format(
                    "Preskočen neaktivan USB devnode ({0}): pnp='{1}' | {2}",
                    source, pnp, statusDiag ?? ""));
            }
        }

        private bool IsPortCurrentlyOccupiedByOtherDevice(string rawPortKey, string currentDeviceId)
        {
            if (string.IsNullOrEmpty(rawPortKey)) return false;
            foreach (KeyValuePair<string, string> kv in _currentlyAttachedDevices)
            {
                if (!string.Equals(kv.Key, currentDeviceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(kv.Value, rawPortKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FormatPortDisplayLabel(string portKey, string displayLocInfo, string friendlyName)
        {
            if (!string.IsNullOrEmpty(displayLocInfo))
                return string.Format("{0} ({1}) ({2})", portKey, displayLocInfo, friendlyName);
            return string.Format("{0} ({1})", portKey, friendlyName);
        }

        private void TriggerUsbScan()
        {
            if (_isClosingOrDone) return;
            if (Interlocked.CompareExchange(ref _scanInProgress, 1, 0) != 0)
            {
                return;
            }

            DateTime scanStartUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem((state) => {
                List<UsbCandidateScan> foundPorts = new List<UsbCandidateScan>();
                bool isFirstBaselinePass = !_baselineCaptured;
                List<string> newBaselineIds = new List<string>();
                try
                {
                    Dictionary<string, string> candidatePnps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    try
                    {
                        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                            "SELECT DeviceID, PNPDeviceID, Model FROM Win32_DiskDrive WHERE InterfaceType = 'USB'"))
                        {
                            foreach (ManagementObject drive in searcher.Get())
                            {
                                string pnp = (drive["PNPDeviceID"] ?? "").ToString().Trim();
                                string model = (drive["Model"] ?? "USB Flash").ToString().Trim();
                                if (!string.IsNullOrEmpty(pnp) && !candidatePnps.ContainsKey(pnp))
                                {
                                    string diskStatusDiag;
                                    if (!NativeMethods.IsUsbDevNodeLiveAndStarted(pnp, out diskStatusDiag))
                                    {
                                        LogUsbScanSkipOnce(pnp, "Win32_DiskDrive", diskStatusDiag);
                                        continue;
                                    }
                                    candidatePnps[pnp] = model;
                                }
                            }
                        }
                    }
                    catch { }

                    // Dodatno citamo i USBSTOR PnP cvorove u slucaju da DiskDrive kasni prilikom montiranja na xHCI/USB-C portu
                    try
                    {
                        using (ManagementObjectSearcher storSearcher = new ManagementObjectSearcher(
                            "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR\\\\%' AND Status = 'OK'"))
                        {
                            foreach (ManagementObject ent in storSearcher.Get())
                            {
                                string pnp = (ent["DeviceID"] ?? "").ToString().Trim();
                                string name = (ent["Name"] ?? "USB Storage").ToString().Trim();
                                if (!string.IsNullOrEmpty(pnp) && !candidatePnps.ContainsKey(pnp))
                                {
                                    candidatePnps[pnp] = name;
                                }
                            }
                        }
                    }
                    catch { }

                    // Skeniramo i eksterne non-storage USB uredjaje (mis, USB-C hub, LAN adapter, USB4...)
                    // kako bi serviser mogao istim bilo kojim USB uredjajem da testira sve portove.
                    try
                    {
                        using (ManagementObjectSearcher usbSearcher = new ManagementObjectSearcher(
                            "SELECT Name, DeviceID, Service, PNPClass FROM Win32_PnPEntity WHERE (PNPDeviceID LIKE 'USB\\\\%' OR PNPDeviceID LIKE 'USB4\\\\%') AND Status = 'OK'"))
                        {
                            foreach (ManagementObject ent in usbSearcher.Get())
                            {
                                string pnp = (ent["DeviceID"] ?? "").ToString().Trim();
                                string svc = (ent["Service"] ?? "").ToString().Trim();
                                string pnpClass = (ent["PNPClass"] ?? "").ToString().Trim();
                                string name = (ent["Name"] ?? "USB uređaj").ToString().Trim();
                                if (string.IsNullOrEmpty(pnp)) continue;
                                if (pnp.IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (pnp.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (!pnp.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) &&
                                    !pnp.StartsWith("USB4\\VID_", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                // Storage uredjaje vec pokriva Win32_DiskDrive / USBSTOR gore
                                if (svc.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("UASPStor", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                // Interne komponente laptopa koje nikada nisu eksterni USB portovi
                                if (svc.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("usbvideo", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("WacHIDRouterISD", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("WUDFRd", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("AKCCID", StringComparison.OrdinalIgnoreCase) ||
                                    svc.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase) ||
                                    name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Integrated Camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("IR Camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Smart Card", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Fingerprint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Synaptics WBDI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Lenovo Pen", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    continue;
                                }

                                bool clearlyExternal = IsClearlyExternalPreAttachedUsbDevice(pnp, name, svc, pnpClass);
                                if (isFirstBaselinePass)
                                {
                                    if (!clearlyExternal)
                                    {
                                        newBaselineIds.Add(pnp);
                                        continue;
                                    }
                                    HwTestLogger.Info("USB_TEST", string.Format(
                                        "Inicijalno prepoznat eksterni USB uređaj: {0} (Service={1}, Class={2}, PnP={3})",
                                        name, svc, pnpClass, pnp));
                                }

                                if (!isFirstBaselinePass)
                                {
                                    lock (_baselineUsbPnpIds)
                                    {
                                        if (_baselineUsbPnpIds.Contains(pnp)) continue;
                                    }
                                }

                                if (!candidatePnps.ContainsKey(pnp))
                                {
                                    candidatePnps[pnp] = name;
                                }
                            }
                        }
                    }
                    catch { }

                    foreach (KeyValuePair<string, string> kv in candidatePnps)
                    {
                        string pnp = kv.Key;
                        string friendly = kv.Value;

                        string statusDiag;
                        if (!NativeMethods.IsUsbDevNodeLiveAndStarted(pnp, out statusDiag))
                        {
                            LogUsbScanSkipOnce(pnp, "DevNodeGuard", statusDiag);
                            continue;
                        }

                        string dbg;
                        string displayLocInfo;
                        string cfgLoc = NativeMethods.ResolveUsbPhysicalPortLocation(pnp, out dbg, out displayLocInfo);
                        string wmiLoc = string.IsNullOrEmpty(cfgLoc) ? QueryUsbLocation(pnp) : null;
                        bool isStrong = !string.IsNullOrEmpty(cfgLoc) || !string.IsNullOrEmpty(wmiLoc);
                        string rawLoc = !string.IsNullOrEmpty(cfgLoc)
                            ? cfgLoc
                            : (!string.IsNullOrEmpty(wmiLoc) ? wmiLoc : pnp);

                        string diag = string.Format("name='{0}' | pnp='{1}' | {2} | dev='{3}' | disp='{4}' | wmi='{5}' -> rawLoc='{6}' (strong={7})",
                            friendly, pnp, dbg, statusDiag ?? "", displayLocInfo ?? "", wmiLoc ?? "", rawLoc, isStrong);

                        foundPorts.Add(new UsbCandidateScan
                        {
                            DeviceId = pnp,
                            FriendlyName = friendly,
                            RawPortKey = rawLoc,
                            DisplayLocationInfo = displayLocInfo,
                            IsStrongLocation = isStrong,
                            Diag = diag
                        });
                    }

                    // Jaka lokacija ima prednost pri deduplikaciji unutar istog skena
                    foundPorts.Sort((a, b) => b.IsStrongLocation.CompareTo(a.IsStrongLocation));
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        try
                        {
                            if (_isClosingOrDone) return;

                            if (isFirstBaselinePass)
                            {
                                lock (_baselineUsbPnpIds)
                                {
                                    for (int i = 0; i < newBaselineIds.Count; i++)
                                        _baselineUsbPnpIds.Add(newBaselineIds[i]);
                                }
                                _baselineCaptured = true;
                            }
                            else if (_lastDeviceChangeUtc > scanStartUtc)
                            {
                                // Stigao je nov WM_DEVICECHANGE dok je WMI sken trajao: cekamo stabilizaciju debounce tajmera
                                return;
                            }

                            HashSet<string> activeNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < foundPorts.Count; i++)
                            {
                                activeNow.Add(foundPorts[i].DeviceId);
                            }

                            // 1. Detekcija izvlacenja (Unplug) za Single-Device Multi-Port tok
                            List<string> removedIds = new List<string>();
                            foreach (string prevId in _currentlyAttachedDevices.Keys)
                            {
                                if (!activeNow.Contains(prevId))
                                    removedIds.Add(prevId);
                            }
                            DateTime unplugUtc = DateTime.UtcNow;
                            for (int i = 0; i < removedIds.Count; i++)
                            {
                                string remId = removedIds[i];
                                _currentlyAttachedDevices.Remove(remId);
                                _attachedDeviceHadStrongLocation.Remove(remId);
                                _lastUnplugUtc[remId] = unplugUtc;
                                _lastAnyUnplugUtc = unplugUtc;
                                int prevCnt = 0;
                                _deviceReplugCounts.TryGetValue(remId, out prevCnt);
                                _deviceReplugCounts[remId] = prevCnt + 1;
                                _totalReplugEvents++;
                                _pendingSharedPortKey = null;
                                _pendingSharedDeviceName = null;
                                HwTestLogger.Info("USB_UNPLUG", string.Format("Uređaj izvučen: {0} (replug #{1})", remId, _totalReplugEvents));
                            }

                            // 2. Obrada trenutno prikljucenih uredjaja (Replug u isti ili sledeci port)
                            DateTime scanProcessUtc = DateTime.UtcNow;
                            HashSet<string> processedPortKeysThisScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            for (int i = 0; i < foundPorts.Count; i++)
                            {
                                UsbCandidateScan cand = foundPorts[i];
                                if (_loggedScanKeys.Add(cand.Diag))
                                {
                                    HwTestLogger.Info("USB_SCAN", cand.Diag);
                                }

                                // Cooldown posle Unplug-a (< 500 ms): sprecava lazni povratak / #REPLUG- iz WMI kesa
                                DateTime devLastUnplug;
                                if (_lastUnplugUtc.TryGetValue(cand.DeviceId, out devLastUnplug))
                                {
                                    double devCooldownMs = (scanProcessUtc - devLastUnplug).TotalMilliseconds;
                                    if (devCooldownMs < 500.0)
                                    {
                                        continue;
                                    }
                                }
                                if (!cand.IsStrongLocation &&
                                    _lastAnyUnplugUtc != DateTime.MinValue &&
                                    (scanProcessUtc - _lastAnyUnplugUtc).TotalMilliseconds < 500.0)
                                {
                                    continue;
                                }

                                bool newlyAttached = !_currentlyAttachedDevices.ContainsKey(cand.DeviceId);
                                bool hadStrongBefore = false;
                                _attachedDeviceHadStrongLocation.TryGetValue(cand.DeviceId, out hadStrongBefore);

                                if (newlyAttached)
                                {
                                    _currentlyAttachedDevices[cand.DeviceId] = cand.RawPortKey;
                                    _attachedDeviceHadStrongLocation[cand.DeviceId] = cand.IsStrongLocation;
                                }
                                else
                                {
                                    if (!hadStrongBefore && cand.IsStrongLocation)
                                    {
                                        _currentlyAttachedDevices[cand.DeviceId] = cand.RawPortKey;
                                        _attachedDeviceHadStrongLocation[cand.DeviceId] = true;
                                    }
                                    // Apsolutna zabrana dodavanja novog porta ili azuriranja Port #N kad je !newlyAttached
                                    continue;
                                }

                                if (string.IsNullOrEmpty(cand.RawPortKey)) continue;
                                if (hadStrongBefore && !cand.IsStrongLocation) continue;

                                // Deduplikacija multi-node uredjaja unutar istog skena po kanonskom RawPortKey
                                if (!processedPortKeysThisScan.Add(cand.RawPortKey))
                                {
                                    continue;
                                }
                                if (IsPortCurrentlyOccupiedByOtherDevice(cand.RawPortKey, cand.DeviceId))
                                {
                                    continue;
                                }

                                int devReplugs = 0;
                                _deviceReplugCounts.TryGetValue(cand.DeviceId, out devReplugs);
                                int effectiveReplug = Math.Max(devReplugs, _totalReplugEvents);
                                string entryDisplay = FormatPortDisplayLabel(cand.RawPortKey, cand.DisplayLocationInfo, cand.FriendlyName);

                                if (!_detectedPortLocations.Contains(cand.RawPortKey))
                                {
                                    if (_detectedPortLocations.Add(cand.RawPortKey))
                                    {
                                        if (cand.IsStrongLocation && IsDeterministicPhysicalLocation(cand.RawPortKey))
                                        {
                                            _physicalPortLocations.Add(cand.RawPortKey);
                                        }
                                        _pendingSharedPortKey = null;
                                        _pendingSharedDeviceName = null;
                                        AddPortEntry(entryDisplay);
                                        UpdateProgress();
                                        HwTestLogger.Info("USB", string.Format("Registrovan port #{0}: {1} [{2}]", _detectedPortLocations.Count, cand.RawPortKey, cand.FriendlyName));
                                    }
                                }
                                else if (newlyAttached && effectiveReplug > 0)
                                {
                                    if (!cand.IsStrongLocation)
                                    {
                                        // Slab PnP fallback: isti fleš posle izvlacenja u novi port mora dobiti jedinstven replug kljuc
                                        string uniqueReplugKey = string.Format("{0}#REPLUG-{1}", cand.RawPortKey, effectiveReplug);
                                        if (_detectedPortLocations.Add(uniqueReplugKey))
                                        {
                                            _pendingSharedPortKey = null;
                                            _pendingSharedDeviceName = null;
                                            AddPortEntry(FormatPortDisplayLabel(uniqueReplugKey, cand.DisplayLocationInfo, cand.FriendlyName));
                                            UpdateProgress();
                                            HwTestLogger.Info("USB", string.Format("Registrovan port #{0} (replug fallback): {1} [{2}]", _detectedPortLocations.Count, uniqueReplugKey, cand.FriendlyName));
                                        }
                                    }
                                    else
                                    {
                                        // Jaka putanja se poklapa sa prethodnim portom posle izvlacenja (npr. pasivni USB-C / deljeni hub route)
                                        _pendingSharedPortKey = cand.RawPortKey;
                                        _pendingSharedDeviceName = cand.FriendlyName;
                                    }
                                }
                            }

                            UpdateSingleDeviceStatusUi();
                            CheckUsbCChargerPort();
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _scanInProgress, 0);
                        }
                    }));
                }
            });
        }

        private static bool IsDeterministicPhysicalLocation(string rawKey)
        {
            if (string.IsNullOrEmpty(rawKey)) return false;
            string up = rawKey.ToUpperInvariant();
            return (up.Contains("PCIROOT(") || up.Contains("ACPI(")) &&
                   up.Contains("#USB(") &&
                   !up.Contains("#REPLUG-") &&
                   !up.Contains("#PORT_") &&
                   !up.StartsWith(AutoChargerPortKey, StringComparison.OrdinalIgnoreCase);
        }

        private void CheckUsbCChargerPort()
        {
            try
            {
                NativeMethods.SYSTEM_POWER_STATUS ps;
                if (!NativeMethods.GetSystemPowerStatus(out ps)) return;

                bool hasBattery = (ps.BatteryFlag != 128 && ps.BatteryFlag != 255);
                bool batteryCharging = hasBattery && ((ps.BatteryFlag & 8) != 0);
                bool acOnline = (ps.ACLineStatus == 1) || batteryCharging;

                if (!_cachedTypeCCapInitialized)
                {
                    bool detectedTypeC;
                    _maxTypeCChargerCap = DetectTypeCControllerCap(out detectedTypeC);
                    _cachedHasTypeCController = detectedTypeC;
                    _cachedTypeCCapInitialized = true;
                }
                bool hasTypeCController = _cachedHasTypeCController.HasValue && _cachedHasTypeCController.Value;

                if (!_chargerStateInitialized)
                {
                    _chargerStateInitialized = true;
                    _lastAcOnline = acOnline;

                    if (acOnline && hasTypeCController && _chargerPlugCount < _maxTypeCChargerCap)
                    {
                        RegisterChargerPort(true, false);
                    }
                    else if (hasTypeCController)
                    {
                        SetChargerStatusText(
                            HwTestLocalization.Pick("⚡ Punjač nije priključen — ako laptop podržava USB-C punjenje, možete testirati C portove punjačem.",
                                "⚡ 未连接充电器 — 如果笔记本支持 USB-C 充电，您也可以使用充电器测试 Type-C 接口。"),
                            Color.FromRgb(180, 195, 215));
                    }
                    else
                    {
                        SetChargerStatusText(
                            HwTestLocalization.Pick("🔌 Standardno napajanje (nema USB-C PD kontrolera) — ubacite USB fleš disk redom u svaki USB port.",
                                "🔌 标准电源供电（无 USB-C PD 控制器）— 请将 U 盘依次插入每个 USB 接口。"),
                            Color.FromRgb(180, 195, 215));
                    }
                    return;
                }

                // Punjač je upravo izvučen (prelaz 1 -> 0)
                if (_lastAcOnline && !acOnline)
                {
                    _lastAcOnline = false;
                    _waitingForChargerReplug = true;
                    _chargerDisconnectedUtc = DateTime.UtcNow;

                    if (hasTypeCController && _chargerPlugCount < _maxTypeCChargerCap)
                    {
                        SetChargerStatusText(
                            HwTestLocalization.Pick("⚡ Punjač otkačen — priključite USB-C punjač u sledeći C port...",
                                "⚡ 充电器已拔出 — 请将 USB-C 充电器插入下一个 Type-C 接口..."),
                            Color.FromRgb(255, 193, 7));
                        HwTestLogger.Info("USB", string.Format(
                            "Punjač otkačen (ACLine={0}, BatteryFlag=0x{1:X2}) — čekam prebacivanje na drugi C port.",
                            ps.ACLineStatus, ps.BatteryFlag));
                    }
                    return;
                }

                // Punjač je upravo priključen (prelaz 0 -> 1)
                if (!_lastAcOnline && acOnline)
                {
                    _lastAcOnline = true;
                    if (!hasTypeCController) return;

                    if (_chargerPlugCount == 0 && _chargerPlugCount < _maxTypeCChargerCap)
                    {
                        _waitingForChargerReplug = false;
                        RegisterChargerPort(true, false);
                    }
                    else if (_waitingForChargerReplug &&
                             (DateTime.UtcNow - _chargerDisconnectedUtc).TotalMilliseconds >= 250 &&
                             _chargerPlugCount < _maxTypeCChargerCap &&
                             _chargerPlugCount < _targetPortCount)
                    {
                        _waitingForChargerReplug = false;
                        RegisterChargerPort(true, true);
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("USB", "CheckUsbCChargerPort error: " + ex.Message);
            }
        }

        private void RegisterChargerPort(bool hasTypeCController, bool isReplug)
        {
            if (!hasTypeCController) return;
            if (_maxTypeCChargerCap > 0 && _chargerPlugCount >= _maxTypeCChargerCap) return;

            _chargerPlugCount++;
            string portKey = string.Format("{0}#{1}", AutoChargerPortKey, _chargerPlugCount);
            if (_detectedPortLocations.Contains(portKey)) return;

            _detectedPortLocations.Add(portKey);
            string label = isReplug
                ? HwTestLocalization.PickFormat("USB-C / PD Port za punjenje #{0} (Punjač prebačen i ponovo detektovan)", "USB-C / PD 充电接口 #{0}（充电器已换口并重新识别）", _chargerPlugCount)
                : HwTestLocalization.PickFormat("USB-C / PD Port za punjenje #{0} (Punjač priključen — verifikovan)", "USB-C / PD 充电接口 #{0}（充电器已连接 — 已验证）", _chargerPlugCount);

            AddPortEntry(label);
            UpdateProgress();
            HwTestLogger.Info("USB", string.Format(
                "Verifikovan port za punjenje #{0}/{1} (Replug={2}, TypeC={3})",
                _chargerPlugCount, _maxTypeCChargerCap, isReplug, hasTypeCController));

            if (_chargerPlugCount < _maxTypeCChargerCap)
            {
                SetChargerStatusText(
                    HwTestLocalization.PickFormat("✓ USB-C punjač detektovan (#{0}/{1}). Ako imate još USB-C portova za punjenje, izvucite i prebacite punjač na sledeći C port.",
                        "✓ 已检测到 USB-C 充电器 (#{0}/{1})。如还有其他支持充电的 Type-C 接口，请拔出并插入下一个接口。",
                        _chargerPlugCount, _maxTypeCChargerCap),
                    Color.FromRgb(120, 225, 160));
            }
            else
            {
                SetChargerStatusText(
                    HwTestLocalization.PickFormat("✓ USB-C punjač detektovan ({0}/{1} PD slot(a)). Ostale USB portove testirajte USB fleš diskom.",
                        "✓ 已检测到 USB-C 充电器 ({0}/{1} 个 PD 插槽)。请使用 U 盘测试其余 USB 接口。",
                        _chargerPlugCount, _maxTypeCChargerCap),
                    Color.FromRgb(120, 225, 160));
            }
        }

        private void SetChargerStatusText(string text, Color color)
        {
            if (_chargerStatusText != null)
            {
                _chargerStatusText.Text = text;
                _chargerStatusText.Foreground = new SolidColorBrush(color);
            }
        }

        private int DetectTypeCControllerCap(out bool hasTypeCController)
        {
            hasTypeCController = false;
            HashSet<string> pnpControllers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%UCSI%' OR Name LIKE '%Type-C%' OR Name LIKE '%Type C%' OR Name LIKE '%USB-C%' OR Name LIKE '%Power Delivery%' OR Name LIKE '%Thunderbolt%'"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        string name = (obj["Name"] ?? "").ToString().Trim();
                        string devId = (obj["DeviceID"] ?? "").ToString().Trim();
                        string upName = name.ToUpperInvariant();
                        if (upName.Contains("NETWORKING") || upName.Contains("BRIDGE") ||
                            upName.Contains("DEVICE ROUTER") || upName.Contains("P2P") || upName.Contains("AUDIO"))
                        {
                            continue;
                        }
                        string token = !string.IsNullOrEmpty(devId) ? devId : name;
                        if (!string.IsNullOrEmpty(token))
                        {
                            pnpControllers.Add(token);
                        }
                    }
                }
            }
            catch { }

            hasTypeCController = (pnpControllers.Count > 0);
            if (!hasTypeCController) return 0;

            int smbiosTypeCPorts = 0;
            HashSet<string> seenDesignators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher ps = new ManagementObjectSearcher(
                    "SELECT ExternalReferenceDesignator FROM Win32_PortConnector"))
                {
                    foreach (ManagementObject pObj in ps.Get())
                    {
                        string ext = (pObj["ExternalReferenceDesignator"] ?? "").ToString().Trim().ToUpperInvariant();
                        if (string.IsNullOrEmpty(ext) || ext == "NONE" || ext == "NOT SPECIFIED") continue;
                        if (ext.Contains("INTERNAL") || ext.Contains("DOCK") || ext.Contains("ONELINK")) continue;
                        if (ext.Contains("TYPE-C") || ext.Contains("TYPE C") || ext.Contains("USB-C") || ext.Contains("THUNDERBOLT"))
                        {
                            if (seenDesignators.Add(ext))
                            {
                                smbiosTypeCPorts++;
                            }
                        }
                    }
                }
            }
            catch { }

            if (smbiosTypeCPorts > 0)
            {
                return Math.Min(smbiosTypeCPorts, 4);
            }
            return Math.Max(1, Math.Min(pnpControllers.Count, 2));
        }

        private bool HasUsbTypeCController()
        {
            bool hasCtrl;
            DetectTypeCControllerCap(out hasCtrl);
            return hasCtrl;
        }

        private string QueryUsbLocation(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return null;
            try
            {
                string escaped = pnpDeviceId.Replace("\\", "\\\\");
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    string.Format("SELECT DeviceID, LocationPaths FROM Win32_PnPEntity WHERE DeviceID = '{0}'", escaped)))
                {
                    foreach (ManagementObject entity in s.Get())
                    {
                        string[] locs = entity["LocationPaths"] as string[];
                        if (locs != null && locs.Length > 0)
                            return locs[0];
                    }
                }
            }
            catch { }
            return null;
        }

        private void AddPortEntry(string portKey)
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 40, 167, 69)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            TextBlock tb = new TextBlock
            {
                Text = HwTestLocalization.PickFormat("✓ Port #{0} uspešno prepoznan: {1}", "✓ 接口 #{0} 已成功识别: {1}", _detectedPortLocations.Count, portKey),
                Foreground = Brushes.White,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            b.Child = tb;
            _portListPanel.Children.Add(b);
        }

        private void UpdateProgress()
        {
            _progressBar.Maximum = _targetPortCount;
            _progressBar.Value = _detectedPortLocations.Count;
            _progressText.Text = HwTestLocalization.PickFormat("Detektovano: {0} / {1} porta", "已检测到: {0} / {1} 个接口", _detectedPortLocations.Count, _targetPortCount);

            if (_detectedPortLocations.Count >= _targetPortCount)
            {
                _progressText.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _btnPass.IsEnabled = true;
                if (_btnAdjustTargetToDetected != null)
                    _btnAdjustTargetToDetected.Visibility = Visibility.Collapsed;
            }
            else
            {
                _progressText.Foreground = Brushes.White;
                _btnPass.IsEnabled = false;
                if (_btnAdjustTargetToDetected != null)
                {
                    if (_detectedPortLocations.Count >= 1)
                    {
                        _btnAdjustTargetToDetected.Content = HwTestLocalization.PickFormat(
                            "🎯 Uređaj ima samo {0} USB porta? Podesi cilj na {0} (umesto {1}) i omogući PASS",
                            "🎯 设备只有 {0} 个 USB 接口？将目标调整为 {0}（原为 {1}）并允许 PASS",
                            _detectedPortLocations.Count, _targetPortCount);
                        _btnAdjustTargetToDetected.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _btnAdjustTargetToDetected.Visibility = Visibility.Collapsed;
                    }
                }
            }

            if (_baselineCaptured && _usbDeviceStatusText != null)
            {
                UpdateSingleDeviceStatusUi();
            }
        }

        private static bool IsClearlyExternalPreAttachedUsbDevice(string pnp, string name, string svc, string pnpClass)
        {
            string uName = (name ?? "").ToUpperInvariant();
            string uSvc = (svc ?? "").ToUpperInvariant();
            string uClass = (pnpClass ?? "").ToUpperInvariant();

            // Nikada ne tretirati interne Wi-Fi / WWAN / BT / virtuelne adaptere kao eksterni USB port
            if (uName.Contains("BLUETOOTH") || uName.Contains("WI-FI") || uName.Contains("WIFI") ||
                uName.Contains("WLAN") || uName.Contains("WWAN") || uName.Contains("WIRELESS") ||
                uName.Contains("802.11") || uName.Contains("CELLULAR") || uName.Contains("MOBILE BROADBAND") ||
                uName.Contains("VIRTUAL"))
            {
                return false;
            }

            // 1. USB mrežni adapter (USB-to-LAN / Ethernet / ASIX / Realtek USB GbE)
            if (uClass == "NET" ||
                uSvc.Contains("AX88179") || uSvc.Contains("AX88772") || uSvc.Contains("RTUX") ||
                uSvc.Contains("RTU") || uSvc.Contains("RTL815") || uSvc.Contains("USBNET") ||
                uSvc.Contains("RNDIS") || uSvc.Contains("CDC_NCM") || uSvc.Contains("ASIX") ||
                uName.Contains("ETHERNET") || uName.Contains("GIGABIT") || uName.Contains("USB TO LAN") ||
                uName.Contains("LAN ADAPTER") || uName.Contains("RJ45") || uName.Contains("ASIX") ||
                uName.Contains("REALTEK USB GBE") || uName.Contains("REALTEK USB FE"))
            {
                return true;
            }

            // 2. Eksterni USB audio uređaj (slušalice / USB zvučna karta / DAC)
            if (uSvc == "USBAUDIO" ||
                (uClass == "MEDIA" && (uName.Contains("USB AUDIO") || uName.Contains("HEADSET") || uName.Contains("DAC"))))
            {
                return true;
            }

            // 3. Eksterni USB serijski adapter / štampač / skener
            if (uSvc == "USBSER" || uSvc == "FTDIBUS" || uSvc == "CH341SER" ||
                uSvc == "SILABSER" || uSvc == "PL2303" || uSvc == "USBPRINT")
            {
                return true;
            }

            return false;
        }

        private void SafeCloseDialog(bool result)
        {
            try { DialogResult = result; } catch { }
            try { Close(); } catch { }
        }

        private void FinishPass()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;

            int reportedPortCount = _userManuallyAdjustedTarget
                ? _targetPortCount
                : Math.Max(_targetPortCount, _detectedPortLocations.Count);
            string overrideBy = _userManuallyAdjustedTarget ? "serviser" : "serviser_autolearn";

            // Ažuriraj profil modela na ServisHub-u (auto-learn ili ručno podešen cilj)
            if (_profileClient != null && reportedPortCount > 0)
            {
                _profileClient.PostUsbOverride(
                    _modelName,
                    reportedPortCount,
                    _targetPortCount,
                    _detectedPortLocations.Count,
                    new List<string>(_physicalPortLocations),
                    _chargerPlugCount,
                    _maxTypeCChargerCap,
                    _userManuallyAdjustedTarget,
                    _serialNumber,
                    overrideBy);
            }

            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["target_ports"] = _targetPortCount;
            Result.Details["detected_ports"] = _detectedPortLocations.Count;
            Result.Details["port_locations"] = new List<string>(_detectedPortLocations);
            Result.Details["physical_port_locations"] = new List<string>(_physicalPortLocations);
            Result.Details["charger_ports_count"] = _chargerPlugCount;
            Result.Details["max_typec_cap"] = _maxTypeCChargerCap;
            Result.Details["manual_override"] = _userManuallyAdjustedTarget;

            SafeCloseDialog(true);
        }

        private void FinishFail()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;

            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = HwTestLocalization.PickFormat("Neispravan USB port: detektovano {0}/{1} portova", "USB 接口故障: 仅检测到 {0}/{1} 个接口", _detectedPortLocations.Count, _targetPortCount);
            Result.Details["target_ports"] = _targetPortCount;
            Result.Details["detected_ports"] = _detectedPortLocations.Count;
            Result.Details["port_locations"] = new List<string>(_detectedPortLocations);
            Result.Details["physical_port_locations"] = new List<string>(_physicalPortLocations);
            Result.Details["charger_ports_count"] = _chargerPlugCount;
            Result.Details["max_typec_cap"] = _maxTypeCChargerCap;
            Result.Details["manual_override"] = _userManuallyAdjustedTarget;

            SafeCloseDialog(false);
        }
    }
}
