using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WIS.UI
{
    public partial class App : Application
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static Mutex _appMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                try
                {
                    string shortName = new System.Reflection.AssemblyName(eventArgs.Name).Name;
                    if (string.Equals(shortName, "zxing", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Assets.zxing.dll"))
                        {
                            if (s != null)
                            {
                                byte[] buf = new byte[s.Length];
                                int offset = 0;
                                while (offset < buf.Length)
                                {
                                    int read = s.Read(buf, offset, buf.Length - offset);
                                    if (read <= 0) break;
                                    offset += read;
                                }
                                return System.Reflection.Assembly.Load(buf);
                            }
                        }
                    }
                }
                catch { }
                return null;
            };

            // 0. Striktna procesna provera: ako već postoji aktivan WIS.UI proces, dovedi ga u prvi plan i odmah ugasi ovu instancu
            try
            {
                Process current = Process.GetCurrentProcess();
                Process[] instances = Process.GetProcessesByName(current.ProcessName);
                if (instances.Length > 1)
                {
                    foreach (Process p in instances)
                    {
                        if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            try
                            {
                                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                                SetForegroundWindow(p.MainWindowHandle);
                            }
                            catch { }
                            break;
                        }
                    }
                    Environment.Exit(0);
                    return;
                }
            }
            catch { }

            if (!IsRunningAsAdministrator())
            {
                try
                {
                    ProcessStartInfo procInfo = new ProcessStartInfo
                    {
                        FileName = Environment.GetCommandLineArgs()[0],
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    // Prenesi sve argumente komandne linije (npr. --master)
                    string[] args = Environment.GetCommandLineArgs();
                    if (args.Length > 1)
                    {
                        List<string> subArgs = new List<string>();
                        for (int i = 1; i < args.Length; i++) subArgs.Add(args[i]);
                        procInfo.Arguments = string.Join(" ", subArgs.ToArray());
                    }
                    Process.Start(procInfo);
                    Environment.Exit(0);
                    return;
                }
                catch
                {
                    // Korisnik je odbio UAC ili je doslo do greske
                }
            }

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += (s, ev) => { ev.SetObserved(); };

            bool createdNew;
            _appMutex = new Mutex(true, @"Global\WIS_UI_SingleInstance_Mutex", out createdNew);
            if (!createdNew)
            {
                Environment.Exit(0);
                return;
            }
            base.OnStartup(e);
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                LogCrash("DispatcherUnhandledException", e.Exception);
            }
            catch { }
            e.Handled = true; // Tihi oporavak - sprečiti rušenje UI procesa!
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                LogCrash("CurrentDomain_UnhandledException", ex);
            }
            catch { }
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string message = ex != null ? ex.Message : "Nepoznat izuzetak";
                string stackTrace = ex != null ? ex.StackTrace : "";
                string logText = string.Format("[{0}] [{1}] CRASH RECOVERED: {2}\r\nStack: {3}\r\n----------------------------------------\r\n",
                    timestamp, source, message, stackTrace);

                string[] targets = new string[]
                {
                    @"C:\setup\wis_ui_crash.log",
                    @"C:\Windows\Logs\WIS\ui_crash.log"
                };

                for (int i = 0; i < targets.Length; i++)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(targets[i]);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.AppendAllText(targets[i], logText, Encoding.UTF8);
                    }
                    catch { }
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_appMutex != null)
            {
                try { _appMutex.ReleaseMutex(); _appMutex.Close(); } catch { }
                _appMutex = null;
            }
            base.OnExit(e);
        }
    }
}
