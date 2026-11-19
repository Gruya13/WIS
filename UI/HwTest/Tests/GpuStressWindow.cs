using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;
using WIS.UI;

namespace WIS.HwTest.Tests
{
    public class GpuStressWindow : Window
    {
        public TestItemResult Result { get; private set; }

        private readonly AutoDiagnosticsEngine _diagEngine;
        private readonly int _durationSeconds = 8;

        private readonly TextBlock _tbHeaderInfo;
        private readonly TextBlock _tbFps;
        private readonly TextBlock _tbTemp;
        private readonly TextBlock _tbStatus;
        private readonly ProgressBar _progress;

        private readonly AxisAngleRotation3D _rotY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        private readonly AxisAngleRotation3D _rotX = new AxisAngleRotation3D(new Vector3D(1, 0.3, 0), 0);
        private readonly TranslateTransform3D _lightMove = new TranslateTransform3D();

        private readonly Stopwatch _totalSw = new Stopwatch();
        private readonly Stopwatch _fpsWindowSw = new Stopwatch();
        private DispatcherTimer _telemetryTimer;

        private int _totalFrames;
        private int _windowFrames;
        private double _minFps = 9999.0;
        private double _maxFps = 0.0;
        private float _peakGpuTemp = 0f;
        private bool _completedNormally = false;
        private bool _isClosing = false;
        private bool _isClosingOrDone = false;

        private string _gpuName;
        private string _driverVer;
        private long _vramMb;
        private uint _pnpErr;
        private bool _isBasicDisplay;
        private string _allAdapters;
        private int _renderTier;

        public GpuStressWindow(AutoDiagnosticsEngine diagEngine)
        {
            _diagEngine = diagEngine;
            Result = new TestItemResult("auto_gpu", "Grafika (GPU)", "Provera drajvera, VRAM-a i 3D hardverskog renderovanja (WPF Direct3D)");
            Result.IsAutomated = true;

            AutoDiagnosticsEngine.InspectGpuAdapters(out _gpuName, out _driverVer, out _vramMb, out _pnpErr, out _isBasicDisplay, out _allAdapters);
            _renderTier = (RenderCapability.Tier >> 16);

            Title = "GPU 3D Hardverski Stres Test";
            Width = 920;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(16, 18, 24));
            ResizeMode = ResizeMode.NoResize;
            SourceInitialized += (s, e) => NativeMethods.EnableDarkTitleBar(this);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Gornja traka sa informacijama o GPU-u
            Border topBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 29, 36)),
                Padding = new Thickness(18, 12, 18, 12)
            };
            Grid tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _tbHeaderInfo = new TextBlock
            {
                Text = string.Format("GPU: {0} | Drajver: {1} | VRAM: {2} | Render Tier: {3}",
                    _gpuName,
                    string.IsNullOrEmpty(_driverVer) ? "N/A" : _driverVer,
                    _vramMb > 0 ? (_vramMb + " MB") : "Shared",
                    _renderTier),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            tg.Children.Add(_tbHeaderInfo);

            StackPanel rightStats = new StackPanel { Orientation = Orientation.Horizontal };
            _tbFps = new TextBlock
            {
                Text = "-- FPS",
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _tbTemp = new TextBlock
            {
                Text = "Temp: --°C",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102)),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            rightStats.Children.Add(_tbFps);
            rightStats.Children.Add(_tbTemp);
            Grid.SetColumn(rightStats, 1);
            tg.Children.Add(rightStats);
            topBar.Child = tg;
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // Centralni Viewport3D
            Viewport3D viewport = BuildStressViewport();
            Border viewHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(10, 12, 16)),
                Child = viewport
            };
            Grid.SetRow(viewHost, 1);
            root.Children.Add(viewHost);

            // Donja status traka
            Border botBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 29, 36)),
                Padding = new Thickness(18, 12, 18, 12)
            };
            StackPanel spBot = new StackPanel();
            _tbStatus = new TextBlock
            {
                Text = "Pokrećem 3D Direct3D opterećenje (provera stabilnosti prikaza i FPS)...",
                Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 230)),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            spBot.Children.Add(_tbStatus);

            Grid pg = new Grid();
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = _durationSeconds,
                Height = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Background = new SolidColorBrush(Color.FromRgb(45, 48, 56))
            };
            pg.Children.Add(_progress);

            Button btnAbort = new Button
            {
                Content = "Prekini",
                Background = new SolidColorBrush(Color.FromRgb(85, 50, 50)),
                Foreground = Brushes.White,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(14, 0, 0, 0)
            };
            btnAbort.Click += (s, e) => Close();
            Grid.SetColumn(btnAbort, 1);
            pg.Children.Add(btnAbort);

            spBot.Children.Add(pg);
            botBar.Child = spBot;
            Grid.SetRow(botBar, 2);
            root.Children.Add(botBar);

            Content = root;

            Loaded += OnLoaded;
            Closing += OnWindowClosing;
        }

        private Viewport3D BuildStressViewport()
        {
            Viewport3D vp = new Viewport3D();
            PerspectiveCamera cam = new PerspectiveCamera
            {
                Position = new Point3D(0, 0.5, 5.2),
                LookDirection = new Vector3D(0, -0.08, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 50
            };
            vp.Camera = cam;

            Model3DGroup scene = new Model3DGroup();
            scene.Children.Add(new AmbientLight(Color.FromRgb(45, 50, 65)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(180, 210, 255), new Vector3D(-1, -1, -1)));

            PointLight dynamicLight = new PointLight(Color.FromRgb(255, 190, 110), new Point3D(2.5, 2.0, 2.5));
            dynamicLight.Transform = _lightMove;
            scene.Children.Add(dynamicLight);

            // Visokopoligonalni torus (80 x 48 = 7,680 trouglova) + unutarnja sfera
            MeshGeometry3D torusMesh = CreateTorusMesh(1.45, 0.55, 80, 48);
            ImageBrush texBrush = new ImageBrush(CreateCheckerTexture(512));
            texBrush.ViewportUnits = BrushMappingMode.Absolute;
            texBrush.TileMode = TileMode.Tile;

            MaterialGroup mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(texBrush));
            mat.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(220, 235, 255)), 65.0));

            GeometryModel3D torusModel = new GeometryModel3D(torusMesh, mat);
            Transform3DGroup tg = new Transform3DGroup();
            tg.Children.Add(new RotateTransform3D(_rotX));
            tg.Children.Add(new RotateTransform3D(_rotY));
            torusModel.Transform = tg;
            scene.Children.Add(torusModel);

            // Sekundarni prsten za dodatni fill-rate / overdraw stres
            MeshGeometry3D innerTorus = CreateTorusMesh(0.75, 0.25, 64, 32);
            MaterialGroup mat2 = new MaterialGroup();
            mat2.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(200, 0, 150, 255))));
            mat2.Children.Add(new SpecularMaterial(Brushes.White, 90.0));
            GeometryModel3D innerModel = new GeometryModel3D(innerTorus, mat2);
            RotateTransform3D innerRot = new RotateTransform3D(_rotY);
            innerModel.Transform = innerRot;
            scene.Children.Add(innerModel);

            ModelVisual3D visual = new ModelVisual3D();
            visual.Content = scene;
            vp.Children.Add(visual);
            return vp;
        }

        private static BitmapSource CreateCheckerTexture(int size)
        {
            int stride = size * 4;
            byte[] pixels = new byte[size * stride];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool chk = (((x / 32) + (y / 32)) % 2) == 0;
                    int idx = (y * stride) + (x * 4);
                    if (chk)
                    {
                        pixels[idx] = (byte)(180 + (x * 75 / size));     // B
                        pixels[idx + 1] = (byte)(120 + (y * 120 / size)); // G
                        pixels[idx + 2] = 40;                             // R
                    }
                    else
                    {
                        pixels[idx] = 45;
                        pixels[idx + 1] = 52;
                        pixels[idx + 2] = (byte)(90 + (x * 140 / size));
                    }
                    pixels[idx + 3] = 255;
                }
            }
            BitmapSource bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }

        private static MeshGeometry3D CreateTorusMesh(double majorR, double minorR, int uSeg, int vSeg)
        {
            MeshGeometry3D mesh = new MeshGeometry3D();
            for (int i = 0; i <= uSeg; i++)
            {
                double u = (2.0 * Math.PI * i) / uSeg;
                double cu = Math.Cos(u);
                double su = Math.Sin(u);
                for (int j = 0; j <= vSeg; j++)
                {
                    double v = (2.0 * Math.PI * j) / vSeg;
                    double cv = Math.Cos(v);
                    double sv = Math.Sin(v);
                    double r = majorR + minorR * cv;
                    mesh.Positions.Add(new Point3D(r * cu, minorR * sv, r * su));
                    mesh.Normals.Add(new Vector3D(cv * cu, sv, cv * su));
                    mesh.TextureCoordinates.Add(new Point((double)i / uSeg * 4.0, (double)j / vSeg * 2.0));
                }
            }
            int stride = vSeg + 1;
            for (int i = 0; i < uSeg; i++)
            {
                for (int j = 0; j < vSeg; j++)
                {
                    int a = i * stride + j;
                    int b = (i + 1) * stride + j;
                    int c = (i + 1) * stride + (j + 1);
                    int d = i * stride + (j + 1);
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(c);
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(c);
                    mesh.TriangleIndices.Add(d);
                }
            }
            mesh.Freeze();
            return mesh;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _totalSw.Start();
            _fpsWindowSw.Start();
            CompositionTarget.Rendering += OnRenderFrame;

            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _telemetryTimer.Tick += OnTelemetryTick;
            _telemetryTimer.Start();
        }

        private void OnRenderFrame(object sender, EventArgs e)
        {
            if (_isClosing) return;

            double t = _totalSw.Elapsed.TotalSeconds;
            _rotY.Angle = (t * 95.0) % 360.0;
            _rotX.Angle = (t * 53.0) % 360.0;
            _lightMove.OffsetX = Math.Sin(t * 2.1) * 2.5;
            _lightMove.OffsetY = Math.Cos(t * 1.7) * 1.8;

            _totalFrames++;
            _windowFrames++;

            if (_fpsWindowSw.ElapsedMilliseconds >= 450)
            {
                double fps = _windowFrames / Math.Max(0.05, _fpsWindowSw.Elapsed.TotalSeconds);
                if (t > 0.7) // Ignoriši inicijalni warmup frejm
                {
                    if (fps < _minFps) _minFps = fps;
                    if (fps > _maxFps) _maxFps = fps;
                }
                _tbFps.Text = string.Format("{0:0.0} FPS", fps);
                _windowFrames = 0;
                _fpsWindowSw.Restart();
            }
        }

        private void OnTelemetryTick(object sender, EventArgs e)
        {
            if (_isClosing) return;

            double elapsed = _totalSw.Elapsed.TotalSeconds;
            _progress.Value = Math.Min(_durationSeconds, elapsed);

            if (_diagEngine != null)
            {
                ThermalSensorEngine eng = _diagEngine.EnsureSensorsAndUpdate();
                if (eng != null && eng.GpuTemperature > 0)
                {
                    if (eng.GpuTemperature > _peakGpuTemp) _peakGpuTemp = eng.GpuTemperature;
                    _tbTemp.Text = string.Format("Temp: {0:0}°C (Peak {1:0}°C)", eng.GpuTemperature, _peakGpuTemp);
                }
            }

            double remain = Math.Max(0.0, _durationSeconds - elapsed);
            _tbStatus.Text = string.Format("Direct3D 3D render test u toku... Preostalo {0:0.0}s | Ukupno frejmova: {1}", remain, _totalFrames);

            if (elapsed >= _durationSeconds)
            {
                _completedNormally = true;
                FinalizeAndClose();
            }
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            CompositionTarget.Rendering -= OnRenderFrame;
            if (_telemetryTimer != null) _telemetryTimer.Stop();

            if (!_completedNormally)
            {
                Result.Status = TestStatus.Skip;
                Result.Reason = "GPU 3D test je prekinut pre završetka";
                Result.MetricSummary = "Prekinuto";
            }
        }

        private void SafeCloseDialog(bool result)
        {
            try { DialogResult = result; } catch { }
            try { Close(); } catch { }
        }

        private void FinalizeAndClose()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            _isClosing = true;
            CompositionTarget.Rendering -= OnRenderFrame;
            if (_telemetryTimer != null) _telemetryTimer.Stop();

            double elapsedSec = Math.Max(0.5, _totalSw.Elapsed.TotalSeconds);
            double avgFps = Math.Round(_totalFrames / elapsedSec, 1);
            if (_minFps > 9000) _minFps = avgFps;

            Result.DurationSeconds = (int)Math.Ceiling(elapsedSec);
            Result.Details["gpu_name"] = _gpuName;
            Result.Details["driver_version"] = _driverVer;
            Result.Details["vram_mb"] = _vramMb;
            Result.Details["pnp_error_code"] = _pnpErr;
            Result.Details["is_basic_display"] = _isBasicDisplay;
            Result.Details["wpf_render_tier"] = _renderTier;
            Result.Details["all_adapters"] = _allAdapters;
            Result.Details["avg_fps"] = avgFps;
            Result.Details["min_fps"] = Math.Round(_minFps, 1);
            Result.Details["max_fps"] = Math.Round(_maxFps, 1);
            Result.Details["total_frames"] = _totalFrames;
            if (_peakGpuTemp > 0) Result.Details["peak_gpu_temp_c"] = Math.Round(_peakGpuTemp, 1);

            string shortGpu = _gpuName.Length > 26 ? (_gpuName.Substring(0, 25) + "…") : _gpuName;
            string vramStr = _vramMb > 0 ? string.Format(" | {0} MB", _vramMb) : "";
            string tempStr = _peakGpuTemp > 0 ? string.Format(" | {0:0}°C", _peakGpuTemp) : "";
            Result.MetricSummary = string.Format("{0}{1} | {2:0} FPS | Tier {3}{4}", shortGpu, vramStr, avgFps, _renderTier, tempStr);

            if (_isBasicDisplay)
            {
                Result.Status = TestStatus.Fail;
                Result.Reason = "Aktivan je Microsoft Basic Display Adapter (nedostaje originalni GPU drajver!)";
            }
            else if (_pnpErr != 0)
            {
                Result.Status = TestStatus.Fail;
                Result.Reason = string.Format("Greška grafičke kartice u Device Manager-u (Error Code {0})", _pnpErr);
            }
            else if (_renderTier == 0)
            {
                Result.Status = TestStatus.Fail;
                Result.Reason = "WPF Render Tier 0: Direct3D hardverska akceleracija nije aktivna (softversko renderovanje)!";
            }
            else if (_peakGpuTemp >= 95f)
            {
                Result.Status = TestStatus.Fail;
                Result.Reason = string.Format("Kritično pregrevanje GPU-a ({0:0}°C >= 95°C)!", _peakGpuTemp);
            }
            else if (avgFps < 8.0)
            {
                Result.Status = TestStatus.Fail;
                Result.Reason = string.Format("Ekstremno niske 3D performanse ({0:0.0} FPS < 8 FPS)", avgFps);
            }
            else if (_renderTier == 1 || avgFps < 20.0 || _peakGpuTemp >= 86f)
            {
                Result.Status = TestStatus.Pass;
                Result.HasWarning = true;
                List<string> warns = new List<string>();
                if (_renderTier == 1) warns.Add("Delimična GPU akceleracija (Tier 1)");
                if (avgFps < 20.0) warns.Add(string.Format("Niži 3D FPS ({0:0.0} FPS)", avgFps));
                if (_peakGpuTemp >= 86f) warns.Add(string.Format("Visoka temperatura GPU-a ({0:0}°C)", _peakGpuTemp));
                Result.Reason = "UPOZORENJE: " + string.Join("; ", warns.ToArray());
            }
            else
            {
                Result.Status = TestStatus.Pass;
                Result.HasWarning = false;
                Result.Reason = string.Format("GPU drajver i 3D renderovanje OK ({0:0} FPS, Tier {1})", avgFps, _renderTier);
            }

            SafeCloseDialog(true);
        }
    }
}
