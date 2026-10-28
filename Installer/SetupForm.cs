using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MineTunnel.Installer
{
    public class SetupForm : Form
    {
        private readonly Color BgDark = Color.FromArgb(24, 24, 27);
        private readonly Color CardBg = Color.FromArgb(39, 39, 42);
        private readonly Color BorderColor = Color.FromArgb(63, 63, 70);
        private readonly Color TextLight = Color.FromArgb(244, 244, 245);
        private readonly Color TextMuted = Color.FromArgb(161, 161, 170);
        private readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
        private readonly Color AccentGreenHover = Color.FromArgb(5, 150, 105);

        private TextBox _txtInstallDir;
        private Button _btnBrowse;
        private CheckBox _chkDesktopShortcut;
        private CheckBox _chkStartMenuShortcut;
        private CheckBox _chkLaunchApp;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private Button _btnInstall;
        private Button _btnCancel;

        public SetupForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.Text = "MineTunnel Setup - Minecraft Server Tunnel";
            this.Size = new Size(620, 480);
            this.MinimumSize = new Size(540, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.BackColor = BgDark;
            this.ForeColor = TextLight;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Banner
            Panel banner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = CardBg,
                Padding = new Padding(20, 12, 20, 12)
            };

            Label lblTitle = new Label
            {
                Text = "⛏ MineTunnel Setup",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = AccentGreen,
                AutoSize = true,
                Location = new Point(18, 12)
            };

            Label lblSubtitle = new Label
            {
                Text = "Install MineTunnel for Windows 10 & 11 (Minecraft LAN & Server Tunnel)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(20, 42)
            };

            banner.Controls.Add(lblTitle);
            banner.Controls.Add(lblSubtitle);
            this.Controls.Add(banner);

            // Bottom Buttons Panel
            Panel bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = CardBg,
                Padding = new Padding(20, 10, 20, 10)
            };

            _btnCancel = new Button
            {
                Text = "Cancel",
                BackColor = BgDark,
                ForeColor = TextLight,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(bottom.ClientSize.Width - 110, 13),
                Size = new Size(95, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderColor = BorderColor;
            _btnCancel.Click += (s, e) => this.Close();

            _btnInstall = new Button
            {
                Text = "✔ Install",
                BackColor = AccentGreen,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Location = new Point(bottom.ClientSize.Width - 225, 13),
                Size = new Size(105, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Cursor = Cursors.Hand
            };
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Click += BtnInstall_Click;

            bottom.Controls.Add(_btnInstall);
            bottom.Controls.Add(_btnCancel);
            this.Controls.Add(bottom);

            // Main Content Area
            Panel content = new Panel
            {
                Location = new Point(20, 85),
                Size = new Size(this.ClientSize.Width - 40, this.ClientSize.Height - 85 - 75),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = CardBg,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(16)
            };

            Label lblDest = new Label
            {
                Text = "Destination Folder:",
                Location = new Point(14, 16),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = TextLight
            };

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "MineTunnel");

            _txtInstallDir = new TextBox
            {
                Text = defaultDir,
                Location = new Point(16, 42),
                Size = new Size(content.ClientSize.Width - 130, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = BgDark,
                ForeColor = TextLight,
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(content.ClientSize.Width - 105, 40),
                Size = new Size(90, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = CardBg,
                ForeColor = TextLight,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnBrowse.FlatAppearance.BorderColor = BorderColor;
            _btnBrowse.Click += (s, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.SelectedPath = _txtInstallDir.Text;
                    fbd.Description = "Select installation directory for MineTunnel:";
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _txtInstallDir.Text = fbd.SelectedPath;
                    }
                }
            };

            _chkDesktopShortcut = new CheckBox
            {
                Text = "Create Desktop Shortcut",
                Checked = true,
                Location = new Point(16, 85),
                AutoSize = true,
                ForeColor = TextLight
            };

            _chkStartMenuShortcut = new CheckBox
            {
                Text = "Create Start Menu Shortcut",
                Checked = true,
                Location = new Point(16, 115),
                AutoSize = true,
                ForeColor = TextLight
            };

            _chkLaunchApp = new CheckBox
            {
                Text = "Launch MineTunnel after setup completes",
                Checked = true,
                Location = new Point(16, 145),
                AutoSize = true,
                ForeColor = TextLight
            };

            _progressBar = new ProgressBar
            {
                Location = new Point(16, 185),
                Size = new Size(content.ClientSize.Width - 32, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            _lblStatus = new Label
            {
                Text = "Ready to install. Click 'Install' to begin.",
                Location = new Point(16, 212),
                AutoSize = true,
                ForeColor = TextMuted
            };

            content.Controls.Add(lblDest);
            content.Controls.Add(_txtInstallDir);
            content.Controls.Add(_btnBrowse);
            content.Controls.Add(_chkDesktopShortcut);
            content.Controls.Add(_chkStartMenuShortcut);
            content.Controls.Add(_chkLaunchApp);
            content.Controls.Add(_progressBar);
            content.Controls.Add(_lblStatus);
            this.Controls.Add(content);
        }

        private void BtnInstall_Click(object sender, EventArgs e)
        {
            string installDir = _txtInstallDir.Text.Trim();
            if (string.IsNullOrEmpty(installDir))
            {
                MessageBox.Show("Please choose a valid destination directory.", "MineTunnel Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnInstall.Enabled = false;
            _btnBrowse.Enabled = false;
            _txtInstallDir.Enabled = false;
            _chkDesktopShortcut.Enabled = false;
            _chkStartMenuShortcut.Enabled = false;
            _chkLaunchApp.Enabled = false;

            _progressBar.Visible = true;
            _progressBar.Value = 10;
            _lblStatus.Text = "Preparing installation...";

            bool createDesktop = _chkDesktopShortcut.Checked;
            bool createStartMenu = _chkStartMenuShortcut.Checked;
            bool launchNow = _chkLaunchApp.Checked;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    PerformInstallation(installDir, createDesktop, createStartMenu, launchNow);
                }
                catch (Exception ex)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _progressBar.Visible = false;
                        _lblStatus.Text = "Error during installation: " + ex.Message;
                        _lblStatus.ForeColor = Color.Red;
                        _btnInstall.Enabled = true;
                        MessageBox.Show("Installation failed:\n" + ex.Message, "MineTunnel Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void PerformInstallation(string installDir, bool createDesktop, bool createStartMenu, bool launchNow)
        {
            UpdateProgress(25, "Creating destination directory...");
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            // 1. Extract MineTunnel.exe
            UpdateProgress(45, "Extracting MineTunnel.exe...");
            string targetExe = Path.Combine(installDir, "MineTunnel.exe");
            ExtractEmbeddedResource("MineTunnel.exe", targetExe);

            // 2. Extract README.md, INSTRUCTIONS.md, MineTunnelRelay.exe, minetunnel_relay.py, and install_oracle.sh
            ExtractEmbeddedResource("MineTunnelRelay.exe", Path.Combine(installDir, "MineTunnelRelay.exe"));
            ExtractEmbeddedResource("minetunnel_relay.py", Path.Combine(installDir, "minetunnel_relay.py"));
            ExtractEmbeddedResource("install_oracle.sh", Path.Combine(installDir, "install_oracle.sh"));
            ExtractEmbeddedResource("README.md", Path.Combine(installDir, "README.md"));
            ExtractEmbeddedResource("INSTRUCTIONS.md", Path.Combine(installDir, "INSTRUCTIONS.md"));

            // 3. Create Uninstaller executable
            UpdateProgress(65, "Configuring Uninstaller...");
            string uninstallerExe = Path.Combine(installDir, "uninstall.exe");
            string currentInstallerPath = Assembly.GetExecutingAssembly().Location;
            if (File.Exists(currentInstallerPath))
            {
                File.Copy(currentInstallerPath, uninstallerExe, true);
            }

            // 4. Create Shortcuts
            UpdateProgress(80, "Creating Windows shortcuts...");
            if (createDesktop)
            {
                string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopLnk = Path.Combine(desktopFolder, "MineTunnel.lnk");
                CreateShortcut(desktopLnk, targetExe, "MineTunnel - Minecraft Server Tunnel");
            }

            if (createStartMenu)
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", "MineTunnel");
                if (!Directory.Exists(startMenu)) Directory.CreateDirectory(startMenu);

                string startLnk = Path.Combine(startMenu, "MineTunnel.lnk");
                CreateShortcut(startLnk, targetExe, "MineTunnel - Minecraft Server Tunnel");

                string uninstallLnk = Path.Combine(startMenu, "Uninstall MineTunnel.lnk");
                CreateShortcut(uninstallLnk, uninstallerExe, "Uninstall MineTunnel", "/uninstall");
            }

            // 5. Register in Windows Registry (Add/Remove Programs)
            UpdateProgress(95, "Registering application with Windows...");
            RegisterUninstall(installDir, targetExe, uninstallerExe);

            UpdateProgress(100, "Installation complete!");

            this.BeginInvoke((MethodInvoker)delegate
            {
                _lblStatus.Text = "MineTunnel has been successfully installed!";
                _lblStatus.ForeColor = AccentGreen;
                _btnInstall.Text = "Finish";
                _btnInstall.Enabled = true;
                _btnCancel.Visible = false;

                _btnInstall.Click -= BtnInstall_Click;
                _btnInstall.Click += delegate
                {
                    if (launchNow && File.Exists(targetExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetExe,
                            WorkingDirectory = installDir
                        });
                    }
                    this.Close();
                };
            });
        }

        private void UpdateProgress(int percentage, string statusMessage)
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                _progressBar.Value = Math.Min(100, Math.Max(0, percentage));
                _lblStatus.Text = statusMessage;
            });
        }

        private void ExtractEmbeddedResource(string resourceName, string outputPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            // Look for matching resource ending with resourceName
            string foundResource = null;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    foundResource = name;
                    break;
                }
            }

            if (foundResource == null)
            {
                // Fallback: check if local file exists beside installer
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, resourceName);
                if (File.Exists(localPath))
                {
                    File.Copy(localPath, outputPath, true);
                    return;
                }
                return;
            }

            using (Stream input = asm.GetManifestResourceStream(foundResource))
            using (FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[16384];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }
        }

        private void CreateShortcut(string shortcutPath, string targetExePath, string description, string arguments = null)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null, shell,
                    new object[] { shortcutPath });

                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExePath });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetExePath) });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                if (!string.IsNullOrEmpty(arguments))
                {
                    scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                }
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Shortcut creation failed: " + ex.Message);
            }
        }

        private void RegisterUninstall(string installDir, string exePath, string uninstallerPath)
        {
            try
            {
                using (RegistryKey parent = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MineTunnel"))
                {
                    if (parent != null)
                    {
                        parent.SetValue("DisplayName", "MineTunnel (Minecraft LAN & Server Tunnel)");
                        parent.SetValue("DisplayVersion", "1.0.0");
                        parent.SetValue("Publisher", "MineTunnel Team");
                        parent.SetValue("DisplayIcon", exePath);
                        parent.SetValue("InstallLocation", installDir);
                        parent.SetValue("UninstallString", string.Format("\"{0}\" /uninstall", uninstallerPath));
                        parent.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        parent.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Registry registration failed: " + ex.Message);
            }
        }
    }
}
