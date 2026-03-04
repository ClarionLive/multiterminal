using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    public class AboutDialog : Form
    {
        private readonly TerminalTheme _theme;

        public AboutDialog(TerminalTheme theme)
        {
            _theme = theme ?? TerminalTheme.Dark;
            InitializeComponent();
            ApplyTheme();
        }

        private void InitializeComponent()
        {
            Text = "About MultiTerminal";
            Size = new Size(460, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10f);

            int leftMargin = 25;
            int contentWidth = ClientSize.Width - leftMargin * 2;
            int yPos = 20;

            // App name
            var nameLabel = new Label
            {
                Text = "MultiTerminal",
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            Controls.Add(nameLabel);
            yPos += nameLabel.PreferredHeight + 4;

            // Description
            var descLabel = new Label
            {
                Text = "Multi-agent coordination system for Claude Code",
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            Controls.Add(descLabel);
            yPos += descLabel.PreferredHeight + 16;

            // Version
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionLabel = new Label
            {
                Text = $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            Controls.Add(versionLabel);
            yPos += versionLabel.PreferredHeight + 4;

            // Copyright
            var copyrightLabel = new Label
            {
                Text = "Copyright \u00A9 2025 John Hickey",
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            Controls.Add(copyrightLabel);
            yPos += copyrightLabel.PreferredHeight + 4;

            // License
            var licenseLink = new LinkLabel
            {
                Text = "MIT License",
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            licenseLink.LinkClicked += (s, e) => ShowLicenseText();
            Controls.Add(licenseLink);
            yPos += licenseLink.PreferredHeight + 4;

            // GitHub link
            var githubLink = new LinkLabel
            {
                Text = "github.com/peterparker57",
                Location = new Point(leftMargin, yPos),
                AutoSize = true
            };
            githubLink.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/peterparker57") { UseShellExecute = true }); }
                catch { }
            };
            Controls.Add(githubLink);
            yPos += githubLink.PreferredHeight + 20;

            // System Info group
            var sysGroup = new GroupBox
            {
                Text = "System Information",
                Location = new Point(leftMargin, yPos),
                Size = new Size(contentWidth, 105)
            };

            var sysInfo = new Label
            {
                Text = GetSystemInfo(),
                Location = new Point(10, 22),
                Size = new Size(contentWidth - 20, 75),
                Font = new Font("Segoe UI", 9f)
            };
            sysGroup.Controls.Add(sysInfo);
            Controls.Add(sysGroup);
            yPos += sysGroup.Height + 15;

            // Acknowledgments group
            var ackGroup = new GroupBox
            {
                Text = "Third-Party Acknowledgments",
                Location = new Point(leftMargin, yPos),
                Size = new Size(contentWidth, 120)
            };

            var ackText = new Label
            {
                Text = "DockPanelSuite 3.1.1 (MIT)\n" +
                       "Microsoft WebView2 (BSD-style)\n" +
                       "System.Data.SQLite (Public Domain)\n" +
                       "System.Text.Json (MIT)",
                Location = new Point(10, 22),
                Size = new Size(contentWidth - 20, 90),
                Font = new Font("Segoe UI", 9f)
            };
            ackGroup.Controls.Add(ackText);
            Controls.Add(ackGroup);
            yPos += ackGroup.Height + 20;

            // Close button
            var closeBtn = new Button
            {
                Text = "Close",
                Size = new Size(90, 32),
                Location = new Point(ClientSize.Width - 90 - leftMargin, yPos),
                DialogResult = DialogResult.OK
            };
            Controls.Add(closeBtn);

            AcceptButton = closeBtn;
            CancelButton = closeBtn;
        }

        private string GetSystemInfo()
        {
            var dotnet = Environment.Version.ToString();
            var os = Environment.OSVersion.ToString();

            string webview2 = "Not installed";
            try
            {
                webview2 = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .GetAvailableBrowserVersionString();
            }
            catch { }

            return $".NET Runtime: {dotnet}\n" +
                   $"OS: {os}\n" +
                   $"WebView2: {webview2}";
        }

        private void ShowLicenseText()
        {
            string licenseText =
                "MIT License\n\n" +
                "Copyright (c) 2025 John Hickey\n\n" +
                "Permission is hereby granted, free of charge, to any person obtaining a copy " +
                "of this software and associated documentation files (the \"Software\"), to deal " +
                "in the Software without restriction, including without limitation the rights " +
                "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell " +
                "copies of the Software, and to permit persons to whom the Software is " +
                "furnished to do so, subject to the following conditions:\n\n" +
                "The above copyright notice and this permission notice shall be included in all " +
                "copies or substantial portions of the Software.\n\n" +
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR " +
                "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, " +
                "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
                "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER " +
                "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, " +
                "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE " +
                "SOFTWARE.";

            MessageBox.Show(this, licenseText, "MIT License", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ApplyTheme()
        {
            bool isDark = _theme.IsDark;

            BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
            ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

            foreach (Control control in Controls)
            {
                ApplyThemeToControl(control, isDark);
            }
        }

        private void ApplyThemeToControl(Control control, bool isDark)
        {
            if (control is GroupBox groupBox)
            {
                groupBox.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            }
            else if (control is Button button)
            {
                button.BackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(225, 225, 225);
                button.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = isDark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 180, 180);
            }
            else if (control is LinkLabel link)
            {
                link.LinkColor = isDark ? Color.FromArgb(100, 180, 255) : Color.FromArgb(0, 102, 204);
                link.ActiveLinkColor = isDark ? Color.FromArgb(140, 200, 255) : Color.FromArgb(0, 80, 160);
            }
            else if (control is Label label)
            {
                label.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            }

            foreach (Control child in control.Controls)
            {
                ApplyThemeToControl(child, isDark);
            }
        }
    }
}
