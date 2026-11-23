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
            Title = HwTestLocalization.Pick("Detalji dijagnostike: ", "诊断详情: ") + item.Title;
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
                Text = HwTestLocalization.PickFormat("Status: {0}{1} | Trajanje: {2}s",
                    "状态: {0}{1} | 耗时: {2}s",
                    item.Status.ToString().ToUpperInvariant(),
                    item.HasWarning ? HwTestLocalization.Pick(" (UPOZORENJE)", " (警告)") : "",
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
                    Text = HwTestLocalization.Pick("Nalaz: ", "诊断说明: ") + item.Reason,
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
                    Text = HwTestLocalization.Pick("Još nema prikupljenih telemetrijskih detalja za ovaj test.", "暂无此测试的遥测详细信息。"),
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
                Content = HwTestLocalization.Pick("🔄 Ponovi test", "🔄 重新测试"),
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
                Content = HwTestLocalization.Pick("Zatvori", "关闭"),
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
                case "primary_model": return HwTestLocalization.Pick("Primarni disk", "主硬盘型号");
                case "primary_media": return HwTestLocalization.Pick("Tip diska", "硬盘类型");
                case "disks_summary": return HwTestLocalization.Pick("Svi diskovi (SMART)", "所有硬盘 (SMART)");
                case "smart_predict_fail": return HwTestLocalization.Pick("SMART PredictFailure", "SMART 故障预测");
                case "uncorrected_errors": return HwTestLocalization.Pick("Neispravljive greške", "不可纠正错误");
                case "wear_percent": return HwTestLocalization.Pick("Istrošenost (Wear %)", "损耗度 (Wear %)");
                case "temp_c": return HwTestLocalization.Pick("Temperatura (°C)", "温度 (°C)");
                case "seq_read_mbs": return HwTestLocalization.Pick("Seq Read (MB/s)", "顺序读取 (MB/s)");
                case "seq_write_mbs": return HwTestLocalization.Pick("Seq Write (MB/s)", "顺序写入 (MB/s)");
                case "rand_4k_iops": return HwTestLocalization.Pick("4K Random Read (IOPS)", "4K 随机读取 (IOPS)");
                case "free_gb": return HwTestLocalization.Pick("Slobodno prostora (GB)", "可用空间 (GB)");
                case "cpu_name": return HwTestLocalization.Pick("Model procesora", "处理器型号");
                case "cores": return HwTestLocalization.Pick("Fizička jezgra", "物理核心数");
                case "logical_processors": return HwTestLocalization.Pick("Logičke niti (Threads)", "逻辑线程数");
                case "base_clock_mhz": return HwTestLocalization.Pick("Bazni takt (MHz)", "基准频率 (MHz)");
                case "avg_clock_mhz": return HwTestLocalization.Pick("Prosečan takt (MHz)", "平均频率 (MHz)");
                case "peak_temp_c": return HwTestLocalization.Pick("Maks. temperatura (°C)", "最高温度 (°C)");
                case "max_throttle_pct": return HwTestLocalization.Pick("Maks. Throttling (%)", "最大降频 (%)");
                case "max_fan_rpm": return HwTestLocalization.Pick("Ventilator (RPM)", "风扇转速 (RPM)");
                case "worker_errors": return HwTestLocalization.Pick("Greške radnih niti", "工作线程错误");
                case "total_gb": return HwTestLocalization.Pick("Ukupan RAM (GB)", "总内存 (GB)");
                case "avail_mb": return HwTestLocalization.Pick("Slobodno pri startu (MB)", "启动时可用内存 (MB)");
                case "dimm_count": return HwTestLocalization.Pick("Broj modula", "内存条数量");
                case "total_slots": return HwTestLocalization.Pick("Ukupno slotova", "总插槽数");
                case "speed_mhz": return HwTestLocalization.Pick("Brzina RAM (MHz)", "内存频率 (MHz)");
                case "memory_type": return HwTestLocalization.Pick("Tip memorije", "内存类型");
                case "dimms": return HwTestLocalization.Pick("DIMM moduli", "DIMM 内存条详情");
                case "tested_mb": return HwTestLocalization.Pick("Testirano (MB)", "已测试内存 (MB)");
                case "bit_errors": return HwTestLocalization.Pick("Memorijske greške", "内存位错误");
                case "bandwidth_gbs": return HwTestLocalization.Pick("Propusnost (GB/s)", "内存带宽 (GB/s)");
                case "gpu_name": return HwTestLocalization.Pick("Primarni GPU", "主显卡 (GPU)");
                case "driver_version": return HwTestLocalization.Pick("Verzija drajvera", "驱动版本");
                case "vram_mb": return HwTestLocalization.Pick("VRAM (MB)", "显存 (MB)");
                case "wpf_render_tier": return HwTestLocalization.Pick("WPF Render Tier", "WPF 渲染等级");
                case "avg_fps": return HwTestLocalization.Pick("Prosečan 3D FPS", "平均 3D 帧率 (FPS)");
                case "min_fps": return HwTestLocalization.Pick("Minimalni 3D FPS", "最低 3D 帧率 (FPS)");
                case "max_fps": return HwTestLocalization.Pick("Maksimalni 3D FPS", "最高 3D 帧率 (FPS)");
                case "peak_gpu_temp_c": return HwTestLocalization.Pick("Peak GPU Temp (°C)", "GPU 峰值温度 (°C)");
                case "all_adapters": return HwTestLocalization.Pick("Svi GPU adapteri", "所有 GPU 适配器");
                case "battery_name": return HwTestLocalization.Pick("Model baterije", "电池型号");
                case "health_percent": return HwTestLocalization.Pick("Zdravlje baterije (%)", "电池健康度 (%)");
                case "design_mwh": return HwTestLocalization.Pick("Fabrički kapacitet (mWh)", "设计容量 (mWh)");
                case "full_charge_mwh": return HwTestLocalization.Pick("Trenutni kapacitet (mWh)", "满充容量 (mWh)");
                case "cycle_count": return HwTestLocalization.Pick("Broj ciklusa", "循环次数");
                case "charge_percent": return HwTestLocalization.Pick("Nivo napunjenosti (%)", "当前电量 (%)");
                case "ac_online": return HwTestLocalization.Pick("AC punjač priključen", "电源适配器已连接");
                case "wifi_adapters": return HwTestLocalization.Pick("Wi-Fi adapteri", "Wi-Fi 无线网卡");
                case "lan_adapters": return HwTestLocalization.Pick("LAN adapteri", "有线网卡");
                case "hub_ok": return HwTestLocalization.Pick("ServisHub dostupan", "ServisHub 可达");
                case "hub_latency_ms": return HwTestLocalization.Pick("ServisHub ping (ms)", "ServisHub 延迟 (ms)");
                case "gateway_ping_ms": return HwTestLocalization.Pick("Gateway ping (ms)", "网关延迟 (ms)");
                case "interfaces": return HwTestLocalization.Pick("Mrežni interfejsi", "网络接口");
                case "bios_password_state": return HwTestLocalization.Pick("BIOS Supervisor lozinka", "BIOS 管理员密码");
                case "computrace_status": return HwTestLocalization.Pick("Computrace / Absolute status", "Computrace / Absolute 状态");
                case "tpm_status": return HwTestLocalization.Pick("TPM 2.0 bezbednosni čip", "TPM 2.0 安全芯片");
                case "secure_boot": return HwTestLocalization.Pick("Secure Boot stanje", "Secure Boot 安全启动");
                case "oa3_oem_key": return HwTestLocalization.Pick("OEM Windows OA3 licenca", "OEM Windows OA3 密钥");
                case "pnp_error_count": return HwTestLocalization.Pick("PnP greške drajvera (broj)", "PnP 驱动错误数");
                case "pnp_errors": return HwTestLocalization.Pick("Uređaji sa greškom (Code <> 0)", "异常设备 (Code <> 0)");
                case "charge_rate_w": return HwTestLocalization.Pick("Snaga punjenja (W)", "充电功率 (W)");
                case "discharge_rate_w": return HwTestLocalization.Pick("Snaga pražnjenja (W)", "放电功率 (W)");
                case "voltage_v": return HwTestLocalization.Pick("Napon baterije (V)", "电池电压 (V)");
                case "wifi_visible_networks": return HwTestLocalization.Pick("Vidljive Wi-Fi mreže", "可见 Wi-Fi 网络");
                case "wifi_max_signal_pct": return HwTestLocalization.Pick("Najjači Wi-Fi signal (%)", "最强 Wi-Fi 信号 (%)");
                case "bluetooth_status": return HwTestLocalization.Pick("Bluetooth adapter status", "蓝牙适配器状态");
                case "bios_password_locked": return HwTestLocalization.Pick("BIOS zaključan lozinkom", "BIOS 密码锁定");
                case "computrace_active": return HwTestLocalization.Pick("Computrace / Absolute aktivan", "Computrace / Absolute 已激活");
                case "battery_present": return HwTestLocalization.Pick("Baterija prisutna", "检测到电池");
                case "is_charging": return HwTestLocalization.Pick("Punjenje aktivno", "正在充电");
                case "wmi_status": return HwTestLocalization.Pick("WMI status baterije", "电池 WMI 状态");
                case "bluetooth_present": return HwTestLocalization.Pick("Bluetooth prisutan", "检测到蓝牙");
                case "any_link_up": return HwTestLocalization.Pick("Aktivna mrežna veza", "活动网络连接");
                case "gateway_ip": return HwTestLocalization.Pick("Gateway IP adresa", "网关 IP 地址");
                case "hub_url": return HwTestLocalization.Pick("ServisHub URL", "ServisHub 地址");
                case "system_drive": return HwTestLocalization.Pick("Sistemska particija", "系统分区");
                case "bench_executed": return HwTestLocalization.Pick("Benchmark izvršen", "基准测试已执行");
                case "bench_error": return HwTestLocalization.Pick("Status benchmarka", "基准测试状态");
                case "idle_temp_c": return HwTestLocalization.Pick("Idle temperatura (°C)", "空载温度 (°C)");
                case "iterations": return HwTestLocalization.Pick("Broj iteracija", "迭代次数");
                case "sensor_source": return HwTestLocalization.Pick("Izvor termalnog senzora", "温度传感器来源");
                case "pnp_error_code": return HwTestLocalization.Pick("Device Manager kod greške", "设备管理器错误码");
                case "is_basic_display": return HwTestLocalization.Pick("Basic Display drajver", "基本显示适配器驱动");
                case "total_frames": return HwTestLocalization.Pick("Ukupno 3D frejmova", "总 3D 渲染帧数");
                default: return key;
            }
        }
    }
}
