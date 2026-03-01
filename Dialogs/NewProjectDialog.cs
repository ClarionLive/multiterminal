using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Lightweight WebView2 dialog for creating a new project.
    /// Collects project name, folder, and team lead, then returns results
    /// for the caller to persist and launch.
    /// </summary>
    public class NewProjectDialog : Form
    {
        private WebView2 _webView;
        private Panel _bottomPanel;
        private Button _createButton;
        private Button _cancelButton;

        private readonly TerminalTheme _theme;
        private readonly List<(string Id, string DisplayName, string AvatarUrl)> _teamLeadProfiles;
        private bool _isWebViewReady;

        /// <summary>Project name entered by the user.</summary>
        public string ProjectName { get; private set; }

        /// <summary>Absolute folder path selected by the user.</summary>
        public string ProjectFolder { get; private set; }

        /// <summary>Display name of the selected team lead, or null if none.</summary>
        public string SelectedTeamLead { get; private set; }

        public NewProjectDialog(
            TerminalTheme theme,
            List<(string Id, string DisplayName, string AvatarUrl)> teamLeadProfiles)
        {
            _theme = theme ?? TerminalTheme.Dark;
            _teamLeadProfiles = teamLeadProfiles ?? new List<(string, string, string)>();

            InitializeLayout();
            ApplyNativeTheme();
            _ = InitializeWebViewAsync();
        }

        private void InitializeLayout()
        {
            Text = "Create New Project";
            Size = new Size(520, 360);
            MinimumSize = new Size(420, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 10f);

            _bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };

            _createButton = new Button
            {
                Text = "Create && Launch",
                Size = new Size(130, 32),
                DialogResult = DialogResult.None
            };
            _createButton.Click += OnCreateClicked;

            _cancelButton = new Button
            {
                Text = "Cancel",
                Size = new Size(80, 32),
                DialogResult = DialogResult.Cancel
            };

            _bottomPanel.Controls.Add(_createButton);
            _bottomPanel.Controls.Add(_cancelButton);

            _webView = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_webView);
            Controls.Add(_bottomPanel);

            CancelButton = _cancelButton;
            AcceptButton = _createButton;

            _bottomPanel.Resize += (s, e) => LayoutBottomButtons();
            LayoutBottomButtons();
        }

        private void LayoutBottomButtons()
        {
            _cancelButton.Location = new Point(
                _bottomPanel.Width - _cancelButton.Width - 12,
                (_bottomPanel.Height - _cancelButton.Height) / 2);
            _createButton.Location = new Point(
                _cancelButton.Left - _createButton.Width - 8,
                (_bottomPanel.Height - _createButton.Height) / 2);
        }

        private void ApplyNativeTheme()
        {
            bool dark = _theme.IsDark;
            BackColor = dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            ForeColor = dark ? Color.White : Color.FromArgb(30, 30, 30);
            _bottomPanel.BackColor = dark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(228, 228, 228);
            ApplyButtonTheme(_createButton, dark, primary: true);
            ApplyButtonTheme(_cancelButton, dark, primary: false);
        }

        private static void ApplyButtonTheme(Button b, bool dark, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            if (primary)
            {
                b.BackColor = dark ? Color.FromArgb(0, 122, 204) : Color.FromArgb(0, 99, 177);
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = dark ? Color.FromArgb(0, 140, 230) : Color.FromArgb(0, 80, 150);
            }
            else
            {
                b.BackColor = dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(225, 225, 225);
                b.ForeColor = dark ? Color.White : Color.FromArgb(30, 30, 30);
                b.FlatAppearance.BorderColor = dark ? Color.FromArgb(86, 86, 86) : Color.FromArgb(173, 173, 173);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigateToString(BuildFormHtml());

                _isWebViewReady = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NewProjectDialog] WebView2 init failed: {ex.Message}");
                MessageBox.Show(
                    $"Failed to initialize dialog: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnWebMessageReceived(sender, e)));
                return;
            }

            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;

                switch (typeEl.GetString())
                {
                    case "browse_folder":
                        HandleBrowseFolder(root);
                        break;

                    case "submit":
                        HandleSubmit(root);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NewProjectDialog] WebMessage error: {ex.Message}");
            }
        }

        private void HandleBrowseFolder(JsonElement root)
        {
            string current = root.TryGetProperty("currentValue", out var cv) ? cv.GetString() : "";

            using var dlg = new FolderBrowserDialog
            {
                Description = "Select Project Folder",
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dlg.SelectedPath = current;

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var result = JsonSerializer.Serialize(new { type = "folder_selected", path = dlg.SelectedPath });
                _webView.CoreWebView2.PostWebMessageAsJson(result);
            }
        }

        private void HandleSubmit(JsonElement root)
        {
            string name = root.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : "";
            string folder = root.TryGetProperty("folder", out var f) ? f.GetString()?.Trim() : "";
            string lead = root.TryGetProperty("lead", out var l) ? l.GetString()?.Trim() : "";

            if (string.IsNullOrEmpty(name))
            {
                SendError("Project name is required.");
                return;
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                SendError("Please select a valid project folder.");
                return;
            }

            ProjectName = name;
            ProjectFolder = folder;
            SelectedTeamLead = string.IsNullOrEmpty(lead) ? null : lead;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SendError(string message)
        {
            var msg = JsonSerializer.Serialize(new { type = "error", message });
            _webView.CoreWebView2.PostWebMessageAsJson(msg);
        }

        private void OnCreateClicked(object sender, EventArgs e)
        {
            if (!_isWebViewReady) return;
            _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"collect_and_submit\"}");
        }

        private string BuildFormHtml()
        {
            bool dark = _theme.IsDark;

            string bgColor = dark ? "#1e1e1e" : "#f5f5f5";
            string textColor = dark ? "#e0e0e0" : "#1e1e1e";
            string labelColor = dark ? "#a0a0a0" : "#555555";
            string inputBg = dark ? "#2d2d30" : "#ffffff";
            string inputBorder = dark ? "#3e3e42" : "#cccccc";
            string inputFocus = dark ? "#007acc" : "#0063b1";
            string btnBrowseBg = dark ? "#3e3e42" : "#e0e0e0";
            string btnBrowseHover = dark ? "#505054" : "#d0d0d0";
            string errorColor = dark ? "#f14c4c" : "#d32f2f";

            var options = new StringBuilder();
            options.Append("<option value=\"\">(none)</option>");
            foreach (var (id, displayName, avatarUrl) in _teamLeadProfiles)
            {
                string safe = WebUtility.HtmlEncode(displayName ?? id);
                string val = WebUtility.HtmlEncode(displayName ?? id);
                options.Append($"<option value=\"{val}\">{safe}</option>");
            }

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""UTF-8"">
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{
    font-family: 'Segoe UI', sans-serif;
    background: {bgColor};
    color: {textColor};
    padding: 28px 24px 16px;
  }}
  h2 {{
    font-size: 16px;
    font-weight: 600;
    margin-bottom: 24px;
    color: {textColor};
  }}
  .field {{
    margin-bottom: 18px;
  }}
  label {{
    display: block;
    font-size: 12px;
    font-weight: 600;
    color: {labelColor};
    margin-bottom: 6px;
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }}
  input[type=text], select {{
    width: 100%;
    padding: 8px 10px;
    font-size: 13px;
    font-family: 'Segoe UI', sans-serif;
    border: 1px solid {inputBorder};
    border-radius: 4px;
    background: {inputBg};
    color: {textColor};
    outline: none;
    transition: border-color 0.15s;
  }}
  input[type=text]:focus, select:focus {{
    border-color: {inputFocus};
  }}
  .folder-row {{
    display: flex;
    gap: 6px;
  }}
  .folder-row input {{
    flex: 1;
  }}
  .browse-btn {{
    padding: 0 14px;
    font-size: 14px;
    font-weight: 600;
    border: 1px solid {inputBorder};
    border-radius: 4px;
    background: {btnBrowseBg};
    color: {textColor};
    cursor: pointer;
    white-space: nowrap;
    transition: background 0.15s;
  }}
  .browse-btn:hover {{
    background: {btnBrowseHover};
  }}
  .error-msg {{
    color: {errorColor};
    font-size: 12px;
    margin-top: 12px;
    display: none;
  }}
</style>
</head>
<body>
  <h2>New Project</h2>
  <div class=""field"">
    <label>Project Name</label>
    <input id=""name"" type=""text"" placeholder=""My Project"" autofocus>
  </div>
  <div class=""field"">
    <label>Project Folder</label>
    <div class=""folder-row"">
      <input id=""folder"" type=""text"" placeholder=""C:\Projects\..."">
      <button class=""browse-btn"" onclick=""browseFolder()"">...</button>
    </div>
  </div>
  <div class=""field"">
    <label>Team Lead</label>
    <select id=""teamLead"">
      {options}
    </select>
  </div>
  <div id=""error"" class=""error-msg""></div>

  <script>
    window.chrome.webview.addEventListener('message', function(event) {{
      var data = event.data;
      if (!data || typeof data !== 'object') return;
      if (data.type === 'folder_selected') {{
        document.getElementById('folder').value = data.path;
        var nameEl = document.getElementById('name');
        if (!nameEl.value && data.path) {{
          var parts = data.path.replace(/\\\\/g, '/').split('/');
          nameEl.value = parts.filter(function(p) {{ return p; }}).pop() || '';
        }}
      }}
      else if (data.type === 'collect_and_submit') {{
        submitForm();
      }}
      else if (data.type === 'error') {{
        showError(data.message);
      }}
    }});

    function browseFolder() {{
      var current = document.getElementById('folder').value;
      window.chrome.webview.postMessage({{ type: 'browse_folder', currentValue: current }});
    }}

    function showError(msg) {{
      var el = document.getElementById('error');
      el.textContent = msg;
      el.style.display = msg ? 'block' : 'none';
    }}

    function submitForm() {{
      showError('');
      var name = document.getElementById('name').value;
      var folder = document.getElementById('folder').value;
      var lead = document.getElementById('teamLead').value;
      window.chrome.webview.postMessage({{ type: 'submit', name: name, folder: folder, lead: lead }});
    }}

    document.addEventListener('keydown', function(e) {{
      if (e.key === 'Enter') submitForm();
    }});
  </script>
</body>
</html>";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _webView?.Dispose();
            base.Dispose(disposing);
        }
    }
}
