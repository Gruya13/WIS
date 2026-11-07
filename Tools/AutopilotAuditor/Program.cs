using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace WIS.AutopilotAuditor
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        public static int Main(string[] args)
        {
            bool isCliMode = false;
            bool isJsonOutput = false;
            string exportHtmlPath = null;
            string exportHashPath = null;

            // Analiza argumenata komandne linije
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant().Trim();
                if (arg == "--help" || arg == "-h" || arg == "/?")
                {
                    TryAttachOrAllocConsole();
                    PrintHelp();
                    return 0;
                }
                else if (arg == "--cli" || arg == "-c")
                {
                    isCliMode = true;
                }
                else if (arg == "--json" || arg == "-j")
                {
                    isCliMode = true;
                    isJsonOutput = true;
                }
                else if ((arg == "--export-html" || arg == "-html") && i + 1 < args.Length)
                {
                    isCliMode = true;
                    exportHtmlPath = args[++i];
                }
                else if ((arg == "--export-hash" || arg == "-hash") && i + 1 < args.Length)
                {
                    isCliMode = true;
                    exportHashPath = args[++i];
                }
            }

            // Ako je zatražen CLI režim ili eksport preko komandne linije
            if (isCliMode)
            {
                TryAttachOrAllocConsole();

                try
                {
                    if (!isJsonOutput)
                    {
                        Console.WriteLine("\n[+] Pokrećem Microsoft Autopilot Forensic Auditor v2.0...");
                        Console.WriteLine("[*] Skeniram hardver i WMI parametre...");
                    }

                    HardwareDetails hw = HardwareInspector.Inspect();

                    if (!isJsonOutput)
                    {
                        Console.WriteLine("[*] Vrsim lokalnu sistemsku i registarsku forenziku...");
                    }

                    LocalForensicsDetails local = LocalForensicsInspector.Inspect();

                    if (!isJsonOutput)
                    {
                        Console.WriteLine("[*] Kontaktiram Microsoft ZTD servis i proveravam Event Log...");
                    }

                    CloudZtdDetails cloud = CloudZtdInspector.Inspect(hw.SerialNumber);

                    AuditorResult result = DecisionMatrix.Evaluate(hw, local, cloud);

                    // Izvoz u HTML ako je traženo
                    if (!string.IsNullOrEmpty(exportHtmlPath))
                    {
                        try
                        {
                            ReportExporter.ExportHtml(result, exportHtmlPath);
                            if (!isJsonOutput)
                                Console.WriteLine(string.Format("[✓] HTML izveštaj uspešno sačuvan u: {0}", exportHtmlPath));
                        }
                        catch (Exception ex)
                        {
                            if (!isJsonOutput)
                                Console.WriteLine(string.Format("[-] Greška pri snimanju HTML-a: {0}", ex.Message));
                        }
                    }

                    // Izvoz u CSV heš ako je traženo
                    if (!string.IsNullOrEmpty(exportHashPath))
                    {
                        try
                        {
                            ReportExporter.ExportHardwareHashCsv(result, exportHashPath);
                            if (!isJsonOutput)
                                Console.WriteLine(string.Format("[✓] Hardware Hash CSV uspešno sačuvan u: {0}", exportHashPath));
                        }
                        catch (Exception ex)
                        {
                            if (!isJsonOutput)
                                Console.WriteLine(string.Format("[-] Greška pri snimanju CSV-a: {0}", ex.Message));
                        }
                    }

                    // Ispis rezultata
                    if (isJsonOutput)
                    {
                        Console.WriteLine(result.ToJson());
                    }
                    else
                    {
                        Console.WriteLine(ReportExporter.GetFormattedConsoleText(result));
                    }

                    // Deterministički Exit Code
                    switch (result.MatrixCode)
                    {
                        case MatrixStatus.CONFIRMED_CLEAN:
                            return 0; // 100% Čisto
                        case MatrixStatus.LOCKED_TENANT:
                        case MatrixStatus.HIDDEN_LOCK:
                            return 1; // Zaključano na firmu
                        case MatrixStatus.RELEASED_LOCAL_TRACES:
                        case MatrixStatus.UNVERIFIED_LOCAL_TRACES:
                            return 2; // Postoje lokalni tragovi
                        default:
                            return 3; // Offline ili Neodređeno
                    }
                }
                finally
                {
                    TryFreeConsole();
                }
            }
            else
            {
                // GUI Režim (dvoklik u Exploreru)
                try
                {
                    return LaunchGui();
                }
                catch (Exception ex)
                {
                    // Fallback na konzolu ako WPF nije podržan (npr. WinPE okruženje)
                    TryAttachOrAllocConsole();
                    Console.WriteLine("[!] Upozorenje: Grafički interfejs (WPF) nije dostupan na ovom sistemu (WinPE/Minimal OS): " + ex.Message);
                    Console.WriteLine("[*] Automatski prelazim na konzolni režim...\n");
                    
                    HardwareDetails hw = HardwareInspector.Inspect();
                    LocalForensicsDetails local = LocalForensicsInspector.Inspect();
                    CloudZtdDetails cloud = CloudZtdInspector.Inspect(hw.SerialNumber);
                    AuditorResult result = DecisionMatrix.Evaluate(hw, local, cloud);
                    Console.WriteLine(ReportExporter.GetFormattedConsoleText(result));
                    return result.IsLocked ? 1 : 0;
                }
            }
        }

        private static void TryAttachOrAllocConsole()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    if (!AttachConsole(ATTACH_PARENT_PROCESS))
                    {
                        AllocConsole();
                    }
                }
            }
            catch { }
        }

        private static void TryFreeConsole()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    FreeConsole();
                }
            }
            catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int LaunchGui()
        {
            Application app = new Application();
            MainWindow window = new MainWindow();
            return app.Run(window);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
================================================================================
               MICROSOFT AUTOPILOT FORENSIC AUDITOR v2.0
                        Tehno kutak WIS Suite
================================================================================
Upotreba:
  WIS-AutopilotAuditor.exe [OPCIJE]

Opcije:
  (bez argumenata)       Pokreće moderni grafički prozor (WPF GUI).
  --cli, -c              Pokreće direktnu analizu u konzoli sa formatiranim ispisom.
  --json, -j             Vraća kompaktan JSON izveštaj na standardni izlaz (za skripte).
  --export-html <fajl>   Automatski generiše stilizovani HTML servisni izveštaj.
  --export-hash <fajl>   Generiše 4K Hardware Hash CSV fajl za uvoz u Microsoft Intune.
  --help, -h             Prikazuje ovo uputstvo.

Exit kodovi (CLI):
  0 = Potvrđeno slobodan i čist uređaj (CONFIRMED_CLEAN)
  1 = Zaključan za korporativni tenant (LOCKED_TENANT ili HIDDEN_LOCK)
  2 = Pronađeni lokalni tragovi bivše organizacije (RELEASED_LOCAL_TRACES)
  3 = Offline režim ili status nije eksplicitno potvrđen u oblaku
");
        }
    }
}
