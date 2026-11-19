using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WIS.UI
{
    public class DeviceChangeMonitor : IDisposable
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private readonly MainWindow _mainWindow;
        private readonly MainViewModel _viewModel;
        private HwndSource _hwndSource;
        private DispatcherTimer _debounceTimer;
        private DispatcherTimer _pollingTimer;
        private bool _isDisposed;
        private int _isRefreshing = 0;
        private bool _pendingDirty = false;
        private HwndSourceHook _hook;

        private string _lastGpuFingerprint = "";
        private string _lastResolutionFingerprint = "";

        public DeviceChangeMonitor(MainWindow mainWindow, MainViewModel viewModel)
        {
            _mainWindow = mainWindow;
            _viewModel = viewModel;

            _debounceTimer = new DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromSeconds(3);
            _debounceTimer.Tick += OnDebounceTimerTick;

            _pollingTimer = new DispatcherTimer();
            _pollingTimer.Interval = TimeSpan.FromSeconds(30);
            _pollingTimer.Tick += OnPollingTimerTick;
        }

        public void Start(IntPtr hwnd)
        {
            if (_isDisposed) return;

            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    _hwndSource = HwndSource.FromHwnd(hwnd);
                    if (_hwndSource != null)
                    {
                        _hook = new HwndSourceHook(HwndHook);
                        _hwndSource.AddHook(_hook);
                    }
                }
            }
            catch { }

            _pollingTimer.Start();
        }

        public void PausePolling(bool pause)
        {
            if (_isDisposed) return;

            if (pause)
            {
                _pollingTimer.Stop();
            }
            else
            {
                _pollingTimer.Start();
            }
        }

        public void TriggerImmediateRefresh(HwCategory categories)
        {
            if (_isDisposed) return;
            ScheduleRefresh(categories);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int w = wParam.ToInt32();
                if (w == DBT_DEVNODES_CHANGED || w == DBT_DEVICEARRIVAL || w == DBT_DEVICEREMOVECOMPLETE || w == 0)
                {
                    TriggerDebouncedRefresh(HwCategory.Gpu | HwCategory.Display | HwCategory.Peripherals | HwCategory.Storage);
                }
            }
            return IntPtr.Zero;
        }

        public void TriggerDebouncedRefresh(HwCategory categories)
        {
            if (_isDisposed) return;

            // Debounce 3s: svaki novi događaj restartuje tajmer
            Action act = delegate
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            };

            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(act);
            }
            else
            {
                act();
            }
        }

        private void OnDebounceTimerTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            ScheduleRefresh(HwCategory.Gpu | HwCategory.Display | HwCategory.Peripherals | HwCategory.Storage);
        }

        private void OnPollingTimerTick(object sender, EventArgs e)
        {
            // Rezervni polling od 30s za lakši otisak: GPU, rezolucija
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string curRes = string.Format("{0}x{1}", (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
                    string curGpu = _viewModel != null ? _viewModel.GpuName : "";

                    bool changed = false;
                    HwCategory cats = HwCategory.None;

                    if (!string.IsNullOrEmpty(_lastResolutionFingerprint) && !string.Equals(_lastResolutionFingerprint, curRes, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        cats |= HwCategory.Display;
                    }
                    _lastResolutionFingerprint = curRes;

                    if (HardwareValueQuality.IsPlaceholder(curGpu) || (!string.IsNullOrEmpty(_lastGpuFingerprint) && !string.Equals(_lastGpuFingerprint, curGpu, StringComparison.OrdinalIgnoreCase)))
                    {
                        changed = true;
                        cats |= HwCategory.Gpu;
                    }
                    _lastGpuFingerprint = curGpu;

                    if (changed)
                    {
                        ScheduleRefresh(cats);
                    }
                }
                catch { }
            });
        }

        private void ScheduleRefresh(HwCategory categories)
        {
            if (_isDisposed) return;

            // Single-flight sa Interlocked i dirty bitom
            if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) == 0)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        do
                        {
                            _pendingDirty = false;
                            if (_mainWindow != null)
                            {
                                _mainWindow.RefreshCategories(categories);
                            }
                        }
                        while (_pendingDirty && !_isDisposed);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isRefreshing, 0);
                    }
                });
            }
            else
            {
                _pendingDirty = true;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer = null;
            }

            if (_pollingTimer != null)
            {
                _pollingTimer.Stop();
                _pollingTimer = null;
            }

            if (_hwndSource != null && _hook != null)
            {
                try
                {
                    _hwndSource.RemoveHook(_hook);
                }
                catch { }
                _hwndSource = null;
                _hook = null;
            }
        }
    }
}
