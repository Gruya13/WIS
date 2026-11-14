// =============================================================================
// WIS - Štampanje nalepnice sa specifikacijom uređaja (WIS-TASK-188)
// -----------------------------------------------------------------------------
// - Nalepnica se renderuje kao WPF bitmapa širine 576 tačaka (72 mm štampe @ 203 dpi),
//   pa se šalje kao raster slika na Star TSP700II preko USB-a:
//     1) direktno na USB uređaj (GUID_DEVINTERFACE_USBPRINT, usbprint.sys —
//        ugrađen u Windows, drajver štampača NIJE potreban),
//     2) ili kao RAW posao na instalirani Windows red štampe (Star drajver),
//     3) ili (opciono) RAW TCP 9100 za mrežne štampače.
//   Podrazumevana emulacija je Star Line Mode (fabrički režim TSP700II);
//   ESC/POS raster (GS v 0) je dostupan kao alternativa.
// - Raster pristup omogućava srpska slova (č/ć/š/ž/đ) bez kodnih strana i
//   vertikalni ispis modela uz desnu ivicu.
// - Konfiguracija se čita iz [Printer] sekcije u Wifi-Servis.ini PRI STARTU
//   UI-ja i drži se samo u memoriji, jer se ini fajl briše u Fazi 6.
// - Napomena: kod je C# 5 (langversion:5) zbog Linux Mono Roslyn build-a.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WIS.UI
{
    // -------------------------------------------------------------------------
    // Konfiguracija štampača
    // -------------------------------------------------------------------------
    public class LabelPrinterConfig
    {
        public bool Enabled { get; set; }
        public string Transport { get; set; }   // Usb | Queue | Network
        public string Emulation { get; set; }   // StarLine | EscPos
        public string PrinterName { get; set; } // opciono: ime Windows reda štampe (Queue)
        public string Host { get; set; }
        public int Port { get; set; }
        public int Copies { get; set; }
        public int WidthDots { get; set; }
        public string ShopName { get; set; }
        public bool LoadedFromIni { get; set; }

        private static LabelPrinterConfig _current;
        private static readonly object _lock = new object();

        public LabelPrinterConfig()
        {
            Enabled = true;
            Transport = "Usb";
            Emulation = "StarLine";
            PrinterName = "";
            Host = "";
            Port = 9100;
            Copies = 1;
            WidthDots = 576;
            ShopName = "Tehno kutak";
            LoadedFromIni = false;
        }

        public bool IsStarLine
        {
            get { return !string.Equals(Emulation, "EscPos", StringComparison.OrdinalIgnoreCase); }
        }

        public static LabelPrinterConfig Current
        {
            get
            {
                lock (_lock)
                {
                    if (_current == null)
                    {
                        _current = LoadFromIni();
                    }
                    return _current;
                }
            }
        }

        /// <summary>
        /// Učitava [Printer] sekciju. Poziva se iz MainWindow konstruktora kako bi
        /// podaci ostali u memoriji i nakon brisanja Wifi-Servis.ini u Fazi 6.
        /// </summary>
        public static void Preload()
        {
            LabelPrinterConfig unused = Current;
        }

        private static LabelPrinterConfig LoadFromIni()
        {
            LabelPrinterConfig cfg = new LabelPrinterConfig();
            try
            {
                string[] candidates = new string[]
                {
                    @"C:\setup\Wifi-Servis.ini",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wifi-Servis.ini"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\Wifi-Servis.ini"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\Wifi-Servis.ini")
                };

                string iniPath = null;
                foreach (string p in candidates)
                {
                    if (File.Exists(p)) { iniPath = p; break; }
                }
                if (iniPath == null) return cfg;

                string section = "";
                foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    if (section != "printer") continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int sc = val.IndexOf(';');
                    if (sc >= 0) val = val.Substring(0, sc).Trim();

                    int n;
                    switch (key)
                    {
                        case "enabled":
                            cfg.Enabled = !(val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase));
                            cfg.LoadedFromIni = true;
                            break;
                        case "transport":
                            if (val.Length > 0) cfg.Transport = val;
                            break;
                        case "emulation":
                            if (val.Equals("EscPos", StringComparison.OrdinalIgnoreCase) || val.Equals("ESC/POS", StringComparison.OrdinalIgnoreCase)) cfg.Emulation = "EscPos";
                            else if (val.Length > 0) cfg.Emulation = "StarLine";
                            break;
                        case "printername":
                            cfg.PrinterName = val;
                            break;
                        case "host":
                            cfg.Host = val;
                            break;
                        case "port":
                            if (int.TryParse(val, out n) && n > 0 && n < 65536) cfg.Port = n;
                            break;
                        case "copies":
                            if (int.TryParse(val, out n) && n >= 1 && n <= 10) cfg.Copies = n;
                            break;
                        case "widthdots":
                            if (int.TryParse(val, out n) && n >= 256 && n <= 832) cfg.WidthDots = n - (n % 8);
                            break;
                        case "shopname":
                            if (val.Length > 0) cfg.ShopName = val;
                            break;
                    }
                }
            }
            catch { }
            return cfg;
        }
    }

    // -------------------------------------------------------------------------
    // Podaci nalepnice
    // -------------------------------------------------------------------------
    public class LabelRow
    {
        public string Caption { get; set; }
        public string Value { get; set; }
        public LabelRow(string caption, string value) { Caption = caption; Value = value; }
    }

    public class LabelData
    {
        public string Model { get; set; }
        public List<LabelRow> Rows { get; private set; }
        public string Footer { get; set; }
        public LabelData() { Rows = new List<LabelRow>(); Model = ""; Footer = ""; }
    }

    // -------------------------------------------------------------------------
    // Servis: prikupljanje, renderovanje, ESC/POS, slanje
    // -------------------------------------------------------------------------
    public static class LabelPrintService
    {
        private const string FontFamilyName = "Segoe UI";

        #region Prikupljanje podataka

        public static LabelData BuildFromViewModel(MainViewModel vm, LabelPrinterConfig cfg)
        {
            LabelData d = new LabelData();
            HardwareInfoViewModel hw = vm != null ? vm.HardwareInfo : null;

            string model = Clean(vm != null ? vm.ComputerModel : null);
            if (model.Length == 0 && hw != null) model = Clean(hw.MotherboardModel);
            if (model.Length == 0) model = "Nepoznat model";
            d.Model = model;

            string serial = Clean(vm != null ? vm.SerialNumber : null);
            if (serial.Length == 0 && hw != null) serial = Clean(hw.SerialNumber);
            if (serial.Length > 0) d.Rows.Add(new LabelRow("S/N", serial));

            if (hw != null)
            {
                string cpu = FormatCpu(hw.CpuModel, hw.CpuCoresThreads);
                if (cpu.Length > 0) d.Rows.Add(new LabelRow("CPU", cpu));

                string ram = FormatRam(hw.RamSummary, hw.RamSpeed);
                if (ram.Length > 0) d.Rows.Add(new LabelRow("RAM", ram));

                string disk = FormatDisk(hw.StorageModel, hw.StorageType, vm.DiskWear);
                if (disk.Length > 0) d.Rows.Add(new LabelRow("Disk", disk));

                AddGpuRow(d, hw.GpuPrimary, hw.GpuPrimaryVram);
                if (hw.HasDualGpu) AddGpuRow(d, hw.GpuSecondary, hw.GpuSecondaryVram);

                string disp = FormatDisplay(hw, vm);
                if (disp.Length > 0) d.Rows.Add(new LabelRow("Ekran", disp));

                string bat = FormatBatteryWear(hw.BatteryWear);
                if (bat.Length > 0) d.Rows.Add(new LabelRow("Baterija", bat));

                // WIS-TASK-189: Rezultat ručnog hardverskog testiranja
                if (vm != null && vm.IsHwTestFinished && !string.IsNullOrEmpty(vm.HwTestSummaryText))
                {
                    string hwLine = string.Format("{0} ({1})", vm.HwTestOverallStatus, vm.HwTestSummaryText);
                    d.Rows.Add(new LabelRow("HW test", hwLine));
                }
            }

            string shop = cfg != null && !string.IsNullOrEmpty(cfg.ShopName) ? cfg.ShopName : "Tehno kutak";
            d.Footer = string.Format("{0}  •  {1}", shop, DateTime.Now.ToString("dd.MM.yyyy.", CultureInfo.InvariantCulture));
            return d;
        }

        /// <summary>Vraća prazan string za placeholder/nepoznate vrednosti.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string t = s.Trim();
            string low = t.ToLowerInvariant();
            if (low == "n/a" || low == "none" || low == "nepoznato" || low.StartsWith("učitavanje") ||
                low.StartsWith("detekcija") || low.StartsWith("greška") || low.StartsWith("default string") ||
                low.StartsWith("to be filled") || low.StartsWith("system product name") || low == "standardni pc")
            {
                return "";
            }
            t = t.Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "")
                 .Replace("®", "").Replace("™", "");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t;
        }

        private static string FormatCpu(string cpuModel, string coresThreads)
        {
            string cpu = Clean(cpuModel);
            if (cpu.Length == 0) return "";
            cpu = Regex.Replace(cpu, @"\s*CPU\s*@\s*[\d\.]+\s*GHz", "", RegexOptions.IgnoreCase);
            cpu = Regex.Replace(cpu, @"\s*@\s*[\d\.]+\s*GHz", "", RegexOptions.IgnoreCase);
            cpu = Regex.Replace(cpu, @"\s+with\s+Radeon.*$", "", RegexOptions.IgnoreCase);
            cpu = Regex.Replace(cpu, @"\b\d+(th|st|nd|rd) Gen\s+", "", RegexOptions.IgnoreCase);
            cpu = Regex.Replace(cpu, @"\s+(\d+-Core|Processor)\b.*$", "", RegexOptions.IgnoreCase);
            cpu = cpu.Trim();

            string ct = Clean(coresThreads);
            Match m = Regex.Match(ct, @"(\d+)\D+(\d+)");
            if (m.Success)
            {
                cpu += string.Format(" ({0}C/{1}T)", m.Groups[1].Value, m.Groups[2].Value);
            }
            return cpu;
        }

        private static string FormatRam(string summary, string speed)
        {
            string s = Clean(summary);
            if (s.Length == 0) return "";
            // "16 GB RAM (DDR4)" -> "16 GB DDR4"
            Match m = Regex.Match(s, @"([\d\.,]+)\s*GB.*?\(([^)]+)\)", RegexOptions.IgnoreCase);
            string result;
            if (m.Success)
            {
                string type = m.Groups[2].Value.Trim();
                if (type.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || type.Equals("N/A", StringComparison.OrdinalIgnoreCase)) type = "";
                result = (m.Groups[1].Value + " GB " + type).Trim();
            }
            else
            {
                result = s.Replace(" RAM", "");
            }
            string sp = Clean(speed);
            if (sp.Length > 0) result += " " + sp;
            return result;
        }

        private static string FormatDisk(string storageModel, string storageType, int diskWear)
        {
            string modelRaw = Clean(storageModel);
            if (modelRaw.Length == 0) return "";

            string size = "";
            string name = modelRaw;
            Match m = Regex.Match(modelRaw, @"^(.*)\((\d+)\s*GB\)\s*$");
            if (m.Success)
            {
                name = m.Groups[1].Value.Trim();
                int gb;
                if (int.TryParse(m.Groups[2].Value, out gb))
                {
                    size = gb >= 1000 ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} TB", gb / 1000.0) : gb + " GB";
                }
            }

            string type = Clean(storageType);
            string shortType = "";
            if (type.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0) shortType = "NVMe";
            else if (type.IndexOf("SATA", StringComparison.OrdinalIgnoreCase) >= 0) shortType = "SATA";

            string result = string.Format("{0} {1}", size, shortType).Trim();
            if (name.Length > 0) result = (result + " " + name).Trim();
            if (diskWear > 0 && diskWear <= 100) result += string.Format(" (zdravlje {0}%)", diskWear);
            return result;
        }

        private static void AddGpuRow(LabelData d, string gpuName, string vram)
        {
            string name = Clean(gpuName);
            if (name.Length == 0) return;
            string low = name.ToLowerInvariant();
            if (low.StartsWith("nema") || low.Contains("basic display") || low.Contains("basic render") ||
                low.Contains("remote display") || low.Contains("parsec") || low.Contains("citrix") ||
                low.Contains("standardni vga") || low.Contains("standard vga") || low.Contains("virtual"))
            {
                return;
            }

            bool integrated = IsIntegratedGpu(name);
            string mem = FormatVram(vram, integrated);
            string value = mem.Length > 0 ? name + " • " + mem : name;
            d.Rows.Add(new LabelRow(integrated ? "iGPU" : "dGPU", value));
        }

        public static bool IsIntegratedGpu(string gpuName)
        {
            string l = (gpuName ?? "").ToLowerInvariant();
            if (l.Contains("nvidia") || l.Contains("geforce") || l.Contains("quadro") || l.Contains("rtx") ||
                l.Contains("arc a") || l.Contains("radeon rx") || l.Contains("radeon pro") || l.Contains(" rx "))
            {
                return false;
            }
            if (l.Contains("intel")) return true; // UHD / Iris / HD / Xe / Arc Graphics (iGPU)
            if (l.Contains("radeon graphics") || l.Contains("radeon(tm) graphics") || l.Contains("vega") ||
                l.Contains("radeon 610m") || l.Contains("radeon 660m") || l.Contains("radeon 680m") || l.Contains("radeon 780m"))
            {
                return true;
            }
            return false;
        }

        private static string FormatVram(string vram, bool integrated)
        {
            string v = Clean(vram);
            if (v.Length == 0) return integrated ? "deljena mem." : "";
            Match m = Regex.Match(v, @"(\d+)\s*(GB|MB)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string amount = m.Groups[1].Value + " " + m.Groups[2].Value.ToUpperInvariant();
                return integrated ? amount + " deljena" : amount;
            }
            return integrated ? "deljena mem." : "";
        }

        private static string FormatDisplay(HardwareInfoViewModel hw, MainViewModel vm)
        {
            int w = hw.DisplayWidth;
            int h = hw.DisplayHeight;
            int hz = hw.DisplayRefreshHz;

            if ((w <= 0 || h <= 0) && vm != null && !string.IsNullOrEmpty(vm.DisplayResolution))
            {
                Match m = Regex.Match(vm.DisplayResolution, @"(\d+)\s*[x×]\s*(\d+)");
                if (m.Success) { int.TryParse(m.Groups[1].Value, out w); int.TryParse(m.Groups[2].Value, out h); }
            }
            if (hz <= 0 && vm != null && !string.IsNullOrEmpty(vm.DisplayRefreshRate))
            {
                Match m = Regex.Match(vm.DisplayRefreshRate, @"(\d+)");
                if (m.Success) int.TryParse(m.Groups[1].Value, out hz);
            }
            if (w <= 0 || h <= 0) return "";

            // Maksimalna frekvencija panela za trenutnu rezoluciju (ne samo trenutno podešena)
            int maxHz = GetMaxRefreshRate(w, h);
            if (maxHz > hz) hz = maxHz;

            List<string> parts = new List<string>();
            parts.Add(ClassifyResolution(w, h));

            string panel = (hw.DisplayPanelType ?? "").Trim().ToUpperInvariant();
            if (panel == "IPS" || panel == "OLED") parts.Add(panel);

            if (hz > 0) parts.Add(hz + "Hz");
            if (hw.HasTouchscreen) parts.Add("Touch");
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>"12.3% Istrošenost (..)" -> "istrošenost 12.3%"; desktop / nepoznato -> "".</summary>
        private static string FormatBatteryWear(string batteryWear)
        {
            string b = Clean(batteryWear);
            if (b.Length == 0) return "";
            if (b.IndexOf("Nema baterije", StringComparison.OrdinalIgnoreCase) >= 0) return "";
            Match m = Regex.Match(b, @"([\d]+(?:[\.,]\d+)?)\s*%");
            if (!m.Success) return "";
            return string.Format("istrošenost {0}%", m.Groups[1].Value.Replace(',', '.'));
        }

        public static string ClassifyResolution(int w, int h)
        {
            int lw = Math.Max(w, h);
            int lh = Math.Min(w, h);
            if (lw == 1280 && lh == 720) return "HD";
            if (lw == 1366 && lh == 768) return "HD";
            if (lw == 1280 && lh == 800) return "WXGA";
            if (lw == 1600 && lh == 900) return "HD+";
            if (lw == 1440 && lh == 900) return "WXGA+";
            if (lw == 1680 && lh == 1050) return "WSXGA+";
            if (lw == 1920 && lh == 1080) return "FHD";
            if (lw == 1920 && lh == 1200) return "FHD+";
            if (lw == 1920 && lh == 1280) return "FHD+";
            if (lw == 2160 && lh == 1440) return "2K";
            if (lw == 2256 && lh == 1504) return "2.2K";
            if (lw == 2240 && lh == 1400) return "2.2K";
            if (lw == 2560 && lh == 1440) return "QHD";
            if (lw == 2560 && lh == 1600) return "QHD+";
            if (lw == 2880 && lh == 1800) return "3K";
            if (lw == 2880 && lh == 1620) return "3K";
            if (lw == 3000 && lh == 2000) return "3K";
            if (lw == 3200 && lh == 1800) return "QHD+";
            if (lw == 3200 && lh == 2000) return "3.2K";
            if (lw == 3456 && lh == 2160) return "3.5K";
            if (lw == 3840 && lh == 2160) return "4K UHD";
            if (lw == 3840 && lh == 2400) return "4K UHD+";
            return string.Format("{0}x{1}", lw, lh);
        }

        #endregion

        #region Maksimalna frekvencija ekrana (EnumDisplaySettings)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        public static int GetMaxRefreshRate(int width, int height)
        {
            int max = 0;
            try
            {
                DEVMODE dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                int i = 0;
                while (i < 2000 && EnumDisplaySettings(null, i, ref dm))
                {
                    if (dm.dmPelsWidth == width && dm.dmPelsHeight == height &&
                        dm.dmDisplayFrequency > max && dm.dmDisplayFrequency < 1000)
                    {
                        max = dm.dmDisplayFrequency;
                    }
                    i++;
                }
            }
            catch { }
            return max;
        }

        #endregion

        #region Renderovanje (WPF bitmapa)

        private class LabelVisual : DrawingVisual
        {
            public LabelVisual()
            {
                VisualTextRenderingMode = TextRenderingMode.Aliased;
                VisualTextHintingMode = TextHintingMode.Fixed;
            }
        }

        private static FormattedText MakeText(string text, double size, bool bold, double maxWidth, int maxLines)
        {
            Typeface tf = new Typeface(new FontFamily(FontFamilyName), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            FormattedText ft = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                tf, size, Brushes.Black, 1.0);
            if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
            if (maxLines > 0) { ft.MaxLineCount = maxLines; ft.Trimming = TextTrimming.CharacterEllipsis; }
            return ft;
        }

        /// <summary>Renderuje nalepnicu u crno-belu bitmapu tačne širine štampača.</summary>
        public static BitmapSource RenderLabel(LabelData d, int widthDots)
        {
            const double margin = 10;
            const double stripW = 60;          // desna traka sa vertikalnim modelom
            const double captionW = 104;
            const double bodyFont = 23;
            double W = widthDots;

            // --- merenje ---
            FormattedText title = MakeText(d.Model, 40, true, W - 2 * margin, 3);
            title.TextAlignment = TextAlignment.Center;

            double valueX = margin + captionW;
            double valueW = W - valueX - stripW - margin;
            List<FormattedText[]> rows = new List<FormattedText[]>();
            double bodyH = 0;
            foreach (LabelRow r in d.Rows)
            {
                FormattedText cap = MakeText(r.Caption + ":", bodyFont, true, captionW - 4, 1);
                FormattedText val = MakeText(r.Value, bodyFont, false, valueW, 3);
                rows.Add(new FormattedText[] { cap, val });
                bodyH += Math.Max(cap.Height, val.Height) + 8;
            }

            FormattedText bottom = MakeText(d.Model, 32, true, W - 2 * margin, 2);
            bottom.TextAlignment = TextAlignment.Center;
            FormattedText footer = MakeText(d.Footer, 19, false, W - 2 * margin, 1);
            footer.TextAlignment = TextAlignment.Center;

            // vertikalni model: širina teksta mora stati u visinu tela
            double minBody = 200;
            if (bodyH < minBody) bodyH = minBody;
            double vSize = 32;
            FormattedText vText = MakeText(d.Model, vSize, true, 0, 0);
            while (vText.WidthIncludingTrailingWhitespace > bodyH - 12 && vSize > 18)
            {
                vSize -= 1;
                vText = MakeText(d.Model, vSize, true, 0, 0);
            }
            if (vText.WidthIncludingTrailingWhitespace > bodyH - 12)
            {
                vText = MakeText(d.Model, vSize, true, bodyH - 12, 1);
            }

            double y = margin;
            double titleTop = y; y += title.Height + 8;
            double line1 = y; y += 3 + 10;
            double bodyTop = y; y += bodyH;
            double line2 = y + 2; y += 2 + 3 + 10;
            double bottomTop = y; y += bottom.Height + 4;
            double footerTop = y; y += footer.Height + margin;
            int H = (int)Math.Ceiling(y);

            // --- crtanje ---
            LabelVisual visual = new LabelVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                dc.DrawText(title, new Point(margin, titleTop));
                dc.DrawRectangle(Brushes.Black, null, new Rect(margin, line1, W - 2 * margin, 3));

                double ry = bodyTop;
                foreach (FormattedText[] pair in rows)
                {
                    dc.DrawText(pair[0], new Point(margin, ry));
                    dc.DrawText(pair[1], new Point(valueX, ry));
                    ry += Math.Max(pair[0].Height, pair[1].Height) + 8;
                }

                // desna traka: separator + rotiran model (čita se odozgo nadole)
                double stripX = W - margin - stripW;
                dc.DrawRectangle(Brushes.Black, null, new Rect(stripX + 4, bodyTop - 4, 2, bodyH));
                double textLen = vText.WidthIncludingTrailingWhitespace;
                double cx = stripX + 6 + (stripW - 6) / 2.0;
                double startY = bodyTop - 4 + (bodyH - textLen) / 2.0;
                dc.PushTransform(new TranslateTransform(cx + vText.Height / 2.0, startY));
                dc.PushTransform(new RotateTransform(90));
                dc.DrawText(vText, new Point(0, 0));
                dc.Pop();
                dc.Pop();

                dc.DrawRectangle(Brushes.Black, null, new Rect(margin, line2, W - 2 * margin, 3));
                dc.DrawText(bottom, new Point(margin, bottomTop));
                dc.DrawText(footer, new Point(margin, footerTop));
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(widthDots, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        #endregion

        #region Konverzija u 1-bit raster

        /// <summary>Bitmapa -> 1 bit po tački (MSB = levi piksel), redovi po (w+7)/8 bajtova.</summary>
        private static byte[] ToMonoRows(BitmapSource bmp, out int bytesPerRow, out int height)
        {
            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);

            bytesPerRow = (w + 7) / 8;
            height = h;
            byte[] mono = new byte[bytesPerRow * h];
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    int i = yy * stride + xx * 4;
                    int lum = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000;
                    if (lum < 140)
                    {
                        mono[yy * bytesPerRow + (xx >> 3)] |= (byte)(0x80 >> (xx & 7));
                    }
                }
            }
            return mono;
        }

        private static void W(MemoryStream ms, params byte[] b) { ms.Write(b, 0, b.Length); }

        private static void WAscii(MemoryStream ms, string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        public static byte[] BuildPrinterData(BitmapSource bmp, int copies, bool starLine)
        {
            return starLine ? BuildStarLine(bmp, copies) : BuildEscPos(bmp, copies);
        }

        #endregion

        #region Star Line Mode (Star TSP700II - fabrički režim)

        /// <summary>
        /// Star Line Mode raster: ESC * r A (ulaz), ESC * r P 0 NUL (kontinualna dužina),
        /// 'b' nL nH data po liniji, ESC * r B (izlaz), ESC d 3 (dopuni do noža + delimičan rez).
        /// </summary>
        public static byte[] BuildStarLine(BitmapSource bmp, int copies)
        {
            int bpr, h;
            byte[] mono = ToMonoRows(bmp, out bpr, out h);

            MemoryStream ms = new MemoryStream();
            if (copies < 1) copies = 1;
            for (int c = 0; c < copies; c++)
            {
                W(ms, 0x1B, 0x40);                          // ESC @  inicijalizacija
                W(ms, 0x1B, 0x2A, 0x72, 0x41);              // ESC * r A  ulaz u raster režim
                W(ms, 0x1B, 0x2A, 0x72, 0x50); WAscii(ms, "0"); W(ms, 0x00); // ESC * r P 0 NUL  kontinualno

                int blank = 0;
                for (int y = 0; y < h; y++)
                {
                    bool empty = true;
                    int off = y * bpr;
                    for (int k = 0; k < bpr; k++) { if (mono[off + k] != 0) { empty = false; break; } }

                    if (empty) { blank++; continue; }
                    if (blank > 0)
                    {
                        // ESC * r Y n NUL  vertikalni pomeraj za prazne linije
                        W(ms, 0x1B, 0x2A, 0x72, 0x59); WAscii(ms, blank.ToString(CultureInfo.InvariantCulture)); W(ms, 0x00);
                        blank = 0;
                    }
                    W(ms, 0x62, (byte)(bpr & 0xFF), (byte)(bpr >> 8)); // 'b' nL nH
                    ms.Write(mono, off, bpr);
                }
                if (blank > 0)
                {
                    W(ms, 0x1B, 0x2A, 0x72, 0x59); WAscii(ms, blank.ToString(CultureInfo.InvariantCulture)); W(ms, 0x00);
                }

                W(ms, 0x1B, 0x2A, 0x72, 0x42);              // ESC * r B  izlaz iz raster režima
                W(ms, 0x1B, 0x64, 0x03);                    // ESC d 3  dopuni do noža + delimičan rez
            }
            return ms.ToArray();
        }

        #endregion

        #region ESC/POS (alternativna emulacija)

        /// <summary>ESC/POS raster (GS v 0) + feed + rez.</summary>
        public static byte[] BuildEscPos(BitmapSource bmp, int copies)
        {
            int bytesPerRow, h;
            byte[] mono = ToMonoRows(bmp, out bytesPerRow, out h);

            MemoryStream ms = new MemoryStream();
            if (copies < 1) copies = 1;
            for (int c = 0; c < copies; c++)
            {
                W(ms, 0x1B, 0x40);                          // ESC @
                W(ms, 0x1B, 0x61, 0x00);                    // ESC a 0
                const int band = 128;
                for (int y0 = 0; y0 < h; y0 += band)
                {
                    int bh = Math.Min(band, h - y0);
                    W(ms, 0x1D, 0x76, 0x30, 0x00,
                        (byte)(bytesPerRow & 0xFF), (byte)(bytesPerRow >> 8),
                        (byte)(bh & 0xFF), (byte)(bh >> 8));
                    ms.Write(mono, y0 * bytesPerRow, bh * bytesPerRow);
                }
                W(ms, 0x1B, 0x64, 0x04);                    // ESC d 4
                W(ms, 0x1D, 0x56, 0x42, 0x00);              // GS V B 0
            }
            return ms.ToArray();
        }

        #endregion

        #region Pronalaženje štampača (USB / Windows red / mreža)

        public const string KindUsb = "USB";
        public const string KindQueue = "Queue";
        public const string KindNetwork = "Network";

        public class PrintTarget
        {
            public string Kind { get; set; }
            public string Path { get; set; }      // USB device path / ime reda / host
            public int Port { get; set; }
            public string Display { get; set; }
            public bool IsStar { get; set; }
            public override string ToString() { return Display; }
        }

        // GUID_DEVINTERFACE_USBPRINT (usbprint.sys - ugrađen u Windows, drajver štampača nije potreban)
        private static Guid UsbPrintGuid = new Guid("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const int SPDRP_DEVICEDESC = 0x00;
        private const int SPDRP_FRIENDLYNAME = 0x0C;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfoSet, IntPtr devInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, int detailSize, out int requiredSize, ref SP_DEVINFO_DATA devInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devInfoSet, ref SP_DEVINFO_DATA devInfoData, int property, out int regDataType, byte[] buffer, int bufferSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped);

        private static string GetDevProp(IntPtr set, ref SP_DEVINFO_DATA dev, int prop)
        {
            try
            {
                byte[] buf = new byte[1024];
                int type, req;
                if (SetupDiGetDeviceRegistryProperty(set, ref dev, prop, out type, buf, buf.Length, out req))
                {
                    return Encoding.Unicode.GetString(buf, 0, Math.Max(0, Math.Min(req, buf.Length))).TrimEnd('\0').Trim();
                }
            }
            catch { }
            return "";
        }

        private static List<PrintTarget> FindUsbPrinters()
        {
            List<PrintTarget> list = new List<PrintTarget>();
            IntPtr set = IntPtr.Zero;
            try
            {
                set = SetupDiGetClassDevs(ref UsbPrintGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

                for (int i = 0; i < 32; i++)
                {
                    SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                    ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref UsbPrintGuid, i, ref ifData)) break;

                    SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                    devData.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out required, ref devData);
                    if (required <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize za SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, required, out required, ref devData)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        string name = GetDevProp(set, ref devData, SPDRP_FRIENDLYNAME);
                        if (name.Length == 0) name = GetDevProp(set, ref devData, SPDRP_DEVICEDESC);
                        string low = path.ToLowerInvariant();
                        bool isStar = low.Contains("vid_0519") || name.IndexOf("Star", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      name.IndexOf("TSP", StringComparison.OrdinalIgnoreCase) >= 0;

                        string vidPid = "";
                        Match m = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
                        if (m.Success) vidPid = string.Format("VID_{0} PID_{1}", m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant());

                        string label = isStar ? "Star Micronics (TSP700II)" : (name.Length > 0 ? name : "USB štampač");
                        list.Add(new PrintTarget
                        {
                            Kind = KindUsb,
                            Path = path,
                            IsStar = isStar,
                            Display = string.Format("USB: {0}{1}", label, vidPid.Length > 0 ? " [" + vidPid + "]" : "")
                        });
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            catch { }
            finally
            {
                if (set != IntPtr.Zero && set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }

        private static List<PrintTarget> FindQueuePrinters()
        {
            List<PrintTarget> list = new List<PrintTarget>();
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT Name, PortName, DriverName FROM Win32_Printer"))
                {
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        string name = o["Name"] != null ? o["Name"].ToString() : "";
                        string port = o["PortName"] != null ? o["PortName"].ToString() : "";
                        string drv = o["DriverName"] != null ? o["DriverName"].ToString() : "";
                        if (name.Length == 0) continue;
                        string all = (name + " " + drv + " " + port).ToLowerInvariant();
                        if (all.Contains("pdf") || all.Contains("xps") || all.Contains("onenote") || all.Contains("fax") || all.Contains("portprompt")) continue;

                        bool isStar = all.Contains("star") || all.Contains("tsp");
                        list.Add(new PrintTarget
                        {
                            Kind = KindQueue,
                            Path = name,
                            IsStar = isStar,
                            Display = string.Format("Windows: {0} ({1})", name, port)
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Redosled: Star USB direktno → Star Windows red → ostali USB → ostali redovi → mreža.</summary>
        public static List<PrintTarget> DiscoverTargets(LabelPrinterConfig cfg)
        {
            List<PrintTarget> usb = FindUsbPrinters();
            List<PrintTarget> queues = FindQueuePrinters();
            List<PrintTarget> result = new List<PrintTarget>();

            bool preferQueue = cfg != null && string.Equals(cfg.Transport, "Queue", StringComparison.OrdinalIgnoreCase);
            if (cfg != null && !string.IsNullOrEmpty(cfg.PrinterName))
            {
                foreach (PrintTarget q in queues)
                {
                    if (string.Equals(q.Path, cfg.PrinterName, StringComparison.OrdinalIgnoreCase)) { result.Add(q); preferQueue = true; }
                }
            }

            if (preferQueue)
            {
                foreach (PrintTarget t in queues) if (t.IsStar && !result.Contains(t)) result.Add(t);
                foreach (PrintTarget t in usb) if (t.IsStar) result.Add(t);
            }
            else
            {
                foreach (PrintTarget t in usb) if (t.IsStar) result.Add(t);
                foreach (PrintTarget t in queues) if (t.IsStar && !result.Contains(t)) result.Add(t);
            }
            foreach (PrintTarget t in usb) if (!t.IsStar) result.Add(t);
            foreach (PrintTarget t in queues) if (!t.IsStar && !result.Contains(t)) result.Add(t);

            if (cfg != null && !string.IsNullOrEmpty(cfg.Host))
            {
                PrintTarget net = new PrintTarget
                {
                    Kind = KindNetwork,
                    Path = cfg.Host,
                    Port = cfg.Port,
                    Display = string.Format("Mreža: {0}:{1}", cfg.Host, cfg.Port)
                };
                if (string.Equals(cfg.Transport, "Network", StringComparison.OrdinalIgnoreCase)) result.Insert(0, net);
                else result.Add(net);
            }
            return result;
        }

        #endregion

        #region Slanje

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string printerName, out IntPtr hPrinter, IntPtr defaults);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 docInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, byte[] buffer, int count, out int written);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        private static string SendUsbDirect(string devicePath, byte[] data)
        {
            const uint GENERIC_WRITE = 0x40000000;
            const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
            const uint OPEN_EXISTING = 3;
            using (Microsoft.Win32.SafeHandles.SafeFileHandle h = CreateFile(devicePath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    return string.Format("USB štampač nije dostupan (Win32 greška {0}). Proverite kabl i da li je štampač uključen.", Marshal.GetLastWin32Error());
                }
                int offset = 0;
                const int chunk = 4096;
                while (offset < data.Length)
                {
                    int len = Math.Min(chunk, data.Length - offset);
                    byte[] part = new byte[len];
                    Buffer.BlockCopy(data, offset, part, 0, len);
                    int written;
                    if (!WriteFile(h, part, len, out written, IntPtr.Zero) || written <= 0)
                    {
                        return string.Format("Greška pri upisu na USB (Win32 {0}). Proverite papir/poklopac štampača.", Marshal.GetLastWin32Error());
                    }
                    offset += written;
                }
            }
            return null;
        }

        private static string SendToQueue(string printerName, byte[] data)
        {
            IntPtr h;
            if (!OpenPrinter(printerName, out h, IntPtr.Zero))
            {
                return string.Format("Ne mogu da otvorim štampač „{0}“ (Win32 {1}).", printerName, Marshal.GetLastWin32Error());
            }
            try
            {
                DOC_INFO_1 di = new DOC_INFO_1 { pDocName = "WIS nalepnica", pOutputFile = null, pDataType = "RAW" };
                if (StartDocPrinter(h, 1, di) == 0)
                {
                    return string.Format("Štampač „{0}“ odbija RAW posao (Win32 {1}).", printerName, Marshal.GetLastWin32Error());
                }
                try
                {
                    StartPagePrinter(h);
                    int written;
                    bool ok = WritePrinter(h, data, data.Length, out written);
                    EndPagePrinter(h);
                    if (!ok || written != data.Length)
                    {
                        return string.Format("Greška pri slanju na „{0}“ (Win32 {1}).", printerName, Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    EndDocPrinter(h);
                }
            }
            finally
            {
                ClosePrinter(h);
            }
            return null;
        }

        private static string SendNetwork(string host, int port, byte[] data, int timeoutMs)
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    return string.Format("Štampač {0}:{1} ne odgovara (timeout {2}s).", host, port, timeoutMs / 1000);
                }
                client.EndConnect(ar);
                client.SendTimeout = 10000;
                using (NetworkStream ns = client.GetStream())
                {
                    ns.Write(data, 0, data.Length);
                    ns.Flush();
                }
            }
            return null;
        }

        /// <summary>Šalje podatke na izabrani štampač. Vraća null ako je uspešno, inače poruku greške.</summary>
        public static async Task<string> SendAsync(PrintTarget target, byte[] data)
        {
            if (target == null) return "Štampač nije izabran.";
            Task<string> work = Task.Run(delegate
            {
                try
                {
                    if (target.Kind == KindUsb) return SendUsbDirect(target.Path, data);
                    if (target.Kind == KindQueue) return SendToQueue(target.Path, data);
                    return SendNetwork(target.Path, target.Port > 0 ? target.Port : 9100, data, 3000);
                }
                catch (Exception ex)
                {
                    return "Greška pri štampi: " + ex.Message;
                }
            });

            // USB upis može da blokira ako je štampač bez papira/otvoren — ne zamrzavaj UI
            Task done = await Task.WhenAny(work, Task.Delay(20000));
            if (done != work) return "Štampač ne prima podatke (20 s). Proverite papir, poklopac i kabl.";
            return work.Result;
        }

        #endregion
    }

    // -------------------------------------------------------------------------
    // Dijalog pregleda i štampe (kreiran iz koda — Linux build ne prevodi XAML)
    // -------------------------------------------------------------------------
    public class LabelPrintWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly LabelPrinterConfig _cfg;
        private BitmapSource _bitmap;
        private readonly Image _preview;
        private readonly ComboBox _targetBox;
        private readonly ComboBox _emulationBox;
        private readonly TextBox _copiesBox;
        private readonly TextBlock _hint;
        private readonly TextBlock _status;
        private readonly Button _printBtn;

        private static string L(string key, string fallback)
        {
            return LocalizationService.Get(key, fallback);
        }

        private static SolidColorBrush B(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public LabelPrintWindow(Window owner, MainViewModel vm)
        {
            _vm = vm;
            _cfg = LabelPrinterConfig.Current;

            Owner = owner;
            Title = L("LabelWindowTitle", "Štampa nalepnice sa specifikacijom");
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Background = B(0x18, 0x24, 0x1D);
            Foreground = Brushes.White;
            Topmost = owner != null && owner.Topmost;

            Grid root = new Grid { Margin = new Thickness(18) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            // Levo: pregled
            Border previewBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = B(0x2D, 0x6A, 0x4F),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 18, 0)
            };
            _preview = new Image { Width = 346, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            ScrollViewer sv = new ScrollViewer
            {
                Content = _preview,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            previewBorder.Child = sv;
            Grid.SetColumn(previewBorder, 0);
            root.Children.Add(previewBorder);

            // Desno: podešavanja
            StackPanel right = new StackPanel();
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            right.Children.Add(new TextBlock
            {
                Text = L("LabelWindowHeader", "🖨 Nalepnica (Star TSP700II, 80 mm)"),
                FontSize = 16, FontWeight = FontWeights.Bold, Foreground = B(0x2E, 0xCC, 0x71),
                Margin = new Thickness(0, 0, 0, 12)
            });

            right.Children.Add(Caption(L("LabelPrinterTarget", "Štampač")));
            _targetBox = new ComboBox { FontSize = 12, Margin = new Thickness(0, 2, 0, 10), MinHeight = 26 };
            right.Children.Add(_targetBox);

            right.Children.Add(Caption(L("LabelEmulation", "Emulacija štampača")));
            _emulationBox = new ComboBox { FontSize = 12, Margin = new Thickness(0, 2, 0, 10), MinHeight = 26 };
            _emulationBox.Items.Add("Star Line Mode (TSP700II fabrički)");
            _emulationBox.Items.Add("ESC/POS");
            _emulationBox.SelectedIndex = _cfg.IsStarLine ? 0 : 1;
            right.Children.Add(_emulationBox);

            right.Children.Add(Caption(L("LabelCopies", "Broj kopija")));
            _copiesBox = new TextBox
            {
                Text = _cfg.Copies.ToString(CultureInfo.InvariantCulture), FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
                Background = B(0x12, 0x1A, 0x15), Foreground = Brushes.White, BorderBrush = B(0x2D, 0x6A, 0x4F),
                CaretBrush = Brushes.White, Margin = new Thickness(0, 2, 0, 10), Width = 80, HorizontalAlignment = HorizontalAlignment.Left
            };
            right.Children.Add(_copiesBox);

            _hint = new TextBlock
            {
                FontSize = 11, Foreground = B(0xF1, 0xC4, 0x0F), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed
            };
            right.Children.Add(_hint);

            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _printBtn = new Button
            {
                Content = L("BtnPrintLabelNow", "🖨 Štampaj"),
                Background = B(0x1F, 0x6F, 0xEB), Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = 13, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0),
                BorderBrush = B(0x38, 0x8B, 0xFD), Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true
            };
            _printBtn.Click += OnPrintClick;
            btns.Children.Add(_printBtn);

            Button refreshBtn = new Button
            {
                Content = L("BtnRefreshLabel", "↻ Osveži"),
                Background = B(0x21, 0x26, 0x2D), Foreground = Brushes.White, FontSize = 13,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 10, 0),
                BorderBrush = B(0x3E, 0x3E, 0x42), Cursor = System.Windows.Input.Cursors.Hand
            };
            refreshBtn.Click += delegate { RenderPreview(); RefreshTargets(); };
            btns.Children.Add(refreshBtn);

            Button closeBtn = new Button
            {
                Content = L("BtnCloseLabel", "Zatvori"),
                Background = B(0x21, 0x26, 0x2D), Foreground = Brushes.White, FontSize = 13,
                Padding = new Thickness(12, 8, 12, 8),
                BorderBrush = B(0x3E, 0x3E, 0x42), Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true
            };
            closeBtn.Click += delegate { Close(); };
            btns.Children.Add(closeBtn);
            right.Children.Add(btns);

            _status = new TextBlock
            {
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
                Foreground = B(0xCC, 0xCC, 0xCC)
            };
            right.Children.Add(_status);

            Content = root;
            RenderPreview();
            Loaded += delegate { RefreshTargets(); };
        }

        private TextBlock Caption(string text)
        {
            return new TextBlock { Text = text, FontSize = 11, Foreground = B(0x88, 0x88, 0x88) };
        }

        private async void RefreshTargets()
        {
            _targetBox.IsEnabled = false;
            _printBtn.IsEnabled = false;
            _targetBox.ItemsSource = null;
            _status.Foreground = B(0xCC, 0xCC, 0xCC);
            _status.Text = L("LabelSearching", "Tražim USB štampač...");

            List<LabelPrintService.PrintTarget> targets;
            try
            {
                targets = await Task.Run(delegate { return LabelPrintService.DiscoverTargets(_cfg); });
            }
            catch
            {
                targets = new List<LabelPrintService.PrintTarget>();
            }

            _targetBox.ItemsSource = targets;
            _targetBox.IsEnabled = true;
            _status.Text = "";

            if (targets.Count > 0)
            {
                _targetBox.SelectedIndex = 0;
                _printBtn.IsEnabled = true;
                bool anyStar = false;
                foreach (LabelPrintService.PrintTarget t in targets) if (t.IsStar) { anyStar = true; break; }
                if (!anyStar)
                {
                    ShowHint(L("LabelStarNotFoundHint", "Star TSP700II nije prepoznat. Proverite da li je izabran pravi štampač."));
                }
                else
                {
                    _hint.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ShowHint(L("LabelNoPrinterHint",
                    "USB štampač nije pronađen. Proverite kabl i da je štampač uključen, pa kliknite „Osveži“. " +
                    "Ako se ni tada ne pojavi, štampač je verovatno u USB „Vendor Class“ režimu — prebacite ga u „Printer Class“ ili instalirajte Star TSP700II drajver."));
            }
        }

        private void ShowHint(string text)
        {
            _hint.Text = "⚠ " + text;
            _hint.Visibility = Visibility.Visible;
        }

        private void RenderPreview()
        {
            try
            {
                LabelData data = LabelPrintService.BuildFromViewModel(_vm, _cfg);
                _bitmap = LabelPrintService.RenderLabel(data, _cfg.WidthDots);
                _preview.Source = _bitmap;
            }
            catch (Exception ex)
            {
                _bitmap = null;
                SetStatus(false, L("LabelRenderError", "Greška pri pripremi nalepnice: ") + ex.Message);
            }
        }

        private void SetStatus(bool ok, string text)
        {
            _status.Foreground = ok ? B(0x2E, 0xCC, 0x71) : B(0xF8, 0x51, 0x49);
            _status.Text = (ok ? "✅ " : "❌ ") + text;
        }

        private async void OnPrintClick(object sender, RoutedEventArgs e)
        {
            LabelPrintService.PrintTarget target = _targetBox.SelectedItem as LabelPrintService.PrintTarget;
            if (target == null)
            {
                SetStatus(false, L("LabelNoTarget", "Izaberite štampač."));
                return;
            }
            int copies;
            if (!int.TryParse((_copiesBox.Text ?? "").Trim(), out copies) || copies < 1) copies = 1;
            if (copies > 10) copies = 10;
            bool starLine = _emulationBox.SelectedIndex != 1;

            if (_bitmap == null) RenderPreview();
            if (_bitmap == null) return;

            _printBtn.IsEnabled = false;
            _status.Foreground = B(0xCC, 0xCC, 0xCC);
            _status.Text = string.Format(L("LabelSending", "Šaljem na {0}..."), target.Display);

            string err;
            try
            {
                byte[] payload = LabelPrintService.BuildPrinterData(_bitmap, copies, starLine);
                err = await LabelPrintService.SendAsync(target, payload);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            _printBtn.IsEnabled = true;

            if (err == null)
            {
                // Zapamti izbor za ovu sesiju (samo u memoriji)
                _cfg.Copies = copies;
                _cfg.Emulation = starLine ? "StarLine" : "EscPos";
                SetStatus(true, string.Format(L("LabelPrinted", "Poslato na {0} (kopija: {1})."), target.Display, copies));
                if (_vm != null) _vm.AddLog("SUCCESS", "LabelPrint", string.Format("Nalepnica poslata na {0} ({1} kom, {2}).", target.Display, copies, starLine ? "Star Line" : "ESC/POS"));
            }
            else
            {
                SetStatus(false, err);
                if (_vm != null) _vm.AddLog("WARN", "LabelPrint", err);
            }
        }
    }
}
