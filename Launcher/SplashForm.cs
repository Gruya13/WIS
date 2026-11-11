using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WIS.Launcher
{
    public class CustomProgressBar : Control
    {
        private int _value = 0;
        private int _maximum = 100;

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(0, Math.Min(_maximum, value));
                if (_value != v)
                {
                    _value = v;
                    Invalidate();
                }
            }
        }

        public int Maximum
        {
            get { return _maximum; }
            set
            {
                if (value > 0 && _maximum != value)
                {
                    _maximum = value;
                    Invalidate();
                }
            }
        }

        public CustomProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 12;
            BackColor = Color.FromArgb(33, 38, 45); // #21262d
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Pozadina
            using (SolidBrush bgBrush = new SolidBrush(BackColor))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
            }

            // Fill
            if (_maximum > 0 && _value > 0)
            {
                float pct = (float)_value / _maximum;
                int fillWidth = (int)(ClientRectangle.Width * pct);
                if (fillWidth > 0)
                {
                    Rectangle fillRect = new Rectangle(0, 0, fillWidth, ClientRectangle.Height);
                    using (LinearGradientBrush fillBrush = new LinearGradientBrush(fillRect, Color.FromArgb(56, 189, 248), Color.FromArgb(14, 165, 233), LinearGradientMode.Horizontal))
                    {
                        g.FillRectangle(fillBrush, fillRect);
                    }
                }
            }

            // Suptilni okvir
            using (Pen borderPen = new Pen(Color.FromArgb(48, 54, 61), 1))
            {
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    public class SplashForm : Form
    {
        // Win32 za prevlacenje prozora bez okvira
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        // Kontrole forme
        private Panel pnlCard;
        private Label lblLogoBadge;
        private Label lblTitle;
        private Label lblSubtitle;
        private Label lblVersionTag;
        private Label lblStatus;
        private Label lblDetail;
        private CustomProgressBar progressBar;
        private Label lblPercent;
        private Button btnClose;

        // 1. Dijagnosticki panel (Offline mod sa reaktivnim osluskivacem)
        private Panel pnlDiagnostic;
        private Label lblDiagTitle;
        private Label lblDiagDesc;
        private TextBox txtAdapters;
        private Button btnRetry;
        private Button btnLaunchLocal;

        // 2. Panel za istu verziju (Up-To-Date sa 7s odbrojavanjem)
        private Panel pnlUpToDate;
        private Label lblUpToDateTitle;
        private Label lblUpToDateInfo;
        private Label lblCountdown;
        private Button btnUpToDateContinue;
        private Button btnUpToDateRedownload;
        private Button btnUpToDateDelete;

        // 3. Panel za novu verziju (Update Available)
        private Panel pnlUpdateAvailable;
        private Label lblUpdateTitle;
        private Label lblUpdateInfo;
        private Label lblUpdateDesc;
        private Button btnUpdateNow;
        private Button btnKeepCurrent;

        // Radno stanje i tokeni
        private volatile bool _isRunning = false;
        private CancellationTokenSource _cts;

        // Reaktivni network listener polja
        private System.Windows.Forms.Timer _netListenTimer;
        private bool _isListeningForNetwork = false;
        private readonly object _netLock = new object();

        // Up-To-Date tajmer polja
        private System.Windows.Forms.Timer _countdownTimer;
        private int _countdownSeconds = 7;

        // Memorisan server kontekst za interaktivne akcije
        private string _savedHubUrl;
        private string _savedNasHost;
        private PayloadVersionInfo _savedRemoteVer;
        private string _lastRecoveryMessage;

        public SplashForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            this.ClientSize = new Size(620, 380);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(12, 16, 23); // #0c1017
            this.ForeColor = Color.FromArgb(240, 246, 252);
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.ShowInTaskbar = true;
            this.Text = "WIS Thin-Client Launcher";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // Glavna Kartica
            pnlCard = new Panel
            {
                Location = new Point(12, 12),
                Size = new Size(596, 356),
                BackColor = Color.FromArgb(22, 27, 34), // #161b22
                BorderStyle = BorderStyle.None
            };
            pnlCard.Paint += PnlCard_Paint;
            pnlCard.MouseDown += Form_MouseDown;

            // Dugme za gasenje
            btnClose = new Button
            {
                Text = "✕",
                Size = new Size(28, 28),
                Location = new Point(560, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(139, 148, 158),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(248, 81, 73);
            btnClose.Click += (s, e) =>
            {
                CleanupTimers();
                Application.Exit();
            };

            // Logo Badge
            lblLogoBadge = new Label
            {
                Text = "TK",
                Location = new Point(20, 20),
                Size = new Size(38, 38),
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(56, 189, 248), // #38bdf8 cyan
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            lblLogoBadge.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1.5f))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, lblLogoBadge.Width - 1, lblLogoBadge.Height - 1);
                }
            };

            // Naslov
            lblTitle = new Label
            {
                Text = "WIS SERVICE AUTOMATIZACIJA",
                Location = new Point(68, 18),
                Size = new Size(400, 22),
                ForeColor = Color.FromArgb(240, 246, 252),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold)
            };
            lblTitle.MouseDown += Form_MouseDown;

            // Podnaslov
            lblSubtitle = new Label
            {
                Text = "WIS Thin-Client Splash Launcher • Zero-Touch Fleet Deployment",
                Location = new Point(68, 40),
                Size = new Size(450, 18),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            };
            lblSubtitle.MouseDown += Form_MouseDown;

            // Verzija oznaka
            lblVersionTag = new Label
            {
                Text = "v2.18.0 (.NET 4.0 / Zero-Touch)",
                Location = new Point(430, 40),
                Size = new Size(150, 18),
                ForeColor = Color.FromArgb(139, 148, 158),
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.TopRight
            };

            // Status Label
            lblStatus = new Label
            {
                Text = "Inicijalizacija pokretačkog modula...",
                Location = new Point(24, 110),
                Size = new Size(548, 26),
                ForeColor = Color.FromArgb(230, 237, 243),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold)
            };

            // Detail Label
            lblDetail = new Label
            {
                Text = "Priprema lokalnog okruženja...",
                Location = new Point(24, 140),
                Size = new Size(548, 38),
                ForeColor = Color.FromArgb(139, 148, 158),
                Font = new Font("Segoe UI", 8.5F)
            };

            // Custom Progress Bar
            progressBar = new CustomProgressBar
            {
                Location = new Point(24, 190),
                Size = new Size(548, 12),
                Value = 0,
                Maximum = 100
            };

            // Procenat tekst
            lblPercent = new Label
            {
                Text = "0%",
                Location = new Point(480, 210),
                Size = new Size(92, 20),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight
            };

            // -------------------------------------------------------------
            // 1. DIJAGNOSTICKI PANEL (Offline mod sa reaktivnim osluskivacem)
            // -------------------------------------------------------------
            pnlDiagnostic = new Panel
            {
                Location = new Point(20, 80),
                Size = new Size(556, 255),
                BackColor = Color.FromArgb(13, 17, 23),
                Visible = false
            };
            pnlDiagnostic.Paint += PnlDiagnostic_Paint;

            lblDiagTitle = new Label
            {
                Text = "⚠  NEMA MREŽNE KONEKCIJE NITI SERVISNOG HUB-A",
                Location = new Point(14, 10),
                Size = new Size(528, 22),
                ForeColor = Color.FromArgb(248, 81, 73),
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold)
            };

            lblDiagDesc = new Label
            {
                Text = "Povežite LAN mrežni kabl ili uključite Wi-Fi. Sistem automatski nastavlja čim se detektuje mrežna veza...",
                Location = new Point(14, 34),
                Size = new Size(528, 32),
                ForeColor = Color.FromArgb(201, 209, 217),
                Font = new Font("Segoe UI", 8.5F)
            };

            txtAdapters = new TextBox
            {
                Location = new Point(14, 68),
                Size = new Size(528, 115),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.FromArgb(139, 148, 158),
                Font = new Font("Consolas", 8.5F),
                BorderStyle = BorderStyle.FixedSingle
            };

            btnRetry = new Button
            {
                Text = "⟳  Pokušaj ponovo",
                Location = new Point(14, 195),
                Size = new Size(160, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 134, 54), // GitHub zelena
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRetry.FlatAppearance.BorderSize = 0;
            btnRetry.Click += BtnRetry_Click;

            btnLaunchLocal = new Button
            {
                Text = "▶  Pokreni lokalno van mreže",
                Location = new Point(184, 195),
                Size = new Size(230, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(31, 111, 235), // Jasno istaknuto plavo dugme
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnLaunchLocal.FlatAppearance.BorderSize = 0;
            btnLaunchLocal.Click += (s, e) =>
            {
                CleanupTimers();
                StartMasterUIAndExit();
            };

            pnlDiagnostic.Controls.Add(lblDiagTitle);
            pnlDiagnostic.Controls.Add(lblDiagDesc);
            pnlDiagnostic.Controls.Add(txtAdapters);
            pnlDiagnostic.Controls.Add(btnRetry);
            pnlDiagnostic.Controls.Add(btnLaunchLocal);

            // -------------------------------------------------------------
            // 2. PANEL ZA ISTU VERZIJU (Up-To-Date sa 7s odbrojavanjem)
            // -------------------------------------------------------------
            pnlUpToDate = new Panel
            {
                Location = new Point(20, 80),
                Size = new Size(556, 255),
                BackColor = Color.FromArgb(13, 17, 23),
                Visible = false
            };
            pnlUpToDate.Paint += PnlUpToDate_Paint;

            lblUpToDateTitle = new Label
            {
                Text = "WIS UI JE VEĆ INSTALIRAN",
                Location = new Point(18, 16),
                Size = new Size(520, 26),
                ForeColor = Color.FromArgb(63, 185, 80), // #3fb950 svetlo zelena
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold)
            };

            lblUpToDateInfo = new Label
            {
                Text = "Lokalna verzija: ... • Verzija na serveru: ... (Paket je ažuran)",
                Location = new Point(18, 48),
                Size = new Size(520, 34),
                ForeColor = Color.FromArgb(201, 209, 217),
                Font = new Font("Segoe UI", 9F)
            };

            lblCountdown = new Label
            {
                Text = "Automatski nastavljam za 7s...",
                Location = new Point(18, 92),
                Size = new Size(520, 24),
                ForeColor = Color.FromArgb(56, 189, 248), // cyan #38bdf8
                Font = new Font("Segoe UI", 9.5F, FontStyle.Italic)
            };

            btnUpToDateContinue = new Button
            {
                Text = "[▶ Nastavi (7)]",
                Location = new Point(18, 140),
                Size = new Size(155, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 134, 54), // GitHub zelena #238636
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnUpToDateContinue.FlatAppearance.BorderSize = 0;
            btnUpToDateContinue.Click += BtnUpToDateContinue_Click;

            btnUpToDateRedownload = new Button
            {
                Text = "[⟳ Preuzmi ponovo]",
                Location = new Point(183, 140),
                Size = new Size(165, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(31, 111, 235), // GitHub plava #1f6feb
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnUpToDateRedownload.FlatAppearance.BorderSize = 0;
            btnUpToDateRedownload.Click += BtnUpToDateRedownload_Click;

            btnUpToDateDelete = new Button
            {
                Text = "[✕ Obriši]",
                Location = new Point(358, 140),
                Size = new Size(130, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(218, 54, 51), // GitHub crvena #da3633
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnUpToDateDelete.FlatAppearance.BorderSize = 0;
            btnUpToDateDelete.Click += BtnUpToDateDelete_Click;

            pnlUpToDate.Controls.Add(lblUpToDateTitle);
            pnlUpToDate.Controls.Add(lblUpToDateInfo);
            pnlUpToDate.Controls.Add(lblCountdown);
            pnlUpToDate.Controls.Add(btnUpToDateContinue);
            pnlUpToDate.Controls.Add(btnUpToDateRedownload);
            pnlUpToDate.Controls.Add(btnUpToDateDelete);

            // -------------------------------------------------------------
            // 3. PANEL ZA NOVU VERZIJU (Update Available)
            // -------------------------------------------------------------
            pnlUpdateAvailable = new Panel
            {
                Location = new Point(20, 80),
                Size = new Size(556, 255),
                BackColor = Color.FromArgb(13, 17, 23),
                Visible = false
            };
            pnlUpdateAvailable.Paint += PnlUpdateAvailable_Paint;

            lblUpdateTitle = new Label
            {
                Text = "DOSTUPNO JE AŽURIRANJE UI PAKETA",
                Location = new Point(18, 16),
                Size = new Size(520, 26),
                ForeColor = Color.FromArgb(56, 189, 248), // cyan #38bdf8
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold)
            };

            lblUpdateInfo = new Label
            {
                Text = "Lokalna verzija: ... ➔ Nova verzija na serveru: ...",
                Location = new Point(18, 48),
                Size = new Size(520, 32),
                ForeColor = Color.FromArgb(240, 246, 252),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };

            lblUpdateDesc = new Label
            {
                Text = "Na NAS serveru se nalazi novija verzija paketa. Preporučuje se ažuriranje radi novih funkcija i stabilnosti.",
                Location = new Point(18, 86),
                Size = new Size(520, 36),
                ForeColor = Color.FromArgb(139, 148, 158),
                Font = new Font("Segoe UI", 8.5F)
            };

            btnUpdateNow = new Button
            {
                Text = "[⬇ Ažuriraj sada]",
                Location = new Point(18, 140),
                Size = new Size(180, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 134, 54), // GitHub zelena
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnUpdateNow.FlatAppearance.BorderSize = 0;
            btnUpdateNow.Click += BtnUpdateNow_Click;

            btnKeepCurrent = new Button
            {
                Text = "[▶ Zadrži trenutnu verziju]",
                Location = new Point(208, 140),
                Size = new Size(225, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(201, 209, 217),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnKeepCurrent.FlatAppearance.BorderSize = 1;
            btnKeepCurrent.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            btnKeepCurrent.Click += BtnKeepCurrent_Click;

            pnlUpdateAvailable.Controls.Add(lblUpdateTitle);
            pnlUpdateAvailable.Controls.Add(lblUpdateInfo);
            pnlUpdateAvailable.Controls.Add(lblUpdateDesc);
            pnlUpdateAvailable.Controls.Add(btnUpdateNow);
            pnlUpdateAvailable.Controls.Add(btnKeepCurrent);

            // Dodavanje komponenti na glavnu karticu
            pnlCard.Controls.Add(btnClose);
            pnlCard.Controls.Add(lblLogoBadge);
            pnlCard.Controls.Add(lblTitle);
            pnlCard.Controls.Add(lblSubtitle);
            pnlCard.Controls.Add(lblVersionTag);
            pnlCard.Controls.Add(lblStatus);
            pnlCard.Controls.Add(lblDetail);
            pnlCard.Controls.Add(progressBar);
            pnlCard.Controls.Add(lblPercent);
            pnlCard.Controls.Add(pnlDiagnostic);
            pnlCard.Controls.Add(pnlUpToDate);
            pnlCard.Controls.Add(pnlUpdateAvailable);

            this.Controls.Add(pnlCard);
            this.MouseDown += Form_MouseDown;
            this.Shown += SplashForm_Shown;
            this.FormClosing += SplashForm_FormClosing;
            this.ResumeLayout(false);
        }

        private void PnlCard_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen borderPen = new Pen(Color.FromArgb(48, 54, 61), 1))
            {
                g.DrawRectangle(borderPen, 0, 0, pnlCard.Width - 1, pnlCard.Height - 1);
            }
            using (SolidBrush cyanBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
            {
                g.FillRectangle(cyanBrush, 0, 0, pnlCard.Width, 3);
            }
        }

        private void PnlDiagnostic_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen p = new Pen(Color.FromArgb(248, 81, 73), 1))
            {
                g.DrawRectangle(p, 0, 0, pnlDiagnostic.Width - 1, pnlDiagnostic.Height - 1);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(248, 81, 73)))
            {
                g.FillRectangle(b, 0, 0, pnlDiagnostic.Width, 2);
            }
        }

        private void PnlUpToDate_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen p = new Pen(Color.FromArgb(63, 185, 80), 1))
            {
                g.DrawRectangle(p, 0, 0, pnlUpToDate.Width - 1, pnlUpToDate.Height - 1);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(63, 185, 80)))
            {
                g.FillRectangle(b, 0, 0, pnlUpToDate.Width, 2);
            }
        }

        private void PnlUpdateAvailable_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1))
            {
                g.DrawRectangle(p, 0, 0, pnlUpdateAvailable.Width - 1, pnlUpdateAvailable.Height - 1);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(56, 189, 248)))
            {
                g.FillRectangle(b, 0, 0, pnlUpdateAvailable.Width, 2);
            }
        }

        private void Form_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        private void SplashForm_Shown(object sender, EventArgs e)
        {
            StartWorkflow();
        }

        private void SplashForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            CleanupTimers();
        }

        private void StartWorkflow()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();

            CleanupTimers();

            pnlDiagnostic.Visible = false;
            pnlUpToDate.Visible = false;
            pnlUpdateAvailable.Visible = false;

            lblStatus.Visible = true;
            lblDetail.Visible = true;
            progressBar.Visible = true;
            lblPercent.Visible = true;

            ThreadPool.QueueUserWorkItem(state => RunWorkflow(_cts.Token));
        }

        private void BtnRetry_Click(object sender, EventArgs e)
        {
            StopNetworkListener();
            StartWorkflow();
        }

        private void SetStatus(string status, string detail = null, int progress = -1)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke((Action)(() =>
            {
                if (status != null) lblStatus.Text = status;
                if (detail != null) lblDetail.Text = detail;
                if (progress >= 0)
                {
                    progressBar.Value = progress;
                    lblPercent.Text = progress + "%";
                }
            }));
        }

        #region Reaktivni Network Listener (Auto-Resume)

        private void StartNetworkListener()
        {
            lock (_netLock)
            {
                if (_isListeningForNetwork) return;
                _isListeningForNetwork = true;
            }

            try
            {
                WifiHelper.NetworkStatusChanged += OnNetworkStatusChanged;
                WifiHelper.NetworkAddressChanged += OnNetworkAddressChanged;
            }
            catch { }

            try
            {
                this.BeginInvoke((Action)(() =>
                {
                    if (_netListenTimer == null)
                    {
                        _netListenTimer = new System.Windows.Forms.Timer();
                        _netListenTimer.Interval = 1500;
                        _netListenTimer.Tick += NetListenTimer_Tick;
                    }
                    _netListenTimer.Start();
                }));
            }
            catch { }
        }

        private void StopNetworkListener()
        {
            lock (_netLock)
            {
                if (!_isListeningForNetwork && _netListenTimer == null) return;
                _isListeningForNetwork = false;
            }

            try
            {
                WifiHelper.NetworkStatusChanged -= OnNetworkStatusChanged;
                WifiHelper.NetworkAddressChanged -= OnNetworkAddressChanged;
            }
            catch { }

            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        if (_netListenTimer != null)
                        {
                            _netListenTimer.Stop();
                            _netListenTimer.Tick -= NetListenTimer_Tick;
                            _netListenTimer.Dispose();
                            _netListenTimer = null;
                        }
                    }));
                }
                else
                {
                    if (_netListenTimer != null)
                    {
                        _netListenTimer.Stop();
                        _netListenTimer.Tick -= NetListenTimer_Tick;
                        _netListenTimer.Dispose();
                        _netListenTimer = null;
                    }
                }
            }
            catch { }
        }

        private void OnNetworkStatusChanged(bool isConnected)
        {
            if (isConnected && WifiHelper.IsNetworkConnected())
            {
                TriggerAutoResume();
            }
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            if (WifiHelper.IsNetworkConnected())
            {
                TriggerAutoResume();
            }
        }

        private void NetListenTimer_Tick(object sender, EventArgs e)
        {
            if (WifiHelper.IsNetworkConnected())
            {
                TriggerAutoResume();
            }
        }

        private void TriggerAutoResume()
        {
            lock (_netLock)
            {
                if (!_isListeningForNetwork) return;
                _isListeningForNetwork = false;
            }

            StopNetworkListener();

            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke((Action)(() =>
            {
                pnlDiagnostic.Visible = false;
                SetStatus("Mrežna konekcija je detektovana!", "Mreža je aktivna, automatski pokrećem radni tok...", 15);
                StartWorkflow();
            }));
        }

        #endregion

        #region Up-To-Date Dijalog i Odbrojavanje (7 sekundi)

        private void StartCountdownTimer()
        {
            StopCountdownTimer();
            _countdownSeconds = 7;
            _countdownTimer = new System.Windows.Forms.Timer();
            _countdownTimer.Interval = 1000;
            _countdownTimer.Tick += CountdownTimer_Tick;
            _countdownTimer.Start();
        }

        private void StopCountdownTimer()
        {
            if (_countdownTimer != null)
            {
                _countdownTimer.Stop();
                _countdownTimer.Tick -= CountdownTimer_Tick;
                _countdownTimer.Dispose();
                _countdownTimer = null;
            }
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            _countdownSeconds--;
            if (_countdownSeconds > 0)
            {
                lblCountdown.Text = string.Format("Automatski nastavljam za {0}s...", _countdownSeconds);
                btnUpToDateContinue.Text = string.Format("[▶ Nastavi ({0})]", _countdownSeconds);
            }
            else
            {
                StopCountdownTimer();
                StartMasterUIAndExit();
            }
        }

        private void BtnUpToDateContinue_Click(object sender, EventArgs e)
        {
            StopCountdownTimer();
            StartMasterUIAndExit();
        }

        private void BtnUpToDateRedownload_Click(object sender, EventArgs e)
        {
            StopCountdownTimer();
            pnlUpToDate.Visible = false;
            ExecuteDownloadAndLaunch(_savedHubUrl, _savedNasHost, _savedRemoteVer, force: true);
        }

        private void BtnUpToDateDelete_Click(object sender, EventArgs e)
        {
            StopCountdownTimer();
            bool deleted = PayloadManager.DeleteLocalPayload();

            lblUpToDateTitle.Text = "LOKALNI PAKET JE OBRISAN";
            lblUpToDateTitle.ForeColor = Color.FromArgb(248, 81, 73); // crvena

            if (deleted)
            {
                lblUpToDateInfo.Text = "Lokalni direktorijum C:\\setup je očišćen.\r\nMožete pokrenuti sveže preuzimanje i raspakivanje paketa sa NAS servera.";
            }
            else
            {
                lblUpToDateInfo.Text = "Uklanjanje lokalnog paketa je delimično završeno.\r\nMožete pokrenuti ponovno preuzimanje radi prepisivanja datoteka.";
            }

            lblCountdown.Visible = false;
            btnUpToDateContinue.Visible = false;
            btnUpToDateDelete.Visible = false;

            btnUpToDateRedownload.Location = new Point(18, 140);
            btnUpToDateRedownload.Size = new Size(240, 42);
            btnUpToDateRedownload.Text = "[⬇ Čista nova instalacija]";
            btnUpToDateRedownload.BackColor = Color.FromArgb(35, 134, 54); // zelena
        }

        #endregion

        #region Update Available Dijalog

        private void BtnUpdateNow_Click(object sender, EventArgs e)
        {
            pnlUpdateAvailable.Visible = false;
            ExecuteDownloadAndLaunch(_savedHubUrl, _savedNasHost, _savedRemoteVer, force: true);
        }

        private void BtnKeepCurrent_Click(object sender, EventArgs e)
        {
            pnlUpdateAvailable.Visible = false;
            StartMasterUIAndExit();
        }

        #endregion

        private void CleanupTimers()
        {
            StopCountdownTimer();
            StopNetworkListener();
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupTimers();
            }
            base.Dispose(disposing);
        }

        private void ShowDiagnosticScreen(string reason)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            // Audio signal za operatera u servisu
            try
            {
                Console.Beep(800, 300);
            }
            catch { }

            this.BeginInvoke((Action)(() =>
            {
                _isRunning = false;
                StopCountdownTimer();

                lblStatus.Visible = false;
                lblDetail.Visible = false;
                progressBar.Visible = false;
                lblPercent.Visible = false;
                pnlUpToDate.Visible = false;
                pnlUpdateAvailable.Visible = false;

                string fullReason = reason ?? "Nepoznata greška";
                if (!string.IsNullOrEmpty(_lastRecoveryMessage))
                {
                    fullReason = fullReason + "\r\nOPORAVAK: " + _lastRecoveryMessage;
                }

                List<string> adapters = WifiHelper.GetNetworkAdapterStatus();
                txtAdapters.Text = string.Format("RAČUNAR: {0} ({1})\r\nUZROK: {2}\r\n\r\nDETEKTOVANI MREŽNI INTERFEJSI:\r\n{3}",
                    Environment.MachineName,
                    Environment.OSVersion.VersionString,
                    fullReason,
                    string.Join("\r\n", adapters.ToArray()));

                bool localExists = File.Exists(PayloadManager.TargetUiExe);
                btnLaunchLocal.Enabled = localExists;
                btnLaunchLocal.Visible = localExists;
                if (localExists)
                {
                    btnLaunchLocal.Text = "▶  Pokreni lokalno van mreže";
                    btnLaunchLocal.BackColor = Color.FromArgb(31, 111, 235);
                    btnLaunchLocal.ForeColor = Color.White;
                }
                else
                {
                    btnLaunchLocal.Text = "Lokalni UI nije preuzet";
                    btnLaunchLocal.BackColor = Color.FromArgb(33, 38, 45);
                    btnLaunchLocal.ForeColor = Color.FromArgb(110, 118, 129);
                }

                pnlDiagnostic.Visible = true;
                pnlDiagnostic.BringToFront();

                StartNetworkListener();
            }));
        }

        private void ShowUpToDateScreen(string hubUrl, string nasHost, PayloadVersionInfo remoteVer, string localVer)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke((Action)(() =>
            {
                _isRunning = false;
                _savedHubUrl = hubUrl;
                _savedNasHost = nasHost;
                _savedRemoteVer = remoteVer;

                StopNetworkListener();

                lblStatus.Visible = false;
                lblDetail.Visible = false;
                progressBar.Visible = false;
                lblPercent.Visible = false;
                pnlDiagnostic.Visible = false;
                pnlUpdateAvailable.Visible = false;

                lblUpToDateTitle.Text = "WIS UI JE VEĆ INSTALIRAN";
                lblUpToDateTitle.ForeColor = Color.FromArgb(63, 185, 80);

                string remVerStr = (remoteVer != null && !string.IsNullOrEmpty(remoteVer.Version)) ? remoteVer.Version : localVer;
                string localDisplay = localVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? localVer : "v" + localVer;
                string remoteDisplay = remVerStr.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? remVerStr : "v" + remVerStr;
                string infoText = string.Format("Lokalna verzija: {0} • Verzija na serveru: {1} (Potpis i SHA-256 potvrđeni)", localDisplay, remoteDisplay);
                if (!string.IsNullOrEmpty(_lastRecoveryMessage))
                {
                    infoText = infoText + "\r\n" + _lastRecoveryMessage;
                }
                lblUpToDateInfo.Text = infoText;

                lblCountdown.Visible = true;
                btnUpToDateContinue.Visible = true;
                btnUpToDateDelete.Visible = true;
                btnUpToDateRedownload.Visible = true;

                btnUpToDateRedownload.Location = new Point(183, 140);
                btnUpToDateRedownload.Size = new Size(165, 42);
                btnUpToDateRedownload.Text = "[⟳ Preuzmi ponovo]";
                btnUpToDateRedownload.BackColor = Color.FromArgb(31, 111, 235);

                _countdownSeconds = 3;
                lblCountdown.Text = string.Format("Automatski nastavljam za {0}s...", _countdownSeconds);
                btnUpToDateContinue.Text = string.Format("[▶ Nastavi ({0})]", _countdownSeconds);

                pnlUpToDate.Visible = true;
                pnlUpToDate.BringToFront();

                StartCountdownTimer();
            }));
        }

        private void ShowUpdateAvailableScreen(string hubUrl, string nasHost, PayloadVersionInfo remoteVer, string localVer)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke((Action)(() =>
            {
                _isRunning = false;
                _savedHubUrl = hubUrl;
                _savedNasHost = nasHost;
                _savedRemoteVer = remoteVer;

                StopNetworkListener();
                StopCountdownTimer();

                lblStatus.Visible = false;
                lblDetail.Visible = false;
                progressBar.Visible = false;
                lblPercent.Visible = false;
                pnlDiagnostic.Visible = false;
                pnlUpToDate.Visible = false;

                lblUpdateTitle.Text = "DOSTUPNO JE AŽURIRANJE UI PAKETA";
                lblUpdateTitle.ForeColor = Color.FromArgb(56, 189, 248);

                string remVerStr = (remoteVer != null && !string.IsNullOrEmpty(remoteVer.Version)) ? remoteVer.Version : "novija";
                string localDisplay = localVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? localVer : "v" + localVer;
                string remoteDisplay = remVerStr.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? remVerStr : "v" + remVerStr;

                lblUpdateInfo.Text = string.Format("Lokalna verzija: {0} ➔ Nova verzija na serveru: {1}",
                    localDisplay,
                    remoteDisplay);

                if (!string.IsNullOrEmpty(_lastRecoveryMessage))
                {
                    lblUpdateDesc.Text = _lastRecoveryMessage;
                }

                pnlUpdateAvailable.Visible = true;
                pnlUpdateAvailable.BringToFront();
            }));
        }

        private void ExecuteDownloadAndLaunch(string hubUrl, string nasHost, PayloadVersionInfo remoteVer, bool force)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke((Action)(() =>
            {
                pnlDiagnostic.Visible = false;
                pnlUpToDate.Visible = false;
                pnlUpdateAvailable.Visible = false;

                lblStatus.Visible = true;
                lblDetail.Visible = true;
                progressBar.Visible = true;
                lblPercent.Visible = true;
            }));

            ThreadPool.QueueUserWorkItem(state =>
            {
                IDisposable updateSession = null;
                try
                {
                    updateSession = PayloadManager.BeginUpdateSession();
                    bool downloadSuccess = false;

                    // 1. Primarni kanal: ServisHub HTTP API (force zaobilazi samo proveru ažurnosti, NIKADA potpis ili SHA-256!)
                    if (!string.IsNullOrEmpty(hubUrl))
                    {
                        if (remoteVer == null)
                        {
                            remoteVer = PayloadManager.GetRemoteVersion(hubUrl);
                        }

                        if (remoteVer != null && remoteVer.IsSignatureValid && !string.IsNullOrEmpty(remoteVer.Sha256))
                        {
                            string targetVer = !string.IsNullOrEmpty(remoteVer.Version) ? remoteVer.Version : "aktuelna";
                            SetStatus("Preuzimanje UI sa NAS-a (" + targetVer + ")...", "Inicijalizacija verifikovanog HTTP prenosa...", 45);

                            downloadSuccess = PayloadManager.DownloadPayload(
                                hubUrl,
                                remoteVer.Sha256,
                                remoteVer.RawJson,
                                (received, total, speed) =>
                                {
                                    int pct = total > 0 ? (int)((received * 45) / total) + 45 : 60;
                                    double recMb = received / (1024.0 * 1024.0);
                                    double totMb = total > 0 ? total / (1024.0 * 1024.0) : 0;
                                    double speedMb = speed / (1024.0 * 1024.0);

                                    string detailStr = total > 0
                                        ? string.Format("Preuzeto: {0:F1} MB / {1:F1} MB ({2}%) • {3:F1} MB/s", recMb, totMb, pct, speedMb)
                                        : string.Format("Preuzeto: {0:F1} MB • {1:F1} MB/s", recMb, speedMb);

                                    SetStatus(null, detailStr, pct);
                                });
                        }
                    }

                    // 2. Sekundarni fallback kanal: NAS SMB deljenje (\\host\servis) uz obavezan potpisani payload_meta.json
                    if (!downloadSuccess && !string.IsNullOrEmpty(nasHost))
                    {
                        SetStatus("Preuzimanje UI preko SMB deljenja sa NAS-a...", "Povezivanje i verifikacija potpisa na \\\\" + nasHost + "\\servis...", 50);
                        downloadSuccess = PayloadManager.DownloadFromSmb(nasHost, (received, total, speed) =>
                        {
                            int pct = total > 0 ? (int)((received * 40) / total) + 50 : 70;
                            double recMb = received / (1024.0 * 1024.0);
                            double speedMb = speed / (1024.0 * 1024.0);
                            SetStatus(null, string.Format("Kopirano: {0:F1} MB • {1:F1} MB/s", recMb, speedMb), pct);
                        });
                    }

                    // Obavezna provera uspešnosti preuzimanja i RSA/SHA-256 verifikacije
                    if (!downloadSuccess)
                    {
                        string dlErr = !string.IsNullOrEmpty(PayloadManager.LastError)
                            ? PayloadManager.LastError
                            : ("Nije moguće preuzeti ili verifikovati potpisani UI paket sa NAS servera (" + (hubUrl ?? nasHost ?? "nepoznat") + ").");
                        ShowDiagnosticScreen(dlErr);
                        return;
                    }

                    // 3. Transakciona ekstrakcija u .staging_extract, Zip-Slip provera, verifikacija fajlova i promocija
                    SetStatus("Raspakivanje i verifikacija UI paketa...", "Transakciona ekstrakcija u C:\\setup\\.staging_extract...", 92);

                    bool extOk = PayloadManager.ExtractPayload((stepMsg, cur, total) =>
                    {
                        int pct = total > 0 ? 92 + (int)((cur * 7) / total) : 95;
                        SetStatus(null, stepMsg, pct);
                    });

                    if (!extOk)
                    {
                        if (PayloadManager.SelfUpdateDelegated) { Environment.Exit(0); return; }
                        string extErr = !string.IsNullOrEmpty(PayloadManager.LastError)
                            ? PayloadManager.LastError
                            : "Neuspešno raspakivanje, verifikacija ili promocija UI paketa u C:\\setup.";
                        ShowDiagnosticScreen(extErr);
                        return;
                    }

                    // 4. Pokretanje Master UI-ja
                    SetStatus("WIS UI je spreman!", "Pokretanje radnog okruženja...", 100);
                    Thread.Sleep(600);

                    StartMasterUIAndExit();
                }
                catch (Exception ex)
                {
                    ShowDiagnosticScreen("Neočekivana greška u Launcheru: " + ex.Message);
                }
                finally { if (updateSession != null) updateSession.Dispose(); }
            });
        }

        private void RunWorkflow(CancellationToken token)
        {
            try
            {
                // 0. Provera i automatski rollback ako je prethodno ažuriranje prekinuto pre commita
                bool rolledBack;
                string recoveryMsg;
                if (!PayloadManager.RecoverInterruptedUpdateIfNeeded(out rolledBack, out recoveryMsg))
                {
                    ShowDiagnosticScreen(recoveryMsg);
                    return;
                }
                if (rolledBack)
                {
                    _lastRecoveryMessage = recoveryMsg;
                    SetStatus("Oporavak paketa izvršen...", recoveryMsg, 5);
                    Thread.Sleep(1200);
                }

                SetStatus("Provera mrežne konekcije...", "Analiza fizičkih adaptera i IP konfiguracije...", 10);
                Thread.Sleep(400);

                // 1. Provera mreze & Spajanje na Wi-Fi
                if (!WifiHelper.IsNetworkConnected())
                {
                    SetStatus("Mreža nije aktivna...", "Pokušaj automatskog povezivanja na servisni Wi-Fi...", 15);
                    bool connectedWifi = WifiHelper.TryConnectServisWifi(msg => SetStatus(null, msg, -1));

                    if (!connectedWifi && !WifiHelper.IsNetworkConnected())
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            Thread.Sleep(500);
                            if (WifiHelper.IsNetworkConnected()) break;
                        }
                    }
                }

                if (!WifiHelper.IsNetworkConnected())
                {
                    PayloadVersionInfo offlineManifest;
                    string offlineVerifyErr;
                    if (PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out offlineManifest, out offlineVerifyErr) && offlineManifest != null)
                    {
                        ShowUpToDateScreen(null, null, offlineManifest, PayloadManager.GetLocalVersion());
                        return;
                    }
                    ShowDiagnosticScreen("Nema aktivne mrežne konekcije. Povežite LAN kabl ili Wi-Fi radi preuzimanja paketa sa NAS servera.");
                    return;
                }

                SetStatus("Mreža je aktivna!", "Pretraga NAS servera na lokalnoj mreži...", 25);
                Thread.Sleep(300);

                // 2. Pronalazenje kandidata adrese NAS servera / ServisHub-a
                string hubUrl = HubDiscovery.DiscoverHubUrl(msg => SetStatus("Pretraga NAS servera...", msg, -1));
                string nasHost = HubDiscovery.GetNasHost(hubUrl);
                PayloadVersionInfo localManifest;
                string localVerifyErr;
                bool isLocalPackageVerified = PayloadManager.TryVerifyInstalledPayload(PayloadManager.SetupDir, out localManifest, out localVerifyErr);

                // 3. Primarni kanal: ServisHub HTTP API (obavezan potpisan manifest)
                if (!string.IsNullOrEmpty(hubUrl))
                {
                    SetStatus("NAS ServisHub kandidat otkriven: " + hubUrl, "Provera RSA potpisa i verzije UI paketa...", 40);
                    Thread.Sleep(300);

                    PayloadVersionInfo remoteVer = PayloadManager.GetRemoteVersion(hubUrl);
                    string effectiveHubUrl = hubUrl;
                    if (!string.IsNullOrEmpty(nasHost))
                    {
                        PayloadVersionInfo smbVer = PayloadManager.GetRemoteSmbVersion(nasHost);
                        if (smbVer != null && smbVer.IsSignatureValid && !string.IsNullOrEmpty(smbVer.Version))
                        {
                            if (remoteVer == null ||
                                !remoteVer.IsSignatureValid ||
                                string.IsNullOrEmpty(remoteVer.Version) ||
                                PayloadManager.CompareVersions(smbVer.Version, remoteVer.Version) >= 0)
                            {
                                remoteVer = smbVer;
                                effectiveHubUrl = null;
                            }
                        }
                    }

                    string localVer = PayloadManager.GetLocalVersion();
                    bool localExists = File.Exists(PayloadManager.TargetUiExe);

                    if (remoteVer != null && remoteVer.IsSignatureValid && !string.IsNullOrEmpty(remoteVer.Version))
                    {
                        if (localExists && isLocalPackageVerified)
                        {
                            int cmp = PayloadManager.CompareVersions(localVer, remoteVer.Version);
                            bool isLocalVerifiedAndUpToDate = (!string.IsNullOrEmpty(remoteVer.Sha256) && PayloadManager.IsLocalPayloadUpToDate(remoteVer.Sha256))
                                || (isLocalPackageVerified && cmp > 0);

                            if (isLocalVerifiedAndUpToDate && cmp >= 0)
                            {
                                ShowUpToDateScreen(effectiveHubUrl, nasHost, remoteVer, localVer);
                                return;
                            }
                        }

                        // Ako lokalni UI ne postoji, nije verifikovan ili postoji noviji potpisani paket na NAS-u:
                        // direktno verifikovano preuzimanje, čišćenje C:\setup i startovanje (Zero-Touch)
                        ExecuteDownloadAndLaunch(effectiveHubUrl, nasHost, remoteVer, force: true);
                        return;
                    }
                    else
                    {
                        // Ako server vraća stariji/nekompatibilan manifest (npr. 2.16.x dok je na ISO-u već potpisan i verifikovan 2.17.0),
                        // nastavljamo automatski sa verifikovanim lokalnim paketom bez zaustavljanja na crvenom ekranu.
                        if (isLocalPackageVerified && localManifest != null)
                        {
                            ShowUpToDateScreen(effectiveHubUrl, nasHost, localManifest, localVer);
                            return;
                        }

                        // Server na hubUrl nije vratio validno potpisan manifest — pokušaj SMB fallback sa potpisanim manifestom
                        if (!string.IsNullOrEmpty(nasHost))
                        {
                            ExecuteDownloadAndLaunch(null, nasHost, null, force: false);
                            return;
                        }

                        ShowDiagnosticScreen("Manifest na otkrivenom serveru (" + hubUrl + ") nema validan RSA-2048 potpis: " + PayloadManager.LastError);
                        return;
                    }
                }

                // 4. Sekundarni fallback kanal: ako postoji kriptografski verifikovan lokalni paket, nastavi sa njim
                if (isLocalPackageVerified && localManifest != null)
                {
                    ShowUpToDateScreen(hubUrl, nasHost, localManifest, PayloadManager.GetLocalVersion());
                    return;
                }

                if (!string.IsNullOrEmpty(nasHost))
                {
                    ExecuteDownloadAndLaunch(null, nasHost, null, force: false);
                    return;
                }

                ShowDiagnosticScreen("Nije moguće pronaći niti preuzeti potpisani UI paket sa NAS servera (" + (hubUrl ?? nasHost ?? "nepoznat") + ").");
            }
            catch (Exception ex)
            {
                ShowDiagnosticScreen("Neočekivana greška u Launcheru: " + ex.Message);
            }
        }

        private static void StartMasterUIAndExit()
        {
            try
            {
                Process[] existingUi = Process.GetProcessesByName("WIS.UI");
                if (existingUi != null && existingUi.Length > 0)
                {
                    // WIS.UI je već aktivan na sistemu — izađi tiho bez ponovnog pokretanja
                    Environment.Exit(0);
                    return;
                }
            }
            catch { }

            string error;
            if (!PayloadManager.TryStartVerifiedLocalUi(out error))
            {
                MessageBox.Show("Lokalni WIS paket nije bezbedno pokrenut:\r\n" + error,
                    "WIS Greška", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Environment.Exit(0);
        }
    }
}
