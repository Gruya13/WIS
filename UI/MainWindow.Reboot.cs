using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WIS.UI
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Prilagođava završni panel (BAML iz prethodne kompilacije) tako da dugmad prelaze
        /// u WrapPanel i prošireni Border (MinWidth 680 / MaxWidth 920 + ScrollViewer),
        /// kako bi svih 5 opcija uvek bilo potpuno vidljivo bez isecanja.
        /// </summary>
        private void EnsureCompletionOverlayResponsiveLayout()
        {
            if (_completionLayoutAdapted) return;
            try
            {
                List<Button> anchors = new List<Button>();
                CollectButtonsByCommandPath(this, "CleanAndShutdownCommand", anchors);
                if (anchors.Count == 0) return;

                foreach (Button anchor in anchors)
                {
                    bool isLarge = anchor.FontSize >= 13;
                    Panel origPanel = anchor.Parent as Panel;
                    if (origPanel == null) continue;

                    // Ako je roditelj još uvek StackPanel, zameni ga sa WrapPanel-om da se dugmad ne seku van okvira
                    if (origPanel is StackPanel)
                    {
                        Panel grandParent = origPanel.Parent as Panel;
                        if (grandParent != null)
                        {
                            WrapPanel wrap = new WrapPanel
                            {
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = isLarge ? HorizontalAlignment.Center : HorizontalAlignment.Right,
                                VerticalAlignment = origPanel.VerticalAlignment,
                                Margin = origPanel.Margin
                            };

                            System.Windows.Data.Binding visBinding = System.Windows.Data.BindingOperations.GetBinding(origPanel, UIElement.VisibilityProperty);
                            if (visBinding != null)
                            {
                                System.Windows.Data.BindingOperations.SetBinding(wrap, UIElement.VisibilityProperty, visBinding);
                            }
                            else
                            {
                                wrap.Visibility = origPanel.Visibility;
                            }

                            List<UIElement> children = new List<UIElement>();
                            foreach (object child in origPanel.Children)
                            {
                                UIElement ue = child as UIElement;
                                if (ue != null) children.Add(ue);
                            }
                            origPanel.Children.Clear();
                            for (int i = 0; i < children.Count; i++)
                            {
                                Button childBtn = children[i] as Button;
                                if (childBtn != null && isLarge)
                                {
                                    childBtn.Padding = new Thickness(16, 10, 16, 10);
                                    childBtn.Margin = new Thickness(6, 5, 6, 5);
                                }
                                wrap.Children.Add(children[i]);
                            }

                            int gIdx = grandParent.Children.IndexOf(origPanel);
                            grandParent.Children.RemoveAt(gIdx);
                            grandParent.Children.Insert(gIdx < 0 ? 0 : gIdx, wrap);
                        }
                    }

                    // Za završni prozor (overlay karticu): ukloni fiksnu širinu 680 i umotaj sadržaj u ScrollViewer
                    if (isLarge)
                    {
                        DependencyObject cur = anchor.Parent;
                        while (cur != null)
                        {
                            Border b = cur as Border;
                            if (b != null && (b.Width == 680 || b.MinWidth == 680))
                            {
                                b.Width = double.NaN;
                                b.MinWidth = 680;
                                b.MaxWidth = 920;
                                b.Margin = new Thickness(20);
                                if (!(b.Child is ScrollViewer))
                                {
                                    UIElement innerContent = b.Child;
                                    b.Child = null;
                                    ScrollViewer sv = new ScrollViewer
                                    {
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                        Content = innerContent
                                    };
                                    b.Child = sv;
                                }
                                break;
                            }
                            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
                        }
                    }
                }
                _completionLayoutAdapted = true;
            }
            catch { }
        }

        private void HandleInterimReboot()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.AddLog("WARN", "TaskRunner", "Faza 3 uspešno završena. Pripremam međurestart radi rasterećenja CBS/MSI podsistema...");
                ViewModel.LiveStatus = "Kontrolisani međurestart za 5 sekundi...";
            }));

            // 1. Zapis stanja phase: 4 u C:\setup\deploy-state.json
            SaveDeployStatePhase(4);

            // 2. Obezbeđivanje RunOnce unosa za WIS.UI.exe --master
            EnsureRunOnceEntry();

            // 3. Nalaz 14 (Paket C2): Realno poštovanje odloženog restarta (+10m Restart) i pred-restart odbrojavanje
            bool readyForReboot = false;
            while (!readyForReboot)
            {
                DateTime dueUtc = GetScheduledRestartDueUtc();
                while (dueUtc > DateTime.UtcNow)
                {
                    TimeSpan remaining = dueUtc - DateTime.UtcNow;
                    if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
                    int remMin = (int)Math.Floor(remaining.TotalMinutes);
                    int remSec = remaining.Seconds;
                    string dueLocalStr = dueUtc.ToLocalTime().ToString("HH:mm:ss");
                    string waitMsg = string.Format("Međurestart odložen od strane servisera — preostalo {0:D2}:{1:D2} (do {2})", remMin, remSec, dueLocalStr);

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.SubPhase = string.Format("Međurestart odložen (do {0})", dueLocalStr);
                        ViewModel.LiveStatus = waitMsg;
                    }));

                    Thread.Sleep(1000);
                    dueUtc = GetScheduledRestartDueUtc();
                }

                // Pred-restart odbrojavanje (5 sekundi) sa proverom odlaganja u svakoj sekundi
                bool delayedDuringCountdown = false;
                for (int sec = 5; sec >= 1; sec--)
                {
                    if (GetScheduledRestartDueUtc() > DateTime.UtcNow)
                    {
                        delayedDuringCountdown = true;
                        break;
                    }
                    int currentSec = sec;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.LiveStatus = string.Format("Kontrolisani međurestart za {0}s... (Kliknite '+10m Restart' za odlaganje)", currentSec);
                    }));
                    Thread.Sleep(1000);
                }

                if (!delayedDuringCountdown && GetScheduledRestartDueUtc() <= DateTime.UtcNow)
                {
                    readyForReboot = true;
                }
            }

            lock (_restartScheduleLock)
            {
                _restartPending = false;
                _restartDueAtUtc = DateTime.MinValue;
            }
            SaveDeployStatePhase(4);

            // U2 (Paket C2): Čisto zaustavi Screen Capture worker pre međurestarta
            StopScreenCaptureWorker();

            // 4. Pokretanje kontrolisanog restarta računara
            try
            {
                Process.Start("shutdown.exe", "/r /t 5 /f /c \"WIS: Kontrolisani međurestart radi stabilizacije CBS/MSI podsistema.\"");
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    ViewModel.AddLog("ERROR", "TaskRunner", "Greška pri pozivu shutdown.exe: " + ex.Message);
                }));
            }

            Thread.Sleep(1500);
            Dispatcher.BeginInvoke((Action)(() => Close()));
        }

        private void EnsureRunOnceEntry()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!File.Exists(exePath)) exePath = @"C:\setup\Skripte\WIS.UI\WIS.UI.exe";

                string launcherPath = @"C:\setup\WIS.Launcher.exe";
                // WIS-TASK-244: Kad je WIS.UI.exe već prisutan na disku, na restartu pokrećemo direktno WIS.UI.exe --master bez ponovnog Launchera.
                string resumeTarget = File.Exists(exePath)
                    ? string.Format("\"{0}\" --master", exePath)
                    : (File.Exists(launcherPath)
                        ? string.Format("\"{0}\" --master", launcherPath)
                        : string.Format("\"{0}\" --master", @"C:\setup\Skripte\WIS.UI\WIS.UI.exe"));

                // 1. Čišćenje konkurentskih Startup skripti koje mogu izazvati paralelno pokretanje
                try
                {
                    string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    if (string.IsNullOrEmpty(startupDir)) startupDir = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
                    if (Directory.Exists(startupDir))
                    {
                        string[] obsoleteCmds = new string[] { "WIS-AutoStart.cmd", "WIS-Resume.cmd", "WIS-DesktopStart.cmd", "GWADT-AutoStart.cmd", "GWADT-Resume.cmd" };
                        foreach (string cmdName in obsoleteCmds)
                        {
                            string cmdPath = Path.Combine(startupDir, cmdName);
                            if (File.Exists(cmdPath))
                            {
                                try { File.Delete(cmdPath); } catch { }
                            }
                        }
                    }
                }
                catch { }

                // 2. Čišćenje svih zaostalih / konkurentskih RunOnce ključeva iz HKLM i HKCU
                string[] staleKeys = new string[] { "WIS_AutoStart", "WIS_Launcher", "ServisDeployResume", "GWADT_Resume", "GWADT_AutoStart" };
                bool hklmSuccess = false;

                // 3. Primarni upis isključivo u HKLM
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    {
                        if (key != null)
                        {
                            foreach (string stale in staleKeys)
                            {
                                try { key.DeleteValue(stale, false); } catch { }
                            }
                            key.SetValue("WIS_Resume", resumeTarget);
                            hklmSuccess = true;
                        }
                    }
                }
                catch { }

                // 4. HKCU rukovanje: ako je HKLM uspeo, očisti HKCU unos da Explorer ne bi startovao dvaput!
                // Ako HKLM nije uspeo, koristi HKCU kao fallback.
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    {
                        if (key != null)
                        {
                            foreach (string stale in staleKeys)
                            {
                                try { key.DeleteValue(stale, false); } catch { }
                            }

                            if (hklmSuccess)
                            {
                                // HKLM je već postavljen — obavezno ukloni iz HKCU da sprečiš dvostruko pokretanje!
                                try { key.DeleteValue("WIS_Resume", false); } catch { }
                            }
                            else
                            {
                                // HKLM nije uspeo — postavi HKCU fallback
                                key.SetValue("WIS_Resume", resumeTarget);
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        private void ClearRunOnceEntries()
        {
            try
            {
                string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                if (string.IsNullOrEmpty(startupDir)) startupDir = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
                if (Directory.Exists(startupDir))
                {
                    string[] obsoleteCmds = new string[] { "WIS-AutoStart.cmd", "WIS-Resume.cmd", "WIS-DesktopStart.cmd", "GWADT-AutoStart.cmd", "GWADT-Resume.cmd" };
                    foreach (string cmdName in obsoleteCmds)
                    {
                        string cmdPath = Path.Combine(startupDir, cmdName);
                        if (File.Exists(cmdPath))
                        {
                            try { File.Delete(cmdPath); } catch { }
                        }
                    }
                }
            }
            catch { }

            string[] keysToClear = new string[] { "WIS_Resume", "WIS_AutoStart", "WIS_Launcher", "ServisDeployResume", "GWADT_Resume", "GWADT_AutoStart" };
            Microsoft.Win32.RegistryKey[] hives = new Microsoft.Win32.RegistryKey[]
            {
                Microsoft.Win32.Registry.LocalMachine,
                Microsoft.Win32.Registry.CurrentUser
            };
            for (int h = 0; h < hives.Length; h++)
            {
                try
                {
                    using (var key = hives[h].OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    {
                        if (key != null)
                        {
                            foreach (string k in keysToClear)
                            {
                                try { key.DeleteValue(k, false); } catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void ExecuteShowCompletionSummary(object param)
        {
            ViewModel.IsCompleted = true;
            EnsureCompletionOverlayResponsiveLayout();
            EnsureLabelPrintButtons();
            EnsureHwTestButtons();
            EnsureEmbeddedHwTestTab();
        }

        private void ExecuteCancelShutdown(object param)
        {
            ViewModel.ShutdownTimerActive = false;
            ViewModel.IsCompleted = false; // Skloni completion overlay da se mogu pregledati tabovi
            ViewModel.IsServiceFinished = true; // Trajno zadrži završnu traku u dnu ekrana
            ViewModel.AddLog("INFO", "Korisnik", "Izabran ostanak u programu radi pregleda. Dugme [🔄 OČISTI I RESTARTUJ RAČUNAR] je dostupno u donjoj traci.");

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "cancel_shutdown" },
                { "timestamp", DateTime.Now.ToString("o") }
            });
        }

        private void ExecuteShutdownNow(object param)
        {
            ViewModel.ShutdownTimerActive = false;
            ViewModel.AddLog("WARN", "Korisnik", "Zahtevano trenutno gašenje računara.");

            StopScreenCaptureWorker();
            ClearRunOnceEntries();

            SendIpcMessage(new Dictionary<string, object>
            {
                { "action", "shutdown_now" },
                { "timestamp", DateTime.Now.ToString("o") }
            });

            try
            {
                Process.Start("shutdown.exe", "/s /t 1 /c \"WIS: Trenutno gasenje na zahtev tehnicara.\"");
            }
            catch { }

            Close();
        }

        private void ExecuteCleanAndShutdown(object param)
        {
            ViewModel.AddLog("INFO", "Sistem", "Čišćenje servisnih tragova i restartovanje računara...");
            ViewModel.CompletionTitle = "ČIŠĆENJE I RESTART...";
            ViewModel.CompletionSummary = "Čišćenje servisnih tragova i restartovanje računara...";

            ThreadPool.QueueUserWorkItem(delegate(object _)
            {
                try
                {
                    // U2 (Paket C2): Prvo čisto zaustavi Screen Capture worker pre brisanja C:\setup
                    StopScreenCaptureWorker();
                    ClearRunOnceEntries();

                    // a) Briše lozinku i Wi-Fi profile: ako postoji C:\setup\Wifi-Servis.ini briše ga
                    string wifiIni = @"C:\setup\Wifi-Servis.ini";
                    if (File.Exists(wifiIni))
                    {
                        try { File.Delete(wifiIni); } catch { }
                    }

                    // Briše i privremeni Registry ključ HKLM\SOFTWARE\WIS za InitialStartTime
                    try
                    {
                        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\WIS", false);
                    }
                    catch { }

                    // b) Agresivno briše prečice sa radne površine (Desktop):
                    // Pretražuje C:\Users\Public\Desktop i direktorijume C:\Users\*\Desktop i briše:
                    // *Launcher*.lnk, *WIS*.lnk, *WIS*.*, *GWADT*.lnk, *GWADT*.*, *Pokreni*.bat, *Pokreni*.lnk, setup.lnk
                    string[] patterns = new string[]
                    {
                        "*Launcher*.lnk",
                        "*WIS*.lnk",
                        "*WIS*.*",
                        "*GWADT*.lnk",
                        "*GWADT*.*",
                        "*Pokreni*.bat",
                        "*Pokreni*.lnk",
                        "setup.lnk"
                    };

                    List<string> desktopDirs = new List<string>();
                    string publicDesktop = @"C:\Users\Public\Desktop";
                    if (Directory.Exists(publicDesktop))
                    {
                        desktopDirs.Add(publicDesktop);
                    }

                    string usersDir = @"C:\Users";
                    if (Directory.Exists(usersDir))
                    {
                        try
                        {
                            string[] userDirs = Directory.GetDirectories(usersDir);
                            for (int i = 0; i < userDirs.Length; i++)
                            {
                                string userDesktop = Path.Combine(userDirs[i], "Desktop");
                                if (Directory.Exists(userDesktop) && !desktopDirs.Contains(userDesktop))
                                {
                                    desktopDirs.Add(userDesktop);
                                }
                            }
                        }
                        catch { }
                    }

                    for (int d = 0; d < desktopDirs.Count; d++)
                    {
                        string dir = desktopDirs[d];
                        for (int p = 0; p < patterns.Length; p++)
                        {
                            try
                            {
                                string[] files = Directory.GetFiles(dir, patterns[p]);
                                for (int f = 0; f < files.Length; f++)
                                {
                                    try { File.Delete(files[f]); } catch { }
                                }
                            }
                            catch { }
                        }
                    }

                    // c) Poziva osvežavanje Explorer shell-a (SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero))
                    try
                    {
                        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }

                    // d) Pokreće odloženo brisanje C:\setup i restart:
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c timeout /t 2 /nobreak >nul & rmdir /s /q C:\\setup >nul 2>&1 & shutdown.exe /r /t 3 /c \"WIS: Servis zavrsen, ociscen i spreman za restart.\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                    }
                    catch { }
                }
                catch { }

                // e) Zatvara prozor Close() i gasi proces
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { Close(); } catch { }
                    try { Environment.Exit(0); } catch { }
                }));
            });
        }

        private void ExecuteExitWithoutClean(object param)
        {
            ViewModel.AddLog("INFO", "Korisnik", "Izlazak iz aplikacije bez čišćenja servisnih tragova.");
            StopScreenCaptureWorker();
            ClearRunOnceEntries();
            try { Close(); } catch { }
            try { Environment.Exit(0); } catch { }
        }
    }
}
