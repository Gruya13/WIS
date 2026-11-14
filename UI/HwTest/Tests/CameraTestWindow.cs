using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class CameraTestWindow : Window
    {
        private readonly bool _hasIrCamera;
        private readonly string _webcamName;
        private readonly string _irCameraName;

        private System.Windows.Controls.Image _videoImage;
        private Grid _videoStageGrid;
        private Canvas _faceOverlayCanvas;
        private WriteableBitmap _writeableBmp;
        private int _currentVideoWidth = 0;
        private int _currentVideoHeight = 0;

        private TextBlock _statusText;
        private TextBlock _subTitleText;
        private TextBlock _qrDetectionBanner;
        private Border _irScanBanner;
        private TextBlock _irScanText;
        private ProgressBar _irScanProgress;
        private Button _btnConfirmIr;
        private Button _btnFailIr;
        private Button _btnFailRgb;
        private Button _btnSkip;
        private Button _btnSwitchIr;
        private Border _qrGuideBox;
        private TextBlock _qrGuideLabel;
        private DispatcherTimer _autoPassTimer;
        private DispatcherTimer _autoSwitchIrTimer;
        private bool _isClosingOrDone = false;

        private Thread _workerThread;
        private volatile bool _isRunning = false;
        private volatile bool _switchToIrRequested = false;
        private int _frameCounter = 0;
        private bool _qrPassed = false;
        private bool _irPassed = false;
        private string _irMethodNote = "";
        private string _activeCameraFriendlyName = "";
        private string _activeCameraFormat = "";

        private int _irFaceConfirmFrames = 0;
        private int _irFaceMissStreak = 0;
        private const int RequiredIrFaceFrames = 3;
        private volatile bool _irFaceConfirmed = false;
        private double _scanLinePhase = 0.0;

        public bool InIrMode { get; private set; }
        public TestItemResult Result { get; private set; }

        private struct PixelBox
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        private class FaceLandmarksData
        {
            public PixelBox Box;
            public bool Confirmed;
            public int ConfirmCount;
            public int RequiredCount;
            public string DetectorSource;
            public System.Windows.Point LeftEye;
            public System.Windows.Point RightEye;
            public System.Windows.Point[] LeftEyebrow;
            public System.Windows.Point[] RightEyebrow;
            public System.Windows.Point NoseBridgeTop;
            public System.Windows.Point NoseTip;
            public System.Windows.Point NoseLeftWing;
            public System.Windows.Point NoseRightWing;
            public System.Windows.Point MouthLeft;
            public System.Windows.Point MouthRight;
            public System.Windows.Point MouthTop;
            public System.Windows.Point MouthBottom;
            public System.Windows.Point[] JawContour;
        }

        private enum VideoPixelKind
        {
            Unknown,
            Rgb32,
            Nv12,
            Yuy2,
            L8,
            L16
        }

        public CameraTestWindow(bool hasIrCamera, string webcamName, string irCameraName)
        {
            _hasIrCamera = hasIrCamera;
            _webcamName = webcamName ?? "Web Kamera";
            _irCameraName = irCameraName ?? "IR Kamera";

            Result = new TestItemResult("camera", "Kamera (QR kod i IR)", "Ispitivanje RGB web kamere očitavanjem QR koda i proverom IR senzora lica");

            Title = "WIS — Test Kamere";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 28));
            Topmost = true;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });

            // Header
            Border header = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            Grid hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel spH = new StackPanel();
            _statusText = new TextBlock
            {
                Text = "📷 Usmerite odštampani QR kod (WIS-CAMTEST-v1) ispred kamere...",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176))
            };
            spH.Children.Add(_statusText);

            _subTitleText = new TextBlock
            {
                Text = string.Format("Uređaj: {0}{1}", _webcamName, _hasIrCamera ? (" | IR kamera: " + _irCameraName) : ""),
                FontSize = 13,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 170)),
                Margin = new Thickness(0, 4, 0, 0)
            };
            spH.Children.Add(_subTitleText);
            hg.Children.Add(spH);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Center Video Area: Viewbox drzi sliku i Canvas sa crtama lica 1:1 poravnate u svakoj rezoluciji
            Grid centerGrid = new Grid();
            Viewbox stageViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _videoStageGrid = new Grid
            {
                Width = 640,
                Height = 480,
                ClipToBounds = true
            };
            _videoImage = new System.Windows.Controls.Image
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Stretch = Stretch.Fill
            };
            _videoStageGrid.Children.Add(_videoImage);

            _faceOverlayCanvas = new Canvas
            {
                Width = 640,
                Height = 480,
                Background = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };
            _videoStageGrid.Children.Add(_faceOverlayCanvas);

            stageViewbox.Child = _videoStageGrid;
            centerGrid.Children.Add(stageViewbox);

            // Vidljiv kvadrat / nišan na ekranu za pozicioniranje QR koda
            double guideSize = SystemParameters.PrimaryScreenHeight <= 820 ? 390.0 : 480.0;
            Border qrGuideBox = new Border
            {
                Name = "QrGuideBox",
                Width = guideSize,
                Height = guideSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)),
                BorderThickness = new Thickness(5),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 78, 201, 176)),
                IsHitTestVisible = false
            };
            StackPanel spGuide = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14)
            };
            TextBlock tbGuide = new TextBlock
            {
                Text = "Postavite QR kod ovde",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(175, 20, 22, 26)),
                Padding = new Thickness(14, 6, 14, 6),
                TextAlignment = TextAlignment.Center
            };
            spGuide.Children.Add(tbGuide);
            qrGuideBox.Child = spGuide;
            _qrGuideBox = qrGuideBox;
            _qrGuideLabel = tbGuide;
            centerGrid.Children.Add(qrGuideBox);

            // Banner za detekciju QR koda
            _qrDetectionBanner = new TextBlock
            {
                Text = "✓ QR KOD USPEŠNO OČITAN: WIS-CAMTEST-v1",
                FontSize = 22,
                FontWeight = FontWeights.ExtraBold,
                Foreground = System.Windows.Media.Brushes.White,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 40, 167, 69)),
                Padding = new Thickness(25, 12, 25, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 40),
                Visibility = Visibility.Collapsed
            };
            centerGrid.Children.Add(_qrDetectionBanner);

            // Banner za očitavanje lica u IR fazi
            _irScanBanner = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 18, 24, 34)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(22, 12, 22, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26),
                Visibility = Visibility.Collapsed
            };
            StackPanel spIr = new StackPanel { MinWidth = 420 };
            _irScanText = new TextBlock
            {
                Text = "👁️ IR SKENIRANJE LICA: Pogledajte pravo u kameru...",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            spIr.Children.Add(_irScanText);
            _irScanProgress = new ProgressBar
            {
                Height = 14,
                Minimum = 0,
                Maximum = RequiredIrFaceFrames,
                Value = 0,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 205, 65)),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 50, 60))
            };
            spIr.Children.Add(_irScanProgress);
            _irScanBanner.Child = spIr;
            centerGrid.Children.Add(_irScanBanner);

            Grid.SetRow(centerGrid, 1);
            root.Children.Add(centerGrid);

            // Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 34, 40)),
                Padding = new Thickness(25, 12, 25, 12)
            };
            StackPanel spF = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            if (_hasIrCamera)
            {
                _btnSwitchIr = new Button
                {
                    Content = "👁️ Prebaci na test IR kamere",
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204)),
                    Foreground = System.Windows.Media.Brushes.White,
                    Padding = new Thickness(15, 8, 15, 8),
                    Margin = new Thickness(0, 0, 12, 0),
                    IsEnabled = false
                };
                _btnSwitchIr.Click += (s, e) => SwitchToIrMode();
                spF.Children.Add(_btnSwitchIr);

                _btnConfirmIr = new Button
                {
                    Content = "✓ Potvrdi IR kameru (PASS)",
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)),
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(18, 8, 18, 8),
                    Margin = new Thickness(0, 0, 12, 0),
                    IsEnabled = false,
                    Visibility = Visibility.Collapsed
                };
                _btnConfirmIr.Click += (s, e) =>
                {
                    _irPassed = true;
                    if (string.IsNullOrEmpty(_irMethodNote)) _irMethodNote = "Ručna potvrda servisera";
                    FinishPass();
                };
                spF.Children.Add(_btnConfirmIr);

                _btnFailIr = new Button
                {
                    Content = "✗ IR senzor ne radi (FAIL)",
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 50, 62)),
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(16, 8, 16, 8),
                    Margin = new Thickness(0, 0, 12, 0),
                    Visibility = Visibility.Collapsed
                };
                _btnFailIr.Click += (s, e) => FinishFail("IR kamera ne očitava lice ili ne daje sliku (prijavio serviser)");
                spF.Children.Add(_btnFailIr);
            }

            _btnFailRgb = new Button
            {
                Content = "✗ Kamera ne radi / loša slika (FAIL)",
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 50, 62)),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 12, 0)
            };
            _btnFailRgb.Click += (s, e) => FinishFail("Kamera ne daje ispravnu sliku ili ne radi (prijavio serviser)");
            spF.Children.Add(_btnFailRgb);

            _btnSkip = new Button
            {
                Content = "Preskoči test kamere",
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 85, 95)),
                Foreground = System.Windows.Media.Brushes.White,
                Padding = new Thickness(15, 8, 15, 8)
            };
            _btnSkip.Click += (s, e) => FinishSkip();
            spF.Children.Add(_btnSkip);

            footer.Child = spF;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            Loaded += (s, e) => StartCameraWorker();
            Closed += (s, e) =>
            {
                StopTimers();
                StopCameraWorker();
            };
        }

        private void StartCameraWorker()
        {
            _isRunning = true;
            _workerThread = new Thread(CameraCaptureLoop);
            _workerThread.IsBackground = true;
            try { _workerThread.SetApartmentState(ApartmentState.MTA); } catch { }
            _workerThread.Start();
        }

        private void StopCameraWorker()
        {
            _isRunning = false;
            if (_workerThread != null && _workerThread.IsAlive)
            {
                try { _workerThread.Join(1800); } catch { }
            }
        }

        private void UpdateUiStatus(string text, System.Windows.Media.Color color)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => {
                    _statusText.Text = text;
                    _statusText.Foreground = new SolidColorBrush(color);
                }));
            }
            catch { }
        }

        private void UpdateSubTitleUi(string text)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => {
                    if (_subTitleText != null) _subTitleText.Text = text;
                }));
            }
            catch { }
        }

        private static bool IsIrDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("IR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Infrared", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Hello", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Biometric", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<KeyValuePair<NativeMethods.IMFActivate, string>> EnumerateMfDevices(bool sensorCategoryOnly)
        {
            var result = new List<KeyValuePair<NativeMethods.IMFActivate, string>>();
            NativeMethods.IMFAttributes attrs = null;
            IntPtr pSources = IntPtr.Zero;
            try
            {
                int hrAttr = NativeMethods.MFCreateAttributes(out attrs, sensorCategoryOnly ? 2u : 1u);
                if (hrAttr != 0 || attrs == null) return result;

                Guid gSrcType = NativeMethods.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
                Guid gVidCap = NativeMethods.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
                attrs.SetGUID(ref gSrcType, ref gVidCap);

                if (sensorCategoryOnly)
                {
                    Guid gCatAttr = NativeMethods.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY;
                    Guid gSensorCat = NativeMethods.KSCATEGORY_SENSOR_CAMERA;
                    attrs.SetGUID(ref gCatAttr, ref gSensorCat);
                }

                uint count = 0;
                int hrEnum = NativeMethods.MFEnumDeviceSources(attrs, out pSources, out count);
                if (hrEnum != 0 || count == 0 || pSources == IntPtr.Zero) return result;

                for (int i = 0; i < (int)count; i++)
                {
                    IntPtr pAct = Marshal.ReadIntPtr(pSources, i * IntPtr.Size);
                    if (pAct == IntPtr.Zero) continue;

                    NativeMethods.IMFActivate act = (NativeMethods.IMFActivate)Marshal.GetObjectForIUnknown(pAct);
                    Marshal.Release(pAct);

                    string friendlyName = (sensorCategoryOnly ? "IR Sensor #" : "Kamera #") + (i + 1);
                    try
                    {
                        Guid gName = NativeMethods.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;
                        uint nameLen = 0;
                        if (act.GetStringLength(ref gName, out nameLen) == 0 && nameLen > 0)
                        {
                            System.Text.StringBuilder sb = new System.Text.StringBuilder((int)nameLen + 2);
                            uint dummyLen;
                            if (act.GetString(ref gName, sb, (uint)sb.Capacity, out dummyLen) == 0)
                            {
                                friendlyName = sb.ToString();
                            }
                        }
                    }
                    catch { }

                    result.Add(new KeyValuePair<NativeMethods.IMFActivate, string>(act, friendlyName));
                }
            }
            catch { }
            finally
            {
                if (pSources != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(pSources); } catch { } }
                if (attrs != null) { try { Marshal.ReleaseComObject(attrs); } catch { } }
            }
            return result;
        }

        private static bool TryConfigureStreamFormat(
            NativeMethods.IMFSourceReader reader,
            uint streamIndex,
            Guid subtype,
            VideoPixelKind pixelKind,
            out VideoPixelKind chosenKind,
            out string formatLabel)
        {
            chosenKind = VideoPixelKind.Unknown;
            formatLabel = "";
            NativeMethods.IMFMediaType targetType = null;
            try
            {
                if (NativeMethods.MFCreateMediaType(out targetType) != 0 || targetType == null) return false;

                Guid gMajor = NativeMethods.MF_MT_MAJOR_TYPE;
                Guid gVideo = NativeMethods.MFMediaType_Video;
                targetType.SetGUID(ref gMajor, ref gVideo);

                Guid gSub = NativeMethods.MF_MT_SUBTYPE;
                targetType.SetGUID(ref gSub, ref subtype);

                int hrSet = reader.SetCurrentMediaType(streamIndex, IntPtr.Zero, targetType);
                if (hrSet == 0)
                {
                    chosenKind = pixelKind;
                    formatLabel = pixelKind.ToString().ToUpperInvariant();
                    return true;
                }
            }
            catch { }
            finally
            {
                if (targetType != null) { try { Marshal.ReleaseComObject(targetType); } catch { } }
            }
            return false;
        }

        private static VideoPixelKind DetectNativePixelKind(Guid sub)
        {
            if (sub == NativeMethods.MFVideoFormat_RGB32) return VideoPixelKind.Rgb32;
            if (sub == NativeMethods.MFVideoFormat_NV12) return VideoPixelKind.Nv12;
            if (sub == NativeMethods.MFVideoFormat_YUY2) return VideoPixelKind.Yuy2;
            if (sub == NativeMethods.MFVideoFormat_L8) return VideoPixelKind.L8;
            if (sub == NativeMethods.MFVideoFormat_L16) return VideoPixelKind.L16;
            return VideoPixelKind.Unknown;
        }

        private void CameraCaptureLoop()
        {
            bool mfStarted = false;
            try
            {
                int hrStart = NativeMethods.MFStartup(0x00020070, 0);
                if (hrStart != 0)
                {
                    HwTestLogger.Warn("CAMERA", string.Format("MFStartup(v2) vratio 0x{0:X8}, pokušavam v1...", hrStart));
                    hrStart = NativeMethods.MFStartup(0x00010070, 0);
                }
                if (hrStart != 0)
                {
                    string msg = string.Format("MFStartup neuspešan (HRESULT=0x{0:X8})", hrStart);
                    HwTestLogger.Error("CAMERA", msg);
                    UpdateUiStatus("⚠ " + msg, System.Windows.Media.Color.FromRgb(220, 53, 69));
                    return;
                }
                mfStarted = true;

                QrDecoderProxy qrProxy = null;
                bool qrEnabled = false;
                try
                {
                    qrProxy = new QrDecoderProxy();
                    qrEnabled = true;
                }
                catch (Exception zxEx)
                {
                    HwTestLogger.Warn("CAMERA", "ZXing QR dekoder nije dostupan: " + zxEx.Message);
                }

                WinRtFaceDetectorProxy winRtFace = null;

                // Spoljna petlja omogucava cisto prebacivanje iz RGB kamere na IR kameru bez gasenja niti
                while (_isRunning)
                {
                    bool wantIr = InIrMode;
                    _switchToIrRequested = false;

                    if (wantIr)
                    {
                        // Eksplicitno oslobađanje COM RCW referenci i kratak predah da kompozitna UVC RGB+IR kamera oslobodi interfejs
                        try
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                        catch { }
                        Thread.Sleep(250);
                    }

                    NativeMethods.IMFAttributes readerAttrs = null;
                    NativeMethods.IMFActivate selectedActivate = null;
                    NativeMethods.IMFMediaSource mediaSource = null;
                    NativeMethods.IMFSourceReader reader = null;
                    NativeMethods.IMFMediaType currentType = null;

                    try
                    {
                        HwTestLogger.Info("CAMERA", wantIr ? "Pokrećem IR kamera sesiju za očitavanje lica..." : ("Pokrećem RGB kamera sesiju za: " + _webcamName));

                        var vidCapList = EnumerateMfDevices(false);
                        var sensorList = wantIr ? EnumerateMfDevices(true) : new List<KeyValuePair<NativeMethods.IMFActivate, string>>();

                        for (int i = 0; i < vidCapList.Count; i++)
                            HwTestLogger.Info("CAMERA", string.Format("VidCap [{0}]: {1}", i, vidCapList[i].Value));
                        for (int i = 0; i < sensorList.Count; i++)
                            HwTestLogger.Info("CAMERA", string.Format("SensorCam [{0}]: {1}", i, sensorList[i].Value));

                        string activeName = "";
                        bool usedIrHardwareDevice = false;

                        if (wantIr)
                        {
                            // 1. Traži IR uređaj u standardnim VidCap kamerama
                            int irIdx = -1;
                            for (int i = 0; i < vidCapList.Count; i++)
                            {
                                if (IsIrDeviceName(vidCapList[i].Value))
                                {
                                    irIdx = i;
                                    break;
                                }
                            }
                            if (irIdx >= 0)
                            {
                                selectedActivate = vidCapList[irIdx].Key;
                                activeName = vidCapList[irIdx].Value;
                                usedIrHardwareDevice = true;
                            }
                            else if (sensorList.Count > 0)
                            {
                                // 2. KSCATEGORY_SENSOR_CAMERA uređaj (čest na Windows Hello IR senzorima)
                                int sIdx = 0;
                                for (int i = 0; i < sensorList.Count; i++)
                                {
                                    if (IsIrDeviceName(sensorList[i].Value)) { sIdx = i; break; }
                                }
                                selectedActivate = sensorList[sIdx].Key;
                                activeName = sensorList[sIdx].Value;
                                usedIrHardwareDevice = true;
                            }
                            else if (vidCapList.Count > 0)
                            {
                                // 3. Fallback na primarnu kameru ako IR pin ili deljeni senzor deli stream
                                selectedActivate = vidCapList[0].Key;
                                activeName = vidCapList[0].Value + " (Deljeni IR/Lice senzor)";
                                usedIrHardwareDevice = false;
                            }
                        }
                        else
                        {
                            if (vidCapList.Count > 0)
                            {
                                int rgbIdx = 0;
                                for (int i = 0; i < vidCapList.Count; i++)
                                {
                                    if (!IsIrDeviceName(vidCapList[i].Value))
                                    {
                                        rgbIdx = i;
                                        break;
                                    }
                                }
                                selectedActivate = vidCapList[rgbIdx].Key;
                                activeName = vidCapList[rgbIdx].Value;
                            }
                        }

                        // Oslobodi ostale nekorišćene activate objekte
                        foreach (var kv in vidCapList)
                        {
                            if (!object.ReferenceEquals(kv.Key, selectedActivate) && kv.Key != null)
                            {
                                try { Marshal.ReleaseComObject(kv.Key); } catch { }
                            }
                        }
                        foreach (var kv in sensorList)
                        {
                            if (!object.ReferenceEquals(kv.Key, selectedActivate) && kv.Key != null)
                            {
                                try { Marshal.ReleaseComObject(kv.Key); } catch { }
                            }
                        }

                        if (selectedActivate == null)
                        {
                            UpdateUiStatus("⚠ Nije pronađena nijedna dostupna kamera u Media Foundation.", System.Windows.Media.Color.FromRgb(220, 53, 69));
                            return;
                        }

                        _activeCameraFriendlyName = activeName;
                        HwTestLogger.Info("CAMERA", "Aktivirana kamera: " + activeName);

                        Guid iidSource = NativeMethods.IID_IMFMediaSource;
                        object sourceObj;
                        int hrAct = selectedActivate.ActivateObject(ref iidSource, out sourceObj);
                        if ((hrAct != 0 || sourceObj == null) && wantIr && usedIrHardwareDevice)
                        {
                            // Ako je posvećeni IR senzor zaključan (Windows Hello), fallback na primarnu kameru
                            HwTestLogger.Warn("CAMERA", string.Format("Posvećena IR kamera '{0}' zauzeta/odbila aktivaciju (0x{1:X8}); prelazim na fallback senzor.", activeName, hrAct));
                            try { Marshal.ReleaseComObject(selectedActivate); } catch { }
                            selectedActivate = null;
                            var retryVidCap = EnumerateMfDevices(false);
                            for (int i = 0; i < retryVidCap.Count; i++)
                            {
                                if (i == 0 && !IsIrDeviceName(retryVidCap[i].Value))
                                {
                                    selectedActivate = retryVidCap[i].Key;
                                    activeName = retryVidCap[i].Value + " (IR Fallback)";
                                }
                                else if (selectedActivate == null && retryVidCap.Count > 0)
                                {
                                    selectedActivate = retryVidCap[0].Key;
                                    activeName = retryVidCap[0].Value + " (IR Fallback)";
                                }
                                if (!object.ReferenceEquals(retryVidCap[i].Key, selectedActivate))
                                {
                                    try { Marshal.ReleaseComObject(retryVidCap[i].Key); } catch { }
                                }
                            }
                            if (selectedActivate != null)
                            {
                                hrAct = selectedActivate.ActivateObject(ref iidSource, out sourceObj);
                                usedIrHardwareDevice = false;
                            }
                            else
                            {
                                sourceObj = null;
                            }
                        }

                        if (hrAct != 0 || sourceObj == null)
                        {
                            string msg = string.Format("Aktivacija kamere '{0}' nije uspela (0x{1:X8}).", activeName, hrAct);
                            HwTestLogger.Error("CAMERA", msg);
                            UpdateUiStatus("⚠ " + msg, System.Windows.Media.Color.FromRgb(220, 53, 69));
                            return;
                        }
                        mediaSource = (NativeMethods.IMFMediaSource)sourceObj;

                        if (NativeMethods.MFCreateAttributes(out readerAttrs, 1) == 0 && readerAttrs != null)
                        {
                            Guid gVideoProc = NativeMethods.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                            readerAttrs.SetUINT32(ref gVideoProc, 1);
                        }

                        int hrReader = NativeMethods.MFCreateSourceReaderFromMediaSource(mediaSource, readerAttrs, out reader);
                        if (hrReader != 0 || reader == null)
                        {
                            hrReader = NativeMethods.MFCreateSourceReaderFromMediaSource(mediaSource, null, out reader);
                        }
                        if (hrReader != 0 || reader == null)
                        {
                            string msg = string.Format("Kreiranje SourceReader-a nije uspelo (0x{0:X8}).", hrReader);
                            HwTestLogger.Error("CAMERA", msg);
                            UpdateUiStatus("⚠ " + msg, System.Windows.Media.Color.FromRgb(220, 53, 69));
                            return;
                        }

                        uint streamIndex = NativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM;
                        reader.SetStreamSelection(streamIndex, true);

                        VideoPixelKind pixelKind = VideoPixelKind.Unknown;
                        string fmtLabel = "";

                        // Ako je u pitanju multi-stream kamera u IR modu bez zasebnog uređaja, proveri stream 1 za L8/L16/NV12 IR stream
                        if (wantIr && !usedIrHardwareDevice)
                        {
                            NativeMethods.IMFMediaType st1Type = null;
                            if (reader.GetNativeMediaType(1, 0, out st1Type) == 0 && st1Type != null)
                            {
                                Guid sub1;
                                Guid gSubType = NativeMethods.MF_MT_SUBTYPE;
                                if (st1Type.GetGUID(ref gSubType, out sub1) == 0)
                                {
                                    VideoPixelKind k1 = DetectNativePixelKind(sub1);
                                    if (k1 == VideoPixelKind.L8 || k1 == VideoPixelKind.L16 || k1 == VideoPixelKind.Nv12)
                                    {
                                        streamIndex = 1;
                                        reader.SetStreamSelection(0, false);
                                        reader.SetStreamSelection(1, true);
                                        usedIrHardwareDevice = true;
                                        activeName = _webcamName + " (IR Stream #1)";
                                        HwTestLogger.Info("CAMERA", "Pronađen sekundarni IR video stream #1 na istoj kameri (" + k1 + ")");
                                    }
                                }
                                try { Marshal.ReleaseComObject(st1Type); } catch { }
                            }
                        }

                        // Pokušavamo RGB32 prvo, a za IR kamere bez RGB32 podrške direktno NV12, YUY2, L8, L16
                        if (wantIr && usedIrHardwareDevice)
                        {
                            if (!TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_L8, VideoPixelKind.L8, out pixelKind, out fmtLabel) &&
                                !TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_RGB32, VideoPixelKind.Rgb32, out pixelKind, out fmtLabel) &&
                                !TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_NV12, VideoPixelKind.Nv12, out pixelKind, out fmtLabel) &&
                                !TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_YUY2, VideoPixelKind.Yuy2, out pixelKind, out fmtLabel) &&
                                !TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_L16, VideoPixelKind.L16, out pixelKind, out fmtLabel))
                            {
                                pixelKind = VideoPixelKind.Unknown;
                            }
                        }
                        else
                        {
                            if (!TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_RGB32, VideoPixelKind.Rgb32, out pixelKind, out fmtLabel) &&
                                !TryConfigureStreamFormat(reader, 0, NativeMethods.MFVideoFormat_RGB32, VideoPixelKind.Rgb32, out pixelKind, out fmtLabel))
                            {
                                if (TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_NV12, VideoPixelKind.Nv12, out pixelKind, out fmtLabel) ||
                                    TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_YUY2, VideoPixelKind.Yuy2, out pixelKind, out fmtLabel) ||
                                    TryConfigureStreamFormat(reader, streamIndex, NativeMethods.MFVideoFormat_L8, VideoPixelKind.L8, out pixelKind, out fmtLabel))
                                {
                                    // OK
                                }
                            }
                            else
                            {
                                // Proveri koji streamIndex je prosao
                                if (reader.GetCurrentMediaType(streamIndex, out currentType) != 0)
                                {
                                    streamIndex = 0;
                                }
                                else if (currentType != null)
                                {
                                    try { Marshal.ReleaseComObject(currentType); } catch { }
                                    currentType = null;
                                }
                            }
                        }

                        if (pixelKind == VideoPixelKind.Unknown)
                        {
                            if (reader.GetCurrentMediaType(streamIndex, out currentType) == 0 && currentType != null)
                            {
                                Guid sub;
                                Guid gSubType = NativeMethods.MF_MT_SUBTYPE;
                                if (currentType.GetGUID(ref gSubType, out sub) == 0)
                                {
                                    pixelKind = DetectNativePixelKind(sub);
                                    fmtLabel = pixelKind.ToString().ToUpperInvariant();
                                }
                            }
                        }

                        if (pixelKind == VideoPixelKind.Unknown)
                        {
                            string msg = string.Format("Kamera '{0}' ne podržava nijedan prepoznat nekomprimovan video format.", activeName);
                            HwTestLogger.Error("CAMERA", msg);
                            UpdateUiStatus("⚠ " + msg, System.Windows.Media.Color.FromRgb(220, 53, 69));
                            return;
                        }

                        int width = 640;
                        int height = 480;
                        if (currentType == null)
                        {
                            reader.GetCurrentMediaType(streamIndex, out currentType);
                        }
                        if (currentType != null)
                        {
                            Guid gSize = NativeMethods.MF_MT_FRAME_SIZE;
                            ulong packedSize = 0;
                            if (currentType.GetUINT64(ref gSize, out packedSize) == 0 && packedSize != 0)
                            {
                                int w = (int)(packedSize >> 32);
                                int h = (int)(packedSize & 0xFFFFFFFF);
                                if (w > 0 && h > 0 && w <= 4096 && h <= 4096)
                                {
                                    width = w;
                                    height = h;
                                }
                            }
                        }

                        _activeCameraFormat = fmtLabel;
                        HwTestLogger.Info("CAMERA", string.Format("Video stream aktivan [{0}]: {1} ({2}x{3}, {4})",
                            wantIr ? "IR" : "RGB", activeName, width, height, fmtLabel));

                        UpdateSubTitleUi(string.Format("Aktivan uređaj ({0}): {1} [{2}x{3} {4}]",
                            wantIr ? "IR režim" : "RGB režim", activeName, width, height, fmtLabel));

                        Dispatcher.BeginInvoke(new Action(() => {
                            _currentVideoWidth = width;
                            _currentVideoHeight = height;
                            _writeableBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                            _videoImage.Source = _writeableBmp;
                            if (_videoStageGrid != null)
                            {
                                _videoStageGrid.Width = width;
                                _videoStageGrid.Height = height;
                            }
                            if (_faceOverlayCanvas != null)
                            {
                                _faceOverlayCanvas.Width = width;
                                _faceOverlayCanvas.Height = height;
                                _faceOverlayCanvas.Children.Clear();
                            }
                        }));

                        if (wantIr && winRtFace == null)
                        {
                            winRtFace = new WinRtFaceDetectorProxy();
                            HwTestLogger.Info("CAMERA", string.Format("WinRT FaceDetector dostupnost: {0} ({1})", winRtFace.IsAvailable, winRtFace.InitStatus));
                        }

                        byte[] bgr32Buffer = new byte[width * height * 4];
                        byte[] grayBuffer = new byte[width * height];
                        int consecutiveReadErrors = 0;
                        int validFrames = 0;

                        while (_isRunning && !_switchToIrRequested)
                        {
                            uint actualIndex, streamFlags;
                            ulong timestamp;
                            NativeMethods.IMFSample sample = null;

                            int hr = reader.ReadSample(streamIndex, 0, out actualIndex, out streamFlags, out timestamp, out sample);
                            if (hr != 0)
                            {
                                consecutiveReadErrors++;
                                if (consecutiveReadErrors == 1 || consecutiveReadErrors % 20 == 0)
                                {
                                    HwTestLogger.Warn("CAMERA", string.Format("ReadSample greška #{0}: 0x{1:X8}", consecutiveReadErrors, hr));
                                }
                                if (consecutiveReadErrors >= 30)
                                {
                                    UpdateUiStatus(string.Format("⚠ Prekid video signala sa kamere (0x{0:X8}).", hr), System.Windows.Media.Color.FromRgb(220, 53, 69));
                                    return;
                                }
                                Thread.Sleep(45);
                                continue;
                            }

                            if (sample != null)
                            {
                                consecutiveReadErrors = 0;
                                NativeMethods.IMFMediaBuffer buffer = null;
                                bool locked = false;
                                try
                                {
                                    int hrBuf = sample.ConvertToContiguousBuffer(out buffer);
                                    if (hrBuf == 0 && buffer != null)
                                    {
                                        IntPtr pData;
                                        uint maxLen, currentLen;
                                        if (buffer.Lock(out pData, out maxLen, out currentLen) == 0 && pData != IntPtr.Zero)
                                        {
                                            locked = true;
                                            int minBytes = GetExpectedMinBytes(pixelKind, width, height);
                                            if (currentLen >= (uint)minBytes)
                                            {
                                                byte[] rawBytes = new byte[currentLen];
                                                Marshal.Copy(pData, rawBytes, 0, (int)currentLen);

                                                validFrames++;
                                                if (validFrames == 1)
                                                {
                                                    HwTestLogger.Info("CAMERA", string.Format("Prvi frejm primljen ({0} B, {1}x{2}, {3}, mod={4}).",
                                                        currentLen, width, height, fmtLabel, wantIr ? "IR" : "RGB"));
                                                    Dispatcher.BeginInvoke(new Action(() => {
                                                        if (!wantIr && !_qrPassed)
                                                        {
                                                            _statusText.Text = string.Format("📷 Slika aktivna ({0}x{1}) — Pokažite QR kod (WIS-CAMTEST-v1) u kvadrat na ekranu", width, height);
                                                        }
                                                    }));
                                                }

                                                ConvertRawFrameToBgr32AndGray(rawBytes, pixelKind, width, height, bgr32Buffer, grayBuffer);

                                                if (wantIr)
                                                {
                                                    // Kontrast + edge konture na slici tako da se jasno vide crte lica iz IR/video toka
                                                    BuildIrEdgeEnhancedBgr32(grayBuffer, width, height, bgr32Buffer);
                                                }

                                                byte[] frameToPaint = (byte[])bgr32Buffer.Clone();
                                                Dispatcher.BeginInvoke(new Action(() => {
                                                    if (_writeableBmp != null && _currentVideoWidth == width && _currentVideoHeight == height)
                                                    {
                                                        _writeableBmp.WritePixels(new Int32Rect(0, 0, width, height), frameToPaint, width * 4, 0);
                                                    }
                                                }));

                                                _frameCounter++;

                                                if (!wantIr)
                                                {
                                                    if (qrEnabled && qrProxy != null && _frameCounter % 5 == 0 && !_qrPassed)
                                                    {
                                                        try
                                                        {
                                                            string decoded = qrProxy.DecodeQrFromBgr32(bgr32Buffer, width, height);
                                                            if (!string.IsNullOrEmpty(decoded))
                                                            {
                                                                HwTestLogger.Info("CAMERA", "Dekodiran QR/barkod: " + decoded);
                                                                if (decoded.IndexOf("WIS-CAMTEST-v1", StringComparison.OrdinalIgnoreCase) >= 0)
                                                                {
                                                                    _qrPassed = true;
                                                                    string capturedText = decoded;
                                                                    Dispatcher.BeginInvoke(new Action(() => {
                                                                        OnQrCodeDetected(capturedText);
                                                                    }));
                                                                }
                                                            }
                                                        }
                                                        catch { }
                                                    }
                                                }
                                                else if (!_irFaceConfirmed)
                                                {
                                                    if (_frameCounter % 2 == 0)
                                                    {
                                                        FaceLandmarksData lm = DetectFaceAndLandmarks(grayBuffer, width, height, winRtFace);
                                                        bool found = (lm != null);
                                                        if (found)
                                                        {
                                                            _irFaceMissStreak = 0;
                                                            if (_irFaceConfirmFrames < RequiredIrFaceFrames)
                                                                _irFaceConfirmFrames++;
                                                            lm.ConfirmCount = _irFaceConfirmFrames;
                                                            lm.RequiredCount = RequiredIrFaceFrames;
                                                            lm.Confirmed = (_irFaceConfirmFrames >= RequiredIrFaceFrames);

                                                            if (lm.Confirmed && !_irFaceConfirmed)
                                                            {
                                                                _irFaceConfirmed = true;
                                                                _irPassed = true;
                                                                _irMethodNote = string.Format("{0} ({1})", activeName, lm.DetectorSource);
                                                                HwTestLogger.Info("CAMERA_IR", "Lice uspešno očitano preko IR režima (" + _irMethodNote + ") -> Automatski PASS!");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            _irFaceMissStreak++;
                                                            if (_irFaceMissStreak >= 4 && _irFaceConfirmFrames > 0)
                                                            {
                                                                _irFaceConfirmFrames = 0;
                                                            }
                                                        }

                                                        int countSnapshot = _irFaceConfirmFrames;
                                                        bool confirmedSnapshot = _irFaceConfirmed;
                                                        Dispatcher.BeginInvoke(new Action(() => {
                                                            OnIrFaceScanUpdate(lm, countSnapshot, confirmedSnapshot);
                                                        }));
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    if (locked && buffer != null) { try { buffer.Unlock(); } catch { } }
                                    if (buffer != null) { try { Marshal.ReleaseComObject(buffer); } catch { } }
                                    try { Marshal.ReleaseComObject(sample); } catch { }
                                }
                            }

                            Thread.Sleep(25);
                        }
                    }
                    finally
                    {
                        if (currentType != null) { try { Marshal.ReleaseComObject(currentType); } catch { } }
                        if (reader != null)
                        {
                            try { reader.Flush(NativeMethods.MF_SOURCE_READER_FIRST_VIDEO_STREAM); } catch { }
                            try { Marshal.ReleaseComObject(reader); } catch { }
                        }
                        if (mediaSource != null)
                        {
                            try { mediaSource.Shutdown(); } catch { }
                            try { Marshal.ReleaseComObject(mediaSource); } catch { }
                        }
                        if (selectedActivate != null)
                        {
                            try { selectedActivate.ShutdownObject(); } catch { }
                            try { Marshal.ReleaseComObject(selectedActivate); } catch { }
                        }
                        if (readerAttrs != null) { try { Marshal.ReleaseComObject(readerAttrs); } catch { } }
                    }

                    // Ako nismo u prebacivanju ka IR modu, izlazimo iz spoljne petlje
                    if (!_switchToIrRequested)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                HwTestLogger.Error("CAMERA", "Greška u kamera modulu: " + ex.Message + " | " + ex.StackTrace);
                UpdateUiStatus("⚠ Greška kamere: " + ex.Message, System.Windows.Media.Color.FromRgb(220, 53, 69));
            }
            finally
            {
                if (mfStarted)
                {
                    try { NativeMethods.MFShutdown(); } catch { }
                }
            }
        }

        private static int GetExpectedMinBytes(VideoPixelKind kind, int width, int height)
        {
            switch (kind)
            {
                case VideoPixelKind.Rgb32: return width * height * 4;
                case VideoPixelKind.Yuy2:
                case VideoPixelKind.L16: return width * height * 2;
                case VideoPixelKind.Nv12: return (width * height * 3) / 2;
                case VideoPixelKind.L8: return width * height;
                default: return width * height;
            }
        }

        private static void ConvertRawFrameToBgr32AndGray(byte[] raw, VideoPixelKind kind, int width, int height, byte[] bgr32, byte[] gray)
        {
            int pxCount = width * height;
            if (kind == VideoPixelKind.Rgb32)
            {
                Buffer.BlockCopy(raw, 0, bgr32, 0, pxCount * 4);
                for (int i = 0, p = 0; i < pxCount; i++, p += 4)
                {
                    int b = raw[p];
                    int g = raw[p + 1];
                    int r = raw[p + 2];
                    gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                }
            }
            else if (kind == VideoPixelKind.L8)
            {
                Buffer.BlockCopy(raw, 0, gray, 0, pxCount);
                for (int i = 0, p = 0; i < pxCount; i++, p += 4)
                {
                    byte v = raw[i];
                    bgr32[p] = v;
                    bgr32[p + 1] = v;
                    bgr32[p + 2] = v;
                    bgr32[p + 3] = 255;
                }
            }
            else if (kind == VideoPixelKind.L16)
            {
                for (int i = 0, s = 0, p = 0; i < pxCount; i++, s += 2, p += 4)
                {
                    int v16 = raw[s] | (raw[s + 1] << 8);
                    byte v = (byte)(v16 >> 4);
                    if (v == 0 && raw[s + 1] > 0) v = raw[s + 1];
                    gray[i] = v;
                    bgr32[p] = v;
                    bgr32[p + 1] = v;
                    bgr32[p + 2] = v;
                    bgr32[p + 3] = 255;
                }
            }
            else if (kind == VideoPixelKind.Nv12)
            {
                // NV12 Y-ravan je direktno grayscale luminance
                Buffer.BlockCopy(raw, 0, gray, 0, pxCount);
                for (int i = 0, p = 0; i < pxCount; i++, p += 4)
                {
                    byte y = raw[i];
                    bgr32[p] = y;
                    bgr32[p + 1] = y;
                    bgr32[p + 2] = y;
                    bgr32[p + 3] = 255;
                }
            }
            else if (kind == VideoPixelKind.Yuy2)
            {
                // YUY2: Y0 U0 Y1 V0
                for (int i = 0, s = 0, p = 0; i + 1 < pxCount && s + 3 < raw.Length; i += 2, s += 4, p += 8)
                {
                    byte y0 = raw[s];
                    byte y1 = raw[s + 2];
                    gray[i] = y0;
                    gray[i + 1] = y1;
                    bgr32[p] = y0; bgr32[p + 1] = y0; bgr32[p + 2] = y0; bgr32[p + 3] = 255;
                    bgr32[p + 4] = y1; bgr32[p + 5] = y1; bgr32[p + 6] = y1; bgr32[p + 7] = 255;
                }
            }
        }

        private static void NormalizeGrayForDetection(byte[] gray, byte[] normOut, int width, int height)
        {
            int len = width * height;
            int minV = 255;
            int maxV = 0;
            for (int y = 8; y < height - 8; y += 4)
            {
                int rowOff = y * width;
                for (int x = 8; x < width - 8; x += 4)
                {
                    int v = gray[rowOff + x];
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
            }
            int span = maxV - minV;
            if (span < 18)
            {
                Buffer.BlockCopy(gray, 0, normOut, 0, len);
                return;
            }
            int scale = (255 << 10) / span;
            for (int i = 0; i < len; i++)
            {
                int val = ((gray[i] - minV) * scale) >> 10;
                if (val < 0) val = 0;
                else if (val > 255) val = 255;
                normOut[i] = (byte)val;
            }
        }

        private static void BuildIrEdgeEnhancedBgr32(byte[] gray, int width, int height, byte[] bgr32)
        {
            // Normalizuje IR kontrast i pojacava konture/crte lica cijan-zelenim ivicama na samoj slici
            byte[] norm = new byte[width * height];
            NormalizeGrayForDetection(gray, norm, width, height);

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    int p = idx * 4;
                    int baseLum = norm[idx];

                    int edge = 0;
                    if (x > 0 && x + 1 < width && y > 0 && y + 1 < height)
                    {
                        int gx = norm[idx + 1] - norm[idx - 1];
                        if (gx < 0) gx = -gx;
                        int gy = norm[idx + width] - norm[idx - width];
                        if (gy < 0) gy = -gy;
                        edge = gx + gy;
                    }

                    int r = baseLum;
                    int g = baseLum;
                    int b = baseLum;

                    if (edge > 28)
                    {
                        int boost = (edge - 28) * 2;
                        if (boost > 210) boost = 210;
                        g = Math.Min(255, (baseLum >> 1) + boost);
                        b = Math.Min(255, (baseLum >> 1) + (boost * 3) / 4);
                        r = Math.Max(0, baseLum >> 2);
                    }

                    bgr32[p] = (byte)b;
                    bgr32[p + 1] = (byte)g;
                    bgr32[p + 2] = (byte)r;
                    bgr32[p + 3] = 255;
                }
            }
        }

        private FaceLandmarksData DetectFaceAndLandmarks(byte[] gray, int width, int height, WinRtFaceDetectorProxy winRtFace)
        {
            byte[] norm = new byte[width * height];
            NormalizeGrayForDetection(gray, norm, width, height);

            PixelBox? detectedBox = null;
            string source = "";

            // 1. Pokušaj Windows ugrađeni FaceDetector preko WinRT refleksije
            if (winRtFace != null && winRtFace.IsAvailable)
            {
                detectedBox = winRtFace.DetectLargestFace(norm, width, height);
                if (detectedBox.HasValue)
                {
                    source = "Windows FaceDetector";
                }
            }

            // 2. Ako WinRT FaceDetector nije dostupan ili ne pronađe lice na IR frejmu, koristi naš brzi Integral-Image detektor
            if (!detectedBox.HasValue)
            {
                detectedBox = FallbackDetectFrontalFace(norm, width, height);
                if (detectedBox.HasValue)
                {
                    source = "IR Biometric Mesh";
                }
            }

            if (!detectedBox.HasValue) return null;

            PixelBox box = detectedBox.Value;
            if (box.Width < 36 || box.Height < 36) return null;

            FaceLandmarksData lm = ExtractFacialLandmarks(norm, width, height, box);
            lm.DetectorSource = source;
            return lm;
        }

        private static PixelBox? FallbackDetectFrontalFace(byte[] norm, int width, int height)
        {
            const int sw = 160;
            const int sh = 120;
            int[] integ = new int[(sw + 1) * (sh + 1)];

            // Gradi downsampled integral image (O(1) sume pravougaonika)
            for (int y = 0; y < sh; y++)
            {
                int srcY = (y * height) / sh;
                int rowSum = 0;
                int iRow = (y + 1) * (sw + 1);
                int prevRow = y * (sw + 1);
                for (int x = 0; x < sw; x++)
                {
                    int srcX = (x * width) / sw;
                    rowSum += norm[srcY * width + srcX];
                    integ[iRow + x + 1] = integ[prevRow + x + 1] + rowSum;
                }
            }

            double bestScore = 0.0;
            int bestX = 0, bestY = 0, bestS = 0;

            int[] sizes = new int[] { 42, 54, 68, 82 };
            for (int si = 0; si < sizes.Length; si++)
            {
                int s = sizes[si];
                int step = 4;
                int maxY = sh - s - 4;
                int maxX = sw - s - 4;
                for (int y = 4; y <= maxY; y += step)
                {
                    for (int x = 14; x <= maxX - 10; x += step)
                    {
                        double foreheadMean = RectMean(integ, sw + 1, x + s * 2 / 10, y + s * 6 / 100, s * 6 / 10, s * 16 / 100);
                        double eyeBandMean = RectMean(integ, sw + 1, x + s * 14 / 100, y + s * 24 / 100, s * 72 / 100, s * 18 / 100);
                        double leftEyeMean = RectMean(integ, sw + 1, x + s * 16 / 100, y + s * 25 / 100, s * 26 / 100, s * 16 / 100);
                        double bridgeMean = RectMean(integ, sw + 1, x + s * 43 / 100, y + s * 25 / 100, s * 14 / 100, s * 16 / 100);
                        double rightEyeMean = RectMean(integ, sw + 1, x + s * 58 / 100, y + s * 25 / 100, s * 26 / 100, s * 16 / 100);
                        double cheekMean = RectMean(integ, sw + 1, x + s * 20 / 100, y + s * 46 / 100, s * 60 / 100, s * 18 / 100);
                        double mouthMean = RectMean(integ, sw + 1, x + s * 26 / 100, y + s * 68 / 100, s * 48 / 100, s * 14 / 100);

                        // Frontalno lice: oci tamnije od cela i obraza; most nosa svetliji od ociju; simetrija oka
                        double contrastForehead = foreheadMean - eyeBandMean;
                        double contrastCheek = cheekMean - eyeBandMean;
                        double bridgeContrast = bridgeMean - ((leftEyeMean + rightEyeMean) * 0.5);
                        double cheekMouthContrast = cheekMean - mouthMean;
                        double eyeAsym = Math.Abs(leftEyeMean - rightEyeMean);

                        if (contrastForehead > 9.0 &&
                            contrastCheek > 10.0 &&
                            bridgeContrast > 3.5 &&
                            cheekMouthContrast > 2.5 &&
                            eyeAsym < 28.0 &&
                            foreheadMean > 48.0)
                        {
                            double score = contrastForehead + contrastCheek * 1.3 + bridgeContrast * 1.2 + cheekMouthContrast - eyeAsym * 0.6;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestX = x;
                                bestY = y;
                                bestS = s;
                            }
                        }
                    }
                }
            }

            if (bestScore < 42.0 || bestS <= 0) return null;

            return new PixelBox
            {
                X = (bestX * width) / sw,
                Y = (bestY * height) / sh,
                Width = (bestS * width) / sw,
                Height = (bestS * height) / sh
            };
        }

        private static double RectMean(int[] integ, int stride, int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return 0.0;
            int x2 = x + w;
            int y2 = y + h;
            int sum = integ[y2 * stride + x2] - integ[y * stride + x2] - integ[y2 * stride + x] + integ[y * stride + x];
            return (double)sum / (w * h);
        }

        private static FaceLandmarksData ExtractFacialLandmarks(byte[] norm, int w, int h, PixelBox box)
        {
            int bx = Math.Max(2, box.X);
            int by = Math.Max(2, box.Y);
            int bw = Math.Min(w - bx - 2, box.Width);
            int bh = Math.Min(h - by - 2, box.Height);

            // Traži stvarne minimume (tamne zenice) za levo i desno oko unutar gornje polovine lica
            System.Windows.Point lEye = FindDarkestSubCenter(norm, w, h,
                bx + (int)(bw * 0.17), by + (int)(bh * 0.24),
                Math.Max(6, (int)(bw * 0.27)), Math.Max(6, (int)(bh * 0.20)));

            System.Windows.Point rEye = FindDarkestSubCenter(norm, w, h,
                bx + (int)(bw * 0.56), by + (int)(bh * 0.24),
                Math.Max(6, (int)(bw * 0.27)), Math.Max(6, (int)(bh * 0.20)));

            // Usta: najjači vertikalni gradijent (gornja/donja usna) u donjoj trećini lica
            double mouthCenterX = (lEye.X + rEye.X) * 0.5;
            double mouthY = FindStrongestHorizontalEdgeY(norm, w, h,
                bx + (int)(bw * 0.28), by + (int)(bh * 0.66),
                Math.Max(8, (int)(bw * 0.44)), Math.Max(8, (int)(bh * 0.20)),
                by + bh * 0.74);

            double eyeSpan = Math.Max(16.0, rEye.X - lEye.X);
            double eyeMidY = (lEye.Y + rEye.Y) * 0.5;
            double noseTipY = eyeMidY + (mouthY - eyeMidY) * 0.56;
            double noseHalfW = eyeSpan * 0.22;
            double mouthHalfW = eyeSpan * 0.42;

            // Obrve: iznad oba oka
            double browDrop = Math.Max(6.0, bh * 0.11);
            System.Windows.Point[] lBrow = new System.Windows.Point[]
            {
                new System.Windows.Point(lEye.X - eyeSpan * 0.24, lEye.Y - browDrop * 0.85),
                new System.Windows.Point(lEye.X, lEye.Y - browDrop * 1.15),
                new System.Windows.Point(lEye.X + eyeSpan * 0.22, lEye.Y - browDrop * 0.90)
            };
            System.Windows.Point[] rBrow = new System.Windows.Point[]
            {
                new System.Windows.Point(rEye.X - eyeSpan * 0.22, rEye.Y - browDrop * 0.90),
                new System.Windows.Point(rEye.X, rEye.Y - browDrop * 1.15),
                new System.Windows.Point(rEye.X + eyeSpan * 0.24, rEye.Y - browDrop * 0.85)
            };

            System.Windows.Point[] jaw = new System.Windows.Point[]
            {
                new System.Windows.Point(bx + bw * 0.10, by + bh * 0.36),
                new System.Windows.Point(bx + bw * 0.15, by + bh * 0.64),
                new System.Windows.Point(bx + bw * 0.30, by + bh * 0.87),
                new System.Windows.Point(mouthCenterX, by + bh * 0.96),
                new System.Windows.Point(bx + bw * 0.70, by + bh * 0.87),
                new System.Windows.Point(bx + bw * 0.85, by + bh * 0.64),
                new System.Windows.Point(bx + bw * 0.90, by + bh * 0.36)
            };

            return new FaceLandmarksData
            {
                Box = box,
                LeftEye = lEye,
                RightEye = rEye,
                LeftEyebrow = lBrow,
                RightEyebrow = rBrow,
                NoseBridgeTop = new System.Windows.Point(mouthCenterX, eyeMidY),
                NoseTip = new System.Windows.Point(mouthCenterX, noseTipY),
                NoseLeftWing = new System.Windows.Point(mouthCenterX - noseHalfW, noseTipY + bh * 0.02),
                NoseRightWing = new System.Windows.Point(mouthCenterX + noseHalfW, noseTipY + bh * 0.02),
                MouthLeft = new System.Windows.Point(mouthCenterX - mouthHalfW, mouthY),
                MouthRight = new System.Windows.Point(mouthCenterX + mouthHalfW, mouthY),
                MouthTop = new System.Windows.Point(mouthCenterX, mouthY - bh * 0.045),
                MouthBottom = new System.Windows.Point(mouthCenterX, mouthY + bh * 0.055),
                JawContour = jaw
            };
        }

        private static System.Windows.Point FindDarkestSubCenter(byte[] norm, int w, int h, int rx, int ry, int rw, int rh)
        {
            int x0 = Math.Max(1, rx);
            int y0 = Math.Max(1, ry);
            int x1 = Math.Min(w - 2, rx + rw);
            int y1 = Math.Min(h - 2, ry + rh);
            int bestX = (x0 + x1) / 2;
            int bestY = (y0 + y1) / 2;
            int minSum = int.MaxValue;

            for (int y = y0; y <= y1 - 2; y += 2)
            {
                for (int x = x0; x <= x1 - 2; x += 2)
                {
                    int idx = y * w + x;
                    int s = norm[idx] + norm[idx + 1] + norm[idx + w] + norm[idx + w + 1];
                    if (s < minSum)
                    {
                        minSum = s;
                        bestX = x + 1;
                        bestY = y + 1;
                    }
                }
            }
            return new System.Windows.Point(bestX, bestY);
        }

        private static double FindStrongestHorizontalEdgeY(byte[] norm, int w, int h, int rx, int ry, int rw, int rh, double fallbackY)
        {
            int x0 = Math.Max(1, rx);
            int y0 = Math.Max(2, ry);
            int x1 = Math.Min(w - 2, rx + rw);
            int y1 = Math.Min(h - 3, ry + rh);
            if (x1 <= x0 || y1 <= y0) return fallbackY;

            int bestY = -1;
            int maxGrad = 0;
            for (int y = y0; y <= y1; y++)
            {
                int rowGrad = 0;
                int row = y * w;
                for (int x = x0; x <= x1; x += 2)
                {
                    int g = norm[row + x + w] - norm[row + x - w];
                    if (g < 0) g = -g;
                    rowGrad += g;
                }
                if (rowGrad > maxGrad)
                {
                    maxGrad = rowGrad;
                    bestY = y;
                }
            }
            return bestY > 0 ? bestY : fallbackY;
        }

        private void OnQrCodeDetected(string text)
        {
            _qrDetectionBanner.Visibility = Visibility.Visible;
            if (_qrGuideBox != null)
            {
                _qrGuideBox.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 205, 65));
                _qrGuideBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 40, 205, 65));
            }
            if (_qrGuideLabel != null)
            {
                _qrGuideLabel.Text = "✓ QR VERIFIKOVAN";
                _qrGuideLabel.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 244, 197));
            }
            _statusText.Text = "✓ Odlično! QR kod verifikovan.";
            _statusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));

            if (_hasIrCamera)
            {
                if (_btnSwitchIr != null)
                {
                    _btnSwitchIr.IsEnabled = true;
                }
                _qrDetectionBanner.Text = "✓ QR VERIFIKOVAN — PRELAZIM AUTOMATSKI NA IR KAMERU...";
                _statusText.Text += " Prelazim automatski na test IR kamere...";
                StartAutoSwitchToIrTimer();
            }
            else
            {
                _statusText.Text += " Automatski završetak testa...";
                StartAutoPassTimer();
            }
        }

        private void StartAutoSwitchToIrTimer()
        {
            StopTimers();
            _autoSwitchIrTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _autoSwitchIrTimer.Tick += (s, e) =>
            {
                if (_autoSwitchIrTimer != null) _autoSwitchIrTimer.Stop();
                if (!InIrMode)
                {
                    SwitchToIrMode();
                }
            };
            _autoSwitchIrTimer.Start();
        }

        private void StartAutoPassTimer()
        {
            StopTimers();
            _autoPassTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1100)
            };
            _autoPassTimer.Tick += (s, e) =>
            {
                StopTimers();
                FinishPass();
            };
            _autoPassTimer.Start();
        }

        private void StopTimers()
        {
            if (_autoPassTimer != null)
            {
                _autoPassTimer.Stop();
                _autoPassTimer = null;
            }
            if (_autoSwitchIrTimer != null)
            {
                _autoSwitchIrTimer.Stop();
                _autoSwitchIrTimer = null;
            }
        }

        private void SwitchToIrMode()
        {
            if (InIrMode) return;
            if (_autoSwitchIrTimer != null) _autoSwitchIrTimer.Stop();

            InIrMode = true;
            _irFaceConfirmFrames = 0;
            _irFaceMissStreak = 0;
            _irFaceConfirmed = false;

            if (_qrGuideBox != null) _qrGuideBox.Visibility = Visibility.Collapsed;
            if (_qrDetectionBanner != null) _qrDetectionBanner.Visibility = Visibility.Collapsed;
            if (_btnSwitchIr != null) _btnSwitchIr.Visibility = Visibility.Collapsed;

            if (_btnConfirmIr != null)
            {
                _btnConfirmIr.Visibility = Visibility.Visible;
                _btnConfirmIr.IsEnabled = true;
            }
            if (_btnFailIr != null)
            {
                _btnFailIr.Visibility = Visibility.Visible;
                if (_btnFailRgb != null) _btnFailRgb.Visibility = Visibility.Collapsed;
            }
            if (_irScanBanner != null)
            {
                _irScanBanner.Visibility = Visibility.Visible;
            }

            _statusText.Text = "👁️ TEST IR KAMERE: Pogledajte pravo u kameru — očitavam crte lica...";
            _statusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176));

            // Signalizira CameraCaptureLoop-u da cisto zatvori RGB uredjaj i aktivira IR kameru
            _switchToIrRequested = true;
        }

        private void OnIrFaceScanUpdate(FaceLandmarksData lm, int confirmCount, bool confirmed)
        {
            if (_isClosingOrDone || !InIrMode) return;

            if (_irScanProgress != null)
            {
                _irScanProgress.Value = confirmCount;
            }

            DrawFaceLandmarksOverlay(lm);

            if (confirmed)
            {
                _statusText.Text = "✓ IR LICE USPEŠNO OČITANO! Automatski PASS testa kamere...";
                _statusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 205, 65));
                if (_irScanBanner != null)
                {
                    _irScanBanner.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 205, 65));
                    _irScanBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 22, 68, 36));
                }
                if (_irScanText != null)
                {
                    _irScanText.Text = string.Format("✓ CRTE LICA POTVRĐENE ({0}/{1}) — AUTOMATSKI PASS!", confirmCount, RequiredIrFaceFrames);
                    _irScanText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 244, 197));
                }
                StartAutoPassTimer();
            }
            else if (lm != null)
            {
                _statusText.Text = string.Format("👁️ IR SKENIRANJE LICA ({0}/{1}): Lice detektovano — zadržite pogled...", confirmCount, RequiredIrFaceFrames);
                _statusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176));
                if (_irScanText != null)
                {
                    _irScanText.Text = string.Format("👁️ Očitavam crte lica ({0}/{1}) [{2}]...", confirmCount, RequiredIrFaceFrames, lm.DetectorSource);
                    _irScanText.Foreground = System.Windows.Media.Brushes.White;
                }
            }
            else
            {
                _statusText.Text = "👁️ TEST IR KAMERE: Stanite ispred kamere — tražim crte lica...";
                _statusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7));
                if (_irScanText != null)
                {
                    _irScanText.Text = "🔍 Približite lice na 40-70 cm ispred IR kamere...";
                    _irScanText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 100));
                }
            }
        }

        private void DrawFaceLandmarksOverlay(FaceLandmarksData lm)
        {
            if (_faceOverlayCanvas == null) return;
            _faceOverlayCanvas.Children.Clear();
            if (lm == null) return;

            System.Windows.Media.Color primaryCol = lm.Confirmed
                ? System.Windows.Media.Color.FromRgb(40, 225, 85)
                : System.Windows.Media.Color.FromRgb(56, 235, 210);
            System.Windows.Media.Color accentCol = lm.Confirmed
                ? System.Windows.Media.Color.FromRgb(185, 255, 195)
                : System.Windows.Media.Color.FromRgb(255, 212, 70);

            var primaryBrush = new SolidColorBrush(primaryCol);
            var meshBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, primaryCol.R, primaryCol.G, primaryCol.B));
            var accentBrush = new SolidColorBrush(accentCol);

            double x = lm.Box.X;
            double y = lm.Box.Y;
            double w = lm.Box.Width;
            double h = lm.Box.Height;
            double cLen = Math.Max(14.0, Math.Min(w, h) * 0.20);

            // 1. Uglovi okvira oko lica (HUD skener)
            AddCornerBracket(x, y, cLen, 1, 1, primaryBrush, 3.0);
            AddCornerBracket(x + w, y, cLen, -1, 1, primaryBrush, 3.0);
            AddCornerBracket(x, y + h, cLen, 1, -1, primaryBrush, 3.0);
            AddCornerBracket(x + w, y + h, cLen, -1, -1, primaryBrush, 3.0);

            // 2. Kontura vilice i obraza
            AddPolyline(lm.JawContour, primaryBrush, 2.2);

            // 3. Obrve
            AddPolyline(lm.LeftEyebrow, accentBrush, 2.4);
            AddPolyline(lm.RightEyebrow, accentBrush, 2.4);

            // 4. Oči + zenice
            double eyeR = Math.Max(5.0, w * 0.055);
            AddEyeGraphic(lm.LeftEye, eyeR, primaryBrush, accentBrush);
            AddEyeGraphic(lm.RightEye, eyeR, primaryBrush, accentBrush);

            // 5. Nos (most + baza)
            AddLine(lm.NoseBridgeTop, lm.NoseTip, primaryBrush, 2.2);
            AddLine(lm.NoseLeftWing, lm.NoseTip, primaryBrush, 2.0);
            AddLine(lm.NoseTip, lm.NoseRightWing, primaryBrush, 2.0);

            // 6. Usne / usta
            AddPolyline(new System.Windows.Point[] { lm.MouthLeft, lm.MouthTop, lm.MouthRight, lm.MouthBottom, lm.MouthLeft }, accentBrush, 2.1);
            AddLine(lm.MouthLeft, lm.MouthRight, meshBrush, 1.4);

            // 7. Biometrijska povezna mreža između tačaka lica
            AddLine(lm.LeftEye, lm.NoseTip, meshBrush, 1.1);
            AddLine(lm.RightEye, lm.NoseTip, meshBrush, 1.1);
            AddLine(lm.LeftEye, lm.MouthLeft, meshBrush, 1.0);
            AddLine(lm.RightEye, lm.MouthRight, meshBrush, 1.0);
            AddLine(lm.NoseLeftWing, lm.MouthLeft, meshBrush, 1.0);
            AddLine(lm.NoseRightWing, lm.MouthRight, meshBrush, 1.0);

            // 8. Animirana horizontalna sken linija preko lica dok traje očitavanje
            if (!lm.Confirmed)
            {
                _scanLinePhase = (_scanLinePhase + 0.14) % 1.0;
                double scanY = y + h * _scanLinePhase;
                Line scanLine = new Line
                {
                    X1 = x + 4,
                    Y1 = scanY,
                    X2 = x + w - 4,
                    Y2 = scanY,
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 90, 255, 220)),
                    StrokeThickness = 2.0
                };
                _faceOverlayCanvas.Children.Add(scanLine);
            }

            // 9. Oznaka statusa iznad lica
            Border tag = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 12, 16, 22)),
                BorderBrush = primaryBrush,
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2)
            };
            tag.Child = new TextBlock
            {
                Text = lm.Confirmed
                    ? "✓ LICE OČITANO"
                    : string.Format("CRTE LICA ({0}/{1})", lm.ConfirmCount, lm.RequiredCount),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = primaryBrush
            };
            Canvas.SetLeft(tag, Math.Max(4, x));
            Canvas.SetTop(tag, Math.Max(4, y - 24));
            _faceOverlayCanvas.Children.Add(tag);
        }

        private void AddCornerBracket(double cx, double cy, double len, int dx, int dy, System.Windows.Media.Brush brush, double thick)
        {
            _faceOverlayCanvas.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = cx + dx * len, Y2 = cy, Stroke = brush, StrokeThickness = thick });
            _faceOverlayCanvas.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = cx, Y2 = cy + dy * len, Stroke = brush, StrokeThickness = thick });
        }

        private void AddLine(System.Windows.Point a, System.Windows.Point b, System.Windows.Media.Brush brush, double thick)
        {
            _faceOverlayCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = thick });
        }

        private void AddPolyline(System.Windows.Point[] pts, System.Windows.Media.Brush brush, double thick)
        {
            if (pts == null || pts.Length < 2) return;
            Polyline pl = new Polyline { Stroke = brush, StrokeThickness = thick };
            for (int i = 0; i < pts.Length; i++)
            {
                pl.Points.Add(pts[i]);
            }
            _faceOverlayCanvas.Children.Add(pl);
        }

        private void AddEyeGraphic(System.Windows.Point center, double r, System.Windows.Media.Brush ringBrush, System.Windows.Media.Brush pupilBrush)
        {
            Ellipse outer = new Ellipse
            {
                Width = r * 2.4,
                Height = r * 1.5,
                Stroke = ringBrush,
                StrokeThickness = 2.0
            };
            Canvas.SetLeft(outer, center.X - r * 1.2);
            Canvas.SetTop(outer, center.Y - r * 0.75);
            _faceOverlayCanvas.Children.Add(outer);

            Ellipse pupil = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = pupilBrush
            };
            Canvas.SetLeft(pupil, center.X - 2.5);
            Canvas.SetTop(pupil, center.Y - 2.5);
            _faceOverlayCanvas.Children.Add(pupil);
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
            StopTimers();

            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["qr_verified"] = _qrPassed;
            Result.Details["ir_verified"] = _hasIrCamera ? _irPassed : false;
            Result.Details["ir_auto_face_read"] = _irFaceConfirmed;
            if (!string.IsNullOrEmpty(_irMethodNote))
            {
                Result.Details["ir_method"] = _irMethodNote;
            }
            SafeCloseDialog(true);
        }

        private void FinishFail(string reason)
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopTimers();

            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = reason;
            Result.Details["qr_verified"] = _qrPassed;
            Result.Details["ir_verified"] = false;
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopTimers();

            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test kamere";
            SafeCloseDialog(false);
        }

        private sealed class QrDecoderProxy
        {
            private readonly ZXing.BarcodeReader _reader;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public QrDecoderProxy()
            {
                _reader = new ZXing.BarcodeReader();
                _reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE };
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public string DecodeQrFromBgr32(byte[] frameBytes, int width, int height)
            {
                using (Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    BitmapData bmpData = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bmp.PixelFormat);
                    Marshal.Copy(frameBytes, 0, bmpData.Scan0, frameBytes.Length);
                    bmp.UnlockBits(bmpData);

                    var qrResult = _reader.Decode(bmp);
                    return (qrResult != null) ? qrResult.Text : null;
                }
            }
        }

        private sealed class WinRtFaceDetectorProxy
        {
            private readonly object _detectorInstance;
            private readonly MethodInfo _detectFacesMethod;
            private readonly MethodInfo _createFromByteArrayMethod;
            private readonly MethodInfo _createCopyFromBufferMethod;
            private readonly object _gray8EnumVal;

            public bool IsAvailable { get; private set; }
            public string InitStatus { get; private set; }

            public WinRtFaceDetectorProxy()
            {
                IsAvailable = false;
                InitStatus = "Nije inicijalizovan";

                try
                {
                    Type faceDetectorType = ResolveWinRtType("Windows.Media.FaceAnalysis.FaceDetector");
                    Type softBmpType = ResolveWinRtType("Windows.Graphics.Imaging.SoftwareBitmap");
                    Type pixFmtType = ResolveWinRtType("Windows.Graphics.Imaging.BitmapPixelFormat");
                    Type cryptoType = ResolveWinRtType("Windows.Security.Cryptography.CryptographicBuffer");

                    if (faceDetectorType == null || softBmpType == null || pixFmtType == null || cryptoType == null)
                    {
                        InitStatus = "WinRT FaceAnalysis tipovi nedostupni";
                        return;
                    }

                    PropertyInfo isSupProp = faceDetectorType.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
                    if (isSupProp != null)
                    {
                        object sup = isSupProp.GetValue(null, null);
                        if (sup is bool && !(bool)sup)
                        {
                            InitStatus = "FaceDetector.IsSupported == false";
                            return;
                        }
                    }

                    MethodInfo createAsync = faceDetectorType.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (createAsync == null)
                    {
                        InitStatus = "CreateAsync nije pronađen";
                        return;
                    }

                    object op = createAsync.Invoke(null, null);
                    _detectorInstance = AwaitWinRtAsync(op, 2500);
                    if (_detectorInstance == null)
                    {
                        InitStatus = "CreateAsync vratio null";
                        return;
                    }

                    _createFromByteArrayMethod = cryptoType.GetMethod("CreateFromByteArray", new Type[] { typeof(byte[]) });
                    _gray8EnumVal = Enum.ToObject(pixFmtType, 62); // BitmapPixelFormat.Gray8 == 62

                    foreach (MethodInfo mi in softBmpType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (mi.Name == "CreateCopyFromBuffer")
                        {
                            ParameterInfo[] ps = mi.GetParameters();
                            if (ps.Length == 4 && ps[1].ParameterType == pixFmtType)
                            {
                                _createCopyFromBufferMethod = mi;
                                break;
                            }
                        }
                    }

                    foreach (MethodInfo mi in _detectorInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (mi.Name == "DetectFacesAsync")
                        {
                            ParameterInfo[] ps = mi.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == softBmpType)
                            {
                                _detectFacesMethod = mi;
                                break;
                            }
                        }
                    }

                    if (_createFromByteArrayMethod != null && _createCopyFromBufferMethod != null && _detectFacesMethod != null)
                    {
                        IsAvailable = true;
                        InitStatus = "OK";
                    }
                    else
                    {
                        InitStatus = "Nedostaju WinRT metode";
                    }
                }
                catch (Exception ex)
                {
                    InitStatus = "Izuzetak: " + ex.Message;
                    IsAvailable = false;
                }
            }

            public PixelBox? DetectLargestFace(byte[] gray, int width, int height)
            {
                if (!IsAvailable) return null;
                try
                {
                    int targetW = width;
                    int targetH = height;
                    byte[] inputGray = gray;

                    if (width > 360)
                    {
                        targetW = 320;
                        targetH = Math.Max(120, (height * targetW) / width);
                        inputGray = DownsampleGray(gray, width, height, targetW, targetH);
                    }

                    object ibuffer = _createFromByteArrayMethod.Invoke(null, new object[] { inputGray });
                    if (ibuffer == null) return null;

                    object softBmp = _createCopyFromBufferMethod.Invoke(null, new object[] { ibuffer, _gray8EnumVal, targetW, targetH });
                    if (softBmp == null) return null;

                    try
                    {
                        object asyncOp = _detectFacesMethod.Invoke(_detectorInstance, new object[] { softBmp });
                        object facesObj = AwaitWinRtAsync(asyncOp, 600);
                        IEnumerable faces = facesObj as IEnumerable;
                        if (faces == null) return null;

                        PixelBox? best = null;
                        int maxArea = 0;

                        foreach (object face in faces)
                        {
                            PropertyInfo boxProp = face.GetType().GetProperty("FaceBox");
                            if (boxProp == null) continue;
                            object boxVal = boxProp.GetValue(face, null);
                            if (boxVal == null) continue;

                            Type bt = boxVal.GetType();
                            int fx = ReadBoxFieldOrProp(bt, boxVal, "X");
                            int fy = ReadBoxFieldOrProp(bt, boxVal, "Y");
                            int fw = ReadBoxFieldOrProp(bt, boxVal, "Width");
                            int fh = ReadBoxFieldOrProp(bt, boxVal, "Height");

                            int area = fw * fh;
                            if (area > maxArea && fw > 0 && fh > 0)
                            {
                                maxArea = area;
                                best = new PixelBox
                                {
                                    X = (fx * width) / targetW,
                                    Y = (fy * height) / targetH,
                                    Width = (fw * width) / targetW,
                                    Height = (fh * height) / targetH
                                };
                            }
                        }

                        return best;
                    }
                    finally
                    {
                        IDisposable disp = softBmp as IDisposable;
                        if (disp != null) disp.Dispose();
                    }
                }
                catch
                {
                    return null;
                }
            }

            private static int ReadBoxFieldOrProp(Type t, object obj, string name)
            {
                FieldInfo fi = t.GetField(name);
                if (fi != null) return Convert.ToInt32(fi.GetValue(obj));
                PropertyInfo pi = t.GetProperty(name);
                if (pi != null) return Convert.ToInt32(pi.GetValue(obj, null));
                return 0;
            }

            private static byte[] DownsampleGray(byte[] src, int sw, int sh, int dw, int dh)
            {
                byte[] dst = new byte[dw * dh];
                for (int y = 0; y < dh; y++)
                {
                    int sy = (y * sh) / dh;
                    int sRow = sy * sw;
                    int dRow = y * dw;
                    for (int x = 0; x < dw; x++)
                    {
                        dst[dRow + x] = src[sRow + ((x * sw) / dw)];
                    }
                }
                return dst;
            }

            private static Type ResolveWinRtType(string fullTypeName)
            {
                string[] parts = fullTypeName.Split('.');
                for (int len = parts.Length - 1; len >= 1; len--)
                {
                    string asmName = string.Join(".", parts, 0, len);
                    Type t = Type.GetType(fullTypeName + ", " + asmName + ", ContentType=WindowsRuntime", false);
                    if (t != null) return t;
                }
                return Type.GetType(fullTypeName + ", Windows, ContentType=WindowsRuntime", false);
            }

            private static object AwaitWinRtAsync(object asyncOp, int timeoutMs)
            {
                if (asyncOp == null) return null;
                Type opType = asyncOp.GetType();

                PropertyInfo statusProp = opType.GetProperty("Status");
                if (statusProp == null)
                {
                    Type asyncInfoType = opType.GetInterface("Windows.Foundation.IAsyncInfo");
                    if (asyncInfoType != null)
                        statusProp = asyncInfoType.GetProperty("Status");
                }

                MethodInfo getResults = opType.GetMethod("GetResults");
                if (statusProp == null || getResults == null) return null;

                int waited = 0;
                while (waited < timeoutMs)
                {
                    int st = Convert.ToInt32(statusProp.GetValue(asyncOp, null));
                    if (st != 0) break; // 0 == Started
                    Thread.Sleep(15);
                    waited += 15;
                }

                int finalSt = Convert.ToInt32(statusProp.GetValue(asyncOp, null));
                if (finalSt == 1) // 1 == Completed
                {
                    return getResults.Invoke(asyncOp, null);
                }
                return null;
            }
        }
    }
}
