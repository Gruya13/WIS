using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class AudioTestWindow : Window
    {
        private readonly int _expectedLeftDigit;
        private readonly int _expectedRightDigit;
        private int _currentChannel = 0; // 0 = Left, 1 = Right
        private int _leftAttempts = 0;
        private int _rightAttempts = 0;
        private int _leftReplays = 0;
        private int _rightReplays = 0;
        private bool _inMusicPhase = true;
        private int _musicGeneration = 0;
        private volatile bool _stopMusic = false;
        private IntPtr _activeWaveOut = IntPtr.Zero;
        private readonly object _waveLock = new object();
        private bool _isClosingOrDone = false;

        private float _savedVolume = 0.5f;
        private bool _savedMute = false;
        private NativeMethods.IAudioEndpointVolume _audioVolume;

        private readonly TextBlock _speakerIndicator;
        private readonly TextBlock _subInstruction;
        private readonly TextBlock _statusLeft;
        private readonly TextBlock _statusRight;
        private readonly WrapPanel _numPanel;
        private readonly Button _btnReplay;
        private readonly StackPanel _musicPanel;
        private readonly TextBlock _musicVolumeText;
        private readonly ProgressBar _musicProgressBar;
        private readonly Button _btnReplayMusic;
        private readonly Button _btnMusicPass;

        public TestItemResult Result { get; private set; }

        public AudioTestWindow()
        {
            Title = "WIS — Test Zvuka (Levi i Desni Zvučnik)";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            Topmost = true;

            Result = new TestItemResult("audio", "Zvuk (Levi / Desni kanal)", "Test nezavisnih stereo kanala zvučnika izgovorom nasumičnih cifara");

            // Izbor nasumičnih cifara
            Random rnd = new Random();
            _expectedLeftDigit = rnd.Next(0, 10);
            do
            {
                _expectedRightDigit = rnd.Next(0, 10);
            } while (_expectedRightDigit == _expectedLeftDigit);

            // Inicijalizacija jačine zvuka na 70%
            SetupAudioVolume();

            // Izgradnja UI-ja
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
                Text = "🔊 TEST STEREO ZVUČNIKA (LEVI I DESNI KANAL)",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(title);

            StackPanel spCh = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _statusLeft = new TextBlock
            {
                Text = "Levi zvučnik: ⏳ Čeka",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 30, 0)
            };
            _statusRight = new TextBlock
            {
                Text = "Desni zvučnik: ⏳ Čeka",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            spCh.Children.Add(_statusLeft);
            spCh.Children.Add(_statusRight);
            spH.Children.Add(spCh);
            hg.Children.Add(spH);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Center Panel
            Border centerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(35),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 650
            };

            StackPanel spCenter = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _speakerIndicator = new TextBlock
            {
                Text = "🎵 PROVERA KVALITETA ZVUKA (30% → 100%)",
                FontSize = 24,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            spCenter.Children.Add(_speakerIndicator);

            _subInstruction = new TextBlock
            {
                Text = "Slušajte melodiju (8 sekundi) — jačina ide od 30% do 100% i ostaje na maksimumu ~3.5s:",
                FontSize = 15,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 20)
            };
            spCenter.Children.Add(_subInstruction);

            // Panel za test muzike (30% -> 100% + zadržavanje na 100%) pre L/R testa
            _musicPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 570,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _musicVolumeText = new TextBlock
            {
                Text = "🔊 Jačina zvuka: 30%",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _musicPanel.Children.Add(_musicVolumeText);

            _musicProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 30,
                Height = 22,
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                Margin = new Thickness(0, 0, 0, 20)
            };
            _musicPanel.Children.Add(_musicProgressBar);

            WrapPanel spMusicBtns = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _btnMusicPass = new Button
            {
                Content = "✓ Zvuk čist (PASS) — Nastavi na L/D",
                Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4),
                IsEnabled = false,
                Opacity = 0.45
            };
            _btnMusicPass.Click += (s, e) => TransitionToChannelTest();
            spMusicBtns.Children.Add(_btnMusicPass);

            Button btnCrackleFail = new Button
            {
                Content = "❌ Zvučnici krče (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4)
            };
            btnCrackleFail.Click += (s, e) => FinishFail("Zvučnici krče ili izobličavaju zvuk (30-100%).");
            spMusicBtns.Children.Add(btnCrackleFail);

            _btnReplayMusic = new Button
            {
                Content = "🔁 Pusti ponovo",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4)
            };
            _btnReplayMusic.Click += (s, e) => StartMusicSweepPhase();
            spMusicBtns.Children.Add(_btnReplayMusic);

            _musicPanel.Children.Add(spMusicBtns);
            spCenter.Children.Add(_musicPanel);

            // Numerička tastatura (0-9) - skrivena dok traje muzikica
            _numPanel = new WrapPanel
            {
                Width = 330,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20),
                Visibility = Visibility.Collapsed
            };

            for (int i = 1; i <= 9; i++)
            {
                _numPanel.Children.Add(CreateDigitButton(i));
            }
            _numPanel.Children.Add(CreateDigitButton(0, 100));
            spCenter.Children.Add(_numPanel);

            // Replay Button
            _btnReplay = new Button
            {
                Content = "🔁 Ponovi izgovor broja (preostalo: 1)",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(18, 10, 18, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _btnReplay.Click += (s, e) => ReplayCurrentDigit();
            spCenter.Children.Add(_btnReplay);

            centerBorder.Child = spCenter;
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
            fgFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button btnFooterFail = new Button
            {
                Content = "❌ Zvučnici krče / ne rade (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnFooterFail.Click += (s, e) => FinishFail("Tehničar označio neispravne/krčeće zvučnike.");
            Grid.SetColumn(btnFooterFail, 0);
            fgFooter.Children.Add(btnFooterFail);

            Button btnSkip = new Button
            {
                Content = "Preskoči test zvuka",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 8, 15, 8)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            Grid.SetColumn(btnSkip, 2);
            fgFooter.Children.Add(btnSkip);

            footer.Child = fgFooter;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            KeyDown += OnWindowKeyDown;
            Closed += (s, e) =>
            {
                StopActivePlayback();
                RestoreAudioVolume();
            };

            Loaded += (s, e) => StartMusicSweepPhase();
        }

        private Button CreateDigitButton(int digit, double width = 100)
        {
            Button btn = new Button
            {
                Content = digit.ToString(),
                Width = width,
                Height = 60,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56)),
                Foreground = Brushes.White,
                Margin = new Thickness(5),
                Tag = digit
            };
            btn.Click += (s, e) => OnDigitSubmitted(digit);
            return btn;
        }

        private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_inMusicPhase) return;

            if (e.Key >= System.Windows.Input.Key.D0 && e.Key <= System.Windows.Input.Key.D9)
            {
                OnDigitSubmitted((int)e.Key - (int)System.Windows.Input.Key.D0);
            }
            else if (e.Key >= System.Windows.Input.Key.NumPad0 && e.Key <= System.Windows.Input.Key.NumPad9)
            {
                OnDigitSubmitted((int)e.Key - (int)System.Windows.Input.Key.NumPad0);
            }
        }

        private void SetMasterVolumeScalarSafe(float level)
        {
            try
            {
                if (_audioVolume != null)
                {
                    if (level < 0.0f) level = 0.0f;
                    if (level > 1.0f) level = 1.0f;
                    Guid empty = Guid.Empty;
                    _audioVolume.SetMute(false, ref empty);
                    _audioVolume.SetMasterVolumeLevelScalar(level, ref empty);
                }
            }
            catch { }
        }

        private void StopActivePlayback()
        {
            _stopMusic = true;
            _musicGeneration++;
            lock (_waveLock)
            {
                if (_activeWaveOut != IntPtr.Zero)
                {
                    try { NativeMethods.waveOutReset(_activeWaveOut); } catch { }
                }
            }
        }

        private void StartMusicSweepPhase()
        {
            StopActivePlayback();
            _stopMusic = false;
            _inMusicPhase = true;
            int myGen = _musicGeneration;

            _musicPanel.Visibility = Visibility.Visible;
            _numPanel.Visibility = Visibility.Collapsed;
            _btnReplay.Visibility = Visibility.Collapsed;
            _btnMusicPass.IsEnabled = false;
            _btnMusicPass.Opacity = 0.45;
            _speakerIndicator.Text = "🎵 PROVERA KVALITETA ZVUKA (30% → 100%)";
            _speakerIndicator.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            _subInstruction.Text = "Slušajte melodiju (8 sekundi) — jačina ide od 30% do 100% i ostaje na maksimumu ~3.5s:";
            _musicVolumeText.Text = "🔊 Jačina zvuka: 30% (pojačavanje...)";
            _musicProgressBar.Value = 30;
            SetMasterVolumeScalarSafe(0.30f);

            byte[] musicPcm = GenerateWarmSweepMusicPcm(8.2);

            // Nit 1: Reprodukcija 8.2s stereo melodije
            System.Threading.ThreadPool.QueueUserWorkItem((state) => {
                PlayWaveOutTracked(musicPcm, 22050, 2, myGen);
            });

            // Nit 2: Precizno pojačavanje po Stopwatch vremenu (4.5s ramp 30%->100%, zatim >=3.3s na 100%)
            System.Threading.ThreadPool.QueueUserWorkItem((state) => {
                try
                {
                    const double rampSec = 4.5;
                    const double holdUntilSec = 7.9;
                    Stopwatch sw = Stopwatch.StartNew();
                    int lastPct = -1;
                    bool lastHolding = false;

                    while (true)
                    {
                        if (_stopMusic || myGen != _musicGeneration) return;
                        double elapsed = sw.Elapsed.TotalSeconds;
                        bool holding = elapsed >= rampSec;
                        double progress = holding ? 1.0 : (elapsed / rampSec);
                        float vol = (float)(0.30 + 0.70 * progress);
                        int pct = (int)Math.Round(vol * 100.0);
                        if (holding) pct = 100;

                        SetMasterVolumeScalarSafe(vol);
                        if (pct != lastPct || holding != lastHolding)
                        {
                            lastPct = pct;
                            lastHolding = holding;
                            string label = holding
                                ? "🔊 Jačina zvuka: 100% — Zadržavanje na maksimumu!"
                                : string.Format("🔊 Jačina zvuka: {0}% (pojačavanje...)", pct);
                            Dispatcher.BeginInvoke(new Action(() => {
                                if (myGen == _musicGeneration && !_stopMusic)
                                {
                                    _musicVolumeText.Text = label;
                                    _musicProgressBar.Value = pct;
                                }
                            }));
                        }

                        if (elapsed >= holdUntilSec) break;
                        System.Threading.Thread.Sleep(50);
                    }

                    // Sačekamo kraj fade-outa melodije na 100% pre nuđenja PASS/FAIL odluke
                    System.Threading.Thread.Sleep(300);
                    if (_stopMusic || myGen != _musicGeneration) return;

                    SetMasterVolumeScalarSafe(0.70f);
                    Dispatcher.BeginInvoke(new Action(() => {
                        if (myGen == _musicGeneration && !_stopMusic)
                        {
                            _musicVolumeText.Text = "✅ Melodija završena (100% testirano) — Izaberite PASS ili FAIL:";
                            _musicProgressBar.Value = 100;
                            _subInstruction.Text = "Da li je zvuk čist na 100%? Kliknite PASS za nastavak na test levog/desnog kanala ili FAIL ako krči:";
                            _btnMusicPass.IsEnabled = true;
                            _btnMusicPass.Opacity = 1.0;
                        }
                    }));
                }
                catch { }
            });
        }

        private void TransitionToChannelTest()
        {
            _inMusicPhase = false;
            _musicPanel.Visibility = Visibility.Collapsed;
            _numPanel.Visibility = Visibility.Visible;
            _btnReplay.Visibility = Visibility.Visible;
            Result.Details["music_sweep_completed"] = true;
            PlayCurrentChannel();
        }

        private static byte[] GenerateWarmSweepMusicPcm(double durationSeconds)
        {
            const int sampleRate = 22050;
            int totalSamples = (int)(sampleRate * durationSeconds);
            byte[] pcm = new byte[totalSamples * 4]; // 16-bit stereo

            // 4-takta prijatna progresija sa basom i arpeggiom (1.25s po taktu = 5.0s)
            double[,] chords = new double[,]
            {
                { 130.81, 261.63, 329.63, 392.00, 523.25 }, // C
                { 123.47, 246.94, 293.66, 392.00, 587.33 }, // G/B
                { 110.00, 220.00, 261.63, 329.63, 523.25 }, // Am
                {  98.00, 196.00, 246.94, 349.23, 587.33 }  // G7
            };

            double chordDur = durationSeconds / 4.0;
            double noteDur = chordDur / 4.0;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = i / (double)sampleRate;
                int cIdx = Math.Min(3, (int)(t / chordDur));
                double tInChord = t - (cIdx * chordDur);
                int nIdx = Math.Min(3, (int)(tInChord / noteDur));
                double tInNote = tInChord - (nIdx * noteDur);

                // Blagi envelope po taktu i noti da nema pucketanja iz samog signala
                double chordEnv = Math.Min(1.0, tInChord / 0.03) * Math.Min(1.0, (chordDur - tInChord) / 0.04);
                double noteEnv = Math.Min(1.0, tInNote / 0.015) * Math.Exp(-tInNote * 4.5);

                double bassFreq = chords[cIdx, 0];
                double padFreq1 = chords[cIdx, 1];
                double padFreq2 = chords[cIdx, 2];
                double arpFreq = chords[cIdx, 1 + nIdx];

                // Bogat ali čist signal (osnovna frekvencija + blagi 2. i 3. harmonik)
                double bass = 0.28 * Math.Sin(2.0 * Math.PI * bassFreq * t)
                            + 0.08 * Math.Sin(2.0 * Math.PI * (bassFreq * 2.0) * t);
                double pad = (0.14 * Math.Sin(2.0 * Math.PI * padFreq1 * t)
                            + 0.12 * Math.Sin(2.0 * Math.PI * padFreq2 * t)) * chordEnv;
                double melody = (0.28 * Math.Sin(2.0 * Math.PI * arpFreq * t)
                               + 0.08 * Math.Sin(2.0 * Math.PI * (arpFreq * 2.0) * t)) * noteEnv;

                double mixL = bass + pad * 0.95 + melody * (0.75 + 0.25 * Math.Sin(t * 2.5));
                double mixR = bass + pad * 0.95 + melody * (0.75 - 0.25 * Math.Sin(t * 2.5));

                // Ukupni master fade-in / fade-out
                double masterEnv = Math.Min(1.0, t / 0.05) * Math.Min(1.0, (durationSeconds - t) / 0.08);
                mixL *= masterEnv * 0.85;
                mixR *= masterEnv * 0.85;

                short sL = (short)Math.Max(-32000, Math.Min(32000, (int)(mixL * 32000.0)));
                short sR = (short)Math.Max(-32000, Math.Min(32000, (int)(mixR * 32000.0)));

                int idx = i * 4;
                pcm[idx] = (byte)(sL & 0xFF);
                pcm[idx + 1] = (byte)((sL >> 8) & 0xFF);
                pcm[idx + 2] = (byte)(sR & 0xFF);
                pcm[idx + 3] = (byte)((sR >> 8) & 0xFF);
            }

            return pcm;
        }

        private void PlayWaveOutTracked(byte[] pcm, uint sampleRate, ushort channels, int expectedGen)
        {
            NativeMethods.WAVEFORMATEX wfx = new NativeMethods.WAVEFORMATEX
            {
                wFormatTag = NativeMethods.WAVE_FORMAT_PCM,
                nChannels = channels,
                nSamplesPerSec = sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0
            };

            IntPtr hwo;
            if (NativeMethods.waveOutOpen(out hwo, NativeMethods.WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, 0) == 0)
            {
                lock (_waveLock) { _activeWaveOut = hwo; }
                IntPtr pData = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, pData, pcm.Length);

                NativeMethods.WAVEHDR wh = new NativeMethods.WAVEHDR
                {
                    lpData = pData,
                    dwBufferLength = (uint)pcm.Length
                };

                NativeMethods.waveOutPrepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh));
                NativeMethods.waveOutWrite(hwo, ref wh, (uint)Marshal.SizeOf(wh));

                int totalMs = (int)((pcm.Length / (double)wfx.nAvgBytesPerSec) * 1000.0) + 120;
                int waited = 0;
                while (waited < totalMs && !_stopMusic && expectedGen == _musicGeneration)
                {
                    System.Threading.Thread.Sleep(50);
                    waited += 50;
                }

                lock (_waveLock)
                {
                    if (_activeWaveOut == hwo) _activeWaveOut = IntPtr.Zero;
                }
                NativeMethods.waveOutReset(hwo);
                NativeMethods.waveOutUnprepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh));
                NativeMethods.waveOutClose(hwo);
                Marshal.FreeHGlobal(pData);
            }
        }

        private void SetupAudioVolume()
        {
            try
            {
                var enumerator = (NativeMethods.IMMDeviceEnumerator)new NativeMethods.MMDeviceEnumeratorComObject();
                NativeMethods.IMMDevice dev;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) == 0 && dev != null)
                {
                    Guid iid = typeof(NativeMethods.IAudioEndpointVolume).GUID;
                    object obj;
                    if (dev.Activate(ref iid, 1, IntPtr.Zero, out obj) == 0 && obj != null)
                    {
                        _audioVolume = (NativeMethods.IAudioEndpointVolume)obj;
                        _audioVolume.GetMasterVolumeLevelScalar(out _savedVolume);
                        _audioVolume.GetMute(out _savedMute);

                        Guid empty = Guid.Empty;
                        _audioVolume.SetMute(false, ref empty);
                        _audioVolume.SetMasterVolumeLevelScalar(0.70f, ref empty);
                    }
                }
            }
            catch { }
        }

        private void RestoreAudioVolume()
        {
            try
            {
                if (_audioVolume != null)
                {
                    Guid empty = Guid.Empty;
                    _audioVolume.SetMute(_savedMute, ref empty);
                    _audioVolume.SetMasterVolumeLevelScalar(_savedVolume, ref empty);
                }
            }
            catch { }
        }

        private void PlayCurrentChannel()
        {
            int digit = (_currentChannel == 0) ? _expectedLeftDigit : _expectedRightDigit;
            bool isLeft = (_currentChannel == 0);

            _speakerIndicator.Text = isLeft ? "◀ SLUŠAJTE LEVI ZVUČNIK" : "SLUŠAJTE DESNI ZVUČNIK ▶";
            _speakerIndicator.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
            _subInstruction.Text = isLeft
                ? "Slušajte broj koji izgovara LEVI zvučnik i unesite ga ispod:"
                : "Slušajte broj koji izgovara DESNI zvučnik i unesite ga ispod:";

            PlayDigitPanned(digit, isLeft);
        }

        private void ReplayCurrentDigit()
        {
            if (_currentChannel == 0)
            {
                if (_leftReplays >= 1) return;
                _leftReplays++;
                _btnReplay.IsEnabled = false;
                _btnReplay.Content = "🔁 Ponovi izgovor (iskorišćeno)";
                PlayCurrentChannel();
            }
            else
            {
                if (_rightReplays >= 1) return;
                _rightReplays++;
                _btnReplay.IsEnabled = false;
                _btnReplay.Content = "🔁 Ponovi izgovor (iskorišćeno)";
                PlayCurrentChannel();
            }
        }

        private void PlayDigitPanned(int digit, bool isLeft)
        {
            System.Threading.ThreadPool.QueueUserWorkItem((state) => {
                try
                {
                    byte[] pcmMono = LoadDigitPcm(digit);
                    if (pcmMono == null || pcmMono.Length == 0) return;

                    // Pretvaramo mono 16-bit PCM (22050 Hz) u stereo 16-bit sa jednim utišanim kanalom
                    byte[] pcmStereo = new byte[pcmMono.Length * 2];
                    for (int i = 0; i < pcmMono.Length; i += 2)
                    {
                        short sample = (short)(pcmMono[i] | (pcmMono[i + 1] << 8));
                        int stereoIdx = i * 2;

                        if (isLeft)
                        {
                            // Left channel gets sample, Right gets 0
                            pcmStereo[stereoIdx] = (byte)(sample & 0xFF);
                            pcmStereo[stereoIdx + 1] = (byte)((sample >> 8) & 0xFF);
                            pcmStereo[stereoIdx + 2] = 0;
                            pcmStereo[stereoIdx + 3] = 0;
                        }
                        else
                        {
                            // Left gets 0, Right gets sample
                            pcmStereo[stereoIdx] = 0;
                            pcmStereo[stereoIdx + 1] = 0;
                            pcmStereo[stereoIdx + 2] = (byte)(sample & 0xFF);
                            pcmStereo[stereoIdx + 3] = (byte)((sample >> 8) & 0xFF);
                        }
                    }

                    PlayWaveOut(pcmStereo, 22050, 2);
                }
                catch { }
            });
        }

        private static byte[] ExtractPcmFromWavBytes(byte[] wav)
        {
            if (wav == null || wav.Length <= 44) return null;
            // Traži RIFF 'data' chunk za robusno čitanje bez obzira na veličinu zaglavlja
            if (wav.Length > 12 && wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F')
            {
                int pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                    if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
                    {
                        int dataStart = pos + 8;
                        int avail = wav.Length - dataStart;
                        int copyLen = (chunkSize > 0 && chunkSize <= avail) ? chunkSize : avail;
                        if (copyLen > 0)
                        {
                            byte[] pcmChunk = new byte[copyLen];
                            Array.Copy(wav, dataStart, pcmChunk, 0, copyLen);
                            return pcmChunk;
                        }
                        break;
                    }
                    if (chunkSize < 0) break;
                    pos += 8 + chunkSize + (chunkSize % 2);
                }
            }

            byte[] pcm = new byte[wav.Length - 44];
            Array.Copy(wav, 44, pcm, 0, pcm.Length);
            return pcm;
        }

        private byte[] LoadDigitPcm(int digit)
        {
            // 1. Prioritet: ugrađeni manifest resurs u WIS.HwTest.exe (radi i standalone bez Assets foldera)
            try
            {
                string resName = string.Format("Assets.digits_en.{0}.wav", digit);
                using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                {
                    if (rs != null)
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            rs.CopyTo(ms);
                            byte[] pcmRes = ExtractPcmFromWavBytes(ms.ToArray());
                            if (pcmRes != null && pcmRes.Length > 0) return pcmRes;
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback: proveri Assets/digits_en/{digit}.wav na disku
            string[] paths = new string[] {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "digits_en", digit + ".wav"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Assets", "digits_en", digit + ".wav")
            };
            foreach (string p in paths)
            {
                if (File.Exists(p))
                {
                    byte[] wav = File.ReadAllBytes(p);
                    byte[] pcmDisk = ExtractPcmFromWavBytes(wav);
                    if (pcmDisk != null && pcmDisk.Length > 0)
                        return pcmDisk;
                }
            }

            // 3. Fallback: System.Speech.Synthesis
            using (var synth = new SpeechSynthesizer())
            using (var ms = new MemoryStream())
            {
                synth.SetOutputToAudioStream(
                    ms,
                    new System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono)
                );
                synth.Speak(digit.ToString());
                synth.SetOutputToNull();
                return ms.ToArray();
            }
        }

        private void PlayWaveOut(byte[] pcm, uint sampleRate, ushort channels)
        {
            NativeMethods.WAVEFORMATEX wfx = new NativeMethods.WAVEFORMATEX
            {
                wFormatTag = NativeMethods.WAVE_FORMAT_PCM,
                nChannels = channels,
                nSamplesPerSec = sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0
            };

            IntPtr hwo;
            if (NativeMethods.waveOutOpen(out hwo, NativeMethods.WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, 0) == 0)
            {
                IntPtr pData = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, pData, pcm.Length);

                NativeMethods.WAVEHDR wh = new NativeMethods.WAVEHDR
                {
                    lpData = pData,
                    dwBufferLength = (uint)pcm.Length
                };

                NativeMethods.waveOutPrepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh));
                NativeMethods.waveOutWrite(hwo, ref wh, (uint)Marshal.SizeOf(wh));

                // Sačekaj reprodukciju
                int durationMs = (int)((pcm.Length / (double)wfx.nAvgBytesPerSec) * 1000.0) + 150;
                System.Threading.Thread.Sleep(durationMs);

                NativeMethods.waveOutReset(hwo);
                NativeMethods.waveOutUnprepareHeader(hwo, ref wh, (uint)Marshal.SizeOf(wh));
                NativeMethods.waveOutClose(hwo);
                Marshal.FreeHGlobal(pData);
            }
        }

        private void OnDigitSubmitted(int digit)
        {
            if (_currentChannel == 0)
            {
                _leftAttempts++;
                if (digit == _expectedLeftDigit)
                {
                    _statusLeft.Text = string.Format("Levi zvučnik: ✓ Tačno ({0})", digit);
                    _statusLeft.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));

                    // Prelazak na desni kanal
                    _currentChannel = 1;
                    _btnReplay.IsEnabled = true;
                    _btnReplay.Content = "🔁 Ponovi izgovor broja (preostalo: 1)";
                    _statusRight.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    PlayCurrentChannel();
                }
                else
                {
                    if (_leftAttempts >= 2)
                    {
                        FinishFail("Netačan broj za levi zvučnik.");
                    }
                    else
                    {
                        MessageBox.Show("Netačan broj za levi zvučnik! Imate još jedan pokušaj.", "Upozorenje", MessageBoxButton.OK, MessageBoxImage.Warning);
                        PlayCurrentChannel();
                    }
                }
            }
            else
            {
                _rightAttempts++;
                if (digit == _expectedRightDigit)
                {
                    _statusRight.Text = string.Format("Desni zvučnik: ✓ Tačno ({0})", digit);
                    _statusRight.Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69));
                    FinishPass();
                }
                else
                {
                    if (_rightAttempts >= 2)
                    {
                        FinishFail("Netačan broj za desni zvučnik.");
                    }
                    else
                    {
                        MessageBox.Show("Netačan broj za desni zvučnik! Imate još jedan pokušaj.", "Upozorenje", MessageBoxButton.OK, MessageBoxImage.Warning);
                        PlayCurrentChannel();
                    }
                }
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
            StopActivePlayback();
            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["left_digit_verified"] = true;
            Result.Details["right_digit_verified"] = true;
            SafeCloseDialog(true);
        }

        private void FinishFail(string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopActivePlayback();
            Result.Status = TestStatus.Fail;
            Result.Reason = reason;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopActivePlayback();
            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test zvuka";
            SafeCloseDialog(false);
        }
    }
}
