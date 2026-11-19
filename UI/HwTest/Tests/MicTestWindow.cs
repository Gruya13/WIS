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

        public TestItemResult Result { get; private set; }

        public MicTestWindow()
        {
            Title = "WIS — Test Mikrofona";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = new TestItemResult("mic", "Mikrofon", "Ispitivanje mikrofona, VU metar i automatska reprodukcija 3s snimka");

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
            DockPanel headerDock = new DockPanel();
            TextBlock title = new TextBlock
            {
                Text = "🎤 TEST MIKROFONA (VU METAR I AUTOMATSKI SNIMAK)",
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
                Text = "Ulazni uređaj: ",
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
                Text = "Recite nešto u mikrofon ili pucnite prstima...",
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
                Text = "Nivo signala: -60 dBFS (Prag za automatsko snimanje: > -44 dBFS)",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 180)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 25)
            };
            spC.Children.Add(_dbfsText);

            StackPanel spButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _btnPass = new Button
            {
                Content = "✓ Potvrdi sada (PASS)",
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
                Content = "✗ Mikrofon ne radi (FAIL)",
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
                Content = "⏺️ Snimi ponovo 2s",
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
                Content = "🔊 Preslušaj snimak 2s",
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

            // Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            Button btnSkip = new Button
            {
                Content = "Preskoči test mikrofona",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            footer.Child = btnSkip;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            Loaded += (s, e) => {
                PopulateMicDevices();
                StartWaveIn();
            };
            Closed += (s, e) => {
                StopWaveIn();
                RestoreCaptureEndpointVolume();
            };
        }

        private class MicDeviceChoice
        {
            public uint DeviceId { get; set; }
            public string Label { get; set; }
            public override string ToString() { return Label; }
        }

        private void PopulateMicDevices()
        {
            if (_micCombo == null) return;
            try
            {
                uint numDevs = NativeMethods.waveInGetNumDevs();
                _suppressMicComboEvent = true;
                _micCombo.Items.Clear();
                _micCombo.Items.Add(new MicDeviceChoice
                {
                    DeviceId = NativeMethods.WAVE_MAPPER,
                    Label = "Podrazumevani mikrofon (Windows Mapper)"
                });

                uint capsSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WAVEINCAPSW));
                for (uint i = 0; i < numDevs; i++)
                {
                    NativeMethods.WAVEINCAPSW caps;
                    string devName = string.Format("Mikrofon #{0}", i + 1);
                    if (NativeMethods.waveInGetDevCaps((IntPtr)i, out caps, capsSize) == 0 && !string.IsNullOrEmpty(caps.szPname))
                    {
                        devName = caps.szPname.Trim();
                    }
                    _micCombo.Items.Add(new MicDeviceChoice
                    {
                        DeviceId = i,
                        Label = string.Format("[{0}] {1}", i + 1, devName)
                    });
                    HwTestLogger.Info("MIC", string.Format("Ulazni audio uređaj #{0}: {1}", i, devName));
                }

                _micCombo.SelectedIndex = 0;
                _suppressMicComboEvent = false;

                if (_micSelectPanel != null)
                {
                    _micSelectPanel.Visibility = (numDevs > 1) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
                _suppressMicComboEvent = false;
            }
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
            _statusText.Foreground = Brushes.White;
            _statusText.Text = "Prebačeno na: " + choice.Label + " — recite nešto u mikrofon...";

            StopWaveIn();
            StartWaveIn();
        }

        private void StartWaveIn()
        {
            try
            {
                uint numDevs = NativeMethods.waveInGetNumDevs();
                HwTestLogger.Info("MIC", "waveInGetNumDevs=" + numDevs + " | SelectedDeviceId=" + _selectedDeviceId);
                if (numDevs == 0)
                {
                    _statusText.Text = "⚠ Nije detektovan nijedan ulazni audio uređaj (mikrofon).";
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                    return;
                }

                TryUnmuteAndLogCaptureEndpoint();

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

                if (openRes != 0 || _hwi == IntPtr.Zero)
                {
                    string err = string.Format("waveInOpen neuspešan (MMSYSERR={0}). Proverite privatnost mikrofona u Windowsu.", openRes);
                    HwTestLogger.Error("MIC", err);
                    _statusText.Text = "⚠ " + err;
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
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
                    _statusText.Text = string.Format("⚠ waveInStart neuspešan (code={0}).", sRes);
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                    return;
                }

                _btnRecordManual.IsEnabled = true;
                _isClosing = false;
                _captureThread = new Thread(CaptureLoop) { IsBackground = true };
                _captureThread.Start();
            }
            catch (Exception ex)
            {
                HwTestLogger.Error("MIC", "StartWaveIn exception: " + ex.Message);
                _statusText.Text = "⚠ Greška pri pokretanju mikrofona: " + ex.Message;
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
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

        private void UpdateVuMeter(double dbfs)
        {
            if (dbfs > _peakDbfs) _peakDbfs = dbfs;
            _vuMeter.Value = dbfs;
            _dbfsText.Text = string.Format("Nivo signala: {0:F1} dBFS | Peak: {1:F1} dBFS (Prag: > -44 dBFS)", dbfs, _peakDbfs);

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
            _statusText.Text = "⏺️ SNIMANJE U TOKU (2 sekunde)... Govorite u mikrofon!";
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
                    _statusText.Text = string.Format(
                        "✓ AUTOMATSKA PROVERA PROŠLA! (Peak={0:F1} dBFS, Aktivno={1} okvira, Dinamika={2:F1} dB) — reprodukujem i završavam...",
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

                    _statusText.Text = string.Format(
                        "⚠️ AUTOMATSKA PROVERA NIJE USPELA: {0}. Pokušajte ponovo (govorite/kucnite) ili kliknite PASS/FAIL.",
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
                res.Reason = "Snimak je prekratak ili prazan";
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
                res.Reason = string.Format("Preslab signal / tišina (Peak={0:F1} dBFS, aktivno={1} okvira)", peakDbfs, activeFrames);
                return res;
            }

            if (acStdDev < 25.0)
            {
                res.Passed = false;
                res.Reason = string.Format("Ravan signal bez akustične oscilacije (AC StdDev={0:F1})", acStdDev);
                return res;
            }

            if (dynamicRange < 2.2 && !(maxFrameDb > -30.0 && acStdDev > 200.0 && dynamicRange >= 1.2))
            {
                res.Passed = false;
                res.Reason = string.Format("Konstantan šum/zujanje bez govorne dinamike ({0:F1} dB)", dynamicRange);
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
            StopAutoPassTimer();
            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = string.IsNullOrEmpty(reason)
                ? "Mikrofon nije prošao test (nema validnog audio signala)"
                : ("Mikrofon ne radi (" + reason + ")");
            Result.Details["mic_level_dbfs"] = Math.Round(_peakDbfs, 1);
            Result.Details["auto_verified"] = false;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopAutoPassTimer();
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test mikrofona";
            SafeCloseDialog(false);
        }
    }
}
