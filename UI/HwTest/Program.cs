using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using WIS.HwTest.Core;

namespace WIS.HwTest
{
    public class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => {
                try
                {
                    string shortName = new System.Reflection.AssemblyName(eventArgs.Name).Name;
                    string resourceName = null;
                    if (string.Equals(shortName, "zxing", StringComparison.OrdinalIgnoreCase))
                        resourceName = "Assets.zxing.dll";

                    if (resourceName != null)
                    {
                        using (System.IO.Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
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

            string outPath = @"C:\setup\hwtest-result.json";
            string modelName = "Nepoznat model";
            string serialNumber = "Nepoznat serijski broj";
            string manufacturer = "";
            string sku = "";
            string hubUrl = "";
            string lang = "sr";
            bool standalone = false;
            bool hasExplicitOut = false;

            // Parsiranje argumenata
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    hasExplicitOut = true;
                    outPath = args[++i];
                }
                else if (arg.Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    modelName = args[++i];
                }
                else if (arg.Equals("--serial", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    serialNumber = args[++i];
                }
                else if (arg.Equals("--manufacturer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    manufacturer = args[++i];
                }
                else if (arg.Equals("--sku", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    sku = args[++i];
                }
                else if (arg.Equals("--hub", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    hubUrl = args[++i];
                }
                else if (arg.Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    lang = args[++i];
                }
                else if (arg.Equals("--standalone", StringComparison.OrdinalIgnoreCase))
                {
                    standalone = true;
                }
            }

            if (!hasExplicitOut)
            {
                standalone = true;
            }

            ModelProfileClient bootClient = new ModelProfileClient(string.IsNullOrEmpty(hubUrl) ? null : hubUrl);
            HwTestLogger.Initialize(bootClient.HubBaseUrl);
            HwTestLogger.Info("ARGS", string.Format("Model={0} | Serial={1} | Mfg={2} | SKU={3} | Standalone={4}",
                modelName, serialNumber, manufacturer, sku, standalone));

            if (standalone)
            {
                // Standalone režim: bez watchdog gašenja
            }
            else
            {
                // Watchdog: gašenje procesa nakon 30 minuta neaktivnosti
                Thread watchdog = new Thread(() => {
                    Thread.Sleep(30 * 60 * 1000);
                    HwTestLogger.Warn("WATCHDOG", "Istekao 30-minutni watchdog tajmer, zatvaram aplikaciju.");
                    HwTestLogger.FlushAndStop();
                    Environment.Exit(3);
                }) { IsBackground = true };
                watchdog.Start();
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Exception ex = e.ExceptionObject as Exception;
                string msg = ex != null ? (ex.Message + " | " + ex.StackTrace) : Convert.ToString(e.ExceptionObject);
                HwTestLogger.Error("FATAL", msg);
                HwTestLogger.FlushAndStop();
                try
                {
                    string logPath = @"C:\setup\hwtest-crash.log";
                    System.IO.File.AppendAllText(logPath, string.Format("[{0}] FATAL: {1}\n\n",
                        DateTime.Now.ToString("o"), msg));
                }
                catch { }
            };

            // Pokretanje WPF Aplikacije
            Application app = new Application();
            app.DispatcherUnhandledException += (s, e) => {
                HwTestLogger.Error("CRASH", e.Exception.Message + " | " + e.Exception.StackTrace);
                HwTestLogger.FlushAndStop();
                try
                {
                    string logPath = @"C:\setup\hwtest-crash.log";
                    System.IO.File.AppendAllText(logPath, string.Format("[{0}] CRASH: {1}\n{2}\n\n",
                        DateTime.Now.ToString("o"), e.Exception.Message, e.Exception.StackTrace));
                }
                catch { }
                e.Handled = true;
            };

            HwTestShell shell = new HwTestShell(outPath, modelName, serialNumber, manufacturer, sku, bootClient);
            app.Run(shell);

            HwTestLogger.Info("EXIT", "WIS.HwTest završio rad sa ExitCode=" + Environment.ExitCode);
            HwTestLogger.FlushAndStop();
            return Environment.ExitCode;
        }
    }
}
