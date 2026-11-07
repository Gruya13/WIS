using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace WIS.Launcher
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            string[] startupArgs = Environment.GetCommandLineArgs();
            if (startupArgs.Length == 4 && startupArgs[1] == "--apply-pending-update")
            {
                int parentPid;
                string updateError = "Neispravan PID aktivnog Launchera.";
                if (!int.TryParse(startupArgs[2], out parentPid) ||
                    !PayloadManager.ApplyPendingUpdateAfterExit(parentPid, startupArgs[3], out updateError))
                    MessageBox.Show(updateError, "WIS ažuriranje nije uspelo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
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

            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    SetProcessDPIAware();
                }
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Globalni rukovalac greskama za sprecavanje tihog pucanja
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    LogCrash("ThreadException", e.Exception);
                    Console.Beep(800, 300);
                }
                catch { }
                MessageBox.Show("WIS Launcher greška:\r\n" + e.Exception.Message, "Kritična Greška", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                try
                {
                    LogCrash("UnhandledException", ex);
                    Console.Beep(800, 300);
                }
                catch { }
                string msg = ex != null ? ex.Message : "Nepoznata greška";
                MessageBox.Show("WIS Launcher unhandled greška:\r\n" + msg, "Kritična Greška", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            bool createdNewInstance;
            using (System.Threading.Mutex singleInstanceMutex = new System.Threading.Mutex(true, @"Local\WIS_Launcher_SingleInstance_Mutex", out createdNewInstance))
            {
                if (!createdNewInstance)
                {
                    return;
                }
                Application.Run(new SplashForm());
            }
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string message = ex != null ? ex.Message : "Nepoznat izuzetak";
                string stackTrace = ex != null ? ex.StackTrace : "";
                string logText = string.Format("[{0}] [{1}] LAUNCHER CRASH: {2}\r\nStack: {3}\r\n----------------------------------------\r\n",
                    timestamp, source, message, stackTrace);

                string[] targets = new string[]
                {
                    @"C:\setup\wis_launcher_crash.log",
                    @"C:\Windows\Logs\WIS\launcher_crash.log"
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
    }
}
