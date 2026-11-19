using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class FingerprintTestWindow : Window
    {
        private readonly string _sensorName;
        private readonly uint _initialUnitId;
        private readonly HardwareProbeResult _hwInfo;

        private readonly Border _sensorBadge;
        private readonly TextBlock _sensorIconText;
        private readonly TextBlock _statusText;
        private readonly TextBlock _detailText;
        private readonly Button _btnPass;
        private readonly Button _btnFail;
        private readonly Button _btnRetry;
        private readonly Button _btnSwitchMode;

        private readonly object _sessionLock = new object();
        private uint _sessionHandle = 0;
        private int _startMode = 0; // 0..4 automatska rotacija kroz System/Private Locate, Identify, Capture
        private volatile bool _stopRequested = false;
        private volatile bool _timedOutForSwitch = false;
        private volatile bool _captureSucceeded = false;
        private volatile bool _touchDetected = false;
        private string _captureMethod = "";
        private uint _verifiedUnitId = 0;
        private uint _sampleSizeBytes = 0;
        private DispatcherTimer _autoPassTimer;
        private bool _isClosingOrDone = false;

        public TestItemResult Result { get; private set; }

        public FingerprintTestWindow(HardwareProbeResult hwInfo)
            : this(hwInfo != null ? hwInfo.FingerprintName : "", hwInfo != null ? hwInfo.FingerprintUnitId : 0)
        {
            _hwInfo = hwInfo;
        }

        public FingerprintTestWindow(string sensorName, uint unitId)
        {
            _sensorName = string.IsNullOrEmpty(sensorName) ? "Biometrijski čitač otiska prsta" : sensorName;
            _initialUnitId = unitId;

            Result = new TestItemResult("fingerprint", "Čitač otiska prsta", "Provera odziva biometrijskog senzora otiska prsta");

            Title = "WIS — Test Čitača Otiska Prsta";
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
            StackPanel spH = new StackPanel();
            spH.Children.Add(new TextBlock
            {
                Text = "🖐️ TEST ČITAČA OTISKA PRSTA (FINGERPRINT)",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            });
            spH.Children.Add(new TextBlock
            {
                Text = string.Format("Senzor: {0}{1}",
                    _sensorName,
                    _initialUnitId > 0 ? string.Format(" (WBF Unit ID: {0})", _initialUnitId) : ""),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            header.Child = spH;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Center
            Border centerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(40),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 780
            };

            StackPanel spC = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            _sensorBadge = new Border
            {
                Width = 170,
                Height = 170,
                CornerRadius = new CornerRadius(85),
                Background = new SolidColorBrush(Color.FromRgb(38, 44, 58)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                BorderThickness = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24)
            };
            _sensorIconText = new TextBlock
            {
                Text = "👆",
                FontSize = 68,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _sensorBadge.Child = _sensorIconText;
            spC.Children.Add(_sensorBadge);

            _statusText = new TextBlock
            {
                Text = "Prislonite bilo koji prst na čitač otiska prsta...",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            spC.Children.Add(_statusText);

            _detailText = new TextBlock
            {
                Text = "Povezivanje sa Windows Biometric Framework (WinBio) servisom...",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 185)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 28)
            };
            spC.Children.Add(_detailText);

            WrapPanel spBtns = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _btnPass = new Button
            {
                Content = "✓ Potvrdi (PASS)",
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 10, 20, 10),
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = false
            };
            _btnPass.Click += (s, e) => FinishPass();
            spBtns.Children.Add(_btnPass);

            _btnFail = new Button
            {
                Content = "✗ Senzor ne radi (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 12, 0)
            };
            _btnFail.Click += (s, e) => FinishFail("Tehničar označio da čitač otiska prsta ne reaguje / ne radi");
            spBtns.Children.Add(_btnFail);

            _btnRetry = new Button
            {
                Content = "↻ Testiraj ponovo",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 12, 0)
            };
            _btnRetry.Click += (s, e) => RestartListener();
            spBtns.Children.Add(_btnRetry);

            _btnSwitchMode = new Button
            {
                Content = "🔄 Režim: LocateSensor",
                Background = new SolidColorBrush(Color.FromRgb(75, 82, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(14, 10, 14, 10)
            };
            _btnSwitchMode.Click += (s, e) =>
            {
                _startMode = (_startMode + 1) % 5;
                RestartListener();
            };
            spBtns.Children.Add(_btnSwitchMode);

            spC.Children.Add(spBtns);
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
            Button btnSkip = new Button
            {
                Content = "Preskoči test otiska prsta",
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

            Loaded += (s, e) =>
            {
                ForceWindowFocus();
                StartListener();
            };
            ContentRendered += (s, e) => ForceWindowFocus();
            Closed += (s, e) => StopListener();
        }

        private void ForceWindowFocus()
        {
            try
            {
                Topmost = true;
                Activate();
                Focus();
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
            catch { }
        }

        private string GetModeDisplayName(int mode)
        {
            switch (mode % 5)
            {
                case 0: return "System LocateSensor";
                case 1: return "Private LocateSensor";
                case 2: return "System Identify";
                case 3: return "Private CaptureSample";
                default: return "System CaptureSample";
            }
        }

        private void RestartListener()
        {
            StopAutoPassTimer();
            StopListener();
            ForceWindowFocus();

            _captureSucceeded = false;
            _touchDetected = false;
            _btnPass.IsEnabled = false;
            _sensorBadge.Background = new SolidColorBrush(Color.FromRgb(38, 44, 58));
            _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            _sensorIconText.Text = "👆";
            _statusText.Foreground = Brushes.White;
            _statusText.Text = "Prislonite bilo koji prst na čitač otiska prsta...";
            _detailText.Text = string.Format("Pokrećem WinBio sesiju ({0})...", GetModeDisplayName(_startMode));
            _btnSwitchMode.Content = string.Format("🔄 Režim: {0}", GetModeDisplayName(_startMode));

            StartListener();
        }

        private void StartListener()
        {
            _stopRequested = false;
            int modeSnapshot = _startMode;
            Thread t = new Thread(() => ListenLoop(modeSnapshot));
            t.IsBackground = true;
            t.Start();
        }

        private void StopListener()
        {
            _stopRequested = true;
            StopAutoPassTimer();
            lock (_sessionLock)
            {
                if (_sessionHandle != 0)
                {
                    try { NativeMethods.WinBioCancel(_sessionHandle); } catch { }
                    try { NativeMethods.WinBioCloseSession(_sessionHandle); } catch { }
                    _sessionHandle = 0;
                }
            }
            try { NativeMethods.WinBioReleaseFocus(); } catch { }
        }

        private void CloseActiveSession()
        {
            lock (_sessionLock)
            {
                if (_sessionHandle != 0)
                {
                    try { NativeMethods.WinBioCloseSession(_sessionHandle); } catch { }
                    _sessionHandle = 0;
                }
            }
        }

        private static bool EnsureWbioServiceRunning()
        {
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT Name, State FROM Win32_Service WHERE Name = 'WbioSrvc'"))
                using (var col = s.Get())
                {
                    foreach (System.Management.ManagementObject mo in col)
                    {
                        string st = mo["State"] != null ? mo["State"].ToString() : "";
                        if (st.Equals("Running", StringComparison.OrdinalIgnoreCase))
                            return true;
                        object res = mo.InvokeMethod("StartService", null);
                        HwTestLogger.Info("FINGERPRINT", "Pokrenut WbioSrvc servis (StartService=" + (res != null ? res.ToString() : "null") + ")");
                        Thread.Sleep(750);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("FINGERPRINT", "WbioSrvc provera/start upozorenje: " + ex.Message);
            }
            return false;
        }

        private int OpenSystemSessionSafe(uint flags, out uint sessionHandle, out string dbUsed)
        {
            sessionHandle = 0;
            // WINBIO_DB_DEFAULT = 1, WINBIO_DB_ONCHIP = 3, WINBIO_DB_BOOTSTRAP = 2, NULL = 0
            int[] dbs = new int[] { 1, 3, 2, 0 };
            string[] dbNames = new string[] { "WINBIO_DB_DEFAULT", "WINBIO_DB_ONCHIP", "WINBIO_DB_BOOTSTRAP", "NULL" };
            int lastHr = unchecked((int)0x80004005);
            dbUsed = "WINBIO_DB_DEFAULT";

            for (int i = 0; i < dbs.Length; i++)
            {
                dbUsed = dbNames[i];
                IntPtr dbPtr = (dbs[i] == 0) ? IntPtr.Zero : new IntPtr(dbs[i]);
                lastHr = NativeMethods.WinBioOpenSession(
                    NativeMethods.WINBIO_TYPE_FINGERPRINT,
                    NativeMethods.WINBIO_POOL_SYSTEM,
                    flags,
                    IntPtr.Zero,
                    UIntPtr.Zero,
                    dbPtr,
                    out sessionHandle);
                if (lastHr == 0 && sessionHandle != 0)
                    return 0;
            }
            return lastHr;
        }

        private int OpenPrivateSessionSafe(uint flags, uint[] unitIds, out uint sessionHandle)
        {
            sessionHandle = 0;
            if (unitIds == null || unitIds.Length == 0)
                return unchecked((int)0x80070057);

            IntPtr unitPtr = Marshal.AllocHGlobal(unitIds.Length * 4);
            try
            {
                for (int i = 0; i < unitIds.Length; i++)
                    Marshal.WriteInt32(unitPtr, i * 4, unchecked((int)unitIds[i]));

                int hr = NativeMethods.WinBioOpenSession(
                    NativeMethods.WINBIO_TYPE_FINGERPRINT,
                    NativeMethods.WINBIO_POOL_PRIVATE,
                    flags,
                    unitPtr,
                    new UIntPtr((uint)unitIds.Length),
                    IntPtr.Zero,
                    out sessionHandle);

                if ((hr != 0 || sessionHandle == 0) && flags == NativeMethods.WINBIO_FLAG_BASIC)
                {
                    hr = NativeMethods.WinBioOpenSession(
                        NativeMethods.WINBIO_TYPE_FINGERPRINT,
                        NativeMethods.WINBIO_POOL_PRIVATE,
                        NativeMethods.WINBIO_FLAG_DEFAULT,
                        unitPtr,
                        new UIntPtr((uint)unitIds.Length),
                        IntPtr.Zero,
                        out sessionHandle);
                }
                return hr;
            }
            finally
            {
                Marshal.FreeHGlobal(unitPtr);
            }
        }

        private void CancelSessionOnTimeout(object state)
        {
            _timedOutForSwitch = true;
            lock (_sessionLock)
            {
                if (_sessionHandle != 0)
                {
                    try { NativeMethods.WinBioCancel(_sessionHandle); } catch { }
                }
            }
        }

        private void ListenLoop(int initialMode)
        {
            System.Threading.Timer watchdog = null;
            try
            {
                // 0. Osiguraj da je Windows Biometric Service pokrenut
                EnsureWbioServiceRunning();

                // 1. Provera aktivnih WBF jedinica
                IntPtr unitArr = IntPtr.Zero;
                UIntPtr unitCount = UIntPtr.Zero;
                int hrEnum = NativeMethods.WinBioEnumBiometricUnits(NativeMethods.WINBIO_TYPE_FINGERPRINT, out unitArr, out unitCount);
                int count = (hrEnum == 0) ? (int)unitCount : 0;
                string unitDesc = _sensorName;
                string schemaDiag = "";
                var allUnitIds = new System.Collections.Generic.List<uint>();

                if (unitArr != IntPtr.Zero)
                {
                    try
                    {
                        int elemSize = Marshal.SizeOf(typeof(NativeMethods.WINBIO_UNIT_SCHEMA));
                        for (int u = 0; u < count; u++)
                        {
                            IntPtr elemPtr = new IntPtr(unitArr.ToInt64() + ((long)u * elemSize));
                            var schema = (NativeMethods.WINBIO_UNIT_SCHEMA)Marshal.PtrToStructure(
                                elemPtr, typeof(NativeMethods.WINBIO_UNIT_SCHEMA));
                            allUnitIds.Add(schema.UnitId);
                            if (u == 0)
                            {
                                if (!string.IsNullOrEmpty(schema.Description))
                                    unitDesc = schema.Description.Trim();
                                schemaDiag = string.Format(
                                    "UnitId={0} Pool={1} SubType={2} Caps=0x{3:X} Model='{4}' Dev='{5}'",
                                    schema.UnitId, schema.PoolType, schema.SensorSubType, schema.Capabilities,
                                    (schema.Model ?? "").Trim(), (schema.DeviceInstanceId ?? "").Trim());
                            }
                            else
                            {
                                schemaDiag += string.Format(" | ExtraUnit={0}('{1}')", schema.UnitId, (schema.Description ?? "").Trim());
                            }
                        }
                    }
                    finally
                    {
                        try { NativeMethods.WinBioFree(unitArr); } catch { }
                    }
                }

                HwTestLogger.Info("FINGERPRINT", string.Format("WinBioEnumBiometricUnits hr=0x{0:X8} count={1} desc='{2}' {3}",
                    hrEnum, count, unitDesc, schemaDiag));

                if (count == 0)
                {
                    if (_hwInfo != null && _hwInfo.FingerprintDisabledInBios)
                    {
                        HwTestLogger.Warn("FINGERPRINT_BIOS", "Uređaj je onemogućen u BIOS-u: " + _hwInfo.FingerprintBiosStatus);
                        Dispatcher.BeginInvoke(new Action(() => {
                            _sensorBadge.Background = new SolidColorBrush(Color.FromArgb(70, 220, 53, 69));
                            _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                            _sensorIconText.Text = "🔒";
                            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                            _statusText.Text = "UREĐAJ JE ONEMOGUĆEN U PODEŠAVANJIMA BIOS-a!";
                            _detailText.Text = string.Format(
                                "BIOS postavka: {0}.\nUđite u BIOS (HP: F10, Lenovo: F1, Dell: F2), omogućite čitač otiska prsta (Enable) i sačuvajte podešavanja.",
                                _hwInfo.FingerprintBiosStatus);
                        }));
                        return;
                    }

                    string extraDiag = "";
                    if (_hwInfo != null)
                    {
                        if (!string.IsNullOrEmpty(_hwInfo.FingerprintBiosStatus))
                            extraDiag += " | BIOS: " + _hwInfo.FingerprintBiosStatus;
                        if (_hwInfo.FactoryFingerprint == true && !string.IsNullOrEmpty(_hwInfo.FactoryFingerprintEvidence))
                            extraDiag += " | Fabrički OEM BOM: " + _hwInfo.FactoryFingerprintEvidence;
                        if (_hwInfo.FingerprintPnpError != 0)
                            extraDiag += string.Format(" | PnP Err={0} (verovatno nedostaje drajver)", _hwInfo.FingerprintPnpError);
                        if (!string.IsNullOrEmpty(_hwInfo.WbioServiceState))
                            extraDiag += " | WbioSrvc=" + _hwInfo.WbioServiceState;
                    }
                    Dispatcher.BeginInvoke(new Action(() => {
                        _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                        _sensorIconText.Text = "⚠️";
                        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                        _statusText.Text = "Windows Biometric Service (WBF) ne vidi aktivan senzor";
                        _detailText.Text = string.Format(
                            "PnP: '{0}'{1}. Proverite Device Manager (nedostaje WBDI drajver) ili BIOS podešavanje za Fingerprint.",
                            _sensorName, extraDiag);
                    }));
                    return;
                }

                // Obezbedi Win32 fokus prozora i WBF fokus SA UI niti (zahteva MSDN za WINBIO_POOL_SYSTEM)
                int hrFocus = 0;
                Dispatcher.Invoke(new Action(() =>
                {
                    ForceWindowFocus();
                    try { hrFocus = NativeMethods.WinBioAcquireFocus(); } catch { }
                }));
                HwTestLogger.Info("FINGERPRINT", string.Format("WinBioAcquireFocus(UI) hr=0x{0:X8} | StartMode={1}",
                    hrFocus, GetModeDisplayName(initialMode)));

                if (_stopRequested) return;

                uint[] unitIdArray = allUnitIds.ToArray();
                watchdog = new System.Threading.Timer(CancelSessionOnTimeout, null, Timeout.Infinite, Timeout.Infinite);
                const int switchTimeoutMs = 4500;

                int lastErrorHr = 0;
                string lastErrorName = "";
                int modeIdx = initialMode % 5;
                int roundSuccessOrWaitCount = 0;
                int modesTriedInRound = 0;

                while (!_stopRequested)
                {
                    int mode = modeIdx % 5;
                    string modeLabel = GetModeDisplayName(mode);
                    modesTriedInRound++;

                    if (mode == 0)
                    {
                        uint ses = 0;
                        string dbUsed;
                        int hrOpen = OpenSystemSessionSafe(NativeMethods.WINBIO_FLAG_DEFAULT, out ses, out dbUsed);
                        HwTestLogger.Info("FINGERPRINT", string.Format("[Mode 0] OpenSystem(DEFAULT, db={0}) hr=0x{1:X8}", dbUsed, hrOpen));
                        if (hrOpen == 0 && ses != 0)
                        {
                            lock (_sessionLock) { _sessionHandle = ses; }
                            _timedOutForSwitch = false;
                            watchdog.Change(switchTimeoutMs, Timeout.Infinite);

                            Dispatcher.BeginInvoke(new Action(() => {
                                _btnSwitchMode.Content = "🔄 Režim: " + modeLabel;
                                _detailText.Text = string.Format("Aktivna sesija [{0}] ({1}) — dodirnite senzor prstom...", modeLabel, unitDesc);
                            }));

                            uint locatedUnit = 0;
                            int hrLoc = NativeMethods.WinBioLocateSensor(ses, out locatedUnit);
                            watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                            CloseActiveSession();

                            if (_stopRequested) return;
                            if (hrLoc == 0)
                            {
                                _captureSucceeded = true;
                                _touchDetected = true;
                                _captureMethod = "WinBioLocateSensor_System";
                                _verifiedUnitId = locatedUnit;
                                HwTestLogger.Info("FINGERPRINT", string.Format("WinBioLocateSensor(System) PASS: unit={0}", locatedUnit));
                                NotifySuccess(unitDesc, locatedUnit, string.Format("Senzor #{0} uspešno detektovao prst (System LocateSensor).", locatedUnit));
                                return;
                            }
                            if (hrLoc == NativeMethods.WINBIO_E_CANCELED && _timedOutForSwitch)
                            {
                                roundSuccessOrWaitCount++;
                            }
                            else
                            {
                                lastErrorHr = hrLoc;
                                lastErrorName = "WinBioLocateSensor(System)";
                                HwTestLogger.Warn("FINGERPRINT", string.Format("WinBioLocateSensor(System) hr=0x{0:X8}", hrLoc));
                            }
                        }
                        else
                        {
                            lastErrorHr = hrOpen;
                            lastErrorName = "OpenSystem(Locate)";
                        }
                    }
                    else if (mode == 1)
                    {
                        uint ses = 0;
                        int hrOpen = OpenPrivateSessionSafe(NativeMethods.WINBIO_FLAG_BASIC, unitIdArray, out ses);
                        HwTestLogger.Info("FINGERPRINT", string.Format("[Mode 1] OpenPrivate(BASIC, units={0}) hr=0x{1:X8}", unitIdArray.Length, hrOpen));
                        if (hrOpen == 0 && ses != 0)
                        {
                            lock (_sessionLock) { _sessionHandle = ses; }
                            _timedOutForSwitch = false;
                            watchdog.Change(switchTimeoutMs, Timeout.Infinite);

                            Dispatcher.BeginInvoke(new Action(() => {
                                _btnSwitchMode.Content = "🔄 Režim: " + modeLabel;
                                _detailText.Text = string.Format("Aktivna sesija [{0}] ({1}) — prislonite prst na čitač...", modeLabel, unitDesc);
                            }));

                            uint locatedUnit = 0;
                            int hrLoc = NativeMethods.WinBioLocateSensor(ses, out locatedUnit);
                            watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                            CloseActiveSession();

                            if (_stopRequested) return;
                            if (hrLoc == 0)
                            {
                                _captureSucceeded = true;
                                _touchDetected = true;
                                _captureMethod = "WinBioLocateSensor_Private";
                                _verifiedUnitId = locatedUnit;
                                HwTestLogger.Info("FINGERPRINT", string.Format("WinBioLocateSensor(Private) PASS: unit={0}", locatedUnit));
                                NotifySuccess(unitDesc, locatedUnit, string.Format("Senzor #{0} uspešno detektovao prst (Private LocateSensor).", locatedUnit));
                                return;
                            }
                            if (hrLoc == NativeMethods.WINBIO_E_CANCELED && _timedOutForSwitch)
                            {
                                roundSuccessOrWaitCount++;
                            }
                            else
                            {
                                lastErrorHr = hrLoc;
                                lastErrorName = "WinBioLocateSensor(Private)";
                                HwTestLogger.Warn("FINGERPRINT", string.Format("WinBioLocateSensor(Private) hr=0x{0:X8}", hrLoc));
                            }
                        }
                        else
                        {
                            lastErrorHr = hrOpen;
                            lastErrorName = "OpenPrivate(Locate)";
                        }
                    }
                    else if (mode == 2)
                    {
                        uint ses = 0;
                        string dbUsed;
                        int hrOpen = OpenSystemSessionSafe(NativeMethods.WINBIO_FLAG_DEFAULT, out ses, out dbUsed);
                        HwTestLogger.Info("FINGERPRINT", string.Format("[Mode 2] OpenSystem(Identify, db={0}) hr=0x{1:X8}", dbUsed, hrOpen));
                        if (hrOpen == 0 && ses != 0)
                        {
                            lock (_sessionLock) { _sessionHandle = ses; }
                            Dispatcher.BeginInvoke(new Action(() => {
                                _btnSwitchMode.Content = "🔄 Režim: " + modeLabel;
                                _detailText.Text = string.Format("Aktivna sesija [{0}] ({1}) — prislonite prst na čitač...", modeLabel, unitDesc);
                            }));

                            IntPtr idBuf = Marshal.AllocHGlobal(128);
                            try
                            {
                                bool keepIdent = true;
                                while (!_stopRequested && keepIdent)
                                {
                                    _timedOutForSwitch = false;
                                    watchdog.Change(_touchDetected ? Timeout.Infinite : switchTimeoutMs, Timeout.Infinite);
                                    uint unitId = 0;
                                    byte subFactor = 0;
                                    uint rejectDetail = 0;
                                    int hrIdent = NativeMethods.WinBioIdentify(ses, out unitId, idBuf, out subFactor, out rejectDetail);
                                    watchdog.Change(Timeout.Infinite, Timeout.Infinite);

                                    if (_stopRequested) { CloseActiveSession(); return; }
                                    if (hrIdent == 0 || hrIdent == NativeMethods.WINBIO_E_UNKNOWN_ID || hrIdent == NativeMethods.WINBIO_E_NO_MATCH)
                                    {
                                        _captureSucceeded = true;
                                        _touchDetected = true;
                                        _captureMethod = (hrIdent == 0) ? "WinBioIdentify_Match" : "WinBioIdentify_Captured";
                                        _verifiedUnitId = unitId;
                                        CloseActiveSession();
                                        HwTestLogger.Info("FINGERPRINT", string.Format("WinBioIdentify PASS: hr=0x{0:X8} unit={1}", hrIdent, unitId));
                                        NotifySuccess(unitDesc, unitId, string.Format("Senzor #{0} uspešno očitao prst (WBF Identify).", unitId));
                                        return;
                                    }
                                    else if (hrIdent == NativeMethods.WINBIO_E_BAD_CAPTURE)
                                    {
                                        _touchDetected = true;
                                        roundSuccessOrWaitCount++;
                                        HwTestLogger.Warn("FINGERPRINT", string.Format("WinBioIdentify BAD_CAPTURE (dodir detektovan): unit={0} reject=0x{1:X}", unitId, rejectDetail));
                                        Dispatcher.BeginInvoke(new Action(() => {
                                            _btnPass.IsEnabled = true;
                                            _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                                            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                                            _statusText.Text = "Dodir detektovan! Prislonite prst mirno radi čistog očitavanja...";
                                            _detailText.Text = string.Format("Senzor je reagovao na prst (RejectDetail=0x{0:X}). Možete ponoviti ili kliknuti PASS.", rejectDetail);
                                        }));
                                    }
                                    else if (hrIdent == NativeMethods.WINBIO_E_CANCELED && _timedOutForSwitch)
                                    {
                                        roundSuccessOrWaitCount++;
                                        keepIdent = false;
                                    }
                                    else
                                    {
                                        lastErrorHr = hrIdent;
                                        lastErrorName = "WinBioIdentify";
                                        HwTestLogger.Warn("FINGERPRINT", string.Format("WinBioIdentify hr=0x{0:X8} reject=0x{1:X}", hrIdent, rejectDetail));
                                        keepIdent = false;
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(idBuf);
                                CloseActiveSession();
                            }
                        }
                        else
                        {
                            lastErrorHr = hrOpen;
                            lastErrorName = "OpenSystem(Identify)";
                        }
                    }
                    else
                    {
                        bool usePrivate = (mode == 3);
                        uint ses = 0;
                        string dbUsed = "PRIVATE";
                        int hrOpen = usePrivate
                            ? OpenPrivateSessionSafe(NativeMethods.WINBIO_FLAG_RAW, unitIdArray, out ses)
                            : OpenSystemSessionSafe(NativeMethods.WINBIO_FLAG_RAW, out ses, out dbUsed);

                        HwTestLogger.Info("FINGERPRINT", string.Format("[Mode {0}] OpenCapture({1}, db={2}) hr=0x{3:X8}",
                            mode, usePrivate ? "PRIVATE" : "SYSTEM", dbUsed, hrOpen));

                        if (hrOpen == 0 && ses != 0)
                        {
                            lock (_sessionLock) { _sessionHandle = ses; }
                            Dispatcher.BeginInvoke(new Action(() => {
                                _btnSwitchMode.Content = "🔄 Režim: " + modeLabel;
                                _detailText.Text = string.Format("Aktivna sesija [{0}] ({1}) — prislonite prst na čitač...", modeLabel, unitDesc);
                            }));

                            byte[] purposes = new byte[] { NativeMethods.WINBIO_NO_PURPOSE_AVAILABLE, NativeMethods.WINBIO_PURPOSE_VERIFY };
                            byte[] dataFlags = new byte[] { NativeMethods.WINBIO_DATA_FLAG_RAW, NativeMethods.WINBIO_DATA_FLAG_PROCESSED };
                            bool switchedOut = false;

                            for (int pi = 0; pi < purposes.Length && !switchedOut && !_stopRequested; pi++)
                            {
                                for (int fi = 0; fi < dataFlags.Length && !switchedOut && !_stopRequested; fi++)
                                {
                                    bool keepCap = true;
                                    while (!_stopRequested && keepCap)
                                    {
                                        _timedOutForSwitch = false;
                                        watchdog.Change(_touchDetected ? Timeout.Infinite : switchTimeoutMs, Timeout.Infinite);
                                        uint unitId = 0;
                                        IntPtr samplePtr = IntPtr.Zero;
                                        UIntPtr sampleSize = UIntPtr.Zero;
                                        uint rejectDetail = 0;

                                        int hrCap = NativeMethods.WinBioCaptureSample(
                                            ses, purposes[pi], dataFlags[fi],
                                            out unitId, out samplePtr, out sampleSize, out rejectDetail);
                                        watchdog.Change(Timeout.Infinite, Timeout.Infinite);

                                        uint bytesCount = (uint)sampleSize;
                                        if (samplePtr != IntPtr.Zero)
                                        {
                                            try { NativeMethods.WinBioFree(samplePtr); } catch { }
                                        }

                                        if (_stopRequested) { CloseActiveSession(); return; }
                                        if (hrCap == 0)
                                        {
                                            _captureSucceeded = true;
                                            _touchDetected = true;
                                            _captureMethod = string.Format("CaptureSample_{0}_{1}",
                                                usePrivate ? "Private" : "System",
                                                dataFlags[fi] == NativeMethods.WINBIO_DATA_FLAG_RAW ? "RAW" : "PROCESSED");
                                            _verifiedUnitId = unitId;
                                            _sampleSizeBytes = bytesCount;
                                            CloseActiveSession();
                                            HwTestLogger.Info("FINGERPRINT", string.Format("{0} PASS: unit={1} bytes={2}", _captureMethod, unitId, bytesCount));
                                            NotifySuccess(unitDesc, unitId, string.Format("Senzor #{0} očitao biometrijski uzorak ({1} B).", unitId, bytesCount));
                                            return;
                                        }
                                        else if (hrCap == NativeMethods.WINBIO_E_BAD_CAPTURE)
                                        {
                                            _touchDetected = true;
                                            roundSuccessOrWaitCount++;
                                            HwTestLogger.Warn("FINGERPRINT", string.Format("CaptureSample BAD_CAPTURE (dodir detektovan): unit={0} reject=0x{1:X}", unitId, rejectDetail));
                                            Dispatcher.BeginInvoke(new Action(() => {
                                                _btnPass.IsEnabled = true;
                                                _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                                                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                                                _statusText.Text = "Dodir detektovan! Prislonite ceo prst mirno radi čistog očitavanja...";
                                                _detailText.Text = string.Format("Senzor je reagovao na prst (RejectDetail=0x{0:X}). Možete ponoviti ili odmah kliknuti PASS.", rejectDetail);
                                            }));
                                        }
                                        else if (hrCap == NativeMethods.WINBIO_E_CANCELED && _timedOutForSwitch)
                                        {
                                            roundSuccessOrWaitCount++;
                                            keepCap = false;
                                            switchedOut = true;
                                        }
                                        else
                                        {
                                            lastErrorHr = hrCap;
                                            lastErrorName = "WinBioCaptureSample";
                                            HwTestLogger.Warn("FINGERPRINT", string.Format("CaptureSample({0}, purpose={1}, df=0x{2:X2}) hr=0x{3:X8}",
                                                usePrivate ? "Private" : "System", purposes[pi], dataFlags[fi], hrCap));
                                            keepCap = false;
                                        }
                                    }
                                }
                            }
                            CloseActiveSession();
                        }
                        else
                        {
                            lastErrorHr = hrOpen;
                            lastErrorName = usePrivate ? "OpenPrivate(RAW)" : "OpenSystem(RAW)";
                        }
                    }

                    modeIdx = (modeIdx + 1) % 5;
                    if (modesTriedInRound >= 5)
                    {
                        if (roundSuccessOrWaitCount == 0)
                        {
                            // Svih 5 režima je momentalno palo sa greškom
                            break;
                        }
                        modesTriedInRound = 0;
                        roundSuccessOrWaitCount = 0;
                    }
                }

                if (!_stopRequested)
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                        _sensorIconText.Text = "⚠️";
                        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                        _statusText.Text = string.Format("{0} greška (0x{1:X8})", string.IsNullOrEmpty(lastErrorName) ? "WinBio" : lastErrorName, lastErrorHr);
                        _detailText.Text = "Nijedan WBF režim nije uspeo da otvori sesiju. Kliknite 'Testiraj ponovo' ili označite FAIL ako senzor ne reaguje.";
                    }));
                }
            }
            catch (DllNotFoundException)
            {
                HwTestLogger.Error("FINGERPRINT", "winbio.dll nije dostupan na ovom Windows sistemu");
                Dispatcher.BeginInvoke(new Action(() => {
                    _statusText.Text = "winbio.dll nije dostupan na sistemu";
                    _detailText.Text = "Windows Biometric Framework nije instaliran.";
                }));
            }
            catch (Exception ex)
            {
                HwTestLogger.Error("FINGERPRINT", "ListenLoop exception: " + ex.Message);
            }
            finally
            {
                if (watchdog != null)
                {
                    try { watchdog.Dispose(); } catch { }
                }
            }
        }

        private void NotifySuccess(string unitDesc, uint unitId, string msg)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                _sensorBadge.Background = new SolidColorBrush(Color.FromArgb(90, 40, 167, 69));
                _sensorBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _sensorIconText.Text = "✓";
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                _statusText.Text = "✓ ČITAČ OTISKA PRSTA RADI!";
                _detailText.Text = msg + string.Format(" ({0}) — automatski zaključujem za 1.5s...", unitDesc);
                _btnPass.IsEnabled = true;

                StartAutoPassTimer();
            }));
        }

        private void StartAutoPassTimer()
        {
            StopAutoPassTimer();
            _autoPassTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _autoPassTimer.Tick += (s, e) => {
                StopAutoPassTimer();
                FinishPass();
            };
            _autoPassTimer.Start();
        }

        private void StopAutoPassTimer()
        {
            if (_autoPassTimer != null)
            {
                try { _autoPassTimer.Stop(); } catch { }
                _autoPassTimer = null;
            }
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
            StopAutoPassTimer();
            StopListener();
            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["sensor_name"] = _sensorName;
            Result.Details["unit_id"] = _verifiedUnitId > 0 ? _verifiedUnitId : _initialUnitId;
            Result.Details["capture_method"] = _captureMethod;
            Result.Details["touch_detected"] = _touchDetected;
            if (_sampleSizeBytes > 0) Result.Details["sample_bytes"] = _sampleSizeBytes;
            SafeCloseDialog(true);
        }

        private void FinishFail(string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopAutoPassTimer();
            StopListener();
            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = reason;
            Result.Details["sensor_name"] = _sensorName;
            Result.Details["touch_detected"] = _touchDetected;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopAutoPassTimer();
            StopListener();
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test otiska prsta";
            Result.Details["sensor_name"] = _sensorName;
            SafeCloseDialog(false);
        }
    }
}
