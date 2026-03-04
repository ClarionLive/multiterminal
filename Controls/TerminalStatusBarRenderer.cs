using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;
using MultiTerminal.Services;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based renderer for terminal status bar.
    /// Displays team member avatar, name, and current task.
    /// </summary>
    public class TerminalStatusBarRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private string _pendingUpdateJson;
        private string _pendingStatusLineJson;
        private DebugLogService _debugLogService;

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when the user clicks the Home button on the status bar.
        /// </summary>
        public event EventHandler HomeRequested;

        /// <summary>
        /// Event fired when the user clicks the Open Folder button on the status bar.
        /// The string argument is the folder path.
        /// </summary>
        public event EventHandler<string> OpenFolderRequested;

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Sets the debug log service for logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
        }

        public TerminalStatusBarRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "TerminalStatusBarRenderer";
            Height = 140; // 3-row status bar: identity + folder + statusline
            Dock = DockStyle.Top;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetStatusBarHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("Status bar HTML file not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
                _isInitializing = false;
            }
        }

        private string GetStatusBarHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try Controls/TerminalStatusBar/statusbar.html
            string path = Path.Combine(assemblyDir, "Controls", "TerminalStatusBar", "statusbar.html");
            if (File.Exists(path)) return path;

            // Try TerminalStatusBar/statusbar.html
            path = Path.Combine(assemblyDir, "TerminalStatusBar", "statusbar.html");
            if (File.Exists(path)) return path;

            // Fallback
            return Path.Combine(assemblyDir, "Controls", "TerminalStatusBar", "statusbar.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load status bar: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = JsonDocument.Parse(json);
                var root = message.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    string type = typeEl.GetString();

                    if (type == "ready")
                    {
                        _debugLogService?.Trace("TerminalStatusBar", "JS 'ready' received!");
                        _isInitialized = true;
                        _isInitializing = false;

                        // Send theme
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");

                        // Send pending updates if any
                        if (!string.IsNullOrEmpty(_pendingUpdateJson))
                        {
                            SendMessage($"update:{_pendingUpdateJson}");
                            _pendingUpdateJson = null;
                        }
                        if (!string.IsNullOrEmpty(_pendingStatusLineJson))
                        {
                            SendMessage($"statusline:{_pendingStatusLineJson}");
                            _pendingStatusLineJson = null;
                        }

                        Ready?.Invoke(this, EventArgs.Empty);
                    }
                    else if (type == "home")
                    {
                        _debugLogService?.Trace("TerminalStatusBar", "Home button clicked");
                        HomeRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (type == "openFolder")
                    {
                        if (root.TryGetProperty("path", out var pathEl))
                        {
                            string folderPath = pathEl.GetString();
                            _debugLogService?.Trace("TerminalStatusBar", $"Open folder requested: {folderPath}");
                            OpenFolderRequested?.Invoke(this, folderPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalStatusBar", $"Error processing message: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the status bar with new data.
        /// </summary>
        /// <param name="name">Terminal/member name</param>
        /// <param name="avatarUrl">Avatar URL (or null for initials)</param>
        /// <param name="activityDescription">Current activity description</param>
        /// <param name="taskTitle">Current task title</param>
        /// <param name="taskId">Current task ID</param>
        /// <param name="status">Activity status (active, idle, offline)</param>
        public void UpdateStatus(string name, string avatarUrl, string activityDescription, string taskTitle, string taskId, string status)
        {
            _debugLogService?.Trace("TerminalStatusBar", $"UpdateStatus called:");
            _debugLogService?.Trace("TerminalStatusBar", $"  - name: '{name}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - avatarUrl: '{avatarUrl}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - activityDescription: '{activityDescription}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - taskTitle: '{taskTitle}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - taskId: '{taskId}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - status: '{status}'");
            _debugLogService?.Trace("TerminalStatusBar", $"  - _isInitialized: {_isInitialized}");

            var data = new
            {
                name = name ?? "Terminal",
                avatarUrl = avatarUrl ?? "",
                activityDescription = activityDescription ?? "",
                taskTitle = taskTitle ?? "",
                taskId = taskId ?? "",
                status = status ?? "idle"
            };

            string json = JsonSerializer.Serialize(data);
            _debugLogService?.Trace("TerminalStatusBar", $"Serialized JSON: {json}");

            if (_isInitialized)
            {
                _debugLogService?.Trace("TerminalStatusBar", "Sending update to JS (initialized)");
                SendMessage($"update:{json}");
            }
            else
            {
                _debugLogService?.Trace("TerminalStatusBar", "Queuing update (not initialized yet)");
                // Queue for when ready
                _pendingUpdateJson = json;
            }
        }

        /// <summary>
        /// Updates the status line (rows 2 and 3) with Claude Code session data.
        /// </summary>
        public void UpdateStatusLine(string model, string folder, string gitBranch, string gitStatus, bool gitDirty, int? contextPct)
        {
            var data = new
            {
                model = model ?? "claude",
                folder = folder ?? "",
                gitBranch = gitBranch ?? "",
                gitStatus = gitStatus ?? "",
                gitDirty = gitDirty,
                contextPct = contextPct
            };

            string json = JsonSerializer.Serialize(data);

            if (_isInitialized)
            {
                SendMessage($"statusline:{json}");
            }
            else
            {
                _pendingStatusLineJson = json;
            }
        }

        /// <summary>
        /// Sets the theme (dark or light).
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized)
            {
                SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
            }
        }

        private void SendMessage(string message)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TerminalStatusBar", $"Error sending message: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    }
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
