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
        /// u WrapPanel i rekonstruiše 2-kolonski završni karton (Specifikacije + Periferije levo,
        /// ServiceHub QR kod + Upozorenja sa kojima je proces završen desno).
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

                    if (!isLarge)
                    {
                        if (origPanel is StackPanel)
                        {
                            ReplaceStackPanelWithWrapPanel(origPanel, false);
                        }
                        continue;
                    }

                    // Za završni prozor (overlay karticu): pronađi spoljašnji Border kartice
                    Border cardBorder = null;
                    DependencyObject cur = anchor.Parent;
                    while (cur != null)
                    {
                        Border b = cur as Border;
                        if (b != null && (
                            b.Width == 680 ||
                            b.MinWidth == 680 ||
                            b.MinWidth == 780 ||
                            string.Equals(Convert.ToString(b.Tag), "CompletionCardRedesignedV2", StringComparison.Ordinal)))
                        {
                            cardBorder = b;
                            break;
                        }
                        cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
                    }

                    if (cardBorder == null) continue;

                    if (!string.Equals(Convert.ToString(cardBorder.Tag), "CompletionCardRedesignedV2", StringComparison.Ordinal))
                    {
                        WrapPanel actionButtonsWrap;
                        if (origPanel is WrapPanel)
                        {
                            actionButtonsWrap = (WrapPanel)origPanel;
                            Panel oldParent = actionButtonsWrap.Parent as Panel;
                            if (oldParent != null) oldParent.Children.Remove(actionButtonsWrap);
                        }
                        else
                        {
                            actionButtonsWrap = new WrapPanel
                            {
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Center
                            };
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
                                if (childBtn != null)
                                {
                                    childBtn.Padding = new Thickness(16, 10, 16, 10);
                                    childBtn.Margin = new Thickness(6, 5, 6, 5);
                                }
                                actionButtonsWrap.Children.Add(children[i]);
                            }
                        }

                        BuildRedesignedCompletionCardContent(cardBorder, actionButtonsWrap);
                    }
                    else if (!(cardBorder.Child is ScrollViewer))
                    {
                        UIElement innerContent = cardBorder.Child;
                        cardBorder.Child = null;
                        cardBorder.Child = new ScrollViewer
                        {
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = innerContent
                        };
                    }
                }
                _completionLayoutAdapted = true;
            }
            catch { }
        }

        private static WrapPanel ReplaceStackPanelWithWrapPanel(Panel origPanel, bool isLarge)
        {
            Panel grandParent = origPanel.Parent as Panel;
            if (grandParent == null) return null;

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
            return wrap;
        }

        private void BuildRedesignedCompletionCardContent(Border cardBorder, WrapPanel actionButtonsWrap)
        {
            if (cardBorder == null || actionButtonsWrap == null) return;
            var boolToVis = new BooleanToVisibilityConverter();

            cardBorder.Tag = "CompletionCardRedesignedV2";
            cardBorder.Width = double.NaN;
            cardBorder.MinWidth = 780;
            cardBorder.MaxWidth = 1080;
            cardBorder.Margin = new Thickness(18);
            cardBorder.Padding = new Thickness(22, 18, 22, 18);
            cardBorder.Background = MainViewModel.CreateFrozenBrush(24, 28, 27);
            cardBorder.BorderThickness = new Thickness(2);
            cardBorder.CornerRadius = new CornerRadius(10);
            cardBorder.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("CompletionCardBorderBrush"));

            var rootStack = new StackPanel();

            // 1. ZAGLAVLJE KARTONA
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel();
            var statusPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            statusPill.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("CompletionStatusBadgeBrush"));
            var statusPillTb = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold
            };
            statusPillTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("CompletionStatusBadgeText"));
            statusPillTb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("CompletionStatusBadgeForeground"));
            statusPill.Child = statusPillTb;
            headerLeft.Children.Add(statusPill);

            var modelTb = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 6, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            modelTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ComputerModel"));
            headerLeft.Children.Add(modelTb);

            var metaRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 3)
            };
            metaRow.Children.Add(new TextBlock
            {
                Text = "SN: ",
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136)
            });
            var snTb = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204)
            };
            snTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SerialNumber"));
            metaRow.Children.Add(snTb);
            metaRow.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtProgramsPrefix", "  •  Programi: "),
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136)
            });
            var appsTb = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(88, 166, 255)
            };
            appsTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("AppsSummaryText"));
            metaRow.Children.Add(appsTb);
            headerLeft.Children.Add(metaRow);

            var subSummaryTb = new TextBlock
            {
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(176, 184, 180),
                TextWrapping = TextWrapping.Wrap
            };
            subSummaryTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("CompletionAlertsSummaryText"));
            headerLeft.Children.Add(subSummaryTb);

            Grid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            var headerRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top
            };
            var durBox = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(12, 0, 8, 0)
            };
            var durStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            durStack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtDurationHeader", "TRAJANJE"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var durValTb = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            durValTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DurationString"));
            durStack.Children.Add(durValTb);
            durBox.Child = durStack;
            headerRight.Children.Add(durBox);

            var closeBtn = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 28,
                Background = MainViewModel.CreateFrozenBrush(37, 37, 38),
                Foreground = MainViewModel.CreateFrozenBrush(204, 204, 204),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };
            closeBtn.SetBinding(Button.CommandProperty, new System.Windows.Data.Binding("CancelShutdownCommand"));
            closeBtn.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Loc[TooltipStayInApp]"));
            headerRight.Children.Add(closeBtn);

            Grid.SetColumn(headerRight, 1);
            headerGrid.Children.Add(headerRight);
            rootStack.Children.Add(headerGrid);

            // 2. GLAVNI 2-KOLONSKI SADRŽAJ
            var bodyGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 310 });

            // Leva kolona: Specifikacije uređaja + Dodatne periferije (1:1 sa slike)
            var specsCard = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var specsGrid = new Grid();
            specsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            specsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var specsStack = new StackPanel();
            specsStack.Children.Add(CreateOverviewSpecRow("CPU", "CpuInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("RAM", "RamInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Disk", "DiskInfo", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Rezolucija ekrana", "HardwareInfo.OverviewResolution", null, null, boolToVis, false, "HardwareInfo.OverviewResolutionTooltip"));
            specsStack.Children.Add(CreateOverviewSpecRow("GPU", "HardwareInfo.OverviewGpu", null, null, boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("iGPU", "HardwareInfo.OverviewIgpu", null, "HardwareInfo.HasOverviewIgpu", boolToVis, false));
            specsStack.Children.Add(CreateOverviewSpecRow("Battery wear", "HardwareInfo.BatteryWear", "HardwareInfo.BatteryHealthBrush", null, boolToVis, true));
            Grid.SetRow(specsStack, 0);
            specsGrid.Children.Add(specsStack);

            var periphSection = new Border
            {
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(0, 8, 0, 0)
            };
            var periphStack = new StackPanel();
            periphStack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtExtraPeripheralsHeader", "DODATNE PERIFERIJE"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var periphWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            periphStack.Children.Add(periphWrap);

            var periphEmptyTb = new TextBlock
            {
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(119, 119, 119)
            };
            periphEmptyTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("HardwareInfo.OverviewPeripheralsEmptyText"));
            periphEmptyTb.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HardwareInfo.ShowOverviewPeripheralsEmpty") { Converter = boolToVis });
            periphStack.Children.Add(periphEmptyTb);

            RebuildOverviewPeripheralsHost(periphWrap);
            if (ViewModel != null && ViewModel.HardwareInfo != null && ViewModel.HardwareInfo.OverviewPeripherals != null)
            {
                ViewModel.HardwareInfo.OverviewPeripherals.CollectionChanged += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        RebuildOverviewPeripheralsHost(periphWrap);
                    });
                };
            }

            periphSection.Child = periphStack;
            Grid.SetRow(periphSection, 1);
            specsGrid.Children.Add(periphSection);

            specsCard.Child = specsGrid;
            Grid.SetColumn(specsCard, 0);
            bodyGrid.Children.Add(specsCard);

            // Desna kolona: QR kod kartona gore + Upozorenja sa kojima je proces završen dole
            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var qrCard = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var qrStack = new StackPanel();
            qrStack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtDeviceCardHeader", "KARTON UREĐAJA (SERVISHUB)"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var qrViewbox = new Viewbox
            {
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6)
            };
            var qrHost = new Grid();
            var qrWhite = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8)
            };
            qrWhite.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HasDeviceCardQr") { Converter = boolToVis });
            var qrImg = new Image
            {
                Width = 168,
                Height = 168,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(qrImg, BitmapScalingMode.NearestNeighbor);
            qrImg.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("DeviceCardQrImage"));
            qrWhite.Child = qrImg;
            qrHost.Children.Add(qrWhite);

            var qrPlaceholder = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(37, 37, 40),
                BorderBrush = MainViewModel.CreateFrozenBrush(62, 62, 66),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Width = 168,
                Height = 168,
                Padding = new Thickness(12)
            };
            qrPlaceholder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowDeviceCardQrPlaceholder") { Converter = boolToVis });
            qrPlaceholder.Child = new TextBlock
            {
                Text = LocalizationService.Get("RbtQrPlaceholder", "QR kod će se pojaviti kada se otkrije ServisHub..."),
                FontSize = 11,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            qrHost.Children.Add(qrPlaceholder);
            qrViewbox.Child = qrHost;
            qrStack.Children.Add(qrViewbox);

            var qrStatusTb = new TextBlock
            {
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(170, 170, 170),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            qrStatusTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceCardStatusText"));
            qrStack.Children.Add(qrStatusTb);

            var qrUrlTb = new TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MainViewModel.CreateFrozenBrush(78, 201, 176),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            qrUrlTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceCardUrl"));
            qrUrlTb.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("DeviceCardUrl"));
            qrStack.Children.Add(qrUrlTb);

            qrCard.Child = qrStack;
            Grid.SetRow(qrCard, 0);
            rightGrid.Children.Add(qrCard);

            var alertsCard = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(30, 30, 30),
                BorderBrush = MainViewModel.CreateFrozenBrush(51, 51, 56),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var alertsGrid = new Grid();
            alertsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            alertsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var alertsHdr = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            alertsHdr.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtWarningsHeader", "UPOZORENJA SA KOJIMA JE PROCES ZAVRŠEN"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = MainViewModel.CreateFrozenBrush(136, 136, 136),
                VerticalAlignment = VerticalAlignment.Center
            });
            var alertsBadge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            alertsBadge.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("OverviewAlertsBadgeBrush"));
            var alertsBadgeTb = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            alertsBadgeTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("OverviewAlertsCountText"));
            alertsBadge.Child = alertsBadgeTb;
            alertsHdr.Children.Add(alertsBadge);
            Grid.SetRow(alertsHdr, 0);
            alertsGrid.Children.Add(alertsHdr);

            var alertsBodyHost = new Grid();
            Grid.SetRow(alertsBodyHost, 1);

            var alertsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 168
            };
            alertsScroll.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("HasOverviewAlerts") { Converter = boolToVis });
            var alertsHost = new StackPanel();
            alertsScroll.Content = alertsHost;
            alertsBodyHost.Children.Add(alertsScroll);

            RebuildOverviewAlertsHost(alertsHost);
            if (ViewModel != null && ViewModel.OverviewAlerts != null)
            {
                ViewModel.OverviewAlerts.CollectionChanged += delegate
                {
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        RebuildOverviewAlertsHost(alertsHost);
                    });
                };
            }

            var noAlertsBorder = new Border
            {
                Background = MainViewModel.CreateFrozenBrush(27, 42, 33),
                BorderBrush = MainViewModel.CreateFrozenBrush(45, 90, 63),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            noAlertsBorder.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ShowNoOverviewAlerts") { Converter = boolToVis });
            var noAlertsStack = new StackPanel();
            noAlertsStack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtNoWarningsTitle", "✓ Proces završen bez upozorenja"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MainViewModel.CreateFrozenBrush(46, 204, 113)
            });
            noAlertsStack.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("RbtNoWarningsDesc", "Temperature, Autopilot, drajveri, programi i hardver su u normalnom stanju."),
                FontSize = 10,
                Foreground = MainViewModel.CreateFrozenBrush(168, 197, 180),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            noAlertsBorder.Child = noAlertsStack;
            alertsBodyHost.Children.Add(noAlertsBorder);

            alertsGrid.Children.Add(alertsBodyHost);
            alertsCard.Child = alertsGrid;
            Grid.SetRow(alertsCard, 2);
            rightGrid.Children.Add(alertsCard);

            Grid.SetColumn(rightGrid, 2);
            bodyGrid.Children.Add(rightGrid);
            rootStack.Children.Add(bodyGrid);

            // 3. DONJA TRAKA SA DUGMADIMA
            actionButtonsWrap.HorizontalAlignment = HorizontalAlignment.Center;
            actionButtonsWrap.Margin = new Thickness(0, 2, 0, 0);
            rootStack.Children.Add(actionButtonsWrap);

            cardBorder.Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rootStack
            };
        }

        private void SuspendBitLockerBeforeReboot()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"try { Suspend-BitLocker -MountPoint 'C:' -RebootCount 1 -ErrorAction SilentlyContinue | Out-Null } catch {}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(10000);
                    }
                }
            }
            catch { }
        }

        private void HandleInterimReboot()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ViewModel.AddLog("WARN", "TaskRunner", "Faza 4 i instalacija drajvera završeni. Pripremam kontrolisani restart pred Fazu 5...");
                ViewModel.LiveStatus = LocalizationService.Get("RbtControlledReboot5s", "Kontrolisani međurestart za 5 sekundi...");
            }));

            // 1. Zapis stanja phase: 5 i post_reboot_finalize: true u C:\setup\deploy-state.json (V350-06)
            SaveDeployStatePhase(5, true);

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
                    string waitMsg = string.Format(LocalizationService.Get("RbtRebootDelayedMsg", "Međurestart odložen od strane servisera — preostalo {0:D2}:{1:D2} (do {2})"), remMin, remSec, dueLocalStr);

                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        ViewModel.SubPhase = string.Format(LocalizationService.Get("RbtRebootDelayedSubPhase", "Međurestart odložen (do {0})"), dueLocalStr);
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
                        ViewModel.LiveStatus = string.Format(LocalizationService.Get("RbtControlledRebootCountdown", "Kontrolisani međurestart za {0}s... (Kliknite '+10m Restart' za odlaganje)"), currentSec);
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
            SaveDeployStatePhase(5, true);

            // V350-01: Sačuvaj dosadašnji progres hardverskih testova pre restarta SAMO ako ima stvarno završenih testova
            try
            {
                if (_embeddedHwTestShell != null && _embeddedHwTestShell.HasAnyRealCompletedTestsForCheckpoint())
                {
                    _embeddedHwTestShell.SaveCheckpoint("");
                }
            }
            catch { }

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
            if (HasUnfinishedInteractiveHwTests())
            {
                if (ViewModel != null)
                {
                    ViewModel.IsCompleted = false;
                    ViewModel.AddLog("WARN", "Hardver", "Završni karton je zaključan dok se svi obavezni interaktivni hardverski testovi ne završe.");
                }
                ShowPendingInteractiveTestsWarning(GetPendingInteractiveHwTestNames(), true, true);
                return;
            }

            HidePendingInteractiveTestsWarning();
            if (ViewModel != null)
            {
                ViewModel.RebuildOverviewAlerts();
            }
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
            ViewModel.AddLog("INFO", "Sistem", LocalizationService.Get("RbtCleanupSummary", "Čišćenje servisnih tragova i restartovanje računara..."));
            ViewModel.CompletionTitle = LocalizationService.Get("RbtCleanupTitle", "ČIŠĆENJE I RESTART...");
            ViewModel.CompletionSummary = LocalizationService.Get("RbtCleanupSummary", "Čišćenje servisnih tragova i restartovanje računara...");

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
