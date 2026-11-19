using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WIS.HwTest.Core;

namespace WIS.HwTest.Tests
{
    public class TouchscreenMazeWindow : Window
    {
        private enum TouchPhase
        {
            Serpentine = 1,
            Infinity = 2,
            MultitouchHand = 3
        }

        private readonly Canvas _canvas;
        private readonly TextBlock _statusText;
        private readonly Button _btnFlipHand;
        private readonly HardwareProbeResult _hwInfo;

        private TouchPhase _phase = TouchPhase.Serpentine;
        private bool _serpentineCompleted = false;
        private bool _infinityCompleted = false;
        private bool _multitouchCompleted = false;
        private bool _isClosingOrDone = false;

        // Stanje za Fazu 1 (Gore-Dole cik-cak) i Fazu 2 (Znak beskonacno)
        private List<Point> _pathPoints = new List<Point>();
        private int _nextCheckpointIdx = 0;
        private double _hitRadius = 60.0;
        private Path _completedPathVisual;
        private Path _fingerTrailVisual;
        private readonly List<Point> _liveTrail = new List<Point>();
        private readonly List<Ellipse> _checkpointDots = new List<Ellipse>();
        private Ellipse _activeTargetHalo;
        private readonly HashSet<int> _pathActiveTouchIds = new HashSet<int>();

        // Stanje za Fazu 3 (5-tackasti multitouch u obliku sake)
        private bool _rightHandMode = true;
        private readonly Dictionary<int, Point> _activeTouches = new Dictionary<int, Point>();
        private readonly List<Point> _handTargets = new List<Point>();
        private readonly List<Ellipse> _handCircles = new List<Ellipse>();
        private readonly List<TextBlock> _handLabels = new List<TextBlock>();
        private readonly List<Ellipse> _liveFingerMarkers = new List<Ellipse>();
        private TextBlock _handCounterText;
        private ProgressBar _multiProgress;
        private DispatcherTimer _multitouchTimer;
        private double _multitouchHoldSeconds = 0.0;
        private double _handCaptureRadius = 96.0;
        private readonly bool _hasStylusPen;
        private bool _stylusDetected;
        private float _maxStylusPressure = 0.0f;
        private readonly TextBlock _stylusBadge;

        public TestItemResult Result { get; private set; }

        public TouchscreenMazeWindow() : this(null)
        {
        }

        public TouchscreenMazeWindow(HardwareProbeResult hwInfo)
        {
            _hwInfo = hwInfo;
            _hasStylusPen = (_hwInfo != null && _hwInfo.HasStylusPen);
            Title = _hasStylusPen
                ? "WIS — Test Ekrana na Dodir i Aktivne Olovke (Stylus)"
                : "WIS — Test Ekrana na Dodir (Gore-Dole, Beskonačno, 5-Prstiju Šaka)";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 28));
            Topmost = true;

            Result = new TestItemResult("touchscreen", _hasStylusPen ? "Touchscreen + Olovka (Stylus)" : "Ekran na dodir (Touchscreen)",
                "Test celog ekrana (gore-dole), znaka beskonačno (∞) i 5-tačkastog multitoucha u obliku šake");

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_hasStylusPen ? 98 : 74) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });

            // Header
            Border header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 32, 40)),
                Padding = new Thickness(25, 10, 25, 10)
            };
            _statusText = new TextBlock
            {
                Text = "🖐️ Faza 1/3: Krenite prstom od START (dole levo) i pratite putanju GORE-DOLE preko celog ekrana",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            if (_hwInfo != null && !_hwInfo.HasTouchscreen)
            {
                if (_hwInfo.TouchscreenDisabledInBios)
                {
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    _statusText.Text = "⚠️ TOUCHSCREEN JE ONEMOGUĆEN U BIOS-u (" + _hwInfo.TouchscreenBiosStatus + ")! Uključite ga u BIOS-u ili označite FAIL.";
                }
                else if (_hwInfo.FactoryTouchscreen == true || _hwInfo.TouchscreenPnpPresent)
                {
                    _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                    _statusText.Text = "⚠️ FABRIČKI TOUCHSCREEN NIJE AKTIVAN U WINDOWS-U (" +
                        (!string.IsNullOrEmpty(_hwInfo.FactoryTouchscreenEvidence) ? _hwInfo.FactoryTouchscreenEvidence : "PnP/BOM") +
                        ") — proverite drajver/kabl ili označite FAIL.";
                }
            }
            if (_hasStylusPen)
            {
                string penLabel = string.IsNullOrEmpty(_hwInfo.StylusPenName) ? "Wacom / Aktivna olovka" : _hwInfo.StylusPenName;
                _stylusBadge = new TextBlock
                {
                    Text = string.Format("🖊️ Aktivna olovka ({0}): Dodirnite ekran olovkom (opciono testiranje pritiska)", penLabel),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 205, 80)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                StackPanel spHeader = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                spHeader.Children.Add(_statusText);
                spHeader.Children.Add(_stylusBadge);
                header.Child = spHeader;
            }
            else
            {
                header.Child = _statusText;
            }
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Canvas
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            Grid.SetRow(_canvas, 1);
            root.Children.Add(_canvas);

            // Footer
            Border footer = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 32, 40)),
                Padding = new Thickness(25, 10, 25, 10)
            };
            StackPanel spFooter = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _btnFlipHand = new Button
            {
                Content = "🔄 Zameni šaku (Leva / Desna)",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 12, 0),
                Visibility = Visibility.Collapsed
            };
            _btnFlipHand.Click += (s, e) =>
            {
                _rightHandMode = !_rightHandMode;
                if (_phase == TouchPhase.MultitouchHand)
                {
                    SetupHandMultitouchPhase();
                }
            };
            spFooter.Children.Add(_btnFlipHand);

            Button btnFail = new Button
            {
                Content = "❌ Ne reaguje na dodir (FAIL)",
                Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 6, 15, 6),
                Margin = new Thickness(0, 0, 12, 0)
            };
            btnFail.Click += (s, e) => FinishFail();
            spFooter.Children.Add(btnFail);

            Button btnSkip = new Button
            {
                Content = "Preskoči test ekrana na dodir",
                Background = new SolidColorBrush(Color.FromRgb(80, 85, 95)),
                Foreground = Brushes.White,
                Padding = new Thickness(15, 6, 15, 6)
            };
            btnSkip.Click += (s, e) => FinishSkip();
            spFooter.Children.Add(btnSkip);

            footer.Child = spFooter;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            Loaded += (s, e) => SetupSerpentinePhase();
            Closed += (s, e) => StopMultitouchTimer();

            TouchDown += OnScreenTouchDown;
            TouchMove += OnScreenTouchMove;
            TouchUp += OnScreenTouchUp;
            StylusDown += OnScreenStylusDown;
            StylusMove += OnScreenStylusMove;
        }

        private void SetupSerpentinePhase()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 120 || h < 120) return;

            _phase = TouchPhase.Serpentine;
            _btnFlipHand.Visibility = Visibility.Collapsed;

            int cols = (int)Math.Max(6, Math.Min(10, Math.Round(w / 145.0)));
            double padX = 48.0;
            double padY = 48.0;

            _pathPoints = MazeGenerator.GenerateSerpentinePath(w, h, padX, padY, cols);
            double colStep = (w - 2.0 * padX) / Math.Max(1, cols - 1);
            _hitRadius = Math.Max(52.0, Math.Min(80.0, colStep * 0.62));
            _nextCheckpointIdx = 0;

            RenderGuidedPathScene(
                "🖐️ Faza 1/3: Pratite putanju GORE-DOLE od ZELENE tačke (dole levo) do CRVENE tačke",
                new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                54.0,
                true);
        }

        private void SetupInfinityPhase()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 120 || h < 120) return;

            _phase = TouchPhase.Infinity;
            _btnFlipHand.Visibility = Visibility.Collapsed;

            _pathPoints = MazeGenerator.GenerateInfinityPath(w, h, 36);
            _hitRadius = Math.Max(56.0, Math.Min(82.0, Math.Min(w, h) * 0.11));
            _nextCheckpointIdx = 0;

            RenderGuidedPathScene(
                "♾️ Faza 2/3: Pratite prstom znak BESKONAČNO (od centra kroz obe petlje nazad u centar)",
                new SolidColorBrush(Color.FromRgb(95, 195, 255)),
                58.0,
                false);
        }

        private void RenderGuidedPathScene(string headerPrompt, Brush accentBrush, double trackThickness, bool showVerticalArrows)
        {
            _canvas.Children.Clear();
            _checkpointDots.Clear();
            _liveTrail.Clear();
            _pathActiveTouchIds.Clear();

            if (!HasHardwareWarningBanner())
            {
                _statusText.Text = headerPrompt;
                _statusText.Foreground = accentBrush;
            }

            if (_pathPoints.Count < 2) return;

            // 1. Spoljna tamna traka putanje (sirina prsta)
            Path bgTrack = new Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(42, 48, 62)),
                StrokeThickness = trackThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = BuildPathGeometry(_pathPoints, _pathPoints.Count)
            };
            _canvas.Children.Add(bgTrack);

            // 2. Centralna isprekidana vodilja
            Path centerGuide = new Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(130, 180, 195, 220)),
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 4 }),
                StrokeLineJoin = PenLineJoin.Round,
                Data = BuildPathGeometry(_pathPoints, _pathPoints.Count)
            };
            _canvas.Children.Add(centerGuide);

            // 3. Strelice smera na putanji (za laksu orijentaciju)
            if (showVerticalArrows)
            {
                for (int i = 2; i + 2 < _pathPoints.Count; i += 5)
                {
                    Point pPrev = _pathPoints[i - 1];
                    Point pCurr = _pathPoints[i];
                    string arrow = (pCurr.Y < pPrev.Y) ? "▲" : ((pCurr.Y > pPrev.Y) ? "▼" : "▶");
                    TextBlock tbArr = new TextBlock
                    {
                        Text = arrow,
                        FontSize = 18,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255))
                    };
                    Canvas.SetLeft(tbArr, pCurr.X - 8);
                    Canvas.SetTop(tbArr, pCurr.Y - 12);
                    _canvas.Children.Add(tbArr);
                }
            }
            else
            {
                // Strelica na ulazu desne i leve petlje znaka beskonacno
                int[] arrowIdx = new int[] { 4, 14, 22, 31 };
                for (int ai = 0; ai < arrowIdx.Length; ai++)
                {
                    int idx = arrowIdx[ai];
                    if (idx + 1 < _pathPoints.Count)
                    {
                        Point a = _pathPoints[idx];
                        Point b = _pathPoints[idx + 1];
                        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X) * (180.0 / Math.PI);
                        TextBlock tbInfArr = new TextBlock
                        {
                            Text = "➤",
                            FontSize = 20,
                            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                            RenderTransformOrigin = new Point(0.5, 0.5),
                            RenderTransform = new RotateTransform(ang)
                        };
                        Canvas.SetLeft(tbInfArr, a.X - 10);
                        Canvas.SetTop(tbInfArr, a.Y - 12);
                        _canvas.Children.Add(tbInfArr);
                    }
                }
            }

            // 4. Sloj za vec predjeni zeleni deo putanje
            _completedPathVisual = new Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(200, 40, 185, 80)),
                StrokeThickness = trackThickness * 0.42,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            _canvas.Children.Add(_completedPathVisual);

            // 5. Zivi trag prsta
            _fingerTrailVisual = new Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 90, 240, 210)),
                StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            _canvas.Children.Add(_fingerTrailVisual);

            // 6. Checkpoint tacke duz putanje
            for (int i = 0; i < _pathPoints.Count; i++)
            {
                Point p = _pathPoints[i];
                bool isFirst = (i == 0);
                bool isLast = (i == _pathPoints.Count - 1);
                double dotSize = (isFirst || isLast) ? 44.0 : 14.0;

                Ellipse dot = new Ellipse
                {
                    Width = dotSize,
                    Height = dotSize,
                    Fill = isFirst
                        ? new SolidColorBrush(Color.FromRgb(40, 167, 69))
                        : (isLast
                            ? new SolidColorBrush(Color.FromRgb(220, 53, 69))
                            : new SolidColorBrush(Color.FromArgb(140, 180, 195, 220))),
                    Stroke = (isFirst || isLast) ? Brushes.White : Brushes.Transparent,
                    StrokeThickness = (isFirst || isLast) ? 3 : 0
                };
                Canvas.SetLeft(dot, p.X - dotSize * 0.5);
                Canvas.SetTop(dot, p.Y - dotSize * 0.5);
                _canvas.Children.Add(dot);
                _checkpointDots.Add(dot);

                if (isFirst || isLast)
                {
                    TextBlock lbl = new TextBlock
                    {
                        Text = isFirst ? "START" : "CILJ",
                        FontSize = 11,
                        FontWeight = FontWeights.ExtraBold,
                        Foreground = Brushes.White
                    };
                    Canvas.SetLeft(lbl, p.X - 16);
                    Canvas.SetTop(lbl, p.Y - dotSize * 0.5 - 18);
                    _canvas.Children.Add(lbl);
                }
            }

            // 7. Zlatni halo krug za narednu aktivnu metu
            _activeTargetHalo = new Ellipse
            {
                Width = 42,
                Height = 42,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 205, 50)),
                StrokeThickness = 4,
                Fill = new SolidColorBrush(Color.FromArgb(55, 255, 205, 50))
            };
            _canvas.Children.Add(_activeTargetHalo);
            UpdateActiveTargetVisuals();
        }

        private bool HasHardwareWarningBanner()
        {
            return _hwInfo != null && !_hwInfo.HasTouchscreen &&
                   (_hwInfo.TouchscreenDisabledInBios || _hwInfo.FactoryTouchscreen == true || _hwInfo.TouchscreenPnpPresent);
        }

        private static PathGeometry BuildPathGeometry(List<Point> pts, int count)
        {
            PathGeometry geom = new PathGeometry();
            if (pts == null || count <= 0) return geom;
            int lim = Math.Min(count, pts.Count);
            if (lim <= 0) return geom;

            PathFigure fig = new PathFigure { StartPoint = pts[0] };
            for (int i = 1; i < lim; i++)
            {
                fig.Segments.Add(new LineSegment(pts[i], true));
            }
            geom.Figures.Add(fig);
            return geom;
        }

        private void UpdateActiveTargetVisuals()
        {
            if (_completedPathVisual != null && _nextCheckpointIdx > 1)
            {
                _completedPathVisual.Data = BuildPathGeometry(_pathPoints, _nextCheckpointIdx);
            }

            for (int i = 1; i < _checkpointDots.Count - 1; i++)
            {
                if (i < _nextCheckpointIdx)
                {
                    _checkpointDots[i].Fill = new SolidColorBrush(Color.FromRgb(56, 224, 110));
                }
            }

            if (_activeTargetHalo != null)
            {
                if (_nextCheckpointIdx >= 0 && _nextCheckpointIdx < _pathPoints.Count)
                {
                    Point target = _pathPoints[_nextCheckpointIdx];
                    Canvas.SetLeft(_activeTargetHalo, target.X - 21);
                    Canvas.SetTop(_activeTargetHalo, target.Y - 21);
                    _activeTargetHalo.Visibility = Visibility.Visible;
                }
                else
                {
                    _activeTargetHalo.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void AdvanceGuidedPath(Point pt)
        {
            if (_nextCheckpointIdx >= _pathPoints.Count) return;

            bool advanced = false;
            while (_nextCheckpointIdx < _pathPoints.Count)
            {
                Point target = _pathPoints[_nextCheckpointIdx];
                double allowedR = (_nextCheckpointIdx == 0) ? Math.Max(_hitRadius, 72.0) : _hitRadius;
                if (Distance(pt, target) <= allowedR)
                {
                    _nextCheckpointIdx++;
                    advanced = true;
                }
                else
                {
                    break;
                }
            }

            if (advanced)
            {
                UpdateActiveTargetVisuals();
                if (!HasHardwareWarningBanner())
                {
                    int pct = Math.Min(100, (_nextCheckpointIdx * 100) / Math.Max(1, _pathPoints.Count));
                    if (_phase == TouchPhase.Serpentine)
                    {
                        _statusText.Text = string.Format("🖐️ Faza 1/3 (Gore-Dole): {0}% — nastavite ka zlatnom krugu do CRVENE tačke", pct);
                    }
                    else if (_phase == TouchPhase.Infinity)
                    {
                        _statusText.Text = string.Format("♾️ Faza 2/3 (Znak Beskonačno): {0}% — pratite krivu ∞ do kraja", pct);
                    }
                }
            }

            if (_nextCheckpointIdx >= _pathPoints.Count)
            {
                if (_phase == TouchPhase.Serpentine)
                {
                    _serpentineCompleted = true;
                    SetupInfinityPhase();
                }
                else if (_phase == TouchPhase.Infinity)
                {
                    _infinityCompleted = true;
                    SetupHandMultitouchPhase();
                }
            }
        }

        private void SetupHandMultitouchPhase()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w < 120 || h < 120) return;

            _phase = TouchPhase.MultitouchHand;
            StopMultitouchTimer();
            _activeTouches.Clear();
            _handTargets.Clear();
            _handCircles.Clear();
            _handLabels.Clear();
            _liveFingerMarkers.Clear();
            _multitouchHoldSeconds = 0.0;

            _btnFlipHand.Visibility = Visibility.Visible;

            if (!HasHardwareWarningBanner())
            {
                _statusText.Text = string.Format("🖐️ Faza 3/3 (5-Prstiju Multitouch - {0} šaka): Spustite SVIH 5 PRSTIJU na krugove u obliku ruke i držite 1s!",
                    _rightHandMode ? "Desna" : "Leva");
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            }

            _canvas.Children.Clear();

            double cx = w * 0.5;
            double cy = h * 0.48;
            double scale = Math.Max(110.0, Math.Min(185.0, Math.Min(w * 0.24, h * 0.34)));
            double circleSize = Math.Max(78.0, Math.Min(108.0, scale * 0.56));
            _handCaptureRadius = Math.Max(115.0, circleSize * 1.25);

            double dir = _rightHandMode ? 1.0 : -1.0;

            // Proširen ergonomski raspored 5 prstiju šake za udobno spuštanje jedne ruke na ekran
            Point[] offsets = new Point[]
            {
                new Point(-1.35 * scale * dir,  0.62 * scale), // 1. Palac
                new Point(-0.82 * scale * dir, -0.45 * scale), // 2. Kažiprst
                new Point(-0.15 * scale * dir, -0.78 * scale), // 3. Srednji prst
                new Point( 0.52 * scale * dir, -0.58 * scale), // 4. Domali prst
                new Point( 1.15 * scale * dir, -0.15 * scale)  // 5. Mali prst
            };
            string[] fingerNames = new string[]
            {
                "1\nPalac",
                "2\nKažiprst",
                "3\nSrednji",
                "4\nDomali",
                "5\nMali"
            };

            Point palmCenter = new Point(cx, cy + scale * 0.66);

            // Suptilna silueta dlana i linije ka prstima
            Ellipse palmHalo = new Ellipse
            {
                Width = scale * 1.6,
                Height = scale * 1.35,
                Fill = new SolidColorBrush(Color.FromArgb(24, 110, 175, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(80, 110, 175, 255)),
                StrokeThickness = 2
            };
            Canvas.SetLeft(palmHalo, palmCenter.X - scale * 0.8);
            Canvas.SetTop(palmHalo, palmCenter.Y - scale * 0.67);
            _canvas.Children.Add(palmHalo);

            for (int i = 0; i < 5; i++)
            {
                Point target = new Point(cx + offsets[i].X, cy + offsets[i].Y);
                _handTargets.Add(target);

                Line bone = new Line
                {
                    X1 = palmCenter.X,
                    Y1 = palmCenter.Y,
                    X2 = target.X,
                    Y2 = target.Y,
                    Stroke = new SolidColorBrush(Color.FromArgb(95, 130, 175, 225)),
                    StrokeThickness = 6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _canvas.Children.Add(bone);

                Ellipse fingerCircle = new Ellipse
                {
                    Width = circleSize,
                    Height = circleSize,
                    Fill = new SolidColorBrush(Color.FromArgb(145, 0, 122, 204)),
                    Stroke = Brushes.White,
                    StrokeThickness = 3.5
                };
                Canvas.SetLeft(fingerCircle, target.X - circleSize * 0.5);
                Canvas.SetTop(fingerCircle, target.Y - circleSize * 0.5);
                _canvas.Children.Add(fingerCircle);
                _handCircles.Add(fingerCircle);

                TextBlock lbl = new TextBlock
                {
                    Text = fingerNames[i],
                    Width = circleSize,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(lbl, target.X - circleSize * 0.5);
                Canvas.SetTop(lbl, target.Y - 16);
                _canvas.Children.Add(lbl);
                _handLabels.Add(lbl);
            }

            // Status & progress bar ispod sake
            double uiTop = Math.Min(h - 75, palmCenter.Y + scale * 0.78);
            _handCounterText = new TextBlock
            {
                Text = "Aktivno prstiju na šaci: 0 / 5",
                Width = 420,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(_handCounterText, cx - 210);
            Canvas.SetTop(_handCounterText, uiTop);
            _canvas.Children.Add(_handCounterText);

            _multiProgress = new ProgressBar
            {
                Width = 380,
                Height = 20,
                Minimum = 0,
                Maximum = 1.0,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(40, 185, 80)),
                Background = new SolidColorBrush(Color.FromRgb(42, 48, 60))
            };
            Canvas.SetLeft(_multiProgress, cx - 190);
            Canvas.SetTop(_multiProgress, uiTop + 32);
            _canvas.Children.Add(_multiProgress);

            _multitouchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _multitouchTimer.Tick += (s, e) =>
            {
                int matched = EvaluateHandMultitouch();
                if (matched >= 5)
                {
                    _multitouchHoldSeconds += 0.05;
                    _multiProgress.Value = Math.Min(1.0, _multitouchHoldSeconds);
                    if (_multitouchHoldSeconds >= 1.0)
                    {
                        _multitouchTimer.Stop();
                        _multitouchCompleted = true;
                        FinishPass();
                    }
                }
                else
                {
                    _multitouchHoldSeconds = Math.Max(0.0, _multitouchHoldSeconds - 0.08);
                    _multiProgress.Value = _multitouchHoldSeconds;
                }
            };
            _multitouchTimer.Start();
        }

        private int EvaluateHandMultitouch()
        {
            if (_phase != TouchPhase.MultitouchHand || _handTargets.Count != 5) return 0;

            bool[] targetHit = new bool[5];
            HashSet<int> usedTouchIds = new HashSet<int>();

            // Uparujemo svaku metu sa najblizim slobodnim prstom unutar _handCaptureRadius
            for (int pass = 0; pass < 5; pass++)
            {
                double bestDist = _handCaptureRadius;
                int bestTi = -1;
                int bestTouchId = -1;

                for (int ti = 0; ti < 5; ti++)
                {
                    if (targetHit[ti]) continue;
                    foreach (KeyValuePair<int, Point> kv in _activeTouches)
                    {
                        if (usedTouchIds.Contains(kv.Key)) continue;
                        double d = Distance(_handTargets[ti], kv.Value);
                        if (d <= bestDist)
                        {
                            bestDist = d;
                            bestTi = ti;
                            bestTouchId = kv.Key;
                        }
                    }
                }

                if (bestTi >= 0)
                {
                    targetHit[bestTi] = true;
                    usedTouchIds.Add(bestTouchId);
                }
                else
                {
                    break;
                }
            }

            int matchedCount = 0;
            for (int ti = 0; ti < 5; ti++)
            {
                if (targetHit[ti])
                {
                    matchedCount++;
                    _handCircles[ti].Fill = new SolidColorBrush(Color.FromArgb(210, 40, 185, 80));
                    _handCircles[ti].Stroke = new SolidColorBrush(Color.FromRgb(166, 255, 190));
                }
                else
                {
                    _handCircles[ti].Fill = new SolidColorBrush(Color.FromArgb(145, 0, 122, 204));
                    _handCircles[ti].Stroke = Brushes.White;
                }
            }

            // Osvezi zive tacke ispod svakog trenutno pritisnutog prsta
            for (int i = 0; i < _liveFingerMarkers.Count; i++)
            {
                _canvas.Children.Remove(_liveFingerMarkers[i]);
            }
            _liveFingerMarkers.Clear();

            foreach (KeyValuePair<int, Point> kv in _activeTouches)
            {
                Ellipse dot = new Ellipse
                {
                    Width = 28,
                    Height = 28,
                    Fill = new SolidColorBrush(Color.FromArgb(180, 90, 240, 210)),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, kv.Value.X - 14);
                Canvas.SetTop(dot, kv.Value.Y - 14);
                _canvas.Children.Add(dot);
                _liveFingerMarkers.Add(dot);
            }

            if (_handCounterText != null)
            {
                _handCounterText.Text = string.Format("Aktivno prstiju na šaci: {0} / 5 (ukupno dodira: {1})",
                    matchedCount, _activeTouches.Count);
                _handCounterText.Foreground = (matchedCount >= 5)
                    ? new SolidColorBrush(Color.FromRgb(78, 235, 125))
                    : Brushes.White;
            }

            return matchedCount;
        }

        private void OnScreenTouchDown(object sender, TouchEventArgs e)
        {
            if (_isClosingOrDone) return;
            Point pt = e.GetTouchPoint(_canvas).Position;

            if (_phase == TouchPhase.MultitouchHand)
            {
                _activeTouches[e.TouchDevice.Id] = pt;
                EvaluateHandMultitouch();
                return;
            }

            _pathActiveTouchIds.Add(e.TouchDevice.Id);
            _liveTrail.Clear();
            _liveTrail.Add(pt);
            UpdateLiveTrailVisual();
            AdvanceGuidedPath(pt);
        }

        private void OnScreenTouchMove(object sender, TouchEventArgs e)
        {
            if (_isClosingOrDone) return;
            Point pt = e.GetTouchPoint(_canvas).Position;

            if (_phase == TouchPhase.MultitouchHand)
            {
                _activeTouches[e.TouchDevice.Id] = pt;
                EvaluateHandMultitouch();
                return;
            }

            _liveTrail.Add(pt);
            if (_liveTrail.Count > 250)
            {
                _liveTrail.RemoveAt(0);
            }
            UpdateLiveTrailVisual();
            AdvanceGuidedPath(pt);
        }

        private void OnScreenTouchUp(object sender, TouchEventArgs e)
        {
            if (_phase == TouchPhase.MultitouchHand)
            {
                _activeTouches.Remove(e.TouchDevice.Id);
                EvaluateHandMultitouch();
                return;
            }

            _pathActiveTouchIds.Remove(e.TouchDevice.Id);
            if (_pathActiveTouchIds.Count == 0)
            {
                _liveTrail.Clear();
                UpdateLiveTrailVisual();
            }
        }

        private void OnScreenStylusDown(object sender, StylusDownEventArgs e)
        {
            ProcessStylusInput(e, true);
        }

        private void OnScreenStylusMove(object sender, StylusEventArgs e)
        {
            ProcessStylusInput(e, false);
        }

        private void ProcessStylusInput(StylusEventArgs e, bool isDown)
        {
            if (_isClosingOrDone || e.StylusDevice == null || e.StylusDevice.TabletDevice == null) return;
            if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus) return;

            StylusPointCollection pts = e.GetStylusPoints(_canvas);
            if (pts == null || pts.Count == 0) return;

            StylusPoint sp = pts[pts.Count - 1];
            Point pt = new Point(sp.X, sp.Y);
            float pressure = sp.PressureFactor;
            if (pressure > _maxStylusPressure) _maxStylusPressure = pressure;

            if (!_stylusDetected)
            {
                HwTestLogger.Info("TOUCHSCREEN", string.Format("Aktivna olovka detektovana (prvi kontakt, pritisak={0:0}%)", pressure * 100.0f));
            }
            _stylusDetected = true;

            if (_stylusBadge != null)
            {
                _stylusBadge.Text = string.Format("🖊️ Aktivna olovka: DETEKTOVANA ✓ (maks. pritisak: {0:0}%)", _maxStylusPressure * 100.0f);
                _stylusBadge.Foreground = new SolidColorBrush(Color.FromRgb(78, 235, 125));
            }

            if (_phase != TouchPhase.MultitouchHand)
            {
                if (isDown) _liveTrail.Clear();
                _liveTrail.Add(pt);
                if (_liveTrail.Count > 250)
                {
                    _liveTrail.RemoveAt(0);
                }
                UpdateLiveTrailVisual();
                AdvanceGuidedPath(pt);
            }
        }

        private void UpdateLiveTrailVisual()
        {
            if (_fingerTrailVisual == null) return;
            if (_liveTrail.Count < 2)
            {
                _fingerTrailVisual.Data = null;
                return;
            }
            _fingerTrailVisual.Data = BuildPathGeometry(_liveTrail, _liveTrail.Count);
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void StopMultitouchTimer()
        {
            if (_multitouchTimer != null)
            {
                try { _multitouchTimer.Stop(); } catch { }
                _multitouchTimer = null;
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
            StopMultitouchTimer();

            Result.Status = TestStatus.Pass;
            Result.Attempts = 1;
            Result.Details["serpentine_completed"] = _serpentineCompleted;
            Result.Details["infinity_completed"] = _infinityCompleted;
            Result.Details["multitouch_5finger_hand"] = _multitouchCompleted;
            Result.Details["stylus_expected"] = _hasStylusPen;
            Result.Details["stylus_detected"] = _stylusDetected;
            Result.Details["stylus_max_pressure"] = (double)Math.Round(_maxStylusPressure * 100.0f, 1);
            SafeCloseDialog(true);
        }

        private void FinishFail()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopMultitouchTimer();

            Result.Status = TestStatus.Fail;
            Result.Attempts = 1;
            Result.Reason = (_hwInfo != null && _hwInfo.TouchscreenDisabledInBios)
                ? ("Touchscreen onemogućen u BIOS-u: " + _hwInfo.TouchscreenBiosStatus)
                : "Ekran na dodir ne reaguje na unos";
            Result.Details["stylus_expected"] = _hasStylusPen;
            Result.Details["stylus_detected"] = _stylusDetected;
            Result.Details["stylus_max_pressure"] = (double)Math.Round(_maxStylusPressure * 100.0f, 1);
            SafeCloseDialog(false);
        }

        private void FinishSkip()
        {
            if (_isClosingOrDone) return;
            _isClosingOrDone = true;
            StopMultitouchTimer();

            Result.Status = TestStatus.Skip;
            Result.Reason = "Korisnik preskočio test ekrana na dodir";
            SafeCloseDialog(false);
        }
    }
}
