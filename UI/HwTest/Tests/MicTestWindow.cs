using System;
using System.Collections.Generic;
using System.IO;
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
    public class MicTestWindow : Window
    {
        private IntPtr _hwi = IntPtr.Zero;
        private AutoResetEvent _waveEvent;
        private Thread _captureThread;
        private volatile bool _isClosing = false;
        private bool _isClosingOrDone = false;
        private int _micInitGeneration = 0;
        private readonly object _waveInLock = new object();

        private IntPtr _pBuffer1 = IntPtr.Zero;
        private IntPtr _pBuffer2 = IntPtr.Zero;
        private IntPtr _pHdr1 = IntPtr.Zero;
        private IntPtr _pHdr2 = IntPtr.Zero;
        private const int BUFFER_SIZE = 3200; // 100 ms na 16000 Hz 16-bit mono

        private readonly ProgressBar _vuMeter;
        private readonly TextBlock _dbfsText;
        private readonly TextBlock _statusText;
        private readonly Button _btnPass;
        private readonly Button _btnFail;
        private readonly Button _btnRecordManual;
        private readonly Button _btnReplay;
        private readonly StackPanel _micSelectPanel;
        private readonly ComboBox _micCombo;
        private uint _selectedDeviceId = NativeMethods.WAVE_MAPPER;
        private bool _suppressMicComboEvent = false;

        private int _consecutiveAboveThreshold = 0;
        private bool _isRecording3s = false;
        private readonly object _recordLock = new object();
        private MemoryStream _recordStream;
        private DispatcherTimer _recordTimer;
        private DispatcherTimer _autoPassTimer;
        private byte[] _recorded3sAudio;
        private double _peakDbfs = -60.0;
        private bool _autoVerified = false;
        private int _lastActiveFrames = 0;
        private double _lastDynamicRangeDb = 0.0;
        private double _lastAcStdDev = 0.0;
        private string _analysisSummary = "";
        private float _originalMicVolume = -1f;
        private bool _originalMicMuted = false;
        private bool _micVolumeModified = false;
        private readonly Action<string> _onRequestUefiBiosReboot;
        private readonly bool _showBiosRebootHint;
        private readonly Border _footerBorder;

        public Action<string> OnRequestUefiBiosReboot { get; set; }
        public TestItemResult Result { get; private set; }

        public MicTestWindow(Action<string> onRequestUefiBiosReboot = null, bool showBiosRebootHint = false)
        {
            _onRequestUefiBiosReboot = onRequestUefiBiosReboot;
            _showBiosRebootHint = showBiosRebootHint;
            Title = HwTestLocalization.Pick("WIS — Test Mikrofona", "WIS — 麦克风测试");
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = new TestItemResult("mic",
                HwTestLocalization.Pick("Mikrofon", "麦克风"),
                HwTestLocalization.Pick("Ispitivanje mikrofona, VU metar i automatska reprodukcija 3s snimka", "麦克风检测、音量表及自动录音回放测试"));

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            DockPanel headerDock = new DockPanel();
            TextBlock title = new TextBlock
            {
                Text = HwTestLocalization.Pick("🎤 TEST MIKROFONA (VU METAR I AUTOMATSKI SNIMAK)", "🎤 麦克风测试（音量表与自动录音）"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Left);
            headerDock.Children.Add(title);

            _micSelectPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Visibility = Visibility.Collapsed
            };
            _micSelectPanel.Children.Add(new TextBlock
            {
                Text = HwTestLocalization.Pick("Ulazni uređaj: ", "输入设备: "),
                Foreground = new SolidColorBrush(Color.FromRgb(185, 190, 200)),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _micCombo = new ComboBox
            {
                Width = 280,
                Height = 30,
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _micCombo.SelectionChanged += OnMicSelectionChanged;
            _micSelectPanel.Children.Add(_micCombo);
            headerDock.Children.Add(_micSelectPanel);

            header.Child = headerDock;
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
                Width = 600
            };

            StackPanel spC = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _statusText = new TextBlock
            {
                Text = HwTestLocalization.Pick("Recite nešto u mikrofon ili pucnite prstima...", "请对着麦克风说话或打响指..."),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };
            spC.Children.Add(_statusText);

            // VU Meter
            _vuMeter = new ProgressBar
            {
                Width = 480,
                Height = 30,
                Minimum = -60,
                Maximum = 0,
                Value = -60,
                Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            spC.Children.Add(_vuMeter);

            _dbfsText = new TextBlock
            {
                Text = HwTestLocalization.Pick("Nivo signala: -60 dBFS (Prag za automatsko snimanje: > -44 dBFS)", "信号电平: -60 dBFS（自动录音阈值: > -44 dBFS）"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 180)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 25)
            };
            spC.Children.Add(_dbfsText);

            StackPanel spButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _btnPass = new Button
            {
                Content = HwTestLocalization.Pick("✓ Potvrdi sada (PASS)", "✓ 立即确认通过 (PASS)"),
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 10, 20, 10),
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = false
            };
            _btnPass.Click += (s, e) => FinishPass();
            spButtons.Children.Add(_btnPass);

            _btnFail = new Button
            {
                Content = HwTestLocalization.Pick("✗ Mikrofon ne radi (FAIL)", "✗ 麦克风故障 (FAIL)"),
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = true
            };
            _btnFail.Click += (s, e) => FinishFail(_analysisSummary);
            spButtons.Children.Add(_btnFail);

            _btnRecordManual = new Button
            {
                Content = HwTestLocalization.Pick("⏺️ Snimi ponovo 2s", "⏺️ 重新录音 2 秒"),
                Background = new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = false
            };
            _btnRecordManual.Click += (s, e) => {
                StopAutoPassTimer();
                if (!_isRecording3s)
                {
                    Start3sRecording();
                }
            };
            spButtons.Children.Add(_btnRecordManual);

            _btnReplay = new Button
            {
                Content = HwTestLocalization.Pick("🔊 Preslušaj snimak 2s", "🔊 回放 2 秒录音"),
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 10, 15, 10),
                IsEnabled = false
            };
            _btnReplay.Click += (s, e) => {
                StopAutoPassTimer();
                PlayRecordedAudio();
            };
            spButtons.Children.Add(_btnReplay);

            spC.Children.Add(spButtons);
            centerBorder.Child = spC;
            Grid.SetRow(centerBorder, 1);
            root.Children.Add(centerBorder);

            // Footer (prikazuje se samo kada je potrebno uslovno UEFI BIOS dugme)
            _footerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12),
                Visibility = _showBiosRebootHint ? Visibility.Visible : Visibility.Collapsed
            };
            DockPanel footerDock = new DockPanel();
            TextBlock biosHintLabel = new TextBlock
            {
                Text = HwTestLocalization.Pick("⚠️ Mikrofon je onemogućen u BIOS-u ili nedostaje uprkos OEM BOM-u (HP: F10, Lenovo: F1, Dell: F2).",
                    "⚠️ 尽管出厂 BOM 包含麦克风，但其在 BIOS 中被禁用或缺失（HP: F10, Lenovo: F1, Dell: F2）。"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 16, 0)
            };
            DockPanel.SetDock(biosHintLabel, Dock.Left);

            Button btnUefiBios = new Button
            {
                Content = HwTestLocalization.Pick("🔧 Restartuj u UEFI BIOS (i nastavi WIS)", "🔧 重启进入 UEFI BIOS（之后继续 WIS）"),
                Background = new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 9, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btnUefiBios.Click += (s, e) => RequestUefiBiosReboot();
            DockPanel.SetDock(btnUefiBios, Dock.Right);

            footerDock.Children.Add(btnUefiBios);
            footerDock.Children.Add(biosHintLabel);
            _footerBorder.Child = footerDock;
            Grid.SetRow(_footerBorder, 2);
            root.Children.Add(_footerBorder);

            Content = root;

            Loaded += (s, e) => {
                _btnRecordManual.IsEnabled = false;
                _statusText.Text = HwTestLocalization.Pick("⏳ Inicijalizacija audio ulaza (mikrofon)...", "⏳ 正在初始化音频输入设备（麦克风）...");
                _statusText.Foreground = Brushes.White;
                StartMicInitAsync(true);
            };
            Closed += (s, e) => {
                _isClosingOrDone = true;
                _isClosing = true;
                Interlocked.Increment(ref _micInitGeneration);
                StopAutoPassTimer();
                if (_recordTimer != null)
                {
                    try { _recordTimer.Stop(); } catch { }
                    _recordTimer = null;
                }
                Thread cleanupThread = new Thread(() => {
                    try
                    {
                        StopWaveIn();
                        RestoreCaptureEndpointVolume();
                    }
                    catch { }
                });
                cleanupThread.IsBackground = true;
                cleanupThread.SetApartmentState(ApartmentState.STA);
                cleanupThread.Start();
                try { cleanupThread.Join(1200); } catch { }
            };
        }

        private class MicDeviceChoice
        {
            public uint DeviceId { get; set; }
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }

        private void UpdateStatusSafe(string text, Color fgColor, bool? enableManualRecord = null)
        {
            if (_isClosingOrDone) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (_isClosingOrDone) return;
                _statusText.Text = text;
                _statusText.Foreground = new SolidColorBrush(fgColor);
                if (enableManualRecord.HasValue)
                {
                    _btnRecordManual.IsEnabled = enableManualRecord.Value;
                }
            }));
        }

        private void StartMicInitAsync(bool populateDevices)
        {
            int myGen = Interlocked.Increment(ref _micInitGeneration);
            Thread initThread = new Thread(() => {
                try
                {
                    // Kratka grace pauza posle AudioTestWindow waveOutClose da audio drajver oslobodi sesiju
                    if (populateDevices)
                    {
                        Thread.Sleep(100);
                    }
                    if (_isClosingOrDone || myGen != _micInitGeneration) return;

                    StopWaveIn();
                    if (_isClosingOrDone || myGen != _micInitGeneration) return;

                    if (populateDevices)
                    {
                        List<MicDeviceChoice> choices = CollectMicDevices();
                        if (_isClosingOrDone || myGen != _micInitGeneration) return;

                        Dispatcher.BeginInvoke(new Action(() => {
                            PopulateMicDevices(choices, myGen);
                        }));
                    }

                    TryUnmuteAndLogCaptureEndpoint();
                    if (_isClosingOrDone || myGen != _micInitGeneration) return;

                    StartWaveInCore(myGen);
                }
                catch (Exception ex)
                {
                    HwTestLogger.Error("MIC", "StartMicInitAsync exception: " + ex.Message);
                    UpdateStatusSafe(HwTestLocalization.Pick("⚠ Greška pri pokretanju mikrofona: ", "⚠ 启动麦克风失败: ") + ex.Message, Color.FromRgb(220, 53, 69), false);
                }
            });
            initThread.IsBackground = true;
            initThread.SetApartmentState(ApartmentState.STA);
            initThread.Start();
        }

        private void PopulateMicDevices(List<MicDeviceChoice> choices, int expectedGen)
        {
            if (_isClosingOrDone || expectedGen != _micInitGeneration || _micCombo == null || choices == null) return;
            try
            {
                _suppressMicComboEvent = true;
                _micCombo.Items.Clear();
                foreach (MicDeviceChoice ch in choices)
                {
                    _micCombo.Items.Add(ch);
                }
                _micCombo.SelectedIndex = 0;
                _suppressMicComboEvent = false;
                if (_micSelectPanel != null)
                {
                    _micSelectPanel.Visibility = (choices.Count > 2) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
                _suppressMicComboEvent = false;
            }
        }

        private List<MicDeviceChoice> CollectMicDevices()
        {
            List<MicDeviceChoice> choices = new List<MicDeviceChoice>();
            choices.Add(new MicDeviceChoice
            {
                DeviceId = NativeMethods.WAVE_MAPPER,
                Label = HwTestLocalization.Pick("Podrazumevani mikrofon (Windows Mapper)", "默认麦克风 (Windows Mapper)")
            });

            try
            {
                uint numDevs = NativeMethods.waveInGetNumDevs();
                uint capsSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WAVEINCAPSW));
                for (uint i = 0; i < numDevs; i++)
                {
                    NativeMethods.WAVEINCAPSW caps;
                    string devName = HwTestLocalization.PickFormat("Mikrofon #{0}", "麦克风 #{0}", i + 1);
                    if (NativeMethods.waveInGetDevCaps((IntPtr)i, out caps, capsSize) == 0 && !string.IsNullOrEmpty(caps.szPname))
                    {
                        devName = caps.szPname.Trim();
                    }
                    choices.Add(new MicDeviceChoice
                    {
                        DeviceId = i,
                        Label = string.Format("[{0}] {1}", i + 1, devName)
                    });
                    HwTestLogger.Info("MIC", string.Format("Ulazni audio uređaj #{0}: {1}", i, devName));
                }
            }
            catch { }
            return choices;
        }

        private void OnMicSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressMicComboEvent || _micCombo == null) return;
            MicDeviceChoice choice = _micCombo.SelectedItem as MicDeviceChoice;
            if (choice == null) return;

            _selectedDeviceId = choice.DeviceId;
            HwTestLogger.Info("MIC", string.Format("Korisnik izabrao mikrofon ID={0} ({1})", _selectedDeviceId, choice.Label));

            StopAutoPassTimer();
            lock (_recordLock) { _isRecording3s = false; }
            if (_recordTimer != null) { try { _recordTimer.Stop(); } catch { } }
            _recorded3sAudio = null;
            _peakDbfs = -60.0;
            _autoVerified = false;
            _consecutiveAboveThreshold = 0;
            _btnPass.IsEnabled = false;
            _btnReplay.IsEnabled = false;
            _btnRecordManual.IsEnabled = false;
            _statusText.Foreground = Brushes.White;
            _statusText.Text = HwTestLocalization.PickFormat("Prebačeno na: {0} — inicijalizacija...", "已切换至: {0} — 正在初始化...", choice.Label);

            StartMicInitAsync(false);
        }

        private void StartWaveInCore(int expectedGen)
        {
            lock (_waveInLock)
            {
                try
                {
                    if (_isClosingOrDone || expectedGen != _micInitGeneration) return;

                    uint numDevs = NativeMethods.waveInGetNumDevs();
                    HwTestLogger.Info("MIC", "waveInGetNumDevs=" + numDevs + " | SelectedDeviceId=" + _selectedDeviceId);
                    if (numDevs == 0)
                    {
                        UpdateStatusSafe(HwTestLocalization.Pick("⚠ Nije detektovan nijedan ulazni audio uređaj (mikrofon).", "⚠ 未检测到任何音频输入设备（麦克风）。"), Color.FromRgb(220, 53, 69), false);
                        return;
                    }

                    NativeMethods.WAVEFORMATEX wfx = new NativeMethods.WAVEFORMATEX
                    {
                        wFormatTag = NativeMethods.WAVE_FORMAT_PCM,
                        nChannels = 1,
                        nSamplesPerSec = 16000,
                        wBitsPerSample = 16,
                        nBlockAlign = 2,
                        nAvgBytesPerSec = 32000,
                        cbSize = 0
                    };

                    _waveEvent = new AutoResetEvent(false);
                    int openRes = NativeMethods.waveInOpen(
                        out _hwi,
                        _selectedDeviceId,
                        ref wfx,
                        _waveEvent.SafeWaitHandle.DangerousGetHandle(),
                        IntPtr.Zero,
                        NativeMethods.CALLBACK_EVENT
                    );

                    if (_isClosingOrDone || expectedGen != _micInitGeneration)
                    {
                        StopWaveIn();
                        return;
                    }

                    if (openRes != 0 || _hwi == IntPtr.Zero)
                    {
                        string err = HwTestLocalization.PickFormat("waveInOpen neuspešan (MMSYSERR={0}). Proverite privatnost mikrofona u Windowsu.",
                            "waveInOpen 失败 (MMSYSERR={0})。请检查 Windows 麦克风隐私权限。", openRes);
                        HwTestLogger.Error("MIC", string.Format("waveInOpen neuspešan (MMSYSERR={0}).", openRes));
                        UpdateStatusSafe("⚠ " + err, Color.FromRgb(220, 53, 69), false);
                        return;
                    }

                    int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
                    _pBuffer1 = Marshal.AllocHGlobal(BUFFER_SIZE);
                    _pBuffer2 = Marshal.AllocHGlobal(BUFFER_SIZE);
                    _pHdr1 = Marshal.AllocHGlobal(hdrSize);
                    _pHdr2 = Marshal.AllocHGlobal(hdrSize);

                    NativeMethods.WAVEHDR h1 = new NativeMethods.WAVEHDR { lpData = _pBuffer1, dwBufferLength = BUFFER_SIZE };
                    NativeMethods.WAVEHDR h2 = new NativeMethods.WAVEHDR { lpData = _pBuffer2, dwBufferLength = BUFFER_SIZE };
                    Marshal.StructureToPtr(h1, _pHdr1, false);
                    Marshal.StructureToPtr(h2, _pHdr2, false);

                    int p1 = NativeMethods.waveInPrepareHeader(_hwi, _pHdr1, (uint)hdrSize);
                    int p2 = NativeMethods.waveInPrepareHeader(_hwi, _pHdr2, (uint)hdrSize);
                    int a1 = NativeMethods.waveInAddBuffer(_hwi, _pHdr1, (uint)hdrSize);
                    int a2 = NativeMethods.waveInAddBuffer(_hwi, _pHdr2, (uint)hdrSize);
                    int sRes = NativeMethods.waveInStart(_hwi);

                    HwTestLogger.Info("MIC", string.Format("waveInStart res={0} (prep={1}/{2}, add={3}/{4})", sRes, p1, p2, a1, a2));

                    if (sRes != 0)
                    {
                        UpdateStatusSafe(HwTestLocalization.PickFormat("⚠ waveInStart neuspešan (code={0}).", "⚠ waveInStart 失败 (code={0})。", sRes), Color.FromRgb(220, 53, 69), false);
                        return;
                    }

                    _isClosing = false;
                    _captureThread = new Thread(CaptureLoop) { IsBackground = true };
                    _captureThread.Start();
                    UpdateStatusSafe(HwTestLocalization.Pick("Recite nešto glasno u mikrofon (npr. 'Test jedan dva tri')...", "请对着麦克风大声说话（例如“测试一二三”）..."), Colors.White, true);
                }
                catch (Exception ex)
                {
                    HwTestLogger.Error("MIC", "StartWaveInCore exception: " + ex.Message);
                    UpdateStatusSafe(HwTestLocalization.Pick("⚠ Greška pri pokretanju mikrofona: ", "⚠ 启动麦克风失败: ") + ex.Message, Color.FromRgb(220, 53, 69), false);
                }
            }
        }

        private void TryUnmuteAndLogCaptureEndpoint()
        {
            try
            {
                Type enumeratorType = typeof(NativeMethods.MMDeviceEnumeratorComObject);
                NativeMethods.IMMDeviceEnumerator devEnum = (NativeMethods.IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
                NativeMethods.IMMDevice capDev;
                // eCapture = 1, eConsole = 0
                int hr = devEnum.GetDefaultAudioEndpoint(1, 0, out capDev);
                if (hr == 0 && capDev != null)
                {
                    Guid iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                    object volObj;
                    if (capDev.Activate(ref iidVol, 23, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        NativeMethods.IAudioEndpointVolume epVol = (NativeMethods.IAudioEndpointVolume)volObj;
                        bool isMuted;
                        float scalarVol;
                        epVol.GetMute(out isMuted);
                        epVol.GetMasterVolumeLevelScalar(out scalarVol);
                        HwTestLogger.Info("MIC", string.Format("CoreAudio capture endpoint: mute={0}, level={1:P0}", isMuted, scalarVol));
                        if (_originalMicVolume < 0f)
                        {
                            _originalMicVolume = scalarVol;
                            _originalMicMuted = isMuted;
                        }
                        if (isMuted)
                        {
                            Guid ctx = Guid.Empty;
                            epVol.SetMute(false, ref ctx);
                            _micVolumeModified = true;
                            HwTestLogger.Info("MIC", "Mikrofon je bio mutiran — automatski odmutiran za test.");
                        }
                        if (scalarVol < 0.95f)
                        {
                            Guid ctx = Guid.Empty;
                            epVol.SetMasterVolumeLevelScalar(0.96f, ref ctx);
                            _micVolumeModified = true;
                            HwTestLogger.Info("MIC", string.Format("Pojačan nivo mikrofona na 96% radi testa (prethodno: {0:P0}).", scalarVol));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("MIC", "CoreAudio endpoint check: " + ex.Message);
            }
        }

        private void RestoreCaptureEndpointVolume()
        {
            if (!_micVolumeModified) return;
            _micVolumeModified = false;
            try
            {
                Type enumeratorType = typeof(NativeMethods.MMDeviceEnumeratorComObject);
                NativeMethods.IMMDeviceEnumerator devEnum = (NativeMethods.IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
                NativeMethods.IMMDevice capDev;
                int hr = devEnum.GetDefaultAudioEndpoint(1, 0, out capDev);
                if (hr == 0 && capDev != null)
                {
                    Guid iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                    object volObj;
                    if (capDev.Activate(ref iidVol, 23, IntPtr.Zero, out volObj) == 0 && volObj != null)
                    {
                        NativeMethods.IAudioEndpointVolume epVol = (NativeMethods.IAudioEndpointVolume)volObj;
                        Guid ctx = Guid.Empty;
                        if (_originalMicVolume >= 0f && _originalMicVolume <= 1.0f)
                        {
                            epVol.SetMasterVolumeLevelScalar(_originalMicVolume, ref ctx);
                        }
                        if (_originalMicMuted)
                        {
                            epVol.SetMute(true, ref ctx);
                        }
                        HwTestLogger.Info("MIC", string.Format("Vraćen originalni nivo mikrofona: level={0:P0}, mute={1}", _originalMicVolume, _originalMicMuted));
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Warn("MIC", "RestoreCaptureEndpointVolume: " + ex.Message);
            }
        }

        private void CaptureLoop()
        {
            int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
            try
            {
                while (!_isClosing && _hwi != IntPtr.Zero)
                {
                    if (_waveEvent != null)
                    {
                        _waveEvent.WaitOne(150);
                    }
                    if (_isClosing || _hwi == IntPtr.Zero) break;

                    ProcessCaptureHeader(_pHdr1, hdrSize);
                    ProcessCaptureHeader(_pHdr2, hdrSize);
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Error("MIC", "CaptureLoop greška: " + ex.Message);
            }
        }

        private void ProcessCaptureHeader(IntPtr pHdr, int hdrSize)
        {
            if (pHdr == IntPtr.Zero || _isClosing || _hwi == IntPtr.Zero) return;

            NativeMethods.WAVEHDR hdr = (NativeMethods.WAVEHDR)Marshal.PtrToStructure(pHdr, typeof(NativeMethods.WAVEHDR));
            if ((hdr.dwFlags & NativeMethods.WHDR_DONE) == 0) return;

            byte[] data = null;
            if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
            {
                int len = (int)hdr.dwBytesRecorded;
                if (len > BUFFER_SIZE) len = BUFFER_SIZE;
                data = new byte[len];
                Marshal.Copy(hdr.lpData, data, 0, len);
            }

            // Recikliraj unmanaged WAVEHDR nazad u queue bez re-prepare
            if (!_isClosing && _hwi != IntPtr.Zero)
            {
                hdr.dwFlags &= ~NativeMethods.WHDR_DONE;
                hdr.dwBytesRecorded = 0;
                Marshal.StructureToPtr(hdr, pHdr, false);
                NativeMethods.waveInAddBuffer(_hwi, pHdr, (uint)hdrSize);
            }

            if (data != null && data.Length >= 2)
            {
                if (_isRecording3s)
                {
                    lock (_recordLock)
                    {
                        if (_isRecording3s && _recordStream != null)
                        {
                            _recordStream.Write(data, 0, data.Length);
                        }
                    }
                }

                double sumSq = 0;
                int maxAbs = 0;
                int samples = data.Length / 2;
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    short s = (short)(data[i] | (data[i + 1] << 8));
                    int absVal = s < 0 ? -s : s;
                    if (absVal > maxAbs) maxAbs = absVal;
                    sumSq += (double)s * s;
                }
                double rms = Math.Sqrt(sumSq / Math.Max(1, samples));
                double rmsDbfs = 20.0 * Math.Log10(Math.Max(1.0, rms) / 32768.0);
                double peakBlockDbfs = 20.0 * Math.Log10(Math.Max(1.0, (double)maxAbs) / 32768.0);
                double dbfs = Math.Max(rmsDbfs, peakBlockDbfs - 4.0);
                if (dbfs < -60.0) dbfs = -60.0;
                if (dbfs > 0.0) dbfs = 0.0;

                Dispatcher.BeginInvoke(new Action(() => {
                    UpdateVuMeter(dbfs);
                }));
            }
        }

        private void StopWaveIn()
        {
            _isClosing = true;
            if (_waveEvent != null)
            {
                try { _waveEvent.Set(); } catch { }
            }
            if (_captureThread != null && _captureThread.IsAlive)
            {
                try { _captureThread.Join(1000); } catch { }
            }

            lock (_waveInLock)
            {
                if (_hwi != IntPtr.Zero)
                {
                    int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
                    try { NativeMethods.waveInStop(_hwi); } catch { }
                    try { NativeMethods.waveInReset(_hwi); } catch { }
                    if (_pHdr1 != IntPtr.Zero) { try { NativeMethods.waveInUnprepareHeader(_hwi, _pHdr1, (uint)hdrSize); } catch { } }
                    if (_pHdr2 != IntPtr.Zero) { try { NativeMethods.waveInUnprepareHeader(_hwi, _pHdr2, (uint)hdrSize); } catch { } }
                    try { NativeMethods.waveInClose(_hwi); } catch { }
                    _hwi = IntPtr.Zero;
                }

                if (_pHdr1 != IntPtr.Zero) { Marshal.FreeHGlobal(_pHdr1); _pHdr1 = IntPtr.Zero; }
                if (_pHdr2 != IntPtr.Zero) { Marshal.FreeHGlobal(_pHdr2); _pHdr2 = IntPtr.Zero; }
                if (_pBuffer1 != IntPtr.Zero) { Marshal.FreeHGlobal(_pBuffer1); _pBuffer1 = IntPtr.Zero; }
                if (_pBuffer2 != IntPtr.Zero) { Marshal.FreeHGlobal(_pBuffer2); _pBuffer2 = IntPtr.Zero; }
                if (_waveEvent != null)
                {
                    try { _waveEvent.Close(); } catch { }
                    _waveEvent = null;
                }
            }
        }

        private void UpdateVuMeter(double dbfs)
        {
            if (dbfs > _peakDbfs) _peakDbfs = dbfs;
            _vuMeter.Value = dbfs;
            _dbfsText.Text = HwTestLocalization.PickFormat("Nivo signala: {0:F1} dBFS | Peak: {1:F1} dBFS (Prag: > -44 dBFS)",
                "信号电平: {0:F1} dBFS | 峰值: {1:F1} dBFS（阈值: > -44 dBFS）", dbfs, _peakDbfs);

            if (_peakDbfs > -42.0 && !_btnPass.IsEnabled)
            {
                _btnPass.IsEnabled = true;
            }

            if (dbfs > -44.0)
            {
                _consecutiveAboveThreshold++;
                if (_consecutiveAboveThreshold >= 1 && !_isRecording3s && _recorded3sAudio == null)
                {
                    Start3sRecording();
                }
            }
            else
            {
                _consecutiveAboveThreshold = 0;
            }
        }

        private void Start3sRecording()
        {
            if (_isRecording3s) return;
            HwTestLogger.Info("MIC", string.Format("Pokrenuto 2s snimanje (peak={0:F1} dBFS)", _peakDbfs));

            lock (_recordLock)
            {
                _recordStream = new MemoryStream();
                _isRecording3s = true;
            }

            _btnRecordManual.IsEnabled = false;
            _statusText.Text = HwTestLocalization.Pick("⏺️ SNIMANJE U TOKU (2 sekunde)... Govorite u mikrofon!", "⏺️ 正在录音（2 秒）... 请对着麦克风说话！");
            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));

            if (_recordTimer != null)
            {
                try { _recordTimer.Stop(); } catch { }
            }

            _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _recordTimer.Tick += (s, e) => {
                _recordTimer.Stop();
                byte[] captured;
                lock (_recordLock)
                {
                    _isRecording3s = false;
                    captured = _recordStream != null ? _recordStream.ToArray() : new byte[0];
                }
                _recorded3sAudio = captured;
                HwTestLogger.Info("MIC", string.Format("2s snimak završen: {0} bajtova", captured.Length));

                MicAnalysisResult analysis = AnalyzeRecordedAudio(captured);
                _lastActiveFrames = analysis.ActiveFrames;
                _lastDynamicRangeDb = analysis.DynamicRangeDb;
                _lastAcStdDev = analysis.AcStdDev;
                _analysisSummary = analysis.Summary;

                _btnRecordManual.IsEnabled = true;
                _btnReplay.IsEnabled = captured.Length > 0;

                if (analysis.Passed)
                {
                    _autoVerified = true;
                    _btnPass.IsEnabled = true;
                    _statusText.Text = HwTestLocalization.PickFormat(
                        "✓ AUTOMATSKA PROVERA PROŠLA! (Peak={0:F1} dBFS, Aktivno={1} okvira, Dinamika={2:F1} dB) — reprodukujem i završavam...",
                        "✓ 自动检测通过！(峰值={0:F1} dBFS, 有效帧={1}, 动态范围={2:F1} dB) — 正在回放并完成测试...",
                        analysis.PeakDbfs, analysis.ActiveFrames, analysis.DynamicRangeDb);
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                    HwTestLogger.Info("MIC_AUTO", "PASS: " + analysis.Summary);

                    PlayRecordedAudio();
                    StartAutoPassTimer();
                }
                else
                {
                    _autoVerified = false;
                    _btnPass.IsEnabled = (_peakDbfs > -42.0);
                    // Dozvoljavamo automatski novi pokušaj ako tehničar ponovo govori u mikrofon
                    _recorded3sAudio = null;
                    _consecutiveAboveThreshold = 0;

                    _statusText.Text = HwTestLocalization.PickFormat(
                        "⚠️ AUTOMATSKA PROVERA NIJE USPELA: {0}. Pokušajte ponovo (govorite/kucnite) ili kliknite PASS/FAIL.",
                        "⚠️ 自动检测未通过: {0}。请重试（说话或轻敲）或手动点击 PASS/FAIL。",
                        analysis.Reason);
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    HwTestLogger.Warn("MIC_AUTO", "NOT VERIFIED: " + analysis.Summary);
                }
            };
            _recordTimer.Start();
        }

        private class MicAnalysisResult
        {
            public bool Passed;
            public double PeakDbfs;
            public int ActiveFrames;
            public double DynamicRangeDb;
            public double AcStdDev;
            public string Reason;
            public string Summary;
        }

        private MicAnalysisResult AnalyzeRecordedAudio(byte[] pcm16)
        {
            MicAnalysisResult res = new MicAnalysisResult();
            res.PeakDbfs = -60.0;

            if (pcm16 == null || pcm16.Length < 12000)
            {
                res.Passed = false;
                res.Reason = HwTestLocalization.Pick("Snimak je prekratak ili prazan", "录音过短或为空");
                res.Summary = string.Format("len={0}", pcm16 != null ? pcm16.Length : 0);
                return res;
            }

            int totalSamples = pcm16.Length / 2;
            double sum = 0.0;
            for (int i = 0; i < totalSamples; i++)
            {
                short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                sum += s;
            }
            double dcMean = sum / totalSamples;

            double sumSqAc = 0.0;
            double maxAbsAc = 0.0;
            const int frameSamples = 800; // 50 ms na 16 kHz
            int frameCount = totalSamples / frameSamples;
            double minFrameDb = 0.0;
            double maxFrameDb = -60.0;
            bool firstFrame = true;
            int activeFrames = 0;

            for (int f = 0; f < frameCount; f++)
            {
                double fSumSq = 0.0;
                int offset = f * frameSamples;
                for (int j = 0; j < frameSamples; j++)
                {
                    int idx = (offset + j) * 2;
                    short s = (short)(pcm16[idx] | (pcm16[idx + 1] << 8));
                    double ac = s - dcMean;
                    sumSqAc += ac * ac;
                    fSumSq += ac * ac;
                    double absAc = Math.Abs(ac);
                    if (absAc > maxAbsAc) maxAbsAc = absAc;
                }

                double fRms = Math.Sqrt(fSumSq / frameSamples);
                double fDb = (fRms > 0.5) ? (20.0 * Math.Log10(fRms / 32768.0)) : -60.0;
                if (fDb < -60.0) fDb = -60.0;

                if (firstFrame)
                {
                    minFrameDb = fDb;
                    maxFrameDb = fDb;
                    firstFrame = false;
                }
                else
                {
                    if (fDb < minFrameDb) minFrameDb = fDb;
                    if (fDb > maxFrameDb) maxFrameDb = fDb;
                }

                if (fDb >= -44.0)
                {
                    activeFrames++;
                }
            }

            double acStdDev = Math.Sqrt(sumSqAc / Math.Max(1, totalSamples));
            double peakDbfs = (maxAbsAc > 0.5) ? (20.0 * Math.Log10(maxAbsAc / 32768.0)) : -60.0;
            if (peakDbfs < -60.0) peakDbfs = -60.0;
            double dynamicRange = Math.Max(0.0, maxFrameDb - minFrameDb);

            res.PeakDbfs = peakDbfs;
            res.ActiveFrames = activeFrames;
            res.DynamicRangeDb = dynamicRange;
            res.AcStdDev = acStdDev;
            res.Summary = string.Format(
                "len={0}B peak={1:F1}dBFS activeFrames={2}/{3} dyn={4:F1}dB acStd={5:F1} dcMean={6:F1}",
                pcm16.Length, peakDbfs, activeFrames, frameCount, dynamicRange, acStdDev, dcMean);

            if (peakDbfs < -44.0 || activeFrames < 2)
            {
                res.Passed = false;
                res.Reason = HwTestLocalization.PickFormat("Preslab signal / tišina (Peak={0:F1} dBFS, aktivno={1} okvira)",
                    "信号过弱 / 静音 (峰值={0:F1} dBFS, 有效帧={1})", peakDbfs, activeFrames);
                return res;
            }

            if (acStdDev < 25.0)
            {
                res.Passed = false;
                res.Reason = HwTestLocalization.PickFormat("Ravan signal bez akustične oscilacije (AC StdDev={0:F1})",
                    "信号平直无声学波动 (AC StdDev={0:F1})", acStdDev);
                return res;
            }

            if (dynamicRange < 2.2 && !(maxFrameDb > -30.0 && acStdDev > 200.0 && dynamicRange >= 1.2))
            {
                res.Passed = false;
                res.Reason = HwTestLocalization.PickFormat("Konstantan šum/zujanje bez govorne dinamike ({0:F1} dB)",
                    "存在持续底噪或嗡嗡声，缺乏语音动态变化 ({0:F1} dB)", dynamicRange);
                return res;
            }

            res.Passed = true;
            res.Reason = "OK";
            return res;
        }

        private void StartAutoPassTimer()
        {
            StopAutoPassTimer();
            _autoPassTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
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

        private void PlayRecordedAudio()
        {
            byte[] audio = _recorded3sAudio;
            if (audio == null || audio.Length == 0) return;

            ThreadPool.QueueUserWorkItem((state) => {
                IntPtr hwo = IntPtr.Zero;
                IntPtr pData = IntPtr.Zero;
                IntPtr pHdr = IntPtr.Zero;
                int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));

                try
                {
                    NativeMethods.WAVEFORMATEX wfx = new NativeMethods.WAVEFORMATEX
                    {
                        wFormatTag = NativeMethods.WAVE_FORMAT_PCM,
                        nChannels = 1,
                        nSamplesPerSec = 16000,
                        wBitsPerSample = 16,
                        nBlockAlign = 2,
                        nAvgBytesPerSec = 32000,
                        cbSize = 0
                    };

                    int openRes = NativeMethods.waveOutOpen(out hwo, NativeMethods.WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
                    if (openRes != 0 || hwo == IntPtr.Zero)
                    {
                        HwTestLogger.Error("MIC", "PlayRecordedAudio waveOutOpen MMSYSERR=" + openRes);
                        return;
                    }

                    pData = Marshal.AllocHGlobal(audio.Length);
                    Marshal.Copy(audio, 0, pData, audio.Length);

                    pHdr = Marshal.AllocHGlobal(hdrSize);
                    NativeMethods.WAVEHDR wh = new NativeMethods.WAVEHDR
                    {
                        lpData = pData,
                        dwBufferLength = (uint)audio.Length
                    };
                    Marshal.StructureToPtr(wh, pHdr, false);

                    NativeMethods.waveOutPrepareHeader(hwo, pHdr, (uint)hdrSize);
                    NativeMethods.waveOutWrite(hwo, pHdr, (uint)hdrSize);

                    int waitMs = Math.Max(1500, (int)((audio.Length / 32000.0) * 1000.0) + 300);
                    Thread.Sleep(waitMs);
                }
                catch (Exception ex)
                {
                    HwTestLogger.Error("MIC", "PlayRecordedAudio exception: " + ex.Message);
                }
                finally
                {
                    if (hwo != IntPtr.Zero)
                    {
                        try { NativeMethods.waveOutReset(hwo); } catch { }
                        if (pHdr != IntPtr.Zero)
                        {
                            try { NativeMethods.waveOutUnprepareHeader(hwo, pHdr, (uint)hdrSize); } catch { }
                        }
                        try { NativeMethods.waveOutClose(hwo); } catch { }
                    }
                    if (pHdr != IntPtr.Zero) Marshal.FreeHGlobal(pHdr);
                    if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                }
            });
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
            _isClosing = true;
            Interlocked.Increment(ref _micInitGeneration);
            StopAutoPassTimer();
            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["mic_level_dbfs"] = Math.Round(_peakDbfs, 1);
            Result.Details["recorded_3s"] = (_recorded3sAudio != null && _recorded3sAudio.Length > 0);
            Result.Details["auto_verified"] = _autoVerified;
            Result.Details["active_frames"] = _lastActiveFrames;
            Result.Details["dynamic_range_db"] = Math.Round(_lastDynamicRangeDb, 1);
            Result.Details["ac_stddev"] = Math.Round(_lastAcStdDev, 1);
            SafeCloseDialog(true);
        }

        private void FinishFail(string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            _isClosing = true;
            Interlocked.Increment(ref _micInitGeneration);
            StopAutoPassTimer();
            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = string.IsNullOrEmpty(reason)
                ? HwTestLocalization.Pick("Mikrofon nije prošao test (nema validnog audio signala)", "麦克风测试未通过（未检测到有效音频信号）")
                : HwTestLocalization.PickFormat("Mikrofon ne radi ({0})", "麦克风故障 ({0})", reason);
            Result.Details["mic_level_dbfs"] = Math.Round(_peakDbfs, 1);
            Result.Details["auto_verified"] = false;
            SafeCloseDialog(false);
        }

        private void RequestUefiBiosReboot()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            _isClosing = true;
            Interlocked.Increment(ref _micInitGeneration);
            StopAutoPassTimer();
            if (_recordTimer != null)
            {
                try { _recordTimer.Stop(); } catch { }
                _recordTimer = null;
            }

            Result.Status = TestStatus.Pending;
            Action<string> cb = OnRequestUefiBiosReboot ?? _onRequestUefiBiosReboot;
            try { Topmost = false; } catch { }
            SafeCloseDialog(false);
            if (cb != null)
            {
                cb("mic");
            }
            else
            {
                MessageBox.Show(
                    HwTestLocalization.Pick("Za uključivanje mikrofona restartujte računar u UEFI BIOS (HP: F10, Lenovo: F1, Dell: F2) i omogućite ugrađeni mikrofon.",
                        "要启用麦克风，请重启电脑进入 UEFI BIOS（HP: F10, Lenovo: F1, Dell: F2）并开启内置麦克风。"),
                    HwTestLocalization.Pick("Restart u UEFI BIOS", "重启进入 UEFI BIOS"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}
