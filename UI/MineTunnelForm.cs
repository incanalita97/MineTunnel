using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using MineTunnel.Common;
using MineTunnel.Core;

namespace MineTunnel.UI
{
    public class MineTunnelForm : Form
    {
        // ====================================================================
        // Modern Gaming Dark Theme Palette (Discord / Steam Overlay Style)
        // ====================================================================
        private readonly Color BgDark = Color.FromArgb(16, 16, 20);            // Deep background (#101014)
        private readonly Color CardBg = Color.FromArgb(26, 26, 34);            // Elevated card surface (#1A1A22)
        private readonly Color CardHeaderBg = Color.FromArgb(32, 32, 42);      // Elevated card header (#20202A)
        private readonly Color BorderColor = Color.FromArgb(46, 46, 58);        // Modern 1px boundary (#2E2E3A)
        private readonly Color InputBg = Color.FromArgb(18, 18, 24);           // Sleek input fields (#121218)
        private readonly Color TextLight = Color.FromArgb(248, 250, 252);      // High-contrast primary text
        private readonly Color TextMuted = Color.FromArgb(156, 163, 175);      // Clean secondary text (#9CA3AF)
        private readonly Color AccentGreen = Color.FromArgb(16, 185, 129);     // Vibrant emerald (#10B981)
        private readonly Color AccentGreenHover = Color.FromArgb(5, 150, 105); // (#059669)
        private readonly Color AccentBlue = Color.FromArgb(99, 102, 241);       // Indigo primary action (#6366F1)
        private readonly Color AccentBlueHover = Color.FromArgb(79, 70, 229);
        private readonly Color AccentRed = Color.FromArgb(239, 68, 68);         // Disconnect / Stop (#EF4444)
        private readonly Color AccentRedHover = Color.FromArgb(220, 38, 38);
        private readonly Color AccentGold = Color.FromArgb(245, 158, 11);       // NAT punching / Waiting (#F59E0B)

        // Core Services
        private LanMulticastBridge _lanBridge;
        private MinecraftDetector _detector;
        private RelayHostConnector _relayHost;
        private TunnelClient _relayClient;
        private P2PHolePuncher _p2pPuncher;
        private P2PStreamBridge _p2pBridge;

        // Header Controls
        private Label _lblHeaderMcPill;

        // UI Controls - Host Tab
        private Label _lblMcStatus;
        private Label _lblMcDetails;
        private TextBox _txtHostRoom;
        private Button _btnP2PHostToggle;
        private Label _lblP2PHostStatus;
        private TextBox _txtP2PInviteCode;
        private Button _btnCopyP2PCode;
        private ListView _lstPeers;
        private Label _lblPeersEmptyHint;
        private Label _lblPeersCountBadge;

        // UI Controls - Join Tab
        private TextBox _txtJoinPlayerName;
        private TextBox _txtJoinP2PCode;
        private Button _btnPasteJoinCode;
        private Button _btnJoinP2PToggle;
        private Label _lblJoinP2PStatus;
        private Label _lblJoinP2PPing;
        private Label _lblJoinP2PHelp;
        private Button _btnCopyProxyAddress;

        // UI Controls - Relay & Advanced Tab
        private TextBox _txtRelayServer;
        private TextBox _txtHostPassword;
        private Button _btnRelayHostToggle;
        private Button _btnCopyRelayAddress;
        private TextBox _txtJoinAddress;
        private TextBox _txtJoinRoom;
        private TextBox _txtJoinPassword;
        private TextBox _txtPlayerName;
        private Button _btnJoinRelayToggle;

        // Status Bar
        private Label _lblStatusMsg;
        private Label _lblNetworkIp;

        // Layout Delegates
        private Action _refreshHostLayout;
        private Action _refreshJoinLayout;
        private Action _refreshGuideLayout;
        private Action _refreshRelayLayout;

        public MineTunnelForm()
        {
            InitializeComponent();
            InitializeCoreServices();
            LoadConfig();
        }

        private void InitializeComponent()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.Text = "MineTunnel - Zero-Setup P2P Minecraft Multiplayer";
            this.Size = new Size(880, 750);
            this.MinimumSize = new Size(780, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgDark;
            this.ForeColor = TextLight;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // ================================================================
            // Top Modern Header Banner (Matching the Digital Artifact)
            // ================================================================
            Panel banner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = CardBg,
                Padding = new Padding(20, 12, 20, 12)
            };
            banner.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen borderPen = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawLine(borderPen, 0, banner.Height - 1, banner.Width, banner.Height - 1);
                }
            };

            Label lblTitle = new Label
            {
                Text = "⚡ MineTunnel",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = AccentGreen,
                AutoSize = true,
                Location = new Point(18, 10)
            };

            Label lblVersionBadge = new Label
            {
                Text = "v2.4 P2P Hardened",
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = TextMuted,
                BackColor = Color.FromArgb(34, 34, 46),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(6, 2, 6, 2),
                AutoSize = true,
                Location = new Point(186, 17)
            };
            lblVersionBadge.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, lblVersionBadge.Width - 1, lblVersionBadge.Height - 1);
                }
            };

            Label lblSubtitle = new Label
            {
                Text = "Direct P2P Multi-Player • Zero Accounts • No Port Forwarding • Lowest Ping • Bypasses PLDT CGNAT",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(20, 42)
            };

            _lblHeaderMcPill = new Label
            {
                Text = "● Scanning for Minecraft...",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = TextMuted,
                BackColor = InputBg,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(14, 6, 14, 6),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lblHeaderMcPill.Location = new Point(banner.ClientSize.Width - 250, 18);
            _lblHeaderMcPill.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, _lblHeaderMcPill.Width - 1, _lblHeaderMcPill.Height - 1);
                }
            };
            banner.Resize += delegate
            {
                _lblHeaderMcPill.Location = new Point(banner.ClientSize.Width - _lblHeaderMcPill.Width - 20, 18);
            };

            banner.Controls.Add(lblTitle);
            banner.Controls.Add(lblVersionBadge);
            banner.Controls.Add(lblSubtitle);
            banner.Controls.Add(_lblHeaderMcPill);

            // ================================================================
            // Tab Control with Custom Sleek Owner-Draw
            // ================================================================
            TabControl tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Point(20, 8),
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(160, 36)
            };

            tabControl.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                TabPage page = tabControl.TabPages[e.Index];
                bool isSelected = (tabControl.SelectedIndex == e.Index);

                // Tab Header Background
                using (SolidBrush bgBrush = new SolidBrush(isSelected ? CardBg : BgDark))
                {
                    e.Graphics.FillRectangle(bgBrush, e.Bounds);
                }

                // Selected Tab Accent Bar
                if (isSelected)
                {
                    using (SolidBrush accentBrush = new SolidBrush(AccentGreen))
                    {
                        e.Graphics.FillRectangle(accentBrush, e.Bounds.Left, e.Bounds.Bottom - 3, e.Bounds.Width, 3);
                    }
                }

                // Tab Header Text
                Color textColor = isSelected ? AccentGreen : TextMuted;
                TextRenderer.DrawText(e.Graphics, page.Text, e.Font, e.Bounds, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };

            TabPage tabHost = new TabPage("👑 Host Game");
            tabHost.BackColor = BgDark;
            BuildHostTab(tabHost);

            TabPage tabJoin = new TabPage("🎮 Join Game");
            tabJoin.BackColor = BgDark;
            BuildJoinTab(tabJoin);

            TabPage tabGuide = new TabPage("📖 How to Play");
            tabGuide.BackColor = BgDark;
            BuildGuideTab(tabGuide);

            TabPage tabRelay = new TabPage("⚙️ Relay & Advanced");
            tabRelay.BackColor = BgDark;
            BuildRelayTab(tabRelay);

            tabControl.TabPages.Add(tabHost);
            tabControl.TabPages.Add(tabJoin);
            tabControl.TabPages.Add(tabGuide);
            tabControl.TabPages.Add(tabRelay);

            // ================================================================
            // Modern Status Bar Panel
            // ================================================================
            Panel statusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = CardBg,
                Padding = new Padding(16, 6, 16, 6)
            };
            statusBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen borderPen = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawLine(borderPen, 0, 0, statusBar.Width, 0);
                }
            };

            _lblStatusMsg = new Label
            {
                Text = "Ready. Start your Minecraft server or LAN world, then click 'Start P2P Host'.",
                ForeColor = TextMuted,
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI", 9f)
            };

            _lblNetworkIp = new Label
            {
                Text = "Local IP: Discovering...",
                ForeColor = AccentGreen,
                Dock = DockStyle.Right,
                AutoSize = true,
                Font = new Font("Consolas", 9f, FontStyle.Bold)
            };

            statusBar.Controls.Add(_lblStatusMsg);
            statusBar.Controls.Add(_lblNetworkIp);

            this.Controls.Add(tabControl);
            this.Controls.Add(statusBar);
            this.Controls.Add(banner);

            banner.SendToBack();
            statusBar.SendToBack();
            tabControl.BringToFront();
        }

        // ====================================================================
        // TAB 1: HOST GAME (Clean, Focused, Modern Cards)
        // ====================================================================
        private void BuildHostTab(TabPage tab)
        {
            tab.AutoScroll = true;
            Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };

            int cardWidth = Math.Max(500, container.ClientSize.Width - 32);

            // 1. Live Minecraft Detection Status Card
            Panel cardDetect = CreateCardPanel("Local Minecraft Server Status", 16, 12, cardWidth, 90, "🔍");

            Button btnRescan = CreateSecondaryButton("🔄 Re-Scan", cardWidth - 110, 4, 96, 24);
            btnRescan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRescan.Font = new Font("Segoe UI", 8.5f);
            btnRescan.Click += delegate
            {
                if (_detector != null)
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        _detector.CheckServerStatus();
                    });
                    SetStatus("Scanned for active Minecraft servers.");
                }
            };
            cardDetect.Controls.Add(btnRescan);

            _lblMcStatus = new Label
            {
                Text = "● Scanning for Java Minecraft (Paper, Fabric, Forge, Vanilla, LAN)...",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Location = new Point(18, 38),
                AutoSize = true
            };

            _lblMcDetails = new Label
            {
                Text = "Tip: Start Minecraft and click 'Open to LAN' in singleplayer, or run your server.jar on port 25565.",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9f),
                Location = new Point(18, 62),
                AutoSize = true
            };
            cardDetect.Controls.Add(_lblMcStatus);
            cardDetect.Controls.Add(_lblMcDetails);
            container.Controls.Add(cardDetect);

            // 2. Direct P2P Host & Invite Code Card
            Panel cardHost = CreateCardPanel("Host & Share Direct P2P Game", 16, 114, cardWidth, 230, "⚡");

            Label lblRName = new Label
            {
                Text = "World / Room Name:",
                Location = new Point(18, 42),
                AutoSize = true,
                ForeColor = TextLight,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            _txtHostRoom = new TextBox
            {
                Text = "SteveWorld",
                Location = new Point(175, 39),
                Width = 220,
                BackColor = InputBg,
                ForeColor = TextLight,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f)
            };

            _btnP2PHostToggle = CreatePrimaryButton("▶ Start P2P Host", AccentGreen, AccentGreenHover, 18, 76, 180, 36);
            _btnP2PHostToggle.Click += BtnP2PHostToggle_Click;

            _lblP2PHostStatus = new Label
            {
                Text = "● Ready to host",
                Location = new Point(212, 84),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            Label lblCodePrompt = new Label
            {
                Text = "P2P Invite Code for Friends (Share this code):",
                Location = new Point(18, 126),
                AutoSize = true,
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _txtP2PInviteCode = new TextBox
            {
                ReadOnly = true,
                Text = "Click '▶ Start P2P Host' above to resolve your STUN endpoint and generate invite code",
                BackColor = InputBg,
                ForeColor = TextMuted,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(18, 150),
                Size = new Size(Math.Max(380, cardWidth - 210), 28)
            };
            _txtP2PInviteCode.Click += delegate { _txtP2PInviteCode.SelectAll(); };

            _btnCopyP2PCode = CreateSecondaryButton("📋 Copy Invite Code", cardWidth - 180, 148, 165, 30);
            _btnCopyP2PCode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCopyP2PCode.Click += BtnCopyP2PCode_Click;

            Label lblCodeTip = new Label
            {
                Text = "Friends paste this into their 'Join Game' tab to connect directly via RFC 5389 UDP hole punching.",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(18, 188),
                AutoSize = true
            };

            cardHost.Controls.Add(lblRName);
            cardHost.Controls.Add(_txtHostRoom);
            cardHost.Controls.Add(_btnP2PHostToggle);
            cardHost.Controls.Add(_lblP2PHostStatus);
            cardHost.Controls.Add(lblCodePrompt);
            cardHost.Controls.Add(_txtP2PInviteCode);
            cardHost.Controls.Add(_btnCopyP2PCode);
            cardHost.Controls.Add(lblCodeTip);
            container.Controls.Add(cardHost);

            // 3. Connected Friends List Card (Custom-Drawn Dark Table)
            int peersHeight = Math.Max(180, container.ClientSize.Height - 380);
            Panel cardPeers = CreateCardPanel("Connected Friends (Direct P2P)", 16, 356, cardWidth, peersHeight, "👥");

            _lblPeersCountBadge = new Label
            {
                Text = "● 0 Players",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Location = new Point(cardPeers.ClientSize.Width - 110, 8),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            cardPeers.Controls.Add(_lblPeersCountBadge);

            _lstPeers = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = InputBg,
                ForeColor = TextLight,
                Location = new Point(16, 40),
                Size = new Size(cardPeers.ClientSize.Width - 32, cardPeers.ClientSize.Height - 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f),
                OwnerDraw = true
            };
            _lstPeers.Columns.Add("Friend / Player Name", 220);
            _lstPeers.Columns.Add("Direct P2P Endpoint", 200);
            _lstPeers.Columns.Add("Status", 140);
            _lstPeers.Columns.Add("Ping Latency", 110);

            // Custom Owner-Draw for ListView (Exact Dark Mode Table Aesthetic)
            _lstPeers.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(CardHeaderBg))
                {
                    e.Graphics.FillRectangle(b, e.Bounds);
                }
                using (Pen p = new Pen(BorderColor))
                {
                    e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
                }
                TextRenderer.DrawText(e.Graphics, e.Header.Text, new Font("Segoe UI", 9f, FontStyle.Bold),
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                    TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };

            _lstPeers.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                Color textCol = TextLight;
                if (e.ColumnIndex == 2 && e.SubItem.Text.Contains("Connected")) textCol = AccentGreen;
                else if (e.ColumnIndex == 3 && e.SubItem.Text.Contains("ms")) textCol = AccentGreen;

                using (SolidBrush b = new SolidBrush(e.ItemIndex % 2 == 0 ? InputBg : CardBg))
                {
                    e.Graphics.FillRectangle(b, e.Bounds);
                }
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, new Font("Segoe UI", 9f),
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                    textCol, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };

            _lblPeersEmptyHint = new Label
            {
                Text = "No friends connected yet. Click '▶ Start P2P Host' and share your Invite Code above!",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                BackColor = InputBg
            };

            cardPeers.Controls.Add(_lstPeers);
            container.Controls.Add(cardPeers);

            _refreshHostLayout = delegate
            {
                int w = Math.Max(500, container.ClientSize.Width - 32);
                cardDetect.Width = w;
                btnRescan.Location = new Point(w - 110, 4);
                cardHost.Width = w;
                if (_txtP2PInviteCode != null) _txtP2PInviteCode.Width = Math.Max(380, w - 210);
                if (_btnCopyP2PCode != null) _btnCopyP2PCode.Location = new Point(w - 180, 148);
                cardPeers.Width = w;
                int h = Math.Max(160, container.ClientSize.Height - 380);
                cardPeers.Height = h;
                if (_lblPeersCountBadge != null) _lblPeersCountBadge.Location = new Point(w - 110, 8);
            };
            container.Resize += delegate { _refreshHostLayout(); };

            tab.Controls.Add(container);
        }

        // ====================================================================
        // TAB 2: JOIN GAME (User-Friendly, Instant Paste, Clear Guidance)
        // ====================================================================
        private void BuildJoinTab(TabPage tab)
        {
            tab.AutoScroll = true;
            Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };

            int cardWidth = Math.Max(500, container.ClientSize.Width - 32);

            // 1. Direct P2P Join Card
            Panel cardJoin = CreateCardPanel("Connect to Friend's World (Direct P2P)", 16, 12, cardWidth, 270, "⚡");

            Label lblPName = new Label
            {
                Text = "Your In-Game Name:",
                Location = new Point(18, 42),
                AutoSize = true,
                ForeColor = TextLight,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            _txtJoinPlayerName = new TextBox
            {
                Text = ConfigManager.Instance.PlayerName,
                Location = new Point(175, 39),
                Size = new Size(200, 26),
                BackColor = InputBg,
                ForeColor = TextLight,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f)
            };

            Label lblCodePrompt = new Label
            {
                Text = "Friend's P2P Invite Code:",
                Location = new Point(18, 76),
                AutoSize = true,
                ForeColor = TextLight,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            _txtJoinP2PCode = new TextBox
            {
                Text = "",
                Location = new Point(18, 98),
                Width = Math.Max(380, cardWidth - 210),
                BackColor = InputBg,
                ForeColor = AccentGreen,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnPasteJoinCode = CreateSecondaryButton("📋 Paste from Clipboard", cardWidth - 180, 96, 165, 30);
            _btnPasteJoinCode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnPasteJoinCode.Click += BtnPasteJoinCode_Click;

            _btnJoinP2PToggle = CreatePrimaryButton("⚡ Connect to Friend", AccentGreen, AccentGreenHover, 18, 142, 200, 36);
            _btnJoinP2PToggle.Click += BtnJoinP2PToggle_Click;

            _lblJoinP2PStatus = new Label
            {
                Text = "● Disconnected",
                Location = new Point(230, 150),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };

            _lblJoinP2PPing = new Label
            {
                Text = "Ping: -- ms",
                Location = new Point(440, 151),
                AutoSize = true,
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9.5f)
            };

            Label lblJoinInstructions = new Label
            {
                Text = "Enter your friend's invite code and click 'Connect'. Once connected, Minecraft will discover the world automatically!",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(18, 192),
                AutoSize = true
            };

            cardJoin.Controls.Add(lblPName);
            cardJoin.Controls.Add(_txtJoinPlayerName);
            cardJoin.Controls.Add(lblCodePrompt);
            cardJoin.Controls.Add(_txtJoinP2PCode);
            cardJoin.Controls.Add(_btnPasteJoinCode);
            cardJoin.Controls.Add(_btnJoinP2PToggle);
            cardJoin.Controls.Add(_lblJoinP2PStatus);
            cardJoin.Controls.Add(_lblJoinP2PPing);
            cardJoin.Controls.Add(lblJoinInstructions);
            container.Controls.Add(cardJoin);

            // 2. Minecraft In-Game Connection Guide Card (Matching the Digital Artifact)
            Panel cardInGame = CreateCardPanel("How to Join in Minecraft", 16, 296, cardWidth, 230, "🎮");

            Label lblStep1 = new Label
            {
                Text = "1. Open Minecraft -> Click 'Multiplayer'",
                Location = new Point(18, 42),
                AutoSize = true,
                ForeColor = TextLight,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            // Highlighted emerald LAN banner
            Panel pnlLanHighlight = new Panel
            {
                Location = new Point(18, 70),
                Size = new Size(cardWidth - 36, 34),
                BackColor = Color.FromArgb(20, 36, 28)
            };
            pnlLanHighlight.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(27, 61, 43), 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, pnlLanHighlight.Width - 1, pnlLanHighlight.Height - 1);
                }
            };

            Label lblStep2 = new Label
            {
                Text = "2. Look under 'LAN Games' at the bottom — your friend's world appears automatically!",
                Location = new Point(8, 8),
                AutoSize = true,
                ForeColor = AccentGreen,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            pnlLanHighlight.Controls.Add(lblStep2);

            Label lblStep3 = new Label
            {
                Text = "3. Alternatively, click 'Direct Connection' and enter:",
                Location = new Point(18, 114),
                AutoSize = true,
                ForeColor = TextLight,
                Font = new Font("Segoe UI", 9.5f)
            };

            _lblJoinP2PHelp = new Label
            {
                Text = "🎮 Server Address: 127.0.0.1:25565",
                ForeColor = Color.Gold,
                Location = new Point(18, 144),
                AutoSize = true,
                Font = new Font("Consolas", 11f, FontStyle.Bold)
            };

            _btnCopyProxyAddress = CreateSecondaryButton("📋 Copy Address", cardWidth - 170, 138, 150, 32);
            _btnCopyProxyAddress.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCopyProxyAddress.Click += BtnCopyProxyAddress_Click;

            Label lblProxyNote = new Label
            {
                Text = "MineTunnel runs a local proxy that securely encrypts and tunnels your game data straight to the host.",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(18, 185),
                AutoSize = true
            };

            cardInGame.Controls.Add(lblStep1);
            cardInGame.Controls.Add(pnlLanHighlight);
            cardInGame.Controls.Add(lblStep3);
            cardInGame.Controls.Add(_lblJoinP2PHelp);
            cardInGame.Controls.Add(_btnCopyProxyAddress);
            cardInGame.Controls.Add(lblProxyNote);
            container.Controls.Add(cardInGame);

            _refreshJoinLayout = delegate
            {
                int w = Math.Max(500, container.ClientSize.Width - 32);
                cardJoin.Width = w;
                if (_txtJoinP2PCode != null) _txtJoinP2PCode.Width = Math.Max(380, w - 210);
                if (_btnPasteJoinCode != null) _btnPasteJoinCode.Location = new Point(w - 180, 96);
                cardInGame.Width = w;
                pnlLanHighlight.Width = w - 36;
                if (_btnCopyProxyAddress != null) _btnCopyProxyAddress.Location = new Point(w - 170, 138);
            };
            container.Resize += delegate { _refreshJoinLayout(); };

            tab.Controls.Add(container);
        }

        // ====================================================================
        // TAB 3: HOW TO PLAY (Clean Visual Step-by-Step Cards)
        // ====================================================================
        private void BuildGuideTab(TabPage tab)
        {
            tab.AutoScroll = true;
            Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };

            int cardWidth = Math.Max(500, container.ClientSize.Width - 32);

            // Card 1: Fast Setup Steps
            Panel cardSteps = CreateCardPanel("3-Step Quick Start Guide", 16, 12, cardWidth, 230, "🚀");

            string[] hostSteps = new string[]
            {
                "1. HOST: Launch Minecraft, start your world, and click 'Open to LAN' (or start your server.jar).",
                "2. HOST: Open MineTunnel -> 'Host Game' -> Click 'Start P2P Host' -> Click 'Copy Invite Code'.",
                "3. FRIEND: Open MineTunnel -> 'Join Game' -> Click 'Paste from Clipboard' -> Click 'Connect to Friend'.",
                "4. PLAY: In Minecraft, click 'Multiplayer' -> Connect to '127.0.0.1:25565' (or find it in LAN Games)!"
            };

            int yOffset = 42;
            for (int i = 0; i < hostSteps.Length; i++)
            {
                Label lbl = new Label
                {
                    Text = hostSteps[i],
                    Location = new Point(18, yOffset),
                    AutoSize = true,
                    ForeColor = i == 3 ? AccentGreen : TextLight,
                    Font = new Font("Segoe UI", 9.5f, i == 3 ? FontStyle.Bold : FontStyle.Regular)
                };
                cardSteps.Controls.Add(lbl);
                yOffset += 26;
            }

            Label lblAdvantage = new Label
            {
                Text = "✨ Why Pure P2P? Zero logins required. Direct UDP hole punching bypasses PLDT CGNAT with 10-25ms lowest ping.",
                Location = new Point(18, yOffset + 8),
                AutoSize = true,
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 9f, FontStyle.Italic)
            };
            cardSteps.Controls.Add(lblAdvantage);
            container.Controls.Add(cardSteps);

            // Card 2: FAQ & Troubleshooting
            Panel cardFaq = CreateCardPanel("Frequently Asked Questions & Troubleshooting", 16, 254, cardWidth, 270, "❓");

            string faqText =
                "Q: How do cracked or offline Minecraft accounts connect (TLauncher, Prism)?\r\n" +
                "A: In your Minecraft server's server.properties file, set: online-mode=false. This allows non-Microsoft accounts to join.\r\n\r\n" +
                "Q: Do I need to forward ports in my router or call PLDT to un-CGNAT?\r\n" +
                "A: No! MineTunnel uses RFC 5389 UDP STUN to punch a direct hole through PLDT's Carrier-Grade NAT automatically.\r\n\r\n" +
                "Q: What if hole punching takes longer or fails on strict cellular hotspots?\r\n" +
                "A: Some mobile data tethering connections use Symmetric NAT. If connection times out, simply stop and restart the P2P host,\r\n" +
                "   or use the optional Relay mode in the 'Relay & Advanced' tab.\r\n\r\n" +
                "Q: Can multiple friends join the same host?\r\n" +
                "A: Yes! MineTunnel supports multiple simultaneous players, each with isolated encryption keys and independent buffers.";

            TextBox txtFaq = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = faqText,
                BackColor = InputBg,
                ForeColor = TextLight,
                Font = new Font("Consolas", 9.5f),
                Location = new Point(16, 42),
                Size = new Size(cardFaq.ClientSize.Width - 32, cardFaq.ClientSize.Height - 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle
            };
            cardFaq.Controls.Add(txtFaq);
            container.Controls.Add(cardFaq);

            _refreshGuideLayout = delegate
            {
                int w = Math.Max(500, container.ClientSize.Width - 32);
                cardSteps.Width = w;
                cardFaq.Width = w;
            };
            container.Resize += delegate { _refreshGuideLayout(); };

            tab.Controls.Add(container);
        }

        // ====================================================================
        // TAB 4: RELAY & ADVANCED (Dedicated section for private VPS owners)
        // ====================================================================
        private void BuildRelayTab(TabPage tab)
        {
            tab.AutoScroll = true;
            Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };

            int cardWidth = Math.Max(500, container.ClientSize.Width - 32);

            // Card 1: Host via Custom Relay (VPS)
            Panel cardRelayHost = CreateCardPanel("Optional: Host via Custom Dedicated Relay (VPS)", 16, 12, cardWidth, 180, "🌐");

            Label lblRelayAddr = new Label { Text = "Relay Server IP:Port :", Location = new Point(18, 42), AutoSize = true, ForeColor = TextLight };
            _txtRelayServer = new TextBox { Text = "127.0.0.1:25560", Location = new Point(175, 39), Width = 180, BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            Label lblRPass = new Label { Text = "Password:", Location = new Point(370, 42), AutoSize = true, ForeColor = TextLight };
            _txtHostPassword = new TextBox { Text = "1234", Location = new Point(445, 39), Width = 110, BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            _btnRelayHostToggle = CreatePrimaryButton("▶ Start Hosting via Relay", AccentGreen, AccentGreenHover, 18, 76, 220, 34);
            _btnRelayHostToggle.Click += BtnRelayHostToggle_Click;

            _btnCopyRelayAddress = CreateSecondaryButton("📋 Copy Relay Details", 250, 76, 190, 34);
            _btnCopyRelayAddress.Click += BtnCopyRelayAddress_Click;

            Label lblRelayHint = new Label
            {
                Text = "For VPS owners running 'minetunnel_relay.py' or 'MineTunnelRelay.exe' on Oracle Cloud, AWS, or DigitalOcean.",
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(18, 126),
                AutoSize = true
            };

            cardRelayHost.Controls.Add(lblRelayAddr);
            cardRelayHost.Controls.Add(_txtRelayServer);
            cardRelayHost.Controls.Add(lblRPass);
            cardRelayHost.Controls.Add(_txtHostPassword);
            cardRelayHost.Controls.Add(_btnRelayHostToggle);
            cardRelayHost.Controls.Add(_btnCopyRelayAddress);
            cardRelayHost.Controls.Add(lblRelayHint);
            container.Controls.Add(cardRelayHost);

            // Card 2: Join via Custom Relay
            Panel cardRelayJoin = CreateCardPanel("Optional: Join Game via Relay Server", 16, 204, cardWidth, 230, "🎮");

            Label lblAddr = new Label { Text = "Relay Server IP:Port :", Location = new Point(18, 42), AutoSize = true, ForeColor = TextLight };
            _txtJoinAddress = new TextBox { Text = "127.0.0.1:25560", Location = new Point(175, 39), Size = new Size(260, 24), BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            Label lblJRoom = new Label { Text = "Room Name:", Location = new Point(18, 74), AutoSize = true, ForeColor = TextLight };
            _txtJoinRoom = new TextBox { Text = "SteveWorld", Location = new Point(175, 71), Size = new Size(260, 24), BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            Label lblJPass = new Label { Text = "Room Password:", Location = new Point(18, 106), AutoSize = true, ForeColor = TextLight };
            _txtJoinPassword = new TextBox { Text = "1234", Location = new Point(175, 103), Size = new Size(260, 24), BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            Label lblPlayer = new Label { Text = "Your Player Name:", Location = new Point(18, 138), AutoSize = true, ForeColor = TextLight };
            _txtPlayerName = new TextBox { Text = Environment.UserName, Location = new Point(175, 135), Size = new Size(260, 24), BackColor = InputBg, ForeColor = TextLight, BorderStyle = BorderStyle.FixedSingle };

            _btnJoinRelayToggle = CreateSecondaryButton("🎮 Connect to Relay", 175, 172, 180, 34);
            _btnJoinRelayToggle.Click += BtnJoinRelayToggle_Click;

            cardRelayJoin.Controls.Add(lblAddr);
            cardRelayJoin.Controls.Add(_txtJoinAddress);
            cardRelayJoin.Controls.Add(lblJRoom);
            cardRelayJoin.Controls.Add(_txtJoinRoom);
            cardRelayJoin.Controls.Add(lblJPass);
            cardRelayJoin.Controls.Add(_txtJoinPassword);
            cardRelayJoin.Controls.Add(lblPlayer);
            cardRelayJoin.Controls.Add(_txtPlayerName);
            cardRelayJoin.Controls.Add(_btnJoinRelayToggle);
            container.Controls.Add(cardRelayJoin);

            _refreshRelayLayout = delegate
            {
                int w = Math.Max(500, container.ClientSize.Width - 32);
                cardRelayHost.Width = w;
                cardRelayJoin.Width = w;
            };
            container.Resize += delegate { _refreshRelayLayout(); };

            tab.Controls.Add(container);
        }

        // ====================================================================
        // Custom UI Helper Methods (Matching Artifact Card Design)
        // ====================================================================
        private Panel CreateCardPanel(string headerText, int x, int y, int width, int height, string icon)
        {
            Panel p = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = CardBg,
                BorderStyle = BorderStyle.None
            };

            p.Paint += delegate(object s, PaintEventArgs e)
            {
                // Draw 1px clean modern border
                using (Pen borderPen = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, p.Width - 1, p.Height - 1);
                }

                // Header surface fill
                using (SolidBrush headerBrush = new SolidBrush(CardHeaderBg))
                {
                    e.Graphics.FillRectangle(headerBrush, 1, 1, p.Width - 2, 30);
                }

                // Header separator line
                using (Pen linePen = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawLine(linePen, 0, 31, p.Width, 31);
                }
            };

            Label lbl = new Label
            {
                Text = string.IsNullOrEmpty(icon) ? headerText.ToUpper() : (icon + "  " + headerText.ToUpper()),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = TextMuted,
                Location = new Point(14, 8),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            p.Controls.Add(lbl);
            return p;
        }

        private Button CreatePrimaryButton(string text, Color baseColor, Color hoverColor, int x, int y, int width, int height)
        {
            Button btn = new Button
            {
                Text = text,
                BackColor = baseColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(x, y),
                Size = new Size(width, height),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = hoverColor;
            return btn;
        }

        private Button CreateSecondaryButton(string text, int x, int y, int width, int height)
        {
            Button btn = new Button
            {
                Text = text,
                BackColor = CardHeaderBg,
                ForeColor = TextLight,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location = new Point(x, y),
                Size = new Size(width, height),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BorderColor;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 64);
            return btn;
        }

        // ====================================================================
        // Core Networking & Detection Wiring
        // ====================================================================
        private void InitializeCoreServices()
        {
            _lanBridge = new LanMulticastBridge();
            _lanBridge.StartListening();

            _detector = new MinecraftDetector(_lanBridge);
            _detector.OnServerStatusUpdated += Detector_OnServerStatusUpdated;
            _detector.Start();

            _p2pPuncher = new P2PHolePuncher();
            _p2pPuncher.OnLog += delegate(string line) { SetStatus("P2P: " + line); };
            _p2pPuncher.OnPeerConnected += delegate(IPEndPoint peer, string peerName, long ping)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    string pName = string.IsNullOrEmpty(peerName) ? "Player" : peerName;
                    AddOrUpdatePeerInList(peer.ToString(), pName, peer.ToString(), "🟢 Connected", ping + " ms");
                    UpdateHostStatusBadge();

                    _lblJoinP2PStatus.Text = string.Format("🟢 Connected to {0}", pName);
                    _lblJoinP2PStatus.ForeColor = AccentGreen;
                    _lblJoinP2PPing.Text = string.Format("Ping: {0} ms", ping);
                    _lblJoinP2PPing.ForeColor = AccentGreen;
                });
            };

            _p2pPuncher.OnPeerPingUpdated += delegate(IPEndPoint peer, long ping)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    UpdatePeerPingInList(peer.ToString(), ping);
                });
            };

            _p2pPuncher.OnPingUpdated += delegate(long ping)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    Color c = ping < 40 ? AccentGreen : (ping < 100 ? AccentGold : AccentRed);
                    _lblJoinP2PPing.Text = string.Format("Ping: {0} ms", ping);
                    _lblJoinP2PPing.ForeColor = c;
                });
            };

            _p2pPuncher.OnPeerDisconnected += delegate(IPEndPoint peer, string peerName)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    RemovePeerFromList(peer.ToString());
                    UpdateHostStatusBadge();
                });
            };

            _p2pPuncher.OnDisconnected += delegate
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    _lblP2PHostStatus.Text = "● Ready to host";
                    _lblP2PHostStatus.ForeColor = Color.Gray;
                    _lblJoinP2PStatus.Text = "● Disconnected";
                    _lblJoinP2PStatus.ForeColor = Color.Gray;
                    _lblJoinP2PPing.Text = "Ping: -- ms";
                    _lstPeers.Items.Clear();
                    UpdateHostStatusBadge();
                });
            };

            _p2pBridge = new P2PStreamBridge(_p2pPuncher, _lanBridge);
            _p2pBridge.OnLog += delegate(string line) { SetStatus("P2P Bridge: " + line); };
            _p2pBridge.OnLocalProxyBound += delegate(int port)
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    _lblJoinP2PHelp.Text = string.Format("🎮 Server Address: 127.0.0.1:{0}", port);
                    _btnCopyProxyAddress.Text = string.Format("📋 Copy Address (127.0.0.1:{0})", port);
                });
            };

            _relayHost = new RelayHostConnector(_lanBridge);
            _relayHost.OnLog += delegate(string line) { SetStatus("Relay: " + line); };

            _relayClient = new TunnelClient(_lanBridge);
            _relayClient.OnLog += delegate(string line) { SetStatus("Relay Client: " + line); };

            // Fetch local IP in background
            ThreadPool.QueueUserWorkItem(delegate
            {
                string localIp = UpnpHelper.GetLocalIpAddress();
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    _lblNetworkIp.Text = string.Format("Local IP: {0}", localIp);
                });
            });
        }

        private void AddOrUpdatePeerInList(string key, string name, string endpoint, string status, string ping)
        {
            foreach (ListViewItem itm in _lstPeers.Items)
            {
                if (itm.Tag as string == key)
                {
                    itm.SubItems[0].Text = name;
                    itm.SubItems[1].Text = endpoint;
                    itm.SubItems[2].Text = status;
                    itm.SubItems[3].Text = ping;
                    return;
                }
            }

            ListViewItem newItem = new ListViewItem(name);
            newItem.Tag = key;
            newItem.SubItems.Add(endpoint);
            newItem.SubItems.Add(status);
            newItem.SubItems.Add(ping);
            _lstPeers.Items.Add(newItem);
        }

        private void UpdatePeerPingInList(string key, long ping)
        {
            foreach (ListViewItem itm in _lstPeers.Items)
            {
                if (itm.Tag as string == key)
                {
                    itm.SubItems[3].Text = ping + " ms";
                    break;
                }
            }
        }

        private void RemovePeerFromList(string key)
        {
            for (int i = _lstPeers.Items.Count - 1; i >= 0; i--)
            {
                if (_lstPeers.Items[i].Tag as string == key)
                {
                    _lstPeers.Items.RemoveAt(i);
                    break;
                }
            }
        }

        private void UpdateHostStatusBadge()
        {
            int count = _lstPeers.Items.Count;
            if (_lblPeersCountBadge != null)
            {
                _lblPeersCountBadge.Text = string.Format("● {0} Player(s)", count);
                _lblPeersCountBadge.ForeColor = count > 0 ? AccentGreen : TextMuted;
            }

            if (_p2pPuncher.IsConnected || _p2pBridge.IsRunning)
            {
                if (count > 0)
                {
                    _lblP2PHostStatus.Text = string.Format("🟢 Hosting: {0} Player(s) Connected", count);
                    _lblP2PHostStatus.ForeColor = AccentGreen;
                }
                else
                {
                    _lblP2PHostStatus.Text = "● Listening for incoming peer punch...";
                    _lblP2PHostStatus.ForeColor = AccentGold;
                }
            }
        }

        private void LoadConfig()
        {
            ConfigManager cfg = ConfigManager.Instance;
            if (_txtHostRoom != null) _txtHostRoom.Text = cfg.RoomName;
            if (_txtJoinP2PCode != null) _txtJoinP2PCode.Text = cfg.P2PLastPeerCode;
            if (_txtRelayServer != null) _txtRelayServer.Text = cfg.CustomRelayAddress;
            if (_txtHostPassword != null) _txtHostPassword.Text = cfg.RoomPassword;
            if (_txtJoinAddress != null) _txtJoinAddress.Text = cfg.JoinAddress;
            if (_txtJoinRoom != null) _txtJoinRoom.Text = cfg.JoinRoom;
            if (_txtJoinPassword != null) _txtJoinPassword.Text = cfg.JoinPassword;
            if (_txtPlayerName != null) _txtPlayerName.Text = cfg.PlayerName;
            if (_txtJoinPlayerName != null) _txtJoinPlayerName.Text = cfg.PlayerName;
        }

        private void SaveConfig()
        {
            ConfigManager cfg = ConfigManager.Instance;
            if (_txtHostRoom != null) cfg.RoomName = _txtHostRoom.Text.Trim();
            if (_txtJoinP2PCode != null) cfg.P2PLastPeerCode = _txtJoinP2PCode.Text.Trim();
            if (_txtRelayServer != null) cfg.CustomRelayAddress = _txtRelayServer.Text.Trim();
            if (_txtHostPassword != null) cfg.RoomPassword = _txtHostPassword.Text;
            if (_txtJoinAddress != null) cfg.JoinAddress = _txtJoinAddress.Text.Trim();
            if (_txtJoinRoom != null) cfg.JoinRoom = _txtJoinRoom.Text.Trim();
            if (_txtJoinPassword != null) cfg.JoinPassword = _txtJoinPassword.Text;
            if (_txtPlayerName != null) cfg.PlayerName = _txtPlayerName.Text.Trim();
            if (_txtJoinPlayerName != null && !string.IsNullOrEmpty(_txtJoinPlayerName.Text)) cfg.PlayerName = _txtJoinPlayerName.Text.Trim();

            cfg.Save();
        }

        // ====================================================================
        // Host Action Handlers
        // ====================================================================
        private void BtnP2PHostToggle_Click(object sender, EventArgs e)
        {
            if (_p2pPuncher.IsConnected || _p2pBridge.IsRunning)
            {
                _p2pBridge.Stop();
                _p2pPuncher.Stop();

                _btnP2PHostToggle.Text = "▶ Start P2P Host";
                _btnP2PHostToggle.BackColor = AccentGreen;
                _lblP2PHostStatus.Text = "● Ready to host";
                _lblP2PHostStatus.ForeColor = Color.Gray;
                if (_txtP2PInviteCode != null)
                {
                    _txtP2PInviteCode.Text = "Click '▶ Start P2P Host' above to resolve your STUN endpoint and generate invite code";
                    _txtP2PInviteCode.ForeColor = TextMuted;
                }
                _lstPeers.Items.Clear();
                UpdateHostStatusBadge();
            }
            else
            {
                _btnP2PHostToggle.Enabled = false;
                _btnP2PHostToggle.Text = "Resolving STUN...";

                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok = _p2pPuncher.InitializeSocket(0);
                    int mcPort = _detector.ActivePort > 0 ? _detector.ActivePort : 25565;

                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _btnP2PHostToggle.Enabled = true;
                        if (ok)
                        {
                            string code = _p2pPuncher.GenerateInviteCode(_txtHostRoom.Text);
                            if (_txtP2PInviteCode != null)
                            {
                                _txtP2PInviteCode.Text = code;
                                _txtP2PInviteCode.ForeColor = AccentGreen;
                            }
                            _lblP2PHostStatus.Text = "● Listening for incoming peer punch...";
                            _lblP2PHostStatus.ForeColor = AccentGold;
                            _btnP2PHostToggle.Text = "■ Stop P2P Host";
                            _btnP2PHostToggle.BackColor = AccentRed;

                            _p2pPuncher.StartHostListening();
                            _p2pBridge.StartHostBridge(mcPort);
                            SetStatus(string.Format("P2P Host active on port {0}. Share your Invite Code with friends!", mcPort));
                        }
                        else
                        {
                            _btnP2PHostToggle.Text = "▶ Start P2P Host";
                            _btnP2PHostToggle.BackColor = AccentGreen;
                            MessageBox.Show("Could not resolve STUN endpoint. Check your internet connection or firewall UDP settings.", "STUN Traversal Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    });
                });
            }
        }

        private void BtnCopyP2PCode_Click(object sender, EventArgs e)
        {
            string code = _txtP2PInviteCode != null && !string.IsNullOrEmpty(_txtP2PInviteCode.Text) && _txtP2PInviteCode.Text.StartsWith("MINE-P2P#")
                ? _txtP2PInviteCode.Text
                : _p2pPuncher.GenerateInviteCode(_txtHostRoom.Text);

            if (!string.IsNullOrEmpty(code) && code.StartsWith("MINE-P2P#"))
            {
                try { Clipboard.SetText(code); } catch { }
                _btnCopyP2PCode.Text = "✓ Copied to Clipboard!";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(2000);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _btnCopyP2PCode.Text = "📋 Copy Invite Code";
                    });
                });
            }
            else
            {
                MessageBox.Show("Please click '▶ Start P2P Host' first to generate your P2P Invite Code.", "MineTunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnCopyProxyAddress_Click(object sender, EventArgs e)
        {
            int p = _p2pBridge != null && _p2pBridge.LocalProxyPort > 0 ? _p2pBridge.LocalProxyPort : 25565;
            string addr = "127.0.0.1:" + p;
            try { Clipboard.SetText(addr); } catch { }
            _btnCopyProxyAddress.Text = "✓ Copied (" + addr + ")!";
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(2000);
                if (this.IsDisposed || !this.IsHandleCreated) return;
                this.BeginInvoke((MethodInvoker)delegate
                {
                    _btnCopyProxyAddress.Text = "📋 Copy Address (" + addr + ")";
                });
            });
        }

        // ====================================================================
        // Join Action Handlers
        // ====================================================================
        private void BtnPasteJoinCode_Click(object sender, EventArgs e)
        {
            try
            {
                string clip = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clip))
                {
                    clip = clip.Trim();
                    _txtJoinP2PCode.Text = clip;
                    _btnPasteJoinCode.Text = "✓ Pasted!";
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(1500);
                        if (this.IsDisposed || !this.IsHandleCreated) return;
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            _btnPasteJoinCode.Text = "📋 Paste from Clipboard";
                        });
                    });
                }
                else
                {
                    MessageBox.Show("Clipboard is empty. Copy the P2P Invite Code from your friend first.", "MineTunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
        }

        private void BtnJoinP2PToggle_Click(object sender, EventArgs e)
        {
            if (_p2pPuncher.IsConnected || _p2pBridge.IsRunning)
            {
                _p2pBridge.Stop();
                _p2pPuncher.Stop();

                _btnJoinP2PToggle.Text = "⚡ Connect to Friend";
                _btnJoinP2PToggle.BackColor = AccentGreen;
                _lblJoinP2PStatus.Text = "● Disconnected";
                _lblJoinP2PStatus.ForeColor = Color.Gray;
                _lblJoinP2PPing.Text = "Ping: -- ms";
                _txtJoinP2PCode.Enabled = true;
                if (_txtJoinPlayerName != null) _txtJoinPlayerName.Enabled = true;
            }
            else
            {
                string rawCode = _txtJoinP2PCode.Text.Trim();
                IPEndPoint peerEndpoint;
                string room;
                byte[] sessionId;
                byte[] sharedSecret;

                if (!P2PHolePuncher.TryParseInviteCode(rawCode, out peerEndpoint, out room, out sessionId, out sharedSecret))
                {
                    MessageBox.Show(
                        "Invalid P2P Invite Code format.\r\n\r\n" +
                        "Ask your friend to click '📋 Copy Invite Code' in MineTunnel and paste it here.\r\n" +
                        "(Expected format: MINE-P2P#IP:Port#RoomName#AuthToken)",
                        "Invalid Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_txtJoinPlayerName != null && !string.IsNullOrEmpty(_txtJoinPlayerName.Text))
                {
                    ConfigManager.Instance.PlayerName = _txtJoinPlayerName.Text.Trim();
                }
                SaveConfig();

                string playerName = _txtJoinPlayerName != null && !string.IsNullOrEmpty(_txtJoinPlayerName.Text)
                    ? _txtJoinPlayerName.Text.Trim()
                    : Environment.UserName;

                _btnJoinP2PToggle.Text = "Punching Pinhole...";
                _btnJoinP2PToggle.Enabled = false;
                _txtJoinP2PCode.Enabled = false;
                if (_txtJoinPlayerName != null) _txtJoinPlayerName.Enabled = false;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    _p2pPuncher.InitializeSocket(0);
                    _p2pBridge.StartClientProxy(25565);
                    _p2pPuncher.StartHolePunching(peerEndpoint, sessionId, sharedSecret, playerName);

                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _btnJoinP2PToggle.Enabled = true;
                        _btnJoinP2PToggle.Text = "■ Disconnect P2P";
                        _btnJoinP2PToggle.BackColor = AccentRed;
                        _lblJoinP2PStatus.Text = "● Punching P2P Hole...";
                        _lblJoinP2PStatus.ForeColor = AccentGold;
                    });
                });
            }
        }

        // ====================================================================
        // Custom Relay Action Handlers
        // ====================================================================
        private void BtnRelayHostToggle_Click(object sender, EventArgs e)
        {
            if (_relayHost != null && _relayHost.IsConnected)
            {
                _relayHost.Disconnect();
                _btnRelayHostToggle.Text = "▶ Start Hosting via Relay";
                _btnRelayHostToggle.BackColor = AccentGreen;
                SetStatus("Relay host stopped.");
            }
            else
            {
                string rawRelay = _txtRelayServer.Text.Trim();
                string rHost = "127.0.0.1";
                int rPort = 25560;
                if (rawRelay.Contains(":"))
                {
                    string[] parts = rawRelay.Split(':');
                    rHost = parts[0];
                    int.TryParse(parts[1], out rPort);
                }

                string room = _txtHostRoom != null ? _txtHostRoom.Text.Trim() : "SteveWorld";
                string pass = _txtHostPassword.Text;
                int mcPort = _detector.ActivePort > 0 ? _detector.ActivePort : 25565;

                _btnRelayHostToggle.Text = "Connecting...";
                _btnRelayHostToggle.Enabled = false;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok = _relayHost.ConnectAndRegister(rHost, rPort, room, pass, mcPort);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _btnRelayHostToggle.Enabled = true;
                        if (ok)
                        {
                            _btnRelayHostToggle.Text = "■ Stop Hosting";
                            _btnRelayHostToggle.BackColor = AccentRed;
                            SetStatus("Hosting via Relay active!");
                        }
                        else
                        {
                            _btnRelayHostToggle.Text = "▶ Start Hosting via Relay";
                            _btnRelayHostToggle.BackColor = AccentGreen;
                            MessageBox.Show("Failed to connect to relay server.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    });
                });
            }
        }

        private void BtnCopyRelayAddress_Click(object sender, EventArgs e)
        {
            string roomName = _txtHostRoom != null ? _txtHostRoom.Text : "SteveWorld";
            string shareText = string.Format("Relay: {0} | Room: {1} | Pass: {2}", _txtRelayServer.Text, roomName, _txtHostPassword.Text);
            try { Clipboard.SetText(shareText); } catch { }
            MessageBox.Show("Copied relay details to clipboard:\r\n" + shareText, "Custom Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnJoinRelayToggle_Click(object sender, EventArgs e)
        {
            if (_relayClient.IsConnected)
            {
                _relayClient.Disconnect();
                _btnJoinRelayToggle.Text = "🎮 Connect to Relay";
                _btnJoinRelayToggle.BackColor = CardHeaderBg;
            }
            else
            {
                string raw = _txtJoinAddress.Text.Trim();
                string host = "127.0.0.1";
                int port = 25560;
                if (raw.Contains(":"))
                {
                    string[] parts = raw.Split(':');
                    host = parts[0];
                    int.TryParse(parts[1], out port);
                }
                string room = _txtJoinRoom.Text.Trim();
                string pass = _txtJoinPassword.Text;
                string player = _txtPlayerName.Text.Trim();

                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok = _relayClient.Connect(host, port, room, pass, player);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (ok)
                        {
                            _btnJoinRelayToggle.Text = "Disconnect Relay";
                            _btnJoinRelayToggle.BackColor = AccentRed;
                        }
                        else
                        {
                            MessageBox.Show("Failed to connect to relay room.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    });
                });
            }
        }

        // ====================================================================
        // Minecraft Status & Notification Callbacks
        // ====================================================================
        private void Detector_OnServerStatusUpdated(MinecraftServerInfo info, int port)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke((MethodInvoker)delegate
            {
                if (info.IsOnline)
                {
                    if (_p2pBridge != null && _p2pBridge.IsRunning && port > 0)
                    {
                        _p2pBridge.UpdateTargetServerPort(port);
                    }

                    string modStr = info.IsModded ? (info.ModCount > 0 ? string.Format(" [{0} Mods]", info.ModCount) : " [Modded]") : "";
                    _lblMcStatus.Text = string.Format("● {0} ({1}){2} Online on Port {3}",
                        info.ServerType, info.VersionName, modStr, port);
                    _lblMcStatus.ForeColor = AccentGreen;

                    string motdClean = !string.IsNullOrEmpty(info.Motd) ? info.Motd : "Minecraft Java World";
                    _lblMcDetails.Text = string.Format("Players: {0}/{1} | Ping: {2}ms | MOTD: \"{3}\"",
                        info.CurrentPlayers, info.MaxPlayers, info.PingMs, motdClean);

                    if (_lblHeaderMcPill != null)
                    {
                        _lblHeaderMcPill.Text = string.Format("🟢 Java Online (:{0})", port);
                        _lblHeaderMcPill.ForeColor = AccentGreen;
                    }
                }
                else
                {
                    _lblMcStatus.Text = "● Scanning for Java Minecraft (Paper, Fabric, Forge, Vanilla, LAN)...";
                    _lblMcStatus.ForeColor = TextMuted;
                    _lblMcDetails.Text = "Tip: Start Minecraft and click 'Open to LAN' in singleplayer, or run your server.jar on port 25565.";

                    if (_lblHeaderMcPill != null)
                    {
                        _lblHeaderMcPill.Text = "⚪ Minecraft Waiting";
                        _lblHeaderMcPill.ForeColor = TextMuted;
                    }
                }
            });
        }

        private void SetStatus(string msg)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            this.BeginInvoke((MethodInvoker)delegate
            {
                _lblStatusMsg.Text = msg;
            });
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_refreshHostLayout != null) _refreshHostLayout();
            if (_refreshJoinLayout != null) _refreshJoinLayout();
            if (_refreshGuideLayout != null) _refreshGuideLayout();
            if (_refreshRelayLayout != null) _refreshRelayLayout();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();

            base.OnFormClosing(e);

            if (_p2pBridge != null)
            {
                _p2pBridge.Stop();
            }

            if (_p2pPuncher != null)
            {
                _p2pPuncher.Stop();
            }

            if (_relayHost != null && _relayHost.IsConnected)
            {
                _relayHost.Disconnect();
            }

            if (_relayClient != null)
            {
                _relayClient.Disconnect();
            }

            if (_detector != null)
            {
                _detector.Stop();
            }

            if (_lanBridge != null)
            {
                _lanBridge.Stop();
            }
        }
    }
}
