using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WIS.AutopilotAuditor
{
    public class MainWindow : Window
    {
        private AuditorResult _currentResult;
        private Border _heroBanner;
        private TextBlock _heroTitle;
        private TextBlock _heroDesc;
        private TextBlock _heroRec;
        private TextBlock _lblTenantDomain;
        private TextBlock _lblTenantId;
        private TextBlock _lblZtdStatus;
        private TextBlock _lblQueryMode;
        private TextBlock _lblZtdMsg;
        private TextBlock _lblHwModel;
        private TextBlock _lblHwSerial;
        private TextBlock _lblHwUuid;
        private TextBlock _lblTpmStatus;
        private TextBlock _lblAzureAd;
        private TextBlock _lblMdmStatus;
        private TextBlock _lblStatusFooter;
        private ProgressBar _progressBar;
        private Button _btnRescan;
        private Button _btnExportHtml;
        private Button _btnCopyJson;
        private Button _btnExportHash;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => RunScanAsync();
        }

        private void InitializeComponent()
        {
            Title = "WIS Autopilot Forensic Auditor v3.0";
            Width = 840;
            Height = 650;
            MinWidth = 760;
            MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)); // Slate 900
            Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249));

            Grid rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Hero Banner
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Cards Grid
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Actions
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

            // 1. Header
            Border headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(2, 6, 23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 14, 20, 14)
            };

            DockPanel headerDock = new DockPanel();
            TextBlock headerTitle = new TextBlock
            {
                Text = "🛡️ MICROSOFT AUTOPILOT FORENSIC AUDITOR",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248))
            };
            TextBlock headerSub = new TextBlock
            {
                Text = "WIS Service Standalone Tool v3.0",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            headerDock.Children.Add(headerTitle);
            headerDock.Children.Add(headerSub);
            headerBorder.Child = headerDock;
            Grid.SetRow(headerBorder, 0);
            rootGrid.Children.Add(headerBorder);

            // 2. Hero Banner
            _heroBanner = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18),
                Margin = new Thickness(20, 16, 20, 12),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(40, 56, 189, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248))
            };

            StackPanel heroPanel = new StackPanel();
            _heroTitle = new TextBlock
            {
                Text = "SKENIRANJE U TOKU...",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            _heroDesc = new TextBlock
            {
                Text = "Sistem vrši duboku inspekciju hardvera, registra i Microsoft ZTD oblaka...",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _heroRec = new TextBlock
            {
                Text = "Sačekajte završetak bezbednosne provere...",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                Padding = new Thickness(8),
                TextWrapping = TextWrapping.Wrap
            };

            heroPanel.Children.Add(_heroTitle);
            heroPanel.Children.Add(_heroDesc);
            heroPanel.Children.Add(_heroRec);
            _heroBanner.Child = heroPanel;
            Grid.SetRow(_heroBanner, 1);
            rootGrid.Children.Add(_heroBanner);

            // 3. Središnji deo sa karticama
            Grid cardsGrid = new Grid { Margin = new Thickness(20, 0, 20, 12) };
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Leva kartica - Oblak & Organizacija
            Border cloudCard = CreateCard("☁️ Microsoft Oblak & Organizacija");
            StackPanel cloudPanel = (StackPanel)cloudCard.Child;
            _lblTenantDomain = AddRow(cloudPanel, "Pripada Tenantu:", "Učitavanje...");
            _lblTenantId = AddRow(cloudPanel, "Tenant ID (GUID):", "Učitavanje...");
            _lblZtdStatus = AddRow(cloudPanel, "ZTD Oblak Status:", "Provera...");
            _lblQueryMode = AddRow(cloudPanel, "Metod analize:", "Provera...");
            _lblZtdMsg = AddRow(cloudPanel, "Poruka servisa:", "Povezivanje...");
            Grid.SetColumn(cloudCard, 0);
            cardsGrid.Children.Add(cloudCard);

            // Desna kartica - Hardver & TPM
            Border hwCard = CreateCard("💻 Hardverski Profil & Sigurnost");
            StackPanel hwPanel = (StackPanel)hwCard.Child;
            _lblHwModel = AddRow(hwPanel, "Model računara:", "Učitavanje...");
            _lblHwSerial = AddRow(hwPanel, "Serijski broj:", "Učitavanje...");
            _lblHwUuid = AddRow(hwPanel, "SMBIOS UUID:", "Učitavanje...");
            _lblTpmStatus = AddRow(hwPanel, "TPM 2.0 status:", "Provera...");
            _lblAzureAd = AddRow(hwPanel, "Azure AD Join:", "Provera...");
            _lblMdmStatus = AddRow(hwPanel, "Intune MDM upis:", "Provera...");
            Grid.SetColumn(hwCard, 2);
            cardsGrid.Children.Add(hwCard);

            Grid.SetRow(cardsGrid, 2);
            rootGrid.Children.Add(cardsGrid);

            // 4. Traka sa akcijama
            Border actionBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Padding = new Thickness(20, 12, 20, 12)
            };

            DockPanel actionDock = new DockPanel();

            _progressBar = new ProgressBar
            {
                Height = 4,
                IsIndeterminate = true,
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Visible
            };

            StackPanel btnPanel = new StackPanel { Orientation = Orientation.Horizontal };

            _btnRescan = CreateButton("🔄 Ponovi proveru", Color.FromRgb(37, 99, 235));
            _btnRescan.Click += (s, e) => RunScanAsync();
            btnPanel.Children.Add(_btnRescan);

            _btnExportHtml = CreateButton("📄 Sačuvaj Izveštaj (HTML)", Color.FromRgb(15, 118, 110));
            _btnExportHtml.Click += BtnExportHtml_Click;
            btnPanel.Children.Add(_btnExportHtml);

            _btnCopyJson = CreateButton("📋 Kopiraj JSON", Color.FromRgb(71, 85, 105));
            _btnCopyJson.Click += BtnCopyJson_Click;
            btnPanel.Children.Add(_btnCopyJson);

            _btnExportHash = CreateButton("🔑 Hardware Hash (CSV)", Color.FromRgb(124, 58, 237));
            _btnExportHash.Click += BtnExportHash_Click;
            btnPanel.Children.Add(_btnExportHash);

            Button btnClose = CreateButton("✖ Zatvori", Color.FromRgb(100, 116, 139));
            btnClose.Click += (s, e) => Close();
            btnClose.HorizontalAlignment = HorizontalAlignment.Right;

            actionDock.Children.Add(btnClose);
            actionDock.Children.Add(btnPanel);

            StackPanel actionStack = new StackPanel();
            actionStack.Children.Add(_progressBar);
            actionStack.Children.Add(actionDock);
            actionBorder.Child = actionStack;

            Grid.SetRow(actionBorder, 3);
            rootGrid.Children.Add(actionBorder);

            // 5. Footer statusna linija
            Border footerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Padding = new Thickness(20, 6, 20, 6)
            };
            _lblStatusFooter = new TextBlock
            {
                Text = "Sistem spreman.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139))
            };
            footerBorder.Child = _lblStatusFooter;
            Grid.SetRow(footerBorder, 4);
            rootGrid.Children.Add(footerBorder);

            Content = rootGrid;
        }

        private async void RunScanAsync()
        {
            _progressBar.Visibility = Visibility.Visible;
            _btnRescan.IsEnabled = false;
            _lblStatusFooter.Text = "Skeniranje hardvera i provjera Microsoft oblaka u toku...";

            _heroTitle.Text = "PROVERA U TOKU...";
            _heroDesc.Text = "Očitavanje registarskih grana, Windows Event Log-a i Microsoft ZTD endpoint-a...";
            _heroRec.Text = "Molimo sačekajte završetak...";
            SetHeroColor(Color.FromRgb(56, 189, 248));

            AuditorResult res = await Task.Run(() =>
            {
                HardwareDetails hw = HardwareInspector.Inspect();
                LocalForensicsDetails local = LocalForensicsInspector.Inspect();
                CloudZtdDetails cloud = CloudZtdInspector.Inspect(hw.SerialNumber);
                AuditorResult evaluated = DecisionMatrix.Evaluate(hw, local, cloud);
                CloudZtdInspector.SaveCachedReport(evaluated);
                return evaluated;
            });

            _currentResult = res;
            UpdateUiFromResults(res);

            _progressBar.Visibility = Visibility.Collapsed;
            _btnRescan.IsEnabled = true;
            _lblStatusFooter.Text = string.Format("Poslednja provera završena: {0} | Režim: {1}", res.Timestamp, res.CloudZtd.OnlineChecked ? "ONLINE" : "OFFLINE");
        }

        private void UpdateUiFromResults(AuditorResult res)
        {
            _heroTitle.Text = res.StatusTitle;
            _heroDesc.Text = res.StatusDescription;
            _heroRec.Text = "PREPORUKA SERVISA: " + res.RecommendedAction;

            Color themeColor;
            switch (res.StatusLevel)
            {
                case StatusSeverity.CRITICAL:
                    themeColor = Color.FromRgb(239, 68, 68); // Red
                    break;
                case StatusSeverity.WARNING:
                    themeColor = Color.FromRgb(245, 158, 11); // Amber
                    break;
                case StatusSeverity.OK:
                    themeColor = Color.FromRgb(34, 197, 94); // Green
                    break;
                default:
                    themeColor = Color.FromRgb(56, 189, 248); // Blue
                    break;
            }
            SetHeroColor(themeColor);

            _lblTenantDomain.Text = !string.IsNullOrEmpty(res.TenantDomain) ? res.TenantDomain : "Nema (Nije dodeljen)";
            _lblTenantId.Text = !string.IsNullOrEmpty(res.TenantId) ? res.TenantId : "N/A";
            _lblZtdStatus.Text = res.CloudZtd.IsRegistered ? "REGISTROVAN U OBLAKU" : (res.CloudZtd.ConfirmedFree ? "SLOBODAN (Nije u bazi)" : "Nije eksplicitno potvrđen");
            _lblQueryMode.Text = res.CloudZtd.QueryMode;
            _lblZtdMsg.Text = res.CloudZtd.RawStatusMessage;

            _lblHwModel.Text = string.Format("{0} {1}", res.Hardware.Manufacturer, res.Hardware.Model).Trim();
            _lblHwSerial.Text = res.Hardware.SerialNumber;
            _lblHwUuid.Text = res.Hardware.SmbiosUuid;
            _lblTpmStatus.Text = res.Hardware.TpmPresent && res.Hardware.TpmReady ? "Spreman (TPM 2.0 aktivan)" : (res.Hardware.TpmPresent ? "Prisutan (Nije spreman)" : "Nije detektovan");
            _lblAzureAd.Text = res.LocalForensics.AzureAdStatus;
            _lblMdmStatus.Text = res.LocalForensics.MdmStatus;
        }

        private void SetHeroColor(Color c)
        {
            _heroBanner.Background = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            _heroBanner.BorderBrush = new SolidColorBrush(c);
            _heroTitle.Foreground = new SolidColorBrush(c);
        }

        private Border CreateCard(string title)
        {
            Border b = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(2, 6, 23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14)
            };

            StackPanel sp = new StackPanel();
            TextBlock tb = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            sp.Children.Add(tb);
            b.Child = sp;
            return b;
        }

        private TextBlock AddRow(StackPanel parent, string label, string defaultValue)
        {
            DockPanel dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            TextBlock lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Width = 130
            };
            TextBlock val = new TextBlock
            {
                Text = defaultValue,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap
            };
            dp.Children.Add(lbl);
            dp.Children.Add(val);
            parent.Children.Add(dp);
            return val;
        }

        private Button CreateButton(string text, Color bg)
        {
            Button btn = new Button
            {
                Content = text,
                Background = new SolidColorBrush(bg),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            return btn;
        }

        private void BtnExportHtml_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult == null) return;
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "HTML dokument (*.html)|*.html",
                FileName = string.Format("Autopilot_Izvestaj_{0}.html", _currentResult.Hardware.SerialNumber)
            };
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    ReportExporter.ExportHtml(_currentResult, sfd.FileName);
                    MessageBox.Show("HTML izveštaj je uspešno sačuvan!", "Uspeh", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Greška pri snimanju izveštaja: " + ex.Message, "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnCopyJson_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult == null) return;
            try
            {
                Clipboard.SetText(_currentResult.ToJson());
                MessageBox.Show("Kompletan JSON izveštaj je kopiran u Clipboard!", "Kopirano", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Greška pri kopiranju: " + ex.Message, "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportHash_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResult == null) return;
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "CSV fajl za Microsoft Intune (*.csv)|*.csv",
                FileName = string.Format("AutopilotHash_{0}.csv", _currentResult.Hardware.SerialNumber)
            };
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    ReportExporter.ExportHardwareHashCsv(_currentResult, sfd.FileName);
                    MessageBox.Show("Hardware Hash CSV je uspešno sačuvan za uvoz u Intune portal!", "Uspeh", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Greška pri snimanju: " + ex.Message, "Greška", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
