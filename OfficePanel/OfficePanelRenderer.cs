using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;

namespace MultiTerminal.OfficePanel
{
    /// <summary>
    /// WebView2-based renderer for the Office Panel - animated office environment.
    /// Displays team members as characters in a virtual office with speech bubbles,
    /// status indicators, and a whiteboard showing task counts.
    /// </summary>
    public class OfficePanelRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private readonly Queue<string> _pendingMessages = new();
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when user clicks on a character in the office.
        /// Provides the terminal ID of the clicked character.
        /// </summary>
        public event EventHandler<string> CharacterClicked;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Applies immediately if initialized, otherwise deferred.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        public OfficePanelRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "OfficePanelRenderer";
            Size = new Size(350, 500);

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
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetPanelHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("Panel HTML file not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private string GetPanelHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "OfficePanel", "panel.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "OfficePanel", "panel.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load panel: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = ParseJsonMessage(json);

                switch (message.Type)
                {
                    case "ready":
                        System.Diagnostics.Debug.WriteLine($"[OfficeRenderer] JS 'ready' received! Pending messages: {_pendingMessages.Count}");
                        _isInitialized = true;
                        _isInitializing = false;
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
                        // Flush all pending messages in order
                        while (_pendingMessages.Count > 0)
                        {
                            var pendingMsg = _pendingMessages.Dequeue();
                            System.Diagnostics.Debug.WriteLine($"[OfficeRenderer] Flushing queued message: {pendingMsg.Substring(0, Math.Min(60, pendingMsg.Length))}...");
                            _webView.CoreWebView2.PostWebMessageAsString(pendingMsg);
                        }
                        _webView.ZoomFactorChanged += (s, e) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                        if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                            _webView.ZoomFactor = _pendingZoom;
                        System.Diagnostics.Debug.WriteLine("[OfficeRenderer] Firing Ready event");
                        Ready?.Invoke(this, EventArgs.Empty);
                        break;

                    case "characterClicked":
                        CharacterClicked?.Invoke(this, message.TerminalId);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OfficePanelRenderer] Error handling message: {ex.Message}");
            }
        }

        private void SendMessage(string message)
        {
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                System.Diagnostics.Debug.WriteLine($"[OfficeRenderer] SendMessage: Posting to WebView2 - {message.Substring(0, Math.Min(80, message.Length))}...");
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            else
            {
                // Queue message to send when WebView2 is ready
                System.Diagnostics.Debug.WriteLine($"[OfficeRenderer] SendMessage: QUEUING (WebView2 not ready) - {message.Substring(0, Math.Min(80, message.Length))}...");
                _pendingMessages.Enqueue(message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Public API Methods
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Add a character to the office representing a team member.
        /// </summary>
        /// <param name="terminalId">Unique terminal identifier.</param>
        /// <param name="name">Display name for the character.</param>
        /// <param name="color">Color associated with this terminal.</param>
        /// <param name="status">Current status (idle, working, blocked).</param>
        /// <param name="activity">Description of current activity.</param>
        public void AddCharacter(string terminalId, string name, string color, string status, string activity, string role = "director", string spawnedBy = null)
        {
            var spawnedByField = string.IsNullOrEmpty(spawnedBy) ? "" : $",\"spawnedBy\":\"{EscapeJson(spawnedBy)}\"";
            var json = $"{{\"terminalId\":\"{EscapeJson(terminalId)}\",\"name\":\"{EscapeJson(name)}\",\"color\":\"{EscapeJson(color)}\",\"status\":\"{EscapeJson(status)}\",\"activity\":\"{EscapeJson(activity)}\",\"role\":\"{EscapeJson(role)}\"{spawnedByField}}}";
            SendMessage($"addCharacter:{json}");
        }

        /// <summary>
        /// Remove a character from the office (terminal disconnected).
        /// </summary>
        /// <param name="terminalId">Terminal identifier to remove.</param>
        public void RemoveCharacter(string terminalId)
        {
            SendMessage($"removeCharacter:{EscapeJson(terminalId)}");
        }

        /// <summary>
        /// Update a character's status and activity description.
        /// </summary>
        /// <param name="terminalId">Terminal identifier to update.</param>
        /// <param name="status">New status (idle, working, blocked).</param>
        /// <param name="activity">New activity description.</param>
        public void UpdateCharacterState(string terminalId, string status, string activity)
        {
            var json = $"{{\"terminalId\":\"{EscapeJson(terminalId)}\",\"status\":\"{EscapeJson(status)}\",\"activity\":\"{EscapeJson(activity)}\"}}";
            SendMessage($"updateState:{json}");
        }

        /// <summary>
        /// Show a speech bubble above a character.
        /// </summary>
        /// <param name="terminalId">Terminal identifier to show bubble for.</param>
        /// <param name="message">Message text to display.</param>
        /// <param name="durationMs">How long to show the bubble in milliseconds.</param>
        public void ShowSpeechBubble(string terminalId, string message, int durationMs = 3000)
        {
            var truncated = message?.Length > 80 ? message.Substring(0, 80) + "..." : message ?? "";
            var json = $"{{\"terminalId\":\"{EscapeJson(terminalId)}\",\"message\":\"{EscapeJson(truncated)}\",\"duration\":{durationMs}}}";
            SendMessage($"speechBubble:{json}");
        }

        /// <summary>
        /// Update the whiteboard with current task counts.
        /// </summary>
        /// <param name="todo">Number of todo/suggestion tasks.</param>
        /// <param name="inProgress">Number of in-progress tasks.</param>
        /// <param name="done">Number of completed tasks.</param>
        /// <param name="recentTask">Title of the most recently updated task.</param>
        public void UpdateWhiteboard(int todo, int inProgress, int done, string recentTask)
        {
            var json = $"{{\"todo\":{todo},\"inProgress\":{inProgress},\"done\":{done},\"recentTask\":\"{EscapeJson(recentTask ?? "")}\"}}";
            SendMessage($"whiteboard:{json}");
        }

        /// <summary>
        /// Set the theme for the panel.
        /// </summary>
        /// <param name="isDark">True for dark theme, false for light theme.</param>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            SendMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        // ─────────────────────────────────────────────────────────────
        // Helper Methods
        // ─────────────────────────────────────────────────────────────

        private string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        private dynamic ParseJsonMessage(string json)
        {
            // Simple JSON parsing for our message format
            return new
            {
                Type = ExtractJsonString(json, "type"),
                TerminalId = ExtractJsonString(json, "terminalId")
            };
        }

        private string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern);
            if (start < 0) return "";
            start += pattern.Length;
            int end = json.IndexOf("\"", start);
            if (end < 0) return "";
            return json.Substring(start, end - start);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
