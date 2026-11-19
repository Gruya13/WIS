using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class UsbPortTestWindow : Window
    {
        private readonly ModelProfileClient _profileClient;
        private readonly string _modelName;
        private int _targetPortCount = 3;
        private readonly HashSet<string> _detectedPortLocations;

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
        private int _scanInProgress = 0;
        private readonly HashSet<string> _loggedScanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _baselineUsbPnpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _currentlyAttachedDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _deviceReplugCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool _baselineCaptured = false;
        private int _totalReplugEvents = 0;
        private string _pendingSharedPortKey = null;
        private string _pendingSharedDeviceName = null;

        private bool _chargerStateInitialized = false;
        private bool _lastAcOnline = false;
        private bool _waitingForChargerReplug = false;
        private DateTime _chargerDisconnectedUtc = DateTime.MinValue;
        private int _chargerPlugCount = 0;
        private bool? _cachedHasTypeCController = null;
        private bool _isClosingOrDone = false;

        public TestItemResult Result { get; private set; }

        public UsbPortTestWindow(string modelName, int? prefilledCount, ModelProfileClient profileClient)
        {
            _modelName = modelName ?? "Generic";
            _profileClient = profileClient;
            if (prefilledCount.HasValue && prefilledCount.Value > 0)
                _targetPortCount = prefilledCount.Value;

            Result = new TestItemResult("usb", "USB portovi", "Ispitivanje svih fizičkih USB portova uzastopnim ubacivanjem fleš diska");
            _detectedPortLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Title = "WIS — Test USB Portova";
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
                Text = "🔌 TEST USB PORTOVA",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(title);

            TextBlock sub = new TextBlock
            {
                Text = "Ubacite bilo koji USB fleš disk redom u svaki USB port na računaru (sačekajte detekciju pre prelaska na sledeći)",
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
                Text = "Ukupan broj USB portova na kućištu:",
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
                Text = "Detektovano: 0 / 3 porta",
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
                Text = "⚡ Status punjača: proveravam USB-C / PD napajanje...",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 215)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            spC.Children.Add(_chargerStatusText);

            _usbDeviceStatusText = new TextBlock
            {
                Text = "🔌 Ubacite USB fleš ili bilo koji USB uređaj u prvi port (isti uređaj testira sve portove).",
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
                Content = "➕ Priznaj priključeni uređaj kao novi port",
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
                Content = "🎯 Uređaj fizički ima manje portova? Podesi cilj na detektovane",
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
            StackPanel spF = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _btnPass = new Button
            {
                Content = "✓ Potvrdi (PASS)",
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
                Content = "✗ Neispravan USB port (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(210, 50, 62)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 15, 0)
            };
            btnUsbFail.Click += (s, e) => FinishFail();
            spF.Children.Add(btnUsbFail);

            Button btnSkip = new Button
            {
                Content = "Preskoči test USB-a",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            spF.Children.Add(btnSkip);

            footer.Child = spF;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

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

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _pollTimer.Tick += (s, args) => TriggerUsbScan();
            _pollTimer.Start();

            TriggerUsbScan();
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
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

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == NativeMethods.DBT_DEVICEARRIVAL ||
                    eventType == NativeMethods.DBT_DEVICEREMOVECOMPLETE ||
                    eventType == NativeMethods.DBT_DEVNODES_CHANGED)
                {
                    TriggerUsbScan();
                }
            }
            else if (msg == NativeMethods.WM_POWERBROADCAST)
            {
                CheckUsbCChargerPort();
                TriggerUsbScan();
            }
            return IntPtr.Zero;
        }

        private const string AutoChargerPortKey = "AUTO-USBC-CHARGER";

        private class UsbCandidateScan
        {
            public string DeviceId;
            public string FriendlyName;
            public string RawPortKey;
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
                string devLabel = string.IsNullOrEmpty(_pendingSharedDeviceName) ? "USB uređaj" : _pendingSharedDeviceName;
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                _usbDeviceStatusText.Text = string.Format(
                    "⚠️ '{0}' je ponovo uboden, ali kontroler prijavljuje istu putanju kao prethodni port. Ako je ovo drugi port, kliknite dugme ispod:",
                    devLabel);
                if (_btnConfirmSharedPort != null) _btnConfirmSharedPort.Visibility = Visibility.Visible;
                return;
            }

            if (_btnConfirmSharedPort != null) _btnConfirmSharedPort.Visibility = Visibility.Collapsed;

            if (_currentlyAttachedDevices.Count > 0)
            {
                if (_detectedPortLocations.Count >= _targetPortCount)
                {
                    _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                    _usbDeviceStatusText.Text = string.Format("✅ Svi ciljani USB portovi ({0}/{1}) su uspešno detektovani!", _detectedPortLocations.Count, _targetPortCount);
                }
                else
                {
                    _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(165, 215, 255));
                    _usbDeviceStatusText.Text = "🔄 Uređaj je u portu — sada ga IZVUCITE i ubacite u SLEDEĆI USB port!";
                }
            }
            else if (_totalReplugEvents > 0)
            {
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                _usbDeviceStatusText.Text = "✅ Uređaj izvučen — spremno! Ubacite ga u sledeći USB port...";
            }
            else
            {
                _usbDeviceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 215));
                _usbDeviceStatusText.Text = "🔌 Ubacite USB fleš ili bilo koji USB uređaj u prvi port (isti uređaj testira sve portove).";
            }
        }

        private void TriggerUsbScan()
        {
            if (Interlocked.CompareExchange(ref _scanInProgress, 1, 0) != 0)
            {
                return;
            }

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
                        string dbg;
                        string cfgLoc = NativeMethods.ResolveUsbPhysicalPortLocation(pnp, out dbg);
                        string wmiLoc = string.IsNullOrEmpty(cfgLoc) ? QueryUsbLocation(pnp) : null;
                        bool isStrong = !string.IsNullOrEmpty(cfgLoc) || !string.IsNullOrEmpty(wmiLoc);
                        string rawLoc = !string.IsNullOrEmpty(cfgLoc)
                            ? cfgLoc
                            : (!string.IsNullOrEmpty(wmiLoc) ? wmiLoc : pnp);

                        string diag = string.Format("name='{0}' | pnp='{1}' | {2} | wmi='{3}' -> rawLoc='{4}' (strong={5})",
                            friendly, pnp, dbg, wmiLoc ?? "", rawLoc, isStrong);

                        foundPorts.Add(new UsbCandidateScan
                        {
                            DeviceId = pnp,
                            FriendlyName = friendly,
                            RawPortKey = rawLoc,
                            IsStrongLocation = isStrong,
                            Diag = diag
                        });
                    }
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        try
                        {
                            if (isFirstBaselinePass)
                            {
                                lock (_baselineUsbPnpIds)
                                {
                                    for (int i = 0; i < newBaselineIds.Count; i++)
                                        _baselineUsbPnpIds.Add(newBaselineIds[i]);
                                }
                                _baselineCaptured = true;
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
                            for (int i = 0; i < removedIds.Count; i++)
                            {
                                string remId = removedIds[i];
                                _currentlyAttachedDevices.Remove(remId);
                                int prevCnt = 0;
                                _deviceReplugCounts.TryGetValue(remId, out prevCnt);
                                _deviceReplugCounts[remId] = prevCnt + 1;
                                _totalReplugEvents++;
                                _pendingSharedPortKey = null;
                                _pendingSharedDeviceName = null;
                                HwTestLogger.Info("USB_UNPLUG", string.Format("Uređaj izvučen: {0} (replug #{1})", remId, _totalReplugEvents));
                            }

                            // 2. Obrada trenutno prikljucenih uredjaja (Replug u isti ili sledeci port)
                            for (int i = 0; i < foundPorts.Count; i++)
                            {
                                UsbCandidateScan cand = foundPorts[i];
                                if (_loggedScanKeys.Add(cand.Diag))
                                {
                                    HwTestLogger.Info("USB_SCAN", cand.Diag);
                                }

                                bool newlyAttached = !_currentlyAttachedDevices.ContainsKey(cand.DeviceId);
                                _currentlyAttachedDevices[cand.DeviceId] = cand.RawPortKey;

                                if (string.IsNullOrEmpty(cand.RawPortKey)) continue;

                                int devReplugs = 0;
                                _deviceReplugCounts.TryGetValue(cand.DeviceId, out devReplugs);
                                int effectiveReplug = Math.Max(devReplugs, _totalReplugEvents);

                                if (!_detectedPortLocations.Contains(cand.RawPortKey))
                                {
                                    if (_detectedPortLocations.Add(cand.RawPortKey))
                                    {
                                        _pendingSharedPortKey = null;
                                        _pendingSharedDeviceName = null;
                                        AddPortEntry(string.Format("{0} ({1})", cand.RawPortKey, cand.FriendlyName));
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
                                            AddPortEntry(string.Format("{0} ({1})", uniqueReplugKey, cand.FriendlyName));
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

        private void CheckUsbCChargerPort()
        {
            try
            {
                NativeMethods.SYSTEM_POWER_STATUS ps;
                if (!NativeMethods.GetSystemPowerStatus(out ps)) return;

                bool hasBattery = (ps.BatteryFlag != 128 && ps.BatteryFlag != 255);
                bool batteryCharging = hasBattery && ((ps.BatteryFlag & 8) != 0);
                bool acOnline = (ps.ACLineStatus == 1) || batteryCharging;

                if (!_cachedHasTypeCController.HasValue)
                    _cachedHasTypeCController = HasUsbTypeCController();
                bool hasTypeCController = _cachedHasTypeCController.Value;

                if (!_chargerStateInitialized)
                {
                    _chargerStateInitialized = true;
                    _lastAcOnline = acOnline;

                    if (acOnline && (hasBattery || hasTypeCController))
                    {
                        RegisterChargerPort(hasTypeCController, false);
                    }
                    else if (hasTypeCController)
                    {
                        SetChargerStatusText(
                            "⚡ Punjač nije priključen — ako laptop podržava USB-C punjenje, možete testirati C portove punjačem.",
                            Color.FromRgb(180, 195, 215));
                    }
                    else
                    {
                        SetChargerStatusText(
                            "🔌 Ubacite USB fleš disk redom u svaki USB port.",
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

                    if (hasTypeCController)
                    {
                        SetChargerStatusText(
                            "⚡ Punjač otkačen — priključite USB-C punjač u sledeći C port...",
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
                    if (!(hasBattery || hasTypeCController)) return;

                    if (_chargerPlugCount == 0)
                    {
                        _waitingForChargerReplug = false;
                        RegisterChargerPort(hasTypeCController, false);
                    }
                    else if (_waitingForChargerReplug && hasTypeCController &&
                             (DateTime.UtcNow - _chargerDisconnectedUtc).TotalMilliseconds >= 250 &&
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
            _chargerPlugCount++;
            string portKey = string.Format("{0}#{1}", AutoChargerPortKey, _chargerPlugCount);
            if (_detectedPortLocations.Contains(portKey)) return;

            _detectedPortLocations.Add(portKey);
            string label;
            if (hasTypeCController)
            {
                label = isReplug
                    ? string.Format("USB-C / PD Port za punjenje #{0} (Punjač prebačen i ponovo detektovan)", _chargerPlugCount)
                    : string.Format("USB-C / PD Port za punjenje #{0} (Punjač priključen — verifikovan)", _chargerPlugCount);
            }
            else
            {
                label = string.Format("USB / Port za punjenje #{0} (Punjač priključen — automatski verifikovan)", _chargerPlugCount);
            }

            AddPortEntry(label);
            UpdateProgress();
            HwTestLogger.Info("USB", string.Format(
                "Verifikovan port za punjenje #{0} (Replug={1}, TypeC={2})",
                _chargerPlugCount, isReplug, hasTypeCController));

            if (hasTypeCController)
            {
                SetChargerStatusText(
                    string.Format("✓ USB-C punjač detektovan (#{0}). Ako imate još USB-C portova za punjenje, izvucite i prebacite punjač na sledeći C port.", _chargerPlugCount),
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

        private bool HasUsbTypeCController()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%UCSI%' OR Name LIKE '%Type-C%' OR Name LIKE '%Type C%' OR Name LIKE '%USB-C%' OR Name LIKE '%Power Delivery%' OR Name LIKE '%Thunderbolt%'"))
                {
                    foreach (ManagementObject obj in s.Get())
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
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
                Text = string.Format("✓ Port #{0} uspešno prepoznan: {1}", _detectedPortLocations.Count, portKey),
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
            _progressText.Text = string.Format("Detektovano: {0} / {1} porta", _detectedPortLocations.Count, _targetPortCount);

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
                        _btnAdjustTargetToDetected.Content = string.Format(
                            "🎯 Uređaj ima samo {0} USB porta? Podesi cilj na {0} (umesto {1}) i omogući PASS",
                            _detectedPortLocations.Count, _targetPortCount);
                        _btnAdjustTargetToDetected.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _btnAdjustTargetToDetected.Visibility = Visibility.Collapsed;
                    }
                }
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

            // Ažuriraj broj portova na ServisHub-u ako je promenjen ili definisan
            if (_profileClient != null && _targetPortCount > 0)
            {
                _profileClient.PostUsbOverride(_modelName, _targetPortCount);
            }

            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["target_ports"] = _targetPortCount;
            Result.Details["detected_ports"] = _detectedPortLocations.Count;
            Result.Details["port_locations"] = new List<string>(_detectedPortLocations);

            SafeCloseDialog(true);
        }

        private void FinishFail()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;

            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = string.Format("Neispravan USB port: detektovano {0}/{1} portova", _detectedPortLocations.Count, _targetPortCount);
            Result.Details["target_ports"] = _targetPortCount;
            Result.Details["detected_ports"] = _detectedPortLocations.Count;
            Result.Details["port_locations"] = new List<string>(_detectedPortLocations);

            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test USB portova";
            SafeCloseDialog(false);
        }
    }
}
