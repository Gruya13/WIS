using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WIS.HwTest.Core;
using WIS.HwTest.Interop;

namespace WIS.HwTest.Tests
{
    public class AutoTestDetailsWindow : Window
    {
        public bool RerunRequested { get; private set; }

        public AutoTestDetailsWindow(TestItemResult item)
        {
            RerunRequested = false;
            Title = "Detalji dijagnostike: " + item.Title;
            Width = 680;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(22, 24, 29));
            ResizeMode = ResizeMode.CanResize;
            SourceInitialized += (s, e) => NativeMethods.EnableDarkTitleBar(this);

            Grid root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header kartica
            Border hdr = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 35, 42)),
                BorderBrush = item.Status == TestStatus.Fail
                    ? new SolidColorBrush(Color.FromRgb(220, 53, 69))
                    : (item.HasWarning ? new SolidColorBrush(Color.FromRgb(255, 193, 7)) : new SolidColorBrush(Color.FromRgb(55, 62, 74))),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 12)
            };
            StackPanel spHdr = new StackPanel();
            spHdr.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            spHdr.Children.Add(new TextBlock
            {
                Text = string.Format("Status: {0}{1} | Trajanje: {2}s",
                    item.Status.ToString().ToUpperInvariant(),
                    item.HasWarning ? " (UPOZORENJE)" : "",
                    item.DurationSeconds),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = item.Status == TestStatus.Fail
                    ? new SolidColorBrush(Color.FromRgb(255, 110, 120))
                    : (item.HasWarning ? new SolidColorBrush(Color.FromRgb(255, 209, 102)) : new SolidColorBrush(Color.FromRgb(78, 201, 176))),
                Margin = new Thickness(0, 4, 0, 0)
            });
            if (!string.IsNullOrEmpty(item.MetricSummary))
            {
                spHdr.Children.Add(new TextBlock
                {
                    Text = item.MetricSummary,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 232)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
            if (!string.IsNullOrEmpty(item.Reason))
            {
                spHdr.Children.Add(new TextBlock
                {
                    Text = "Nalaz: " + item.Reason,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(185, 195, 208)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            hdr.Child = spHdr;
            Grid.SetRow(hdr, 0);
            root.Children.Add(hdr);

            // Tabela detalja
            ScrollViewer sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
                Padding = new Thickness(12)
            };
            StackPanel spRows = new StackPanel();
            if (item.Details != null && item.Details.Count > 0)
            {
                foreach (KeyValuePair<string, object> kv in item.Details)
                {
                    Border row = new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(42, 46, 54)),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(4, 7, 4, 7)
                    };
                    Grid rg = new Grid();
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    TextBlock tk = new TextBlock
                    {
                        Text = FormatKeyLabel(kv.Key),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(150, 175, 205))
                    };
                    rg.Children.Add(tk);

                    TextBlock tv = new TextBlock
                    {
                        Text = kv.Value != null ? kv.Value.ToString() : "",
                        FontSize = 12,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    };
                    Grid.SetColumn(tv, 1);
                    rg.Children.Add(tv);

                    row.Child = rg;
                    spRows.Children.Add(row);
                }
            }
            else
            {
                spRows.Children.Add(new TextBlock
                {
                    Text = "Još nema prikupljenih telemetrijskih detalja za ovaj test.",
                    Foreground = new SolidColorBrush(Color.FromRgb(160, 165, 175)),
                    FontSize = 13
                });
            }
            sv.Content = spRows;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);

            // Footer dugmad
            StackPanel spBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            Button btnRerun = new Button
            {
                Content = "🔄 Ponovi test",
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            btnRerun.Click += (s, e) => { RerunRequested = true; Close(); };
            spBtns.Children.Add(btnRerun);

            Button btnClose = new Button
            {
                Content = "Zatvori",
                Background = new SolidColorBrush(Color.FromRgb(65, 70, 80)),
                Foreground = Brushes.White,
                Padding = new Thickness(18, 8, 18, 8)
            };
            btnClose.Click += (s, e) => Close();
            spBtns.Children.Add(btnClose);

            Grid.SetRow(spBtns, 2);
            root.Children.Add(spBtns);

            Content = root;
        }

        private static string FormatKeyLabel(string key)
        {
            switch (key)
            {
                case "primary_model": return "Primarni disk";
                case "primary_media": return "Tip diska";
                case "disks_summary": return "Svi diskovi (SMART)";
                case "smart_predict_fail": return "SMART PredictFailure";
                case "uncorrected_errors": return "Neispravljive greške";
                case "wear_percent": return "Istrošenost (Wear %)";
                case "temp_c": return "Temperatura (°C)";
                case "seq_read_mbs": return "Seq Read (MB/s)";
                case "seq_write_mbs": return "Seq Write (MB/s)";
                case "rand_4k_iops": return "4K Random Read (IOPS)";
                case "free_gb": return "Slobodno prostora (GB)";
                case "cpu_name": return "Model procesora";
                case "cores": return "Fizička jezgra";
                case "logical_processors": return "Logičke niti (Threads)";
                case "base_clock_mhz": return "Bazni takt (MHz)";
                case "avg_clock_mhz": return "Prosečan takt (MHz)";
                case "peak_temp_c": return "Maks. temperatura (°C)";
                case "max_throttle_pct": return "Maks. Throttling (%)";
                case "max_fan_rpm": return "Ventilator (RPM)";
                case "worker_errors": return "Greške radnih niti";
                case "total_gb": return "Ukupan RAM (GB)";
                case "avail_mb": return "Slobodno pri startu (MB)";
                case "dimm_count": return "Broj modula";
                case "total_slots": return "Ukupno slotova";
                case "speed_mhz": return "Brzina RAM (MHz)";
                case "memory_type": return "Tip memorije";
                case "dimms": return "DIMM moduli";
                case "tested_mb": return "Testirano (MB)";
                case "bit_errors": return "Memorijske greške";
                case "bandwidth_gbs": return "Propusnost (GB/s)";
                case "gpu_name": return "Primarni GPU";
                case "driver_version": return "Verzija drajvera";
                case "vram_mb": return "VRAM (MB)";
                case "wpf_render_tier": return "WPF Render Tier";
                case "avg_fps": return "Prosečan 3D FPS";
                case "min_fps": return "Minimalni 3D FPS";
                case "max_fps": return "Maksimalni 3D FPS";
                case "peak_gpu_temp_c": return "Peak GPU Temp (°C)";
                case "all_adapters": return "Svi GPU adapteri";
                case "battery_name": return "Model baterije";
                case "health_percent": return "Zdravlje baterije (%)";
                case "design_mwh": return "Fabrički kapacitet (mWh)";
                case "full_charge_mwh": return "Trenutni kapacitet (mWh)";
                case "cycle_count": return "Broj ciklusa";
                case "charge_percent": return "Nivo napunjenosti (%)";
                case "ac_online": return "AC punjač priključen";
                case "wifi_adapters": return "Wi-Fi adapteri";
                case "lan_adapters": return "LAN adapteri";
                case "hub_ok": return "ServisHub dostupan";
                case "hub_latency_ms": return "ServisHub ping (ms)";
                case "gateway_ping_ms": return "Gateway ping (ms)";
                case "interfaces": return "Mrežni interfejsi";
                case "bios_password_state": return "BIOS Supervisor lozinka";
                case "computrace_status": return "Computrace / Absolute status";
                case "tpm_status": return "TPM 2.0 bezbednosni čip";
                case "secure_boot": return "Secure Boot stanje";
                case "oa3_oem_key": return "OEM Windows OA3 licenca";
                case "pnp_error_count": return "PnP greške drajvera (broj)";
                case "pnp_errors": return "Uređaji sa greškom (Code <> 0)";
                case "charge_rate_w": return "Snaga punjenja (W)";
                case "discharge_rate_w": return "Snaga pražnjenja (W)";
                case "voltage_v": return "Napon baterije (V)";
                case "wifi_visible_networks": return "Vidljive Wi-Fi mreže";
                case "wifi_max_signal_pct": return "Najjači Wi-Fi signal (%)";
                case "bluetooth_status": return "Bluetooth adapter status";
                case "bios_password_locked": return "BIOS zaključan lozinkom";
                case "computrace_active": return "Computrace / Absolute aktivan";
                case "battery_present": return "Baterija prisutna";
                case "is_charging": return "Punjenje aktivno";
                case "wmi_status": return "WMI status baterije";
                case "bluetooth_present": return "Bluetooth prisutan";
                case "any_link_up": return "Aktivna mrežna veza";
                case "gateway_ip": return "Gateway IP adresa";
                case "hub_url": return "ServisHub URL";
                case "system_drive": return "Sistemska particija";
                case "bench_executed": return "Benchmark izvršen";
                case "bench_error": return "Status benchmarka";
                case "idle_temp_c": return "Idle temperatura (°C)";
                case "iterations": return "Broj iteracija";
                case "sensor_source": return "Izvor termalnog senzora";
                case "pnp_error_code": return "Device Manager kod greške";
                case "is_basic_display": return "Basic Display drajver";
                case "total_frames": return "Ukupno 3D frejmova";
                default: return key;
            }
        }
    }
}
